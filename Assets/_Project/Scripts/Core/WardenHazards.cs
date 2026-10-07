using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared plumbing for the Warden's runtime hazards: a live registry (a boss
/// reset or player death wipes every blade, ring and crack at once) and one
/// cached view of the player — body axis, grounded/wall-run state, and a
/// damage call that respects dodge i-frames and the post-hit grace window.
/// </summary>
public static class WardenHazard
{
    private static readonly List<GameObject> live = new List<GameObject>();
    private static PlayerHealth hp;
    private static PlayerState st;
    private static CharacterController cc;
    private static WallRunController wall;
    private static PlayerLocomotion loco;

    public static void Track(GameObject go)
    {
        if (live.Count > 64) live.RemoveAll(g => g == null);
        live.Add(go);
    }

    public static void ClearAll()
    {
        foreach (var g in live) if (g != null) Object.Destroy(g);
        live.Clear();
    }

    private static bool Find()
    {
        if (hp != null) return true;
        loco = Object.FindFirstObjectByType<PlayerLocomotion>();
        if (loco == null) return false;
        hp = loco.GetComponent<PlayerHealth>();
        st = loco.GetComponent<PlayerState>();
        cc = loco.GetComponent<CharacterController>();
        wall = loco.GetComponent<WallRunController>();
        return hp != null;
    }

    public static bool HasPlayer => Find();
    public static Transform Player => Find() ? hp.transform : null;
    public static Vector3 Feet => Find() ? hp.transform.position : Vector3.zero;
    public static Vector3 Chest => Feet + Vector3.up * 1.1f;
    public static bool Alive => Find() && !hp.IsDead && (st == null || !st.IsDead);
    public static bool Grounded => Find() && cc != null && cc.isGrounded;
    public static bool WallRunning => Find() && wall != null && wall.IsWallRunning;
    public static WallRunController Wall => Find() ? wall : null;
    public static PlayerLocomotion Locomotion => Find() ? loco : null;
    public static PlayerState State => Find() ? st : null;
    public static CharacterController Capsule => Find() ? cc : null;

    /// <summary>Deals damage through PlayerHealth (i-frames/grace apply). True when it landed.</summary>
    public static bool Damage(float amount, Vector3 from)
    {
        if (!Alive || amount <= 0f) return false;
        var before = hp.Current;
        hp.TakeDamage(amount, from);
        return hp.Current < before;
    }

    /// <summary>A heavy hit's weight: the player is pushed <paramref name="metres"/> straight
    /// away from <paramref name="from"/> over <paramref name="seconds"/> (ease-out). Never
    /// while wall-running or dead — the wall and the death sequence own the body.</summary>
    public static void Shove(Vector3 from, float metres, float seconds = 0.22f)
    {
        if (!Alive || cc == null || metres <= 0f || WallRunning) return;
        var dir = Vector3.ProjectOnPlane(Feet - from, Vector3.up);
        if (dir.sqrMagnitude < 1e-4f) dir = -hp.transform.forward;
        if (!hp.TryGetComponent<WardenShove>(out var s)) s = hp.gameObject.AddComponent<WardenShove>();
        s.Push(dir.normalized * metres, seconds);
    }

    /// <summary>Closest distance between segment p0–p1 and the player's body axis.</summary>
    public static float SegmentToBody(Vector3 p0, Vector3 p1)
    {
        var f = Feet;
        return SegmentDistance(p0, p1, f + Vector3.up * 0.25f, f + Vector3.up * 1.65f);
    }

    public static float PointToBody(Vector3 p) => SegmentToBody(p, p);

    /// <summary>Segment–segment closest distance (Ericson, Real-Time Collision Detection 5.1.9).</summary>
    public static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-6f && e <= 1e-6f) return Vector3.Distance(p1, p2);
        if (a <= 1e-6f) { s = 0f; t = Mathf.Clamp01(f / e); }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-6f) { t = 0f; s = Mathf.Clamp01(-c / a); }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom > 1e-6f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        return Vector3.Distance(p1 + d1 * s, p2 + d2 * t);
    }

    /// <summary>Highest walkable surface under <paramref name="p"/> within <paramref name="reach"/>
    /// below — skips the player, bosses and anything carrying Health.</summary>
    public static bool FloorAt(Vector3 p, float reach, out Vector3 point)
    {
        point = p;
        var hits = Physics.RaycastAll(p + Vector3.up * 1.2f, Vector3.down, reach + 1.2f, ~0, QueryTriggerInteraction.Ignore);
        var best = float.MaxValue;
        foreach (var h in hits)
        {
            if (h.normal.y < 0.5f || h.collider.GetComponentInParent<PlayerHealth>() != null
                || h.collider.GetComponentInParent<Health>() != null) continue;
            if (h.distance < best) { best = h.distance; point = h.point; }
        }
        return best < float.MaxValue;
    }
}

/// <summary>Runtime knockback on the player (added by <see cref="WardenHazard.Shove"/>):
/// an ease-out push through the player's own CharacterController, so walls and
/// ledges still stop it. Cancels the moment a wall-run, death or the Core leap
/// (rooted) takes over.</summary>
public sealed class WardenShove : MonoBehaviour
{
    private CharacterController cc;
    private PlayerState st;
    private WallRunController wall;
    private Vector3 total;
    private float t, dur;

    public void Push(Vector3 displacement, float seconds)
    {
        if (cc == null) { cc = GetComponent<CharacterController>(); st = GetComponent<PlayerState>(); wall = GetComponent<WallRunController>(); }
        total = displacement;
        dur = Mathf.Max(0.05f, seconds);
        t = 0f;
        enabled = true;
    }

    private void Update()
    {
        if (cc == null || !cc.enabled || t >= dur || (st != null && (st.IsDead || st.IsRooted)) || (wall != null && wall.IsWallRunning))
        {
            enabled = false;
            return;
        }
        var k0 = t / dur;
        t = Mathf.Min(dur, t + Time.deltaTime);
        var k1 = t / dur;
        // Ease-out: most of the push lands in the first frames.
        float E(float k) => 1f - (1f - k) * (1f - k);
        cc.Move(total * (E(k1) - E(k0)));
    }
}

/// <summary>A faceted crimson spike punched out of the floor — eruption beat. Pure visual.</summary>
public sealed class EruptionSpike : MonoBehaviour
{
    private float height, radius, life, t;
    private MeshRenderer rend;
    private MaterialPropertyBlock mpb;

    public static void Spawn(Vector3 at, float height, float radius, float life = 0.55f)
    {
        var go = new GameObject("Eruption spike");
        go.transform.SetPositionAndRotation(at, Quaternion.Euler(Random.Range(-7f, 7f), Random.value * 360f, Random.Range(-7f, 7f)));
        go.transform.localScale = new Vector3(radius, 0.01f, radius);
        var s = go.AddComponent<EruptionSpike>();
        s.height = height;
        s.radius = radius;
        s.life = life;
        go.AddComponent<MeshFilter>().sharedMesh = WardenFx.SpikeMesh;
        s.rend = go.AddComponent<MeshRenderer>();
        s.rend.sharedMaterial = WardenFx.GlowMaterial;
        s.rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        s.rend.receiveShadows = false;
        s.mpb = new MaterialPropertyBlock();
        WardenHazard.Track(go);
    }

    private void Update()
    {
        t += Time.deltaTime;
        var k = t / life;
        if (k >= 1f) { Destroy(gameObject); return; }
        var rise = Mathf.Clamp01(t / 0.07f);
        var sink = k > 0.55f ? (k - 0.55f) / 0.45f : 0f;
        var w = radius * (1f - sink * 0.4f);
        transform.localScale = new Vector3(w, Mathf.Max(0.01f, height * rise * (1f - sink * 0.7f)), w);
        var c = Color.white;
        c.a = WardenFx.Stepped(1f - sink);
        mpb.SetColor("_Tint", c);
        rend.SetPropertyBlock(mpb);
    }
}

