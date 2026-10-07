using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Warden's own sword, as the fight sees it — added at runtime by
/// <see cref="BossLord"/>. It measures the weapon actually in his hand (the
/// BossSword, later the Core-forged greatsword) after the final pose each frame
/// and turns it into the player's effects language:
///   • a swing trail — the player's blade ribbon (NEA/BladeRibbon, the crimson art
///     colours, rows from the blade base to an ink lip past the tip, alpha ≤ .55,
///     linear fade over 0.16s), drawn only while the blade is really moving inside
///     a swing gate; a heavy cut leaves ONE afterimage of the blade at its apex;
///   • heat — the windup's danger glow: blade emission steps up (≤ ×2.5) and an
///     ink-backed crimson edge stroke lights along the blade (crimson = damage here);
///   • the glint — a two-frame pale star and a "shing" just before the cut;
///   • contact — <see cref="SweepHits"/> tests the swept blade against the
///     player's body, so his hits come from his sword, not from an invisible arc;
///   • misses hit the room — a tip driven into the floor throws faceted chips and
///     cuts a gouge that cools and clears; a blade through a pillar chips/topples it.
/// A weapon whose grip isn't in the hand holding it (a bad seat) counts as no blade
/// at all, so the brain falls back to its arc test instead of trusting a blade in the sky.
/// </summary>
[DefaultExecutionOrder(90)] // after the animator, FootGrounding (50) and WardenPose (60)
public sealed class WardenBlade : MonoBehaviour
{
    private static readonly int EmissionId = Shader.PropertyToID("_EmissionStrength");
    // BladeRibbon's crimson art colours.
    private static readonly Color RibbonBody = new Color(0.22f, 0.006f, 0.02f);
    private static readonly Color RibbonTip = new Color(0.88f, 0.018f, 0.055f);

    private const float TrailLife = 0.16f;
    private const int MaxSamples = 24;
    private const float MaxEmission = 2.5f;
    private const float TeleportDistance = 3f; // owner root moved this far in one frame = a blink, not a swing
    private const float SeatSlack = 0.4f;      // grip ↔ hand bone, × owner scale
    private const float HeavyForce = 1.4f;

    private struct Sample
    {
        public Vector3 a, b;
        public float born;
    }

    private Transform sword;
    private MeshFilter swordMesh;
    private Vector3 baseLocal, tipLocal;
    private readonly List<Renderer> rends = new List<Renderer>();
    private readonly List<float> emitBase = new List<float>();
    private MaterialPropertyBlock mpb;
    private readonly List<Sample> samples = new List<Sample>();
    private Mesh mesh;
    private GameObject ribbon;
    private Material ribbonMat;
    private readonly List<Vector3> verts = new List<Vector3>();
    private readonly List<Color> cols = new List<Color>();
    private readonly List<int> tris = new List<int>();
    private LineRenderer edgeLine, edgeInk;
    private Transform ownerRoot;

    private Vector3 prevBase, prevTip, prevRoot;
    // The last real frame-to-frame sweep (sampled after the pose in LateUpdate): the brain
    // queries it from Update, which runs BEFORE this frame's animation.
    private Vector3 sweepBase0, sweepTip0, sweepBase1, sweepTip1;
    private bool havePrev, haveSweep, inFloor, seated, warnedSeat;
    private float flash, nextSpark, peakSpeed;
    private readonly List<Vector3> gouge = new List<Vector3>();
    private readonly HashSet<Collider> struckThisSwing = new HashSet<Collider>();
    private int swingId, ghostSwing = -1;

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

    public bool HasBlade => sword != null && seated && sword.gameObject.activeInHierarchy;
    public Vector3 Base => HasBlade ? sword.TransformPoint(baseLocal) : transform.position + Vector3.up;
    public Vector3 Tip => HasBlade ? sword.TransformPoint(tipLocal) : transform.position + Vector3.up + transform.forward;
    public float Length => HasBlade ? Vector3.Distance(sword.TransformPoint(Vector3.zero), Tip) : 1.4f;
    public float TipSpeed { get; private set; }
    public Vector3 TipVelocity { get; private set; }
    /// <summary>Last floor point the tip was driven into this swing (for Worldsplitter-style follow-ups).</summary>
    public Vector3 LastFloorContact { get; private set; }
    public bool FloorContactThisSwing { get; private set; }

