using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A contextual weapon/mechanic state read by the HUD instead of adding
/// another persistent bar — heat, charge, corruption, ammo-like state. One
/// component on the player implementing this is picked up by the HUD;
/// <see cref="CrimsonInstability"/> is the first. Its presentation lives in
/// <see cref="OverdriveEdge"/> (screen-edge arcs), not on the bars.
/// </summary>
public interface IHudGauge
{
    /// <summary>False hides the overlay entirely (e.g. its weapon isn't equipped).</summary>
    bool GaugeVisible { get; }
    float Gauge01 { get; }
    /// <summary>Full and waiting to be triggered.</summary>
    bool GaugeReady { get; }
    /// <summary>The triggered state is running (Overdrive).</summary>
    bool GaugeActive { get; }
    /// <summary>Vented / shut down — the overlay cools off.</summary>
    bool GaugeFault { get; }
    /// <summary>0..1 one-shot emphasis (became ready, triggered, discharged).</summary>
    float GaugeFlare { get; }
}

/// <summary>
/// The Core Energy bar: the slot's own bevel fill (same family as health and
/// stamina) tinted Violet Core #8B35E8, plus restrained feedback — a flow
/// pulse down the bar on every paid cast, a soft brighten on refill, a dim
/// slow breath when nearly empty, the shared copper refusal flash, and a
/// faint strain flicker while the gauge's triggered state (Overdrive) runs.
///
/// Around it, an optional <see cref="IHudGauge"/> draws the crimson
/// containment (v2). It renders ABOVE the hand-drawn frame — the frame is
/// the last sibling in the authored HUD, so the v1 rims sat under its ink
/// and only the haze leaked through onto the health/stamina fills:
/// • Containment seals — 12 pixel plates in each black ink separator above
///   and below the purple channel, over a dim ghost track so the distance
///   left is readable. The leading plate glows hot; each plate that seals
///   flashes and pops. The separators warm with pressure (heat strips).
///   During Overdrive the gauge returns time left, so the seals unwind.
/// • The Crimson Core — a faceted pixel crystal set in a diamond socket in
///   the frame's lower-right corner (the heart socket's mirror). It heats
///   from dull blood to crimson, beats faster with pressure, cracks at
///   50/75/90%, sheds embers past 60%; ignition bursts a shockwave ring and
///   floods the seals white; Overdrive keeps it incandescent with arcs
///   leaping off it; a discharge vents it cold, cracked and smoking.
/// • Arcs — short pixel lightning crackles along the lit seals from 30%,
///   denser when full and during Overdrive.
/// Red never draws over the purple channel. Plain class owned by PlayerHud.
/// </summary>
public sealed class CoreConduit
{
    public static readonly Color Main = new Color32(0x8B, 0x35, 0xE8, 0xFF);
    public static readonly Color Bright = new Color32(0xB7, 0x6C, 0xFF, 0xFF);
    public static readonly Color Shadow = new Color32(0x4D, 0x16, 0x7F, 0xFF);
    private static readonly Color Crimson = new Color(0.78f, 0.04f, 0.08f);
    private static readonly Color CrimsonHot = new Color(1f, 0.34f, 0.28f);
    private static readonly Color CrimsonCold = new Color(0.36f, 0.08f, 0.1f);
    private static readonly Color CrimsonDull = new Color(0.25f, 0.04f, 0.06f);
    private static readonly Color Track = new Color(0.24f, 0.07f, 0.08f, 0.9f);
    private static readonly Color CrackGlow = new Color(1f, 0.82f, 0.72f);
    private static readonly Color Smoke = new Color(0.32f, 0.29f, 0.31f);

    // Layout, in canvas units (the HUD frame texel ≈ 4.2).
    private const int Seals = 12;
    private const float SealGap = 3f, SealThick = 4f, HeatThick = 7f;
    private const float NodeT = 4f;                 // node sprite texel
    private const int SockW = 11, SockH = 15, CryW = 5, CryH = 9;
    private static readonly Vector2 NodeOffset = new Vector2(22f, -12f); // from the slot's right-centre
    private const float BoltT = 2f;
    private const int BoltW = 14, BoltH = 3, BoltVariants = 4;

    private Graphic fill;
    private RectTransform host, flow, crim, node, crystalRt, haloRt, ringRt;
    private Image flowImg, sheen;
    private CanvasGroup crimGroup;
    private GameObject owner;
    private IHudGauge gauge;
    private Vector2 crimBase;
    private float gaugeLookT, flowT = -1f, cast, shownGauge, vis;