/// <summary>
/// A summoned weapon: forms with a chime (shards converge, it scales in), holds
/// wherever its owner parks it, swells before firing, then flies straight —
/// never homing — leaving a thin crimson trail, sticks into the first solid
/// surface and breaks apart. One body hit per flight.
/// The weapon is REAL: a copy of his own sword (<see cref="ArsenalKind.Own"/>) or a
/// Synty sword / greatsword / axe / spear / halberd / scythe prefab from the
/// <see cref="WardenArmory"/>, with its own mesh and material — the Core's energy
/// is only layered on (a hot crimson spine, a trail, an emission swell before it
/// fires). The baked ghost and the procedural blade are fallbacks for a project
/// without the armoury. Spinning weapons (thrown axes, the wheel) turn about
/// their middle; their hazard is a sphere around <see cref="Centre"/>.
/// </summary>
public sealed class SpectralBlade : MonoBehaviour
{
    public enum Phase { Forming, Holding, Flying, Stuck, Dissolving }

    private static readonly int TintId = Shader.PropertyToID("_Tint");
    private static readonly int RimId = Shader.PropertyToID("_Rim");
    private static readonly int FadeId = Shader.PropertyToID("_Fade");
    private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");

    public Phase State { get; private set; }
    /// <summary>Legacy size: 1 = the 1.55 m crown blade.</summary>
    public float Scale => length / 1.55f;
    public float Length => length;
    public ArsenalKind Kind => kind;
    public bool Spinning => spinRate != 0f;
    public Vector3 Tip => transform.position + transform.up * length;
    public Vector3 Centre => transform.position + transform.up * (length * 0.5f);

    private Transform spinner, visual;
    private MeshRenderer rend;
    private bool real;
    private readonly List<Renderer> realRends = new List<Renderer>();
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int EmissionStrengthId = Shader.PropertyToID("_EmissionStrength");
    private static readonly Dictionary<Material, Material> summoned = new Dictionary<Material, Material>();
    private MaterialPropertyBlock mpb;
    private TrailRenderer trail;
    private LineRenderer spine;
    private Transform ignore;
    private ArsenalKind kind;
    private bool ghost;
    private float length = 1.55f, visualScale = 1f, t, formTime = 0.25f, glow, alpha = 1f, dissolve;
    private float speed, damage, maxDist, travelled, spinRate;
    private Vector3 dir, spinAxis = Vector3.right;
    private bool hitPlayer;

    /// <summary>The classic procedural blade (crown slots, rain, wall chase).</summary>
    public static SpectralBlade Spawn(Vector3 pos, Quaternion rot, float scale, Transform ignore, float formTime = 0.25f,
                                      bool chime = true, float chimePitch = 1f)
        => Spawn(ArsenalKind.Blade, -1, 1.55f * scale, pos, rot, ignore, formTime, chime, chimePitch);

    /// <summary>A weapon of <paramref name="kind"/>, <paramref name="length"/> metres grip → tip
    /// (pivot at the grip, +Y to the tip). Variant -1 = random.</summary>
    public static SpectralBlade Spawn(ArsenalKind kind, int variant, float length, Vector3 pos, Quaternion rot, Transform ignore,
                                      float formTime = 0.25f, bool chime = true, float chimePitch = 1f)
    {
        var go = new GameObject("Spectral " + kind);
        go.transform.SetPositionAndRotation(pos, rot);
        var b = go.AddComponent<SpectralBlade>();
        b.kind = kind;
        b.length = Mathf.Max(0.2f, length);
        b.ignore = ignore;
        b.formTime = Mathf.Max(0.01f, formTime);
        b.Build(variant);
        WardenHazard.Track(go);
        var size = Mathf.Sqrt(b.length / 1.55f);
        WardenFx.Converge(pos + rot * Vector3.up * b.length * 0.5f, 10, 0.6f * b.length, b.formTime, WardenFx.Crimson, size);
        if (chime) WardenAudio.Play("chime", pos, 0.5f, chimePitch);
        return b;
    }