    public void Init(Transform owner) => ownerRoot = owner;

    private Transform Owner => ownerRoot != null ? ownerRoot : transform;

    /// <summary>Point the tracker at the weapon now in his hand (null = empty hands).</summary>
    public void SetSword(Transform s)
    {
        RestoreEmission();
        sword = s;
        swordMesh = null;
        seated = false;
        rends.Clear();
        emitBase.Clear();
        havePrev = haveSweep = false;
        samples.Clear();
        if (s == null) return;
        seated = Seated(s);
        Measure(s, out baseLocal, out tipLocal);
        swordMesh = s.GetComponentInChildren<MeshFilter>(true);
        foreach (var r in s.GetComponentsInChildren<Renderer>(true))
        {
            if (r is LineRenderer || r is TrailRenderer || r is ParticleSystemRenderer) continue;
            var m = r.sharedMaterial;
            if (m == null || !m.HasProperty(EmissionId)) continue;
            rends.Add(r);
            emitBase.Add(m.GetFloat(EmissionId));
        }
    }

    /// <summary>The grip (the weapon's pivot) must sit in the hand bone that holds it.
    /// A seat ~170 m off the hand (the old FitSword double scale) would put the trail,
    /// heat, glint and every contact test in the sky — treat it as no blade, once logged.</summary>
    private bool Seated(Transform s)
    {
        var anim = s.GetComponentInParent<Animator>(true);
        var hand = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.RightHand) : null;
        if (hand == null || !s.IsChildOf(hand)) hand = s.parent;
        if (hand == null) return true;
        var gap = Vector3.Distance(s.position, hand.position);
        if (gap <= SeatSlack * Mathf.Max(0.01f, Owner.lossyScale.x)) return true;
        if (!warnedSeat)
        {
            warnedSeat = true;
            Debug.LogWarning($"[Warden] {s.name}'s grip is {gap:F1} m from {hand.name} — treating it as no blade " +
                             "(arc contact fallback, no trail/heat). WardenBody.ReseatSword should have repaired it.");
        }
        return false;
    }

    /// <summary>Begin a new swing: contacts, the floor gouge and the apex ghost start fresh.</summary>
    public void NewSwing(float force = 1f)
    {
        swingId++;
        Force = force;
        peakSpeed = 0f;
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
        var root = Owner.position;
        if (!HasBlade)
        {
            havePrev = haveSweep = false;
            TipSpeed = 0f;
            TipVelocity = Vector3.zero;
            if (inFloor) EndGouge();
            samples.Clear();
            RebuildRibbon();
            if (edgeLine != null) edgeLine.enabled = edgeInk.enabled = false;
            prevRoot = root;
            return;
        }
        var b = Base;
        var t = Tip;
        // A blink/teleport moves HIM, not just the blade: only that breaks the trail — a
        // fast cut never does, however fast the (P3 greatsword) tip travels.
        if (havePrev && (root - prevRoot).sqrMagnitude > TeleportDistance * TeleportDistance)
        {
            havePrev = false;
            samples.Clear();
            TipSpeed = 0f;
            TipVelocity = Vector3.zero;
            if (inFloor) EndGouge();
        }
        prevRoot = root;
        if (havePrev && dt > 0f)
        {
            TipVelocity = (t - prevTip) / dt;
            TipSpeed = TipVelocity.magnitude;
        }
        if (Swinging && havePrev && TipSpeed >= MinSpeed)
        {
            // The ribbon opens on the sweep that just happened (last pose → this one).
            if (samples.Count == 0) samples.Add(new Sample { a = prevBase, b = prevTip, born = Time.time });
            if ((samples[samples.Count - 1].b - t).sqrMagnitude > 0.0004f)
                samples.Add(new Sample { a = b, b = t, born = Time.time });
            Contacts(b, t);
        }
        else if (inFloor) EndGouge();
        if (Swinging && havePrev) ApexGhost();
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

    /// <summary>Heavy cuts leave ONE afterimage of the blade just past its fastest frame —
    /// the player's dodge-ghost language on the sword (ink silhouette, rim band, its wire).</summary>
    private void ApexGhost()
    {
        if (Force < HeavyForce || ghostSwing == swingId || swordMesh == null) return;
        if (TipSpeed > peakSpeed) { peakSpeed = TipSpeed; return; }
        if (peakSpeed < MinSpeed * 2f || TipSpeed > peakSpeed * 0.85f) return;
        ghostSwing = swingId;
        WardenFx.Ghost(swordMesh, WardenFx.Crimson, 0.3f);
    }

    /// <summary>Misses hit the room: the floor under the tip, pillars along the blade.</summary>
    private void Contacts(Vector3 b, Vector3 t)
    {
        // Floor: the tip driven below the arena floor.
        if (t.y < FloorY + 0.1f && TipSpeed > MinSpeed)
        {
            var p = new Vector3(t.x, FloorY + 0.02f, t.z);
            var along = Vector3.ProjectOnPlane(TipVelocity, Vector3.up);
            var dir = along.sqrMagnitude > 1e-4f ? along.normalized : Vector3.zero;
            if (!inFloor)
            {
                inFloor = true;
                gouge.Clear();
                // Metal bites stone: a 2-frame pale peak, a faceted tap ring and chips
                // thrown along the cut — no stretched sparks, no dust.
                WardenFx.Peak(p + Vector3.up * 0.15f, 0.3f);
                WardenFx.Pulse(p, Vector3.up, 0.08f, 0.45f + 0.25f * Force, 0.22f, WardenFx.Crimson, 0.9f, WardenFx.CoreSides);
                WardenFx.Chips(p, 6, 4.5f, dir * 0.6f + Vector3.up * 0.4f, 0.32f, WardenFx.Crimson, 0.9f);
                WardenFx.Debris(p, Mathf.RoundToInt(2 + 2 * Force), 3.5f, 0.9f);
                WardenAudio.Play("metal", p, 0.7f, Random.Range(0.75f, 0.9f));
                WardenAudio.Play("stone", p, 0.5f, Random.Range(0.85f, 1f));
                WardenFx.Shake(0.06f * Force);
            }
            if (gouge.Count == 0 || (gouge[gouge.Count - 1] - p).sqrMagnitude > 0.09f) gouge.Add(p);
            if (Time.time >= nextSpark)
            {
                nextSpark = Time.time + 0.05f;
                WardenFx.Chips(p, 2, 3.5f, -dir * 0.5f + Vector3.up * 0.3f, 0.26f, WardenFx.Crimson, 0.8f);
            }
            FloorContactThisSwing = true;
            LastFloorContact = p;
        }
        else if (inFloor) EndGouge();

        // Pillars (and anything else the sanctum owns) along the blade.
        var sanctum = CoreSanctum.Active;
        if (sanctum == null) return;
        var span = (t - b);
        var len = span.magnitude;
        if (len < 0.05f) return;
        foreach (var h in Physics.SphereCastAll(b, 0.12f, span / len, len, ~0, QueryTriggerInteraction.Ignore))
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

    /// <summary>Windup heat: emission steps up in four bands (capped ×2.5 — a hot edge,
    /// not a bloom blob) and an ink-backed crimson stroke lights the edge; pale only for
    /// the glint's first ~2 frames.</summary>
    private void Glow(Vector3 b, Vector3 t)
    {
        var k = 1f + (MaxEmission - 1f) * WardenFx.Stepped(Mathf.Clamp01(Mathf.Max(Heat, flash * 0.85f)));
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
        if (on && edgeLine == null)
        {
            edgeInk = MakeLine("Warden blade edge ink", 0);
            edgeLine = MakeLine("Warden blade edge", 1);
        }
        if (edgeLine == null) return;
        var alpha = on ? WardenFx.Stepped(Mathf.Max(Heat, flash)) * WardenFx.Opacity : 0f;
        edgeLine.enabled = edgeInk.enabled = alpha > 0.001f;
        if (!edgeLine.enabled) return;
        var a = Vector3.Lerp(b, t, 0.05f);
        var w = (0.02f + 0.03f * Heat + 0.025f * flash) * Mathf.Sqrt(Mathf.Max(0.5f, Length / 1.4f));
        edgeLine.SetPosition(0, a);
        edgeLine.SetPosition(1, t);
        edgeLine.startWidth = w * 0.6f;
        edgeLine.endWidth = w;
        edgeLine.startColor = edgeLine.endColor = WardenFx.Glow(flash > 0.9f ? WardenFx.PaleRed : WardenFx.Crimson, alpha);
        edgeInk.SetPosition(0, a);
        edgeInk.SetPosition(1, t);
        edgeInk.startWidth = w * 0.6f * 2.2f;
        edgeInk.endWidth = w * 2.2f;
        var ink = WardenFx.Ink;
        ink.a = alpha * WardenFx.InkStrength;
        edgeInk.startColor = edgeInk.endColor = ink;
    }

    private static LineRenderer MakeLine(string name, int order)
    {
        var go = new GameObject(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = WardenFx.GlowMaterial;
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.numCornerVertices = 0;
        lr.numCapVertices = 0;
        lr.sortingOrder = order; // ink (0) always under its stroke (1)
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    /// <summary>The player's blade ribbon on his sword: four rows per sample — blade base,
    /// 90%, tip, a thin ink lip past the tip — at .16 / .28 / .55 / .4 × a linear age fade.
    /// Boss scale reads through geometry only (the lip grows with the blade), never alpha.</summary>
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
            // The player's ribbon shader clamps alpha at .55; the shared glow material is only
            // the fallback (the vertex alphas never exceed .55 either way).
            var shader = Shader.Find("NEA/BladeRibbon");
            if (shader != null) ribbonMat = new Material(shader) { name = "Warden blade ribbon (runtime)" };
            r.sharedMaterial = ribbonMat != null ? ribbonMat : WardenFx.GlowMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }
        verts.Clear(); cols.Clear(); tris.Clear();
        var now = Time.time;
        var dim = new Color(RibbonBody.r * 0.16f, RibbonBody.g * 0.16f, RibbonBody.b * 0.16f, 0f);
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var fade = Mathf.Clamp01(1f - (now - s.born) / TrailLife);
            var axis = s.b - s.a;
            var len = axis.magnitude;
            var lip = s.b + (len > 1e-4f ? axis / len : Vector3.up) * 0.035f * Mathf.Clamp(len / 0.55f, 1f, 3f);
            verts.Add(s.a);
            verts.Add(Vector3.Lerp(s.a, s.b, 1f - Mathf.Clamp(0.22f / Mathf.Max(len, 0.01f), 0.1f, 0.3f)));
            verts.Add(s.b);
            verts.Add(lip);
            var root = dim; root.a = 0.16f * fade;
            var body = RibbonBody; body.a = 0.28f * fade;
            var hot = RibbonTip; hot.a = 0.55f * fade;
            var ink = WardenFx.Ink; ink.a = 0.4f * fade;
            cols.Add(root); cols.Add(body); cols.Add(hot); cols.Add(ink);
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
        if (edgeInk != null) edgeInk.enabled = false;
    }

    private void OnDestroy()
    {
        RestoreEmission();
        if (ribbon != null) Destroy(ribbon);
        if (mesh != null) Destroy(mesh);
        if (ribbonMat != null) Destroy(ribbonMat);
        if (edgeLine != null) Destroy(edgeLine.gameObject);
        if (edgeInk != null) Destroy(edgeInk.gameObject);
    }
}
