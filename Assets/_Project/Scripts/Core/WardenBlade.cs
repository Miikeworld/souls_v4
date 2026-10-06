using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Warden's own sword, as the fight sees it — added at runtime by
/// <see cref="BossLord"/>. It measures the weapon actually in his hand (the
/// BossSword, later the Core-forged greatsword) after the final pose each frame
/// and turns it into the fight's language:
///   • a swing trail — ink-edged crimson ribbon with a hot core, drawn only while
///     the blade is really moving inside a swing gate, gone in a fifth of a second;
///   • heat — the windup's danger glow: blade emission climbs and a thin crimson
///     edge line lights along the blade (crimson = damage happens here);
///   • the glint — a two-frame pale star and a "shing" just before the cut;
///   • contact — <see cref="SweepHits"/> tests the swept blade against the
///     player's body, so his hits come from his sword, not from an invisible arc;
///   • misses hit the room — a tip driven into the floor throws sparks, chips and
///     a gouge that cools and clears; a blade through a pillar chips/topples it.
/// </summary>
[DefaultExecutionOrder(90)] // after the animator, FootGrounding (50) and WardenPose (60)
public sealed class WardenBlade : MonoBehaviour
{
    private static readonly int EmissionId = Shader.PropertyToID("_EmissionStrength");

    private const float TrailLife = 0.2f;
    private const int MaxSamples = 18;

    private struct Sample
    {
        public Vector3 a, b;
        public float born;
    }

    private Transform sword;
    private Vector3 baseLocal, tipLocal;
    private readonly List<Renderer> rends = new List<Renderer>();
    private readonly List<float> emitBase = new List<float>();
    private MaterialPropertyBlock mpb;
    private readonly List<Sample> samples = new List<Sample>();
    private Mesh mesh;
    private GameObject ribbon;
    private readonly List<Vector3> verts = new List<Vector3>();
    private readonly List<Color> cols = new List<Color>();
    private readonly List<int> tris = new List<int>();
    private LineRenderer edgeLine;
    private Transform ownerRoot;

    private Vector3 prevBase, prevTip;
    // The last real frame-to-frame sweep (sampled after the pose in LateUpdate): the brain
    // queries it from Update, which runs BEFORE this frame's animation.
    private Vector3 sweepBase0, sweepTip0, sweepBase1, sweepTip1;
    private bool havePrev, haveSweep, inFloor;
    private float flash, nextSpark;
    private readonly List<Vector3> gouge = new List<Vector3>();
    private readonly HashSet<Collider> struckThisSwing = new HashSet<Collider>();
    private int swingId;

    /// <summary>Trail + environment contact are live (the move's swing gate).</summary>
    public bool Swinging { get; set; }
    /// <summary>0..1 windup danger glow.</summary>
    public float Heat { get; set; }
    /// <summary>World floor height (the boss's arena floor) for tip contacts.</summary>
    public float FloorY { get; set; }
    /// <summary>Weight of a pillar strike from this swing (1 = a normal greatsword cut).</summary>
    public float Force { get; set; } = 1f;
    /// <summary>Blade speed below which a "swing" is a windup or a recovery (no trail, no hit).</summary>
    public float MinSpeed { get; set; } = 4.5f;

    public bool HasBlade => sword != null && sword.gameObject.activeInHierarchy;
    public Vector3 Base => HasBlade ? sword.TransformPoint(baseLocal) : transform.position + Vector3.up;
    public Vector3 Tip => HasBlade ? sword.TransformPoint(tipLocal) : transform.position + Vector3.up + transform.forward;
    public float Length => HasBlade ? Vector3.Distance(sword.TransformPoint(Vector3.zero), Tip) : 1.4f;
    public float TipSpeed { get; private set; }
    public Vector3 TipVelocity { get; private set; }
    /// <summary>Last floor point the tip was driven into this swing (for Worldsplitter-style follow-ups).</summary>
    public Vector3 LastFloorContact { get; private set; }
    public bool FloorContactThisSwing { get; private set; }