    private void Build(int variant)
    {
        spinner = new GameObject("Spinner").transform;
        spinner.SetParent(transform, false);
        visual = new GameObject("Visual").transform;
        visual.SetParent(spinner, false);
        mpb = new MaterialPropertyBlock();
        var prefab = WardenArsenal.Prefab(kind, variant);
        real = prefab != null && BuildReal(prefab);
        Mesh baked = null;
        if (!real)
        {
            baked = WardenArsenal.Get(kind, variant);
            ghost = baked != null;
            visual.gameObject.AddComponent<MeshFilter>().sharedMesh = ghost ? baked : WardenFx.BladeMesh;
            rend = visual.gameObject.AddComponent<MeshRenderer>();
            rend.sharedMaterial = ghost ? WardenArsenal.Material : WardenFx.GlowMaterial;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
        }
        visualScale = real ? 1f : ghost ? length : length / 1.55f;
        visual.localScale = Vector3.zero;

        // A thin hot spine down the blade: the weapon reads as a line of danger
        // even face-on at 640 lines, and marks it as the Core's, not a prop.
        var sgo = new GameObject("Spine");
        sgo.transform.SetParent(transform, false);
        spine = sgo.AddComponent<LineRenderer>();
        spine.sharedMaterial = WardenFx.GlowMaterial;
        spine.useWorldSpace = true;
        spine.positionCount = 2;
        spine.numCapVertices = 0;
        spine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        spine.receiveShadows = false;
        spine.enabled = ghost || real;

        var tip = new GameObject("Trail");
        tip.transform.SetParent(visual, false);
        tip.transform.localPosition = Vector3.up * (real ? length * 0.92f : ghost ? 0.92f : 1.45f);
        trail = tip.AddComponent<TrailRenderer>();
        trail.sharedMaterial = WardenFx.GlowMaterial;
        trail.time = 0.2f;
        trail.minVertexDistance = 0.05f;
        trail.widthMultiplier = 0.08f * Mathf.Sqrt(length / 1.55f);
        trail.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(WardenFx.Crimson, 0f), new GradientColorKey(WardenFx.CrimsonDeep, 1f) },
                  new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0f, 1f) });
        trail.colorGradient = g;
        trail.numCapVertices = 0;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        trail.emitting = false;
        State = Phase.Forming;
    }

    /// <summary>Instantiate the real weapon under the visual, normalised so the grip
    /// sits at the origin, the blade/haft runs up +Y and the tip (or head) is at
    /// <see cref="length"/> metres — measured from its mesh bounds, so any Synty pivot
    /// convention (and his own centimetre-scale sword) lands the same way.</summary>
    private bool BuildReal(GameObject prefab)
    {
        var fit = new GameObject("Fit").transform;
        fit.SetParent(visual, false);
        var inst = Instantiate(prefab, fit, false);
        inst.name = prefab.name;
        inst.transform.localPosition = Vector3.zero;
        inst.transform.localRotation = Quaternion.identity;
        inst.transform.localScale = prefab.transform.localScale;
        foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Destroy(c);
        foreach (var rb in inst.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);
        foreach (var mb in inst.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(mb);
        foreach (var l in inst.GetComponentsInChildren<Light>(true)) l.enabled = false;
        foreach (var ps in inst.GetComponentsInChildren<ParticleSystem>(true)) ps.gameObject.SetActive(false);

        // Bounds in the instance root's frame (no readable mesh needed).
        var root = inst.transform;
        var any = false;
        var min = Vector3.one * float.MaxValue;
        var max = Vector3.one * float.MinValue;
        foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            var lower = mf.name.ToLowerInvariant();
            if (lower.Contains("collision") || lower.Contains("lod1") || lower.Contains("lod2")) { mf.gameObject.SetActive(false); continue; }
            var bb = mf.sharedMesh.bounds;
            for (var i = 0; i < 8; i++)
            {
                var corner = bb.center + Vector3.Scale(bb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = root.InverseTransformPoint(mf.transform.TransformPoint(corner));
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        }
        if (!any) { Destroy(fit.gameObject); return false; }
        var size = max - min;
        var axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        var reachAll = size[axis];
        if (reachAll < 0.01f) { Destroy(fit.gameObject); return false; }
        // Synty hand-held weapons pivot at the grip: the far end is the business end.
        float tipSign, gripCoord;
        var nearMin = -min[axis];
        var nearMax = max[axis];
        if (min[axis] <= 0.01f * reachAll && max[axis] >= -0.01f * reachAll && Mathf.Min(nearMin, nearMax) < 0.35f * reachAll)
        {
            tipSign = nearMax >= nearMin ? 1f : -1f;
            gripCoord = 0f;
        }
        else
        {
            tipSign = 1f;
            gripCoord = min[axis] + 0.12f * reachAll;
        }
        var tipCoord = tipSign > 0f ? max[axis] : min[axis];
        var reach = Mathf.Abs(tipCoord - gripCoord);
        if (reach < 0.01f) { Destroy(fit.gameObject); return false; }
        var up = Vector3.zero; up[axis] = tipSign;
        int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
        var right = Vector3.zero; right[size[a1] >= size[a2] ? a1 : a2] = 1f;
        var fwd = Vector3.Cross(right, up);
        var toLocal = Quaternion.Inverse(Quaternion.LookRotation(fwd, up));
        var grip = (min + max) * 0.5f;
        grip[axis] = gripCoord;
        var k = length / reach;
        fit.localRotation = toLocal;
        fit.localScale = Vector3.one * k;
        fit.localPosition = -(toLocal * (grip * k));

        foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            var m = r.sharedMaterial;
            // Legacy Standard (DungeonRealms) renders magenta under URP: re-shade it.
            if (m != null && m.shader != null && (m.shader.name == "Standard" || m.shader.name.StartsWith("Legacy Shaders") || !m.shader.isSupported))
                VendorUrp.Fix(r);
            var mats = r.sharedMaterials;
            for (var i = 0; i < mats.Length; i++) mats[i] = Summoned(mats[i]);
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            realRends.Add(r);
        }
        return realRends.Count > 0;
    }

    /// <summary>A URP Lit material with emission switched on (black at rest) so the
    /// "about to fire" swell can push crimson through a property block; custom
    /// shaders (his wire-shaded sword) keep their material — their own
    /// _EmissionStrength carries the swell.</summary>
    private static Material Summoned(Material src)
    {
        if (src == null || !src.HasProperty(EmissionColorId) || src.HasProperty(EmissionStrengthId)) return src;
        if (summoned.TryGetValue(src, out var m) && m != null) return m;
        m = new Material(src) { name = src.name + " (summoned)" };
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        m.SetColor(EmissionColorId, Color.black);
        summoned[src] = m;
        return m;
    }

    /// <summary>0..1 swell toward white — the "this one fires next" tell.</summary>
    public void SetGlow(float k) => glow = Mathf.Clamp01(k);

    /// <summary>Spin the visual about its middle (thrown axes, the wheel). 0 stops and squares it up.</summary>
    public void SetSpin(Vector3 localAxis, float degPerSec)
    {
        spinRate = degPerSec;
        spinAxis = localAxis.sqrMagnitude > 1e-4f ? localAxis.normalized : Vector3.right;
        if (degPerSec != 0f)
        {
            spinner.localPosition = Vector3.up * length * 0.5f;
            visual.localPosition = Vector3.down * length * 0.5f;
        }
        else
        {
            spinner.localPosition = Vector3.zero;
            spinner.localRotation = Quaternion.identity;
            visual.localPosition = Vector3.zero;
        }
    }

    /// <summary>Turn the tip toward <paramref name="point"/> at a capped rate.</summary>
    public void TurnToward(Vector3 point, float degPerSec)
    {
        var want = Quaternion.FromToRotation(Vector3.up, (point - transform.position).normalized);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, degPerSec * Time.deltaTime);
    }

    /// <param name="spin">End-over-end degrees per second (axes); 0 = flies point-first.</param>
    public void Fire(Vector3 direction, float speed, float damage, float maxDistance, float spin = 0f)
    {
        if (State == Phase.Dissolving) return;
        dir = direction.sqrMagnitude > 1e-4f ? direction.normalized : Vector3.down;
        this.speed = speed;
        this.damage = damage;
        maxDist = maxDistance;
        travelled = 0f;
        hitPlayer = false;
        visual.localScale = Vector3.one * visualScale;
        transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
        SetSpin(Vector3.right, spin);
        trail.Clear();
        trail.emitting = true;
        State = Phase.Flying;
        WardenAudio.Play("swish", transform.position, 0.45f, spin != 0f ? 0.8f : 1.2f);
    }

    /// <summary>Lodge the blade (point = where the tip ends up) — used by scripted drops.</summary>
    public void StickAt(Vector3 tipPoint, float embed = 0.25f)
    {
        if (spinRate != 0f) SetSpin(Vector3.right, 0f);
        transform.position = tipPoint - transform.up * (length - embed * length / 1.55f);
        trail.emitting = false;
        State = Phase.Stuck;
        t = 0f;
    }

    /// <summary>Owner-driven weapons (wheels, impalers, the guillotine) park here; Update leaves them be.</summary>
    public void Hold() { if (State == Phase.Flying || State == Phase.Stuck) State = Phase.Holding; trail.emitting = false; }

    public void Dissolve(float seconds = 0.3f)
    {
        if (State == Phase.Dissolving) return;
        State = Phase.Dissolving;
        t = 0f;
        formTime = Mathf.Max(0.05f, seconds);
        trail.emitting = false;
        WardenFx.Shards(Centre, 5, 1.1f, WardenFx.Crimson, rise: true, size: Mathf.Sqrt(length / 1.55f));
    }

    private void Update()
    {
        var dt = Time.deltaTime;
        t += dt;
        switch (State)
        {
            case Phase.Forming:
                var k = Mathf.Clamp01(t / formTime);
                visual.localScale = Vector3.one * visualScale * (k * k * (3f - 2f * k));
                if (k >= 1f) State = Phase.Holding;
                break;
            case Phase.Flying:
                Fly(dt);
                break;
            case Phase.Stuck:
                if (t >= 0.7f) Dissolve();
                break;
            case Phase.Dissolving:
                var d = t / formTime;
                if (d >= 1f) { Destroy(gameObject); return; }
                alpha = WardenFx.Stepped(1f - d);
                dissolve = d;
                // Opaque steel can't fade: it breaks apart — a quick shrink under the shard burst.
                if (real) visual.localScale = Vector3.one * visualScale * Mathf.Sqrt(Mathf.Max(0f, 1f - d));
                break;
        }
        if (spinRate != 0f) spinner.localRotation = Quaternion.AngleAxis(spinRate * dt, spinAxis) * spinner.localRotation;
        Paint();
    }

    private void Paint()
    {
        if (real)
        {
            // Real weapons keep their material; the Core's energy is an emission swell.
            var e = WardenFx.Crimson * (0.18f + glow * 2.6f);
            e.a = 1f;
            foreach (var r in realRends)
            {
                if (r == null) continue;
                r.GetPropertyBlock(mpb);
                if (r.sharedMaterial != null && r.sharedMaterial.HasProperty(EmissionStrengthId)) mpb.SetFloat(EmissionStrengthId, 1.2f + glow * 3.5f);
                else mpb.SetColor(EmissionColorId, e);
                r.SetPropertyBlock(mpb);
            }
        }
        else if (ghost)
        {
            var fill = WardenArsenal.Fill;
            fill = Color.Lerp(fill, new Color(1f, 0.35f, 0.32f, fill.a), glow * 0.5f);
            fill.a = Mathf.Min(0.8f, WardenArsenal.Fill.a + glow * 0.3f);
            var rim = Color.Lerp(WardenArsenal.Rim, Color.white, glow * 0.7f) * (1f + glow * 0.8f);
            rim.a = 1f;
            mpb.SetColor(TintId, fill);
            mpb.SetColor(RimId, rim);
            mpb.SetFloat(FadeId, alpha);
            mpb.SetFloat(DissolveId, dissolve);
            rend.SetPropertyBlock(mpb);
        }
        else
        {
            var c = Color.white * (1f + glow * 1.3f);
            c.a = alpha;
            mpb.SetColor(TintId, c);
            rend.SetPropertyBlock(mpb);
        }

        if (!spine.enabled) return;
        var unit = real ? length : 1f;
        var a = visual.TransformPoint(Vector3.up * 0.08f * unit);
        var b2 = visual.TransformPoint(Vector3.up * 0.97f * unit);
        spine.SetPosition(0, a);
        spine.SetPosition(1, b2);
        var form = State == Phase.Forming ? Mathf.Clamp01(t / formTime) : 1f;
        var w = (real ? 0.022f : 0.03f) * Mathf.Sqrt(length) * (1f + glow * 0.9f) * form;
        spine.startWidth = w;
        spine.endWidth = w * 0.4f;
        var sc = Color.Lerp(WardenFx.Crimson, WardenFx.PaleRed, glow * 0.8f);
        sc.a = alpha * (real ? 0.75f : 0.9f);
        spine.startColor = spine.endColor = sc;
    }

    private void Fly(float dt)
    {
        var step = speed * dt;
        var spinning = spinRate != 0f;
        var from = spinning ? Centre : Tip;
        var reach = spinning ? length * 0.5f : 0.05f;
        var stop = float.MaxValue;
        RaycastHit stopHit = default;
        foreach (var h in Physics.RaycastAll(from, dir, step + reach, ~0, QueryTriggerInteraction.Ignore))
        {
            if (Ignored(h.collider) || h.distance >= stop) continue;
            stop = h.distance;
            stopHit = h;
        }
        var travel = Mathf.Min(step, Mathf.Max(0f, stop - reach + 0.05f));
        var p1 = from + dir * travel;
        if (!hitPlayer && damage > 0f)
        {
            var near = spinning
                ? WardenHazard.SegmentToBody(from, p1) < length * 0.5f + 0.35f
                : WardenHazard.SegmentToBody(from - dir * 0.45f * length, p1) < 0.42f + 0.06f * length;
            if (near)
            {
                hitPlayer = true;
                if (WardenHazard.Damage(damage, from)) WardenFx.Spikes(p1, 6, 0.6f, WardenFx.Crimson, 0.16f, 0.06f);
            }
        }
        transform.position += dir * travel;
        travelled += travel;
        if (stop < float.MaxValue)
        {
            var n = stopHit.normal;
            if (spinning)
            {
                // A thrown axe bites in head-first along its flight line.
                transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
                StickAt(stopHit.point, 0.35f);
            }
            else StickAt(stopHit.point, 0.25f);
            var size = Mathf.Sqrt(length / 1.55f);
            WardenFx.Peak(stopHit.point + n * 0.2f, 0.35f * size);
            WardenFx.Ring(stopHit.point + n * 0.03f, n, 0.12f, 0.6f * length, 0.26f, WardenFx.Crimson, 0.06f, 12);
            WardenFx.Sparks(stopHit.point, n, 5, 3.2f);
            WardenFx.Debris(stopHit.point, 3, 3f, 0.7f);
            WardenFx.Dust(stopHit.point, 2, 0.8f, 0.6f);
            WardenAudio.Play(kind == ArsenalKind.Axe || kind == ArsenalKind.Greatsword ? "metal" : "metalLight",
                             stopHit.point, 0.55f, Random.Range(0.9f, 1.1f));
            CoreSanctum.Active?.StrikeAt(stopHit.collider, stopHit.point, dir, 0.25f * size);
        }
        else if (travelled >= maxDist) Dissolve();
    }

    private bool Ignored(Collider c)
        => (ignore != null && c.transform.IsChildOf(ignore))
           || c.GetComponentInParent<PlayerHealth>() != null
           || c.GetComponentInParent<Health>() != null
           || c.GetComponentInParent<SpectralBlade>() != null;
}

