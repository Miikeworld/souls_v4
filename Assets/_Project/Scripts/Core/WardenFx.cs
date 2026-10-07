using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Warden fight's effect language — one meaning per colour:
///   crimson   = damage is about to happen HERE (markers, paths, eruptions);
///   purple    = the suit can use this (Corestone walls, the Core leap);
///   pale red  = a two-frame impact peak, then straight back to crimson.
/// The player's TraversalEffects vocabulary at arena scale, tuned by the player's own
/// inspector (opacity / ink strength are read live): faceted 12/8-gons over a darker
/// ink underlay, four-band stepped fades, near-flat colour (glow ×1.28), irregular
/// five-sided chips that tumble and shrink away, tapered impact spikes, Core sigil
/// stamps and Souls/Afterimage ghosts. Bigger effects get wider geometry, never more
/// alpha. No filled discs, no puffs, no soft particles. One lazily built host per
/// scene; chips and strokes run on scaled time (a hitstop freezes the debris
/// mid-flight) — the impact star and the peak play through the freeze, like the player's.
/// </summary>
[DefaultExecutionOrder(1000)] // draw pens after WardenSocket (80), WardenBlade (90) and BladeRibbon (100) so impact peaks keep their first frame
public sealed class WardenFx : MonoBehaviour
{
    public static readonly Color Crimson = new Color(1f, 0.06f, 0.09f, 1f);
    public static readonly Color CrimsonDeep = new Color(0.42f, 0.02f, 0.05f, 1f);
    public static readonly Color PaleRed = new Color(1f, 0.84f, 0.8f, 1f);
    public static readonly Color Ink = new Color(0.05f, 0.02f, 0.09f, 1f);
    public static readonly Color DustCol = new Color(0.4f, 0.36f, 0.34f, 1f);
    public static readonly Color StoneCol = new Color(0.36f, 0.34f, 0.35f, 1f);
    public static Color Purple => CoreConduit.Main;
    public static Color PurpleBright => CoreConduit.Bright;

    // Facet counts: rings are 12-gons and inner marks 8-gons (TraversalEffects).
    public const int RingSides = 12, CoreSides = 8;
    private const float GlowGain = 1.28f;    // the player's Lerp(1, intensity 1.8, .35): near-flat colour
    private const float RingWidth = 0.075f;  // TraversalEffects.ringWidth
    private const float DustAlpha = 0.55f;   // TraversalEffects' dust chip alpha
    // A scale no other time owner writes: the ult burst (WeaponArt.BurstSpec) uses
    // 0.05 and the player's contact hitstop 0.02. Freeze restores 1x only if the
    // scale is still this exact value, so a Last Eclipse burst that starts inside a
    // Warden hitstop is not mistaken for ours, and its world slowdown is not cut
    // short when the hitstop ends.
    private const float FreezeScale = 0.047f;
    private const int PenCap = 320, GhostCap = 24;

    internal static readonly int TintId = Shader.PropertyToID("_Tint");
    private static readonly int RimId = Shader.PropertyToID("_Rim");
    private static readonly int InkId = Shader.PropertyToID("_Ink");
    private static readonly int FadeId = Shader.PropertyToID("_Fade");
    private static readonly int DissolveId = Shader.PropertyToID("_Dissolve");

    private static WardenFx host;
    private static Mesh bladeMesh, shardMesh, spikeMesh, discMesh, quadMesh;
    private static TraversalEffects tuning;
    private static float nextTuningLookup;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { host = null; tuning = null; nextTuningLookup = 0f; }

    private Material glow, afterimage;
    private ParticleSystem chipsFall, chipsRise, chipsGather, chipsUnscaled;
    private readonly List<Pen> pens = new List<Pen>();
    private readonly List<Afterimage> ghosts = new List<Afterimage>();
    private readonly Dictionary<SkinnedMeshRenderer, BakeFrame> bakeFrames = new Dictionary<SkinnedMeshRenderer, BakeFrame>();
    private MaterialPropertyBlock ghostBlock;
    private PlayerCameraController cam;

    private enum PenKind { Ring, Line, Spike, Sigil }

    /// <summary>One pooled ink-backed stroke: an expanding polygon, a drawn-on polyline,
    /// a tapered impact spike or one half of a Core sigil.</summary>
    private sealed class Pen
    {
        public LineRenderer line, ink;
        public readonly Vector3[] pts = new Vector3[64];
        public readonly float[] lens = new float[64];
        public int count, sides;
        public PenKind kind;
        public bool unscaled, inner;
        public Vector3 centre, normal, from, to;
        public float r0, expand, width, taper = 1f, age = 9f, life, hold, grow, spin;
        public Color col;
        public bool Live => age < life;
    }

    /// <summary>A baked pose (or a rigid mesh) left behind, drawn with Souls/Afterimage.</summary>
    private sealed class Afterimage
    {
        public GameObject go;
        public MeshFilter filter;
        public MeshRenderer rend;
        public Mesh baked;
        public Color rim;
        public float age = 9f, life;
        public bool Live => age < life;
    }

    /// <summary>Where BakeMesh actually puts a renderer's pose, measured once per renderer.</summary>
    private struct BakeFrame
    {
        public float scale;
        public Vector3 offset;
    }

    // ------------------------------------------------------------------ tuning

    private static TraversalEffects Tuning
    {
        get
        {
            if (tuning == null && Time.unscaledTime >= nextTuningLookup)
            {
                nextTuningLookup = Time.unscaledTime + 1f;
                tuning = FindFirstObjectByType<TraversalEffects>();
            }
            return tuning;
        }
    }

    /// <summary>The player's live effect opacity (TraversalEffects.opacity) — every boss
    /// stroke, chip, mark and ghost is multiplied by it, so one inspector tunes both.</summary>
    public static float Opacity { get { var t = Tuning; return t != null ? t.opacity : 0.55f; } }

    /// <summary>The player's live ink underlay strength (TraversalEffects.inkStrength).</summary>
    public static float InkStrength { get { var t = Tuning; return t != null ? t.inkStrength : 0.45f; } }

    /// <summary>Near-flat colour: rgb × 1.28 (the player's "glow ×⅓"), alpha as given.</summary>
    public static Color Glow(Color c, float alpha)
    {
        var t = c * GlowGain;
        t.a = alpha;
        return t;
    }

    /// <summary>Four-band stepped alpha — fades click down like the banded lighting.</summary>
    public static float Stepped(float a) => a <= 0.01f ? 0f : Mathf.Ceil(Mathf.Clamp01(a) * 4f) / 4f;

