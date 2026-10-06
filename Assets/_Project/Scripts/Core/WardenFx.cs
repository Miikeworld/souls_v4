using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Warden fight's effect language — one meaning per colour:
///   crimson   = damage is about to happen HERE (markers, paths, eruptions);
///   purple    = the suit can use this (Corestone walls, the Core leap);
///   pale red  = a two-frame impact peak, then straight back to crimson.
/// Same vocabulary as TraversalEffects at arena scale: faceted polygons over an
/// ink underlay, four-band stepped fades, near-flat colour, low-poly stone chips
/// and chunky dust that clears inside a second so the next tell stays readable.
/// One lazily built host per scene; particles run on scaled time, so a hitstop
/// freezes the debris mid-flight.
/// </summary>
public sealed class WardenFx : MonoBehaviour
{
    public static readonly Color Crimson = new Color(1f, 0.06f, 0.09f, 1f);
    public static readonly Color CrimsonDeep = new Color(0.42f, 0.02f, 0.05f, 1f);
    public static readonly Color PaleRed = new Color(1f, 0.84f, 0.8f, 1f);
    public static readonly Color Ink = new Color(0.05f, 0.02f, 0.035f, 1f);
    public static readonly Color DustCol = new Color(0.42f, 0.39f, 0.38f, 1f);
    public static readonly Color StoneCol = new Color(0.36f, 0.34f, 0.35f, 1f);
    public static Color Purple => CoreConduit.Main;
    public static Color PurpleBright => CoreConduit.Bright;

    private const float InkStrength = 0.55f;
    private const float FreezeScale = 0.05f;

    private static WardenFx host;
    private static Mesh bladeMesh, chipMesh, dustMesh, shardMesh, discMesh, quadMesh;

    private Material glow;
    private ParticleSystem chips, dust, shardsFall, shardsRise;
    private readonly List<Stroke> strokes = new List<Stroke>();
    private readonly List<PeakDisc> peaks = new List<PeakDisc>();
    private PlayerCameraController cam;

    private sealed class Stroke
    {
        public LineRenderer line, ink;
        public Vector3[] pts = new Vector3[64];
        public float[] lens = new float[64];
        public int count;
        public bool ring, unscaled;
        public Vector3 centre, normal;
        public float r0, expand, width, age = 9f, life, hold, grow, spin;
        public int sides;
        public Color col;
        public bool Live => age < life;
    }

    /// <summary>The 2-frame pale disc — counted in rendered frames, not time.</summary>
    private sealed class PeakDisc
    {
        public MeshRenderer r;
        public int frames;
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

    private WardenFx Build()
    {
        var shader = Shader.Find("Souls/TraversalGlow") ?? Shader.Find("Sprites/Default");
        glow = new Material(shader) { name = "Warden glow (runtime)" };
        if (glow.HasProperty("_Tint")) glow.SetColor("_Tint", Color.white);
        chips = MakeSystem("Warden stone chips", ChipMesh, 1.6f, 400, 1.2f, false);
        dust = MakeSystem("Warden dust", DustMesh, -0.05f, 300, 4f, true);
        shardsFall = MakeSystem("Warden shards", ShardMesh, 1.1f, 400, 2f, false);
        shardsRise = MakeSystem("Warden rising shards", ShardMesh, -0.12f, 400, 1.4f, false);
        for (var i = 0; i < 48; i++) strokes.Add(NewStroke());
        for (var i = 0; i < 6; i++)
        {
            var go = new GameObject("Warden peak");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = DiscMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = glow;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            peaks.Add(new PeakDisc { r = r });
        }
        return this;
    }