/// <summary>
/// One falling weapon of a sword rain, in three beats: the weapon forms in the
/// sky FIRST (pointing down, glowing faintly), THEN its crimson circle appears on
/// the exact spot <see cref="lead"/> seconds ahead (0.6–0.9s — readable, never
/// boring), THEN it drops to land as the circle peaks. Small strikes get a small
/// flash, a puff and a few chips; only the giant one gets the full impact.
/// </summary>
public sealed class RainStrike : MonoBehaviour
{
    private Vector3 ground, top;
    private float lead, radius, damage, length, t, fall, formLead;
    private bool big, dropping, landed, marked;
    private WardenMark mark;
    private SpectralBlade blade;

    public static RainStrike Spawn(Vector3 ground, float lead, float radius, float damage, float scale, Transform owner, bool big = false,
                                   ArsenalKind kind = ArsenalKind.Blade)
    {
        var go = new GameObject(big ? "Judgment blade" : "Rain blade");
        var s = go.AddComponent<RainStrike>();
        s.ground = ground;
        s.lead = Mathf.Max(0.3f, lead);
        s.radius = radius;
        s.damage = damage;
        s.big = big;
        s.length = kind == ArsenalKind.Blade || !WardenArsenal.Has(kind) ? 1.55f * scale : WardenArsenal.NaturalLength(kind, scale * 0.85f);
        s.fall = big ? 0.38f : 0.18f;
        s.formLead = big ? 0.9f : 0.32f;
        s.top = ground + Vector3.up * (big ? 16f : 9f) + Vector3.up * s.length;
        s.blade = SpectralBlade.Spawn(kind, -1, s.length, s.top, Quaternion.FromToRotation(Vector3.up, Vector3.down), owner,
                                      big ? 0.6f : 0.2f, chime: big, chimePitch: 0.7f);
        WardenHazard.Track(go);
        return s;
    }

    private void Update()
    {
        t += Time.deltaTime;
        // Beat 1: the weapon hangs in the sky, already aimed.
        if (t < formLead)
        {
            if (blade != null) blade.SetGlow(0.15f * t / formLead);
            return;
        }
        // Beat 2: the mark appears where it will land.
        if (!marked)
        {
            marked = true;
            mark = WardenMark.Circle(ground, radius, WardenFx.Crimson, big ? 0.14f : 0.07f, big ? 0.2f : 0.14f, big ? 48 : 24);
            mark.SetAlpha(0f);
        }
        var u = t - formLead;
        var k = u / lead;
        if (!landed)
        {
            mark.SetAlpha(Mathf.Clamp01(k * 4f));
            mark.SetPulse(k > 0.7f ? (k - 0.7f) / 0.3f : 0f);
            if (blade != null) blade.SetGlow(0.15f + 0.85f * k);
        }
        // Beat 3: the drop.
        if (!dropping && u >= lead - fall) dropping = true;
        if (dropping && !landed && blade != null)
        {
            var f = Mathf.Clamp01((u - (lead - fall)) / fall);
            var gripAtLand = ground + Vector3.up * (length * 0.8f);
            blade.transform.position = Vector3.Lerp(top, gripAtLand, f * f);
        }
        if (!landed && u >= lead) Land();
        if (landed && u >= lead + 0.55f)
        {
            if (blade != null) blade.Dissolve();
            Destroy(gameObject);
        }
    }

    private void Land()
    {
        landed = true;
        if (blade != null) blade.StickAt(ground, 0.3f);
        mark.Release(0.12f);
        var flat = Vector3.ProjectOnPlane(WardenHazard.Feet - ground, Vector3.up).magnitude;
        var h = WardenHazard.Feet.y - ground.y;
        if (flat <= radius && h < (big ? 4f : 2.4f)) WardenHazard.Damage(damage, ground);
        if (big)
        {
            WardenFx.Impact(ground, 2.4f, shake: 0.55f, hitstop: 0.08f, debris: 12, dust: 1.6f, cracks: 1.4f);
            WardenAudio.Play("boom", ground, 1f, 0.9f);
            WardenAudio.Play("metal", ground, 0.8f, 0.8f);
        }
        else
        {
            WardenFx.Peak(ground + Vector3.up * 0.3f, 0.32f);
            WardenFx.Ring(ground + Vector3.up * 0.04f, Vector3.up, radius * 0.4f, radius * 0.7f, 0.22f, WardenFx.Crimson, 0.06f, 16);
            WardenFx.Sparks(ground + Vector3.up * 0.05f, Vector3.up, 4, 3f);
            WardenFx.Dust(ground, 2, 0.9f, 0.55f);
            WardenFx.Debris(ground, 3, 3.2f, 0.7f);
            WardenAudio.Play("metalLight", ground, 0.38f, Random.Range(0.85f, 1.15f));
        }
    }
}

/// <summary>
/// Grave of Kings: a colossal spectral sword driven tip-down out of the floor
/// like a headstone. <see cref="Prime"/> sends the red glow into it; after the
/// delay it bursts (Core burst + a black smoke puff + spectral fragments). The
/// husk stays until <see cref="DissolveUp"/> lifts it away.
/// </summary>
public sealed class GraveBlade : MonoBehaviour
{
    private enum Step { Emerging, Planted, Primed, Burst, Rising }