    // Containment v2.
    private float rimTop, rimBot;
    private RectTransform[] sealTop, sealBot;        // lit plates (width = fill)
    private Image[] sealTopImg, sealBotImg, trackImg;
    private float[] sealFlash;
    private Image heatTop, heatBot, crystal, halo, ring, glint;
    private Image[] crackImg;
    private Part[] embers, bolts;
    private int lastWhole;
    private bool wasActive, wasFault, wasReady;
    private float ringT = 1f, ringScale = 3f, floodT, ventT, beatPhase, glintT, emberAcc, boltAcc, ventSmokeT;

    private sealed class Part
    {
        public Image img;
        public RectTransform rt;
        public Vector2 vel;
        public float life, age;
    }

    /// <param name="rimTop">Centre of the ink separator above the channel, in
    /// canvas units above the fill rect's top edge.</param>
    /// <param name="rimBot">Centre of the separator below, in canvas units
    /// below the fill rect's bottom edge.</param>
    public static CoreConduit Build(RectTransform fillRt, GameObject owner, float rimTop = 0.5f, float rimBot = 2.8f)
    {
        if (fillRt == null || fillRt.parent == null) return null;
        var c = new CoreConduit { owner = owner, fill = fillRt.GetComponent<Graphic>(), rimTop = rimTop, rimBot = rimBot };
        var h = Mathf.Max(4f, fillRt.rect.height);
        if (c.fill != null) c.fill.color = Main;
        c.sheen = Stretch("Sheen", fillRt).gameObject.AddComponent<Image>();
        c.sheen.color = new Color(Bright.r, Bright.g, Bright.b, 0f);
        c.sheen.raycastTarget = false;

        c.host = NewRect("CoreFlow", fillRt.parent);
        CopyGeometry(fillRt, c.host);
        c.host.SetSiblingIndex(fillRt.GetSiblingIndex() + 1);
        c.flow = NewRect("Flow", c.host);
        c.flow.anchorMin = c.flow.anchorMax = new Vector2(0f, 0.5f);
        c.flow.sizeDelta = new Vector2(h * 3f, h);
        c.flowImg = c.flow.gameObject.AddComponent<Image>();
        c.flowImg.sprite = BandSprite();
        c.flowImg.color = new Color(1f, 0.92f, 1f, 0f);
        c.flowImg.raycastTarget = false;

        c.BuildContainment(fillRt);
        return c;
    }

