using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Warden's body-side tells, added at runtime by <see cref="BossLord"/>:
///   • the Crimson Core in his chest — hidden (P1), a glow under the armour
///     (P2, steady: he's in control), exposed and beating (P3), wide open (finale);
///   • tiny red fragments orbiting him as he destabilises (P2 foreshadowing);
///   • body / blade emission swells (Executioner's Delay, Worldsplitter);
///   • the five-segment countdown up the greatsword (handle → guard → lower →
///     middle → tip — when the tip lights, he swings);
///   • the swords: his own blade (the BossSword) that breaks at the transition,
///     the Phase 3 greatsword — the SAME weapon reforged by the Core: grown ×1.55,
///     blackened, crimson-veined, loose fragments tethered to it (the procedural
///     Corestone slab is only the fallback when no BossSword is rigged) — and its
///     risen / planted / floating copies. Sword emission belongs to WardenBlade.
/// Colour stays on-language: crimson = danger, pale only at peaks.
/// </summary>
public sealed class WardenBody : MonoBehaviour
{
    public enum CoreMode { Hidden, Glimmer, Exposed, Open }

    private static readonly int EmissionId = Shader.PropertyToID("_EmissionStrength");
    private static readonly int TintId = Shader.PropertyToID("_Tint");
    private static readonly float[] SegmentAt = { 0.02f, 0.14f, 0.38f, 0.64f, 0.95f };
    private Vector3 baseScale = Vector3.one;
    private MeshRenderer seams;
    private readonly List<Transform> shards = new List<Transform>();
    private readonly List<LineRenderer> tethers = new List<LineRenderer>();
    private readonly List<Vector3> shardHome = new List<Vector3>();
    private static Mesh slabMesh, seamMesh;

    private Animator anim;
    private Transform chest;
    private Transform core;
    private MeshRenderer coreR;
    private Light coreLight;
    private MaterialPropertyBlock mpb;
    private CoreMode mode;
    private float charge, beat, coreScale, scaleTarget;
    private readonly List<Renderer> bodyEmit = new List<Renderer>();
    private readonly List<float> bodyBase = new List<float>();
    private Transform swordP2, greatsword;
    private LineRenderer coreLine;
    private const float GreatScale = 1.55f;
    private Transform floatCopy, plantedCopy, risingCopy;
    private Transform floatAnchor;
    private readonly List<Transform> orbit = new List<Transform>();
    private readonly List<Vector3> orbitSeed = new List<Vector3>();
    private float orbitK;
    private Transform[] segs;
    private LineRenderer climb;
    private int lit;
    private Vector3 baseL, tipL;
    private static Mesh coreMesh;

    public Vector3 CorePosition => core != null ? core.position : transform.position + Vector3.up * 1.6f;
    public Vector3 ChestPosition => chest != null ? chest.position : transform.position + Vector3.up * 1.4f;
    public bool HasGreatsword => greatsword != null;
    /// <summary>The weapon currently in his right hand (null = empty-handed).</summary>
    public Transform HandSword => greatsword != null && greatsword.gameObject.activeSelf ? greatsword
                                : swordP2 != null && swordP2.gameObject.activeSelf ? swordP2 : null;
    public Transform BaseSword => swordP2;