    private Vector3 ground, buried, planted;
    private float scale, radius, damage, t, delay;
    private Step step;
    private SpectralBlade blade;
    private WardenMark mark;
    private LineRenderer glowLine;

    public bool HasBurst => step >= Step.Burst;

    public static GraveBlade Spawn(Vector3 ground, float scale, float radius, float damage, Transform owner,
                                   ArsenalKind kind = ArsenalKind.Greatsword)
    {
        var go = new GameObject("Grave blade");
        var g = go.AddComponent<GraveBlade>();
        g.ground = ground;
        g.scale = scale;
        g.radius = radius;
        g.damage = damage;
        var len = 1.55f * scale;
        g.buried = ground + Vector3.up * -0.1f;
        g.planted = ground + Vector3.up * len * 0.62f;
        g.blade = SpectralBlade.Spawn(kind, -1, len, g.buried, Quaternion.FromToRotation(Vector3.up, Vector3.down), owner, 0.05f, chime: false);
        var lr = new GameObject("Glow").AddComponent<LineRenderer>();
        lr.transform.SetParent(go.transform, false);
        lr.sharedMaterial = WardenFx.GlowMaterial;
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.numCapVertices = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.enabled = false;
        g.glowLine = lr;
        WardenFx.Debris(ground, 6, 4f, 1.2f);
        WardenFx.Dust(ground, 5, 1.4f, 1f);
        WardenFx.Cracks(ground, 4, 1.4f, WardenFx.CrimsonDeep, 0.2f, 1.6f);
        WardenAudio.Play("stone", ground, 0.6f, 0.75f);
        WardenHazard.Track(go);
        return g;
    }

    /// <summary>The glow arrives: <paramref name="delay"/> seconds later it bursts.</summary>
    public void Prime(float delay)
    {
        if (step >= Step.Primed) return;
        step = Step.Primed;
        this.delay = delay;
        t = 0f;
        mark = WardenMark.Circle(ground, radius, WardenFx.Crimson, 0.1f, 0.18f, 32);
        glowLine.enabled = true;
        WardenAudio.Play("pulse", ground, 0.6f, 1.25f);
    }

    public void DissolveUp()
    {
        if (step == Step.Rising) return;
        step = Step.Rising;
        t = 0f;
        if (mark != null) mark.Release(0.1f);
    }

    private void Update()
    {
        t += Time.deltaTime;
        switch (step)
        {
            case Step.Emerging:
            {
                var k = Mathf.Clamp01(t / 0.7f);
                if (blade != null) blade.transform.position = Vector3.Lerp(buried, planted, 1f - (1f - k) * (1f - k));
                if (k >= 1f) step = Step.Planted;
                break;
            }
            case Step.Primed:
            {
                var k = Mathf.Clamp01(t / delay);
                if (blade != null) blade.SetGlow(k);
                mark.SetPulse(k);
                // The glow climbs from the floor up the blade to the hilt.
                var a = ground + Vector3.up * 0.05f;
                var b = Vector3.Lerp(a, planted, k);
                glowLine.SetPosition(0, a);
                glowLine.SetPosition(1, b);
                glowLine.startWidth = glowLine.endWidth = 0.16f * scale * 0.3f;
                var c = Color.Lerp(WardenFx.Crimson, Color.white, k * 0.4f);
                glowLine.startColor = glowLine.endColor = c;
                if (k >= 1f) Burst();
                break;
            }
            case Step.Rising:
            {
                var k = Mathf.Clamp01(t / 0.9f);
                if (blade != null) blade.transform.position = Vector3.Lerp(planted, planted + Vector3.up * 3f, k * k);
                if (k >= 1f)
                {
                    if (blade != null) blade.Dissolve(0.25f);
                    Destroy(gameObject);
                }
                break;
            }
        }
    }

    private void Burst()
    {
        step = Step.Burst;
        glowLine.enabled = false;
        mark.Release(0.12f);
        mark = null;
        var flat = Vector3.ProjectOnPlane(WardenHazard.Feet - ground, Vector3.up).magnitude;
        if (flat <= radius && WardenHazard.Feet.y - ground.y < 3.5f) WardenHazard.Damage(damage, ground);
        var mid = ground + Vector3.up * 1.2f;
        WardenFx.Peak(mid, 1.1f);
        WardenFx.Ring(ground + Vector3.up * 0.06f, Vector3.up, 0.6f, radius, 0.4f, WardenFx.Crimson, 0.16f, 28, 0.2f);
        WardenFx.Shards(mid, 18, 6f, WardenFx.Crimson, false, 1.4f, 0.8f, Vector3.up * 0.4f, 0.6f);
        WardenFx.Dust(ground, 6, 1.6f, 1.1f, WardenFx.Ink * 1.6f, 1.2f);
        WardenFx.Shake(0.16f);
        WardenAudio.Play("boom", ground, 0.55f, 1.25f);
        WardenAudio.Play("shatter", ground, 0.45f, 0.8f);
        if (blade != null) blade.SetGlow(0f);
    }
}

/// <summary>
/// Worldsplitter's second hit: a thin crack shoots across the arena, SITS for
/// <see cref="wait"/> seconds (the strip shows exactly where), then a vertical
/// Crimson eruption travels along it. The impact is never the only danger —
/// and the delayed one is never unmarked.
/// </summary>
public sealed class CrackEruption : MonoBehaviour
{
    private Vector3 origin, dir, side;
    private float length, wait, halfWidth, damage, t, front, nextSpike;
    private const float Speed = 34f;
    private bool erupting, hit, done;
    private WardenMark strip;

    public static CrackEruption Spawn(Vector3 origin, Vector3 dir, float length, float wait, float halfWidth, float damage)
    {
        var go = new GameObject("Crack eruption");
        var c = go.AddComponent<CrackEruption>();
        c.origin = origin;
        c.dir = Vector3.ProjectOnPlane(dir, Vector3.up).normalized;
        c.side = Vector3.Cross(Vector3.up, c.dir);
        c.length = length;
        c.wait = wait;
        c.halfWidth = halfWidth;
        c.damage = damage;
        // The crack shoots across fast, then holds glowing until the eruption passes.
        var pts = new List<Vector3> { origin + Vector3.up * 0.04f };
        var s = 0f;
        while (s < length)
        {
            s = Mathf.Min(length, s + Random.Range(0.5f, 0.9f));
            pts.Add(origin + c.dir * s + c.side * Random.Range(-0.18f, 0.18f) + Vector3.up * 0.04f);
        }
        WardenFx.Line(pts, WardenFx.Crimson, 0.09f, wait + length / Speed + 0.6f, 0.14f, 0.82f);
        c.strip = WardenMark.Line(origin, origin + c.dir * length, halfWidth, WardenFx.Crimson, 0.05f, 0.12f);
        WardenAudio.Play("crack", origin, 0.8f, 0.8f);
        WardenHazard.Track(go);
        return c;
    }

    private void Update()
    {
        if (done) return;
        var dt = Time.deltaTime;
        t += dt;
        if (!erupting)
        {
            strip.SetPulse(0.5f + 0.5f * Mathf.Sin(t * 22f));
            if (t >= wait)
            {
                erupting = true;
                WardenAudio.Play("boom", origin + dir * Mathf.Min(6f, length), 1f, 1f);
                WardenFx.Shake(0.3f);
            }
            return;
        }
        var prev = front;
        front = Mathf.Min(length, front + Speed * dt);
        while (nextSpike <= front)
        {
            var p = origin + dir * nextSpike + side * Random.Range(-halfWidth, halfWidth) * 0.55f;
            if (WardenHazard.FloorAt(p, 1.5f, out var floor))
            {
                EruptionSpike.Spawn(floor, Random.Range(2.6f, 3.6f), Random.Range(0.4f, 0.6f));
                if (Random.value < 0.3f) WardenFx.Debris(floor, 2, 4f, 0.8f);
            }
            nextSpike += 0.7f;
        }
        if (!hit)
        {
            var rel = WardenHazard.Feet - origin;
            var s = Vector3.Dot(rel, dir);
            var lateral = Mathf.Abs(Vector3.Dot(rel, side));
            if (s >= prev - 0.6f && s <= front + 0.3f && lateral <= halfWidth && rel.y < 3.3f)
            {
                hit = true;
                WardenHazard.Damage(damage, origin + dir * s);
            }
        }
        if (front >= length)
        {
            done = true;
            strip.Release(0.2f);
            Destroy(gameObject, 0.3f);
        }
    }
}

