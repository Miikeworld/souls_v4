using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Runtime HUD. Builds a uGUI canvas at Awake: the ornate IMG_0040 frame as a
/// RawImage with three masked slot fills inside it — health top, mana middle,
/// stamina bottom. Each fill is a beveled gradient RawImage whose width tracks
/// the fill fraction, with a damage ghost trail and smooth-follow. Enemy target
/// bar and the death stamp stay in OnGUI on top.
/// </summary>
public sealed class PlayerHud : MonoBehaviour
{
    [SerializeField] private PlayerHealth health;
    [SerializeField] private PlayerStamina stamina;
    [SerializeField] private PlayerMana mana;
    [SerializeField] private LockOnController lockOn;

    [Header("Ornate frame (IMG_0040)")]
    [SerializeField] private Texture2D hudFrame;
    [SerializeField] private float frameScale = 0.42f;
    [SerializeField] private Vector2 margin = new Vector2(48f, 46f);
    [Tooltip("Slot rects in normalized frame space (x,y from top-left; w,h).")]
    [SerializeField] private Rect healthSlot  = new Rect(0.129f, 0.415f, 0.730f, 0.060f);
    [SerializeField] private Rect manaSlot    = new Rect(0.129f, 0.510f, 0.575f, 0.055f);
    [SerializeField] private Rect staminaSlot = new Rect(0.129f, 0.585f, 0.575f, 0.050f);
    [SerializeField] private float ghostDrain = 0.6f;
    [SerializeField] private float smoothSpeed = 8f;
    [SerializeField] private Color hpColor = new Color(0.75f, 0.16f, 0.14f);
    [Tooltip("Plain OnGUI fallback only — the Core Energy conduit always paints its own violet palette.")]
    [SerializeField] private Color mpColor = new Color(0.545f, 0.208f, 0.910f);
    [SerializeField] private Color spColor = new Color(0.16f, 0.60f, 0.25f);

    [Header("Or your own Canvas")]
    [Tooltip("Assign all three fill RectTransforms from your authored Canvas — the script only scales their X with the resource value and skips the runtime canvas entirely. Set each fill's pivot.x = 0 so it shrinks left-anchored.")]
    [SerializeField] private RectTransform hpFill;
    [SerializeField] private RectTransform mpFill;
    [SerializeField] private RectTransform spFill;
    [Tooltip("Optional pale fill drawn BEHIND HpFill — drains slowly after damage (the chunk). Auto-found by name 'HpGhost'.")]
    [SerializeField] private RectTransform hpGhostFill;
    [Tooltip("Optional pale fill behind MpFill — same slow-drain chunk on mana loss. Auto-found by name 'MpGhost'.")]
    [SerializeField] private RectTransform mpGhostFill;
    [Tooltip("0 disables hit feedback. Scales the damage shake, punch pop and colour flash on the health bar.")]
    [SerializeField, Range(0f, 2f)] private float hudHitJuice = 1f;
    [Tooltip("The drawn heart in normalized frame space (x,y from top-left; w,h) — slightly padded so the cracked cover hides it fully.")]
    [SerializeField] private Rect heartRect = new Rect(0.074f, 0.388f, 0.05f, 0.092f);

    // Heart shatter on death — the cover only exists while broken; the intact
    // heart is always the hand-drawn art underneath.
    private RectTransform heartRt;
    private Image heartCover;
    private RectTransform[] shards;
    private Vector2[] shardVel;
    private float[] shardSpin;
    private float shardT = -1f, heartPop = -1f;
    private bool heartBroken;

    private Texture2D fillTex, whiteTex;
    private bool externalFills;
    private Vector3 hpFillBase, mpFillBase, spFillBase, hpGhostBase, mpGhostBase;
    private RectTransform fxRoot;      // shaken + punched: fills' parent rect
    private Vector2 fxBasePos;
    private Graphic hpFillG, spFillG, mpFillG;
    private Color hpBaseColor, spBaseColor, mpBaseColor;
    private Vector2 spFillPos; // the fill's authored spot — jitter offsets it, never replaces it
    private float prevHpNorm = -1f, hitPunch, hitFlash, spDenied, lowHp, mpPulse, mpDenied;
    private Bar hpBar, mpBar, spBar;
    private CoreConduit conduit;
    private RectTransform frameRt;
    private bool canvasBuilt;
    private bool firstFillLogged;
    private float hpShown = -1f, spShown = -1f, mpShown = -1f, hpGhost = -1f, mpGhost = -1f;

    // Health fill end: a torn, crackling pixel edge riding the cut, chips that
    // break off on damage, embers at low HP, and the ghost chunk holding a beat
    // before it drains — instead of a flat scale cut-off.
    private float hpGhostHold, hpEmberClock;

    private RectTransform chipRoot;
    private readonly System.Collections.Generic.List<(RawImage img, Vector2 vel, float age, float life)> chips =
        new System.Collections.Generic.List<(RawImage, Vector2, float, float)>();

    /// <summary>Runtime-built bar: fills are RawImages whose own rect width
    /// tracks the value — no mask components involved.</summary>
    private sealed class Bar
    {
        public RawImage ghost; // may be null — width = ghost fill
        public RawImage fill;  // width = current fill
        public float w, h;
    }