    /// <summary>Stroke-width family: 1 at player scale, ~√radius for arena-size marks, capped ×2.2.</summary>
    internal static float Family(float radius) => Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f, radius)), 1f, 2.2f);

    /// <summary>The highlight beat of a colour (the player's PaleViolet slot): a hot crimson for
    /// crimson — PaleRed stays reserved for the two-frame impact peak — white-lifted otherwise
    /// (purple stays purple).</summary>
    private static Color Pale(Color c) => c.r >= c.b ? Color.Lerp(c, Color.white, 0.3f) : Color.Lerp(Color.white, c, 0.4f);

    private static Color Deep(Color c)
    {
        var d = c * 0.42f;
        d.a = 1f;
        return d;
    }

    private static Vector3 Tumble() => new Vector3(Random.value, Random.value, Random.value) * 360f;

    private static Vector3 ToCamera(Vector3 point)
    {
        var c = Camera.main;
        var n = c != null ? c.transform.position - point : Vector3.up;
        return n.sqrMagnitude > 1e-4f ? n.normalized : Vector3.up;
    }

    // ------------------------------------------------------------------ host

    private static WardenFx Host
    {
        get
        {
            if (host == null) host = new GameObject("WardenFx").AddComponent<WardenFx>().Build();
            return host;
        }
    }

    /// <summary>Shared vertex-colour × tint material (Souls/TraversalGlow) — markers,
    /// summoned blades and hazards all draw with it so the palette stays one system.</summary>
    public static Material GlowMaterial => Host.glow;

    /// <summary>Shared Souls/Afterimage material (ink silhouette band, rim band, faint fill,
    /// polygon wire from COLOR, slice dissolve) — paint it with <see cref="PaintAfterimage"/>.
    /// Falls back to the glow material if the shader is missing.</summary>
    public static Material AfterimageMaterial
    {
        get
        {
            var h = Host;
            return h.afterimage != null ? h.afterimage : h.glow;
        }
    }

    private WardenFx Build()
    {
        var shader = Shader.Find("Souls/TraversalGlow") ?? Shader.Find("Sprites/Default");
        glow = new Material(shader) { name = "Warden glow (runtime)" };
        if (glow.HasProperty(TintId)) glow.SetColor(TintId, Color.white);
        var ghostShader = Shader.Find("Souls/Afterimage");
        if (ghostShader != null) afterimage = new Material(ghostShader) { name = "Warden afterimage (runtime)" };
        ghostBlock = new MaterialPropertyBlock();
        chipsFall = MakeChips("Warden chips", 0.6f, 3f, 600, false);
        chipsRise = MakeChips("Warden rising chips", -0.12f, 3f, 400, false);
        chipsGather = MakeChips("Warden gathering chips", 0f, 0f, 300, false);
        chipsUnscaled = MakeChips("Warden impact chips", 0.6f, 3f, 300, true);
        for (var i = 0; i < 64; i++) pens.Add(NewPen());
        return this;
    }

    /// <summary>The player's chip system: irregular five-sided ShardMesh particles tumbling
    /// in 3D, drag, shrinking to nothing over a four-band stepped fade.</summary>
    private ParticleSystem MakeChips(string name, float gravity, float drag, int max, bool unscaled)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.useUnscaledTime = unscaled;
        main.maxParticles = max;
        main.startSpeed = 0f;
        main.startLifetime = 0.35f;
        main.startSize = 0.06f;
        main.startRotation3D = true;
        main.gravityModifier = gravity;
        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient { mode = GradientMode.Fixed };
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(1f, 0.25f), new GradientAlphaKey(0.75f, 0.5f),
                          new GradientAlphaKey(0.5f, 0.75f), new GradientAlphaKey(0.25f, 1f) });
        col.color = g;
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
        var rot = ps.rotationOverLifetime;
        rot.enabled = true;
        rot.separateAxes = true;
        rot.x = new ParticleSystem.MinMaxCurve(-9f, 9f);
        rot.y = new ParticleSystem.MinMaxCurve(-9f, 9f);
        rot.z = new ParticleSystem.MinMaxCurve(-6f, 6f);
        if (drag > 0f)
        {
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.drag = drag;
        }
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Mesh;
        r.mesh = ShardMesh;
        r.alignment = ParticleSystemRenderSpace.World;
        r.sharedMaterial = glow;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        ps.Play();
        return ps;
    }

    private Pen NewPen() => new Pen { ink = MakeLine("Warden stroke ink", 0), line = MakeLine("Warden stroke", 1) };

    private LineRenderer MakeLine(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = glow;
        lr.useWorldSpace = true;
        lr.numCornerVertices = 0;   // hard facet corners
        lr.numCapVertices = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.sortingOrder = order;    // ink (0) always under its coloured stroke (1)
        lr.enabled = false;
        return lr;
    }

    private Pen Acquire()
    {
        Pen pick = null;
        foreach (var s in pens) if (!s.Live) { pick = s; break; }
        if (pick == null)
        {
            if (pens.Count < PenCap) { pick = NewPen(); pens.Add(pick); }
            else { pick = pens[0]; foreach (var s in pens) if (s.age / s.life > pick.age / pick.life) pick = s; }
        }
        pick.age = 0f;
        pick.hold = 0f;
        pick.grow = 0f;
        pick.taper = 1f;
        pick.spin = 0f;
        pick.inner = false;
        pick.unscaled = false;
        return pick;
    }

    // ------------------------------------------------------------------ strokes

    /// <summary>Faceted ring over ink, expanding by <paramref name="expand"/> metres (ease-out)
    /// and stepping out after <paramref name="hold"/>. 8–24 sides, random spin; the width
    /// never drops under the player's ring width widened for the ring's size.</summary>
    public static void Ring(Vector3 centre, Vector3 normal, float radius, float expand, float life, Color col,
                            float width = 0.1f, int sides = RingSides, float hold = 0.2f, bool unscaled = false)
    {
        var s = Host.Acquire();
        s.kind = PenKind.Ring;
        s.centre = centre;
        s.normal = normal.sqrMagnitude < 0.01f ? Vector3.up : normal.normalized;
        s.r0 = radius;
        s.expand = expand;
        s.life = Mathf.Max(0.03f, life);
        s.width = Mathf.Max(width, RingWidth * 1.3f * Family(radius + expand));
        s.sides = Mathf.Clamp(sides, 8, 24);
        s.col = col;
        s.hold = Mathf.Clamp01(hold);
        s.spin = Random.value * Mathf.PI;
        s.unscaled = unscaled;
    }

    /// <summary>The player's pulse: a pooled faceted ring growing radius → radius+expand
    /// (ease-out), ink underlay, Stepped(1-t) × Opacity. <paramref name="width"/> is the
    /// player's multiplier on ringWidth×1.3 (×√size for arena-scale rings).</summary>
    public static void Pulse(Vector3 centre, Vector3 normal, float radius, float expand, float life, Color col,
                             float width = 1f, int sides = RingSides, bool unscaled = false)
    {
        var s = Host.Acquire();
        s.kind = PenKind.Ring;
        s.centre = centre;
        s.normal = normal.sqrMagnitude < 0.01f ? Vector3.up : normal.normalized;
        s.r0 = radius;
        s.expand = expand;
        s.life = Mathf.Max(0.03f, life);
        s.width = RingWidth * 1.3f * Mathf.Max(0.1f, width) * Family(radius + expand);
        s.sides = Mathf.Clamp(sides, 6, 24);
        s.col = col;
        s.spin = Random.value * Mathf.PI;
        s.unscaled = unscaled;
    }

    /// <summary>A Core sigil stamped on a surface (the player's wall-run / landing mark):
    /// 12-gon rim + 8-gon core at 0.42r rotated π/8, each over ink. Pops in over 0.06s,
    /// holds to 35%, then clicks out in four steps; the core is white for the first 20%,
    /// then the pale beat. A few chips knock off the surface.</summary>
    public static void Stamp(Vector3 centre, Vector3 normal, float radius, Color rim, float life = 1.1f)
    {
        var h = Host;
        var n = normal.sqrMagnitude < 0.01f ? Vector3.up : normal.normalized;
        var spin = Random.Range(-0.2f, 0.2f);
        for (var i = 0; i < 2; i++)
        {
            var s = h.Acquire();
            s.kind = PenKind.Sigil;
            s.inner = i == 1;
            s.centre = centre + n * 0.02f;
            s.normal = n;
            s.r0 = radius;
            s.col = rim;
            s.life = Mathf.Max(0.1f, life);
            s.spin = spin + (s.inner ? Mathf.PI / CoreSides : 0f);
            s.sides = s.inner ? CoreSides : radius > 5f ? 24 : RingSides;
            s.width = RingWidth * (s.inner ? 0.9f : 1.1f) * Family(radius);
        }
        var k = Mathf.Sqrt(Mathf.Clamp(radius, 0.3f, 4f));
        Chips(centre, 3, 1.5f * k, n * 0.5f + Vector3.down * 0.2f, 0.45f, rim, 0.8f * k);
        Chips(centre, 2, 1.1f * k, n * 0.4f, 0.4f, Deep(rim), 0.9f * k);
    }

    /// <summary>Ink-backed polyline drawn on over <paramref name="grow"/> seconds — cracks,
    /// scan lines, aim lanes. Hard corners, a slight taper toward the head. Points are copied.</summary>
    public static void Line(IList<Vector3> points, Color col, float width, float life, float grow = 0f,
                            float hold = 0.4f, bool unscaled = false)
        => Polyline(points, col, width, life, grow, hold, unscaled, 0.6f);

    private static void Polyline(IList<Vector3> points, Color col, float width, float life, float grow, float hold,
                                 bool unscaled, float taper)
    {
        if (points == null || points.Count < 2) return;
        var s = Host.Acquire();
        s.kind = PenKind.Line;
        s.count = Mathf.Min(points.Count, s.pts.Length);
        var total = 0f;
        for (var i = 0; i < s.count; i++)
        {
            s.pts[i] = points[i];
            if (i > 0) total += Vector3.Distance(points[i - 1], points[i]);
            s.lens[i] = total;
        }
        s.life = Mathf.Max(0.03f, life);
        s.width = width;
        s.col = col;
        s.hold = Mathf.Clamp01(hold);
        s.grow = grow;
        s.taper = taper;
        s.unscaled = unscaled;
    }

    /// <summary>The player's impact spike: the head shoots out (ease-out), the tail chases,
    /// the stroke thins to <paramref name="taper"/> at the tip over a wider ink underlay and
    /// clicks out in four steps — a drawn stroke, never a constant-width bar.</summary>
    public static void Stroke(Vector3 from, Vector3 to, Color col, float width, float life, float taper = 0.25f,
                              bool unscaled = false)
    {
        var s = Host.Acquire();
        s.kind = PenKind.Spike;
        s.from = from;
        s.to = to;
        s.col = col;
        s.width = width;
        s.life = Mathf.Max(0.03f, life);
        s.taper = taper;
        s.unscaled = unscaled;
    }

    /// <summary>Radial impact star in the camera plane — tapered ink-backed spikes shooting out.</summary>
    public static void Spikes(Vector3 point, int count, float length, Color col, float life = 0.22f, float width = 0.09f)
    {
        var n = ToCamera(point);
        var basis = Vector3.ProjectOnPlane(Vector3.up, n);
        if (basis.sqrMagnitude < 0.01f) basis = Vector3.ProjectOnPlane(Vector3.right, n);
        basis.Normalize();
        var spin = Random.value * 360f;
        for (var i = 0; i < count; i++)
        {
            var dir = Quaternion.AngleAxis(spin + i * 360f / count + Random.Range(-10f, 10f), n) * basis;
            var len = length * Random.Range(0.7f, 1.2f);
            Stroke(point, point + dir * len, i % 3 == 0 ? Color.white : col, width, life, 0.25f, true);
        }
    }

    /// <summary>The player's HitFlash at any scale: a white 2-frame peak chip, white and
    /// <paramref name="col"/> pulses, 6+2·strength tapered spikes in the camera plane
    /// (longest along <paramref name="swingDir"/>) and chips thrown along the swing.
    /// Unscaled time, so it plays through the hit freeze.</summary>
    public static void Star(Vector3 point, Color col, Vector3 swingDir, float strength, float scale = 1f)
        => StarBurst(point, col, swingDir, strength, scale, true);

    private static void StarBurst(Vector3 point, Color col, Vector3 swingDir, float strength, float scale, bool peak)
    {
        var h = Host;
        scale = Mathf.Max(0.2f, scale);
        var op = Opacity;
        var n = ToCamera(point);
        if (peak) EmitOne(h.chipsUnscaled, point, Vector3.zero, Glow(Color.white, op), 3.2f * Mathf.Min(scale, 2.5f), 0.045f);
        Pulse(point, n, 0.04f * scale, 0.3f * scale, 0.12f, Color.white, 0.7f, RingSides, true);
        Pulse(point, n, 0.08f * scale, 0.45f * scale, 0.18f, col, 0.9f, RingSides, true);
        strength = Mathf.Clamp(strength, 0.5f, 2.5f);
        var swing = Vector3.ProjectOnPlane(swingDir, n);
        var hasSwing = swing.sqrMagnitude > 1e-4f;
        if (!hasSwing) swing = Vector3.ProjectOnPlane(Vector3.right, n);
        if (swing.sqrMagnitude < 1e-4f) swing = Vector3.ProjectOnPlane(Vector3.forward, n);
        swing.Normalize();
        var count = Mathf.RoundToInt(6 + 2 * strength);
        var spin = Random.value * Mathf.PI * 2f;
        var width = 0.05f * Mathf.Clamp(Mathf.Sqrt(scale), 0.6f, 2.2f);
        for (var i = 0; i < count; i++)
        {
            var dir = Quaternion.AngleAxis((spin + i * Mathf.PI * 2f / count) * Mathf.Rad2Deg + Random.Range(-12f, 12f), n) * swing;
            var along = Mathf.Abs(Vector3.Dot(dir, swing));
            var len = (0.22f + 0.3f * along * along) * Random.Range(0.8f, 1.15f) * Mathf.Lerp(1f, 1.5f, strength - 1f) * scale;
            Stroke(point, point + dir * len, i % 3 == 0 ? Color.white : col, width, 0.16f + 0.04f * strength, 0.25f, true);
        }
        if (hasSwing)
            h.Emit(h.chipsUnscaled, point, Mathf.RoundToInt(5 * strength), 3.2f * Mathf.Sqrt(scale), swing * 0.9f, 0.3f,
                   Glow(col, op), 0.8f * Mathf.Sqrt(scale));
    }

    /// <summary>Jagged branching cracks on the floor plane through <paramref name="origin"/>:
    /// tapered from the origin to a hairline tip, ink-backed, stepped out.</summary>
    public static void Cracks(Vector3 origin, int count, float length, Color col, float grow = 0.15f,
                              float life = 1.3f, Vector3? along = null, float spread = 360f, float width = 0.07f)
    {
        var y = origin.y + 0.035f;
        var baseDir = along.HasValue ? Vector3.ProjectOnPlane(along.Value, Vector3.up).normalized : Vector3.forward;
        if (baseDir.sqrMagnitude < 0.01f) baseDir = Vector3.forward;
        var pts = new List<Vector3>(16);
        for (var c = 0; c < count; c++)
        {
            var ang = along.HasValue ? Random.Range(-spread * 0.5f, spread * 0.5f) : c * 360f / count + Random.Range(-20f, 20f);
            var dir = Quaternion.AngleAxis(ang, Vector3.up) * baseDir;
            var len = length * Random.Range(0.65f, 1.15f);
            pts.Clear();
            var p = new Vector3(origin.x, y, origin.z);
            pts.Add(p);
            var walked = 0f;
            while (walked < len && pts.Count < 15)
            {
                var step = Random.Range(0.3f, 0.6f);
                dir = Quaternion.AngleAxis(Random.Range(-28f, 28f), Vector3.up) * dir;
                p += dir * step;
                walked += step;
                pts.Add(p);
                if (pts.Count == 5 && Random.value < 0.6f)
                {
                    var b = new List<Vector3> { p };
                    var bd = Quaternion.AngleAxis(Random.value < 0.5f ? 40f : -40f, Vector3.up) * dir;
                    var bp = p;
                    for (var k = 0; k < 4; k++) { bd = Quaternion.AngleAxis(Random.Range(-25f, 25f), Vector3.up) * bd; bp += bd * Random.Range(0.25f, 0.45f); b.Add(bp); }
                    Polyline(b, col, width * 0.7f, life, grow, 0.45f, false, 0.3f);
                }
            }
            Polyline(pts, col, width, life, grow, 0.45f, false, 0.35f);
        }
    }

    // ------------------------------------------------------------------ chips

    /// <summary>The player's shard chips: irregular five-sided meshes, 6–11 cm × size
    /// (capped 2.6), alpha × Opacity, tumbling, shrinking to nothing; drag 3, gravity 0.6.</summary>
    public static void Chips(Vector3 point, int count, float speed, Vector3 bias, float life, Color col,
                             float size = 1f, bool unscaled = false)
    {
        var h = Host;
        h.Emit(unscaled ? h.chipsUnscaled : h.chipsFall, point, count, speed, bias, life, Glow(col, col.a * Opacity), size);
    }

    private void Emit(ParticleSystem ps, Vector3 point, int count, float speed, Vector3 bias, float life, Color tint, float size)
    {
        if (ps == null || count <= 0) return;
        count = Mathf.Min(count, 32);
        size = Mathf.Clamp(size, 0.1f, 2.6f);
        life = Mathf.Max(0.03f, life);
        for (var i = 0; i < count; i++)
        {
            var d = Random.insideUnitSphere;
            d.y = Mathf.Abs(d.y) * 0.6f;
            if (bias != Vector3.zero) d = (d + bias * 2f).normalized;
            ps.Emit(new ParticleSystem.EmitParams
            {
                position = point,
                velocity = d * speed * Random.Range(0.6f, 1f),
                startColor = tint,
                startSize = Random.Range(0.06f, 0.11f) * size,
                rotation3D = Tumble(),
                startLifetime = Random.Range(life * 0.6f, life),
            }, 1);
        }
    }

    private static void EmitOne(ParticleSystem ps, Vector3 position, Vector3 velocity, Color tint, float size, float life)
    {
        if (ps == null) return;
        ps.Emit(new ParticleSystem.EmitParams
        {
            position = position,
            velocity = velocity,
            startColor = tint,
            startSize = Random.Range(0.06f, 0.11f) * size,
            rotation3D = Tumble(),
            startLifetime = Mathf.Max(0.02f, life),
        }, 1);
    }

    /// <summary>Chips knocked out of an impact: stone, deep crimson and every third one ink,
    /// at the player's chip sizes. Flat colour (no glow gain).</summary>
    public static void Debris(Vector3 point, int count, float speed, float size = 1f, Vector3? bias = null)
    {
        var h = Host;
        count = Mathf.Min(count, 24);
        var op = Opacity;
        var s = Mathf.Clamp(1.1f * size, 0.8f, 2f);
        for (var i = 0; i < count; i++)
        {
            var d = Random.insideUnitSphere;
            d.y = Mathf.Abs(d.y) * 0.8f + 0.45f;
            if (bias.HasValue) d += bias.Value;
            var c = i % 3 == 0 ? Ink : i % 3 == 1 ? StoneCol * Random.Range(0.85f, 1.15f) : CrimsonDeep;
            c.a = op;
            EmitOne(h.chipsFall, point + Random.insideUnitSphere * 0.2f, d.normalized * speed * Random.Range(0.55f, 1f),
                    c, s, Random.Range(0.45f, 0.75f));
        }
    }

    /// <summary>The player's dust: large flat chips in the dust colour (or
    /// <paramref name="tint"/>), drifting out and slightly up, gone inside a second.
    /// No puffs.</summary>
    public static void Dust(Vector3 point, int count, float spread, float size = 1f, Color? tint = null, float rise = 0.5f)
    {
        var h = Host;
        count = Mathf.Min(count, 18);
        var c = tint ?? DustCol;
        c.a = DustAlpha * Opacity;
        var s = Mathf.Clamp(1.5f * size, 1.5f, 2.6f);
        for (var i = 0; i < count; i++)
        {
            var d = Random.insideUnitCircle.normalized;
            var v = new Vector3(d.x, 0f, d.y) * spread * Random.Range(0.35f, 0.7f) + Vector3.up * rise * Random.Range(0.6f, 1.2f);
            EmitOne(h.chipsRise, point + new Vector3(d.x, 0.1f, d.y) * 0.3f, v, c, s, Random.Range(0.5f, 0.85f));
        }
    }

    /// <summary>Faceted Core shards at the player's chip sizes. <paramref name="rise"/> =
    /// drift upward (gravity is breaking) instead of falling. Every third chip is ink or
    /// a white-lifted one; at most 24 per burst; life ≤ 0.9s (1.4s rising).</summary>
    public static void Shards(Vector3 point, int count, float speed, Color col, bool rise = false,
                              float size = 1f, float life = 0.8f, Vector3? bias = null, float radius = 0.2f)
    {
        var h = Host;
        var ps = rise ? h.chipsRise : h.chipsFall;
        count = Mathf.Min(count, 24);
        var s = size <= 1.3f ? size : Mathf.Min(2f, 1.3f + (size - 1.3f) * 0.6f);
        life = Mathf.Min(life, rise ? 1.4f : 0.9f);
        var op = Opacity;
        var main = Glow(col, col.a * op);
        var pale = Glow(Color.Lerp(col, Color.white, 0.55f), col.a * op);
        var ink = Ink;
        ink.a = op;
        for (var i = 0; i < count; i++)
        {
            var d = Random.insideUnitSphere;
            if (rise) d.y = Mathf.Abs(d.y) + 0.6f;
            if (bias.HasValue) d += bias.Value;
            EmitOne(ps, point + Random.insideUnitSphere * radius, d.normalized * speed * Random.Range(0.45f, 1f),
                    i % 3 != 0 ? main : i % 6 == 0 ? ink : pale, s, Random.Range(life * 0.6f, life));
        }
    }

    /// <summary>Chips born around <paramref name="point"/> flying IN to it over
    /// <paramref name="time"/> — a blade or the Core gathering itself (the player's forge).</summary>
    public static void Converge(Vector3 point, int count, float radius, float time, Color col, float size = 1f)
    {
        var h = Host;
        time = Mathf.Clamp(time, 0.05f, 0.9f);
        count = Mathf.Min(count, 24);
        var op = Opacity;
        var s = Mathf.Min(size, 1.3f);
        for (var i = 0; i < count; i++)
        {
            var off = Random.onUnitSphere * radius * Random.Range(0.6f, 1f);
            EmitOne(h.chipsGather, point + off, -off / time, Glow(i % 3 == 0 ? Color.white : col, op), s, time);
        }
    }

    /// <summary>Metal on stone: white-hot and crimson chips thrown off a surface along
    /// <paramref name="normal"/> (and the blade's travel, via <paramref name="bias"/>).
    /// Stretched sparks stay reserved for blade-on-body contact (HitFx).</summary>
    public static void Sparks(Vector3 point, Vector3 normal, int count, float speed, Vector3? bias = null)
    {
        var h = Host;
        var n = normal.sqrMagnitude > 1e-4f ? normal.normalized : Vector3.up;
        count = Mathf.Min(count, 16);
        var op = Opacity;
        var hot = Glow(Color.white, op);
        var red = Glow(Crimson, op);
        for (var i = 0; i < count; i++)
        {
            var d = (n + Random.insideUnitSphere * 0.9f + (bias ?? Vector3.zero)).normalized;
            EmitOne(h.chipsFall, point + Random.insideUnitSphere * 0.06f, d * speed * Random.Range(0.5f, 1.1f),
                    i % 3 == 0 ? hot : red, Random.Range(0.6f, 0.9f), Random.Range(0.18f, 0.34f));
        }
    }

    // ------------------------------------------------------------------ composites

    /// <summary>A gouge cut into the floor by a blade: a dark ink-edged scar with a
    /// hot crimson core that cools first. Both step out inside <paramref name="life"/>.</summary>
    public static void Groove(IList<Vector3> points, float width = 0.12f, float life = 1.4f)
    {
        if (points == null || points.Count < 2) return;
        Polyline(points, new Color(0.14f, 0.05f, 0.06f, 1f), width, life, 0.05f, 0.55f, false, 0.7f);
        Polyline(points, Crimson, width * 0.35f, life * 0.45f, 0.05f, 0.35f, false, 0.5f);
    }

    /// <summary>The pre-strike glint on a blade: a small white 8-gon and a four-ray tapered
    /// star in the camera plane — "this edge is about to move". No filled shapes.</summary>
    public static void Glint(Vector3 point, float size = 1f)
    {
        var n = ToCamera(point);
        Pulse(point, n, 0.03f * size, 0.22f * size, 0.14f, Color.white, 0.7f, CoreSides, true);
        var up = Vector3.ProjectOnPlane(Vector3.up, n);
        if (up.sqrMagnitude < 0.01f) up = Vector3.ProjectOnPlane(Vector3.right, n);
        up.Normalize();
        var side = Vector3.Cross(n, up);
        var width = 0.035f * Mathf.Clamp(size, 0.6f, 2.2f);
        for (var i = 0; i < 4; i++)
        {
            var d = (i % 2 == 0 ? up : side) * (i < 2 ? 1f : -1f);
            var len = (i % 2 == 0 ? 0.75f : 0.45f) * size;
            Stroke(point + d * 0.04f * size, point + d * len, i % 2 == 0 ? Color.white : PaleRed, width, 0.2f, 0.25f, true);
        }
    }

    /// <summary>A faceted arc stroke (≈15° facets) drawn on fast around <paramref name="centre"/>
    /// in the plane of <paramref name="normal"/>, from <paramref name="from"/> sweeping
    /// <paramref name="arcDeg"/> (sign = direction); ink-backed, tapering to the leading end.</summary>
    public static void Crescent(Vector3 centre, Vector3 normal, Vector3 from, float radius, float arcDeg, Color col,
                                float width = 0.12f, float life = 0.3f, float grow = 0.08f)
    {
        var n = normal.sqrMagnitude > 1e-4f ? normal.normalized : Vector3.up;
        var f = Vector3.ProjectOnPlane(from, n);
        if (f.sqrMagnitude < 1e-4f) return;
        f.Normalize();
        var steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(arcDeg) / 15f), 3, 24);
        var pts = new Vector3[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var k = i / (float)steps;
            var r = radius * (1f + 0.06f * Mathf.Sin(k * Mathf.PI));
            pts[i] = centre + Quaternion.AngleAxis(arcDeg * k, n) * f * r;
        }
        Polyline(pts, col, width, life, grow, 0.3f, false, 0.35f);
    }

    /// <summary>The pale-red peak, the player's way: one pale chip at 3.2× size for ~2
    /// frames plus a pale-red 8-gon and an inner white 8-gon snapping open in the camera
    /// plane — then the crimson carries the read. Unscaled, never a filled disc.</summary>
    public static void Peak(Vector3 point, float radius)
    {
        var h = Host;
        radius = Mathf.Max(0.05f, radius);
        var n = ToCamera(point);
        EmitOne(h.chipsUnscaled, point, Vector3.zero, Glow(PaleRed, Opacity), 3.2f * Mathf.Clamp(radius / 0.3f, 0.6f, 2.5f), 0.035f);
        Pulse(point, n, 0.15f * radius, 0.9f * radius, 0.12f, PaleRed, 0.7f, CoreSides, true);
        Pulse(point, n, 0.06f * radius, 0.45f * radius, 0.09f, Color.white, 0.6f, CoreSides, true);
    }

    /// <summary>The full layered impact (the player's hard landing at arena scale): peak →
    /// ground sigil + white/crimson/8-gon pulses + impact star + crimson chips; debris chips
    /// (0.05s) → dust chips (0.1s) → red Core cracks spreading. Shake and hitstop land ON
    /// contact, never before it.</summary>
    public static void Impact(Vector3 point, float scale, float shake = 0f, float hitstop = 0f,
                              int debris = 8, float dust = 1f, float cracks = 1f, Color? crackCol = null)
    {
        Host.StartCoroutine(Host.ImpactRoutine(point, scale, shake, hitstop, debris, dust, cracks, crackCol ?? Crimson));
    }

    private IEnumerator ImpactRoutine(Vector3 p, float scale, float shake, float hitstop, int debris, float dustK,
                                      float crackK, Color crackCol)
    {
        scale = Mathf.Max(0.2f, scale);
        var ground = p + Vector3.up * 0.04f;
        Peak(p + Vector3.up * 0.45f * scale, 0.9f * scale);
        Stamp(ground, Vector3.up, Mathf.Min(0.6f * scale, 2.6f), Crimson);
        Pulse(ground, Vector3.up, 0.1f * scale, 1.1f * scale, 0.28f, Color.white, 1.1f);
        Pulse(ground + Vector3.up * 0.01f, Vector3.up, 0.25f * scale, 2f * scale, 0.5f, Crimson, 1.5f);
        Pulse(ground + Vector3.up * 0.06f, Vector3.up, 0.15f * scale, 1.3f * scale, 0.38f, Crimson, 1f, CoreSides);
        StarBurst(p + Vector3.up * 0.6f * scale, Crimson, Vector3.zero, Mathf.Clamp(scale, 0.8f, 2.5f), scale, false);
        Chips(ground, Mathf.RoundToInt(18 * Mathf.Clamp(scale, 0.6f, 1.33f)), 4.2f * Mathf.Sqrt(scale), Vector3.up * 0.25f,
              0.35f, Crimson, Mathf.Clamp(Mathf.Sqrt(scale), 0.8f, 1.6f));
        if (shake > 0f) Shake(shake);
        if (hitstop > 0f) HitStop(hitstop);
        yield return new WaitForSeconds(0.05f);
        if (debris > 0) Debris(p + Vector3.up * 0.1f, Mathf.RoundToInt(debris * Mathf.Clamp(scale, 0.6f, 1.8f)), 6.5f * Mathf.Sqrt(scale), Mathf.Sqrt(scale));
        yield return new WaitForSeconds(0.05f);
        if (dustK > 0f) Dust(p, Mathf.RoundToInt(8 * dustK * Mathf.Clamp(scale, 0.6f, 1.8f)), 2.2f * Mathf.Sqrt(scale), Mathf.Sqrt(scale));
        if (crackK > 0f) Cracks(p, Mathf.RoundToInt(4 + 2 * crackK), 1.6f * scale * crackK, crackCol, 0.16f, 1.2f);
    }

    public static void Shake(float strength)
    {
        if (strength <= 0f) return;
        var h = Host;
        if (h.cam == null) h.cam = FindFirstObjectByType<PlayerCameraController>();
        if (h.cam != null) h.cam.Shake(strength);
    }

    /// <summary>A tiny world freeze on especially massive hits. Respects any other
    /// time owner: only starts from 1x and only restores its own scale.</summary>
    public static void HitStop(float seconds)
    {
        if (seconds <= 0f || !Mathf.Approximately(Time.timeScale, 1f)) return;
        Host.StartCoroutine(Host.Freeze(seconds));
    }

    private IEnumerator Freeze(float seconds)
    {
        Time.timeScale = FreezeScale;
        yield return new WaitForSecondsRealtime(seconds);
        if (Mathf.Approximately(Time.timeScale, FreezeScale)) Time.timeScale = 1f;
    }

    // ------------------------------------------------------------------ afterimages

    /// <summary>The player's dodge afterimage of a skinned body: the current pose baked and
    /// left in place, drawn with Souls/Afterimage (ink silhouette, rim band, faint fill,
    /// the mesh's own polygon wire), holding then slicing away in three steps.
    /// BakeMesh(useScale: false) already returns world-size vertices relative to the
    /// renderer pivot, so the ghost sits at the pivot with UNIT scale — the frame is
    /// verified once per renderer against hand-skinned vertices.</summary>
    public static void Ghost(SkinnedMeshRenderer smr, Color rim, float life = 0.42f)
    {
        if (smr == null || smr.sharedMesh == null || !smr.gameObject.activeInHierarchy) return;
        var h = Host;
        if (h.afterimage == null) return;
        var g = h.AcquireGhost();
        if (g.baked == null)
        {
            g.baked = new Mesh { name = "Warden afterimage" };
            g.baked.MarkDynamic();
        }
        smr.BakeMesh(g.baked);
        g.baked.RecalculateBounds();
        if (!h.bakeFrames.TryGetValue(smr, out var frame))
        {
            frame = Calibrate(smr, g.baked);
            h.bakeFrames[smr] = frame;
        }
        g.filter.sharedMesh = g.baked;
        var tr = smr.transform;
        g.go.transform.SetPositionAndRotation(tr.position + tr.rotation * frame.offset, tr.rotation);
        g.go.transform.localScale = Vector3.one * frame.scale;
        h.StartGhost(g, rim, life);
    }

    /// <summary>Afterimage of a rigid mesh (the sword, a summoned weapon) at its current
    /// pose. The mesh is shared, not copied — wire-format meshes show their polygon wire.</summary>
    public static void Ghost(MeshFilter mf, Color rim, float life = 0.3f)
    {
        if (mf == null || mf.sharedMesh == null || !mf.gameObject.activeInHierarchy) return;
        var h = Host;
        if (h.afterimage == null) return;
        var g = h.AcquireGhost();
        g.filter.sharedMesh = mf.sharedMesh;
        var tr = mf.transform;
        g.go.transform.SetPositionAndRotation(tr.position, tr.rotation);
        g.go.transform.localScale = tr.lossyScale;
        h.StartGhost(g, rim, life);
    }

    /// <summary>Fills Souls/Afterimage's properties the way the player's ghosts are painted:
    /// fill = <paramref name="rim"/> at a 0.16, rim band ×1.4, ink at InkStrength,
    /// _Fade = fade × Opacity, slice dissolve.</summary>
    public static void PaintAfterimage(MaterialPropertyBlock mpb, Color rim, float fade, float dissolve = 0f)
    {
        if (mpb == null) return;
        Paint(mpb, rim, rim * 1.4f, Mathf.Clamp01(fade) * Opacity, Mathf.Clamp01(dissolve), InkStrength);
    }

    private static void Paint(MaterialPropertyBlock mpb, Color fill, Color rim, float fade, float dissolve, float inkK)
    {
        fill.a = 0.16f;
        rim.a = 1f;
        var k = Ink;
        k.a = inkK;
        mpb.SetColor(TintId, fill);
        mpb.SetColor(RimId, rim);
        mpb.SetColor(InkId, k);
        mpb.SetFloat(FadeId, fade);
        mpb.SetFloat(DissolveId, dissolve);
    }

    private Afterimage AcquireGhost()
    {
        Afterimage pick = null;
        foreach (var g in ghosts) if (!g.Live) { pick = g; break; }
        if (pick == null)
        {
            if (ghosts.Count < GhostCap)
            {
                pick = new Afterimage { go = new GameObject("Warden afterimage") };
                pick.go.transform.SetParent(transform, false);
                pick.filter = pick.go.AddComponent<MeshFilter>();
                pick.rend = pick.go.AddComponent<MeshRenderer>();
                pick.rend.sharedMaterial = afterimage;
                pick.rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                pick.rend.receiveShadows = false;
                ghosts.Add(pick);
            }
            else { pick = ghosts[0]; foreach (var g in ghosts) if (g.age / g.life > pick.age / pick.life) pick = g; }
        }
        return pick;
    }

    private void StartGhost(Afterimage g, Color rim, float life)
    {
        g.rim = rim;
        g.rim.a = 1f;
        g.life = Mathf.Max(0.05f, life);
        g.age = 0f;
        g.go.SetActive(true);
        TickGhost(g, Opacity, InkStrength);
    }

    /// <summary>Hold, then fade in three steps while slices drop out; the rim cools from the
    /// pale beat to <see cref="Afterimage.rim"/>, the fill from rim to its deep shade.</summary>
    private void TickGhost(Afterimage g, float op, float inkK)
    {
        var t = Mathf.Clamp01(g.age / g.life);
        var hold = t < 0.3f ? 1f : 1f - (t - 0.3f) / 0.7f;
        var fade = hold <= 0.01f ? 0f : Mathf.Ceil(hold * 3f) / 3f;
        var rim = Color.Lerp(Pale(g.rim), g.rim, Mathf.Clamp01(t * 1.6f)) * 1.4f;
        var fill = Color.Lerp(g.rim, Deep(g.rim), t);
        ghostBlock.Clear();
        Paint(ghostBlock, fill, rim, fade * op, Mathf.Clamp01((t - 0.4f) / 0.6f) * 0.92f, inkK);
        g.rend.SetPropertyBlock(ghostBlock);
    }

    /// <summary>Solve the uniform scale + offset that maps BakeMesh output onto the
    /// hand-skinned highest/lowest bind-pose vertices (TraversalEffects.CalibrateBake).
    /// Expected: scale 1, offset 0 — anything else is logged once. Unreadable meshes
    /// keep the documented frame.</summary>
    private static BakeFrame Calibrate(SkinnedMeshRenderer smr, Mesh baked)
    {
        var frame = new BakeFrame { scale = 1f, offset = Vector3.zero };
        var src = smr.sharedMesh;
        var bones = smr.bones;
        if (src == null || !src.isReadable || bones == null || bones.Length == 0) return frame;
        var srcVerts = src.vertices;
        var bakedVerts = baked.vertices;
        if (srcVerts.Length == 0 || srcVerts.Length != bakedVerts.Length) return frame;
        var bind = src.bindposes;
        var counts = src.GetBonesPerVertex();
        var weights = src.GetAllBoneWeights();
        if (counts.Length != srcVerts.Length) return frame;
        int hi = 0, lo = 0;
        for (var i = 1; i < srcVerts.Length; i++)
        {
            if (srcVerts[i].y > srcVerts[hi].y) hi = i;
            if (srcVerts[i].y < srcVerts[lo].y) lo = i;
        }
        Vector3 Skin(int v)
        {
            var start = 0;
            for (var i = 0; i < v; i++) start += counts[i];
            var sum = Vector3.zero;
            var total = 0f;
            for (var k = 0; k < counts[v]; k++)
            {
                var w = weights[start + k];
                if (w.boneIndex >= bones.Length || w.boneIndex >= bind.Length || bones[w.boneIndex] == null) continue;
                sum += w.weight * (bones[w.boneIndex].localToWorldMatrix * bind[w.boneIndex]).MultiplyPoint3x4(srcVerts[v]);
                total += w.weight;
            }
            return total > 1e-4f ? sum / total : sum;
        }
        Vector3 w1 = Skin(hi), w2 = Skin(lo), b1 = bakedVerts[hi], b2 = bakedVerts[lo];
        var span = (b1 - b2).magnitude;
        if (span < 1e-5f) return frame;
        var r = smr.transform.rotation;
        var p = smr.transform.position;
        var s = (w1 - w2).magnitude / span;
        var off = Quaternion.Inverse(r) * (w1 - (p + r * (b1 * s)));
        var residual = (p + r * (b2 * s + off) - w2).magnitude;
        if (residual > 0.05f * Mathf.Max(1f, (w1 - w2).magnitude / 2f)) return frame;
        frame.scale = Mathf.Abs(s - 1f) < 0.01f ? 1f : s;
        frame.offset = off.magnitude < 0.005f * Mathf.Max(1f, s) ? Vector3.zero : off;
        if (frame.scale != 1f || frame.offset != Vector3.zero)
            Debug.Log($"[WardenFx] Afterimage bake frame for {smr.name}: scale x{frame.scale:0.###}, offset {frame.offset.ToString("F3")}.");
        return frame;
    }

    // ------------------------------------------------------------------ tick

    private void LateUpdate()
    {
        var dt = Time.deltaTime;
        var udt = Time.unscaledDeltaTime;
        var op = Opacity;
        var inkK = InkStrength;
        foreach (var s in pens)
        {
            if (!s.Live)
            {
                if (s.line.enabled) s.line.enabled = s.ink.enabled = false;
                continue;
            }
            s.age += s.unscaled ? udt : dt;
            var t = Mathf.Clamp01(s.age / s.life);
            switch (s.kind)
            {
                case PenKind.Ring: TickRing(s, t, op, inkK); break;
                case PenKind.Line: TickLine(s, t, op, inkK); break;
                case PenKind.Spike: TickSpike(s, t, op, inkK); break;
                default: TickSigil(s, t, op, inkK); break;
            }
        }
        foreach (var g in ghosts)
        {
            if (!g.Live) continue;
            g.age += dt;
            if (!g.Live) { g.go.SetActive(false); continue; }
            TickGhost(g, op, inkK);
        }
    }

    private static void TickRing(Pen s, float t, float op, float inkK)
    {
        var fade = t < s.hold ? 1f : 1f - (t - s.hold) / Mathf.Max(0.001f, 1f - s.hold);
        var a = Stepped(fade) * s.col.a * op;
        // Ease-out expansion so the facets snap open then settle.
        var e = 1f - (1f - t) * (1f - t);
        Polygon(s, s.r0 + s.expand * e, s.spin);
        var w = s.width * (1f - 0.5f * t);
        Push(s, s.sides + 1, Glow(s.col, a), w, w, w * 2.3f, w * 2.3f, a * inkK);
    }

    private static void TickSigil(Pen s, float t, float op, float inkK)
    {
        // Pop in over 0.06s, hold, then click down in four steps.
        var grow = Mathf.Clamp01(s.age / 0.06f);
        var fade = t < 0.35f ? 1f : 1f - (t - 0.35f) / 0.65f;
        var r = s.r0 * (0.7f + 0.3f * grow) * (1f - 0.1f * t) * (s.inner ? 0.42f : 1f);
        Polygon(s, r, s.spin);
        var a = Stepped(fade) * op * (s.inner ? 1f : s.col.a);
        var c = s.inner ? (t < 0.2f ? Color.white : Pale(s.col)) : s.col;
        Push(s, s.sides + 1, Glow(c, a), s.width, s.width, s.width * 2.3f, s.width * 2.3f, a * inkK);
    }

    private static void TickSpike(Pen s, float t, float op, float inkK)
    {
        var head = 1f - (1f - t) * (1f - t) * (1f - t);
        var tail = t * t;
        s.pts[0] = Vector3.LerpUnclamped(s.from, s.to, 0.12f + 0.5f * tail);
        s.pts[1] = Vector3.LerpUnclamped(s.from, s.to, 0.3f + 0.7f * head);
        var a = Stepped(1f - t) * s.col.a * op;
        var w = s.width * Mathf.Lerp(1f, 0.24f, t);
        Push(s, 2, Glow(s.col, a), w, w * s.taper, w * 2.2f, w * Mathf.Max(0.8f, s.taper * 2.2f), a * inkK);
    }

    private static void TickLine(Pen s, float t, float op, float inkK)
    {
        var fade = t < s.hold ? 1f : 1f - (t - s.hold) / Mathf.Max(0.001f, 1f - s.hold);
        var a = Stepped(fade) * s.col.a * op;
        var total = s.lens[s.count - 1];
        var g = s.grow > 0f ? Mathf.Clamp01(s.age / s.grow) : 1f;
        var reach = total * g;
        var n = 1;
        while (n < s.count && s.lens[n] <= reach) n++;
        // Interpolated head point so the line draws on smoothly.
        var buf = s.pts;
        var drawn = Mathf.Min(n + 1, s.count);
        var saved = buf[drawn - 1];
        if (n < s.count)
        {
            var seg = s.lens[n] - s.lens[n - 1];
            buf[drawn - 1] = Vector3.Lerp(buf[n - 1], buf[n], seg > 0f ? (reach - s.lens[n - 1]) / seg : 1f);
        }
        var w0 = s.width;
        var w1 = s.width * Mathf.Lerp(1f, s.taper, total > 0f ? reach / total : 1f);
        Push(s, drawn, Glow(s.col, a), w0, w1, w0 * 2.3f, w1 * 2.3f, a * inkK);
        buf[drawn - 1] = saved;
    }

    private static void Polygon(Pen s, float radius, float spin)
    {
        var rot = Quaternion.FromToRotation(Vector3.up, s.normal);
        for (var i = 0; i <= s.sides; i++)
        {
            var ang = spin + i * Mathf.PI * 2f / s.sides;
            s.pts[i] = s.centre + rot * new Vector3(Mathf.Cos(ang) * radius, 0f, Mathf.Sin(ang) * radius);
        }
    }

    private static void Push(Pen s, int count, Color c, float w0, float w1, float ink0, float ink1, float inkA)
    {
        var on = c.a > 0.005f && count >= 2;
        s.line.enabled = s.ink.enabled = on;
        if (!on) return;
        s.line.positionCount = s.ink.positionCount = count;
        s.line.SetPositions(s.pts);
        s.ink.SetPositions(s.pts);
        s.line.startColor = s.line.endColor = c;
        s.line.startWidth = w0;
        s.line.endWidth = w1;
        var k = Ink;
        k.a = inkA;
        s.ink.startColor = s.ink.endColor = k;
        s.ink.startWidth = ink0;
        s.ink.endWidth = ink1;
    }

    private void OnDestroy()
    {
        if (host == this) host = null;
        foreach (var g in ghosts) if (g.baked != null) Destroy(g.baked);
        if (glow != null) Destroy(glow);
        if (afterimage != null) Destroy(afterimage);
    }

    // ------------------------------------------------------------------ meshes

    /// <summary>The spectral blade — a faceted sword, pivot at the grip, +Y to the tip.
    /// Bright crimson edges over a dark spine: reads at 640 lines without bloom.</summary>
    public static Mesh BladeMesh
    {
        get
        {
            if (bladeMesh != null) return bladeMesh;
            var b = new FacetBuilder();
            var edge = new Color(1f, 0.1f, 0.12f, 0.95f);
            var spine = new Color(0.22f, 0.01f, 0.04f, 0.85f);
            var dark = new Color(0.08f, 0.01f, 0.02f, 0.95f);
            // Blade: diamond cross-section rings at three heights, closed by the tip.
            float[] ys = { 0.24f, 0.95f, 1.3f };
            float[] ws = { 0.075f, 0.068f, 0.06f };
            const float t = 0.022f;
            var rings = new Vector3[ys.Length][];
            for (var i = 0; i < ys.Length; i++)
                rings[i] = new[] { new Vector3(ws[i], ys[i], 0f), new Vector3(0f, ys[i], t), new Vector3(-ws[i], ys[i], 0f), new Vector3(0f, ys[i], -t) };
            Color C(int k) => k % 2 == 0 ? edge : spine;
            for (var i = 0; i < rings.Length - 1; i++)
                for (var k = 0; k < 4; k++)
                {
                    var k2 = (k + 1) % 4;
                    b.Quad(rings[i][k], rings[i][k2], rings[i + 1][k2], rings[i + 1][k], C(k), C(k2), C(k2), C(k));
                }
            var tip = new Vector3(0f, 1.55f, 0f);
            for (var k = 0; k < 4; k++)
            {
                var k2 = (k + 1) % 4;
                b.Tri(rings[2][k], rings[2][k2], tip, C(k), C(k2), edge);
            }
            for (var k = 1; k < 3; k++) b.Tri(rings[0][0], rings[0][k], rings[0][k + 1], dark, dark, dark);
            b.Box(new Vector3(0f, 0.21f, 0f), new Vector3(0.25f, 0.035f, 0.045f), dark, edge);
            b.Box(new Vector3(0f, 0.07f, 0f), new Vector3(0.035f, 0.11f, 0.035f), dark, dark);
            b.Box(new Vector3(0f, -0.06f, 0f), new Vector3(0.05f, 0.03f, 0.05f), dark, edge);
            bladeMesh = b.ToMesh("Warden spectral blade");
            return bladeMesh;
        }
    }

    /// <summary>The player's chip: an irregular five-sided fan, ~1 unit across, normals +Z.</summary>
    private static Mesh ShardMesh
    {
        get
        {
            if (shardMesh != null) return shardMesh;
            shardMesh = new Mesh { name = "Warden shard" };
            shardMesh.vertices = new[] { Vector3.zero, new Vector3(0.04f, 0.52f, 0.03f), new Vector3(-0.4f, 0.2f, -0.04f),
                                         new Vector3(-0.3f, -0.38f, 0.05f), new Vector3(0.24f, -0.44f, -0.03f), new Vector3(0.47f, 0.06f, 0.02f) };
            shardMesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            shardMesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 1 };
            shardMesh.RecalculateBounds();
            return shardMesh;
        }
    }

    /// <summary>Eruption spike — a five-sided faceted pyramid, base radius 1 at y=0, apex
    /// at y=1, in WIRE format (COLOR rg = barycentric corner, b = visible-edge bits, the
    /// encoding WardenBody's wire meshes use). Draw it with <see cref="AfterimageMaterial"/>
    /// + <see cref="PaintAfterimage"/>: ink silhouette, rim band, faint fill, inked facets.
    /// Outward winding (the shader culls back faces) and smooth cone normals, so the
    /// fresnel bands wrap the silhouette instead of flat-shading whole faces.</summary>
    public static Mesh SpikeMesh
    {
        get
        {
            if (spikeMesh != null) return spikeMesh;
            const int n = 5;
            var apex = new Vector3(0.08f, 1f, -0.05f);
            var radial = new Vector3[n];
            var ring = new Vector3[n];
            for (var i = 0; i < n; i++)
            {
                var a = i * Mathf.PI * 2f / n;
                radial[i] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                ring[i] = radial[i] * (i % 2 == 0 ? 1f : 0.8f);
            }
            Vector3 Cone(Vector3 r) => (r + Vector3.up).normalized;
            var w = new WireMesh();
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                w.Tri(ring[j], ring[i], apex, Cone(radial[j]), Cone(radial[i]), Cone((radial[i] + radial[j]).normalized), 7f);
            }
            // Base fan (faces down): only the pentagon outline is inked.
            for (var i = 1; i < n - 1; i++)
            {
                var bits = 1f + (i == n - 2 ? 2f : 0f) + (i == 1 ? 4f : 0f);
                w.Tri(ring[0], ring[i], ring[i + 1], Vector3.down, Vector3.down, Vector3.down, bits);
            }
            spikeMesh = w.Build("Warden eruption spike");
            return spikeMesh;
        }
    }

    /// <summary>Unwelded wire-format mesh: every triangle carries barycentric corners in
    /// COLOR.rg and its visible-edge bits in COLOR.b (1 = edge BC, 2 = CA, 4 = AB).</summary>
    private sealed class WireMesh
    {
        private readonly List<Vector3> v = new List<Vector3>();
        private readonly List<Vector3> nrm = new List<Vector3>();
        private readonly List<Color> c = new List<Color>();
        private readonly List<int> t = new List<int>();

        public void Tri(Vector3 a, Vector3 b, Vector3 d, Vector3 na, Vector3 nb, Vector3 nd, float bits)
        {
            Add(a, na, new Color(1f, 0f, bits, 1f));
            Add(b, nb, new Color(0f, 1f, bits, 1f));
            Add(d, nd, new Color(0f, 0f, bits, 1f));
        }

        private void Add(Vector3 p, Vector3 n, Color col)
        {
            t.Add(v.Count);
            v.Add(p);
            nrm.Add(n);
            c.Add(col);
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetNormals(nrm);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>Unit 24-gon disc in XZ (fan) — faint marker fills.</summary>
    public static Mesh DiscMesh
    {
        get
        {
            if (discMesh != null) return discMesh;
            var b = new FacetBuilder();
            const int n = 24;
            for (var i = 0; i < n; i++)
            {
                var a0 = i * Mathf.PI * 2f / n;
                var a1 = (i + 1) * Mathf.PI * 2f / n;
                b.Tri(Vector3.zero, new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)), new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)),
                      Color.white, Color.white, Color.white);
            }
            discMesh = b.ToMesh("Warden disc");
            return discMesh;
        }
    }

    /// <summary>Unit quad x∈[-0.5,0.5], z∈[0,1] in XZ — line-marker fills.</summary>
    public static Mesh QuadMesh
    {
        get
        {
            if (quadMesh != null) return quadMesh;
            var b = new FacetBuilder();
            b.Quad(new Vector3(-0.5f, 0f, 0f), new Vector3(-0.5f, 0f, 1f), new Vector3(0.5f, 0f, 1f), new Vector3(0.5f, 0f, 0f),
                   Color.white, Color.white, Color.white, Color.white);
            quadMesh = b.ToMesh("Warden quad");
            return quadMesh;
        }
    }

    /// <summary>Unwelded flat-faceted mesh accumulator (vertex colours, no UVs).</summary>
    public sealed class FacetBuilder
    {
        private readonly List<Vector3> v = new List<Vector3>();
        private readonly List<Color> c = new List<Color>();
        private readonly List<int> t = new List<int>();

        public void Tri(Vector3 a, Vector3 b, Vector3 d, Color ca, Color cb, Color cd)
        {
            var i = v.Count;
            v.Add(a); v.Add(b); v.Add(d);
            c.Add(ca); c.Add(cb); c.Add(cd);
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 d, Vector3 e, Color ca, Color cb, Color cd, Color ce)
        {
            Tri(a, b, d, ca, cb, cd);
            Tri(a, d, e, ca, cd, ce);
        }

        /// <summary>Axis-aligned box: <paramref name="side"/> on the faces, <paramref name="tip"/> on the ±X ends.</summary>
        public void Box(Vector3 centre, Vector3 half, Color side, Color tip)
        {
            var p = new Vector3[8];
            for (var i = 0; i < 8; i++)
                p[i] = centre + new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
            Quad(p[2], p[3], p[7], p[6], side, side, side, side);   // top
            Quad(p[0], p[4], p[5], p[1], side, side, side, side);   // bottom
            Quad(p[0], p[2], p[6], p[4], tip, tip, tip, tip);       // -X
            Quad(p[1], p[5], p[7], p[3], tip, tip, tip, tip);       // +X
            Quad(p[0], p[1], p[3], p[2], side, side, side, side);   // -Z
            Quad(p[4], p[6], p[7], p[5], side, side, side, side);   // +Z
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}

/// <summary>
/// A held telegraph the caller drives every frame, drawn as a Core sigil: a faceted
/// ground circle (12-gon rim, 24 past 5 m, around an 8-gon core at 0.42r rotated π/8)
/// or a path strip (two ink-backed edge strokes with 8-gon ticks every ~2 m down the
/// centre line). Pops in like the player's stamps; SetPulse swells in four discrete
/// steps (the core/ticks heat to pale red, the rim widens a band). At most a very faint
/// stepped fill. Circles may face any normal (the wall-chase rings sit ON the wall).
/// Release() fades it out.
/// </summary>
public sealed class WardenMark : MonoBehaviour
{
    private const float PopTime = 0.06f;
    private const float Lift = 0.035f;
    private const int MaxTicks = 24;

    private sealed class Edge
    {
        public LineRenderer line, ink;
        public readonly Vector3[] pts = new Vector3[26];
        public int count;
    }

    private readonly List<Edge> edges = new List<Edge>();
    private MeshRenderer fill;
    private MaterialPropertyBlock mpb;
    private Color col = WardenFx.Crimson;
    private float alpha = 1f, fillAlpha = 0.16f, width = 0.09f, pulse, spin, popAge;
    private int maxSides = WardenFx.RingSides;
    private bool circle, releasing;
    private float releaseT, releaseDur;
    private Vector3 centre, normal = Vector3.up, from, to;
    private float radius, halfWidth;

    public static WardenMark Circle(Vector3 centre, float radius, Color col, float width = 0.09f, float fillAlpha = 0.16f,
                                    int sides = 40, Vector3? normal = null)
    {
        var m = Make("Warden circle", col, width, fillAlpha);
        m.circle = true;
        m.maxSides = sides;
        m.fill.GetComponent<MeshFilter>().sharedMesh = WardenFx.DiscMesh;
        m.AddEdge();   // rim
        m.AddEdge();   // core
        m.SetCircle(centre, radius, normal);
        return m;
    }

    public static WardenMark Line(Vector3 from, Vector3 to, float halfWidth, Color col, float width = 0.07f, float fillAlpha = 0.2f)
    {
        var m = Make("Warden path", col, width, fillAlpha);
        m.fill.GetComponent<MeshFilter>().sharedMesh = WardenFx.QuadMesh;
        m.AddEdge();   // left edge
        m.AddEdge();   // right edge
        m.SetLine(from, to, halfWidth);
        return m;
    }

    private static WardenMark Make(string name, Color col, float width, float fillAlpha)
    {
        var go = new GameObject(name);
        var m = go.AddComponent<WardenMark>();
        m.col = col;
        m.width = width;
        m.fillAlpha = fillAlpha;
        m.spin = Random.Range(-0.2f, 0.2f);
        m.mpb = new MaterialPropertyBlock();
        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(go.transform, false);
        fillGo.AddComponent<MeshFilter>();
        m.fill = fillGo.AddComponent<MeshRenderer>();
        m.fill.sharedMaterial = WardenFx.GlowMaterial;
        m.fill.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        m.fill.receiveShadows = false;
        m.fill.enabled = false;
        WardenHazard.Track(go);
        return m;
    }

    private Edge AddEdge()
    {
        var e = new Edge { ink = MakeLine("Ink", 1), line = MakeLine("Edge", 2) };
        edges.Add(e);
        return e;
    }

    private LineRenderer MakeLine(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = WardenFx.GlowMaterial;
        lr.useWorldSpace = true;
        lr.loop = false;
        lr.numCornerVertices = 0;
        lr.numCapVertices = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.sortingOrder = order;
        lr.enabled = false;
        return lr;
    }

    public void SetCircle(Vector3 centre, float radius, Vector3? normal = null)
    {
        var n = normal ?? Vector3.up;
        this.normal = n.sqrMagnitude > 1e-4f ? n.normalized : Vector3.up;
        this.centre = centre;
        this.radius = radius;
        Apply();
    }

    public void SetLine(Vector3 from, Vector3 to, float halfWidth)
    {
        this.from = from;
        this.to = to;
        this.halfWidth = halfWidth;
        Apply();
    }

    public void SetAlpha(float a) { alpha = Mathf.Clamp01(a); Apply(); }
    public void SetColor(Color c) { col = c; Apply(); }
    /// <summary>0..1 "about to fire" swell in four discrete steps: the rim widens and the core/ticks heat toward a hot crimson (pale red stays the impact peak's).</summary>
    public void SetPulse(float k) { pulse = Mathf.Clamp01(k); Apply(); }

    public void Release(float fade = 0.15f)
    {
        if (releasing) return;
        releasing = true;
        releaseDur = Mathf.Max(0.01f, fade);
        releaseT = 0f;
    }

    private void Update()
    {
        var dirty = false;
        if (releasing)
        {
            releaseT += Time.deltaTime;
            var k = 1f - releaseT / releaseDur;
            if (k <= 0f) { Destroy(gameObject); return; }
            alpha = Mathf.Min(alpha, k);
            dirty = true;
        }
        if (popAge < PopTime && WardenFx.Stepped(alpha) > 0f)
        {
            popAge += Time.deltaTime;
            dirty = true;
        }
        if (dirty) Apply();
    }

    private void Apply()
    {
        if (fill == null || edges.Count < 2) return;
        var stepped = WardenFx.Stepped(alpha);
        var a = stepped * col.a * WardenFx.Opacity;
        if (a <= 0.005f)
        {
            foreach (var e in edges) e.line.enabled = e.ink.enabled = false;
            fill.enabled = false;
            return;
        }
        var inkA = a * WardenFx.InkStrength;
        var q = Mathf.Floor(pulse * 4f + 0.001f) / 4f;
        var pop = 0.7f + 0.3f * Mathf.Clamp01(popAge / PopTime);
        var hot = Color.Lerp(col, Color.white, 0.3f * q);
        var fa = fillAlpha > 0f ? Mathf.Min(fillAlpha * 0.35f * (1f + 0.5f * q), 0.06f) * WardenFx.Opacity * stepped : 0f;
        fill.enabled = fa > 0.002f;
        if (circle)
        {
            var rot = Quaternion.FromToRotation(Vector3.up, normal);
            var c = centre + normal * Lift;
            var sides = Mathf.Clamp(maxSides, WardenFx.CoreSides, radius > 5f ? 24 : WardenFx.RingSides);
            var r = radius * pop;
            var w = Mathf.Max(width, 0.0825f * WardenFx.Family(radius));
            Polygon(edges[0], c, rot, r, sides, spin);
            Draw(edges[0], WardenFx.Glow(col, a), w * (1f + 0.35f * q), w * (1f + 0.35f * q), inkA);
            Polygon(edges[1], c, rot, r * 0.42f, WardenFx.CoreSides, spin + Mathf.PI / WardenFx.CoreSides);
            Draw(edges[1], WardenFx.Glow(hot, a * Mathf.Lerp(0.75f, 1f, q)), w * 0.8f, w * 0.8f, inkA);
            if (fill.enabled)
            {
                fill.transform.SetPositionAndRotation(c - normal * 0.005f, rot);
                var k = r * Mathf.Cos(Mathf.PI / sides);
                fill.transform.localScale = new Vector3(k, 1f, k);
            }
            for (var i = 2; i < edges.Count; i++) edges[i].line.enabled = edges[i].ink.enabled = false;
        }
        else
        {
            var dir = Vector3.ProjectOnPlane(to - from, Vector3.up);
            var len = dir.magnitude;
            if (len < 0.01f) { dir = Vector3.forward; len = 0.01f; }
            dir /= len;
            var side = Vector3.Cross(Vector3.up, dir) * halfWidth * pop;
            var lift = Vector3.up * Lift;
            var w = Mathf.Max(width, 0.06f) * (1f + 0.35f * q);
            Segment(edges[0], from + side + lift, from + side + dir * len + lift);
            Draw(edges[0], WardenFx.Glow(col, a), w, w * 0.7f, inkA);
            Segment(edges[1], from - side + lift, from - side + dir * len + lift);
            Draw(edges[1], WardenFx.Glow(col, a), w, w * 0.7f, inkA);
            // 8-gon ticks every ~2 m down the centre line (one at the middle of a short strip).
            var ticks = len < 2.4f ? 1 : Mathf.Min(MaxTicks, Mathf.FloorToInt((len - 1.4f) / 2f) + 1);
            while (edges.Count < 2 + ticks) AddEdge();
            var tr = Mathf.Clamp(halfWidth * 0.3f, 0.12f, 0.35f) * pop;
            var tickCol = WardenFx.Glow(hot, a * 0.85f);
            for (var i = 0; i < ticks; i++)
            {
                var d = ticks == 1 && len < 2.4f ? len * 0.5f : 1f + 2f * i;
                Polygon(edges[2 + i], from + dir * d + lift, Quaternion.identity, tr, WardenFx.CoreSides, Mathf.PI / WardenFx.CoreSides);
                Draw(edges[2 + i], tickCol, w * 0.6f, w * 0.6f, inkA);
            }
            for (var i = 2 + ticks; i < edges.Count; i++) edges[i].line.enabled = edges[i].ink.enabled = false;
            if (fill.enabled)
            {
                fill.transform.SetPositionAndRotation(from + lift * 0.85f, Quaternion.LookRotation(dir, Vector3.up));
                fill.transform.localScale = new Vector3(halfWidth * 2f * pop, 1f, len);
            }
        }
        if (fill.enabled)
        {
            var f = col;
            f.a = fa;
            mpb.SetColor(WardenFx.TintId, f);
            fill.SetPropertyBlock(mpb);
        }
    }

    private static void Polygon(Edge e, Vector3 c, Quaternion rot, float r, int sides, float spin)
    {
        e.count = sides + 1;
        for (var i = 0; i <= sides; i++)
        {
            var ang = spin + i * Mathf.PI * 2f / sides;
            e.pts[i] = c + rot * new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
        }
    }

    private static void Segment(Edge e, Vector3 a, Vector3 b)
    {
        e.count = 2;
        e.pts[0] = a;
        e.pts[1] = b;
    }

    private static void Draw(Edge e, Color c, float w0, float w1, float inkA)
    {
        e.line.positionCount = e.ink.positionCount = e.count;
        e.line.SetPositions(e.pts);
        e.ink.SetPositions(e.pts);
        e.line.startColor = e.line.endColor = c;
        e.line.startWidth = w0;
        e.line.endWidth = w1;
        var k = WardenFx.Ink;
        k.a = inkA;
        e.ink.startColor = e.ink.endColor = k;
        e.ink.startWidth = w0 * 2.3f;
        e.ink.endWidth = w1 * 2.3f;
        e.line.enabled = e.ink.enabled = true;
    }
}
