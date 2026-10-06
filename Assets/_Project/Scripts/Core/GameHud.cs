using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The in-game HUD in the hand-drawn health bar's pixel-frame style: souls
/// counter, flask, the three ability quick-slots, the interact prompt, toasts,
/// banners, the YOU DIED slam and the screen fade. Built lazily on a
/// <see cref="PixelCanvas"/>. Every element is Root (parallax layer) → Motion
/// (entry/exit/idle animation) → content, so the two never fight over a rect.
/// All animation runs on unscaled time — hitstop and pause can't freeze it.
/// </summary>
public sealed class GameHud : MonoBehaviour
{
    private static GameHud instance;

    private PixelCanvas pixels;

    // souls — top-right, fades out when idle (shown on change and in menus)
    private RectTransform soulsMotion, soulsEmber;
    private Image soulsFrame;
    private Text soulsText, soulsPlus;
    private CanvasGroup soulsPlusGroup, soulsGroup;
    private float soulsShown, soulsPunch, soulsShake, soulsRed, soulsPlusT = -1f, soulsAwakeT;
    private const float SoulsLinger = 4f;
    private int soulsTarget;
    private bool soulsInit;

    // flask — the user's imported flask sprites (Resources/FlaskHp/FlaskMana):
    // wood silhouette, glass and liquid are baked in. The charge number stays
    // on the carved round badge; bubbles + a drifting glint animate inside a
    // masked liquid region so the baked liquid still moves.
    private RectTransform flaskMotion, flaskIconRt, digitHost, liquidMask;
    private Image flaskGourd, flaskGlint;
    private readonly Image[] flaskDigits = new Image[2];
    // low-HP vignette + heartbeat shake
    private Image lowVign;
    // quick item — a small socket beside the flask: icon + count badge, dim when empty
    private RectTransform itemRow, itemMotion, itemBadge;
    private Image itemIcon, itemFrame;
    private readonly Image[] itemDigits = new Image[2];
    private float itemPunch;
    private ItemDef shownItem;
    private Inventory inventory;
    // Persona sprint focus-lines — tapered streaks flickering in from the
    // screen edges while the player is at run speed.
    private RectTransform speedLayer;
    private CanvasGroup speedGroup;
    private SpeedLine[] speedLines;
    private float speedLinesK;
    private PlayerLocomotion playerLoco;
    private WallRunController playerWall;
    private sealed class SpeedLine
    {
        public Image img;
        public float jx, jy, len, thick, rate, phase;
    }
    private PlayerHealth playerHealth;
    private PlayerCameraController playerCam;
    private float heartT, lowPulse;
    private readonly Image[] bubbles = new Image[2];
    private readonly float[] bubbleY = { 0.15f, 0.6f };
    private int flaskCharges = -1, flaskMax = 1;
    private bool flaskMana;
    private float flaskWobble, flaskPunch, flaskFlash, flaskLevel = -1f, flaskTint, digitPunch;

    // Row layout (UI px, bottom-left): every box centre sits on the gourd's
    // glass-window line — flask | quick item | selected art | ghost column.
    // The art socket is a fixed 21-texel frame (84 px, opaque well, decor on
    // the outer rim); the item socket and the two ghosts are Thin 9-slices
    // whose wells fit their icons exactly.
    private const float RowY = 100f, FlaskX = 88f, SocketSize = 84f, RowGap = 18f;
    private const float ItemSize = 64f, GhostSize = 52f;
    private const float IconScale = 3.5f, ItemIconScale = 2.5f, GhostIconScale = 1.75f;
    private const float FlaskH = 112f;  // ~0.74× the old gourd row (152) — the flask read too big
    private const float FlaskWinY = 0.27f; // liquid-window centre, normalized (cropped space)
    private float flaskW = 94f;         // set from the loaded sprite's aspect
    private float ItemX => FlaskX + flaskW * 0.5f + RowGap + ItemSize * 0.5f;
    private float ArtX => ItemX + ItemSize * 0.5f + 26f + SocketSize * 0.5f;
    // Liquid region inside each sprite (normalized over the CROPPED art,
    // bottom-left origin) — the baked liquid disk; bubbles/glint clip to it.
    private static readonly Rect LiqRectHp = new Rect(0.256f, 0.097f, 0.448f, 0.341f);
    private static readonly Rect LiqRectMana = new Rect(0.35f, 0.10f, 0.52f, 0.38f);
    // Opaque-bounds crops (alpha>16 scan): the PNG canvases carry different
    // padding, so sizing by canvas aspect stretched one thin / one fat.
    // Rects are in texture pixels, bottom-left origin.
    private static readonly Rect CropRectHp = new Rect(38f, 70f, 429f, 678f);
    private static readonly Rect CropRectMana = new Rect(26f, 91f, 443f, 595f);
    private static Sprite flaskHpSprite, flaskManaSprite;

    /// <summary>Resources-loaded flask art — cropped to its opaque bounds,
    /// point-filtered, sprite-wrapped. Null if Unity hasn't imported the
    /// PNGs (old painter gourd kept as fallback).</summary>
    private static Sprite FlaskSprite(bool mana)
    {
        var cached = mana ? flaskManaSprite : flaskHpSprite;
        if (cached != null) return cached;
        var tex = Resources.Load<Texture2D>(mana ? "FlaskMana" : "FlaskHp");
        if (tex == null) return null;
        tex.filterMode = FilterMode.Point;
        var crop = mana ? CropRectMana : CropRectHp;
        // The importer downscaled the NPOT PNGs once (512x512) and the crop
        // blew up Sprite.Create — never let a texture resize throw here.
        if (crop.xMax > tex.width || crop.yMax > tex.height)
            crop = new Rect(0f, 0f, tex.width, tex.height);
        if (mana) tex = Violet(tex);
        var sp = Sprite.Create(tex, crop, new Vector2(0.5f, 0.5f), 100f);
        if (mana) flaskManaSprite = sp; else flaskHpSprite = sp;
        return sp;
    }