    public void Init(Transform owner) => ownerRoot = owner;

    /// <summary>Point the tracker at the weapon now in his hand (null = empty hands).</summary>
    public void SetSword(Transform s)
    {
        RestoreEmission();
        sword = s;
        rends.Clear();
        emitBase.Clear();
        havePrev = haveSweep = false;
        samples.Clear();
        if (s == null) return;
        Measure(s, out baseLocal, out tipLocal);
        foreach (var r in s.GetComponentsInChildren<Renderer>(true))
        {
            if (r is LineRenderer || r is TrailRenderer || r is ParticleSystemRenderer) continue;
            var m = r.sharedMaterial;
            if (m == null || !m.HasProperty(EmissionId)) continue;
            rends.Add(r);
            emitBase.Add(m.GetFloat(EmissionId));
        }
    }

    /// <summary>Begin a new swing: contacts and the floor gouge start fresh.</summary>
    public void NewSwing(float force = 1f)
    {
        swingId++;
        Force = force;
        struckThisSwing.Clear();
        FloorContactThisSwing = false;
    }

    /// <summary>The pre-strike glint at the tip — call ~0.1–0.2s before the cut.</summary>
    public void Glint(float size = 1f)
    {
        if (!HasBlade) return;
        WardenFx.Glint(Tip, size * Mathf.Sqrt(Mathf.Max(0.5f, Length / 1.4f)));
        WardenAudio.Play("shing", Tip, 0.5f, Random.Range(0.95f, 1.08f));
        flash = 1f;
    }

    /// <summary>True when the blade swept through the player's body since last frame
    /// (only while it moves at swing speed). <paramref name="radius"/> = blade reach slack.</summary>
    public bool SweepHits(float radius = 0.5f)
    {
        if (!HasBlade || !haveSweep || TipSpeed < MinSpeed * 0.6f || !WardenHazard.Alive) return false;
        for (var i = 0; i <= 3; i++)
        {
            var k = i / 3f;
            var a0 = Vector3.Lerp(sweepBase0, sweepBase1, k);
            var a1 = Vector3.Lerp(sweepTip0, sweepTip1, k);
            if (WardenHazard.SegmentToBody(a0, a1) < radius) return true;
        }
        return WardenHazard.SegmentToBody(sweepTip0, sweepTip1) < radius;
    }

    // ------------------------------------------------------------------ tick

    private void LateUpdate()
    {
        var dt = Time.deltaTime;
        flash = Mathf.MoveTowards(flash, 0f, dt * 4f);
        if (!HasBlade)
        {
            havePrev = haveSweep = false;
            samples.Clear();
            RebuildRibbon();
            if (edgeLine != null) edgeLine.enabled = false;
            return;
        }
        var b = Base;
        var t = Tip;
        if (havePrev && dt > 0f)
        {
            var v = (t - prevTip) / dt;
            // A teleport/blink is never a swing.
            if (v.magnitude > 80f) { havePrev = false; samples.Clear(); }
            else
            {
                TipVelocity = v;
                TipSpeed = v.magnitude;
            }
        }
        if (Swinging && havePrev && TipSpeed >= MinSpeed)
        {
            if (samples.Count == 0 || (samples[samples.Count - 1].b - t).sqrMagnitude > 0.0004f)
                samples.Add(new Sample { a = b, b = t, born = Time.time });
            Contacts(b, t);
        }
        else if (inFloor) EndGouge();
        while (samples.Count > 0 && (Time.time - samples[0].born > TrailLife || samples.Count > MaxSamples)) samples.RemoveAt(0);
        RebuildRibbon();
        Glow(b, t);
        haveSweep = havePrev;
        sweepBase0 = prevBase; sweepTip0 = prevTip;
        sweepBase1 = b; sweepTip1 = t;
        prevBase = b;
        prevTip = t;
        havePrev = true;
    }