    private void BuildContainment(RectTransform fillRt)
    {
        // Slot-sized root, LAST sibling so it draws over the frame's ink.
        crim = NewRect("Crimson", fillRt.parent);
        CopyGeometry(fillRt, crim);
        crim.SetAsLastSibling();
        crimBase = crim.anchoredPosition;
        crimGroup = crim.gameObject.AddComponent<CanvasGroup>();
        crimGroup.alpha = 0f;
        crimGroup.interactable = crimGroup.blocksRaycasts = false;

        heatTop = Img(Rim(crim, "HeatTop", true, rimTop, HeatThick), HeatSprite(), Clear(Crimson));
        heatBot = Img(Rim(crim, "HeatBot", false, rimBot, HeatThick), HeatSprite(), Clear(Crimson));

        sealTop = new RectTransform[Seals]; sealBot = new RectTransform[Seals];
        sealTopImg = new Image[Seals]; sealBotImg = new Image[Seals];
        trackImg = new Image[Seals * 2];
        sealFlash = new float[Seals];
        for (var i = 0; i < Seals; i++)
        {
            for (var side = 0; side < 2; side++)
            {
                var top = side == 0;
                var slot = Rim(crim, (top ? "SealTop" : "SealBot") + i, top, top ? rimTop : rimBot, SealThick);
                // Each plate owns 1/Seals of the width minus the gap — anchors do the maths.
                var a0 = (float)i / Seals;
                var a1 = (float)(i + 1) / Seals;
                slot.anchorMin = new Vector2(a0, slot.anchorMin.y);
                slot.anchorMax = new Vector2(a1, slot.anchorMax.y);
                slot.offsetMin = new Vector2(i == 0 ? 0f : SealGap * 0.5f, slot.offsetMin.y);
                slot.offsetMax = new Vector2(i == Seals - 1 ? 0f : -SealGap * 0.5f, slot.offsetMax.y);
                trackImg[i * 2 + side] = Img(slot, null, Track);
                var lit = NewRect("Lit", slot);
                lit.anchorMin = Vector2.zero; lit.anchorMax = new Vector2(0f, 1f);
                lit.pivot = new Vector2(0f, 0.5f);
                lit.offsetMin = lit.offsetMax = Vector2.zero;
                var img = Img(lit, null, Crimson);
                if (top) { sealTop[i] = lit; sealTopImg[i] = img; }
                else { sealBot[i] = lit; sealBotImg[i] = img; }
            }
        }

        // The Crimson Core: socket + crystal set in the frame's lower-right corner.
        node = NewRect("CrimsonCore", crim);
        node.anchorMin = node.anchorMax = new Vector2(1f, 0.5f);
        node.sizeDelta = new Vector2(SockW * NodeT, SockH * NodeT);
        node.anchoredPosition = NodeOffset;
        haloRt = Child(node, "Halo", new Vector2(0.5f, 0.5f), new Vector2(64f, 64f));
        halo = Img(haloRt, HaloSprite(), Clear(Crimson));
        ringRt = Child(node, "Shockwave", new Vector2(0.5f, 0.5f), new Vector2(40f, 40f));
        ring = Img(ringRt, RingSprite(), Clear(CrimsonHot));
        Img(Child(node, "Socket", new Vector2(0.5f, 0.5f), node.sizeDelta), SocketSprite(), Color.white);
        crystalRt = Child(node, "Crystal", new Vector2(0.5f, 0.5f), new Vector2(CryW * NodeT, CryH * NodeT));
        crystal = Img(crystalRt, CrystalSprite(), CrimsonDull);
        crackImg = new Image[3];
        for (var s = 0; s < crackImg.Length; s++)
        {
            crackImg[s] = Img(Child(crystalRt, "Crack" + (s + 1), new Vector2(0.5f, 0.5f), crystalRt.sizeDelta), CrackSprite(s + 1), Color.black);
            crackImg[s].enabled = false;
        }
        var g = Child(crystalRt, "Glint", new Vector2(0f, 0f), new Vector2(NodeT, NodeT));
        g.anchoredPosition = new Vector2(1.5f * NodeT, 6.5f * NodeT); // upper-left facet
        glint = Img(g, null, Clear(Color.white));

        embers = Pool(node, "Ember", 14, NodeT);
        bolts = Pool(crim, "Arc", 10, 0f);
        foreach (var b in bolts) b.rt.sizeDelta = new Vector2(BoltW * BoltT, BoltH * BoltT);
        crim.gameObject.SetActive(false);
    }

    /// <summary>A paid cast — energy visibly travels down the bar.</summary>
    public void Cast()
    {
        flowT = 0f;
        cast = 1f;
    }

    /// <param name="shown">Smoothed fill fraction the bar is drawing.</param>
    /// <param name="denied">0..1 refused-cast flash.</param>
    /// <param name="restored">0..1 kill/flask refill pulse.</param>
    public void Tick(float shown, float denied, float restored, float dt)
    {
        var t = Time.unscaledTime;
        if (gauge == null && (gaugeLookT -= dt) <= 0f)
        {
            gaugeLookT = 1f; // the gauge component can be added after the HUD wakes
            gauge = owner != null ? owner.GetComponent<IHudGauge>() : null;
        }
        var strained = gauge != null && gauge.GaugeVisible && gauge.GaugeActive;

        var low = shown < 0.2f ? (0.2f - shown) / 0.2f : 0f;
        var col = Color.Lerp(Main, Color.Lerp(Shadow, Main, 0.35f), low * (0.55f + 0.45f * Mathf.Sin(t * 2.2f)));
        col = Color.Lerp(col, new Color(1f, 0.32f, 0.22f), denied);
        if (fill != null) fill.color = col;

        cast = Mathf.Max(0f, cast - dt * 3f);
        var sheenA = Mathf.Max(cast * 0.4f, restored * 0.3f);
        if (strained) sheenA = Mathf.Max(sheenA, 0.08f + 0.1f * Mathf.PerlinNoise(t * 11f, 0.1f));
        sheen.color = new Color(Bright.r, Bright.g, Bright.b, sheenA);

        // Flow pulse: root → leading edge over 0.3s, fading as it arrives.
        if (flowT >= 0f && (flowT += dt / 0.3f) >= 1f) flowT = -1f;
        var fk = flowT < 0f ? 1f : flowT;
        flow.anchoredPosition = new Vector2(Mathf.Lerp(0f, shown * host.rect.width, 1f - (1f - fk) * (1f - fk)), 0f);
        flowImg.color = new Color(1f, 0.92f, 1f, flowT < 0f ? 0f : 0.7f * (1f - fk));

        TickContainment(dt, t);
    }