    private void Awake()
    {
        if (health == null) health = GetComponent<PlayerHealth>();
        if (stamina == null) stamina = GetComponent<PlayerStamina>();
        if (mana == null) mana = GetComponent<PlayerMana>();
        if (lockOn == null) lockOn = GetComponent<LockOnController>();

        BuildFillTextures();
        if (hpFill == null && mpFill == null && spFill == null)
            TryFindAuthoredFills();
        if (hpFill != null || mpFill != null || spFill != null)
        {
            if (hpFill == null || mpFill == null || spFill == null)
            {
                Debug.LogWarning("[PlayerHud] partial canvas assignment — assign hpFill, mpFill AND spFill, or none.", this);
            }
            else
            {
                externalFills = true;
                canvasBuilt = true; // suppress the OnGUI plain-bar fallback
                hpFillBase = hpFill.localScale;
                mpFillBase = mpFill.localScale;
                spFillBase = spFill.localScale;
                if (hpGhostFill == null)
                    TryFindByName("hpghost", ref hpGhostFill);
                if (hpGhostFill != null) hpGhostBase = hpGhostFill.localScale;
                if (mpGhostFill == null)
                    TryFindByName("mpghost", ref mpGhostFill);
                if (mpGhostFill != null) mpGhostBase = mpGhostFill.localScale;
                fxRoot = hpFill.parent as RectTransform;
                if (fxRoot != null) fxBasePos = fxRoot.anchoredPosition;
                hpFillG = hpFill.GetComponent<Graphic>();
                hpBaseColor = hpFillG != null ? hpFillG.color : hpColor;
                spFillG = spFill.GetComponent<Graphic>();
                spBaseColor = spFillG != null ? spFillG.color : spColor;
                spFillPos = spFill.anchoredPosition;
                mpFillG = mpFill.GetComponent<Graphic>();
                mpBaseColor = mpFillG != null ? mpFillG.color : mpColor;
                // Quartile notches ride a sibling copy of the fill's slot
                // geometry — it stays full width while the fill's scale shrinks.
                var tickHost = NewRect("HpTicks", hpFill.parent);
                tickHost.anchorMin = hpFill.anchorMin; tickHost.anchorMax = hpFill.anchorMax;
                tickHost.offsetMin = hpFill.offsetMin; tickHost.offsetMax = hpFill.offsetMax;
                AddQuartileTicks(tickHost);
            }
        }
        if (!externalFills && hudFrame != null)
        {
            try
            {
                BuildCanvas();
                canvasBuilt = true;
                fxRoot = frameRt;
                fxBasePos = frameRt.anchoredPosition;
                hpFillG = hpBar.fill;
                hpBaseColor = hpColor;
                spFillG = spBar.fill;
                spBaseColor = spColor;
                mpFillG = mpBar.fill;
                mpBaseColor = mpColor;
                spFillPos = ((RectTransform)spBar.fill.transform).anchoredPosition;
                AddQuartileTicks((RectTransform)hpBar.fill.transform.parent); // the slot rect
            }
            catch (System.Exception e)
            {
                Debug.LogError("[PlayerHud] Canvas build failed, using plain bars: " + e);
            }
        }
        // Core Energy: the mana slot's fill becomes the Violet Core bar in
        // either canvas mode; contextual IHudGauge state draws as screen-edge
        // arcs on their own pixel canvas under the HUD.
        var mpRect = externalFills ? mpFill : mpBar != null ? (RectTransform)mpBar.fill.transform : null;
        if (Application.isPlaying) OverdriveEdge.Attach(gameObject);
        if (mpRect != null)
        {
            try
            {
                // Separator centres around the channel: the authored canvas's fill
                // overhangs the frame window; the runtime slots butt against it.
                conduit = externalFills
                    ? CoreConduit.Build(mpRect, gameObject)
                    : CoreConduit.Build(mpRect, gameObject, 4.2f, 2.4f);
                var ghostG = externalFills ? (mpGhostFill != null ? mpGhostFill.GetComponent<Graphic>() : null) : mpBar.ghost;
                if (ghostG != null) ghostG.color = new Color(CoreConduit.Bright.r, CoreConduit.Bright.g, CoreConduit.Bright.b, 0.45f);
            }
            catch (System.Exception e)
            {
                conduit = null;
                Debug.LogError("[PlayerHud] Core conduit build failed, plain violet fill kept: " + e, this);
            }
        }
        mpBaseColor = CoreConduit.Main;
        try { BuildHpEnd(); }
        catch (System.Exception e) { hpEnd = mpEnd = spEnd = null; Debug.LogError("[PlayerHud] health-bar end build failed: " + e, this); }

        // Zero-setup parallax: hang HudParallax on the visual root — the
        // canvas's direct child that CONTAINS the fills, so bars and frame
        // drift together. If the scene already has one, the user's own
        // component/settings win.
        RectTransform parallaxTarget = frameRt;
        if (externalFills)
        {
            var t = hpFill;
            while (t.parent is RectTransform p && p.GetComponent<Canvas>() == null)
                t = p;
            parallaxTarget = t;
        }
        // Checked on the target's own chain — GameHud's layers carry their own
        // HudParallax components and must not suppress the health frame's.
        if (parallaxTarget != null && parallaxTarget.GetComponentInParent<HudParallax>() == null)
            parallaxTarget.gameObject.AddComponent<HudParallax>();

        BuildHeart();
        if (health != null)
        {
            health.Died += Shatter;
            health.Revived += ReviveHeart;
        }
        if (stamina != null) stamina.Denied += OnStaminaDenied;
        if (mana != null) { mana.Restored += OnManaRestored; mana.Denied += OnManaDenied; mana.Spent += OnManaSpent; }

        Debug.Log($"[PlayerHud] mode={(externalFills ? "external canvas" : canvasBuilt ? "runtime canvas" : "plain OnGUI")} " +
                  $"hp={(health != null ? "ok" : "NULL")} mp={(mana != null ? "ok" : "NULL")} " +
                  $"sp={(stamina != null ? "ok" : "NULL")} lockOn={(lockOn != null ? "ok" : "NULL")} " +
                  $"frame={(hudFrame != null ? $"{hudFrame.width}x{hudFrame.height}" : "NULL")}" +
                  (hpBar != null ? $" hpSlot={hpBar.w:0.#}x{hpBar.h:0.#}" : ""));
    }

