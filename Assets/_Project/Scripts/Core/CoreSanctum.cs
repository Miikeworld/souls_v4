using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Warden's arena — a circular sanctum built by Tools > Project Restart >
/// Setup Warden Fight. It changes with the boss:
///   Phase 2 — <see cref="Instability"/>: loose stones and dust start to rise,
///             the Corestone veins brighten (foreshadowing, nothing lethal).
///   Phase 3 — <see cref="Shatter"/>: outer floor sections collapse, break or
///             lift; four wall slabs tear off the ring wall and hang over the
///             void as wall-run runways; pillars and debris float.
///   Finale  — <see cref="Ascend"/> lifts everything with the boss,
///             <see cref="Freeze"/> holds it, <see cref="Crash"/> drops it.
///   Always  — the room takes the Warden's misses: <see cref="StrikeAt"/> /
///             <see cref="StrikeLine"/> / <see cref="StrikeRadius"/> chip and
///             crack the pillars; enough weight topples one, which crashes
///             down and crumbles (a missed greatsword is never weightless).
/// Purple Corestone (<see cref="ChargeWalls"/>) = the suit can use this — the
/// runways light up in the player's wall-run sigils; <see cref="SetFlood"/> lays a
/// LOW stepped crimson wash on every floor surface (one band per flood beat) and
/// stamps crimson Core sigils across it while it turns. All FX speak the player's
/// language (WardenFx: faceted ink-backed rings, stepped fades, shard chips — no
/// puffs, no sine flicker, tints capped at ×1.6). Everything returns home on
/// <see cref="ResetArena"/> (player death / rest).
/// Colliders move only on the piece root; idle bobbing is visual-only, so a
/// runner on a slab never fights a moving collider.
/// </summary>
public sealed class CoreSanctum : MonoBehaviour
{
    public enum Role { Static, Collapse, Break, Float, WallSlab, Pillar, Debris }

    [System.Serializable]
    public sealed class Piece
    {
        public Transform root;
        [Tooltip("Visual child that bobs while floating (null = no bob).")]
        public Transform visual;
        public Role role;
        [Tooltip("World pose after the Phase 3 tear.")]
        public Vector3 shatterPos;
        public Vector3 shatterEuler;
        [Tooltip("Extra lift while the arena ascends in the finale.")]
        public Vector3 ascendOffset;
        public float delay;
        [Tooltip("Corestone strips on this piece (wall slabs: the purple run marks).")]
        public Renderer[] veins;
        [Tooltip("Top surface counts as floor (flood overlay, eruption spikes).")]
        public bool walkable;
    }

    [SerializeField] private Piece[] pieces = System.Array.Empty<Piece>();
    [SerializeField] private Renderer[] floorVeins = System.Array.Empty<Renderer>();
    [SerializeField] private Transform centralFloor;
    [SerializeField, Min(2f)] private float platformRadius = 10f;
    [SerializeField, Min(2f)] private float outerRadius = 15.5f;
    [SerializeField] private Color veinRest = new Color(0.9f, 0.05f, 0.08f, 1f);
    [SerializeField] private Color wallRest = new Color(0.32f, 0.12f, 0.55f, 1f);
    [SerializeField] private Color wallCharged = new Color(0.92f, 0.43f, 1.6f, 1f);

    private sealed class Run
    {
        public Vector3 homePos, homeLocalVisual;
        public Quaternion homeRot;
        public bool homeActive;
        public Vector3 fromPos, toPos;
        public Quaternion fromRot, toRot;
        public float t0, dur;
        public int motion;          // 0 home, 1 tearing, 2 hanging (shattered pose), 3 falling, 4 gone, 5 landed, 6 toppling
        public float hp, height;
        public float vy, bob;
        public Vector3 spin;
        public float liftSeed;
        public bool burst;          // the tear / fall chips already thrown for this motion
        public Collider[] colliders;
        public bool[] collidersOn;
        public readonly List<MeshRenderer> overlays = new List<MeshRenderer>();
    }

    private const int Home = 0, Tearing = 1, Hanging = 2, Falling = 3, Gone = 4, Landed = 5, Toppling = 6;
    private const float PillarHp = 3f;
    private const float MaxTint = 1.6f;          // HDR cap: near-flat colour, never a bloom blob
    private const float FloodWash = 0.22f;       // top band of the floor wash, × WardenFx.Opacity
    private const int RunwayStamps = 5;
    // The player's dust chip (TraversalEffects Dust .4/.36/.34 at alpha .55).
    private static readonly Color GritCol = new Color(0.4f, 0.36f, 0.34f, 0.55f);
    private readonly Dictionary<Transform, int> pieceOf = new Dictionary<Transform, int>();