    private void TickContainment(float dt, float t)
    {
        var show = gauge != null && gauge.GaugeVisible;
        vis = Mathf.MoveTowards(vis, show ? 1f : 0f, dt * 4f);
        var on = vis > 0.001f;
        if (crim.gameObject.activeSelf != on) crim.gameObject.SetActive(on);
        crimGroup.alpha = vis;
        if (!on) { shownGauge = 0f; return; }

        var active = show && gauge.GaugeActive;
        var fault = show && gauge.GaugeFault;
        var ready = show && gauge.GaugeReady;
        var flare = show ? gauge.GaugeFlare : 0f;
        var k = show ? Mathf.Clamp01(gauge.Gauge01) : shownGauge;
        shownGauge = Mathf.Lerp(shownGauge, k, 1f - Mathf.Exp(-10f * dt));
        var s = shownGauge;

        // One-shot beats on state edges.
        if (active && !wasActive) { ringT = 0f; ringScale = 3.4f; floodT = 1f; Burst(10, true); }
        if (ready && !wasReady && !active) { ringT = 0f; ringScale = 2f; floodT = 0.6f; }
        if (fault && !wasFault) { ventT = 1f; ventSmokeT = 1.6f; Burst(6, false); }
        wasActive = active; wasReady = ready; wasFault = fault;
        floodT = Mathf.Max(0f, floodT - dt * 2.2f);
        ventT = Mathf.Max(0f, ventT - dt * 1.5f);

        var c = fault ? CrimsonCold
            : active ? Color.Lerp(Crimson, CrimsonHot, 0.25f + 0.5f * Mathf.PerlinNoise(t * 12f, 0.4f))
            : Color.Lerp(Crimson * 0.7f, Crimson, s);
        if (ready) c = Color.Lerp(c, CrimsonHot, 0.3f + 0.3f * Mathf.Sin(t * 7f));
        c = Color.Lerp(c, CrimsonHot, flare);
        c.a = 1f;

        TickSeals(s, c, active, fault, dt, t);

        // Separators warm with pressure — heat in the containment walls.
        var heat = fault ? 0.05f : 0.1f + 0.3f * s;
        if (ready) heat = 0.45f + 0.15f * Mathf.Sin(t * 7f);
        if (active) heat = 0.4f + 0.2f * Mathf.PerlinNoise(t * 13f, 0.9f);
        heat = Mathf.Max(heat, floodT * 0.8f);
        heatTop.color = new Color(c.r, c.g, c.b, heat);
        heatBot.color = new Color(c.r, c.g, c.b, heat);

        TickCore(s, active, fault, ready, flare, dt, t);
        TickBolts(s, active, fault, ready, dt);

        // Overdrive shake: the containment visibly strains against the bar.
        var shake = (active ? 1f : 0f) + flare + ventT;
        if (shake > 0f)
        {
            var amp = Mathf.Min(shake, 1.5f) * 1.3f;
            crim.anchoredPosition = crimBase + new Vector2(
                (Mathf.PerlinNoise(t * 23f, 0.11f) - 0.5f) * 2f * amp,
                (Mathf.PerlinNoise(t * 31f, 0.53f) - 0.5f) * 2f * amp);
        }
        else if (crim.anchoredPosition != crimBase) crim.anchoredPosition = crimBase;
    }