/// <summary>
/// An expanding wave on the floor — a full ring (the Twin Rupture / King's Fall
/// shockwaves: jump it) or a sector with a tall fin (Ruinous Sweep: get on a
/// wall). Faceted, ink-backed; hits once while the band passes the feet.
/// </summary>
public sealed class GroundWave : MonoBehaviour
{
    private Vector3 centre, fwd;
    private float arc, maxR, speed, height, band, damage, r;
    private bool safeOnWall, hit;
    private Color col;
    private LineRenderer bottom, ink, top;
    private Mesh fin;
    private MeshRenderer finR;
    private readonly List<Vector3> verts = new List<Vector3>();
    private readonly List<Color> cols = new List<Color>();
    private readonly List<int> tris = new List<int>();
    private int segs;

    public static GroundWave Spawn(Vector3 centre, Vector3 forward, float arcDeg, float maxRadius, float speed, float height,
                                   float damage, bool safeOnWall = true, float startRadius = 0.6f, float band = 0.65f, Color? col = null)
    {
        var go = new GameObject(arcDeg >= 359f ? "Core ring" : "Ground wave");
        var w = go.AddComponent<GroundWave>();
        w.centre = centre;
        w.fwd = Vector3.ProjectOnPlane(forward, Vector3.up).sqrMagnitude > 1e-4f ? Vector3.ProjectOnPlane(forward, Vector3.up).normalized : Vector3.forward;
        w.arc = Mathf.Clamp(arcDeg, 10f, 360f);
        w.maxR = maxRadius;
        w.speed = speed;
        w.height = height;
        w.damage = damage;
        w.safeOnWall = safeOnWall;
        w.r = startRadius;
        w.band = band;
        w.col = col ?? WardenFx.Crimson;
        w.segs = w.arc >= 359f ? 48 : Mathf.Max(8, Mathf.RoundToInt(w.arc / 7.5f));
        w.ink = w.Line("Ink", 0);
        w.bottom = w.Line("Base", 2);
        w.top = w.Line("Crest", 2);
        var finGo = new GameObject("Fin");
        finGo.transform.SetParent(go.transform, false);
        w.fin = new Mesh { name = "Wave fin" };
        w.fin.MarkDynamic();
        finGo.AddComponent<MeshFilter>().sharedMesh = w.fin;
        w.finR = finGo.AddComponent<MeshRenderer>();
        w.finR.sharedMaterial = WardenFx.GlowMaterial;
        w.finR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        WardenHazard.Track(go);
        return w;
    }

    private LineRenderer Line(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = WardenFx.GlowMaterial;
        lr.useWorldSpace = true;
        lr.numCornerVertices = 0;
        lr.numCapVertices = 0;
        lr.sortingOrder = order;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.positionCount = segs + 1;
        return lr;
    }

    private void Update()
    {
        r += speed * Time.deltaTime;
        if (r >= maxR) { Destroy(gameObject); return; }
        var k = r / maxR;
        var a = WardenFx.Stepped(1f - k * 0.55f);
        verts.Clear(); cols.Clear(); tris.Clear();
        var start = -arc * 0.5f;
        for (var i = 0; i <= segs; i++)
        {
            var ang = start + arc * i / segs;
            var d = Quaternion.AngleAxis(ang, Vector3.up) * fwd;
            var p = centre + d * r + Vector3.up * 0.05f;
            bottom.SetPosition(i, p);
            ink.SetPosition(i, p);
            top.SetPosition(i, p + Vector3.up * height);
            verts.Add(p);
            verts.Add(p + Vector3.up * height);
            var cb = col; cb.a = 0.5f * a;
            var ct = col; ct.a = 0.04f * a;
            cols.Add(cb);
            cols.Add(ct);
            if (i > 0)
            {
                var v = i * 2;
                tris.Add(v - 2); tris.Add(v - 1); tris.Add(v + 1);
                tris.Add(v - 2); tris.Add(v + 1); tris.Add(v);
            }
        }
        fin.Clear();
        fin.SetVertices(verts);
        fin.SetColors(cols);
        fin.SetTriangles(tris, 0);
        fin.RecalculateBounds();
        var c = Color.Lerp(col, Color.white, 0.2f); c.a = a;
        bottom.startColor = bottom.endColor = c;
        bottom.startWidth = bottom.endWidth = 0.16f;
        var crest = col; crest.a = a * 0.55f;
        top.startColor = top.endColor = crest;
        top.startWidth = top.endWidth = 0.05f;
        var ik = WardenFx.Ink; ik.a = a * 0.55f;
        ink.startColor = ink.endColor = ik;
        ink.startWidth = ink.endWidth = 0.36f;

        if (hit || damage <= 0f || !WardenHazard.Alive) return;
        var rel = WardenHazard.Feet - centre;
        var h = rel.y;
        rel.y = 0f;
        if (Mathf.Abs(rel.magnitude - r) > band || h >= height) return;
        if (arc < 359f && Vector3.Angle(fwd, rel) > arc * 0.5f) return;
        if (safeOnWall && WardenHazard.WallRunning) return;
        hit = true;
        WardenHazard.Damage(damage, centre);
    }

    private void OnDestroy()
    {
        if (fin != null) Destroy(fin);
    }
}

/// <summary>King's Spear's blade-wave: a thin vertical crimson edge racing down
/// the marked line. Side-step it; the strip on the floor said exactly where.</summary>
public sealed class BladeWave : MonoBehaviour
{
    private Vector3 origin, dir, side;
    private float length, speed, halfWidth, height, damage, s;
    private bool hit;
    private LineRenderer edge, ink, streak;

    public static BladeWave Spawn(Vector3 origin, Vector3 dir, float length, float speed, float halfWidth, float height, float damage)
    {
        var go = new GameObject("Blade wave");
        var w = go.AddComponent<BladeWave>();
        w.origin = origin;
        w.dir = Vector3.ProjectOnPlane(dir, Vector3.up).normalized;
        w.side = Vector3.Cross(Vector3.up, w.dir);
        w.length = length;
        w.speed = speed;
        w.halfWidth = halfWidth;
        w.height = height;
        w.damage = damage;
        w.ink = w.Line("Ink", 0, 3);
        w.edge = w.Line("Edge", 2, 3);
        w.streak = w.Line("Streak", 1, 2);
        WardenHazard.Track(go);
        return w;
    }

    private LineRenderer Line(string name, int order, int count)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = WardenFx.GlowMaterial;
        lr.useWorldSpace = true;
        lr.positionCount = count;
        lr.numCornerVertices = 0;
        lr.numCapVertices = 0;
        lr.sortingOrder = order;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return lr;
    }

    private void Update()
    {
        var prev = s;
        s += speed * Time.deltaTime;
        if (s >= length) { Destroy(gameObject); return; }
        var p = origin + dir * s + Vector3.up * 0.05f;
        // A crescent edge leaning forward — reads as a cut travelling, not a wall.
        var mid = p + Vector3.up * height * 0.5f + dir * 0.35f;
        var tip = p + Vector3.up * height;
        edge.SetPosition(0, p); edge.SetPosition(1, mid); edge.SetPosition(2, tip);
        ink.SetPosition(0, p); ink.SetPosition(1, mid); ink.SetPosition(2, tip);
        streak.SetPosition(0, origin + dir * Mathf.Max(0f, s - 3.5f) + Vector3.up * 0.04f);
        streak.SetPosition(1, p);
        var a = WardenFx.Stepped(1f - s / length * 0.5f);
        var c = Color.Lerp(WardenFx.Crimson, Color.white, 0.25f); c.a = a;
        edge.startColor = edge.endColor = c;
        edge.startWidth = 0.2f; edge.endWidth = 0.05f;
        var k = WardenFx.Ink; k.a = a * 0.55f;
        ink.startColor = ink.endColor = k;
        ink.startWidth = 0.42f; ink.endWidth = 0.12f;
        var sc = WardenFx.Crimson; sc.a = a * 0.7f;
        streak.startColor = new Color(sc.r, sc.g, sc.b, 0f);
        streak.endColor = sc;
        streak.startWidth = streak.endWidth = 0.08f;

        if (hit) return;
        var rel = WardenHazard.Feet - origin;
        var along = Vector3.Dot(rel, dir);
        if (along >= prev - 0.4f && along <= s + 0.3f && Mathf.Abs(Vector3.Dot(rel, side)) <= halfWidth && rel.y < height)
        {
            hit = true;
            WardenHazard.Damage(damage, p);
        }
    }
}