    private ParticleSystem MakeSystem(string name, Mesh mesh, float gravity, int max, float drag, bool grow)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = max;
        main.startSpeed = 0f;
        main.startLifetime = 1f;
        main.startRotation3D = true;
        main.gravityModifier = gravity;
        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;
        // Four-band stepped fade (Fixed gradient = hard steps) — the banded read.
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient { mode = GradientMode.Fixed };
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(0.75f, 0.55f),
                          new GradientAlphaKey(0.45f, 0.8f), new GradientAlphaKey(0.2f, 1f) });
        col.color = g;
        var rot = ps.rotationOverLifetime;
        rot.enabled = true;
        rot.separateAxes = true;
        rot.x = new ParticleSystem.MinMaxCurve(-7f, 7f);
        rot.y = new ParticleSystem.MinMaxCurve(-7f, 7f);
        rot.z = new ParticleSystem.MinMaxCurve(-5f, 5f);
        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.drag = drag;
        if (grow)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.55f, 1f, 1.35f));
        }
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Mesh;
        r.mesh = mesh;
        r.alignment = ParticleSystemRenderSpace.World;
        r.sharedMaterial = glow;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        ps.Play();
        return ps;
    }

    private Stroke NewStroke()
    {
        var s = new Stroke { ink = MakeLine("Warden stroke ink", 0), line = MakeLine("Warden stroke", 1) };
        return s;
    }

    private LineRenderer MakeLine(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = glow;
        lr.useWorldSpace = true;
        lr.numCornerVertices = 0;
        lr.numCapVertices = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.sortingOrder = order;
        lr.enabled = false;
        return lr;
    }

    private Stroke Acquire()
    {
        Stroke pick = null;
        foreach (var s in strokes) if (!s.Live) { pick = s; break; }
        if (pick == null)
        {
            if (strokes.Count < 160) { pick = NewStroke(); strokes.Add(pick); }
            else { pick = strokes[0]; foreach (var s in strokes) if (s.age / s.life > pick.age / pick.life) pick = s; }
        }
        pick.age = 0f;
        return pick;
    }

    // ------------------------------------------------------------------ public vocabulary

    /// <summary>Four-band stepped alpha — fades click down like the banded lighting.</summary>
    public static float Stepped(float a) => a <= 0.01f ? 0f : Mathf.Ceil(Mathf.Clamp01(a) * 4f) / 4f;

    /// <summary>Faceted ring over ink, expanding by <paramref name="expand"/> metres and stepping out.</summary>
    public static void Ring(Vector3 centre, Vector3 normal, float radius, float expand, float life, Color col,
                            float width = 0.1f, int sides = 24, float hold = 0.2f, bool unscaled = false)
    {
        var s = Host.Acquire();
        s.ring = true;
        s.centre = centre;
        s.normal = normal.sqrMagnitude < 0.01f ? Vector3.up : normal.normalized;
        s.r0 = radius;
        s.expand = expand;
        s.life = Mathf.Max(0.03f, life);
        s.width = width;
        s.sides = Mathf.Clamp(sides, 3, 63);
        s.col = col;
        s.hold = hold;
        s.grow = 0f;
        s.spin = Random.value * Mathf.PI;
        s.unscaled = unscaled;
    }

    /// <summary>Ink-backed polyline drawn on over <paramref name="grow"/> seconds — cracks,
    /// spike strokes, scan lines. Points are copied.</summary>
    public static void Line(IList<Vector3> points, Color col, float width, float life, float grow = 0f,
                            float hold = 0.4f, bool unscaled = false)
    {
        if (points == null || points.Count < 2) return;
        var s = Host.Acquire();
        s.ring = false;
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
        s.hold = hold;
        s.grow = grow;
        s.unscaled = unscaled;
    }

    /// <summary>Radial impact star in the camera plane — ink-backed spikes shooting out.</summary>
    public static void Spikes(Vector3 point, int count, float length, Color col, float life = 0.22f, float width = 0.09f)
    {
        var c = Camera.main;
        var n = c != null ? (c.transform.position - point).normalized : Vector3.up;
        var basis = Vector3.ProjectOnPlane(Vector3.up, n);
        if (basis.sqrMagnitude < 0.01f) basis = Vector3.ProjectOnPlane(Vector3.right, n);
        basis.Normalize();
        var spin = Random.value * 360f;
        var pts = new Vector3[2];
        for (var i = 0; i < count; i++)
        {
            var dir = Quaternion.AngleAxis(spin + i * 360f / count + Random.Range(-10f, 10f), n) * basis;
            var len = length * Random.Range(0.7f, 1.2f);
            pts[0] = point + dir * len * 0.2f;
            pts[1] = point + dir * len;
            Line(pts, i % 3 == 0 ? Color.white : col, width, life, life * 0.35f, 0.1f, true);
        }
    }

    /// <summary>Jagged branching cracks on the floor plane through <paramref name="origin"/>.</summary>
    public static void Cracks(Vector3 origin, int count, float length, Color col, float grow = 0.15f,
                              float life = 1.3f, Vector3? along = null, float spread = 360f, float width = 0.07f)
    {
        var y = origin.y + 0.035f;
        var baseDir = along.HasValue ? Vector3.ProjectOnPlane(along.Value, Vector3.up).normalized : Vector3.forward;
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
                    Line(b, col, width * 0.7f, life, grow, 0.45f);
                }
            }
            Line(pts, col, width, life, grow, 0.45f);
        }
    }

    /// <summary>Low-poly stone chips thrown out of an impact.</summary>
    public static void Debris(Vector3 point, int count, float speed, float size = 1f, Vector3? bias = null)
    {
        var h = Host;
        for (var i = 0; i < count; i++)
        {
            var d = Random.insideUnitSphere;
            d.y = Mathf.Abs(d.y) * 0.8f + 0.45f;
            if (bias.HasValue) d += bias.Value;
            var shade = Random.Range(0.8f, 1.15f);
            var c = StoneCol * shade; c.a = 1f;
            h.chips.Emit(new ParticleSystem.EmitParams
            {
                position = point + Random.insideUnitSphere * 0.2f,
                velocity = d.normalized * speed * Random.Range(0.55f, 1f),
                startColor = c,
                startSize = Random.Range(0.12f, 0.26f) * size,
                rotation3D = new Vector3(Random.value, Random.value, Random.value) * 360f,
                startLifetime = Random.Range(0.6f, 0.95f),
            }, 1);
        }
    }

    /// <summary>Chunky stylised dust puffs — dense at the contact, gone by ~0.8s.</summary>
    public static void Dust(Vector3 point, int count, float spread, float size = 1f, Color? tint = null, float rise = 0.5f)
    {
        var h = Host;
        var col = tint ?? DustCol;
        for (var i = 0; i < count; i++)
        {
            var d = Random.insideUnitCircle.normalized;
            var v = new Vector3(d.x, 0f, d.y) * spread * Random.Range(0.6f, 1.2f) + Vector3.up * rise * Random.Range(0.6f, 1.4f);
            var c = col * Random.Range(0.85f, 1.1f);
            c.a = 0.6f;
            h.dust.Emit(new ParticleSystem.EmitParams
            {
                position = point + new Vector3(d.x, 0.15f, d.y) * 0.3f,
                velocity = v,
                startColor = c,
                startSize = Random.Range(0.45f, 0.8f) * size,
                rotation3D = new Vector3(Random.value, Random.value, Random.value) * 360f,
                startLifetime = Random.Range(0.55f, 0.8f),
            }, 1);
        }
    }

    /// <summary>Faceted Core shards. <paramref name="rise"/> = drift upward (gravity is
    /// breaking) instead of falling.</summary>
    public static void Shards(Vector3 point, int count, float speed, Color col, bool rise = false,
                              float size = 1f, float life = 0.8f, Vector3? bias = null, float radius = 0.2f)
    {
        var h = Host;
        var ps = rise ? h.shardsRise : h.shardsFall;
        for (var i = 0; i < count; i++)
        {
            var d = Random.insideUnitSphere;
            if (rise) d.y = Mathf.Abs(d.y) + 0.6f;
            if (bias.HasValue) d += bias.Value;
            var c = i % 4 == 0 ? Color.Lerp(col, Color.white, 0.45f) : col;
            ps.Emit(new ParticleSystem.EmitParams
            {
                position = point + Random.insideUnitSphere * radius,
                velocity = d.normalized * speed * Random.Range(0.45f, 1f),
                startColor = c,
                startSize = Random.Range(0.08f, 0.16f) * size,
                rotation3D = new Vector3(Random.value, Random.value, Random.value) * 360f,
                startLifetime = Random.Range(life * 0.6f, life),
            }, 1);
        }
    }

    /// <summary>Shards born around <paramref name="point"/> flying IN to it over
    /// <paramref name="time"/> — a blade or the Core gathering itself.</summary>
    public static void Converge(Vector3 point, int count, float radius, float time, Color col, float size = 1f)
    {
        var h = Host;
        time = Mathf.Max(0.05f, time);
        for (var i = 0; i < count; i++)
        {
            var off = Random.onUnitSphere * radius * Random.Range(0.6f, 1f);
            h.shardsFall.Emit(new ParticleSystem.EmitParams
            {
                position = point + off,
                velocity = -off / time,
                startColor = i % 3 == 0 ? Color.Lerp(col, Color.white, 0.5f) : col,
                startSize = Random.Range(0.07f, 0.13f) * size,
                rotation3D = new Vector3(Random.value, Random.value, Random.value) * 360f,
                startLifetime = time,
            }, 1);
        }
    }

    /// <summary>The pale-red peak: a camera-facing faceted disc that lives exactly two
    /// rendered frames, then the crimson ring carries the read.</summary>
    public static void Peak(Vector3 point, float radius)
    {
        var h = Host;
        PeakDisc pick = null;
        foreach (var p in h.peaks) if (p.frames <= 0) { pick = p; break; }
        if (pick == null) pick = h.peaks[0];
        var c = Camera.main;
        var rot = c != null ? Quaternion.LookRotation(point - c.transform.position) * Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
        pick.r.transform.SetPositionAndRotation(point, rot);
        pick.r.transform.localScale = Vector3.one * radius;
        var mpb = new MaterialPropertyBlock();
        var col = PaleRed; col.a = 0.9f;
        mpb.SetColor("_Tint", col);
        pick.r.SetPropertyBlock(mpb);
        pick.r.enabled = true;
        pick.frames = 2;
    }

    /// <summary>The full layered impact in the design's order: contact flash →
    /// debris (0.05s) → dense dust (0.1s) → red Core cracks spreading; shake and
    /// hitstop land ON contact, never before it.</summary>
    public static void Impact(Vector3 point, float scale, float shake = 0f, float hitstop = 0f,
                              int debris = 8, float dust = 1f, float cracks = 1f, Color? crackCol = null)
    {
        Host.StartCoroutine(Host.ImpactRoutine(point, scale, shake, hitstop, debris, dust, cracks, crackCol ?? Crimson));
    }

    private IEnumerator ImpactRoutine(Vector3 p, float scale, float shake, float hitstop, int debris, float dustK,
                                      float crackK, Color crackCol)
    {
        Peak(p + Vector3.up * 0.45f * scale, 0.9f * scale);
        Ring(p + Vector3.up * 0.04f, Vector3.up, 0.3f * scale, 1.6f * scale, 0.18f, PaleRed, 0.12f * Mathf.Sqrt(scale), 24, 0.1f);
        Ring(p + Vector3.up * 0.05f, Vector3.up, 0.5f * scale, 2.6f * scale, 0.42f, Crimson, 0.14f * Mathf.Sqrt(scale), 24, 0.25f);
        if (scale >= 1.4f) Spikes(p + Vector3.up * 0.6f * scale, 9, 1.3f * scale, Crimson, 0.24f, 0.11f);
        if (shake > 0f) Shake(shake);
        if (hitstop > 0f) HitStop(hitstop);
        yield return new WaitForSeconds(0.05f);
        if (debris > 0) Debris(p + Vector3.up * 0.1f, Mathf.RoundToInt(debris * Mathf.Clamp(scale, 0.6f, 1.8f)), 6.5f * Mathf.Sqrt(scale), Mathf.Sqrt(scale));
        yield return new WaitForSeconds(0.05f);
        if (dustK > 0f) Dust(p, Mathf.RoundToInt(10 * dustK * Mathf.Clamp(scale, 0.6f, 1.8f)), 2.2f * Mathf.Sqrt(scale), Mathf.Sqrt(scale));
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

    // ------------------------------------------------------------------ tick

    private void LateUpdate()
    {
        var dt = Time.deltaTime;
        var udt = Time.unscaledDeltaTime;
        foreach (var s in strokes)
        {
            if (!s.Live)
            {
                if (s.line.enabled) s.line.enabled = s.ink.enabled = false;
                continue;
            }
            s.age += s.unscaled ? udt : dt;
            var t = Mathf.Clamp01(s.age / s.life);
            var fade = t < s.hold ? 1f : 1f - (t - s.hold) / Mathf.Max(0.001f, 1f - s.hold);
            var a = Stepped(fade) * s.col.a;
            if (s.ring)
            {
                var k = 1f - (1f - t) * (1f - t);
                var r = s.r0 + s.expand * k;
                var rot = Quaternion.FromToRotation(Vector3.up, s.normal);
                for (var i = 0; i <= s.sides; i++)
                {
                    var ang = s.spin + i * Mathf.PI * 2f / s.sides;
                    s.pts[i] = s.centre + rot * new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
                }
                Push(s, s.sides + 1, a, s.width * Mathf.Lerp(1f, 0.6f, t));
            }
            else
            {
                var total = s.lens[s.count - 1];
                var g = s.grow > 0f ? Mathf.Clamp01(s.age / s.grow) : 1f;
                var reach = total * g;
                var n = 1;
                while (n < s.count && s.lens[n] <= reach) n++;
                // Interpolated head point so the crack draws on smoothly.
                var buf = s.pts;
                Vector3 head;
                if (n < s.count)
                {
                    var seg = s.lens[n] - s.lens[n - 1];
                    head = Vector3.Lerp(buf[n - 1], buf[n], seg > 0f ? (reach - s.lens[n - 1]) / seg : 1f);
                }
                else head = buf[s.count - 1];
                var drawn = Mathf.Min(n + 1, s.count);
                var savedHead = buf[drawn - 1];
                buf[drawn - 1] = n < s.count ? head : buf[drawn - 1];
                Push(s, drawn, a, s.width);
                buf[drawn - 1] = savedHead;
            }
        }
        foreach (var p in peaks)
        {
            if (p.frames <= 0) continue;
            if (--p.frames <= 0) p.r.enabled = false;
        }
    }

    private static void Push(Stroke s, int count, float alpha, float width)
    {
        var on = alpha > 0.005f && count >= 2;
        s.line.enabled = s.ink.enabled = on;
        if (!on) return;
        s.line.positionCount = count;
        s.ink.positionCount = count;
        for (var i = 0; i < count; i++)
        {
            s.line.SetPosition(i, s.pts[i]);
            s.ink.SetPosition(i, s.pts[i]);
        }
        var c = s.col; c.a = alpha;
        s.line.startColor = s.line.endColor = c;
        s.line.startWidth = s.line.endWidth = width;
        var k = Ink; k.a = alpha * InkStrength;
        s.ink.startColor = s.ink.endColor = k;
        s.ink.startWidth = s.ink.endWidth = width * 2.3f;
    }

    private void OnDestroy()
    {
        if (host == this) host = null;
        if (glow != null) Destroy(glow);
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

    private static Mesh ChipMesh
    {
        get
        {
            if (chipMesh != null) return chipMesh;
            var b = new FacetBuilder();
            // Irregular hexahedron — a broken stone flake, face-shaded in vertex colour.
            Vector3[] c =
            {
                new Vector3(-0.5f, -0.42f, -0.46f), new Vector3(0.48f, -0.5f, -0.4f), new Vector3(0.42f, -0.38f, 0.5f), new Vector3(-0.46f, -0.5f, 0.38f),
                new Vector3(-0.36f, 0.44f, -0.5f), new Vector3(0.5f, 0.32f, -0.3f), new Vector3(0.3f, 0.5f, 0.42f), new Vector3(-0.5f, 0.36f, 0.3f),
            };
            Color S(float k) => new Color(k, k, k, 1f);
            b.Quad(c[4], c[5], c[6], c[7], S(1f), S(1f), S(1f), S(1f));
            b.Quad(c[0], c[3], c[2], c[1], S(0.45f), S(0.45f), S(0.45f), S(0.45f));
            b.Quad(c[0], c[1], c[5], c[4], S(0.72f), S(0.72f), S(0.72f), S(0.72f));
            b.Quad(c[1], c[2], c[6], c[5], S(0.6f), S(0.6f), S(0.6f), S(0.6f));
            b.Quad(c[2], c[3], c[7], c[6], S(0.78f), S(0.78f), S(0.78f), S(0.78f));
            b.Quad(c[3], c[0], c[4], c[7], S(0.55f), S(0.55f), S(0.55f), S(0.55f));
            chipMesh = b.ToMesh("Warden stone chip");
            return chipMesh;
        }
    }

    private static Mesh DustMesh
    {
        get
        {
            if (dustMesh != null) return dustMesh;
            var b = new FacetBuilder();
            // Squashed octahedron — a chunky low-poly puff, light on top.
            var top = new Vector3(0f, 0.42f, 0f);
            var bottom = new Vector3(0f, -0.3f, 0f);
            var ring = new Vector3[6];
            for (var i = 0; i < 6; i++)
            {
                var a = i * Mathf.PI / 3f + 0.2f;
                ring[i] = new Vector3(Mathf.Cos(a) * 0.5f, (i % 2 == 0 ? 0.05f : -0.05f), Mathf.Sin(a) * 0.5f);
            }
            for (var i = 0; i < 6; i++)
            {
                var j = (i + 1) % 6;
                var lit = new Color(1f, 1f, 1f, 1f) * (0.85f + 0.15f * Mathf.Cos(i));
                lit.a = 1f;
                b.Tri(ring[i], top, ring[j], lit, Color.white, lit);
                var shade = new Color(0.62f, 0.6f, 0.6f, 1f);
                b.Tri(ring[j], bottom, ring[i], shade, shade, shade);
            }
            dustMesh = b.ToMesh("Warden dust puff");
            return dustMesh;
        }
    }

    private static Mesh ShardMesh
    {
        get
        {
            if (shardMesh != null) return shardMesh;
            var b = new FacetBuilder();
            var o = Vector3.zero;
            Vector3[] r = { new Vector3(0.04f, 0.52f, 0.03f), new Vector3(-0.4f, 0.2f, -0.04f), new Vector3(-0.3f, -0.38f, 0.05f), new Vector3(0.24f, -0.44f, -0.03f), new Vector3(0.47f, 0.06f, 0.02f) };
            for (var i = 0; i < r.Length; i++)
                b.Tri(o, r[i], r[(i + 1) % r.Length], Color.white, new Color(0.8f, 0.8f, 0.8f, 1f), new Color(0.7f, 0.7f, 0.7f, 1f));
            shardMesh = b.ToMesh("Warden shard");
            return shardMesh;
        }
    }

    private static Mesh spikeMesh;

    /// <summary>Eruption spike — a five-sided faceted pyramid, base radius 1 at y=0,
    /// apex at y=1: deep crimson at the root, pale red at the point.</summary>
    public static Mesh SpikeMesh
    {
        get
        {
            if (spikeMesh != null) return spikeMesh;
            var b = new FacetBuilder();
            var apex = new Vector3(0.08f, 1f, -0.05f);
            var root = new Color(0.55f, 0.02f, 0.06f, 0.9f);
            var tip = new Color(1f, 0.55f, 0.5f, 0.95f);
            const int n = 5;
            for (var i = 0; i < n; i++)
            {
                var a0 = i * Mathf.PI * 2f / n;
                var a1 = (i + 1) * Mathf.PI * 2f / n;
                var p0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * (i % 2 == 0 ? 1f : 0.8f);
                var p1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) * ((i + 1) % 2 == 0 ? 1f : 0.8f);
                var side = i % 2 == 0 ? Crimson : root;
                b.Tri(p0, p1, apex, side, side, tip);
            }
            spikeMesh = b.ToMesh("Warden eruption spike");
            return spikeMesh;
        }
    }

    /// <summary>Unit 24-gon disc in XZ (fan) — marker fills and the impact peak.</summary>
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
/// A held telegraph the caller drives every frame — a faceted ground circle or a
/// path strip, ink-backed edge over a low stepped fill. Circles may face any
/// normal (the wall-chase rings sit ON the wall). Release() fades it out.
/// </summary>
public sealed class WardenMark : MonoBehaviour
{
    private LineRenderer edge, ink;
    private MeshRenderer fill;
    private MaterialPropertyBlock mpb;
    private Color col = WardenFx.Crimson;
    private float alpha = 1f, fillAlpha = 0.16f, width = 0.09f, pulse;
    private int sides = 40;
    private bool releasing;
    private float releaseT, releaseDur;
    private readonly Vector3[] pts = new Vector3[64];
    private int count;

    public static WardenMark Circle(Vector3 centre, float radius, Color col, float width = 0.09f, float fillAlpha = 0.16f,
                                    int sides = 40, Vector3? normal = null)
    {
        var m = Make("Warden circle", col, width, fillAlpha);
        m.sides = Mathf.Clamp(sides, 6, 62);
        m.fill.GetComponent<MeshFilter>().sharedMesh = WardenFx.DiscMesh;
        m.SetCircle(centre, radius, normal);
        return m;
    }

    public static WardenMark Line(Vector3 from, Vector3 to, float halfWidth, Color col, float width = 0.07f, float fillAlpha = 0.2f)
    {
        var m = Make("Warden path", col, width, fillAlpha);
        m.fill.GetComponent<MeshFilter>().sharedMesh = WardenFx.QuadMesh;
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
        m.mpb = new MaterialPropertyBlock();
        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(go.transform, false);
        fillGo.AddComponent<MeshFilter>();
        m.fill = fillGo.AddComponent<MeshRenderer>();
        m.fill.sharedMaterial = WardenFx.GlowMaterial;
        m.fill.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        m.fill.receiveShadows = false;
        m.ink = m.MakeLine("Ink", 1);
        m.edge = m.MakeLine("Edge", 2);
        WardenHazard.Track(go);
        return m;
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
        return lr;
    }

    public void SetCircle(Vector3 centre, float radius, Vector3? normal = null)
    {
        var n = normal ?? Vector3.up;
        var rot = Quaternion.FromToRotation(Vector3.up, n.normalized);
        var c = centre + n.normalized * 0.035f;
        count = sides + 1;
        for (var i = 0; i < count; i++)
        {
            var a = i * Mathf.PI * 2f / sides;
            pts[i] = c + rot * new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
        }
        fill.transform.SetPositionAndRotation(c - n.normalized * 0.005f, rot);
        fill.transform.localScale = new Vector3(radius, 1f, radius);
        Apply();
    }

    public void SetLine(Vector3 from, Vector3 to, float halfWidth)
    {
        var dir = Vector3.ProjectOnPlane(to - from, Vector3.up);
        var len = dir.magnitude;
        if (len < 0.01f) { dir = Vector3.forward; len = 0.01f; }
        dir /= len;
        var side = Vector3.Cross(Vector3.up, dir) * halfWidth;
        var lift = Vector3.up * 0.035f;
        count = 5;
        pts[0] = from + side + lift; pts[1] = from + side + dir * len + lift; pts[2] = from - side + dir * len + lift;
        pts[3] = from - side + lift; pts[4] = pts[0];
        fill.transform.SetPositionAndRotation(from + lift * 0.85f, Quaternion.LookRotation(dir, Vector3.up));
        fill.transform.localScale = new Vector3(halfWidth * 2f, 1f, len);
        Apply();
    }

    public void SetAlpha(float a) { alpha = Mathf.Clamp01(a); Apply(); }
    public void SetColor(Color c) { col = c; Apply(); }
    /// <summary>0..1 brightness/width pulse — the "about to fire" swell.</summary>
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
        if (!releasing) return;
        releaseT += Time.deltaTime;
        var k = 1f - releaseT / releaseDur;
        if (k <= 0f) { Destroy(gameObject); return; }
        alpha = Mathf.Min(alpha, k);
        Apply();
    }

    private void Apply()
    {
        if (edge == null) return;
        var a = WardenFx.Stepped(alpha);
        var on = a > 0.01f && count >= 2;
        edge.enabled = ink.enabled = fill.enabled = on;
        if (!on) return;
        edge.positionCount = ink.positionCount = count;
        for (var i = 0; i < count; i++) { edge.SetPosition(i, pts[i]); ink.SetPosition(i, pts[i]); }
        var c = Color.Lerp(col, Color.white, pulse * 0.35f);
        c.a = a * col.a;
        edge.startColor = edge.endColor = c;
        edge.startWidth = edge.endWidth = width * (1f + pulse * 0.6f);
        var k = WardenFx.Ink; k.a = a * 0.55f;
        ink.startColor = ink.endColor = k;
        ink.startWidth = ink.endWidth = width * 2.3f * (1f + pulse * 0.6f);
        var f = col; f.a = fillAlpha * WardenFx.Stepped(alpha * (0.6f + pulse * 0.6f));
        mpb.SetColor("_Tint", f);
        fill.SetPropertyBlock(mpb);
    }
}