    /// <summary>Plates light left → right; the partial plate is the hot growth
    /// front; a plate that just sealed flashes and pops. In Overdrive the
    /// value is time left, so the seals unwind right → left.</summary>
    private void TickSeals(float s, Color c, bool active, bool fault, float dt, float t)
    {
        var lit = s * Seals;
        var whole = Mathf.FloorToInt(lit + 0.0001f);
        if (!active && !fault && whole > lastWhole)
            for (var i = Mathf.Max(0, lastWhole); i < Mathf.Min(whole, Seals); i++) sealFlash[i] = 1f;
        lastWhole = whole;
        var trackCol = fault ? new Color(0.16f, 0.07f, 0.08f, 0.9f) : Track;
        foreach (var tr in trackImg) tr.color = trackCol;

        for (var i = 0; i < Seals; i++)
        {
            var f = Mathf.Clamp01(lit - i);
            sealFlash[i] = Mathf.Max(0f, sealFlash[i] - dt * 3f);
            var flash = Mathf.Max(sealFlash[i], floodT);
            var col = f < 1f ? CrimsonHot : c;
            if (active) col = Color.Lerp(c, CrimsonHot, Mathf.PerlinNoise(t * 9f + i * 1.7f, 0.3f) * 0.6f);
            col = Color.Lerp(col, CrackGlow, flash * 0.85f);
            col.a = f > 0f ? 1f : 0f;
            // Snap the growth front to 2-unit steps so it moves in pixel beats.
            var w = f >= 1f ? 1f : Mathf.Floor(f * 8f) / 8f;
            var pop = 1f + 0.55f * sealFlash[i];
            for (var side = 0; side < 2; side++)
            {
                var rt = side == 0 ? sealTop[i] : sealBot[i];
                var img = side == 0 ? sealTopImg[i] : sealBotImg[i];
                rt.anchorMax = new Vector2(w, 1f);
                rt.localScale = new Vector3(1f, pop, 1f);
                img.color = col;
            }
        }
    }

    private void TickCore(float s, bool active, bool fault, bool ready, float flare, float dt, float t)
    {
        // Heartbeat: quickens with pressure, races during Overdrive.
        var period = active ? 0.32f : Mathf.Lerp(1.3f, 0.45f, s);
        beatPhase = (beatPhase + dt / period) % 1f;
        var beat = fault || s < 0.15f && !active ? 0f : Mathf.Pow(Mathf.Max(0f, Mathf.Sin(beatPhase * Mathf.PI * 2f)), 6f);

        var col = fault ? Color.Lerp(CrimsonCold, Color.black, 0.35f)
            : active ? Color.Lerp(Crimson, CrimsonHot, 0.45f + 0.3f * Mathf.PerlinNoise(t * 14f, 0.2f))
            : ready ? Color.Lerp(Crimson, CrimsonHot, 0.3f + 0.3f * Mathf.Sin(t * 7f))
            : Color.Lerp(CrimsonDull, Crimson, Mathf.Clamp01(s * 1.15f));
        col = Color.Lerp(col, CrimsonHot, Mathf.Max(flare, beat * 0.25f));
        crystal.color = col;
        crystalRt.localScale = Vector3.one * (1f + 0.07f * beat + 0.12f * floodT);

        // Fractures at 50 / 75 / 90%; all open while running or vented.
        var stage = active || fault ? 3 : s >= 0.9f ? 3 : s >= 0.75f ? 2 : s >= 0.5f ? 1 : 0;
        var crackCol = fault ? new Color(0.05f, 0.01f, 0.02f)
            : active || ready ? Color.Lerp(CrackGlow, CrimsonHot, 0.3f * Mathf.PerlinNoise(t * 17f, 0.6f))
            : Color.Lerp(Color.black, CrimsonHot, beat * 0.6f);
        for (var i = 0; i < crackImg.Length; i++)
        {
            crackImg[i].enabled = i < stage;
            crackImg[i].color = crackCol;
        }

        var haloA = fault ? 0f : 0.1f + 0.45f * s + (active ? 0.2f * Mathf.PerlinNoise(t * 11f, 0.8f) : 0f);
        haloA = Mathf.Max(haloA, floodT) * (0.85f + 0.3f * beat);
        halo.color = new Color(Crimson.r, Crimson.g, Crimson.b, Mathf.Clamp01(haloA));
        haloRt.localScale = Vector3.one * (0.9f + 0.35f * s + 0.25f * beat + 0.4f * floodT);

        ringT = Mathf.Min(1f, ringT + dt / 0.5f);
        var e = 1f - (1f - ringT) * (1f - ringT);
        ringRt.localScale = Vector3.one * Mathf.Lerp(0.5f, ringScale, e);
        ring.color = new Color(CrimsonHot.r, CrimsonHot.g, CrimsonHot.b, ringT >= 1f ? 0f : 1f - ringT);

        // Facet glint: a single bright texel blinking now and then.
        glintT -= dt;
        if (glintT <= -0.12f) glintT = Random.Range(1.6f, 3.2f) * (active ? 0.35f : 1f);
        glint.color = new Color(1f, 0.9f, 0.85f, !fault && s > 0.2f && glintT < 0f ? 0.9f : 0f);

        // Embers past 60% / in Overdrive; grey smoke after a vent.
        var rate = fault ? 0f : active ? 14f : s > 0.6f ? (s - 0.6f) * 12f : 0f;
        ventSmokeT -= dt;
        if (ventSmokeT > 0f) rate = 7f;
        emberAcc += rate * dt;
        while (emberAcc >= 1f) { emberAcc -= 1f; Emit(ventSmokeT > 0f); }
        foreach (var p in embers)
        {
            if (p.life <= 0f) continue;
            p.age += dt;
            if (p.age >= p.life) { p.life = 0f; p.img.enabled = false; continue; }
            p.rt.anchoredPosition += p.vel * dt;
            p.vel.x *= 1f - dt * 1.5f;
            var k = p.age / p.life;
            var smoke = p.img.rectTransform.sizeDelta.x > NodeT;
            var pc = smoke ? Smoke : Color.Lerp(CrimsonHot, Crimson, k);
            p.img.color = new Color(pc.r, pc.g, pc.b, (smoke ? 0.55f : 1f) * (1f - k * k));
        }
    }