/// <summary>
/// The arena turns to Core: red cracks spread across every floor surface for
/// <c>warn</c> seconds while the Corestone walls flare purple, then the floor
/// erupts for <c>burn</c> seconds — anyone with their feet on a floor takes
/// ticks; wall-runners are safe. While it burns, <see cref="WallChase"/> sends
/// blades stabbing into the wall just behind a runner.
/// </summary>
public sealed class FloodField : MonoBehaviour
{
    private CoreSanctum sanctum;
    private Vector3 origin;
    private float warn, burn, tickDamage, t, nextCrack, nextSpike, nextTick;
    private bool erupting, dischargeWalls;
    private AudioSource rumble;

    public bool Erupting => erupting;
    public float Remaining => Mathf.Max(0f, warn + burn - t);

    /// <param name="dischargeWalls">False when the caller keeps the purple walls lit
    /// afterwards (the finale's approach).</param>
    public static FloodField Spawn(CoreSanctum sanctum, Vector3 origin, float warn, float burn, float tickDamage,
                                   bool dischargeWalls = true)
    {
        var go = new GameObject("Crimson flood");
        var f = go.AddComponent<FloodField>();
        f.sanctum = sanctum;
        f.origin = origin;
        f.warn = warn;
        f.burn = burn;
        f.tickDamage = tickDamage;
        f.dischargeWalls = dischargeWalls;
        f.rumble = WardenAudio.Loop("scrape", origin, 0.35f, 0.45f);
        if (sanctum != null) sanctum.ChargeWalls(1f);
        WardenHazard.Track(go);
        return f;
    }

    private void Update()
    {
        var dt = Time.deltaTime;
        t += dt;
        if (!erupting)
        {
            var k = Mathf.Clamp01(t / warn);
            if (sanctum != null) sanctum.SetFlood(k * 0.75f);
            if (t >= nextCrack)
            {
                // Liquid cracks crawl outward from the sword, reaching further each beat.
                nextCrack = t + 0.22f;
                var reach = Mathf.Lerp(2f, sanctum != null ? sanctum.OuterRadius : 14f, k);
                WardenFx.Cracks(origin, 3, reach, WardenFx.Crimson, 0.2f, warn - t + burn * 0.5f + 0.4f);
                if (sanctum != null && k > 0.35f)
                    WardenFx.Cracks(sanctum.RandomFloorPoint(), 2, 2.5f, WardenFx.Crimson, 0.25f, warn - t + 0.6f);
            }
            if (t >= warn)
            {
                erupting = true;
                nextTick = t;
                if (sanctum != null) sanctum.SetFlood(1f);
                WardenAudio.Play("boom", origin, 1f, 0.85f);
                WardenFx.Shake(0.35f);
            }
            return;
        }
        if (t >= nextSpike)
        {
            nextSpike = t + 0.07f;
            var p = sanctum != null ? sanctum.RandomFloorPoint() : origin + Random.insideUnitSphere * 8f;
            EruptionSpike.Spawn(p, Random.Range(1.2f, 2.4f), Random.Range(0.3f, 0.5f), 0.45f);
            if (Random.value < 0.25f) WardenFx.Dust(p, 1, 0.6f, 0.6f, WardenFx.CrimsonDeep, 1.2f);
        }
        if (t >= nextTick)
        {
            nextTick = t + 0.4f;
            if (WardenHazard.Grounded && !WardenHazard.WallRunning
                && (sanctum == null || sanctum.Contains(WardenHazard.Feet)))
                WardenHazard.Damage(tickDamage, WardenHazard.Feet + Vector3.down);
        }
        if (t >= warn + burn) Destroy(gameObject);
    }

    private void OnDestroy()
    {
        WardenAudio.StopLoop(rumble);
        if (sanctum == null) return;
        sanctum.SetFlood(0f);
        if (dischargeWalls) sanctum.ChargeWalls(0f);
    }
}

/// <summary>
/// While the floor burns, spectral blades chase a wall-runner — BANG, BANG,
/// BANG — each preceded by a red circle ON the wall a little behind them.
/// Keep moving forward and they always land behind you.
/// </summary>
public sealed class WallChase : MonoBehaviour
{
    private Transform owner;
    private float until, next;
    private const float Interval = 0.3f, Warn = 0.42f;

    public static WallChase Run(Transform owner, float seconds)
    {
        var go = new GameObject("Wall chase");
        var w = go.AddComponent<WallChase>();
        w.owner = owner;
        w.until = Time.time + seconds;
        WardenHazard.Track(go);
        return w;
    }

    private void Update()
    {
        if (Time.time >= until) { Destroy(gameObject); return; }
        var wall = WardenHazard.Wall;
        if (wall == null || !wall.AttachedToWall || Time.time < next) return;
        next = Time.time + Interval;
        var n = wall.WallSurfaceNormal;
        var p = WardenHazard.Player;
        var run = Vector3.ProjectOnPlane(p.forward, n).normalized;
        var target = p.position + Vector3.up * Random.Range(0.6f, 1.5f) - run * 0.6f;
        target -= n * Vector3.Dot(target - wall.WallSurfacePoint, n);
        StartCoroutine(Stab(target, n));
    }

    private System.Collections.IEnumerator Stab(Vector3 point, Vector3 normal)
    {
        var mark = WardenMark.Circle(point, 0.7f, WardenFx.Crimson, 0.07f, 0.2f, 16, normal);
        var from = point + normal * 4f + Vector3.up * 2.5f;
        var blade = SpectralBlade.Spawn(ArsenalKind.Sword, -1, WardenArsenal.NaturalLength(ArsenalKind.Sword, 0.9f), from,
                                        Quaternion.FromToRotation(Vector3.up, (point - from).normalized), owner, 0.18f, chime: false);
        var t = 0f;
        while (t < Warn)
        {
            t += Time.deltaTime;
            mark.SetPulse(t / Warn);
            yield return null;
        }
        mark.Release(0.1f);
        if (blade != null)
        {
            blade.transform.rotation = Quaternion.FromToRotation(Vector3.up, (point - from).normalized);
            blade.StickAt(point, 0.4f);
        }
        WardenFx.Peak(point + normal * 0.2f, 0.4f);
        WardenFx.Ring(point + normal * 0.03f, normal, 0.15f, 0.9f, 0.25f, WardenFx.Crimson, 0.07f, 12);
        WardenFx.Debris(point, 3, 3.5f, 0.6f, normal * 0.6f);
        WardenAudio.Play("metal", point, 0.6f, Random.Range(0.95f, 1.1f));
        if (WardenHazard.PointToBody(point) < 0.9f) WardenHazard.Damage(16f, point);
    }
}

/// <summary>
/// Reaper's Wheel: a spectral axe (or scythe) stood on its edge, spinning like a
/// wheel, rolls down a lane. The lane strip shows first; the wheel revs in place
/// (sparks spit off the floor), then launches. Jump it (it stands about a body
/// high) or step out of the lane. It cuts a groove as it goes.
/// </summary>
public sealed class AxeWheel : MonoBehaviour
{
    private Vector3 origin, dir, side;
    private float lane, warn, speed, damage, t, s, nextSpark, nextGroove;
    private bool rolling, hit;
    private SpectralBlade wheel;
    private WardenMark strip;
    private Vector3 lastGroove;