    public void Init(Animator animator)
    {
        anim = animator;
        mpb = new MaterialPropertyBlock();
        if (anim != null && anim.isHuman)
            chest = anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest)
                    ?? anim.GetBoneTransform(HumanBodyBones.Spine);
        var hand = anim != null && anim.isHuman ? anim.GetBoneTransform(HumanBodyBones.RightHand) : null;
        if (hand != null)
        {
            swordP2 = FindDeep(hand, "BossSword") ?? FindDeep(anim.transform, "LordWeapon");
            greatsword = BuildGreatsword(swordP2 != null ? swordP2.parent : hand, swordP2);
            BladeAxis(greatsword, out baseL, out tipL);
        }
        if (anim != null) baseScale = anim.transform.localScale;
        if (anim != null)
            foreach (var r in anim.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                // The swords' glow is WardenBlade's (windup heat, glint, swing flash).
                if ((swordP2 != null && r.transform.IsChildOf(swordP2)) || (greatsword != null && r.transform.IsChildOf(greatsword))) continue;
                var m = r.sharedMaterial;
                if (m == null || !m.HasProperty(EmissionId)) continue;
                bodyEmit.Add(r);
                bodyBase.Add(m.GetFloat(EmissionId));
            }
        BuildCore();
    }

    private void BuildCore()
    {
        if (chest == null) return;
        var go = new GameObject("Crimson Core");
        core = go.transform;
        core.SetParent(chest, false);
        var fwd = transform.forward;
        var scale = transform.lossyScale.x;
        core.position = chest.position + fwd * 0.17f * scale + Vector3.up * 0.04f * scale;
        core.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        go.AddComponent<MeshFilter>().sharedMesh = CoreMesh;
        coreR = go.AddComponent<MeshRenderer>();
        coreR.sharedMaterial = WardenFx.GlowMaterial;
        coreR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        coreR.receiveShadows = false;
        core.localScale = Vector3.zero;
        var lgo = new GameObject("Core light");
        lgo.transform.SetParent(core, false);
        lgo.transform.localPosition = Vector3.forward * 0.25f;
        coreLight = lgo.AddComponent<Light>();
        coreLight.type = LightType.Point;
        coreLight.color = new Color(1f, 0.12f, 0.1f);
        coreLight.range = 5f;
        coreLight.intensity = 0f;
        coreLight.shadows = LightShadows.None;
    }

    // ------------------------------------------------------------------ the Phase 3 greatsword

    /// <summary>Not the player's engineered red blade: a massive black slab of
    /// Corestone and ruined metal, irregular, almost grown — held together by red
    /// energy (glowing seams, loose fragments tethered to it). Seated on the Phase 2
    /// blade's grip frame (+Y = tip, pivot = grip), drawn in the boss's own wire style.</summary>
    private Transform BuildGreatsword(Transform hand, Transform reference)
    {
        if (reference != null && reference.GetComponent<MeshFilter>() != null) return BuildReforged(hand, reference);
        var go = new GameObject("BossGreatsword");
        var t = go.transform;
        t.SetParent(hand, false);
        if (reference != null)
        {
            t.localPosition = reference.localPosition;
            t.localRotation = reference.localRotation;
            t.localScale = reference.localScale;
        }
        else t.localScale = Vector3.one / Mathf.Max(0.01f, hand.lossyScale.x);
        var mat = SlabMaterial(reference);
        go.AddComponent<MeshFilter>().sharedMesh = SlabMesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;

        var seamGo = new GameObject("Seams");
        seamGo.transform.SetParent(t, false);
        seamGo.AddComponent<MeshFilter>().sharedMesh = SeamMesh;
        seams = seamGo.AddComponent<MeshRenderer>();
        seams.sharedMaterial = WardenFx.GlowMaterial;
        seams.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        // Loose chunks the red energy still holds on to.
        float[] ys = { 0.75f, 1.35f, 1.9f, 2.45f, 2.85f };
        for (var i = 0; i < ys.Length; i++)
        {
            var side = i % 2 == 0 ? 1f : -1f;
            var home = new Vector3(side * (0.5f + 0.06f * i), ys[i], (i % 3 - 1) * 0.06f);
            var shard = new GameObject("Fragment");
            shard.transform.SetParent(t, false);
            shard.transform.localPosition = home;
            shard.transform.localRotation = Quaternion.Euler(i * 37f, i * 71f, i * 23f);
            shard.transform.localScale = Vector3.one * (0.6f + 0.12f * (i % 3));
            shard.AddComponent<MeshFilter>().sharedMesh = FragmentMesh;
            shard.AddComponent<MeshRenderer>().sharedMaterial = mat;
            shards.Add(shard.transform);
            shardHome.Add(home);
            var lr = new GameObject("Tether").AddComponent<LineRenderer>();
            lr.transform.SetParent(t, false);
            lr.sharedMaterial = WardenFx.GlowMaterial;
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tethers.Add(lr);
        }
        go.SetActive(false);
        return t;
    }

    /// <summary>His own sword, reforged by the Core: a copy of the BossSword on the same
    /// grip, ×<see cref="GreatScale"/>, blackened with its emission mask burning crimson,
    /// a hot vein down the blade and broken chunks held to the edges by red tethers.</summary>
    private Transform BuildReforged(Transform hand, Transform reference)
    {
        var go = new GameObject("BossGreatsword");
        var t = go.transform;
        t.SetParent(hand, false);
        t.localPosition = reference.localPosition;
        t.localRotation = reference.localRotation;
        t.localScale = reference.localScale * GreatScale;
        var src = reference.GetComponent<MeshFilter>().sharedMesh;
        go.AddComponent<MeshFilter>().sharedMesh = src;
        var mr = go.AddComponent<MeshRenderer>();
        var srcMat = reference.TryGetComponent<Renderer>(out var rr) ? rr.sharedMaterial : null;
        Material mat;
        if (srcMat != null)
        {
            mat = new Material(srcMat) { name = "Warden reforged greatsword (runtime)" };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.3f, 0.26f, 0.29f));
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", new Color(1f, 0.02f, 0.04f));
            if (mat.HasProperty("_EmissionStrength")) mat.SetFloat("_EmissionStrength", Mathf.Max(1.6f, mat.GetFloat("_EmissionStrength") * 2f));
            if (mat.HasProperty("_WireTint")) mat.SetColor("_WireTint", new Color(0.02f, 0.01f, 0.02f));
        }
        else mat = SlabMaterial(null);
        mr.sharedMaterial = mat;

        var b = src.bounds;
        var tipY = Mathf.Abs(b.max.y) >= Mathf.Abs(b.min.y) ? b.max.y : b.min.y;
        var half = b.extents.x;
        var lgo = new GameObject("Core vein");
        lgo.transform.SetParent(t, false);
        coreLine = lgo.AddComponent<LineRenderer>();
        coreLine.sharedMaterial = WardenFx.GlowMaterial;
        coreLine.useWorldSpace = true;
        coreLine.positionCount = 2;
        coreLine.numCapVertices = 0;
        coreLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        veinA = Vector3.up * tipY * 0.12f;
        veinB = Vector3.up * tipY * 0.94f;

        float[] ys = { 0.3f, 0.45f, 0.6f, 0.74f, 0.86f };
        for (var i = 0; i < ys.Length; i++)
        {
            var side = i % 2 == 0 ? 1f : -1f;
            var home = new Vector3(side * (half + 0.08f + 0.03f * i), ys[i] * tipY, (i % 3 - 1) * 0.04f);
            var shard = new GameObject("Fragment");
            shard.transform.SetParent(t, false);
            shard.transform.localPosition = home;
            shard.transform.localRotation = Quaternion.Euler(i * 37f, i * 71f, i * 23f);
            shard.transform.localScale = Vector3.one * (0.5f + 0.1f * (i % 3));
            shard.AddComponent<MeshFilter>().sharedMesh = FragmentMesh;
            shard.AddComponent<MeshRenderer>().sharedMaterial = mat;
            shards.Add(shard.transform);
            shardHome.Add(home);
            var lr = new GameObject("Tether").AddComponent<LineRenderer>();
            lr.transform.SetParent(t, false);
            lr.sharedMaterial = WardenFx.GlowMaterial;
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.numCapVertices = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tethers.Add(lr);
        }
        edgeHalf = half;
        go.SetActive(false);
        return t;
    }

    private Vector3 veinA, veinB;
    private float edgeHalf = 0.3f * SlabScale;

    private static Material SlabMaterial(Transform reference)
    {
        var src = reference != null && reference.TryGetComponent<Renderer>(out var r) ? r.sharedMaterial : null;
        Material m;
        if (src != null && src.HasProperty("_WireStrength"))
        {
            m = new Material(src) { name = "Warden greatsword (runtime)" };
            m.SetTexture("_BaseMap", Texture2D.whiteTexture);
            if (m.HasProperty("_EmissionMask")) m.SetTexture("_EmissionMask", Texture2D.blackTexture);
            m.SetColor("_BaseColor", new Color(0.13f, 0.11f, 0.13f));
            m.SetColor("_WireTint", new Color(0.02f, 0.01f, 0.02f));
            m.SetFloat("_WireStrength", 1f);
            if (m.HasProperty("_FaceWireStrength")) m.SetFloat("_FaceWireStrength", 1f);
            return m;
        }
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Warden greatsword (runtime)" };
        m.SetColor("_BaseColor", new Color(0.1f, 0.09f, 0.1f));
        m.SetFloat("_Smoothness", 0.2f);
        return m;
    }

    /// <summary>Wire-format mesh (vertex colour rg = barycentric corner, b = edge
    /// bits) — the Stylized Weapon Wire shader inks the polygon edges.</summary>
    private sealed class WireBuilder
    {
        private readonly List<Vector3> v = new List<Vector3>();
        private readonly List<Color> c = new List<Color>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<int> t = new List<int>();

        public void Poly(params Vector3[] p)
        {
            for (var i = 1; i < p.Length - 1; i++)
            {
                var bits = 1f + (i == p.Length - 2 ? 2f : 0f) + (i == 1 ? 4f : 0f);
                Add(p[0], new Color(1f, 0f, bits, 1f));
                Add(p[i], new Color(0f, 1f, bits, 1f));
                Add(p[i + 1], new Color(0f, 0f, bits, 1f));
            }
        }

        private void Add(Vector3 p, Color col)
        {
            t.Add(v.Count);
            v.Add(p);
            c.Add(col);
            uv.Add(new Vector2(p.x, p.y));
        }

        /// <summary>Hexahedron from 8 corners (bottom 0-3, top 4-7, same winding).</summary>
        public void Hex(Vector3[] k)
        {
            Poly(k[0], k[1], k[2], k[3]);
            Poly(k[7], k[6], k[5], k[4]);
            for (var i = 0; i < 4; i++)
            {
                var j = (i + 1) % 4;
                Poly(k[i], k[i + 4], k[j + 4], k[j]);
            }
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetUVs(0, uv);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    // (y, left edge x, right edge x, half thickness) — irregular on purpose: bulges,
    // a bite out of one edge, a broken taper. Scaled to ~3.15 m of blade.
    private static readonly Vector4[] SlabRows =
    {
        new Vector4(0.20f, -0.30f, 0.28f, 0.10f),
        new Vector4(0.55f, -0.33f, 0.30f, 0.12f),
        new Vector4(0.95f, -0.29f, 0.34f, 0.11f),
        new Vector4(1.30f, -0.37f, 0.27f, 0.12f),
        new Vector4(1.48f, -0.24f, 0.29f, 0.11f),
        new Vector4(1.65f, -0.31f, 0.35f, 0.10f),
        new Vector4(2.05f, -0.33f, 0.29f, 0.11f),
        new Vector4(2.45f, -0.27f, 0.31f, 0.10f),
        new Vector4(2.85f, -0.24f, 0.24f, 0.09f),
        new Vector4(3.20f, -0.17f, 0.21f, 0.08f),
        new Vector4(3.45f, -0.10f, 0.13f, 0.06f),
    };
    private const float SlabScale = 0.85f;

    private static Vector3[] SlabRing(Vector4 r)
    {
        var y = r.x * SlabScale;
        var xc = (r.y + r.z) * 0.5f * SlabScale + Mathf.Sin(r.x * 5f) * 0.02f;
        return new[]
        {
            new Vector3(r.y * SlabScale, y, 0f),
            new Vector3(xc, y, r.w * SlabScale),
            new Vector3(r.z * SlabScale, y, 0f),
            new Vector3(xc, y, -r.w * SlabScale),
        };
    }

    private static Mesh SlabMesh
    {
        get
        {
            if (slabMesh != null) return slabMesh;
            var b = new WireBuilder();
            var rings = new Vector3[SlabRows.Length][];
            for (var i = 0; i < rings.Length; i++) rings[i] = SlabRing(SlabRows[i]);
            for (var i = 0; i < rings.Length - 1; i++)
                for (var k = 0; k < 4; k++)
                {
                    var j = (k + 1) % 4;
                    b.Poly(rings[i][k], rings[i + 1][k], rings[i + 1][j], rings[i][j]);
                }
            var tip = new Vector3(0.04f, 3.72f * SlabScale, 0f);
            var last = rings[rings.Length - 1];
            for (var k = 0; k < 4; k++) b.Poly(last[k], tip, last[(k + 1) % 4]);
            b.Poly(rings[0][0], rings[0][1], rings[0][2], rings[0][3]);
            // Guard — a crude chunk of metal; grip; pommel stone.
            b.Hex(Box(new Vector3(0f, 0.11f, 0f), new Vector3(0.44f, 0.1f, 0.16f), 0.05f));
            b.Hex(Box(new Vector3(0f, -0.2f, 0f), new Vector3(0.055f, 0.22f, 0.055f), 0f));
            b.Hex(Box(new Vector3(0.01f, -0.5f, 0f), new Vector3(0.11f, 0.08f, 0.1f), 0.03f));
            slabMesh = b.Build("Warden greatsword");
            return slabMesh;
        }
    }

    private static Mesh fragmentMesh;

    private static Mesh FragmentMesh
    {
        get
        {
            if (fragmentMesh != null) return fragmentMesh;
            var b = new WireBuilder();
            b.Hex(Box(Vector3.zero, new Vector3(0.13f, 0.1f, 0.09f), 0.05f));
            fragmentMesh = b.Build("Warden greatsword fragment");
            return fragmentMesh;
        }
    }

    /// <summary>Crimson energy along the ridges and a few cracks — the binding glow.</summary>
    private static Mesh SeamMesh
    {
        get
        {
            if (seamMesh != null) return seamMesh;
            var b = new WardenFx.FacetBuilder();
            var col = new Color(1f, 0.08f, 0.1f, 0.95f);
            for (var side = -1; side <= 1; side += 2)
            {
                for (var i = 1; i < SlabRows.Length - 1; i++)
                {
                    var a = SlabRing(SlabRows[i])[side > 0 ? 1 : 3];
                    var c = SlabRing(SlabRows[i + 1])[side > 0 ? 1 : 3];
                    var lift = new Vector3(0f, 0f, side * 0.006f);
                    var w = new Vector3(0.022f, 0f, 0f);
                    b.Quad(a - w + lift, c - w + lift, c + w + lift, a + w + lift, col, col, col, col);
                }
                // Cracks branching from the ridge toward the edges.
                foreach (var row in new[] { 2, 5, 7 })
                {
                    var r = SlabRings(row);
                    var from = r[side > 0 ? 1 : 3] + new Vector3(0f, 0f, side * 0.006f);
                    var to = Vector3.Lerp(from, r[row % 2 == 0 ? 0 : 2], 0.8f) + new Vector3(0f, 0.12f, side * 0.004f);
                    var n = new Vector3(0f, 0.014f, 0f);
                    b.Quad(from - n, to - n, to + n, from + n, col, col, col, col);
                }
            }
            seamMesh = b.ToMesh("Warden greatsword seams");
            return seamMesh;
        }
    }

    private static Vector3[] SlabRings(int row) => SlabRing(SlabRows[row]);

    private static Vector3[] Box(Vector3 c, Vector3 h, float jitter)
    {
        var k = new Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var x = (i == 1 || i == 2 || i == 5 || i == 6) ? h.x : -h.x;
            var z = (i == 2 || i == 3 || i == 6 || i == 7) ? h.z : -h.z;
            var y = i < 4 ? -h.y : h.y;
            var j = jitter * Mathf.Sin(i * 12.9898f + c.y * 7f);
            k[i] = c + new Vector3(x + j, y + j * 0.5f, z - j);
        }
        return k;
    }

    private void TickGreatsword()
    {
        if (greatsword == null || !greatsword.gameObject.activeInHierarchy) return;
        var hum = 1.1f + charge * 1.4f + beat * 1.2f + 0.25f * Mathf.Sin(Time.time * 7f);
        if (coreLine != null)
        {
            var va = greatsword.TransformPoint(veinA);
            var vb = greatsword.TransformPoint(veinB);
            coreLine.SetPosition(0, va);
            coreLine.SetPosition(1, vb);
            var vw = (0.016f + 0.01f * charge + 0.012f * beat) * Vector3.Distance(va, vb);
            coreLine.startWidth = vw;
            coreLine.endWidth = vw * 0.35f;
            var vc = Color.Lerp(WardenFx.Crimson, WardenFx.PaleRed, 0.12f * hum - 0.1f);
            vc.a = 0.85f;
            coreLine.startColor = coreLine.endColor = vc;
        }
        if (seams != null)
        {
            mpb.Clear();
            mpb.SetColor(TintId, new Color(hum, hum, hum, 1f));
            seams.SetPropertyBlock(mpb);
        }
        for (var i = 0; i < shards.Count; i++)
        {
            var home = shardHome[i];
            var drift = new Vector3(Mathf.Sin(Time.time * 1.7f + i) * 0.03f, Mathf.Sin(Time.time * 1.3f + i * 2f) * 0.04f, 0f);
            shards[i].localPosition = home + drift;
            shards[i].localRotation *= Quaternion.Euler(0f, 40f * Time.deltaTime, 25f * Time.deltaTime);
            var edgeX = home.x > 0f ? edgeHalf : -edgeHalf;
            var a = greatsword.TransformPoint(new Vector3(edgeX, home.y, 0f));
            var b2 = shards[i].position;
            var lr = tethers[i];
            lr.SetPosition(0, a);
            lr.SetPosition(1, b2);
            var w = 0.018f + 0.01f * Mathf.Abs(Mathf.Sin(Time.time * 23f + i * 3f));
            lr.startWidth = lr.endWidth = w;
            var c = WardenFx.Crimson;
            c.a = 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(Time.time * 11f + i));
            lr.startColor = lr.endColor = c;
        }
    }

    // ------------------------------------------------------------------ growth (the Forsaken form)

    /// <summary>Scale the visual (1 = Phase 1 size). The Core-saturated body is larger.</summary>
    public void SetScale(float k)
    {
        if (anim != null) anim.transform.localScale = baseScale * k;
    }

    // ------------------------------------------------------------------ core

    public void SetCore(CoreMode m)
    {
        mode = m;
        scaleTarget = m switch { CoreMode.Exposed => 1f, CoreMode.Open => 1.9f, _ => 0f };
    }

    /// <summary>0..1 sustained swell — the Core gathering for a big swing.</summary>
    public void Charge(float k) => charge = Mathf.Clamp01(k);

    /// <summary>One heartbeat flash.</summary>
    public void Beat(float k = 1f) => beat = Mathf.Max(beat, k);

    /// <summary>The chest armour splits and the Core shows (the Phase 3 reveal).</summary>
    public void SplitChest()
    {
        var p = ChestPosition + transform.forward * 0.2f;
        WardenFx.Shards(p, 14, 3.2f, WardenFx.StoneCol, false, 1.4f, 0.8f, transform.forward * 0.8f);
        WardenFx.Shards(p, 10, 2f, WardenFx.Crimson, true, 1f, 1.2f);
        WardenFx.Peak(p, 0.6f);
        WardenAudio.Play("shatter", p, 0.7f, 0.7f);
        SetCore(CoreMode.Exposed);
        Beat(1.5f);
    }

    /// <summary>Armour plates pulled upward off him (King's Fall's yank).</summary>
    public void ShedArmour(int count)
    {
        var p = ChestPosition;
        WardenFx.Shards(p, count, 2.5f, WardenFx.StoneCol, true, 1.6f, 1.4f, Vector3.up, 0.5f);
        WardenFx.Shards(p, count / 2, 2f, WardenFx.Crimson, true, 1f, 1.1f, Vector3.up, 0.4f);
    }

    /// <summary>The finisher: the Core bursts.</summary>
    public void ShatterCore()
    {
        var p = CorePosition;
        WardenFx.Peak(p, 2.2f);
        WardenFx.Shards(p, 40, 9f, WardenFx.Crimson, false, 1.8f, 1.1f, null, 0.3f);
        WardenFx.Shards(p, 24, 3f, WardenFx.PaleRed, true, 1.2f, 1.6f, null, 0.3f);
        WardenFx.Ring(p, Vector3.up, 0.5f, 7f, 0.6f, WardenFx.Crimson, 0.2f, 32, 0.15f, unscaled: true);
        WardenFx.Spikes(p, 14, 3.2f, WardenFx.Crimson, 0.35f, 0.14f);
        WardenAudio.Play("shatter", p, 1f, 0.6f);
        WardenAudio.Play("boom", p, 1f, 0.75f);
        SetCore(CoreMode.Hidden);
        coreScale = 0f;
    }

    // ------------------------------------------------------------------ orbit fragments

    /// <summary>0..1 — how many red fragments circle him (P2 instability).</summary>
    public void SetOrbit(float k) => orbitK = Mathf.Clamp01(k);

    private void TickOrbit(float dt)
    {
        var want = Mathf.RoundToInt(orbitK * 9f);
        while (orbit.Count < want)
        {
            var go = new GameObject("Core fragment");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = WardenFx.SpikeMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = WardenFx.GlowMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.transform.localScale = new Vector3(0.05f, 0.16f, 0.05f) / Mathf.Max(0.01f, transform.lossyScale.x);
            orbit.Add(go.transform);
            orbitSeed.Add(new Vector3(Random.value * 360f, Random.Range(0.9f, 1.5f), Random.Range(50f, 120f) * (Random.value < 0.5f ? -1f : 1f)));
            WardenFx.Converge(go.transform.position, 4, 0.4f, 0.2f, WardenFx.Crimson, 0.6f);
        }
        while (orbit.Count > want)
        {
            var last = orbit.Count - 1;
            if (orbit[last] != null) Destroy(orbit[last].gameObject);
            orbit.RemoveAt(last);
            orbitSeed.RemoveAt(last);
        }
        var centre = ChestPosition;
        for (var i = 0; i < orbit.Count; i++)
        {
            var s = orbitSeed[i];
            var ang = (s.x + Time.time * s.z) * Mathf.Deg2Rad;
            var y = Mathf.Sin(Time.time * 1.3f + i) * 0.45f;
            orbit[i].position = centre + new Vector3(Mathf.Cos(ang) * s.y, y, Mathf.Sin(ang) * s.y);
            orbit[i].rotation = Quaternion.Euler(Time.time * 90f + i * 40f, Time.time * 140f, 0f);
        }
    }

    // ------------------------------------------------------------------ swords

    /// <summary>0 = empty hands, 1 = the Phase 2 blade, 2 = the greatsword.</summary>
    public void ShowSword(int which)
    {
        if (swordP2 != null) swordP2.gameObject.SetActive(which == 1);
        if (greatsword != null) greatsword.gameObject.SetActive(which == 2);
    }

    /// <summary>The Phase 2 blade snaps — its red fragments float UP, not down.</summary>
    public void BreakP2Sword()
    {
        if (swordP2 == null) return;
        BladeAxis(swordP2, out var b, out var t);
        for (var i = 0; i < 6; i++)
        {
            var p = swordP2.TransformPoint(Vector3.Lerp(b, t, (i + 0.5f) / 6f));
            WardenFx.Shards(p, 5, 0.8f, WardenFx.Crimson, true, 1.3f, 2.2f);
            WardenFx.Shards(p, 2, 1.5f, WardenFx.StoneCol, false, 1.1f, 0.8f);
        }
        WardenAudio.Play("shatter", swordP2.position, 0.9f, 0.85f);
        swordP2.gameObject.SetActive(false);
    }

    /// <summary>The greatsword rises hilt-first out of the floor at <paramref name="ground"/>.</summary>
    public void RiseGreatsword(Vector3 ground, float seconds)
    {
        if (greatsword == null) return;
        if (risingCopy != null) Destroy(risingCopy.gameObject);
        risingCopy = WorldCopy(greatsword, "Risen greatsword");
        var len = Vector3.Distance(greatsword.TransformPoint(baseL), greatsword.TransformPoint(tipL));
        risingCopy.rotation = Quaternion.FromToRotation(greatsword.rotation * (tipL - baseL).normalized, Vector3.down) * risingCopy.rotation;
        StartCoroutine(RiseRoutine(risingCopy, ground, len, seconds));
        WardenFx.Cracks(ground, 7, 3.2f, WardenFx.Crimson, 0.3f, 2.2f);
        WardenFx.Debris(ground, 10, 5f, 1.3f);
        WardenFx.Dust(ground, 8, 1.8f, 1.2f);
        WardenAudio.Play("stone", ground, 1f, 0.55f);
    }

    private System.Collections.IEnumerator RiseRoutine(Transform copy, Vector3 ground, float len, float seconds)
    {
        var t = 0f;
        // Grip pivot: hilt at ground-0.2 (buried) → hilt ~1.1m up, blade still sunk in stone.
        var from = ground + Vector3.down * (0.2f + len * 0.05f);
        var to = ground + Vector3.up * 1.1f;
        while (t < seconds && copy != null)
        {
            t += Time.deltaTime;
            var k = Mathf.Clamp01(t / seconds);
            copy.position = Vector3.Lerp(from, to, 1f - (1f - k) * (1f - k)) + Random.insideUnitSphere * 0.03f * (1f - k);
            if (Random.value < 0.25f) WardenFx.Debris(ground, 1, 3f, 0.8f);
            yield return null;
        }
    }

    /// <summary>He takes the risen blade — the world copy becomes the hand greatsword.</summary>
    public void GrabGreatsword()
    {
        if (risingCopy != null)
        {
            WardenFx.Dust(risingCopy.position + Vector3.down, 6, 1.6f, 1f);
            Destroy(risingCopy.gameObject);
            risingCopy = null;
        }
        ShowSword(2);
    }

    /// <summary>Grave of Kings: the greatsword stays planted in the floor.</summary>
    public void PlantGreatsword(Vector3 ground)
    {
        if (greatsword == null) return;
        if (plantedCopy != null) Destroy(plantedCopy.gameObject);
        plantedCopy = WorldCopy(greatsword, "Planted greatsword");
        var len = Vector3.Distance(greatsword.TransformPoint(baseL), greatsword.TransformPoint(tipL));
        plantedCopy.rotation = Quaternion.FromToRotation(greatsword.rotation * (tipL - baseL).normalized, Vector3.down) * plantedCopy.rotation;
        plantedCopy.position = ground + Vector3.up * Mathf.Min(1.2f, len * 0.4f);
        WardenFx.Impact(ground, 1.1f, 0.12f, 0f, 6, 0.8f, 0.8f);
        WardenAudio.Play("metal", ground, 0.8f, 0.7f);
        ShowSword(0);
    }

    public void TakeGreatsword()
    {
        if (plantedCopy != null)
        {
            WardenFx.Dust(plantedCopy.position + Vector3.down, 4, 1.2f, 0.9f);
            Destroy(plantedCopy.gameObject);
            plantedCopy = null;
        }
        if (greatsword != null) WardenFx.Converge(greatsword.position, 10, 0.8f, 0.2f, WardenFx.Crimson);
        ShowSword(2);
    }

    /// <summary>Thousand-Blade Judgment: his sword hangs vertically below him.</summary>
    public void FloatSword(bool on)
    {
        if (on)
        {
            if (swordP2 == null || floatCopy != null) return;
            floatCopy = WorldCopy(swordP2, "Suspended blade");
            BladeAxis(swordP2, out var b, out var t);
            floatCopy.rotation = Quaternion.FromToRotation(swordP2.rotation * (t - b).normalized, Vector3.down) * floatCopy.rotation;
            floatAnchor = transform;
            swordP2.gameObject.SetActive(false);
            WardenFx.Converge(floatCopy.position, 8, 0.6f, 0.2f, WardenFx.Crimson);
            return;
        }
        if (floatCopy != null)
        {
            WardenFx.Shards(floatCopy.position, 6, 1.2f, WardenFx.Crimson, true);
            Destroy(floatCopy.gameObject);
            floatCopy = null;
        }
        if (swordP2 != null) swordP2.gameObject.SetActive(true);
    }

    private Transform WorldCopy(Transform src, string name)
    {
        var copy = Instantiate(src.gameObject, src.position, src.rotation).transform;
        copy.name = name;
        copy.localScale = src.lossyScale;
        foreach (Transform child in copy)
            if (child.name.StartsWith("Seg") || child.name == "Climb" || child.name == "Tether") Destroy(child.gameObject);
        copy.gameObject.SetActive(true);
        WardenHazard.Track(copy.gameObject);
        return copy;
    }

    // ------------------------------------------------------------------ countdown

    /// <summary>Light the first <paramref name="segments"/> of five blade segments.</summary>
    public void Countdown(int segments)
    {
        if (greatsword == null) return;
        if (segs == null) BuildSegments();
        segments = Mathf.Clamp(segments, 0, segs.Length);
        if (segments > lit)
        {
            var p = greatsword.TransformPoint(Vector3.Lerp(baseL, tipL, SegmentAt[segments - 1]));
            WardenFx.Peak(p, 0.25f + 0.08f * segments);
            WardenFx.Shards(p, 4, 1.2f, WardenFx.Crimson, true, 0.9f, 0.5f);
            WardenAudio.Play("chime", p, 0.35f, 0.55f + segments * 0.09f);
        }
        lit = segments;
        for (var i = 0; i < segs.Length; i++) segs[i].gameObject.SetActive(i < lit);
        climb.enabled = lit > 0;
    }

    public void ClearCountdown()
    {
        lit = 0;
        if (segs == null) return;
        foreach (var s in segs) if (s != null) s.gameObject.SetActive(false);
        if (climb != null) climb.enabled = false;
    }

    private void BuildSegments()
    {
        segs = new Transform[SegmentAt.Length];
        var world = 0.2f / Mathf.Max(0.01f, greatsword.lossyScale.x);
        for (var i = 0; i < segs.Length; i++)
        {
            var go = new GameObject("Seg" + i);
            go.transform.SetParent(greatsword, false);
            go.transform.localPosition = Vector3.Lerp(baseL, tipL, SegmentAt[i]);
            go.transform.localScale = Vector3.one * world * (i == segs.Length - 1 ? 1.4f : 1f);
            go.AddComponent<MeshFilter>().sharedMesh = CoreMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = WardenFx.GlowMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(false);
            segs[i] = go.transform;
        }
        var lgo = new GameObject("Climb");
        lgo.transform.SetParent(greatsword, false);
        climb = lgo.AddComponent<LineRenderer>();
        climb.sharedMaterial = WardenFx.GlowMaterial;
        climb.useWorldSpace = true;
        climb.positionCount = 2;
        climb.numCapVertices = 0;
        climb.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        climb.enabled = false;
    }

    // ------------------------------------------------------------------ tick

    private void LateUpdate()
    {
        var dt = Time.deltaTime;
        beat = Mathf.MoveTowards(beat, 0f, dt * 2.6f);
        coreScale = Mathf.MoveTowards(coreScale, scaleTarget, dt * 2.5f);
        var pulse = 1f + beat * 0.35f + charge * 0.2f;

        if (core != null)
        {
            core.localScale = Vector3.one * coreScale * pulse / Mathf.Max(0.01f, chest != null ? chest.lossyScale.x : 1f);
            coreR.enabled = coreScale > 0.01f;
            var bright = 1f + charge * 1.6f + beat * 1.4f + (mode == CoreMode.Open ? 1.2f : 0f);
            var c = Color.Lerp(WardenFx.Crimson, Color.white, charge * 0.25f + beat * 0.2f) * bright;
            c.a = 1f;
            mpb.Clear();
            mpb.SetColor(TintId, c);
            coreR.SetPropertyBlock(mpb);
            var baseLight = mode switch { CoreMode.Glimmer => 0.6f, CoreMode.Exposed => 1.6f, CoreMode.Open => 4f, _ => 0f };
            coreLight.intensity = baseLight * (1f + beat * 1.5f) + charge * 3f;
            coreLight.range = mode == CoreMode.Open ? 9f : 5f;
        }

        var emitK = 1f + charge * 1.5f + beat * 0.9f + (mode >= CoreMode.Exposed ? 0.35f : 0f);
        for (var i = 0; i < bodyEmit.Count; i++)
        {
            var r = bodyEmit[i];
            if (r == null) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetFloat(EmissionId, bodyBase[i] * emitK);
            r.SetPropertyBlock(mpb);
        }

        if (orbitK > 0f || orbit.Count > 0) TickOrbit(dt);
        TickGreatsword();

        if (floatCopy != null && floatAnchor != null)
        {
            floatCopy.position = floatAnchor.position + Vector3.down * 0.15f;
            floatCopy.rotation = Quaternion.AngleAxis(40f * dt, Vector3.up) * floatCopy.rotation;
        }

        if (climb != null && climb.enabled && greatsword != null)
        {
            var a = greatsword.TransformPoint(baseL);
            var b = greatsword.TransformPoint(Vector3.Lerp(baseL, tipL, SegmentAt[Mathf.Max(0, lit - 1)]));
            climb.SetPosition(0, a);
            climb.SetPosition(1, b);
            var w = 0.07f + 0.03f * Mathf.Sin(Time.time * 30f);
            climb.startWidth = w;
            climb.endWidth = w * 0.6f;
            var c = Color.Lerp(WardenFx.Crimson, Color.white, lit >= SegmentAt.Length ? 0.5f : 0.1f);
            climb.startColor = climb.endColor = c;
            foreach (var s in segs)
            {
                if (s == null || !s.gameObject.activeSelf) continue;
                var r = s.GetComponent<MeshRenderer>();
                mpb.Clear();
                mpb.SetColor(TintId, WardenFx.Crimson * (1.6f + 0.4f * Mathf.Sin(Time.time * 20f)));
                r.SetPropertyBlock(mpb);
            }
        }
    }

    /// <summary>Everything back to the Phase 1 look.</summary>
    public void ResetAll()
    {
        StopAllCoroutines();
        SetCore(CoreMode.Hidden);
        coreScale = 0f;
        charge = beat = 0f;
        orbitK = 0f;
        TickOrbit(0f);
        ClearCountdown();
        if (floatCopy != null) Destroy(floatCopy.gameObject);
        if (plantedCopy != null) Destroy(plantedCopy.gameObject);
        if (risingCopy != null) Destroy(risingCopy.gameObject);
        floatCopy = plantedCopy = risingCopy = null;
        SetScale(1f);
        ShowSword(1);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Blade axis in <paramref name="sword"/>-local space: grip (pivot) → the far
    /// end of the mesh's longest extent.</summary>
    private static void BladeAxis(Transform sword, out Vector3 grip, out Vector3 tip)
    {
        grip = Vector3.zero;
        tip = Vector3.up;
        var mf = sword.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;
        var b = mf.sharedMesh.bounds;
        var min = sword.InverseTransformPoint(mf.transform.TransformPoint(b.min));
        var max = sword.InverseTransformPoint(mf.transform.TransformPoint(b.max));
        var lo = Vector3.Min(min, max);
        var hi = Vector3.Max(min, max);
        var size = hi - lo;
        var axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        var far = Mathf.Abs(hi[axis]) >= Mathf.Abs(lo[axis]) ? hi[axis] : lo[axis];
        tip = Vector3.zero;
        tip[axis] = far;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (var i = 0; i < root.childCount; i++)
        {
            var f = FindDeep(root.GetChild(i), name);
            if (f != null) return f;
        }
        return null;
    }

    /// <summary>Elongated faceted octahedron — the Core and the countdown studs.</summary>
    private static Mesh CoreMesh
    {
        get
        {
            if (coreMesh != null) return coreMesh;
            var b = new WardenFx.FacetBuilder();
            var top = new Vector3(0f, 0.22f, 0f);
            var bottom = new Vector3(0f, -0.2f, 0f);
            var ring = new Vector3[5];
            for (var i = 0; i < ring.Length; i++)
            {
                var a = i * Mathf.PI * 2f / ring.Length;
                ring[i] = new Vector3(Mathf.Cos(a) * 0.12f, (i % 2 == 0 ? 0.02f : -0.02f), Mathf.Sin(a) * 0.12f);
            }
            var hot = new Color(1f, 0.6f, 0.55f, 1f);
            var mid = new Color(1f, 0.12f, 0.14f, 1f);
            var deep = new Color(0.45f, 0.02f, 0.05f, 1f);
            for (var i = 0; i < ring.Length; i++)
            {
                var j = (i + 1) % ring.Length;
                b.Tri(ring[i], top, ring[j], mid, hot, mid);
                b.Tri(ring[j], bottom, ring[i], mid, deep, mid);
            }
            coreMesh = b.ToMesh("Warden core");
            return coreMesh;
        }
    }
}