    private void TickBolts(float s, bool active, bool fault, bool ready, float dt)
    {
        var rate = fault ? 0f : active ? 11f : ready ? 5f : s > 0.3f ? Mathf.Lerp(0.6f, 3.5f, (s - 0.3f) / 0.7f) : 0f;
        boltAcc += rate * dt;
        var w = crim.rect.width;
        while (boltAcc >= 1f)
        {
            boltAcc -= 1f;
            var p = Free(bolts);
            if (p == null) break;
            p.img.sprite = BoltSprite(Random.Range(0, BoltVariants));
            p.age = 0f; p.life = Random.Range(0.05f, 0.12f);
            p.img.enabled = true;
            if (active && Random.value < 0.3f)
            {
                // Arcs leaping off the Core itself.
                var dir = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)) * Vector3.right;
                p.rt.anchorMin = p.rt.anchorMax = new Vector2(1f, 0.5f);
                p.rt.anchoredPosition = NodeOffset + (Vector2)(dir * Random.Range(16f, 24f));
                p.rt.localRotation = Quaternion.FromToRotation(Vector3.right, dir);
            }
            else
            {
                var top = Random.value < 0.5f;
                var span = Mathf.Max(BoltW * BoltT, w * Mathf.Clamp01(s));
                p.rt.anchorMin = p.rt.anchorMax = new Vector2(0f, top ? 1f : 0f);
                p.rt.anchoredPosition = new Vector2(Random.Range(BoltW * BoltT * 0.5f, span - BoltW * BoltT * 0.5f), top ? rimTop : -rimBot);
                p.rt.localRotation = Quaternion.identity;
            }
            p.rt.localScale = new Vector3(Random.value < 0.5f ? -1f : 1f, 1f, 1f);
        }
        foreach (var p in bolts)
        {
            if (p.life <= 0f) continue;
            p.age += dt;
            if (p.age >= p.life) { p.life = 0f; p.img.enabled = false; continue; }
            p.img.color = new Color(CrimsonHot.r, CrimsonHot.g, CrimsonHot.b, p.age / p.life < 0.5f ? 1f : 0.55f);
        }
    }

    private void Burst(int n, bool hot)
    {
        for (var i = 0; i < n; i++) Emit(!hot, Random.Range(40f, 75f));
    }

    private void Emit(bool smoke, float speed = 0f)
    {
        var p = Free(embers);
        if (p == null) return;
        p.age = 0f;
        p.life = smoke ? Random.Range(0.9f, 1.4f) : Random.Range(0.5f, 0.9f);
        p.rt.sizeDelta = Vector2.one * (smoke ? NodeT * 1.5f : NodeT);
        p.rt.anchoredPosition = new Vector2(Random.Range(-6f, 6f), Random.Range(-4f, 10f));
        p.vel = speed > 0f
            ? (Vector2)(Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)) * Vector3.right) * speed
            : new Vector2(Random.Range(-14f, 14f), smoke ? Random.Range(14f, 24f) : Random.Range(26f, 52f));
        p.img.enabled = true;
    }

    private static Part Free(Part[] pool)
    {
        foreach (var p in pool) if (p.life <= 0f) return p;
        return null;
    }

    private static Part[] Pool(RectTransform parent, string name, int n, float size)
    {
        var pool = new Part[n];
        for (var i = 0; i < n; i++)
        {
            var rt = Child(parent, name + i, new Vector2(0.5f, 0.5f), Vector2.one * size);
            var img = Img(rt, null, Color.white);
            img.enabled = false;
            pool[i] = new Part { rt = rt, img = img };
        }
        return pool;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform)) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static RectTransform Stretch(string name, Transform parent)
    {
        var rt = NewRect(name, parent);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    private static RectTransform Child(Transform parent, string name, Vector2 anchor, Vector2 size)
    {
        var rt = NewRect(name, parent);
        rt.anchorMin = rt.anchorMax = anchor;
        rt.sizeDelta = size;
        rt.anchoredPosition = Vector2.zero;
        return rt;
    }

    /// <summary>A full-width strip of <paramref name="thick"/> centred in the
    /// ink separator above (top) or below the slot.</summary>
    private static RectTransform Rim(Transform parent, string name, bool top, float centre, float thick)
    {
        var rt = NewRect(name, parent);
        var y = top ? 1f : 0f;
        rt.anchorMin = new Vector2(0f, y); rt.anchorMax = new Vector2(1f, y);
        rt.pivot = new Vector2(0.5f, 0.5f);
        var cy = top ? centre : -centre;
        rt.offsetMin = new Vector2(0f, cy - thick * 0.5f);
        rt.offsetMax = new Vector2(0f, cy + thick * 0.5f);
        return rt;
    }

    private static Color Clear(Color c) => new Color(c.r, c.g, c.b, 0f);

    private static Image Img(RectTransform rt, Sprite sp, Color col)
    {
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sp;
        img.color = col;
        img.raycastTarget = false;
        return img;
    }

    private static void CopyGeometry(RectTransform from, RectTransform to)
    {
        to.anchorMin = from.anchorMin;
        to.anchorMax = from.anchorMax;
        to.pivot = from.pivot;
        to.anchoredPosition = from.anchoredPosition;
        to.sizeDelta = from.sizeDelta;
    }

    private static Sprite bandSp, heatSp, haloSp, ringSp, socketSp, crystalSp;
    private static readonly Sprite[] crackSp = new Sprite[3], boltSp = new Sprite[BoltVariants];

    private static Sprite Make(int w, int h, System.Func<int, int, Color> px, FilterMode filter = FilterMode.Point)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = filter, wrapMode = TextureWrapMode.Clamp };
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            tex.SetPixel(x, y, px(x, y));
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
    }

    /// <summary>Soft vertical band filling an ink separator.</summary>
    private static Sprite HeatSprite()
    {
        if (heatSp != null) return heatSp;
        return heatSp = Make(1, 8, (x, y) => new Color(1f, 1f, 1f, Mathf.Sin(Mathf.PI * (y + 0.5f) / 8f)), FilterMode.Bilinear);
    }

    private static Sprite HaloSprite()
    {
        if (haloSp != null) return haloSp;
        return haloSp = Make(32, 32, (x, y) =>
        {
            var d = Mathf.Clamp01(new Vector2(x - 15.5f, y - 15.5f).magnitude / 16f);
            return new Color(1f, 1f, 1f, (1f - d) * (1f - d));
        }, FilterMode.Bilinear);
    }

    /// <summary>Pixel shockwave ring (2 texels thick).</summary>
    private static Sprite RingSprite()
    {
        if (ringSp != null) return ringSp;
        return ringSp = Make(20, 20, (x, y) =>
        {
            var d = new Vector2(x - 9.5f, y - 9.5f).magnitude;
            return new Color(1f, 1f, 1f, d > 7.4f && d <= 9.6f ? 1f : 0f);
        });
    }

    private static int Hash(int x, int y) => ((x * 73856093) ^ (y * 19349663)) & 0x7fffffff;

    /// <summary>Diamond socket in the frame's language: ink outline, violet-
    /// mottled mass, a broken copper (lit, upper-left) / rust line, an ink lip
    /// and a dark well for the crystal.</summary>
    private static Sprite SocketSprite()
    {
        if (socketSp != null) return socketSp;
        var px = new Color[SockW * SockH];
        float cx = (SockW - 1) * 0.5f, cy = (SockH - 1) * 0.5f;
        for (var y = 0; y < SockH; y++)
        for (var x = 0; x < SockW; x++)
        {
            var d = Mathf.Abs(x - cx) / (cx + 0.5f) + Mathf.Abs(y - cy) / (cy + 0.5f);
            Color col;
            if (d > 0.92f) col = Clear(Color.black);
            else if (d > 0.6f)
            {
                col = Hash(x, y) % 3 == 0 ? PersonaUi.Violet : PersonaUi.Night;
                if (d > 0.66f && d < 0.84f && Hash(x, y) % 7 != 0)
                    col = x - cx <= 0f && y - cy >= 0f ? PersonaUi.Copper : PersonaUi.Rust;
            }
            else if (d > 0.5f) col = PersonaUi.Ink;
            else col = new Color(0.03f, 0.02f, 0.035f);
            px[y * SockW + x] = col;
        }
        OutlineInk(px, SockW, SockH);
        return socketSp = Make(SockW, SockH, (x, y) => px[y * SockW + x]);
    }

    /// <summary>Elongated hex crystal in grey values (tinted at runtime): lit
    /// upper-left facets, a central ridge, shaded lower-right.</summary>
    private static Sprite CrystalSprite()
    {
        if (crystalSp != null) return crystalSp;
        float cx = (CryW - 1) * 0.5f, cy = (CryH - 1) * 0.5f;
        return crystalSp = Make(CryW, CryH, (x, y) =>
        {
            var half = cx - Mathf.Max(0f, Mathf.Abs(y - cy) - (cy - cx));
            if (Mathf.Abs(x - cx) > half + 0.01f) return Clear(Color.white);
            bool up = y >= cy, left = x < cx, mid = Mathf.Approximately(x, cx);
            var v = left ? (up ? 1f : 0.74f) : mid ? (up ? 0.84f : 0.58f) : (up ? 0.62f : 0.42f);
            return new Color(v, v, v, 1f);
        });
    }

    /// <summary>Fracture texels over the crystal, cumulative by stage.</summary>
    private static Sprite CrackSprite(int stage)
    {
        if (crackSp[stage - 1] != null) return crackSp[stage - 1];
        var pts = stage switch
        {
            1 => new[] { (1, 6), (2, 5), (2, 4) },
            2 => new[] { (3, 3), (3, 2), (4, 5) },
            _ => new[] { (1, 2), (2, 1), (1, 4) },
        };
        return crackSp[stage - 1] = Make(CryW, CryH, (x, y) =>
            System.Array.IndexOf(pts, (x, y)) >= 0 ? Color.white : Clear(Color.white));
    }

    /// <summary>Zig-zag arc, 1-texel stroke with jumps between three rows.</summary>
    private static Sprite BoltSprite(int seed)
    {
        if (boltSp[seed] != null) return boltSp[seed];
        var px = new Color[BoltW * BoltH];
        var rnd = new System.Random(seed * 7919 + 13);
        var row = 1;
        for (var x = 0; x < BoltW; x++)
        {
            px[row * BoltW + x] = Color.white;
            if (rnd.NextDouble() < 0.45)
            {
                row = Mathf.Clamp(row + (rnd.Next(2) == 0 ? -1 : 1), 0, BoltH - 1);
                px[row * BoltW + x] = Color.white;
            }
        }
        return boltSp[seed] = Make(BoltW, BoltH, (x, y) => px[y * BoltW + x]);
    }

    private static void OutlineInk(Color[] px, int w, int h)
    {
        bool Solid(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && px[y * w + x].a > 0f;
        var add = new System.Collections.Generic.List<int>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            if (!Solid(x, y) && (Solid(x - 1, y) || Solid(x + 1, y) || Solid(x, y - 1) || Solid(x, y + 1)))
                add.Add(y * w + x);
        foreach (var i in add) px[i] = PersonaUi.Ink;
    }

    /// <summary>Horizontal soft band for the travelling flow pulse.</summary>
    private static Sprite BandSprite()
    {
        if (bandSp != null) return bandSp;
        var tex = new Texture2D(16, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (var x = 0; x < 16; x++)
        {
            var s = Mathf.Sin(Mathf.PI * (x + 0.5f) / 16f);
            tex.SetPixel(x, 0, new Color(1f, 1f, 1f, s * s));
        }
        tex.Apply(false, true);
        return bandSp = Sprite.Create(tex, new Rect(0f, 0f, 16f, 1f), new Vector2(0.5f, 0.5f), 100f);
    }
}