    /// <summary>Misses hit the room: the floor under the tip, pillars along the blade.</summary>
    private void Contacts(Vector3 b, Vector3 t)
    {
        // Floor: the tip driven below the arena floor.
        if (t.y < FloorY + 0.1f && TipSpeed > MinSpeed)
        {
            var p = new Vector3(t.x, FloorY + 0.02f, t.z);
            var along = Vector3.ProjectOnPlane(TipVelocity, Vector3.up);
            if (!inFloor)
            {
                inFloor = true;
                gouge.Clear();
                WardenFx.Sparks(p, Vector3.up, 9, 5f, along.normalized * 0.6f);
                WardenFx.Debris(p, Mathf.RoundToInt(3 + 3 * Force), 4.5f, 0.8f + 0.25f * Force);
                WardenFx.Dust(p, 3, 1f, 0.8f);
                WardenFx.Peak(p + Vector3.up * 0.15f, 0.35f);
                WardenAudio.Play("metal", p, 0.7f, Random.Range(0.75f, 0.9f));
                WardenAudio.Play("stone", p, 0.5f, Random.Range(0.85f, 1f));
                WardenFx.Shake(0.06f * Force);
            }
            if (gouge.Count == 0 || (gouge[gouge.Count - 1] - p).sqrMagnitude > 0.09f) gouge.Add(p);
            if (Time.time >= nextSpark)
            {
                nextSpark = Time.time + 0.04f;
                WardenFx.Sparks(p, Vector3.up, 3, 4f, -along.normalized * 0.5f);
            }
            FloorContactThisSwing = true;
            LastFloorContact = p;
        }
        else if (inFloor) EndGouge();

        // Pillars (and anything else the sanctum owns) along the blade.
        var sanctum = CoreSanctum.Active;
        if (sanctum == null) return;
        var dir = (t - b);
        var len = dir.magnitude;
        if (len < 0.05f) return;
        foreach (var h in Physics.SphereCastAll(b, 0.12f, dir / len, len, ~0, QueryTriggerInteraction.Ignore))
        {
            if (h.collider == null || struckThisSwing.Contains(h.collider)) continue;
            if (ownerRoot != null && h.collider.transform.IsChildOf(ownerRoot)) continue;
            if (h.collider.GetComponentInParent<Health>() != null || h.collider.GetComponentInParent<PlayerHealth>() != null) continue;
            var point = h.point == Vector3.zero ? h.collider.ClosestPoint(b) : h.point;
            if (sanctum.StrikeAt(h.collider, point, TipVelocity, Force))
            {
                struckThisSwing.Add(h.collider);
                WardenFx.Shake(0.1f * Force);
            }
        }
    }

    private void EndGouge()
    {
        inFloor = false;
        if (gouge.Count >= 2) WardenFx.Groove(gouge, 0.1f + 0.05f * Force, 1.3f);
        gouge.Clear();
    }

    private void Glow(Vector3 b, Vector3 t)
    {
        var k = 1f + Heat * 3.2f + flash * 2.5f;
        for (var i = 0; i < rends.Count; i++)
        {
            var r = rends[i];
            if (r == null) continue;
            mpb ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            mpb.SetFloat(EmissionId, emitBase[i] * k);
            r.SetPropertyBlock(mpb);
        }
        var on = Heat > 0.2f || flash > 0.05f;
        if (on && edgeLine == null) edgeLine = MakeLine();
        if (edgeLine == null) return;
        edgeLine.enabled = on;
        if (!on) return;
        var a = Vector3.Lerp(b, t, 0.05f);
        edgeLine.SetPosition(0, a);
        edgeLine.SetPosition(1, t);
        var w = (0.025f + 0.05f * Heat + 0.04f * flash) * Mathf.Sqrt(Mathf.Max(0.5f, Length / 1.4f));
        edgeLine.startWidth = w * 0.6f;
        edgeLine.endWidth = w;
        var c = Color.Lerp(WardenFx.Crimson, WardenFx.PaleRed, flash * 0.8f + Heat * 0.15f);
        c.a = WardenFx.Stepped(Mathf.Max(Heat, flash));
        edgeLine.startColor = edgeLine.endColor = c;
    }