    private void TryFindByName(string lower, ref RectTransform slot)
    {
        foreach (var rt in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (rt.name.ToLowerInvariant() == lower) { slot = rt; return; }
    }

    private void TryFindAuthoredFills()
    {
        foreach (var rt in FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var n = rt.name.ToLowerInvariant();
            if (hpFill == null && n == "hpfill") hpFill = rt;
            else if (mpFill == null && n == "mpfill") mpFill = rt;
            else if (spFill == null && n == "spfill") spFill = rt;
            else if (hpGhostFill == null && n == "hpghost") hpGhostFill = rt;
            else if (mpGhostFill == null && n == "mpghost") mpGhostFill = rt;
        }
        if (hpFill != null || mpFill != null || spFill != null)
            Debug.Log($"[PlayerHud] adopted authored fills: hp={(hpFill != null ? hpFill.name : "-")} " +
                      $"mp={(mpFill != null ? mpFill.name : "-")} sp={(spFill != null ? spFill.name : "-")}", this);
    }

    // ---------- canvas construction ----------

    private void BuildCanvas()
    {
        // Stale scene serialization can leave the slot rects at zero (saved before
        // the defaults existed) — a zero-area slot builds a 0-width invisible bar.
        if (healthSlot.width <= 0f || healthSlot.height <= 0f)
            healthSlot = new Rect(0.129f, 0.415f, 0.730f, 0.060f);
        if (manaSlot.width <= 0f || manaSlot.height <= 0f)
            manaSlot = new Rect(0.129f, 0.510f, 0.575f, 0.055f);
        if (staminaSlot.width <= 0f || staminaSlot.height <= 0f)
            staminaSlot = new Rect(0.129f, 0.585f, 0.575f, 0.050f);

        var canvasGo = new GameObject("HUD_Canvas", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        float W = hudFrame.width * frameScale;
        float H = hudFrame.height * frameScale;

        frameRt = NewRect("Frame", canvasGo.transform);
        frameRt.anchorMin = frameRt.anchorMax = frameRt.pivot = new Vector2(0f, 1f);
        frameRt.anchoredPosition = new Vector2(margin.x, -margin.y);
        frameRt.sizeDelta = new Vector2(W, H);
        var frameImg = frameRt.gameObject.AddComponent<RawImage>();
        frameImg.texture = hudFrame;
        frameImg.raycastTarget = false;

        hpBar = BuildBar(frameRt, healthSlot, hpColor, true);
        mpBar = BuildBar(frameRt, manaSlot, mpColor, true);
        spBar = BuildBar(frameRt, staminaSlot, spColor, false);
    }

    // ---------- heart shatter ----------

    /// <summary>Finds the frame image in either mode — the runtime canvas's
    /// frame, or the authored HUD_Canvas's "Frame" sibling of the fills.</summary>
    private RectTransform FindFrameRect()
    {
        if (frameRt != null) return frameRt;
        if (hpFill == null || hpFill.parent == null) return null;
        foreach (Transform c in hpFill.parent)
            if (c.name.ToLowerInvariant() == "frame" && c.GetComponent<RawImage>() != null)
                return (RectTransform)c;
        return null;
    }

    private void BuildHeart()
    {
        var frame = FindFrameRect();
        if (frame == null)
        {
            Debug.Log("[PlayerHud] no frame image found — heart shatter disabled.", this);
            return;
        }
        heartRt = NewRect("HeartCover", frame);
        heartRt.anchorMin = new Vector2(heartRect.x, 1f - heartRect.y - heartRect.height);
        heartRt.anchorMax = new Vector2(heartRect.x + heartRect.width, 1f - heartRect.y);
        heartRt.offsetMin = heartRt.offsetMax = Vector2.zero;
        heartCover = heartRt.gameObject.AddComponent<Image>();
        heartCover.sprite = PixelFrame.Icon(PixelFrame.IconId.HeartCracked);
        heartCover.preserveAspect = true;
        heartCover.raycastTarget = false;
        heartRt.gameObject.SetActive(false);

        var centre = new Vector2(heartRect.x + heartRect.width * 0.5f, 1f - heartRect.y - heartRect.height * 0.5f);
        shards = new RectTransform[18];
        shardVel = new Vector2[shards.Length];
        shardSpin = new float[shards.Length];
        for (var i = 0; i < shards.Length; i++)
        {
            var s = NewRect("Shard" + i, frame);
            s.anchorMin = s.anchorMax = centre;
            s.sizeDelta = Vector2.one * (i % 3 == 0 ? 7f : 5f);
            var img = s.gameObject.AddComponent<Image>();
            img.sprite = PixelFrame.Icon(PixelFrame.IconId.Shard);
            img.color = i % 4 == 0 ? PersonaUi.Blood : Color.white;
            img.raycastTarget = false;
            s.gameObject.SetActive(false);
            shards[i] = s;
        }
    }

    private void Shatter()
    {
        if (heartRt == null) return;
        heartBroken = true;
        heartPop = -1f;
        heartCover.sprite = PixelFrame.Icon(PixelFrame.IconId.HeartCracked);
        heartRt.localScale = Vector3.one * 1.25f;
        heartRt.gameObject.SetActive(true);
        for (var i = 0; i < shards.Length; i++)
        {
            shards[i].gameObject.SetActive(true);
            shards[i].anchoredPosition = Vector2.zero;
            shards[i].localRotation = Quaternion.identity;
            var a = Random.Range(10f, 170f) * Mathf.Deg2Rad; // upper hemisphere
            shardVel[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Random.Range(120f, 380f);
            shardSpin[i] = Random.Range(-720f, 720f);
        }
        shardT = 0f;
        hitPunch = 1f;
        hitFlash = 1f;
    }

    /// <summary>PlayerHealth.Revive — the actual respawn: the heart reignites.
    /// (Not on Damaged — a checkpoint-rest full heal would pop it early.)</summary>
    private void ReviveHeart()
    {
        if (!heartBroken || heartRt == null) return;
        heartBroken = false;
        heartCover.sprite = PixelFrame.Icon(PixelFrame.IconId.Heart);
        heartRt.gameObject.SetActive(true);
        heartPop = 0f;
    }

    private void TickHeart(float dt)
    {
        if (heartRt == null) return;
        if (shardT >= 0f)
        {
            shardT += dt;
            var fadeK = 1f - Mathf.Clamp01((shardT - 0.5f) / 0.4f);
            for (var i = 0; i < shards.Length; i++)
            {
                shardVel[i].y -= 900f * dt;
                shards[i].anchoredPosition += shardVel[i] * dt;
                shards[i].localRotation = Quaternion.Euler(0f, 0f, shardSpin[i] * shardT);
                shards[i].localScale = Vector3.one * fadeK;
            }
            if (shardT >= 0.9f)
            {
                shardT = -1f;
                foreach (var s in shards) s.gameObject.SetActive(false);
                // Crack lines live on the cover sprite — once the shards are
                // gone the socket reads empty until the revive pop.
                heartRt.gameObject.SetActive(false);
            }
        }
        if (heartBroken && heartPop < 0f)
            heartRt.localScale = Vector3.Lerp(heartRt.localScale, Vector3.one, 1f - Mathf.Exp(-12f * dt));
        if (heartPop >= 0f)
        {
            // 0 → 1.2 → 1 flare, then hand back to the drawn heart.
            heartPop += dt / 0.35f;
            heartRt.localScale = Vector3.one * Mathf.LerpUnclamped(0f, 1f, PersonaUi.EaseOutBack(heartPop, 2.4f));
            if (heartPop >= 1.4f)
            {
                heartPop = -1f;
                heartRt.gameObject.SetActive(false);
            }
        }
    }

    private void OnDestroy()
    {
        if (health != null)
        {
            health.Died -= Shatter;
            health.Revived -= ReviveHeart;
        }
        if (stamina != null) stamina.Denied -= OnStaminaDenied;
        if (mana != null) { mana.Restored -= OnManaRestored; mana.Denied -= OnManaDenied; mana.Spent -= OnManaSpent; }
    }

    // Channelled arts pay per frame (fractions of a point) — only real casts pulse.
    private void OnManaSpent(float cost) { if (cost >= 1f) conduit?.Cast(); }
    private void OnStaminaDenied() => spDenied = 1f;
    private void OnManaRestored(float amount) => mpPulse = 1f;
    private void OnManaDenied() => mpDenied = 1f;

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    /// <summary>Three hairline notches at 25/50/75% — the Souls bar's segment
    /// marks. Fixed ink lines on the slot, so they stay put as the fill drains.</summary>
    private static void AddQuartileTicks(RectTransform slot)
    {
        if (slot == null || slot.Find("Tick25") != null) return;
        foreach (var f in new[] { 0.25f, 0.5f, 0.75f })
        {
            var rt = NewRect("Tick" + Mathf.RoundToInt(f * 100f), slot);
            rt.anchorMin = rt.anchorMax = new Vector2(f, 0.5f);
            rt.sizeDelta = new Vector2(1.5f, 0f);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = new Color(PersonaUi.Ink.r, PersonaUi.Ink.g, PersonaUi.Ink.b, 0.5f);
            img.raycastTarget = false;
        }
    }

    private Bar BuildBar(RectTransform frameRt, Rect slotN, Color color, bool withGhost)
    {
        float W = frameRt.sizeDelta.x, H = frameRt.sizeDelta.y;
        float w = slotN.width * W, h = slotN.height * H;

        var slot = NewRect("Slot", frameRt);
        slot.anchorMin = slot.anchorMax = slot.pivot = new Vector2(0f, 1f);
        slot.anchoredPosition = new Vector2(slotN.x * W, -slotN.y * H);
        slot.sizeDelta = new Vector2(w, h);

        var bar = new Bar { w = w, h = h };

        if (withGhost)
        {
            bar.ghost = NewImage("Ghost", slot, fillTex,
                new Color(1f, 0.72f, 0.45f, 0.5f), w, h);
            bar.ghost.raycastTarget = false;
        }

        bar.fill = NewImage("Fill", slot, fillTex, color, w, h);
        bar.fill.raycastTarget = false;
        return bar;
    }

    private static RawImage NewImage(string name, RectTransform parent, Texture2D tex, Color color, float w, float h)
    {
        var rt = NewRect(name, parent);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(w, h);
        var img = rt.gameObject.AddComponent<RawImage>();
        img.texture = tex;
        img.color = color;
        return img;
    }

    private void BuildFillTextures()
    {
        fillTex = new Texture2D(8, 32, TextureFormat.RGBA32, false);
        for (int y = 0; y < 32; y++)
        {
            float v = y / 31f;
            float lum = v < 0.15f ? 0.5f + v / 0.15f * 0.4f
                      : v > 0.85f ? 0.9f - (v - 0.85f) / 0.15f * 0.45f
                      : 0.9f + Mathf.Sin((v - 0.15f) / 0.7f * Mathf.PI) * 0.35f;
            for (int x = 0; x < 8; x++)
                fillTex.SetPixel(x, y, new Color(lum, lum, lum, 1f));
        }
        fillTex.Apply();
        fillTex.wrapMode = TextureWrapMode.Clamp;
        fillTex.filterMode = FilterMode.Bilinear;

        whiteTex = Texture2D.whiteTexture;
    }

    // ---------- per-frame update ----------

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;
        TickHeart(dt);

        if (health != null)
        {
            float hp = health.Current / Mathf.Max(1f, health.Max);
            // Hit feedback triggers on the raw drop, not the smoothed value —
            // the chunk/shake/fire the instant damage lands.
            if (prevHpNorm >= 0f && hp < prevHpNorm - 0.001f)
            {
                hitPunch = Mathf.Min(1f, hitPunch + (prevHpNorm - hp) * 5f + 0.35f);
                hitFlash = 1f;
                hpGhostHold = 0.4f; // the lost chunk holds a beat before draining
            }
            prevHpNorm = hp;
            // Ember pulse below ~28% — the fill breathes toward a hot red as a
            // persistent danger cue (reads through the frame's warm palette).
            lowHp = hp < 0.28f ? (0.28f - hp) / 0.28f : 0f;
            if (hpShown < 0f) { hpShown = hp; hpGhost = hp; }
            hpShown = Mathf.Lerp(hpShown, hp, 1f - Mathf.Exp(-smoothSpeed * dt));
            hpGhostHold = Mathf.Max(0f, hpGhostHold - dt);
            if (hp < hpGhost) { if (hpGhostHold <= 0f) hpGhost = Mathf.MoveTowards(hpGhost, hp, dt / ghostDrain * 0.5f); }
            else hpGhost = hp;
            if (hpBar != null)
            {
                UpdateBar(hpBar, hpShown, hpGhost);
                if (!firstFillLogged)
                {
                    firstFillLogged = true;
                    Debug.Log($"[PlayerHud] first fill hp={hpShown:0.###} fillW={((RectTransform)hpBar.fill.transform).sizeDelta.x:0.#}/{hpBar.w:0.#}");
                }
            }
            else if (externalFills)
            {
                SetFill(hpFill, hpFillBase, hpShown);
                if (hpGhostFill != null) SetFill(hpGhostFill, hpGhostBase, hpGhost);
            }

        }
        if (mana != null)
        {
            float mp = mana.Current / Mathf.Max(1f, mana.Max);
            if (mpShown < 0f) { mpShown = mp; mpGhost = mp; }
            mpShown = Mathf.Lerp(mpShown, mp, 1f - Mathf.Exp(-smoothSpeed * dt));
            if (mp < mpGhost) mpGhost = Mathf.MoveTowards(mpGhost, mp, dt / ghostDrain * 0.5f);
            else mpGhost = mp;
            if (mpBar != null) UpdateBar(mpBar, mpShown, mpGhost);
            else if (externalFills)
            {
                SetFill(mpFill, mpFillBase, mpShown);
                if (mpGhostFill != null) SetFill(mpGhostFill, mpGhostBase, mpGhost);
            }
            // Kill-mana cue: a brief cool pulse on the fill — deliberately
            // subtle, no per-kill message spam. A refused cast (mana or
            // cooldown) flashes the same copper-red the stamina bar uses.
            mpPulse = Mathf.Max(0f, mpPulse - dt * 2.2f);
            mpDenied = Mathf.Max(0f, mpDenied - dt * 2.4f);
            if (conduit != null) conduit.Tick(mpShown, mpDenied, mpPulse, dt);
            else if (mpFillG != null)
            {
                var c = Color.Lerp(mpBaseColor, new Color(1f, 0.32f, 0.22f), mpDenied);
                mpFillG.color = Color.Lerp(c, CoreConduit.Bright, mpPulse * 0.7f);
            }
        }
        if (stamina != null)
        {
            float sp = stamina.Current / Mathf.Max(1f, stamina.Max);
            if (spShown < 0f) spShown = sp;
            spShown = Mathf.Lerp(spShown, sp, 1f - Mathf.Exp(-smoothSpeed * dt));
            if (spBar != null) UpdateBar(spBar, spShown, spShown);
            else if (externalFills) SetFill(spFill, spFillBase, spShown);

            // Spend refused: the bar flashes copper-red and gives a jolt — the
            // "can't afford it" cue without a toast for every button press.
            spDenied = Mathf.Max(0f, spDenied - dt * 2.4f);
            if (spFillG != null)
            {
                spFillG.color = Color.Lerp(spBaseColor, new Color(1f, 0.32f, 0.22f), spDenied);
                ((RectTransform)spFillG.transform).anchoredPosition = spFillPos +
                    new Vector2(Mathf.Sin(spDenied * 22f) * 5f * spDenied, 0f);
            }
        }

        TickEnds(dt);
        TickChips(dt);

        // Hit juice: punch-scale pop + positional jitter + warm flash on the
        // health fill. Applies to whichever rect carries bars+frame (fxRoot) —
        // the Bars container for authored canvases, the frame for runtime.
        if (fxRoot != null && hudHitJuice > 0f)
        {
            hitPunch = Mathf.Max(0f, hitPunch - dt * 4.5f);
            hitFlash = Mathf.Max(0f, hitFlash - dt * 4f);
            if (hitPunch > 0f || hitFlash > 0f)
            {
                var s = 1f + hitPunch * 0.07f * hudHitJuice;
                fxRoot.localScale = new Vector3(s, s, 1f);
                var jit = hitPunch * 9f * hudHitJuice;
                fxRoot.anchoredPosition = fxBasePos +
                    new Vector2(Random.Range(-jit, jit), Random.Range(-jit, jit));
                if (hpFillG != null)
                    hpFillG.color = Color.Lerp(hpBaseColor, new Color(1f, 0.55f, 0.4f), hitFlash * 0.65f * hudHitJuice);
            }
            else
            {
                fxRoot.localScale = Vector3.one;
                fxRoot.anchoredPosition = fxBasePos;
                if (hpFillG != null)
                    hpFillG.color = Color.Lerp(hpBaseColor, new Color(1f, 0.45f, 0.35f),
                        lowHp * (0.7f + 0.3f * Mathf.Sin(Time.unscaledTime * 5.5f)));
            }
        }
    }

    // ---------- fill ends (health / mana / stamina) ----------

    /// <summary>One bar's torn end: rides the fill's right edge, crackles
    /// through tear patterns, and throws chips when the bar drops sharply.</summary>
    private sealed class BarEnd
    {
        public System.Func<RectTransform> fill;
        public RectTransform rt;
        public RawImage img;
        public Texture2D[] frames;
        public Color col;
        public float clock, burst, last = -1f, chipThreshold, cool;
        public int frame;
    }
    private BarEnd hpEnd, mpEnd, spEnd;

    private RectTransform HpFillRect => externalFills ? hpFill : hpBar != null ? (RectTransform)hpBar.fill.transform : null;
    private RectTransform MpFillRect => externalFills ? mpFill : mpBar != null ? (RectTransform)mpBar.fill.transform : null;
    private RectTransform SpFillRect => externalFills ? spFill : spBar != null ? (RectTransform)spBar.fill.transform : null;

    private void BuildHpEnd()
    {
        var fill = HpFillRect;
        if (fill == null || fill.parent == null) return;
        var fillG = fill.GetComponent<Graphic>();
        var baseCol = fillG != null ? fillG.color : hpColor;

        // Ghost chunk behind the fill if the authored canvas has none: a sibling
        // copy of the fill's geometry/texture in a pale bone-red.
        if (externalFills && hpGhostFill == null)
        {
            var g = new GameObject("HpGhost", typeof(RectTransform)).GetComponent<RectTransform>();
            g.gameObject.layer = fill.gameObject.layer;
            g.SetParent(fill.parent, false);
            g.SetSiblingIndex(fill.GetSiblingIndex());
            g.anchorMin = fill.anchorMin; g.anchorMax = fill.anchorMax; g.pivot = fill.pivot;
            g.anchoredPosition = fill.anchoredPosition; g.sizeDelta = fill.sizeDelta;
            g.localRotation = fill.localRotation; g.localScale = hpFillBase;
            var ghostCol = Color.Lerp(baseCol, new Color(1f, .86f, .78f), .45f); ghostCol.a = .8f;
            if (fill.GetComponent<RawImage>() is RawImage raw)
            {
                var gi = g.gameObject.AddComponent<RawImage>();
                gi.texture = raw.texture; gi.uvRect = raw.uvRect; gi.color = ghostCol; gi.raycastTarget = false;
            }
            else if (fill.GetComponent<Image>() is Image im)
            {
                var gi = g.gameObject.AddComponent<Image>();
                gi.sprite = im.sprite; gi.type = im.type; gi.color = ghostCol; gi.raycastTarget = false;
            }
            hpGhostFill = g; hpGhostBase = hpFillBase;
        }

        var canvas = fill.GetComponentInParent<Canvas>();
        chipRoot = canvas != null ? (RectTransform)canvas.rootCanvas.transform : (RectTransform)fill.parent;

        hpEnd = MakeEnd("HpEnd", () => HpFillRect, baseCol, .001f);
        // Mana is tinted Core violet at runtime by CoreConduit; stamina keeps its own colour.
        mpEnd = MakeEnd("MpEnd", () => MpFillRect, CoreConduit.Main, .03f);
        var spG = SpFillRect != null ? SpFillRect.GetComponent<Graphic>() : null;
        // Stamina drains continuously while sprinting — only chunky spends
        // (dodge, attack) break chips off.
        spEnd = MakeEnd("SpEnd", () => SpFillRect, spG != null ? spG.color : spColor, .04f);
    }

    private BarEnd MakeEnd(string name, System.Func<RectTransform> fillOf, Color col, float chipThreshold)
    {
        var fill = fillOf();
        if (fill == null || fill.parent == null) return null;
        var e = new BarEnd { fill = fillOf, col = col, chipThreshold = chipThreshold };
        e.frames = new[] { EndTexture(col, 0), EndTexture(col, 1), EndTexture(col, 2) };
        e.rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        e.rt.gameObject.layer = fill.gameObject.layer;
        e.rt.SetParent(fill.parent, false);
        e.rt.SetSiblingIndex(fill.GetSiblingIndex() + 1);
        e.rt.anchorMin = e.rt.anchorMax = new Vector2(.5f, .5f);
        e.rt.pivot = new Vector2(1f / EndW, .5f); // one fill-coloured column overlaps the cut (no seam)
        e.img = e.rt.gameObject.AddComponent<RawImage>();
        e.img.texture = e.frames[0]; e.img.raycastTarget = false;
        e.frame = Random.Range(0, 3);
        return e;
    }

    private const int EndW = 6, EndH = 8;

    /// <summary>The torn edge, 6×8 texels in the fill's own colour (no bright
    /// cut line): one overlap column, then ragged tear flaps of varying
    /// length per row, a darker last texel and one ink texel at each tip.
    /// Three tear patterns cycle so the edge crackles.</summary>
    private static Texture2D EndTexture(Color fill, int variant)
    {
        int[][] tears =
        {
            new[] { 1, 3, 2, 4, 2, 3, 4, 1 },
            new[] { 2, 2, 4, 3, 3, 4, 2, 2 },
            new[] { 1, 4, 3, 2, 4, 2, 3, 2 },
        };
        var flap = fill; flap.a = 1f;
        var dark = fill * .6f; dark.a = 1f;
        var ink = new Color(.05f, .02f, .04f, 1f);
        var tex = new Texture2D(EndW, EndH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var px = new Color[EndW * EndH];
        for (int y = 0; y < EndH; y++)
        {
            var t = tears[variant][y];
            for (int x = 0; x < EndW; x++)
            {
                Color c;
                if (x < t) c = flap;          // x = 0 is the overlap column
                else if (x == t) c = dark;
                else if (x == t + 1) c = ink;
                else c = Color.clear;
                px[y * EndW + x] = c;
            }
        }
        tex.SetPixels(px); tex.Apply(false, false);
        return tex;
    }

    private void TickEnds(float dt)
    {
        var dead = health != null && health.IsDead;
        TickEnd(hpEnd, hpShown, dt, lowHp, dead, Color.Lerp(Color.white, new Color(1f, .8f, .7f), hitFlash));
        TickEnd(mpEnd, mpShown, dt, 0f, dead, Color.white);
        TickEnd(spEnd, spShown, dt, 0f, dead, Color.white);
    }

    private void TickEnd(BarEnd e, float shown, float dt, float low, bool dead, Color tint)
    {
        if (e == null) return;
        var fill = e.fill();
        var visible = fill != null && shown > .004f && shown < .996f && !dead;
        e.img.enabled = visible;
        if (fill == null) return;

        // Sharp drop since last frame → chips (gradual drains stay quiet).
        if (e.last >= 0f && shown < e.last - e.chipThreshold * dt * 60f) e.burst += e.last - shown;
        e.last = shown;

        // The fill's right edge, in its parent's space (works for scale- or width-driven fills).
        var r = fill.rect;
        var parent = (RectTransform)fill.parent;
        var right = parent.InverseTransformPoint(fill.TransformPoint(new Vector3(r.xMax, r.center.y, 0f)));
        var top = parent.InverseTransformPoint(fill.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
        var h = Mathf.Abs(top.y - right.y) * 2f;
        e.rt.localPosition = right;
        e.rt.sizeDelta = new Vector2(h * EndW / EndH, h);

        // Crackle: swap tear patterns, faster while low.
        e.clock += dt * (8f + 10f * low);
        if (e.clock >= 1f) { e.clock = 0f; e.frame = (e.frame + 1 + Random.Range(0, 2)) % e.frames.Length; e.img.texture = e.frames[e.frame]; }
        e.img.color = tint;

        var texel = h / EndH;
        // One burst per hit: the shown value eases down over several frames, so the
        // drop accumulates and fires at most every 0.3s.
        e.cool -= dt;
        if (visible && e.burst > .01f && e.cool <= 0f)
        {
            var n = Mathf.Clamp(Mathf.RoundToInt(3 + e.burst * 40f), 3, 9);
            for (int i = 0; i < n; i++)
                SpawnChip(e, texel, new Vector2(Random.Range(30f, 140f), Random.Range(-10f, 90f)), i % 3 == 0, .55f);
            e.burst = 0f; e.cool = .3f;
        }
        if (!visible) e.burst = 0f;
        if (e == hpEnd && visible && low > 0f && (hpEmberClock -= dt) <= 0f)
        {
            hpEmberClock = Mathf.Lerp(.7f, .22f, low);
            SpawnChip(e, texel, new Vector2(Random.Range(-15f, 25f), Random.Range(40f, 80f)), true, .8f, rise: true);
        }
    }

    private void TickChips(float dt)
    {
        for (int i = chips.Count - 1; i >= 0; i--)
        {
            var (img, vel, age, life) = chips[i];
            if (img == null) { chips.RemoveAt(i); continue; }
            if (age >= life) continue;
            age += dt;
            if (age >= life) { img.gameObject.SetActive(false); chips[i] = (img, vel, life, life); continue; }
            if (life < .7f) vel.y -= 420f * dt; // falling chips; embers keep rising
            var rt = (RectTransform)img.transform;
            rt.anchoredPosition += vel * dt;
            var c = img.color; c.a = 1f - Mathf.Floor(age / life * 4f) / 4f; img.color = c; // stepped fade
            chips[i] = (img, vel, age, life);
        }
    }

    private void SpawnChip(BarEnd e, float texel, Vector2 vel, bool light, float life, bool rise = false)
    {
        if (chipRoot == null) return;
        RawImage img = null;
        for (int i = 0; i < chips.Count; i++)
            if (chips[i].img != null && chips[i].age >= chips[i].life) { img = chips[i].img; chips.RemoveAt(i); break; }
        if (img == null)
        {
            if (whiteTex == null) { whiteTex = new Texture2D(1, 1); whiteTex.SetPixel(0, 0, Color.white); whiteTex.Apply(); }
            var go = new GameObject("BarChip", typeof(RectTransform));
            go.layer = chipRoot.gameObject.layer;
            img = go.AddComponent<RawImage>(); img.texture = whiteTex; img.raycastTarget = false;
            go.transform.SetParent(chipRoot, false);
        }
        img.gameObject.SetActive(true);
        var rt = (RectTransform)img.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(.5f, .5f);
        rt.position = e.rt.position;
        // Chip size in the chip root's units: one fill texel (embers a bit smaller).
        var scale = chipRoot.lossyScale.x > 1e-5f ? e.rt.lossyScale.x / chipRoot.lossyScale.x : 1f;
        var s = texel * scale * (rise ? .8f : Random.Range(1f, 1.6f));
        rt.sizeDelta = new Vector2(s, s);
        // Chips are the bar's own colour (lighter or darker) — never white.
        var col = light ? Color.Lerp(e.col, Color.white, .25f) : e.col * .7f;
        col.a = 1f;
        img.color = col;
        chips.Add((img, vel, 0f, life)); // velocity in canvas reference pixels/s
    }

    /// <summary>Drives an author-assigned fill: scales X by the normalized
    /// value around the scale captured at Awake — no width math, works with
    /// any authored bar. Pivot x=0 keeps it left-anchored as it shrinks.</summary>
    private static void SetFill(RectTransform rt, Vector3 baseScale, float norm)
    {
        rt.localScale = new Vector3(baseScale.x * Mathf.Clamp01(norm), baseScale.y, baseScale.z);
    }

    private void UpdateBar(Bar bar, float fill, float ghost)
    {
        ((RectTransform)bar.fill.transform).sizeDelta = new Vector2(Mathf.Clamp01(fill) * bar.w, bar.h);
        if (bar.ghost != null)
            ((RectTransform)bar.ghost.transform).sizeDelta = new Vector2(Mathf.Clamp01(ghost) * bar.w, bar.h);
    }

    // ---------- OnGUI: enemy bar, plain fallback ----------

    private void OnGUI()
    {
        if (!canvasBuilt) DrawPlainBars();
        // Death stamp lives on GameHud's Persona slab — no plain-text duplicate.
    }

    private void DrawPlainBars()
    {
        float x = margin.x, y = margin.y;
        if (health != null)
        {
            PlainBar(new Rect(x, y, 320f, 20f), health.Current / Mathf.Max(1f, health.Max), hpColor);
            y += 28f;
        }
        if (mana != null)
        {
            PlainBar(new Rect(x, y, 240f, 14f), mana.Current / Mathf.Max(1f, mana.Max), mpColor);
            y += 20f;
        }
        if (stamina != null)
            PlainBar(new Rect(x, y, 240f, 14f), stamina.Current / Mathf.Max(1f, stamina.Max), spColor);
    }

    private void PlainBar(Rect r, float fill, Color color)
    {
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(r, whiteTex);
        GUI.color = color;
        GUI.DrawTexture(new Rect(r.x + 1f, r.y + 1f, (r.width - 2f) * Mathf.Clamp01(fill), r.height - 2f), whiteTex);
        GUI.color = prev;
    }

}
