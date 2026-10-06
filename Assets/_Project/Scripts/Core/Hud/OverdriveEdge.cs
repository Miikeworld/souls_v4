using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen-edge lightning, read from the player's <see cref="IHudGauge"/>.
/// Lives on its own pixel canvas sorted UNDER both HUD canvases, so the
/// health frame, flask row and ult orb sit on top and the arcs run behind
/// and around them. Everything stays inside the outer band of the screen
/// (<see cref="BandX"/>/<see cref="BandY"/> per inset unit, never deeper than
/// <see cref="MaxInset"/> units: about 7% of width / 10% of height) so the
/// centre is never touched.
///
/// Build-up: small crimson arcs crawl along the corners (HUD corners
/// weighted heaviest), more often and brighter as instability rises.
/// Ignition: one surge, a ring of crimson strikes all round the screen plus
/// violet arcs answering at the corners. Overdrive: dense crimson strikes and
/// crawls with violet arcs running among them; lower Core Energy makes the
/// red wilder and deeper and the violet sparser. End: a violet flash of arcs
/// (expiry) or a red breakthrough (discharge), then a fast fade.
/// Every bolt is a hard core line inside a feathered glow halo. One mesh,
/// rebuilt per frame while visible; shapes re-jag at a stepped ~14fps (the
/// HUD speed lines' cadence) so it reads drawn, not glitchy.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public sealed class OverdriveEdge : MaskableGraphic
{
    private static readonly Color Crimson = new Color(0.78f, 0.04f, 0.08f);
    private static readonly Color CrimsonHot = new Color(1f, 0.34f, 0.28f);
    private static readonly Color CrimsonCold = new Color(0.36f, 0.08f, 0.1f);
    // Corner weights by perimeter index: 0 bottom-left (flask/arts), 1
    // bottom-right (ult orb), 2 top-right (bare), 3 top-left (health/Core frame).
    private static readonly float[] CornerW = { 1.5f, 1f, 0.5f, 2f };

    /// <summary>One inset unit, as a fraction of canvas width / height.</summary>
    public const float BandX = 0.055f, BandY = 0.075f;
    /// <summary>Deepest anything reaches, in inset units.</summary>
    public const float MaxInset = 1.3f;
    private const float Step = 1f / 14f;
    private const float CoreW = 4f, GlowW = 16f, BloomW = 40f; // core = one HUD texel
    private const int MaxBolts = 96;

    private enum Kind { Crawl, Strike }

    private sealed class Bolt
    {
        public readonly Vector2[] pts = new Vector2[8];
        public int n;
        public Kind kind;
        public float u, len, d0, d1, jag, age, life, a, alphaMul;
        public Color core, halo;
    }

    private readonly List<Bolt> live = new List<Bolt>();
    private readonly Stack<Bolt> spare = new Stack<Bolt>();
    private PixelCanvas pixels;
    private GameObject owner;
    private IHudGauge gauge;
    private PlayerMana mana;
    private float lookT, vis, manaK = 1f, strain, stepT, crawlAcc, strikeAcc, violetAcc, holdT;
    private bool wasLit, wasActive, shown;

    public static OverdriveEdge Attach(GameObject owner)
    {
        var pc = PixelCanvas.Create("OverdriveEdge", 5, 2f, owner.transform, interactive: false);
        var go = new GameObject("Arcs", typeof(RectTransform)) { layer = PixelCanvas.UiLayer };
        go.transform.SetParent(pc.Root, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var fx = go.AddComponent<OverdriveEdge>();
        fx.raycastTarget = false;
        fx.pixels = pc;
        fx.owner = owner;
        fx.mana = owner.GetComponent<PlayerMana>();
        pc.SetVisible(false); // no camera cost until there's something to draw
        return fx;
    }

    private void Update()
    {
        if (owner == null) return;
        var dt = Time.unscaledDeltaTime;
        if (gauge == null && (lookT -= dt) <= 0f)
        {
            lookT = 1f;
            gauge = owner.GetComponent<IHudGauge>();
        }
        var visible = gauge != null && gauge.GaugeVisible;
        var active = visible && gauge.GaugeActive;
        var lit = visible && (active || gauge.GaugeReady);
        var fault = gauge != null && gauge.GaugeFault;
        var k = visible && !active && !fault ? Mathf.Clamp01(gauge.Gauge01) : 0f;
        if (mana != null) manaK = Mathf.MoveTowards(manaK, mana.Current / Mathf.Max(1f, mana.Max), dt * 2f);
        strain = active ? 1f - manaK : 0f;
        var r = rectTransform.rect;

        if (lit && !wasLit) Surge(r);
        if (wasActive && !active) End(r, fault);
        wasLit = lit;
        wasActive = active;

        var menu = UiGates.MenuOpen;
        holdT = Mathf.Max(0f, holdT - dt);
        var want = !menu && (active || k > 0.2f || holdT > 0f);
        vis = Mathf.MoveTowards(vis, want ? 1f : 0f, dt * (want ? 8f : 3.5f));

        if (vis > 0f && !menu)
        {
            if (active)
            {
                strikeAcc += dt * Mathf.Lerp(9f, 15f, strain);
                crawlAcc += dt * 7f;
                violetAcc += dt * Mathf.Lerp(4f, 1.5f, strain); // the core answering, thinner as it tires
            }
            else if (k > 0.2f)
            {
                var kk = (k - 0.2f) / 0.8f;
                crawlAcc += dt * Mathf.Lerp(0.4f, 6f, kk * Mathf.Sqrt(kk));
            }
            for (; strikeAcc >= 1f; strikeAcc -= 1f)
            {
                var deep = Random.value < 0.25f + strain * 0.4f;
                Strike(r, Pick(r, 0.6f), deep ? Random.Range(1f, MaxInset) : Random.Range(0.5f, 1f), deep && Random.value < 0.4f);
            }
            for (; crawlAcc >= 1f; crawlAcc -= 1f)
                Crawl(r, Pick(r, active ? 0.6f : 0.9f), !active && Random.value < 0.2f, active ? 1f : k);
            for (; violetAcc >= 1f; violetAcc -= 1f) Crawl(r, Pick(r, 0.6f), true, 1f);
        }
        else crawlAcc = strikeAcc = violetAcc = 0f;

        stepT += dt;
        var step = stepT >= Step;
        if (step) stepT %= Step;
        for (var i = live.Count - 1; i >= 0; i--)
        {
            var b = live[i];
            b.age += dt;
            if (b.age >= b.life) { live.RemoveAt(i); spare.Push(b); }
            else if (step) Reshape(b, r);
        }

        var show = vis > 0f;
        if (show != shown) { shown = show; pixels.SetVisible(show); }
        if (show) SetVerticesDirty();
    }

    // ---------- events ----------

    private void Surge(Rect r)
    {
        holdT = 0.5f;
        for (var i = 0; i < 20; i++) Strike(r, (i + Random.value * 0.6f) * 4f / 20f, Random.Range(0.7f, 1.1f), false);
        for (var c = 0; c < 4; c++)
            for (var j = 0; j < 2; j++) Crawl(r, Advance(r, c, Random.Range(-90f, 90f)), true, 1f);
    }

    private void End(Rect r, bool fault)
    {
        holdT = 0.2f;
        if (fault)
            for (var i = 0; i < 18; i++) Strike(r, Random.Range(0f, 4f), Random.Range(1f, MaxInset), i % 2 == 0);
        else
            for (var c = 0; c < 4; c++)
                for (var j = 0; j < 2; j++) Crawl(r, Advance(r, c, Random.Range(-90f, 90f)), true, 1f);
    }

    // ---------- bolts ----------

    private Bolt Take() => spare.Count > 0 ? spare.Pop() : new Bolt();

    private void Crawl(Rect r, float u, bool violet, float k)
    {
        if (live.Count >= MaxBolts) return;
        var b = Take();
        b.kind = Kind.Crawl;
        b.u = u;
        b.len = violet ? Random.Range(60f, 140f) : Mathf.Lerp(40f, 160f, k) * Random.Range(0.7f, 1.2f);
        b.d0 = Random.Range(0.15f, 0.8f);
        b.d1 = Mathf.Clamp(b.d0 + Random.Range(-0.25f, 0.25f), 0.1f, 0.85f);
        b.jag = violet ? Random.Range(4f, 8f) : Mathf.Lerp(5f, 12f, k) * (1f + strain);
        b.n = Random.Range(5, 8);
        b.life = Random.Range(0.18f, 0.34f);
        b.age = 0f;
        b.core = violet ? Color.Lerp(CoreConduit.Bright, Color.white, 0.25f) : CrimsonHot;
        b.halo = violet ? CoreConduit.Main : Crimson;
        b.alphaMul = violet ? 0.9f : Mathf.Lerp(0.45f, 1f, k);
        Reshape(b, r);
        live.Add(b);
    }

    /// <summary>Edge → inward strike ending <paramref name="reach"/> inset units deep.</summary>
    private void Strike(Rect r, float u, float reach, bool cold)
    {
        if (live.Count >= MaxBolts) return;
        var b = Take();
        b.kind = Kind.Strike;
        b.u = u;
        b.len = Random.Range(-70f, 70f); // tangential drift: strikes come in at an angle
        b.d0 = -0.1f;                     // enters from just beyond the screen edge
        b.d1 = Mathf.Min(reach, MaxInset);
        b.jag = Random.Range(4f, 9f) * (1f + strain);
        b.n = Random.Range(4, 6);
        b.life = Random.Range(0.12f, 0.22f);
        b.age = 0f;
        b.core = cold ? Color.Lerp(CrimsonCold, CrimsonHot, 0.4f) : CrimsonHot;
        b.halo = cold ? CrimsonCold : Crimson;
        b.alphaMul = 1f;
        Reshape(b, r);
        live.Add(b);
    }

    private void Reshape(Bolt b, Rect r)
    {
        var sign = Random.value < 0.5f ? 1f : -1f;
        var last = b.n - 1;
        Vector2 a = Vector2.zero, e = Vector2.zero, perp = Vector2.zero;
        if (b.kind == Kind.Strike)
        {
            a = Edge(r, b.u, b.d0);
            e = Edge(r, Advance(r, b.u, b.len), b.d1);
            var d = (e - a).normalized;
            perp = new Vector2(-d.y, d.x);
        }
        for (var i = 0; i <= last; i++)
        {
            var f = i / (float)last;
            Vector2 p;
            if (b.kind == Kind.Crawl)
            {
                var u = Advance(r, b.u, (f - 0.5f) * b.len);
                p = Edge(r, u, Mathf.Lerp(b.d0, b.d1, f));
                if (i > 0 && i < last) p += Inward(u) * (b.jag * sign * Random.Range(0.5f, 1f));
            }
            else
            {
                p = Vector2.Lerp(a, e, f);
                if (i > 0 && i < last) p += perp * (b.jag * sign * Random.Range(0.5f, 1f));
            }
            sign = -sign; // alternate sides: angular zig-zag, never a wobble
            b.pts[i] = new Vector2(Mathf.Round(p.x * 0.5f) * 2f, Mathf.Round(p.y * 0.5f) * 2f); // snap to the RT pixel grid
        }
        b.a = Random.value < 0.18f ? 0.35f : 1f; // stepped flicker
    }

    private float Pick(Rect r, float cornerBias)
    {
        if (Random.value > cornerBias) return Random.Range(0f, 4f);
        var x = Random.value * (CornerW[0] + CornerW[1] + CornerW[2] + CornerW[3]);
        var c = 0;
        while (c < 3 && (x -= CornerW[c]) > 0f) c++;
        return Advance(r, c, Random.Range(-1f, 1f) * 0.25f * Mathf.Min(r.width, r.height));
    }

    // ---------- perimeter geometry ----------
    // u in [0,4): 0 bottom (left to right), 1 right (bottom to top), 2 top
    // (right to left), 3 left (top to bottom); integer u = a corner (0 = bottom-left).
    // inset 0 = screen edge, 1 = one band unit in.

    public static Vector2 Edge(Rect r, float u, float inset)
    {
        var ix = inset * BandX * r.width;
        var iy = inset * BandY * r.height;
        float x0 = r.xMin + ix, x1 = r.xMax - ix, y0 = r.yMin + iy, y1 = r.yMax - iy;
        u = Mathf.Repeat(u, 4f);
        var s = (int)u;
        var t = u - s;
        switch (s)
        {
            case 0: return new Vector2(Mathf.Lerp(x0, x1, t), y0);
            case 1: return new Vector2(x1, Mathf.Lerp(y0, y1, t));
            case 2: return new Vector2(Mathf.Lerp(x1, x0, t), y1);
            default: return new Vector2(x0, Mathf.Lerp(y1, y0, t));
        }
    }

    private static Vector2 Inward(float u)
    {
        switch ((int)Mathf.Repeat(u, 4f))
        {
            case 0: return Vector2.up;
            case 1: return Vector2.left;
            case 2: return Vector2.down;
            default: return Vector2.right;
        }
    }

    /// <summary>Walk <paramref name="px"/> screen units along the perimeter
    /// (negative = backwards), wrapping round corners.</summary>
    public static float Advance(Rect r, float u, float px)
    {
        for (var guard = 0; guard < 8 && Mathf.Abs(px) > 0.001f; guard++)
        {
            u = Mathf.Repeat(u, 4f);
            var s = Mathf.Floor(u);
            var len = ((int)s % 2 == 0 ? r.width : r.height);
            var room = px > 0f ? (s + 1f - u) * len : (u - s) * len;
            if (Mathf.Abs(px) <= room) return Mathf.Repeat(u + px / len, 4f);
            px -= Mathf.Sign(px) * room;
            u = px > 0f ? s + 1f + 1e-5f : s - 1e-5f;
        }
        return Mathf.Repeat(u, 4f);
    }

    // ---------- mesh ----------

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (vis <= 0f) return;
        // Wide faint bloom → feathered glow → hard core, each pass over all
        // bolts so cores always sit on top of every halo.
        for (var pass = 0; pass < 3; pass++)
            foreach (var b in live) DrawBolt(vh, b, pass);
    }

    private void DrawBolt(VertexHelper vh, Bolt b, int pass)
    {
        var k = b.age / b.life;
        var a = b.a * b.alphaMul * vis * (k < 0.7f ? 1f : 1f - (k - 0.7f) / 0.3f);
        if (a <= 0.01f) return;
        for (var i = 1; i < b.n; i++)
        {
            var p0 = b.pts[i - 1];
            var p1 = b.pts[i];
            if (pass == 0) Glow(vh, p0, p1, BloomW, b.halo, a * 0.16f);
            else if (pass == 1) Glow(vh, p0, p1, GlowW, b.halo, a * 0.55f);
            else
            {
                var c = b.core;
                c.a = a;
                Glow(vh, p0, p1, CoreW, c, a, solid: true);
            }
        }
    }

    /// <summary>Segment of width <paramref name="w"/>: solid, or feathered —
    /// full alpha on the spine fading to zero at both outer edges.</summary>
    private static void Glow(VertexHelper vh, Vector2 a, Vector2 b, float w, Color col, float alpha, bool solid = false)
    {
        var d = b - a;
        var len = d.magnitude;
        if (len < 0.01f || alpha <= 0.004f) return;
        var t = d / len * (w * 0.5f);
        var n = new Vector2(-t.y, t.x);
        var on = col; on.a = alpha;
        var off = col; off.a = solid ? alpha : 0f;
        // Two quads sharing the spine: spine (on) → edge (off) on each side.
        Quad(vh, a - t, b + t, b + t + n, a - t + n, on, on, off, off);
        Quad(vh, a - t, b + t, b + t - n, a - t - n, on, on, off, off);
    }

    private static void Quad(VertexHelper vh, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
        Color c0, Color c1, Color c2, Color c3)
    {
        var i = vh.currentVertCount;
        var v = UIVertex.simpleVert;
        v.position = p0; v.color = c0; vh.AddVert(v);
        v.position = p1; v.color = c1; vh.AddVert(v);
        v.position = p2; v.color = c2; vh.AddVert(v);
        v.position = p3; v.color = c3; vh.AddVert(v);
        vh.AddTriangle(i, i + 1, i + 2);
        vh.AddTriangle(i + 2, i + 3, i);
    }
}