    private Run[] runs;
    private MaterialPropertyBlock mpb, floodBlock;
    private readonly List<MeshRenderer> centralOverlays = new List<MeshRenderer>();
    private readonly List<WallRunSurface> runWalls = new List<WallRunSurface>();
    private float instability, charge, chargeTarget, flood, ascend, ascendTarget, heartbeat, nextMote;
    private float nextFloodStamp, nextRunStamp, floodAlpha = -1f;
    private int floodBand, runStampIdx;
    private bool frozen, floodOn;

    public static CoreSanctum Active { get; private set; }
    public Vector3 Center => transform.position;
    public float PlatformRadius => platformRadius;
    public float OuterRadius => outerRadius;
    public bool Shattered { get; private set; }
    public IReadOnlyList<WallRunSurface> RunWalls => runWalls;

    private void OnEnable() => Active = this;
    private void OnDisable() { if (Active == this) Active = null; }

    private void Awake()
    {
        mpb = new MaterialPropertyBlock();
        runs = new Run[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            var r = new Run { liftSeed = Random.value * 10f };
            if (p?.root != null)
            {
                r.homePos = p.root.position;
                r.homeRot = p.root.rotation;
                r.homeActive = p.root.gameObject.activeSelf;
                if (p.visual != null) r.homeLocalVisual = p.visual.localPosition;
                r.colliders = p.root.GetComponentsInChildren<Collider>(true);
                r.collidersOn = new bool[r.colliders.Length];
                for (var c = 0; c < r.colliders.Length; c++) r.collidersOn[c] = r.colliders[c].enabled;
                if (p.walkable) AddOverlays(p.root, r.overlays);
                pieceOf[p.root] = i;
                if (p.role == Role.Pillar)
                {
                    r.hp = PillarHp;
                    var cap = p.root.GetComponent<CapsuleCollider>();
                    r.height = cap != null ? cap.height : 6f;
                }
                var surf = p.role == Role.WallSlab ? p.root.GetComponentInChildren<WallRunSurface>(true) : null;
                if (surf != null) runWalls.Add(surf);
            }
            runs[i] = r;
        }
        if (centralFloor != null) AddOverlays(centralFloor, centralOverlays);
        ApplyVeins();
    }