    private LineRenderer MakeLine()
    {
        var go = new GameObject("Warden blade edge");
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = WardenFx.GlowMaterial;
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.numCapVertices = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    private void RebuildRibbon()
    {
        if (samples.Count < 2)
        {
            if (mesh != null) mesh.Clear();
            return;
        }
        if (ribbon == null)
        {
            ribbon = new GameObject("Warden swing trail");
            mesh = new Mesh { name = "Warden swing trail" };
            mesh.MarkDynamic();
            ribbon.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = ribbon.AddComponent<MeshRenderer>();
            r.sharedMaterial = WardenFx.GlowMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }
        verts.Clear(); cols.Clear(); tris.Clear();
        var now = Time.time;
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var fade = WardenFx.Stepped(1f - (now - s.born) / TrailLife);
            var axis = s.b - s.a;
            var ink = s.b + axis.normalized * 0.06f;
            // Rows: dim root → crimson body → hot edge → ink lip just past the tip.
            verts.Add(Vector3.Lerp(s.a, s.b, 0.25f));
            verts.Add(Vector3.Lerp(s.a, s.b, 0.82f));
            verts.Add(s.b);
            verts.Add(ink);
            var deep = WardenFx.CrimsonDeep; deep.a = 0.12f * fade;
            var body = WardenFx.Crimson; body.a = 0.5f * fade;
            var hot = Color.Lerp(WardenFx.Crimson, WardenFx.PaleRed, 0.55f * fade); hot.a = 0.9f * fade;
            var lip = WardenFx.Ink; lip.a = 0.65f * fade;
            cols.Add(deep); cols.Add(body); cols.Add(hot); cols.Add(lip);
            if (i == 0) continue;
            var row = i * 4;
            for (var strip = 0; strip < 3; strip++)
            {
                tris.Add(row - 4 + strip); tris.Add(row - 3 + strip); tris.Add(row + strip);
                tris.Add(row + strip); tris.Add(row - 3 + strip); tris.Add(row + 1 + strip);
            }
        }
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetColors(cols);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
    }

    private void RestoreEmission()
    {
        for (var i = 0; i < rends.Count; i++)
        {
            var r = rends[i];
            if (r == null) continue;
            mpb ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(mpb);
            mpb.SetFloat(EmissionId, emitBase[i]);
            r.SetPropertyBlock(mpb);
        }
    }

    /// <summary>Blade axis in sword-local space: pivot (the grip) → the far end of the
    /// mesh's longest extent; the trail root sits a fifth of the way up.</summary>
    public static void Measure(Transform s, out Vector3 baseL, out Vector3 tipL)
    {
        tipL = Vector3.up * 1.4f;
        var mf = s.GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            var bnd = mf.sharedMesh.bounds;
            var lo = s.InverseTransformPoint(mf.transform.TransformPoint(bnd.min));
            var hi = s.InverseTransformPoint(mf.transform.TransformPoint(bnd.max));
            var mn = Vector3.Min(lo, hi);
            var mx = Vector3.Max(lo, hi);
            var size = mx - mn;
            var axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            var far = Mathf.Abs(mx[axis]) >= Mathf.Abs(mn[axis]) ? mx[axis] : mn[axis];
            tipL = Vector3.zero;
            tipL[axis] = far;
        }
        baseL = tipL * 0.2f;
    }

    private void OnDisable()
    {
        samples.Clear();
        if (mesh != null) mesh.Clear();
        if (edgeLine != null) edgeLine.enabled = false;
    }

    private void OnDestroy()
    {
        RestoreEmission();
        if (ribbon != null) Destroy(ribbon);
        if (mesh != null) Destroy(mesh);
        if (edgeLine != null) Destroy(edgeLine.gameObject);
    }
}