    public static AxeWheel Spawn(Vector3 floorStart, Vector3 dir, float laneLength, float warn, float speed, float damage,
                                 Transform owner, ArsenalKind kind = ArsenalKind.Axe, float size = 1f)
    {
        var go = new GameObject("Reaper wheel");
        var w = go.AddComponent<AxeWheel>();
        w.origin = floorStart;
        w.dir = Vector3.ProjectOnPlane(dir, Vector3.up).normalized;
        w.side = Vector3.Cross(Vector3.up, w.dir);
        w.lane = laneLength;
        w.warn = warn;
        w.speed = speed;
        w.damage = damage;
        var len = WardenArsenal.NaturalLength(kind, size) * 0.95f;
        w.wheel = SpectralBlade.Spawn(kind, -1, len, floorStart, Quaternion.LookRotation(w.dir, Vector3.up), owner, 0.25f, true, 0.8f);
        w.wheel.SetSpin(Vector3.right, 260f);
        w.strip = WardenMark.Line(floorStart, floorStart + w.dir * laneLength, 0.75f * size, WardenFx.Crimson, 0.05f, 0.12f);
        w.lastGroove = floorStart;
        WardenHazard.Track(go);
        return w;
    }

    public float Height => wheel != null ? wheel.Length : 1.5f;

    private void Update()
    {
        var dt = Time.deltaTime;
        t += dt;
        if (!rolling)
        {
            var k = Mathf.Clamp01(t / warn);
            strip.SetPulse(k);
            if (wheel != null)
            {
                wheel.SetGlow(k);
                // Revving: the spin climbs as the launch nears.
                wheel.SetSpin(Vector3.right, Mathf.Lerp(260f, 1100f, k * k));
            }
            if (t >= nextSpark)
            {
                nextSpark = t + 0.07f;
                WardenFx.Sparks(origin + Vector3.up * 0.05f, Vector3.up - dir, 3, 3.5f);
            }
            if (t >= warn)
            {
                rolling = true;
                WardenAudio.Play("swish", origin, 0.7f, 0.65f);
                WardenFx.Dust(origin, 3, 1.2f, 0.7f);
            }
            return;
        }
        var prev = s;
        s += speed * dt;
        var p = origin + dir * Mathf.Min(s, lane);
        if (wheel != null) wheel.transform.position = p;
        if (t >= nextSpark)
        {
            nextSpark = t + 0.05f;
            WardenFx.Sparks(p + Vector3.up * 0.04f, Vector3.up - dir * 1.2f, 2, 4f);
        }
        if (s >= nextGroove)
        {
            nextGroove = s + 1.2f;
            WardenFx.Groove(new[] { lastGroove + Vector3.up * 0.03f, p + Vector3.up * 0.03f }, 0.1f, 1.1f);
            lastGroove = p;
        }
        if (!hit && WardenHazard.Alive)
        {
            var rel = WardenHazard.Feet - origin;
            var along = Vector3.Dot(rel, dir);
            var lateral = Mathf.Abs(Vector3.Dot(rel, side));
            if (along >= prev - 0.7f && along <= s + 0.7f && lateral <= 0.75f && rel.y < Height * 0.8f && !WardenHazard.WallRunning)
            {
                hit = true;
                if (WardenHazard.Damage(damage, p)) WardenFx.Spikes(WardenHazard.Chest, 7, 0.8f, WardenFx.Crimson, 0.18f, 0.07f);
            }
        }
        if (s >= lane)
        {
            strip.Release(0.15f);
            if (wheel != null) wheel.Dissolve(0.25f);
            Destroy(gameObject);
        }
    }

    private void OnDestroy()
    {
        if (strip != null) strip.Release(0.1f);
        if (wheel != null && wheel.State != SpectralBlade.Phase.Dissolving) wheel.Dissolve(0.2f);
    }
}

/// <summary>
/// Crimson Guillotine's colossal blade: a giant spectral axe hangs high over a
/// marked strip, edge down, swelling — then drops in a blink and buries itself.
/// A long crack and a groove carve the strip; the blade stays wedged for
/// <c>stuck</c> seconds before it dissolves (the punish window is the Warden's,
/// who is wedged with it).
/// </summary>
public sealed class GuillotineDrop : MonoBehaviour
{
    private Vector3 origin, dir, side;
    private float extent, warn, stuck, damage, t, fall = 0.16f, halfWidth;
    private bool dropped, landed;
    private SpectralBlade blade;
    private WardenMark strip;
    private Vector3 hang, rest;

    public bool Landed => landed;

    public static GuillotineDrop Spawn(Vector3 floorStart, Vector3 dir, float extent, float warn, float stuck, float damage,
                                       Transform owner, ArsenalKind kind = ArsenalKind.Axe)
    {
        var go = new GameObject("Crimson guillotine");
        var g = go.AddComponent<GuillotineDrop>();
        g.origin = floorStart;
        g.dir = Vector3.ProjectOnPlane(dir, Vector3.up).normalized;
        g.side = Vector3.Cross(Vector3.up, g.dir);
        g.extent = extent;
        g.warn = warn;
        g.stuck = stuck;
        g.damage = damage;
        g.halfWidth = 1.05f;
        var len = 6.5f;
        var mid = floorStart + g.dir * extent * 0.55f;
        // Edge down, flat face across the strip: local +Y (tip/head) = world down, local X (the blade's width) along the strip.
        var rot = Quaternion.LookRotation(g.side, Vector3.down);
        g.hang = mid + Vector3.up * (len + 6f);
        g.rest = mid + Vector3.up * (len * 0.72f);
        g.blade = SpectralBlade.Spawn(kind, -1, len, g.hang, rot, owner, 0.5f, true, 0.55f);
        g.strip = WardenMark.Line(floorStart, floorStart + g.dir * extent, g.halfWidth, WardenFx.Crimson, 0.08f, 0.16f);
        WardenHazard.Track(go);
        return g;
    }

    private void Update()
    {
        var dt = Time.deltaTime;
        t += dt;
        if (!dropped)
        {
            var k = Mathf.Clamp01(t / warn);
            strip.SetPulse(k);
            if (blade != null)
            {
                blade.SetGlow(k);
                // A slow lift as it gathers — the drop has somewhere to fall from.
                blade.transform.position = hang + Vector3.up * (0.8f * k * k) + Random.insideUnitSphere * 0.03f * k;
            }
            if (t >= warn) { dropped = true; t = 0f; }
            return;
        }
        if (!landed)
        {
            var f = Mathf.Clamp01(t / fall);
            if (blade != null) blade.transform.position = Vector3.Lerp(hang + Vector3.up * 0.8f, rest, f * f);
            if (f >= 1f) Land();
            return;
        }
        if (t >= stuck)
        {
            if (blade != null) blade.Dissolve(0.4f);
            Destroy(gameObject);
        }
    }

    private void Land()
    {
        landed = true;
        t = 0f;
        strip.Release(0.15f);
        var mid = origin + dir * extent * 0.55f;
        var rel = WardenHazard.Feet - origin;
        var along = Vector3.Dot(rel, dir);
        if (along >= -0.5f && along <= extent + 0.5f && Mathf.Abs(Vector3.Dot(rel, side)) <= halfWidth && rel.y < 4f)
            WardenHazard.Damage(damage, mid);
        WardenFx.Impact(mid, 2.6f, 0.6f, 0.09f, 14, 1.4f, 0f);
        var groove = new List<Vector3>();
        for (var d = 0f; d <= extent; d += 0.8f)
            groove.Add(origin + dir * d + side * Random.Range(-0.12f, 0.12f) + Vector3.up * 0.04f);
        WardenFx.Groove(groove, 0.34f, 2.2f);
        WardenFx.Cracks(mid, 6, extent * 0.4f, WardenFx.Crimson, 0.12f, 1.6f, dir, 40f);
        for (var d = 0.5f; d <= extent; d += 1.6f)
            WardenFx.Sparks(origin + dir * d + Vector3.up * 0.05f, Vector3.up, 4, 5f, side * (Random.value < 0.5f ? 1f : -1f));
        WardenAudio.Play("boom", mid, 1f, 0.75f);
        WardenAudio.Play("metal", mid, 1f, 0.6f);
        WardenAudio.Play("sub", mid, 1f, 1f);
        CoreSanctum.Active?.StrikeLine(origin, dir, extent, halfWidth + 0.6f, 1.2f);
        if (blade != null) blade.Hold();
    }

    private void OnDestroy()
    {
        if (strip != null) strip.Release(0.1f);
    }
}