    /// <summary>A copy of each floor "Surface" mesh, a hair above it — the flood wash
    /// (a low stepped crimson layer; the sigils and cracks carry the read).</summary>
    private static void AddOverlays(Transform root, List<MeshRenderer> into)
    {
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.name != "Surface" || mf.sharedMesh == null) continue;
            var go = new GameObject("Flood overlay");
            go.transform.SetParent(mf.transform, false);
            // A Surface lowered under kit tiles (Setup Warden Fight dressing) still floods ABOVE the tiles.
            go.transform.localPosition = Vector3.up * (0.025f - Mathf.Min(0f, mf.transform.localPosition.y));
            go.transform.localScale = new Vector3(1.002f, 1.01f, 1.002f);
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = WardenFx.GlowMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            into.Add(r);
        }
    }

    // ------------------------------------------------------------------ boss-facing API

    /// <summary>Phase 2 foreshadowing, 0..1 — stones drift up, dust rises, veins warm.</summary>
    public void Instability(float k) => instability = Mathf.Clamp01(k);

    /// <summary>The transition heartbeat: 1 = nearby rocks lift, 2 = floor slabs shake, 3 = the floor cracks open.</summary>
    public void Thump(int n, Vector3 origin)
    {
        heartbeat = 1f;
        // The Core's beat stamped into the floor: a faceted ring rolling out over ink,
        // an 8-gon kick under it and a Core sigil at the source.
        var g = origin + Vector3.up * 0.06f;
        WardenFx.Ring(g, Vector3.up, 0.6f, 3f + n * 3f, 0.5f, WardenFx.Crimson, 0.12f, WardenFx.RingSides, 0.15f);
        WardenFx.Pulse(g + Vector3.up * 0.01f, Vector3.up, 0.3f, 1.2f + 0.5f * n, 0.35f, WardenFx.Crimson, 1.1f, WardenFx.CoreSides);
        WardenFx.Stamp(g, Vector3.up, 0.6f + 0.25f * n, WardenFx.Crimson, 0.9f);
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (p?.root == null || runs[i].motion != Home) continue;
            if (n >= 1 && p.role == Role.Debris && Vector3.Distance(p.root.position, origin) < 6f + n * 4f)
                runs[i].bob = Mathf.Max(runs[i].bob, 0.25f * n);
            if (n >= 2 && (p.role == Role.Collapse || p.role == Role.Break || p.role == Role.Float))
                StartCoroutine(Jolt(p.root, 0.05f * n, 0.35f));
        }
        if (n >= 2) Grit(origin, 4 * n, 2.5f, 1.6f);
        if (n >= 3) WardenFx.Cracks(origin, 8, platformRadius * 0.8f, WardenFx.Crimson, 0.3f, 3f, width: 0.055f);
    }

    /// <summary>Phase 3: the floor tears apart. Pieces leave on their own delays.</summary>
    public void Shatter(Vector3 origin)
    {
        if (Shattered) return;
        Shattered = true;
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (p?.root == null || p.role == Role.Static) continue;
            var r = runs[i];
            if (r.motion == Gone || r.motion == Toppling) continue;   // already smashed by a miss
            // Pieces that only exist once the floor breaks (torn chunks) wake up here.
            if (!p.root.gameObject.activeSelf) p.root.gameObject.SetActive(true);
            r.fromPos = p.root.position;
            r.fromRot = p.root.rotation;
            r.t0 = Time.time + p.delay;
            r.burst = false;
            r.spin = new Vector3(Random.Range(-40f, 40f), Random.Range(-30f, 30f), Random.Range(-40f, 40f));
            // Floating pillars/debris turn slowly — never a rotating collider under a runner.
            if (p.role == Role.Pillar || p.role == Role.Debris || p.role == Role.Collapse)
                foreach (var c in r.colliders) c.enabled = false;
            if (p.role == Role.Collapse)
            {
                r.motion = Falling;
                r.vy = Random.Range(0.5f, 2.5f);
                continue;
            }
            r.toPos = p.shatterPos;
            r.toRot = Quaternion.Euler(p.shatterEuler);
            r.dur = p.role == Role.WallSlab ? 1.25f : p.role == Role.Break ? 0.45f : 1.0f;
            r.motion = Tearing;
        }
        // The tear: thin tapered Core cracks run to the rim; each piece throws its own
        // chips the moment it actually moves (TearFx), so the break ripples outward.
        WardenFx.Cracks(origin, 10, outerRadius, WardenFx.Crimson, 0.25f, 2.2f, width: 0.06f);
        Grit(origin, 18, 5f, 2f);
        WardenFx.Debris(origin + Vector3.up * 0.2f, 12, 5f, 1.4f);
        WardenAudio.Play("boom", origin, 1f, 0.7f);
        WardenAudio.Play("stone", origin, 1f, 0.6f);
    }

    /// <summary>Purple Corestone on the wall slabs: 0 = dim, 1 = flared (the
    /// suit's cue AND a real grip boost — <see cref="WallRunSurface.Charge"/>).</summary>
    public void ChargeWalls(float k)
    {
        k = Mathf.Clamp01(k);
        if (k > 0.5f && chargeTarget <= 0.5f)
        {
            WardenAudio.Play("whoom", Center + Vector3.up * 2f, 1f, 1f);
            if (isActiveAndEnabled)
                foreach (var w in runWalls)
                    if (w != null) StartCoroutine(LightRunway(w.transform));
        }
        chargeTarget = k;
    }

    /// <summary>A point on a wall slab's inner run face, between its two purple run marks
    /// and just proud of them (the dressing lifts the marks 9 cm clear of the kit modules);
    /// <paramref name="along"/> −1..1 spans the runway.</summary>
    private static Vector3 RunFace(Transform t, float along)
        => t.position + t.forward * 0.72f + t.up * 1.25f + t.right * along * 4.6f;

    /// <summary>The runway lights up in the player's own wall-run language: a purple
    /// 12-gon pulse off the face, then Core sigils stamped end to end along the run line
    /// (the marks a wall-runner would leave), rising purple chips.</summary>
    private IEnumerator LightRunway(Transform t)
    {
        var face = RunFace(t, 0f);
        WardenFx.Pulse(face, t.forward, 0.4f, 2.6f, 0.45f, WardenFx.PurpleBright, 1.2f);
        WardenFx.Shards(face, 6, 1.5f, WardenFx.Purple, rise: true, size: 1.1f);
        for (var s = 0; s < RunwayStamps; s++)
        {
            if (t == null) yield break;
            var bright = s % 2 == 0;
            WardenFx.Stamp(RunFace(t, Mathf.Lerp(-1f, 1f, s / (float)(RunwayStamps - 1))), t.forward,
                           bright ? 0.62f : 0.5f, bright ? WardenFx.PurpleBright : WardenFx.Purple);
            yield return new WaitForSeconds(0.06f);
        }
    }

    /// <summary>0..1 Crimson Flood / Arena Collapse on every floor surface: a low four-band
    /// wash that clicks up one band per beat (a crimson floor pulse each step); while it
    /// is still turning (0 &lt; k &lt; 1, the warn) crimson Core sigils pop across the floor.</summary>
    public void SetFlood(float k) => flood = Mathf.Clamp01(k);

    /// <summary>Finale lift, 0..1 of each piece's ascend offset.</summary>
    public void Ascend(float k) => ascendTarget = Mathf.Clamp01(k);

    /// <summary>Hold every floating thing still (the final strike's held breath).</summary>
    public void Freeze(bool on) => frozen = on;

    /// <summary>Gravity returns: everything hanging falls. Debris over the platform
    /// lands as rubble; the rest drops into the void.</summary>
    public void Crash()
    {
        frozen = false;
        ascendTarget = ascend = 0f;
        chargeTarget = 0f;
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            var r = runs[i];
            if (p?.root == null || p.role == Role.Static || p.role == Role.Break) continue;
            if (r.motion == Gone || r.motion == Landed || r.motion == Toppling) continue;
            r.motion = Falling;
            r.vy = 0f;
            r.burst = false;
            r.t0 = Time.time + Random.Range(0f, 0.35f);
            r.spin = new Vector3(Random.Range(-60f, 60f), Random.Range(-40f, 40f), Random.Range(-60f, 60f));
        }
        heartbeat = 1f;
    }

    /// <summary>Back to the intact sanctum — every piece home, every glow at rest.</summary>
    public void ResetArena()
    {
        StopAllCoroutines();
        Shattered = false;
        frozen = false;
        instability = charge = chargeTarget = flood = ascend = ascendTarget = heartbeat = 0f;
        nextFloodStamp = nextRunStamp = 0f;
        floodBand = 0;
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            var r = runs[i];
            if (p?.root == null) continue;
            p.root.gameObject.SetActive(r.homeActive);
            p.root.SetPositionAndRotation(r.homePos, r.homeRot);
            if (p.visual != null) p.visual.localPosition = r.homeLocalVisual;
            for (var c = 0; c < r.colliders.Length; c++)
                if (r.colliders[c] != null) r.colliders[c].enabled = r.collidersOn[c];
            r.motion = Home;
            r.bob = 0f;
            r.vy = 0f;
            r.burst = false;
            if (p.role == Role.Pillar) r.hp = PillarHp;
        }
        foreach (var w in runWalls) if (w != null) w.Charge = 0f;
        ApplyVeins();
        ApplyFlood();
    }

    /// <summary>A random point on a floor surface that still exists (central disc
    /// weighted, plus any surviving outer section), snapped to the real floor.</summary>
    public Vector3 RandomFloorPoint()
    {
        Vector3 guess;
        if (Random.value < 0.7f || !TryOuterPoint(out guess))
        {
            var d = Random.insideUnitCircle * (platformRadius - 0.4f);
            guess = Center + new Vector3(d.x, 0f, d.y);
        }
        return WardenHazard.FloorAt(guess + Vector3.up * 3f, 8f, out var floor) ? floor : guess;
    }

    private bool TryOuterPoint(out Vector3 p)
    {
        p = Center;
        for (var tries = 0; tries < 6; tries++)
        {
            var i = Random.Range(0, pieces.Length);
            var piece = pieces[i];
            if (piece?.root == null || !piece.walkable || runs[i].motion == Gone || runs[i].motion == Falling) continue;
            var col = piece.root.GetComponentInChildren<Collider>();
            if (col == null || !col.enabled) continue;
            var b = col.bounds;
            p = new Vector3(Random.Range(b.min.x, b.max.x), b.max.y, Random.Range(b.min.z, b.max.z));
            return true;
        }
        return false;
    }

    /// <summary>Inside the sanctum volume (flood ticks only count in here).</summary>
    public bool Contains(Vector3 p)
    {
        var d = p - Center;
        return d.y > -3f && d.y < 9f && new Vector2(d.x, d.z).magnitude <= outerRadius + 1.5f;
    }

    /// <summary>Clamp a planar position onto the surviving platform (bosses never walk off).</summary>
    public Vector3 ClampToPlatform(Vector3 p, float margin)
    {
        var d = p - Center;
        var flat = new Vector2(d.x, d.z);
        var max = (Shattered ? platformRadius : outerRadius) - margin;
        if (flat.magnitude <= max) return p;
        flat = flat.normalized * max;
        return new Vector3(Center.x + flat.x, p.y, Center.z + flat.y);
    }

    // ------------------------------------------------------------------ the room takes the hits

    /// <summary>A blade, weapon or wave struck <paramref name="c"/> at <paramref name="point"/>.
    /// Pillars chip and crack; past their weight they topple along <paramref name="dir"/>.
    /// True when a pillar took it.</summary>
    public bool StrikeAt(Collider c, Vector3 point, Vector3 dir, float force)
    {
        if (c == null || force <= 0f) return false;
        var i = PieceIndex(c.transform);
        if (i < 0 || pieces[i].role != Role.Pillar || runs[i].motion != Home) return false;
        Damage(i, point, dir, force);
        return true;
    }

    /// <summary>Everything along a strip (Worldsplitter's crack, the guillotine, a wave fin).</summary>
    public void StrikeLine(Vector3 origin, Vector3 dir, float length, float halfWidth, float force)
    {
        dir = Vector3.ProjectOnPlane(dir, Vector3.up);
        if (dir.sqrMagnitude < 1e-4f || force <= 0f) return;
        dir.Normalize();
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (p?.root == null || p.role != Role.Pillar || runs[i].motion != Home) continue;
            var rel = p.root.position - origin;
            var along = Vector3.Dot(rel, dir);
            if (along < -1f || along > length + 1f) continue;
            var lateral = Vector3.ProjectOnPlane(rel - dir * along, Vector3.up).magnitude;
            if (lateral > halfWidth + 0.8f) continue;
            var hitPoint = p.root.position + Vector3.up * 1.2f - dir * 0.7f;
            Damage(i, hitPoint, dir, force);
        }
    }

    /// <summary>Everything within <paramref name="radius"/> of <paramref name="centre"/> (slams, landings, rings).</summary>
    public void StrikeRadius(Vector3 centre, float radius, float force)
    {
        if (force <= 0f) return;
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (p?.root == null || p.role != Role.Pillar || runs[i].motion != Home) continue;
            var rel = Vector3.ProjectOnPlane(p.root.position - centre, Vector3.up);
            if (rel.magnitude > radius + 0.8f) continue;
            var outward = rel.sqrMagnitude > 0.01f ? rel.normalized : Vector3.forward;
            Damage(i, p.root.position + Vector3.up * 1f - outward * 0.7f, outward, force * Mathf.Lerp(1f, 0.5f, rel.magnitude / Mathf.Max(1f, radius)));
        }
    }

    /// <summary>Phase 2's first sign: every standing pillar cracks (a small strike each).</summary>
    public void CrackPillars()
    {
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (p?.root == null || p.role != Role.Pillar || runs[i].motion != Home) continue;
            var toCentre = Vector3.ProjectOnPlane(Center - p.root.position, Vector3.up).normalized;
            Damage(i, p.root.position + Vector3.up * Random.Range(1.5f, 4f) + toCentre * 0.72f, -toCentre, 0.4f);
        }
    }

    private int PieceIndex(Transform t)
    {
        for (var x = t; x != null; x = x.parent)
            if (pieceOf.TryGetValue(x, out var i)) return i;
        return -1;
    }

    private void Damage(int i, Vector3 point, Vector3 dir, float force)
    {
        var p = pieces[i];
        var r = runs[i];
        r.hp -= force;
        var outward = Vector3.ProjectOnPlane(point - p.root.position, Vector3.up);
        var n = outward.sqrMagnitude > 1e-4f ? outward.normalized : -dir;
        WardenFx.Debris(point, Mathf.RoundToInt(3 + 4 * force), 4.5f, 0.8f + 0.3f * force, n * 0.8f);
        Grit(point, 2 + Mathf.RoundToInt(2 * force), 0.9f, 1.4f);
        WardenFx.Sparks(point, n, 4, 3.5f);
        // Cracks climbing the face from the hit (vertical-ish, on the pillar surface).
        var crack = new List<Vector3> { point + n * 0.03f };
        var c = point + n * 0.03f;
        for (var k = 0; k < 5; k++)
        {
            c += Vector3.up * Random.Range(-0.45f, 0.55f) + Vector3.Cross(Vector3.up, n) * Random.Range(-0.25f, 0.25f);
            var flat = Vector3.ProjectOnPlane(c - p.root.position, Vector3.up).normalized;
            c = new Vector3(p.root.position.x, c.y, p.root.position.z) + flat * 0.74f;
            crack.Add(c);
        }
        WardenFx.Line(crack, WardenFx.Crimson, 0.045f, 1.2f, 0.1f, 0.5f);
        WardenAudio.Play("stone", point, Mathf.Clamp01(0.4f + 0.3f * force), Random.Range(0.8f, 1.05f));
        if (r.hp <= 0f) StartCoroutine(Topple(i, dir));
        else StartCoroutine(Jolt(p.visual != null ? p.visual : p.root, 0.04f * force, 0.25f));
    }

    /// <summary>The pillar goes: a creak, a fall that accelerates about its base,
    /// a crash along its whole length — then it crumbles to nothing.</summary>
    private IEnumerator Topple(int i, Vector3 push)
    {
        var p = pieces[i];
        var r = runs[i];
        r.motion = Toppling;
        foreach (var c in r.colliders) if (c != null) c.enabled = false;
        var flat = Vector3.ProjectOnPlane(push, Vector3.up);
        if (flat.sqrMagnitude < 1e-4f) flat = Vector3.ProjectOnPlane(p.root.position - Center, Vector3.up);
        flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
        var axis = Vector3.Cross(Vector3.up, flat);
        var from = p.root.rotation;
        WardenAudio.Play("crack", p.root.position + Vector3.up * 2f, 0.9f, 0.6f);
        Grit(p.root.position, 6, 1.6f, 1.8f);
        WardenFx.Debris(p.root.position + Vector3.up * 0.3f, 4, 2.5f, 1f, flat * 0.6f);
        const float T = 0.9f;
        var t = 0f;
        while (t < T)
        {
            t += Time.deltaTime;
            var k = Mathf.Clamp01(t / T);
            p.root.rotation = Quaternion.AngleAxis(86f * k * k * k, axis) * from;
            yield return null;
        }
        var basePos = p.root.position;
        var along = flat;
        for (var d = 0.8f; d < r.height; d += 1.3f)
        {
            var at = basePos + along * d;
            WardenFx.Debris(at, 5, 5f, 1.4f);
            Grit(at, 3, 1.4f, 2f);
        }
        // The crash is the room's weight, not a hit zone: no crimson here — a white snap
        // and a dust-coloured 12-gon rolling out over ink, chips the length of the fall.
        var mid = basePos + along * r.height * 0.6f;
        var floorMid = mid + Vector3.up * 0.05f;
        WardenFx.Pulse(floorMid, Vector3.up, 0.2f, 1.4f, 0.24f, Color.white, 1.1f);
        WardenFx.Pulse(floorMid + Vector3.up * 0.01f, Vector3.up, 0.4f, 2.8f, 0.45f, WardenFx.DustCol, 1.4f);
        WardenFx.Debris(mid + Vector3.up * 0.1f, 14, 5.5f, 1.4f);
        Grit(mid, 10, 2.5f, 2.2f);
        WardenFx.Shake(0.3f);
        WardenAudio.Play("boom", mid, 0.8f, 1.1f);
        WardenAudio.Play("stone", mid, 1f, 0.55f);
        heartbeat = Mathf.Max(heartbeat, 0.5f);
        p.root.gameObject.SetActive(false);
        r.motion = Gone;
    }

    /// <summary>The Core's heartbeat on the floor veins (P3 THUMP, finale pulse).</summary>
    public void Pulse(float k = 1f) => heartbeat = Mathf.Max(heartbeat, k);

    // ------------------------------------------------------------------ tick

    private void Update()
    {
        var dt = Time.deltaTime;
        var time = Time.time;
        charge = Mathf.MoveTowards(charge, chargeTarget, dt * (chargeTarget > charge ? 4f : 1.5f));
        ascend = Mathf.MoveTowards(ascend, ascendTarget, dt * 0.35f);
        heartbeat = Mathf.MoveTowards(heartbeat, 0f, dt * 2.2f);
        foreach (var w in runWalls) if (w != null) w.Charge = charge;

        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            var r = runs[i];
            if (p?.root == null) continue;
            switch (r.motion)
            {
                case Home:
                    if (p.role == Role.Debris && (instability > 0f || r.bob > 0f))
                    {
                        // Gravity is starting to fail: stones drift up and turn, slowly.
                        var lift = (instability * 1.1f + r.bob) * (0.6f + 0.4f * Mathf.Sin(time * 0.7f + r.liftSeed));
                        p.root.position = r.homePos + Vector3.up * lift;
                        p.root.rotation = r.homeRot * Quaternion.Euler(0f, time * 12f * instability + r.liftSeed * 30f * instability, 0f);
                        r.bob = Mathf.MoveTowards(r.bob, 0f, dt * 0.15f);
                    }
                    break;
                case Tearing:
                {
                    var k = (time - r.t0) / Mathf.Max(0.01f, r.dur);
                    if (k < 0f)
                    {
                        // Pre-tear tremble in place.
                        if (p.visual != null) p.visual.localPosition = r.homeLocalVisual + Random.insideUnitSphere * 0.04f;
                        break;
                    }
                    if (!r.burst) { r.burst = true; TearFx(p); }
                    k = Mathf.Clamp01(k);
                    var e = 1f - (1f - k) * (1f - k) * (1f - k);
                    var arc = Vector3.up * Mathf.Sin(k * Mathf.PI) * (p.role == Role.WallSlab ? 2.5f : 1f);
                    p.root.SetPositionAndRotation(Vector3.Lerp(r.fromPos, r.toPos, e) + arc, Quaternion.Slerp(r.fromRot, r.toRot, e));
                    if (k >= 1f)
                    {
                        r.motion = Hanging;
                        if (p.visual != null) p.visual.localPosition = r.homeLocalVisual;
                        if (p.role == Role.WallSlab || p.role == Role.Break)
                        {
                            Grit(p.root.position, 5, 1.6f, 2f);
                            WardenFx.Debris(p.root.position, 4, 3f, 1f);
                            WardenAudio.Play("stone", p.root.position, 0.5f, Random.Range(0.6f, 0.8f));
                        }
                    }
                    break;
                }
                case Hanging:
                {
                    var lift = p.ascendOffset * ascend;
                    var target = r.toPos + lift;
                    if ((p.root.position - target).sqrMagnitude > 1e-6f) p.root.position = target;
                    if (p.visual != null && !frozen && p.role != Role.Break)
                        p.visual.localPosition = r.homeLocalVisual + Vector3.up * Mathf.Sin(time * 0.9f + r.liftSeed) * 0.05f;
                    if ((p.role == Role.Debris || p.role == Role.Pillar) && !frozen)
                        p.root.rotation = r.toRot * Quaternion.Euler(Mathf.Sin(time * 0.4f + r.liftSeed) * 4f, time * 6f + r.liftSeed * 20f, 0f);
                    break;
                }
                case Falling:
                {
                    if (time < r.t0 || frozen) break;
                    if (!r.burst) { r.burst = true; TearFx(p); }
                    r.vy -= 22f * dt;
                    var pos = p.root.position + Vector3.up * r.vy * dt;
                    p.root.rotation *= Quaternion.Euler(r.spin * dt);
                    var flat = new Vector2(pos.x - Center.x, pos.z - Center.z).magnitude;
                    if (p.role == Role.Debris && flat < platformRadius - 0.5f && pos.y <= Center.y + 0.3f)
                    {
                        // Lands on the platform as rubble.
                        pos.y = Center.y + 0.3f;
                        r.motion = Landed;
                        Grit(pos, 4, 1.4f, 1.8f);
                        WardenFx.Debris(pos, 4, 3f, 0.8f);
                        WardenAudio.Play("stone", pos, 0.45f, Random.Range(0.8f, 1.1f));
                    }
                    p.root.position = pos;
                    if (pos.y < Center.y - 30f)
                    {
                        r.motion = Gone;
                        p.root.gameObject.SetActive(false);
                    }
                    break;
                }
            }
        }

        // Phase 2 chips drifting UP — the first sign gravity is failing. Shard chips on the
        // rising system (dust-coloured, more of them crimson as it builds), never puffs.
        if (instability > 0.05f && time >= nextMote)
        {
            nextMote = time + Mathf.Lerp(0.6f, 0.12f, instability);
            var d = Random.insideUnitCircle * outerRadius * 0.9f;
            var at = Center + new Vector3(d.x, 0.1f, d.y);
            if (Random.value < 0.25f + 0.45f * instability)
                WardenFx.Shards(at, 3, 0.6f, WardenFx.Crimson, rise: true, size: 0.8f, life: 1.4f);
            else
                WardenFx.Dust(at, 2, 0.15f, 1f, WardenFx.DustCol, 1.4f);
        }

        // Crimson Flood: the wash clicks up one band per beat; while the floor is still
        // turning, crimson Core sigils pop across it (~6/s, quickening).
        var band = Mathf.RoundToInt(WardenFx.Stepped(flood) * 4f);
        if (band > floodBand) FloodBeat();
        floodBand = band;
        if (flood > 0.01f && flood < 0.99f && time >= nextFloodStamp)
        {
            nextFloodStamp = time + Mathf.Lerp(0.22f, 0.13f, flood / 0.75f);
            WardenFx.Stamp(RandomFloorPoint() + Vector3.up * 0.05f, Vector3.up, Random.Range(0.4f, 0.75f),
                           WardenFx.Crimson, Random.Range(0.85f, 1.1f));
        }

        // Charged runways keep answering in purple: one wall-run sigil per wall per second.
        if (charge > 0.5f && runWalls.Count > 0 && time >= nextRunStamp)
        {
            nextRunStamp = time + 0.95f / runWalls.Count;
            runStampIdx = (runStampIdx + 1) % runWalls.Count;
            var w = runWalls[runStampIdx];
            if (w != null && w.gameObject.activeInHierarchy)
                WardenFx.Stamp(RunFace(w.transform, Random.Range(-0.9f, 0.9f)), w.transform.forward,
                               Random.Range(0.4f, 0.52f), WardenFx.Purple, 0.9f);
        }
        ApplyVeins();
        ApplyFlood();
    }

    /// <summary>The player's dust chips (TraversalEffects Dust): flat faceted chips that
    /// kick up a little, tumble and shrink away. Never a puff.</summary>
    private static void Grit(Vector3 at, int count, float speed, float size)
        => WardenFx.Chips(at + Vector3.up * 0.1f, count, speed, Vector3.up * 0.35f, 0.6f, GritCol, size);

    /// <summary>A piece starts to move (tear, collapse, finale fall): chips break off it.</summary>
    private static void TearFx(Piece p)
    {
        var at = p.root.position;
        if (p.role == Role.Debris) { WardenFx.Debris(at, 2, 2f, 0.8f); return; }
        WardenFx.Debris(at + Vector3.up * 0.2f, 6, 4f, 1.2f);
        Grit(at, 4, 2f, 2f);
    }

    /// <summary>One flood band up: the veins kick and a crimson facet ring rolls across the platform.</summary>
    private void FloodBeat()
    {
        heartbeat = Mathf.Max(heartbeat, 0.55f);
        var c = WardenHazard.FloorAt(Center + Vector3.up * 3f, 8f, out var floor) ? floor : Center;
        WardenFx.Pulse(c + Vector3.up * 0.05f, Vector3.up, platformRadius * 0.25f, platformRadius * 0.8f, 0.45f,
                       WardenFx.Crimson, 1.1f, 24);
    }

    /// <summary>HDR cap that keeps the hue: no channel above <see cref="MaxTint"/>.</summary>
    private static Color Cap(Color c)
    {
        var m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        if (m > MaxTint) c *= MaxTint / m;
        c.a = 1f;
        return c;
    }

    private void ApplyVeins()
    {
        if (mpb == null) return;
        // Every beat clicks down in four bands (the player's stepped fade) — no smooth flicker.
        var beat = WardenFx.Stepped(heartbeat);
        beat *= beat;
        var floorCol = Cap(veinRest * (0.55f + instability * 0.5f + beat * 1.4f + WardenFx.Stepped(flood) * 0.8f));
        foreach (var v in floorVeins) Tint(v, floorCol);
        // Charged runways throb on a stepped beat: full on the beat, then three clicks down.
        var pulse = charge > 0.01f ? 0.8f + 0.2f * WardenFx.Stepped(1f - Mathf.Repeat(Time.time * 1.4f, 1f)) : 1f;
        var wallCol = Color.Lerp(wallRest, Cap(wallCharged) * pulse, charge);
        wallCol.a = 1f;
        foreach (var p in pieces)
        {
            if (p?.veins == null) continue;
            var c = p.role == Role.WallSlab ? wallCol : floorCol;
            foreach (var v in p.veins) Tint(v, c);
        }
    }

    private void Tint(Renderer r, Color c)
    {
        if (r == null) return;
        r.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", c);
        r.SetPropertyBlock(mpb);
    }

    private void ApplyFlood()
    {
        // A LOW four-band layer (≤ FloodWash × the player's opacity), steady between beats:
        // the stamped sigils, cracks and spikes carry "the floor hurts", not a red fill.
        var on = flood > 0.01f;
        var a = on ? WardenFx.Stepped(flood) * FloodWash * WardenFx.Opacity : 0f;
        if (on == floodOn && Mathf.Abs(a - floodAlpha) < 1e-4f) return;
        floodOn = on;
        floodAlpha = a;
        floodBlock ??= new MaterialPropertyBlock();
        var block = floodBlock;
        block.SetColor("_Tint", WardenFx.Glow(WardenFx.Crimson, a));
        void Set(MeshRenderer r)
        {
            if (r == null) return;
            if (r.enabled != on) r.enabled = on;
            if (on) r.SetPropertyBlock(block);
        }
        foreach (var r in centralOverlays) Set(r);
        foreach (var run in runs)
            foreach (var r in run.overlays) Set(r);
    }

    private IEnumerator Jolt(Transform t, float amp, float seconds)
    {
        var home = t.position;
        var e = 0f;
        while (e < seconds)
        {
            e += Time.deltaTime;
            t.position = home + Random.insideUnitSphere * amp * (1f - e / seconds);
            yield return null;
        }
        t.position = home;
    }
}