    /// <summary>The Core Energy flask: the imported art's blue/cyan liquid
    /// hue-shifted onto the suit's violet (#8B35E8 / highlight #B76CFF) at
    /// load — a runtime copy (GPU readback, the PNG isn't readable), so the
    /// source art stays untouched. Wood, ink and glass keep their colours.</summary>
    private static Texture2D Violet(Texture2D src)
    {
        var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false) { name = src.name + "_Violet" };
        t.ReadPixels(new Rect(0f, 0f, src.width, src.height), 0, 0);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var px = t.GetPixels();
        for (var i = 0; i < px.Length; i++)
        {
            Color.RGBToHSV(px[i], out var h, out var s, out var v);
            if (s < 0.25f || h < 0.45f || h > 0.72f) continue; // cyan..blue only
            var highlight = h < 0.56f; // the cyan swirl → pale violet
            var c = Color.HSVToRGB(0.75f, highlight ? s * 0.6f : Mathf.Min(1f, s * 1.05f), v);
            c.a = px[i].a;
            px[i] = c;
        }
        t.SetPixels(px);
        t.Apply(false, true);
        t.filterMode = FilterMode.Point;
        t.wrapMode = TextureWrapMode.Clamp;
        return t;
    }

    // floating world UI — the lock-on dot, projected onto this canvas
    private RectTransform worldLayer;
    private Image reticle;
    private LockOnController lockOn;
    private Targetable reticleTarget;
    private float reticlePunch, lockOnFindT;

    // arts — the selected art in one big socket, the other two as small dim
    // ghosts stacked beside it (upper = next, lower = previous)
    private RectTransform skillRow, artMotion;
    private Image artIcon, artGlow, artDigit;
    private readonly Image[] ghostIcons = new Image[2];
    private readonly RectTransform[] ghostMotion = new RectTransform[2];
    private Text artName;
    private CanvasGroup artNameGroup;
    private float artPunch, artNameT = -1f;
    private int shownSelected = -1;
    private IAbilitySlots abilities;
    private PlayerMana mana;
    private AttackController attack;
    private Text commandHint;

    // ultimate — Genshin-style energy orb, bottom-right: liquid fill with a
    // wobbling crest, ready glow/orbiting sparks, shockwave + impact frames on cast
    private UltCharge ult;
    private RectTransform ultRoot, ultMotion, ultIconRt, ultShock;
    private Image ultHalo, ultFill, ultCrest, ultIcon, ultFlash, ultShockImg;
    private readonly RectTransform[] ultSparks = new RectTransform[6];
    private float ultShown, ultTarget, ultPunch, ultFlashK, ultShockT = -1f, ultReadyK;
    private bool ultWasFull;
    private const float UltD = 120f;      // 30-texel ring
    private const float UltFillD = 92f;   // 23-texel disc under the ring's hollow

    // impact frames — stepped full-screen inverted frames with manga burst lines
    private RectTransform impactRoot;
    private Image impactBg;
    private Image[] impactLines;

    // tabs
    private Tab prompt, toast, banner;

    // boss bar
    private BossBar bossBar;

    // death
    private RectTransform deathRoot, deathMotion, deathSub, deathMedal;
    private CanvasGroup deathGroup;
    private Text[] deathLetters;
    private Text deathSubText;
    private Vector2[] deathLetterPos;
    private RectTransform[] deathStripes;
    private Image fade, flash;
    private float flashT;

    /// <summary>Finds or creates the HUD. Every system routes through this so
    /// nothing needs scene wiring.</summary>
    public static GameHud Ensure()
    {
        if (instance != null) return instance;
        instance = FindFirstObjectByType<GameHud>();
        if (instance == null)
            instance = new GameObject("GameHud").AddComponent<GameHud>();
        instance.Build();
        return instance;
    }

    // ================= build =================

    private void Build()
    {
        if (pixels != null) return;
        // Scale 2: a 4 UI px HudArt texel lands on exactly 2 render-texture
        // px, so every art pixel is the same size (1.5 alternates 2/3 px).
        pixels = PixelCanvas.Create("GameHud", 40, 2f, transform, interactive: false);
        var root = pixels.Root;

        worldLayer = PersonaUi.Stretch(root, "World"); // floating bars/reticle sit under the HUD
        reticle = HudArt.Icon(worldLayer, "Reticle", HudArt.LockX(), new Vector2(0.5f, 0.5f), Vector2.zero, 2f);
        reticle.gameObject.SetActive(false);
        // Low-HP vignette — screen-edge glow UNDER every HUD element so it
        // reddens the world, not the frame.
        lowVign = PersonaUi.Stretch(root, "LowHp").gameObject.AddComponent<Image>();
        lowVign.sprite = HudArt.Vignette();
        lowVign.type = Image.Type.Sliced;
        lowVign.pixelsPerUnitMultiplier = 1f / HudArt.T;
        lowVign.raycastTarget = false;
        lowVign.canvasRenderer.SetAlpha(0f);
        BuildSpeedLines(root);
        BuildDeath(root); // behind everything else in the HUD
        BuildSouls(root);
        BuildFlask(root);  // sets flaskW — the row after it lays out from it
        BuildItemSlot(root);
        BuildSlots(root);
        BuildUlt(root);
        prompt = BuildTab(root, "Prompt", new Vector2(0.5f, 0f), new Vector2(0f, 170f), new Vector2(520f, 76f),
            HudArt.Kind.Panel, 26, withKey: true, depth: 1.55f, seed: 3);
        toast = BuildTab(root, "Toast", new Vector2(0.5f, 0f), new Vector2(0f, 270f), new Vector2(600f, 76f),
            HudArt.Kind.Toast, 28, withKey: false, depth: 1.3f, seed: 7);
        banner = BuildTab(root, "Banner", new Vector2(0.5f, 0.66f), Vector2.zero, new Vector2(940f, 124f),
            HudArt.Kind.Panel, 56, withKey: false, depth: 0.95f, seed: 11, curl: true, medallion: true);
        bossBar = BuildBoss(root);
        BuildImpact(root);

        // Screen flash (death impact) + fade — topmost, no parallax.
        flash = PersonaUi.Stretch(root, "Flash").gameObject.AddComponent<Image>();
        flash.color = Color.white;
        flash.raycastTarget = false;
        flash.canvasRenderer.SetAlpha(0f);
        fade = PersonaUi.Stretch(root, "Fade").gameObject.AddComponent<Image>();
        fade.color = PersonaUi.Ink;
        fade.raycastTarget = false;
        fade.canvasRenderer.SetAlpha(0f);

        SoulsWallet.Changed += SetSouls;
        SetSouls(SoulsWallet.Souls);
    }

    /// <summary>Sprint focus-lines: tapered streak sprites parked around the
    /// screen perimeter, pointing inward — manga "concentration lines" in the
    /// HUD palette. Positions are laid out in pixel space once the canvas
    /// rect resolves; a CanvasGroup fades the whole field in/out.</summary>
    private void BuildSpeedLines(RectTransform root)
    {
        speedLayer = PersonaUi.Stretch(root, "SpeedLines");
        speedGroup = speedLayer.gameObject.AddComponent<CanvasGroup>();
        speedGroup.alpha = 0f;
        speedGroup.interactable = false;
        speedGroup.blocksRaycasts = false;
        speedLines = new SpeedLine[6];
        var rng = new System.Random(4242);
        for (var i = 0; i < speedLines.Length; i++)
        {
            var go = new GameObject("L" + i, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(speedLayer, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f); // thick end anchors at the edge
            var img = go.AddComponent<Image>();
            img.sprite = HudArt.Streak();
            img.color = i % 6 == 5 ? PersonaUi.Heart : PersonaUi.Bone; // sparse red accents
            img.raycastTarget = false;
            img.canvasRenderer.SetAlpha(0f);
            speedLines[i] = new SpeedLine
            {
                img = img,
                jx = (float)(rng.NextDouble() - 0.5) * 160f,
                jy = (float)(rng.NextDouble() - 0.5) * 90f,
                len = 70f + (float)rng.NextDouble() * 60f,
                thick = 5f + (float)rng.NextDouble() * 4f,
                rate = 11f + (float)rng.NextDouble() * 6f, // anime step rate, ~12–17fps
                phase = (float)rng.NextDouble() * Mathf.PI * 2f,
            };
        }
    }

    private void BuildSouls(RectTransform root)
    {
        // Top-right, vertically centred on the health frame: the scene frame
        // (margin.y 16 + 568×0.42/2) sits 135 ref-px down = 12.5% of height —
        // the fraction anchor keeps them level at any resolution. Fades out
        // after SoulsLinger seconds idle.
        // One painted plate (HudArt.SoulsPlate) — the health frame mirrored for
        // the right edge: ember socket at the screen edge, count well inward.
        const float T = HudArt.T;
        var size = new Vector2(HudArt.SoulsW, HudArt.SoulsH) * T;
        var r = PersonaUi.Box(root, "Souls", new Vector2(1f, 0.875f), new Vector2(-20f - size.x * 0.5f, 0f), size);
        HudParallax.Attach(r, 0.5f);
        soulsGroup = r.gameObject.AddComponent<CanvasGroup>();
        soulsGroup.alpha = 0f;
        soulsMotion = PersonaUi.Stretch(r, "Motion");
        soulsFrame = PersonaUi.Stretch(soulsMotion, "Plate").gameObject.AddComponent<Image>();
        soulsFrame.sprite = HudArt.SoulsPlate();
        soulsFrame.raycastTarget = false;
        soulsEmber = (RectTransform)HudArt.Icon(soulsMotion, "Ember", HudArt.Ember(), Vector2.zero,
            new Vector2(HudArt.SoulsSocketX, HudArt.SoulsSocketY) * T, 3f).transform;
        var well = HudArt.SoulsWell;
        soulsText = PersonaUi.Label(soulsMotion, "Count", "0", 22, PersonaUi.Bone, TextAnchor.MiddleRight, bold: false, dropShadow: 0f);
        soulsText.verticalOverflow = VerticalWrapMode.Overflow;
        ((RectTransform)soulsText.transform).offsetMin = new Vector2(well.xMin * T, well.yMin * T);
        ((RectTransform)soulsText.transform).offsetMax = new Vector2((well.xMax - HudArt.SoulsW) * T - 6f, (well.yMax - HudArt.SoulsH) * T);

        var plusRt = PersonaUi.Rect(soulsMotion, "Plus", new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(0f, -48f), new Vector2(-20f, -10f));
        soulsPlusGroup = plusRt.gameObject.AddComponent<CanvasGroup>();
        soulsPlus = PersonaUi.Label(plusRt, "Text", "", 22, HudArt.EmberOrange, TextAnchor.MiddleRight, bold: false, dropShadow: 0f);
        soulsPlusGroup.alpha = 0f;
    }

    private void BuildFlask(RectTransform root)
    {
        // Root centred so the liquid window's centre sits on RowY (same rule
        // the gourd had, now in normalized sprite space).
        var sp = FlaskSprite(flaskMana);
        flaskW = sp != null ? FlaskH * sp.rect.width / sp.rect.height : 94f;
        var size = new Vector2(flaskW, FlaskH);
        var winOffset = (FlaskWinY - 0.5f) * FlaskH;
        var r = PersonaUi.Box(root, "Flask", new Vector2(0f, 0f), new Vector2(FlaskX, RowY - winOffset), size);
        HudParallax.Attach(r, 0.8f);
        flaskMotion = PersonaUi.Stretch(r, "Motion");

        // Flask group — imported sprite → liquid mask (bubbles + glint) →
        // carved digit badge. Pivot near the base so a wobble rocks the
        // whole flask, digits included.
        flaskIconRt = PersonaUi.Box(flaskMotion, "Flask", new Vector2(0.5f, 0.5f), Vector2.zero, size);
        flaskIconRt.pivot = new Vector2(0.5f, 0.15f);
        flaskIconRt.anchoredPosition = new Vector2(0f, (0.15f - 0.5f) * size.y);
        flaskGourd = PersonaUi.Stretch(flaskIconRt, "Art").gameObject.AddComponent<Image>();
        flaskGourd.sprite = sp != null ? sp : HudArt.Gourd();
        flaskGourd.raycastTarget = false;

        // Liquid region — a mask over the baked liquid disk so the animating
        // bits stay inside the glass.
        liquidMask = PersonaUi.Rect(flaskIconRt, "LiquidMask", LiqRectHp.min, LiqRectHp.max,
            Vector2.zero, Vector2.zero);
        liquidMask.gameObject.AddComponent<RectMask2D>();
        for (var i = 0; i < bubbles.Length; i++)
            bubbles[i] = HudArt.Quad(liquidMask, "Bubble" + i, Color.white, Vector2.one);
        flaskGlint = HudArt.Quad(liquidMask, "Glint", PersonaUi.WithAlpha(PersonaUi.Bone, 0.5f), new Vector2(3f, 0.6f));
        ApplyFlaskSprite();

        // The charge number stays on the round badge pinned to the flask's
        // bottom-right rim — a separate sprite overlapping the silhouette.
        digitHost = PersonaUi.Box(flaskIconRt, "Digits", new Vector2(0.74f, 0.07f),
            Vector2.zero, new Vector2(44f, 44f));
        var tagImg = PersonaUi.Stretch(digitHost, "Tag").gameObject.AddComponent<Image>();
        tagImg.sprite = HudArt.Tag();
        tagImg.raycastTarget = false;
        for (var i = 0; i < flaskDigits.Length; i++)
            flaskDigits[i] = HudArt.Icon(digitHost, "D" + i, HudArt.Digit(0, outlined: false),
                new Vector2(0.5f, 0.5f), new Vector2((i - 0.5f) * 6.8f, -3f), 3f);
    }

    /// <summary>The ult orb: halo → well → crest + liquid fill (vertical
    /// Filled discs) → flash → emblem → ring frame → shockwave ring, with
    /// orbiting sparks and the T key on a carved badge. Hidden until an
    /// <see cref="UltCharge"/> binds.</summary>
    private void BuildUlt(RectTransform root)
    {
        ultRoot = PersonaUi.Box(root, "Ult", new Vector2(1f, 0f), new Vector2(-100f, 104f), new Vector2(UltD, UltD));
        HudParallax.Attach(ultRoot, 1f);
        ultMotion = PersonaUi.Stretch(ultRoot, "Motion");
        ultHalo = HudArt.Icon(ultMotion, "Halo", HudArt.Halo(), new Vector2(0.5f, 0.5f), Vector2.zero, 7f);
        ultHalo.color = PersonaUi.Heart;
        ultHalo.canvasRenderer.SetAlpha(0f);
        var disc = HudArt.Disc(23);
        var hot = new Color(1f, 0.26f, 0.2f); // sword red — the BigSword accent
        HudArt.Icon(ultMotion, "Well", disc, new Vector2(0.5f, 0.5f), Vector2.zero).color = PersonaUi.Night;
        ultCrest = FillDisc(ultMotion, "Crest", disc, hot);
        ultFill = FillDisc(ultMotion, "Fill", disc, PersonaUi.Blood);
        ultFlash = HudArt.Icon(ultMotion, "Flash", disc, new Vector2(0.5f, 0.5f), Vector2.zero);
        ultFlash.canvasRenderer.SetAlpha(0f);
        ultIcon = HudArt.Icon(ultMotion, "Icon", HudArt.Eclipse(), new Vector2(0.5f, 0.5f), Vector2.zero, 3.5f);
        ultIconRt = ultIcon.rectTransform;
        HudArt.Icon(ultMotion, "Ring", HudArt.UltRing(), new Vector2(0.5f, 0.5f), Vector2.zero);
        for (var i = 0; i < ultSparks.Length; i++)
        {
            var img = i % 3 == 0
                ? (Graphic)PersonaUi.Poly(PersonaUi.Box(ultMotion, "Star" + i, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(22f, 22f)),
                    "S", PolyGraphic.Shape.Star, PersonaUi.Bone)
                : HudArt.Quad(ultMotion, "Spark" + i, i % 2 == 0 ? hot : PersonaUi.Heart, Vector2.one);
            if (img is PolyGraphic star) { star.StarPoints = 4; star.StarInner = 0.25f; }
            ultSparks[i] = i % 3 == 0 ? (RectTransform)img.transform.parent : img.rectTransform;
            ultSparks[i].gameObject.SetActive(false);
        }
        ultShockImg = HudArt.Icon(ultMotion, "Shock", HudArt.UltRing(), new Vector2(0.5f, 0.5f), Vector2.zero);
        ultShockImg.color = hot;
        ultShock = ultShockImg.rectTransform;
        ultShockImg.canvasRenderer.SetAlpha(0f);
        var key = PersonaUi.Box(ultMotion, "Key", new Vector2(0.14f, 0.14f), Vector2.zero, new Vector2(44f, 44f));
        PersonaUi.Stretch(key, "Tag").gameObject.AddComponent<Image>().sprite = HudArt.Tag();
        PersonaUi.Label(key, "Glyph", "T", 22, PersonaUi.Bone, TextAnchor.MiddleCenter, dropShadow: 0f);
        ultRoot.gameObject.SetActive(false);
    }

    private static Image FillDisc(RectTransform parent, string name, Sprite disc, Color col)
    {
        var img = HudArt.Icon(parent, name, disc, new Vector2(0.5f, 0.5f), Vector2.zero);
        img.type = Image.Type.Filled;
        img.fillMethod = Image.FillMethod.Vertical;
        img.fillOrigin = (int)Image.OriginVertical.Bottom;
        img.fillAmount = 0f;
        img.color = col;
        return img;
    }

    /// <summary>Impact-frame layer: a full-screen slab plus inward manga
    /// burst lines from the screen edges. Hidden until <see cref="ImpactFrames"/>.</summary>
    private void BuildImpact(RectTransform root)
    {
        impactRoot = PersonaUi.Stretch(root, "Impact");
        impactBg = impactRoot.gameObject.AddComponent<Image>();
        impactBg.raycastTarget = false;
        impactLines = new Image[28];
        for (var i = 0; i < impactLines.Length; i++)
        {
            var rt = PersonaUi.Box(impactRoot, "L" + i, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(10f, 10f));
            rt.pivot = new Vector2(0f, 0.5f); // thick end on the edge
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = HudArt.Streak();
            img.raycastTarget = false;
            impactLines[i] = img;
        }
        impactRoot.gameObject.SetActive(false);
    }

    /// <summary>The consumable quick-slot — a small Thin socket right beside
    /// the flask (consumables together), same RowY. The count rides the same
    /// carved round badge as the flask; an empty satchel leaves the socket
    /// dim instead of collapsing the row.</summary>
    private void BuildItemSlot(RectTransform root)
    {
        itemRow = PersonaUi.Box(root, "Item", new Vector2(0f, 0f), new Vector2(ItemX, RowY), new Vector2(ItemSize, ItemSize));
        HudParallax.Attach(itemRow, 0.95f);
        itemMotion = PersonaUi.Stretch(itemRow, "Motion");
        itemFrame = HudArt.Build(itemMotion, "Frame", HudArt.Kind.Thin);
        itemIcon = HudArt.Icon(itemMotion, "Icon", HudArt.Gem(), new Vector2(0.5f, 0.5f), Vector2.zero, ItemIconScale);
        itemBadge = PersonaUi.Box(itemMotion, "Badge", new Vector2(0.92f, 0.08f), Vector2.zero, new Vector2(44f, 44f));
        var tag = PersonaUi.Stretch(itemBadge, "Tag").gameObject.AddComponent<Image>();
        tag.sprite = HudArt.Tag();
        tag.raycastTarget = false;
        for (var i = 0; i < itemDigits.Length; i++)
            itemDigits[i] = HudArt.Icon(itemBadge, "D" + i, HudArt.Digit(0, outlined: false), new Vector2(0.5f, 0.5f), Vector2.zero, 3f);
        RefreshItem();
    }

    /// <summary>Swap the flask art + remeasure — shared by build and the
    /// hp↔mana mode swap so both stay consistent.</summary>
    private void ApplyFlaskSprite()
    {
        var sp = FlaskSprite(flaskMana);
        if (flaskGourd != null) flaskGourd.sprite = sp != null ? sp : HudArt.Gourd();
        if (sp == null || liquidMask == null) return;
        // Resize the icon to the art's aspect; the mask + digit badge are
        // anchored normalized so they follow.
        flaskW = FlaskH * sp.rect.width / sp.rect.height;
        flaskIconRt.sizeDelta = new Vector2(flaskW, FlaskH);
        var lr = flaskMana ? LiqRectMana : LiqRectHp;
        liquidMask.anchorMin = lr.min;
        liquidMask.anchorMax = lr.max;
        liquidMask.offsetMin = liquidMask.offsetMax = Vector2.zero;
    }

    private void BuildSlots(RectTransform root)
    {
        // One big socket for the selected art; the other two arts ride a
        // ghost column to its right. The art's name flashes above the socket
        // on a switch, then fades — nothing lingers over the flask's stem.
        skillRow = PersonaUi.Box(root, "Abilities", new Vector2(0f, 0f), new Vector2(ArtX, RowY),
            new Vector2(SocketSize, SocketSize));
        HudParallax.Attach(skillRow, 1.15f);
        artMotion = PersonaUi.Stretch(skillRow, "Motion");
        var glowRt = PersonaUi.Rect(artMotion, "Glow", Vector2.zero, Vector2.one,
            new Vector2(-2f * HudArt.T, -2f * HudArt.T), new Vector2(2f * HudArt.T, 2f * HudArt.T));
        artGlow = glowRt.gameObject.AddComponent<Image>();
        artGlow.sprite = HudArt.GlowRim();
        artGlow.type = Image.Type.Sliced;
        artGlow.pixelsPerUnitMultiplier = 1f / HudArt.T;
        artGlow.raycastTarget = false;
        // Fixed socket frame — vine decor is BAKED into the outer rim, so it
        // can never cover the icon.
        HudArt.Icon(artMotion, "Frame", HudArt.SocketFrame(0), new Vector2(0.5f, 0.5f), Vector2.zero);
        artIcon = HudArt.Icon(artMotion, "Icon", HudArt.Ember(), new Vector2(0.5f, 0.5f), new Vector2(0f, 2f), IconScale);
        artDigit = HudArt.Icon(artMotion, "Key", HudArt.Digit(1, outlined: false), new Vector2(1f, 0f), new Vector2(-9f, 9f), 3.5f);

        for (var i = 0; i < ghostIcons.Length; i++)
        {
            var g = PersonaUi.Box(skillRow, "Ghost" + i, new Vector2(1f, 0.5f),
                new Vector2(10f + GhostSize * 0.5f, (i == 0 ? 1f : -1f) * (GhostSize * 0.5f + 2f)), new Vector2(GhostSize, GhostSize));
            ghostMotion[i] = PersonaUi.Stretch(g, "Motion");
            HudArt.Build(ghostMotion[i], "Frame", HudArt.Kind.Thin).color = new Color(0.75f, 0.7f, 0.8f);
            ghostIcons[i] = HudArt.Icon(ghostMotion[i], "Icon", HudArt.Ember(), new Vector2(0.5f, 0.5f), Vector2.zero, GhostIconScale);
        }

        var nameRt = PersonaUi.Box(skillRow, "ArtName", new Vector2(0.5f, 1f), new Vector2(0f, 24f), new Vector2(420f, 30f));
        artNameGroup = nameRt.gameObject.AddComponent<CanvasGroup>();
        artNameGroup.alpha = 0f;
        artName = PersonaUi.Label(nameRt, "Text", "", 20, PersonaUi.Bone, TextAnchor.MiddleCenter, bold: true, dropShadow: 3f);

        // Modifier-held / aerial-session command hints (detail §254) — one
        // left-pinned line above the flask row, empty most of the time.
        var hintRt = PersonaUi.Box(root, "CommandHint", new Vector2(0f, 0f), new Vector2(24f, RowY + 140f), new Vector2(760f, 24f));
        hintRt.pivot = new Vector2(0f, 0.5f);
        commandHint = PersonaUi.Label(hintRt, "Text", "", 16, PersonaUi.Bone, TextAnchor.MiddleLeft, bold: true, dropShadow: 3f);

        skillRow.gameObject.SetActive(false); // appears once an ability system binds
    }

    private void BuildDeath(RectTransform root)
    {
        // Stretch to the real canvas width, including ultrawide displays.
        // Keep the backdrop separate from the animated banner for stable edges.
        deathRoot = PersonaUi.Stretch(root, "Death");
        deathGroup = deathRoot.gameObject.AddComponent<CanvasGroup>();
        deathGroup.blocksRaycasts = false;
        deathGroup.interactable = false;
        var dim = PersonaUi.Stretch(deathRoot, "Dim").gameObject.AddComponent<Image>();
        dim.color = PersonaUi.WithAlpha(PersonaUi.Ink, 0.58f);
        dim.raycastTarget = false;

        // Soft blood glow behind the band so the title sits in a red bloom.
        var glow = PersonaUi.Rect(deathRoot, "Glow", new Vector2(0.15f, 0.53f), new Vector2(0.85f, 0.53f),
            new Vector2(0f, -260f), new Vector2(0f, 260f)).gameObject.AddComponent<Image>();
        glow.sprite = HudArt.Radial();
        glow.raycastTarget = false;

        // The band is drawn in the health frame's language: mass + copper rim
        // 9-slice across the full width, lumps/thorns/curl along both edges,
        // blood seams inside, the broken-heart medal pinned on the top rim.
        deathMotion = PersonaUi.Rect(deathRoot, "Motion", new Vector2(0f, 0.53f), new Vector2(1f, 0.53f),
            new Vector2(-24f, -120f), new Vector2(24f, 120f));
        HudArt.Build(deathMotion, "Frame", HudArt.Kind.Panel);
        HudArt.Dress(deathMotion, 13, lumps: 7, thorns: 3, curl: true);
        foreach (var top in new[] { true, false })
        {
            var anchorY = top ? 1f : 0f;
            var rule = PersonaUi.Rect(deathMotion, top ? "SeamT" : "SeamB", new Vector2(0.04f, anchorY), new Vector2(0.96f, anchorY),
                new Vector2(0f, top ? -40f : 32f), new Vector2(0f, top ? -32f : 40f)).gameObject.AddComponent<Image>();
            rule.color = PersonaUi.WithAlpha(PersonaUi.Blood, 0.9f);
            rule.raycastTarget = false;
        }
        deathMedal = (RectTransform)HudArt.Icon(deathMotion, "Medal", HudArt.DeathMedal(), new Vector2(0.5f, 1f),
            new Vector2(0f, 4f)).transform;

        // One kerned title instead of uneven individual ransom letters.
        // Best-fit only reduces the font on unusually narrow aspect ratios.
        var title = PersonaUi.Rect(deathMotion, "Title", new Vector2(0.1f, 0.5f), new Vector2(0.9f, 0.5f),
            new Vector2(0f, -72f), new Vector2(0f, 76f));
        var text = PersonaUi.Label(title, "Text", "YOU DIED", 136, PersonaUi.Bone, TextAnchor.MiddleCenter,
            bold: true, dropShadow: 0f);
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = 52;
        text.resizeTextMaxSize = 136;
        // Hard blood offset shadow — a pixel print, not a soft blur.
        var shadow = text.gameObject.AddComponent<Shadow>();
        shadow.effectColor = PersonaUi.Blood;
        shadow.effectDistance = new Vector2(6f, -6f);
        deathLetters = new[] { text };
        deathLetterPos = new[] { title.anchoredPosition };
        title.gameObject.SetActive(false);
        deathStripes = System.Array.Empty<RectTransform>();

        // This is populated before GameLoop drops the wallet, as before.
        // Hangs under the band on the dim, like a tag off the frame.
        deathSub = PersonaUi.Box(deathMotion, "Sub", new Vector2(0.5f, 0f), new Vector2(0f, -60f), new Vector2(560f, 34f));
        deathSubText = PersonaUi.Label(deathSub, "Text", "", 26, PersonaUi.WithAlpha(PersonaUi.Bone, 0.8f),
            TextAnchor.MiddleCenter, bold: false, dropShadow: 0f);
        deathSub.gameObject.SetActive(false);
        deathGroup.alpha = 0f;
    }

    // ================= tabs (prompt / toast / banner) =================

    /// <summary>Pops in (overshoot, key cap first, label wipe), bobs while
    /// shown, squashes out. Show() during the exit reverses smoothly.</summary>
    private sealed class Tab
    {
        public RectTransform root, motion, key, labelHost;
        public CanvasGroup group;
        public Text label, keyText;
        public RectMask2D mask;
        public Image frame;

        private enum S { Hidden, Entering, Shown, Exiting }
        private S s = S.Hidden;
        private float t, bobT, punch, hold = -1f;

        public void Show(string keyLabel, string text, float holdSeconds = -1f)
        {
            if (keyText != null) keyText.text = keyLabel ?? "";
            var changed = label.text != text;
            label.text = text;
            hold = holdSeconds;
            switch (s)
            {
                case S.Hidden:
                    root.gameObject.SetActive(true);
                    s = S.Entering;
                    t = 0f;
                    break;
                case S.Exiting:
                    s = S.Entering;
                    t = Mathf.Clamp01(1f - t) * 0.5f; // resume from about where the exit left it
                    break;
                case S.Shown:
                    if (changed) punch = 0.1f;
                    break;
            }
        }

        public void Hide()
        {
            if (s == S.Hidden || s == S.Exiting) return;
            s = S.Exiting;
            t = 0f;
        }

        public void Tick(float dt)
        {
            switch (s)
            {
                case S.Entering:
                {
                    t += dt / 0.28f;
                    var e = PersonaUi.EaseOutBack(t, 2.2f);
                    motion.anchoredPosition = Vector2.LerpUnclamped(new Vector2(0f, -40f), Vector2.zero, e);
                    motion.localScale = Vector3.LerpUnclamped(new Vector3(0.6f, 0.4f, 1f), Vector3.one, e);
                    group.alpha = Mathf.Clamp01(t * 3f);
                    var secs = t * 0.28f;
                    if (key != null) key.localScale = Vector3.one * PersonaUi.EaseOutBack(Mathf.Clamp01(secs / 0.2f), 2.6f);
                    Reveal(PersonaUi.EaseOutCubic(Mathf.Clamp01((secs - 0.06f) / 0.22f)));
                    if (t >= 1f) { s = S.Shown; bobT = 0f; Reveal(1f); }
                    break;
                }
                case S.Shown:
                {
                    bobT += dt;
                    punch = Mathf.Lerp(punch, 0f, 1f - Mathf.Exp(-18f * dt));
                    motion.anchoredPosition = new Vector2(0f, Mathf.Sin(bobT * Mathf.PI * 2f * 1.6f) * 2f);
                    motion.localScale = new Vector3(1f + punch, 1f, 1f);
                    if (key != null)
                    {
                        var kp = Mathf.Repeat(bobT, 1.2f) / 1.2f;
                        key.localScale = Vector3.one * (1f + (kp < 0.2f ? Mathf.Sin(kp / 0.2f * Mathf.PI) * 0.06f : 0f));
                    }
                    if (hold > 0f && (hold -= dt) <= 0f) Hide();
                    break;
                }
                case S.Exiting:
                {
                    t += dt / 0.16f;
                    var e = PersonaUi.EaseInCubic(t);
                    motion.localScale = Vector3.Lerp(Vector3.one, new Vector3(1.15f, 0.2f, 1f), e);
                    motion.anchoredPosition = new Vector2(0f, -16f * e);
                    group.alpha = 1f - Mathf.Clamp01(t);
                    if (t >= 1f) { s = S.Hidden; root.gameObject.SetActive(false); }
                    break;
                }
            }
        }

        private void Reveal(float k)
        {
            if (mask == null) return;
            var w = labelHost.rect.width;
            mask.padding = new Vector4(0f, 0f, w * (1f - k), 0f);
        }
    }

    // ================= boss bar =================

    /// <summary>Bottom-centre boss bar: name over the left rim, heart-red fill
    /// draining right-to-left with a dark tip and a pale damage ghost.
    /// Slides up on bind, holds a beat after the kill, then sinks.</summary>
    private sealed class BossBar
    {
        public RectTransform root, motion;
        public CanvasGroup group;
        public Text nameText;
        public Image fill, ghost;
        public float barW = 1f;

        private Health boss;
        private enum S { Hidden, Entering, Shown, Dying, Exiting }
        private S s = S.Hidden;
        private float t, shown = -1f, ghostV = -1f, dieT;

        public void Show(Health b, string displayName, float phaseNotch)
        {
            boss = b;
            nameText.text = displayName != null ? displayName.ToUpperInvariant() : "";
            shown = ghostV = -1f;
            dieT = 0f;
            if (s == S.Hidden)
            {
                root.gameObject.SetActive(true);
                s = S.Entering;
                t = 0f;
            }
            else s = S.Shown; // re-bind mid-fight (e.g. phase name change)
        }

        public void Hide()
        {
            if (s == S.Hidden || s == S.Exiting) return;
            s = S.Exiting;
            t = 0f;
        }

        public void Tick(float dt)
        {
            if (s == S.Hidden) return;
            var frac = boss != null ? boss.Current / Mathf.Max(1f, boss.Max) : 0f;
            var dead = boss == null || boss.IsDead;
            if (s == S.Shown && dead) { s = S.Dying; dieT = 0f; shown = 0f; }
            if (s == S.Dying)
            {
                dieT += dt;
                if (dieT >= 1.1f) Hide(); // let the empty bar read a beat
            }

            if (shown < 0f) shown = ghostV = frac;
            shown = Mathf.Lerp(shown, frac, 1f - Mathf.Exp(-14f * dt));
            ghostV = frac < ghostV ? Mathf.MoveTowards(ghostV, frac, dt / 1.1f) : frac;
            ((RectTransform)fill.transform).sizeDelta = new Vector2(Mathf.Clamp01(shown) * barW, ((RectTransform)fill.transform).sizeDelta.y);
            ((RectTransform)ghost.transform).sizeDelta = new Vector2(Mathf.Clamp01(ghostV) * barW, ((RectTransform)ghost.transform).sizeDelta.y);

            switch (s)
            {
                case S.Entering:
                    t += dt / 0.3f;
                    var e = PersonaUi.EaseOutBack(t, 2f);
                    motion.anchoredPosition = Vector2.LerpUnclamped(new Vector2(0f, -34f), Vector2.zero, e);
                    motion.localScale = Vector3.LerpUnclamped(new Vector3(1.06f, 0.5f, 1f), Vector3.one, e);
                    group.alpha = Mathf.Clamp01(t * 2.4f);
                    if (t >= 1f) { s = S.Shown; motion.anchoredPosition = Vector2.zero; motion.localScale = Vector3.one; }
                    break;
                case S.Exiting:
                    t += dt / 0.22f;
                    group.alpha = 1f - Mathf.Clamp01(t);
                    motion.anchoredPosition = new Vector2(0f, -18f * PersonaUi.EaseInCubic(t));
                    if (t >= 1f) { s = S.Hidden; root.gameObject.SetActive(false); }
                    break;
            }
        }
    }

    private BossBar BuildBoss(RectTransform root)
    {
        var bar = new BossBar();
        // Top-of-frame per the reference — the name sits between the bar's top
        // edge and the screen edge instead of reading up from below.
        bar.root = PersonaUi.Box(root, "BossBar", new Vector2(0.5f, 1f), new Vector2(0f, -64f), new Vector2(880f, 76f));
        HudParallax.Attach(bar.root, 0.85f);
        bar.motion = PersonaUi.Stretch(bar.root, "Motion");
        bar.group = bar.motion.gameObject.AddComponent<CanvasGroup>();

        // Slim bar: the Thin rim (ink/wood/ink) with knot ends and light dressing.
        var body = PersonaUi.Rect(bar.motion, "Bar", Vector2.zero, Vector2.one, new Vector2(36f, 0f), new Vector2(-36f, -36f));
        HudArt.Build(body, "Frame", HudArt.Kind.Thin);
        HudArt.KnotCap(body, false);
        HudArt.KnotCap(body, true);
        HudArt.Dress(body, 17, lumps: 2, thorns: 1, curl: true);

        var host = PersonaUi.Rect(body, "FillHost", Vector2.zero, Vector2.one, new Vector2(12f, 12f), new Vector2(-12f, -12f));
        // Fixed reference width — offsets pin the fills to the host's left edge.
        bar.barW = 784f;
        var well = PersonaUi.Stretch(host, "Well").gameObject.AddComponent<Image>();
        well.color = PersonaUi.Ink;
        well.raycastTarget = false;
        bar.ghost = BarImage(host, "Ghost", HudArt.Amber, bar.barW, 0f);
        bar.fill = BarImage(host, "Fill", PersonaUi.Heart, bar.barW, 0f);
        // Reads the right-to-left drain: a lit top edge and a dark tip on the
        // receding right end of the fill.
        var sheenRt = PersonaUi.Rect((RectTransform)bar.fill.transform, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f),
            new Vector2(0f, -HudArt.T * 0.5f), Vector2.zero);
        var sheen = sheenRt.gameObject.AddComponent<Image>();
        sheen.color = new Color(1f, 0.42f, 0.36f, 0.6f);
        sheen.raycastTarget = false;
        var tipRt = PersonaUi.Rect((RectTransform)bar.fill.transform, "Tip", new Vector2(1f, 0f), new Vector2(1f, 1f),
            new Vector2(-HudArt.T, 0f), Vector2.zero);
        var tip = tipRt.gameObject.AddComponent<Image>();
        tip.color = PersonaUi.Blood;
        tip.raycastTarget = false;

        // The name is built AFTER the fills so it renders in front of the
        // health bar, and anchored bottom-left just ABOVE the bar's top edge —
        // it never overlaps the fill.
        bar.nameText = PersonaUi.Label(body, "Name", "", 22, PersonaUi.Bone, TextAnchor.LowerLeft, bold: true, dropShadow: 4f);
        var nameRt = (RectTransform)bar.nameText.transform;
        nameRt.anchorMin = nameRt.anchorMax = new Vector2(0f, 1f);
        nameRt.pivot = new Vector2(0f, 0f);
        nameRt.sizeDelta = new Vector2(620f, 30f);
        nameRt.anchoredPosition = new Vector2(14f, 6f);

        // No phase marker — the bar drains right-to-left plain, by request.

        bar.group.alpha = 0f;
        bar.root.gameObject.SetActive(false);
        return bar;
    }

    private static Image BarImage(RectTransform host, string name, Color color, float w, float h)
    {
        var rt = PersonaUi.Rect(host, name, new Vector2(0f, 0f), new Vector2(0f, 1f),
            Vector2.zero, new Vector2(w, 0f));
        // Left pivot: sizeDelta.x writes keep the left edge pinned so the fill
        // drains right-to-left — a centre pivot collapses both ends to mid.
        // The X anchors are collapsed, so the pivot change alone SHIFTS the
        // rect by (0.5 * w) — anchoredPosition must be re-zeroed after it.
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>Bind the boss bar to a Health — slides in; drains as it fights.</summary>
    public static void Boss(Health boss, string displayName, float phaseNotch = 0.5f)
        => Ensure().bossBar.Show(boss, displayName, phaseNotch);

    /// <summary>Early clear (arena reset, player fled). Death auto-holds+clears.</summary>
    public static void BossClear()
    {
        if (instance != null) instance.bossBar?.Hide();
    }

    /// <summary>The world-anchored layer (reticle and damage numerals).</summary>
    public static RectTransform WorldLayer => Ensure().worldLayer;

    /// <summary>World position → worldLayer local point, via viewport so it stays
    /// correct while the camera targets RetroScreen's low-res RT. False = off-screen.</summary>
    public static bool WorldToLocal(Vector3 world, out Vector2 local)
    {
        local = default;
        var h = Ensure();
        var cam = Camera.main;
        if (cam == null) return false;
        var vp = cam.WorldToViewportPoint(world);
        if (vp.z <= 0f) return false;
        return h.pixels.ScreenToLocal(h.worldLayer, new Vector2(vp.x * Screen.width, vp.y * Screen.height), out local);
    }


    private Tab BuildTab(RectTransform root, string name, Vector2 anchor, Vector2 pos, Vector2 size,
        HudArt.Kind kind, int fontSize, bool withKey, float depth, int seed, bool curl = false, bool medallion = false)
    {
        var tab = new Tab();
        tab.root = PersonaUi.Box(root, name, anchor, pos, size);
        HudParallax.Attach(tab.root, depth);
        tab.motion = PersonaUi.Stretch(tab.root, "Motion");
        tab.group = tab.motion.gameObject.AddComponent<CanvasGroup>();

        var leftInset = 0f;
        if (withKey)
        {
            tab.key = PersonaUi.Box(tab.motion, "Key", new Vector2(0f, 0.5f), new Vector2(34f, 0f), new Vector2(68f, 68f));
            HudArt.Build(tab.key, "Frame", HudArt.Kind.Key);
            tab.keyText = PersonaUi.Label(tab.key, "Glyph", "E", 30, PersonaUi.Ink, TextAnchor.MiddleCenter, dropShadow: 0f);
            leftInset = 76f;
        }
        var body = PersonaUi.Rect(tab.motion, "Body", Vector2.zero, Vector2.one, new Vector2(leftInset, 0f), Vector2.zero);
        tab.frame = HudArt.Build(body, "Frame", kind);
        HudArt.Dress(body, seed, lumps: size.x > 700f ? 3 : 2, thorns: 1, curl: curl);
        HudArt.KnotCap(body, true);
        var textLeft = 24f;
        if (medallion)
        {
            // Big banners open with the health frame's heart medallion.
            HudArt.Icon(body, "Medallion", HudArt.Medallion(), new Vector2(0f, 0.5f), new Vector2(8f, 0f));
            HudArt.Icon(body, "Heart", HudArt.Heart(), new Vector2(0f, 0.5f), new Vector2(8f, 0f));
            textLeft = 60f;
        }
        else if (!withKey) HudArt.KnotCap(body, false);
        tab.labelHost = PersonaUi.Rect(body, "LabelHost", Vector2.zero, Vector2.one, new Vector2(textLeft, 0f), new Vector2(-24f, 0f));
        tab.mask = tab.labelHost.gameObject.AddComponent<RectMask2D>();
        tab.label = PersonaUi.Label(tab.labelHost, "Text", "", fontSize,
            kind == HudArt.Kind.Key ? PersonaUi.Ink : PersonaUi.Bone, TextAnchor.MiddleCenter, bold: false, dropShadow: 0f);
        tab.group.alpha = 0f;
        tab.root.gameObject.SetActive(false);
        return tab;
    }

    // ================= per-frame =================

    private void Update()
    {
        var dt = Time.unscaledDeltaTime;
        prompt?.Tick(dt);
        toast?.Tick(dt);
        banner?.Tick(dt);
        bossBar?.Tick(dt);
        TickSouls(dt);
        TickFlask(dt);
        TickSlots(dt);
        TickItem(dt);
        TickUlt(dt);
        TickLowHp(dt);
        TickSpeedLines(dt);
        TickWorld(dt);
        RefreshHintText();
        if (flashT > 0f)
        {
            flashT = Mathf.Max(0f, flashT - dt);
            flash.canvasRenderer.SetAlpha(flashT / 0.07f * 0.35f);
        }
    }

    private void TickSouls(float dt)
    {
        if (soulsText == null) return;
        // Fade when idle: awake after a change, always up in menus / at rest.
        soulsAwakeT = Mathf.Max(0f, soulsAwakeT - dt);
        var wantSouls = soulsAwakeT > 0f || soulsPlusT >= 0f || UiGates.MenuOpen || GameLoop.IsResting;
        soulsGroup.alpha = Mathf.MoveTowards(soulsGroup.alpha, wantSouls ? 1f : 0f, dt * (wantSouls ? 6f : 1.5f));
        // Roll toward the target — ~0.4s regardless of the gap.
        if (!Mathf.Approximately(soulsShown, soulsTarget))
        {
            var speed = Mathf.Max(20f, Mathf.Abs(soulsTarget - soulsShown) / 0.4f);
            soulsShown = Mathf.MoveTowards(soulsShown, soulsTarget, speed * dt);
            soulsText.text = Mathf.RoundToInt(soulsShown).ToString();
        }
        soulsPunch = Mathf.Lerp(soulsPunch, 0f, 1f - Mathf.Exp(-14f * dt));
        soulsShake = Mathf.Max(0f, soulsShake - dt);
        soulsRed = Mathf.Max(0f, soulsRed - dt * 2f);
        soulsMotion.localScale = Vector3.one * (1f + soulsPunch);
        soulsMotion.anchoredPosition = soulsShake > 0f ? Random.insideUnitCircle * 8f * (soulsShake / 0.35f) : Vector2.zero;
        soulsFrame.color = Color.Lerp(Color.white, new Color(1f, 0.35f, 0.3f), soulsRed);
        soulsText.color = Color.Lerp(PersonaUi.Bone, PersonaUi.Heart, soulsRed);
        // The ember breathes: a slow uneven flicker, flaring on a gain.
        var fl = Mathf.Sin(Time.unscaledTime * 5.3f) * 0.5f + Mathf.Sin(Time.unscaledTime * 8.9f) * 0.5f;
        soulsEmber.localScale = new Vector3(1f + soulsPunch * 1.5f, 1f + fl * 0.04f + soulsPunch * 2f, 1f);

        if (soulsPlusT >= 0f)
        {
            soulsPlusT += dt;
            var k = soulsPlusT / 0.9f;
            ((RectTransform)soulsPlus.transform).anchoredPosition = new Vector2(0f, -40f * PersonaUi.EaseOutCubic(k));
            soulsPlusGroup.alpha = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f;
            if (k >= 1f) { soulsPlusT = -1f; soulsPlusGroup.alpha = 0f; }
        }
    }

    private void TickFlask(float dt)
    {
        if (flaskMotion == null) return;
        flaskWobble = Mathf.Max(0f, flaskWobble - dt);
        flaskPunch = Mathf.Lerp(flaskPunch, 0f, 1f - Mathf.Exp(-14f * dt));
        flaskFlash = Mathf.Max(0f, flaskFlash - dt * 3f);
        var w = flaskWobble / 0.3f;
        flaskIconRt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin((1f - w) * Mathf.PI * 4f) * 12f * w);
        flaskMotion.localScale = Vector3.one * (1f + flaskPunch);
        digitPunch = Mathf.Lerp(digitPunch, 0f, 1f - Mathf.Exp(-14f * dt));
        digitHost.localScale = Vector3.one * (1f + digitPunch);

        // Liquid eases to the charge level and slightly sloshes while wobbling.
        // Charge level still eases (drives the empty look + bubble presence)
        // even though the baked sprite can't drain.
        var level = flaskCharges > 0 ? flaskCharges / (float)Mathf.Max(1, flaskMax) : 0f;
        if (flaskLevel < 0f) flaskLevel = level;
        flaskLevel = Mathf.MoveTowards(flaskLevel, level, dt * 1.6f);
        var hasLiquid = flaskLevel > 0.02f;

        // Health red ↔ mana blue crossfade tints the animated bits; refill
        // warms them for a beat.
        flaskTint = Mathf.MoveTowards(flaskTint, flaskMana ? 1f : 0f, dt * 5f);
        var liquid = Color.Lerp(PersonaUi.Heart, HudArt.ManaBlue, flaskTint);
        liquid = Color.Lerp(liquid, new Color(1f, 0.72f, 0.5f), flaskFlash * 0.55f);

        // Liquid animation inside the masked region: the mask counter-rocks
        // the bottle (the slosh), two bubbles rise + pop at the surface, and
        // a pale glint drifts slowly across the disk.
        liquidMask.localRotation = Quaternion.Euler(0f, 0f,
            Mathf.Sin((1f - w) * Mathf.PI * 4f - 0.6f) * -9f * w);
        var mr = liquidMask.rect;
        var glintT = Mathf.PingPong(Time.unscaledTime * 0.35f, 1f);
        ((RectTransform)flaskGlint.transform).anchoredPosition = new Vector2(
            Mathf.Lerp(-mr.width * 0.22f, mr.width * 0.22f, glintT),
            mr.height * 0.28f + Mathf.Sin(Time.unscaledTime * 1.7f) * 3f);
        flaskGlint.canvasRenderer.SetAlpha(hasLiquid ? 0.4f + flaskFlash * 0.4f : 0f);
        for (var i = 0; i < bubbles.Length; i++)
        {
            bubbleY[i] += dt * (0.12f + 0.06f * i);
            if (bubbleY[i] > 1f) bubbleY[i] = 0f;
            var bx = mr.width * (i == 0 ? -0.18f : 0.22f) + Mathf.Sin(bubbleY[i] * 9f + i) * mr.width * 0.1f;
            var by = Mathf.Lerp(-mr.height * 0.42f, mr.height * 0.42f, bubbleY[i]);
            var b = bubbles[i];
            if (b == null) continue; // partial build guard
            b.enabled = hasLiquid;
            ((RectTransform)b.transform).anchoredPosition = new Vector2(bx, by);
            var pop = Mathf.Min(1f, bubbleY[i] * 8f) * Mathf.Clamp01((1f - bubbleY[i]) * 6f);
            b.canvasRenderer.SetAlpha(pop * 0.8f);
            b.color = Color.Lerp(liquid, Color.white, 0.55f);
        }
        var empty = flaskCharges == 0;
        flaskGourd.color = Color.Lerp(flaskGourd.color, empty ? new Color(0.62f, 0.55f, 0.66f) : Color.white, 1f - Mathf.Exp(-10f * dt));
    }

    /// <summary>Carve a count (1–2 digits) into a round badge — the flask's and the item socket's.</summary>
    private static void SetBadgeDigits(Image[] digits, int n)
    {
        if (digits[0] == null || digits[1] == null) return; // partial build guard
        n = Mathf.Clamp(n, 0, 99);
        var two = n >= 10;
        digits[0].sprite = HudArt.Digit(two ? n / 10 : n, outlined: false);
        digits[1].sprite = HudArt.Digit(n % 10, outlined: false);
        digits[1].gameObject.SetActive(two);
        // Carved 4x6 glyphs centred in the round badge — tight pitch so a
        // double-digit count sits inside the well, nudged under centre.
        ((RectTransform)digits[0].transform).anchoredPosition =
            new Vector2(two ? -1.7f * HudArt.T : 0f, -HudArt.T * 0.75f);
        ((RectTransform)digits[1].transform).anchoredPosition = new Vector2(1.7f * HudArt.T, -HudArt.T * 0.75f);
    }

    /// <summary>Low-HP feedback: a deep-red screen vignette pulses with a
    /// heartbeat that tightens as health drops, and each beat thumps the
    /// camera through the existing decaying shake. Silent above 35% HP and
    /// while dead — the death overlay owns the screen then.</summary>
    private void TickLowHp(float dt)
    {
        if (playerHealth == null) playerHealth = FindFirstObjectByType<PlayerHealth>();
        var alive = playerHealth != null && !playerHealth.IsDead;
        var hpK = alive ? playerHealth.Current / playerHealth.Max : 1f;
        var lowK = alive && hpK < 0.35f ? 1f - hpK / 0.35f : 0f;
        if (lowK > 0f)
        {
            heartT += dt;
            if (heartT >= Mathf.Lerp(0.95f, 0.55f, lowK))
            {
                heartT = 0f;
                lowPulse = 1f;
                if (playerCam == null) playerCam = FindFirstObjectByType<PlayerCameraController>();
                playerCam?.Shake(0.05f + 0.1f * lowK); // slight thump, decays on its own
            }
        }
        else heartT = 0f;
        lowPulse = Mathf.MoveTowards(lowPulse, 0f, dt * 2.2f);
        if (lowVign != null)
            lowVign.canvasRenderer.SetAlpha(lowK * (0.3f + 0.7f * lowPulse));
    }

    /// <summary>Sprint focus-lines fade in as a field, then each line flickers
    /// on stepped ~12–17fps anime frames — a hashed brightness + in/out jitter
    /// per step, not a continuous shimmer. Also holds a low chromatic
    /// aberration floor; the FOV widen itself lives in PlayerCameraController
    /// (speed-scaled 55→62). Same speed gate the old wind FX used.</summary>
    private void TickSpeedLines(float dt)
    {
        if (speedLayer == null) return;
        if (playerLoco == null) playerLoco = FindFirstObjectByType<PlayerLocomotion>();
        if (playerWall == null && playerLoco != null)
            playerWall = playerLoco.GetComponent<WallRunController>();
        // Sprint gate is bypassed while wall-running: IsDisplacing freezes
        // locomotion so ActualPlanarSpeed reads ~0 — wall-run = lines too.
        var sprinting = playerLoco != null && playerLoco.ActualPlanarSpeed >= playerLoco.RunSpeed * 0.85f;
        var on = (sprinting && !playerLoco.IsDashing)
                 || (playerWall != null && playerWall.IsWallRunning);
        speedLinesK = Mathf.MoveTowards(speedLinesK, on ? 1f : 0f, dt * 6f);
        PostPulse.SustainAberration(speedLinesK * 0.16f); // edge chroma while sprinting
        if (speedLinesK <= 0.001f)
        {
            if (speedGroup.alpha > 0f) speedGroup.alpha = 0f;
            return;
        }
        var rect = speedLayer.rect;
        if (rect.width < 10f) return;
        speedGroup.alpha = speedLinesK;
        var w = rect.width;
        var h = rect.height;
        var per = 2f * (w + h);
        var half = rect.size * 0.5f;
        var clear = 0.2f * Mathf.Min(w, h); // centre stays clear
        var t = Time.unscaledTime;
        foreach (var l in speedLines)
        {
            var cell = Mathf.Floor(t * l.rate + l.phase);        // stepped frames
            // Every step re-draws the line at a fresh perimeter spot — hashed
            // arclength walk around the rect edge, no fixed anchors.
            var d0 = FracSin(cell * 47.3f + l.phase * 5.97f) * per;
            Vector2 p;
            if (d0 < w) p = new Vector2(d0, 0f);
            else if ((d0 -= w) < h) p = new Vector2(w, d0);
            else if ((d0 -= h) < w) p = new Vector2(w - d0, h);
            else { d0 -= w; p = new Vector2(0f, h - d0); }
            var pos = p - half;
            var d = (-pos + new Vector2(l.jx, l.jy)).normalized; // inward, jittered
            var a = FracSin(cell * 91.7f + l.phase * 7.31f);     // hashed brightness
            a *= a;                                              // sparse pops
            var j = FracSin(cell * 57.3f + l.phase * 3.17f);     // stepped depth jitter
            var rt = (RectTransform)l.img.transform;
            l.img.canvasRenderer.SetAlpha(a * 0.15f);
            rt.sizeDelta = new Vector2(Mathf.Min(l.len, Mathf.Max(24f, pos.magnitude - clear)), l.thick);
            rt.localScale = new Vector3(0.85f + 0.3f * j, 1f, 1f);
            rt.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            rt.anchoredPosition = pos - d * (8f + j * 12f);
        }
    }

    private static float FracSin(float x)
    {
        var s = Mathf.Sin(x) * 43758.5453f;
        return s - Mathf.Floor(s);
    }

    private void TickSlots(float dt)
    {
        if (abilities == null || abilities.SlotCount <= 0) return;
        var sel = Mathf.Clamp(abilities.Selected, 0, abilities.SlotCount - 1);
        if (sel != shownSelected)
        {
            var first = shownSelected < 0;
            shownSelected = sel;
            RefreshSlots();
            if (!first)
            {
                artPunch = 0.3f;
                var n = abilities.SlotFullName(sel);
                artName.text = string.IsNullOrEmpty(n) ? "" : n.ToUpperInvariant();
                artNameT = 0f;
            }
        }
        artPunch = Mathf.Lerp(artPunch, 0f, 1f - Mathf.Exp(-12f * dt));
        artMotion.localScale = Vector3.one * (1f + artPunch);
        artMotion.anchoredPosition = new Vector2(0f, 10f * artPunch);
        for (var i = 0; i < ghostMotion.Length; i++)
            ghostMotion[i].anchoredPosition = new Vector2(-14f * artPunch, (i == 0 ? -1f : 1f) * 18f * artPunch);
        var pulse = 0.25f + 0.2f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f * 1.2f));
        artGlow.canvasRenderer.SetAlpha(Mathf.Min(1f, pulse + artPunch * 2.5f));

        // Name flash: pop in, hold, fade.
        if (artNameT >= 0f)
        {
            artNameT += dt;
            artNameGroup.alpha = artNameT < 0.1f ? artNameT / 0.1f : artNameT < 1.4f ? 1f : 1f - (artNameT - 1.4f) / 0.4f;
            ((RectTransform)artName.transform).anchoredPosition = new Vector2(0f, 8f * (1f - PersonaUi.EaseOutBack(Mathf.Clamp01(artNameT / 0.2f))));
            if (artNameT >= 1.8f) { artNameT = -1f; artNameGroup.alpha = 0f; }
        }
    }

    private void TickItem(float dt)
    {
        if (itemMotion == null) return;
        itemPunch = Mathf.Lerp(itemPunch, 0f, 1f - Mathf.Exp(-14f * dt));
        itemMotion.localScale = Vector3.one * (1f + itemPunch);
    }

    // ================= ultimate orb =================

    private void TickUlt(float dt)
    {
        if (ultRoot == null || !ultRoot.gameObject.activeSelf) return;
        var t = Time.unscaledTime;
        ultShown = Mathf.Lerp(ultShown, ultTarget, 1f - Mathf.Exp(-8f * dt));
        var full = ult != null && ult.Full;
        if (full && !ultWasFull) { ultPunch = 0.35f; ultFlashK = 1f; ultShockT = 0f; } // READY
        ultWasFull = full;
        ultReadyK = Mathf.MoveTowards(ultReadyK, full ? 1f : 0f, dt * 4f);

        // Liquid: fill + a hot crest just above it that wobbles like a surface.
        ultFill.fillAmount = ultShown;
        ultCrest.fillAmount = ultShown > 0.01f && ultShown < 0.99f ? ultShown + 0.035f + Mathf.Sin(t * 5.1f) * 0.012f : 0f;
        ultFill.color = Color.Lerp(PersonaUi.Blood, PersonaUi.Heart, Mathf.Max(ultShown * 0.6f, ultReadyK));

        ultPunch = Mathf.Lerp(ultPunch, 0f, 1f - Mathf.Exp(-10f * dt));
        ultFlashK = Mathf.MoveTowards(ultFlashK, 0f, dt * 3f);
        ultFlash.canvasRenderer.SetAlpha(ultFlashK * 0.85f);
        var breathe = ultReadyK * Mathf.Sin(t * 3.4f) * 0.03f;
        ultMotion.localScale = Vector3.one * (1f + ultPunch + breathe);

        ultIcon.color = Color.Lerp(new Color(0.45f, 0.4f, 0.48f), Color.white, Mathf.Max(ultReadyK, ultShown * 0.5f));
        ultIconRt.localRotation = Quaternion.Euler(0f, 0f, ultReadyK * Mathf.Sin(t * 1.3f) * 6f);
        ultHalo.canvasRenderer.SetAlpha(ultReadyK * (0.35f + 0.25f * Mathf.Sin(t * 3.4f)));

        // Orbiting sparks + two 4-point stars while ready.
        for (var i = 0; i < ultSparks.Length; i++)
        {
            var s = ultSparks[i];
            var on = ultReadyK > 0.02f;
            if (s.gameObject.activeSelf != on) s.gameObject.SetActive(on);
            if (!on) continue;
            var a = t * (i % 2 == 0 ? 1.6f : -1.1f) + i * Mathf.PI * 2f / ultSparks.Length;
            var r = UltD * 0.5f + 6f + Mathf.Sin(t * 4f + i) * 5f;
            s.anchoredPosition = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * ultReadyK;
            s.localRotation = Quaternion.Euler(0f, 0f, t * 140f);
            s.localScale = Vector3.one * ultReadyK * (0.7f + 0.3f * Mathf.Sin(t * 7f + i * 1.7f));
        }

        // Shockwave ring on READY / cast.
        if (ultShockT >= 0f)
        {
            ultShockT += dt / 0.45f;
            ultShock.localScale = Vector3.one * Mathf.Lerp(1f, 2f, PersonaUi.EaseOutCubic(ultShockT));
            ultShockImg.canvasRenderer.SetAlpha(1f - Mathf.Clamp01(ultShockT));
            if (ultShockT >= 1f) { ultShockT = -1f; ultShockImg.canvasRenderer.SetAlpha(0f); }
        }
    }

    private void RefreshUlt(float cur, float max) => RefreshUlt();

    private void RefreshUlt()
    {
        if (ultRoot == null) return;
        var frac = ult != null ? Mathf.Clamp01(ult.Current / UltCharge.Max) : 0f;
        if (frac > ultTarget + 0.001f) { ultPunch = Mathf.Max(ultPunch, 0.07f); ultFlashK = Mathf.Max(ultFlashK, 0.3f); }
        ultTarget = frac;
    }

    /// <summary>The cast: the orb bursts and the screen plays impact frames.</summary>
    private void OnUltSpent()
    {
        ultPunch = 0.45f;
        ultFlashK = 1f;
        ultShockT = 0f;
        ultTarget = ultShown = 0f;
        ultWasFull = false;
        StartCoroutine(ImpactFrames());
    }

    /// <summary>Anime impact frames on unscaled time: four stepped full-screen
    /// frames (bone / ink / heart / ink) with burst lines re-drawn each step,
    /// a camera kick and a chroma pulse, then a quick release. Public so other
    /// cinematic hits can reuse it.</summary>
    public static IEnumerator ImpactFrames()
    {
        var h = Ensure();
        var frames = new (Color bg, float a, Color line, float hold)[]
        {
            (PersonaUi.Bone, 0.92f, PersonaUi.Ink, 0.05f),
            (PersonaUi.Ink, 0.94f, PersonaUi.Heart, 0.05f),
            (PersonaUi.Heart, 0.85f, PersonaUi.Ink, 0.04f),
            (PersonaUi.Ink, 0.9f, PersonaUi.Bone, 0.06f),
        };
        h.impactRoot.gameObject.SetActive(true);
        var cam = FindFirstObjectByType<PlayerCameraController>();
        if (cam != null) cam.Shake(0.6f);
        PostPulse.AberrationPulse(0.6f, 0.25f);
        var rect = h.impactRoot.rect;
        var reach = rect.size.magnitude * 0.5f;
        var rng = new System.Random();
        foreach (var f in frames)
        {
            h.impactBg.color = PersonaUi.WithAlpha(f.bg, f.a);
            for (var i = 0; i < h.impactLines.Length; i++)
            {
                var a = (i + (float)rng.NextDouble() * 0.6f) / h.impactLines.Length * Mathf.PI * 2f;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                // Edge point along the ray (clipped to the screen rect), pointing inward.
                var edge = Mathf.Min(Mathf.Abs(rect.width * 0.5f / Mathf.Max(0.001f, Mathf.Abs(dir.x))),
                                     Mathf.Abs(rect.height * 0.5f / Mathf.Max(0.001f, Mathf.Abs(dir.y))));
                var rt = h.impactLines[i].rectTransform;
                rt.anchoredPosition = dir * edge;
                rt.localEulerAngles = new Vector3(0f, 0f, a * Mathf.Rad2Deg + 180f);
                rt.sizeDelta = new Vector2(reach * (0.3f + (float)rng.NextDouble() * 0.35f), 10f + (float)rng.NextDouble() * 18f);
                h.impactLines[i].color = f.line;
            }
            yield return new WaitForSecondsRealtime(f.hold);
        }
        var t = 0f;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / 0.14f;
            var k = 1f - Mathf.Clamp01(t);
            h.impactBg.color = PersonaUi.WithAlpha(PersonaUi.Ink, 0.9f * k * k);
            foreach (var l in h.impactLines) l.canvasRenderer.SetAlpha(k);
            yield return null;
        }
        foreach (var l in h.impactLines) l.canvasRenderer.SetAlpha(1f);
        h.impactRoot.gameObject.SetActive(false);
    }

    // ================= floating world UI =================

    private void TickWorld(float dt)
    {
        var cam = Camera.main;
        if (lockOn == null && (lockOnFindT -= dt) <= 0f)
        {
            lockOn = FindFirstObjectByType<LockOnController>();
            lockOnFindT = 1f;
        }
        var target = lockOn != null ? lockOn.CurrentTarget : null;

        // Lock-on X on the aim point; punch on acquire, slow spin while held.
        if (target != reticleTarget) { reticleTarget = target; reticlePunch = target != null ? 0.45f : 0f; }
        var showReticle = false;
        if (target != null && cam != null)
        {
            // WorldToViewportPoint is resolution-independent — WorldToScreenPoint
            // returns RT-pixel coords when the camera targets RetroScreen's
            // low-res texture, which squashed the marker into a corner.
            var vp = cam.WorldToViewportPoint(target.AimPosition);
            var sp = new Vector2(vp.x * Screen.width, vp.y * Screen.height);
            if (vp.z > 0f && pixels.ScreenToLocal(worldLayer, sp, out var local))
            {
                var rt = (RectTransform)reticle.transform;
                rt.anchoredPosition = local;
                reticlePunch = Mathf.Lerp(reticlePunch, 0f, 1f - Mathf.Exp(-12f * dt));
                rt.localScale = Vector3.one * (1f + reticlePunch + Mathf.Sin(Time.unscaledTime * 3.2f) * 0.05f);
                rt.localRotation = Quaternion.Euler(0f, 0f, Time.unscaledTime * 110f);
                showReticle = true;
            }
        }
        if (reticle.gameObject.activeSelf != showReticle) reticle.gameObject.SetActive(showReticle);
    }

    private void OnDestroy()
    {
        SoulsWallet.Changed -= SetSouls;
        if (abilities != null) abilities.Changed -= RefreshSlots;
        if (mana != null) mana.Changed -= OnManaChanged;
        if (ult != null) { ult.Changed -= RefreshUlt; ult.Spent -= OnUltSpent; }
        if (inventory != null) inventory.Changed -= RefreshItem;
        if (instance == this) instance = null;
    }

    // ================= persistent elements =================

    public static void SetSouls(int n)
    {
        var h = Ensure();
        if (h.soulsText == null) return;
        if (!h.soulsInit)
        {
            h.soulsInit = true;
            h.soulsShown = h.soulsTarget = n;
            h.soulsText.text = n.ToString();
            return;
        }
        var diff = n - h.soulsTarget;
        h.soulsTarget = n;
        if (diff != 0) h.soulsAwakeT = SoulsLinger;
        if (diff > 0)
        {
            h.soulsPunch = 0.12f;
            h.soulsPlus.text = "+" + diff;
            h.soulsPlusT = 0f;
        }
        else if (diff < 0)
        {
            h.soulsShake = 0.35f;
            h.soulsRed = 1f;
        }
    }

    /// <summary>Flask count; a drop wobbles the bottle, a refill flashes the slot.</summary>
    public static void SetFlask(int charges, int max)
    {
        var h = Ensure();
        if (h.flaskGourd == null) return;
        if (h.flaskCharges >= 0)
        {
            if (charges < h.flaskCharges) { h.flaskWobble = 0.3f; h.flaskPunch = 0.08f; }
            else if (charges > h.flaskCharges) { h.flaskFlash = 1f; h.flaskPunch = 0.12f; }
        }
        if (h.flaskCharges != charges) h.digitPunch = 0.25f;
        h.flaskCharges = charges;
        h.flaskMax = Mathf.Max(1, max);
        SetBadgeDigits(h.flaskDigits, charges);
    }

    /// <summary>Swaps the bottom-left flask between the estus (hp flask.png)
    /// and mana art (mana flask.png) — driven by EstusFlask's scroll mode.</summary>
    public static void SetFlaskMode(bool mana)
    {
        var h = Ensure();
        if (h.flaskGourd == null) return;
        if (h.flaskMana != mana) h.flaskWobble = 0.3f; // the swap rocks the flask
        h.flaskMana = mana;
        h.flaskPunch = 0.1f;
        h.ApplyFlaskSprite();
    }

    /// <summary>Hooks the quick-slot row to an ability system (spells or arts).</summary>
    public static void BindAbilities(IAbilitySlots source)
    {
        var h = Ensure();
        if (h.abilities == source) { h.RefreshSlots(); return; }
        if (h.abilities != null) h.abilities.Changed -= h.RefreshSlots;
        if (h.mana != null) h.mana.Changed -= h.OnManaChanged;
        if (h.ult != null) { h.ult.Changed -= h.RefreshUlt; h.ult.Spent -= h.OnUltSpent; }
        h.abilities = source;
        var comp = source as Component;
        h.mana = comp != null ? comp.GetComponent<PlayerMana>() : null;
        h.ult = comp != null ? comp.GetComponent<UltCharge>() : null;
        h.attack = comp != null ? comp.GetComponent<AttackController>() : null;
        if (h.abilities != null) h.abilities.Changed += h.RefreshSlots;
        if (h.mana != null) h.mana.Changed += h.OnManaChanged;
        if (h.ult != null) { h.ult.Changed += h.RefreshUlt; h.ult.Spent += h.OnUltSpent; }
        h.RefreshUlt();
        h.ultShown = h.ultTarget;
        if (h.ultRoot != null) h.ultRoot.gameObject.SetActive(h.ult != null);
        if (h.skillRow != null) h.skillRow.gameObject.SetActive(source != null);
        h.shownSelected = -1;
        h.RefreshSlots();
    }

    /// <summary>Hooks the quick-item socket to the satchel.</summary>
    public static void BindInventory(Inventory inv)
    {
        var h = Ensure();
        if (h.inventory == inv) { h.RefreshItem(); return; }
        if (h.inventory != null) h.inventory.Changed -= h.RefreshItem;
        h.inventory = inv;
        if (h.inventory != null) h.inventory.Changed += h.RefreshItem;
        h.RefreshItem();
    }

    private void RefreshItem()
    {
        if (itemRow == null) return;
        var item = inventory != null ? inventory.QuickItem : null;
        itemIcon.enabled = item != null;
        itemBadge.gameObject.SetActive(item != null);
        itemFrame.color = item != null ? Color.white : new Color(0.6f, 0.55f, 0.65f, 0.7f);
        if (item != shownItem && item != null && shownItem != null) itemPunch = 0.25f; // cycled
        shownItem = item;
        if (item == null) return;
        SetIcon(itemIcon, HudArt.ItemIcon(item.icon, item.itemName), ItemIconScale);
        SetBadgeDigits(itemDigits, inventory.Count(item));
    }

    private static void SetIcon(Image img, Sprite sp, float scale)
    {
        img.sprite = sp;
        img.rectTransform.sizeDelta = new Vector2(sp.rect.width, sp.rect.height) * scale;
    }

    private void OnManaChanged(float cur, float max) => RefreshSlots();

    /// <summary>Modifier-held command hints + aerial-session hints (detail
    /// §254) — the line above the flask row. The ult's readiness lives on the orb.</summary>
    private void RefreshHintText()
    {
        if (commandHint == null) return;
        string text;
        if (attack != null && attack.InAirSession)
            text = attack.AirStepsUsed >= 3 ? "AIR COMBO SPENT · ALT/LT+LMB PLUNGE · ALT/LT+RMB SKYFALL"
                                            : "ATTACK AIR COMBO · ALT/LT+LMB PLUNGE · ALT/LT+RMB SKYFALL";
        else if (attack != null && AttackController.ModifierHeld)
            text = "ALT/LT+LMB UPPER · ALT/LT+RMB GRAVE WOLF · ALT/LT+DPAD ARTS";
        else text = "";
        if (commandHint.text != text) commandHint.text = text;
    }

    /// <summary>Selected art in the big socket, next/previous in the ghosts;
    /// unaffordable arts dim.</summary>
    private void RefreshSlots()
    {
        if (abilities == null || artIcon == null) return;
        var n = abilities.SlotCount;
        if (n <= 0) return;
        var cur = mana != null ? mana.Current : float.MaxValue;
        var sel = Mathf.Clamp(abilities.Selected, 0, n - 1);
        Paint(artIcon, sel, IconScale, 1f);
        artDigit.sprite = HudArt.Digit(sel + 1, outlined: false);
        for (var i = 0; i < ghostIcons.Length; i++)
        {
            var slot = (sel + (i == 0 ? 1 : n - 1)) % n;
            ghostIcons[i].transform.parent.parent.gameObject.SetActive(n > 1 + i && slot != sel);
            Paint(ghostIcons[i], slot, GhostIconScale, 0.6f);
        }

        void Paint(Image img, int slot, float scale, float bright)
        {
            var has = !string.IsNullOrEmpty(abilities.SlotFullName(slot));
            img.enabled = has;
            if (!has) return;
            SetIcon(img, HudArt.SkillIcon(abilities.SlotIcon(slot), abilities.SlotFullName(slot)), scale);
            var c = cur >= abilities.SlotCost(slot) ? Color.white : new Color(0.5f, 0.42f, 0.5f);
            img.color = new Color(c.r * bright, c.g * bright, c.b * bright, 1f);
        }
    }

    // ================= transient elements =================

    public static void ShowPrompt(string key, string text) => Ensure().prompt.Show(key, text);

    public static void HidePrompt()
    {
        if (instance != null) instance.prompt?.Hide();
    }

    public static void Toast(string text) => Ensure().toast.Show(null, text, 1.6f);

    /// <summary>Big centred banner (e.g. CHECKPOINT LIT).</summary>
    public static void Banner(string text, float hold = 1.8f) => Ensure().banner.Show(null, text, hold);

    /// <summary>White screen pulse — the burst cinematic's cut-in accent.</summary>
    public static void Flash(float seconds = 0.07f) => Ensure().flashT = seconds;

    // ================= death sequence (GameLoop drives it) =================

    /// <summary>A full-width death banner with a short impact and a stable,
    /// readable hold. GameLoop still owns the death, wallet drop and respawn.</summary>
    public static IEnumerator DeathOverlay(float hold)
    {
        var h = Ensure();
        var m = h.deathMotion;
        var title = (RectTransform)h.deathLetters[0].transform.parent;
        var titleHome = h.deathLetterPos[0];
        title.gameObject.SetActive(true);
        h.deathSub.gameObject.SetActive(false);
        h.deathGroup.alpha = 0f;
        m.anchoredPosition = Vector2.zero;
        m.localRotation = Quaternion.identity;
        h.deathMedal.localScale = Vector3.zero;
        var lost = SoulsWallet.Souls;

        var t = 0f;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / 0.24f;
            var e = PersonaUi.EaseOutCubic(Mathf.Clamp01(t));
            m.localScale = new Vector3(1f, Mathf.Lerp(0.65f, 1f, e), 1f);
            title.localScale = Vector3.one * Mathf.Lerp(1.12f, 1f, e);
            title.anchoredPosition = titleHome + new Vector2(0f, 12f * (1f - e));
            h.deathGroup.alpha = e;
            yield return null;
        }
        m.localScale = Vector3.one;
        title.localScale = Vector3.one;
        title.anchoredPosition = titleHome;
        // The broken-heart medal stamps onto the rim once the band has settled.
        t = 0f;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / 0.16f;
            h.deathMedal.localScale = Vector3.one * Mathf.Lerp(1.9f, 1f, PersonaUi.EaseOutCubic(Mathf.Clamp01(t)));
            yield return null;
        }
        FindFirstObjectByType<PlayerCameraController>()?.Shake(0.2f);
        if (lost > 0)
        {
            h.deathSubText.text = "SOULS LOST   " + lost;
            h.deathSub.gameObject.SetActive(true);
        }
        var elapsed = 0f;
        while (elapsed < hold)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    public static IEnumerator FadeDeathOut(float dur)
    {
        var h = Ensure();
        var from = h.deathGroup.alpha;
        var t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            h.deathGroup.alpha = Mathf.Lerp(from, 0f, Mathf.Clamp01(t / dur));
            yield return null;
        }
        h.deathGroup.alpha = 0f;
    }

    /// <summary>Screen fade: 0 = clear, 1 = black.</summary>
    public static IEnumerator FadeScreen(float target, float dur)
    {
        var h = Ensure();
        var from = h.fade.canvasRenderer.GetAlpha();
        var t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            h.fade.canvasRenderer.SetAlpha(Mathf.Lerp(from, target, Mathf.Clamp01(t / dur)));
            yield return null;
        }
        h.fade.canvasRenderer.SetAlpha(target);
    }
}
