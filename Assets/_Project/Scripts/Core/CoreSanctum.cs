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
/// Purple Corestone (<see cref="ChargeWalls"/>) = the suit can use this;
/// <see cref="SetFlood"/> washes every floor surface crimson. Everything returns
/// home on <see cref="ResetArena"/> (player death / rest).
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
    [SerializeField] private Color wallCharged = new Color(1.5f, 0.7f, 2.6f, 1f);

    private sealed class Run
    {
        public Vector3 homePos, homeLocalVisual;
        public Quaternion homeRot;
        public bool homeActive;
        public Vector3 fromPos, toPos;
        public Quaternion fromRot, toRot;
        public float t0, dur;
        public int motion;          // 0 home, 1 tearing, 2 hanging (shattered pose), 3 falling, 4 gone, 5 landed
        public float vy, bob;
        public Vector3 spin;
        public float liftSeed;
        public Collider[] colliders;
        public bool[] collidersOn;
        public readonly List<MeshRenderer> overlays = new List<MeshRenderer>();
    }

    private const int Home = 0, Tearing = 1, Hanging = 2, Falling = 3, Gone = 4, Landed = 5;

    private Run[] runs;
    private MaterialPropertyBlock mpb, floodBlock;
    private readonly List<MeshRenderer> centralOverlays = new List<MeshRenderer>();
    private readonly List<WallRunSurface> runWalls = new List<WallRunSurface>();
    private float instability, charge, chargeTarget, flood, ascend, ascendTarget, heartbeat, nextMote;
    private bool frozen;

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
                var surf = p.role == Role.WallSlab ? p.root.GetComponentInChildren<WallRunSurface>(true) : null;
                if (surf != null) runWalls.Add(surf);
            }
            runs[i] = r;
        }
        if (centralFloor != null) AddOverlays(centralFloor, centralOverlays);
        ApplyVeins();
    }

    /// <summary>A red copy of each floor "Surface" mesh, a hair above it — the flood wash.</summary>
    private static void AddOverlays(Transform root, List<MeshRenderer> into)
    {
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.name != "Surface" || mf.sharedMesh == null) continue;
            var go = new GameObject("Flood overlay");
            go.transform.SetParent(mf.transform, false);
            go.transform.localPosition = Vector3.up * 0.025f;
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
        WardenFx.Ring(origin + Vector3.up * 0.06f, Vector3.up, 0.6f, 3f + n * 3f, 0.5f, WardenFx.Crimson, 0.12f, 32, 0.15f);
        for (var i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (p?.root == null || runs[i].motion != Home) continue;
            if (n >= 1 && p.role == Role.Debris && Vector3.Distance(p.root.position, origin) < 6f + n * 4f)
                runs[i].bob = Mathf.Max(runs[i].bob, 0.25f * n);
            if (n >= 2 && (p.role == Role.Collapse || p.role == Role.Break || p.role == Role.Float))
                StartCoroutine(Jolt(p.root, 0.05f * n, 0.35f));
        }
        if (n >= 2) WardenFx.Dust(origin, 4 * n, 2.5f, 1f);
        if (n >= 3) WardenFx.Cracks(origin, 8, platformRadius * 0.8f, WardenFx.Crimson, 0.3f, 3f);
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
            // Pieces that only exist once the floor breaks (torn chunks) wake up here.
            if (!p.root.gameObject.activeSelf) p.root.gameObject.SetActive(true);
            r.fromPos = p.root.position;
            r.fromRot = p.root.rotation;
            r.t0 = Time.time + p.delay;
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
        WardenFx.Cracks(origin, 10, outerRadius, WardenFx.Crimson, 0.25f, 2.2f);
        WardenFx.Dust(origin, 18, 5f, 1.6f);
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
            foreach (var w in runWalls)
            {
                if (w == null) continue;
                var t = w.transform;
                var face = t.position + t.forward * 0.62f + Vector3.up * 1.6f;
                WardenFx.Ring(face, t.forward, 0.5f, 3.2f, 0.5f, WardenFx.PurpleBright, 0.12f, 12, 0.2f);
                WardenFx.Shards(face, 6, 1.5f, WardenFx.Purple, rise: true, size: 1.1f);
            }
        }
        chargeTarget = k;
    }

    /// <summary>0..1 crimson wash over every floor surface (Crimson Flood / Arena Collapse).</summary>
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
            if (r.motion == Gone || r.motion == Landed) continue;
            r.motion = Falling;
            r.vy = 0f;
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
                            WardenFx.Dust(p.root.position, 5, 1.6f, 1.2f);
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
                    r.vy -= 22f * dt;
                    var pos = p.root.position + Vector3.up * r.vy * dt;
                    p.root.rotation *= Quaternion.Euler(r.spin * dt);
                    var flat = new Vector2(pos.x - Center.x, pos.z - Center.z).magnitude;
                    if (p.role == Role.Debris && flat < platformRadius - 0.5f && pos.y <= Center.y + 0.3f)
                    {
                        // Lands on the platform as rubble.
                        pos.y = Center.y + 0.3f;
                        r.motion = Landed;
                        WardenFx.Dust(pos, 4, 1.4f, 0.9f);
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

        // Phase 2 dust drifting UP — the first sign gravity is failing.
        if (instability > 0.05f && time >= nextMote)
        {
            nextMote = time + Mathf.Lerp(0.6f, 0.12f, instability);
            var d = Random.insideUnitCircle * outerRadius * 0.9f;
            WardenFx.Dust(Center + new Vector3(d.x, 0.1f, d.y), 1, 0.15f, 0.45f, WardenFx.DustCol * 1.2f, 1.6f);
        }
        ApplyVeins();
        ApplyFlood();
    }

    private void ApplyVeins()
    {
        if (mpb == null) return;
        var beat = heartbeat * heartbeat;
        var floorCol = veinRest * (0.55f + instability * 0.5f + beat * 1.4f + flood * 0.8f);
        floorCol.a = 1f;
        foreach (var v in floorVeins) Tint(v, floorCol);
        var pulse = charge > 0.01f ? 0.85f + 0.15f * Mathf.Sin(Time.time * 9f) : 1f;
        var wallCol = Color.Lerp(wallRest, wallCharged * pulse, charge);
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
        var on = flood > 0.01f;
        var a = WardenFx.Stepped(flood) * (0.42f + 0.08f * Mathf.Sin(Time.time * 14f));
        var c = WardenFx.Crimson;
        c.a = a;
        floodBlock ??= new MaterialPropertyBlock();
        var block = floodBlock;
        block.SetColor("_Tint", c);
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
