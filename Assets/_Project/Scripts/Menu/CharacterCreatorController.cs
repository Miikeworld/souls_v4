using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The character-creation panel, organised as pages: a hub (name, sex, class,
/// gift + links) and sub-panels for FACE, ARMOUR, EXTRAS and COLOURS. Up/down
/// moves between rows, left/right cycles the value; link rows open their page
/// with a slide/fade (no stripes). Every change is applied live to the
/// MenuCharacterPreview. Confirm saves the build and hands off to the game
/// scene; Esc backs out of a sub-page first, then cancels.
/// </summary>
public sealed class CharacterCreatorController : MonoBehaviour
{
    [SerializeField] private MenuCharacterPreview preview;

    private RectTransform panel;
    private RectTransform pageHost;
    private InputField nameField;
    private readonly List<Row> rows = new List<Row>();
    private readonly List<Page> pages = new List<Page>();

    private CharacterBuildData build;
    private Action<CharacterBuildData> onConfirm;
    private Action onBack;
    private bool isOpen;

    private int pageIndex;
    private Page animFrom, animTo;
    private float animT;
    private const float PageAnimDur = 0.22f;

    public sealed class Row
    {
        public string label;
        public Text valueText;
        public Image swatch;
        public Func<string> getText;
        public Func<Color> swatchSource;
        public Func<string> describe;
        public Func<bool> isEnabled;
        public Action<int> cycle; // -1 / +1
        public bool link;      // link/action row ??no ????value arrows
        public string chevron; // glyph baked into the label (» opens, « back)
        public Selectable selectable;
    }

    private sealed class Page
    {
        public RectTransform rt;
        public RectTransform list;
        public CanvasGroup group;
        public Selectable firstSelectable;
        public int emitted;
        public bool cascade; // hub only: rows drift right + narrow
    }

    public void Open(CharacterBuildData data, Action<CharacterBuildData> confirm, Action back)
    {
        build = data;
        build.EnsureParts(); // seed per-piece slots from the outfit on first use
        onConfirm = confirm;
        onBack = back;
        if (panel == null) BuildPanel();
        panel.gameObject.SetActive(true);
        isOpen = true;
        if (nameField != null) nameField.text = build.characterName;
        SetPage(0);
        RefreshAll();
        var first = pages[0].firstSelectable;
        if (first != null && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(first.gameObject);
    }

    private void Update()
    {
        if (!isOpen) return;
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            if (pageIndex != 0) OpenPage(0);
            else Close(false);
        }

        // Sub-page swap: out slides left + fades, in slides from the right
        // with a light overshoot. Short and smooth ??no stripes.
        if (animFrom == null && animTo == null) return;
        animT = Mathf.Clamp01(animT + Time.unscaledDeltaTime / PageAnimDur);
        if (animFrom != null)
        {
            var e = animT * animT;
            animFrom.rt.anchoredPosition = new Vector2(-70f * e, 0f);
            animFrom.group.alpha = 1f - animT;
            if (animT >= 1f) { animFrom.rt.gameObject.SetActive(false); animFrom = null; }
        }
        if (animTo != null)
        {
            var e = EaseOutBack(animT);
            animTo.rt.anchoredPosition = new Vector2(80f * (1f - e), 0f);
            animTo.group.alpha = Mathf.Clamp01(animT * 1.8f);
            if (animT >= 1f) { animTo.rt.anchoredPosition = Vector2.zero; animTo = null; }
        }
    }

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        var u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    private void SetPage(int index)
    {
        pageIndex = index;
        for (var i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var on = i == index;
            p.rt.gameObject.SetActive(on);
            p.rt.anchoredPosition = Vector2.zero;
            p.group.alpha = on ? 1f : 0f;
        }
        animFrom = animTo = null;
    }

    private void OpenPage(int index)
    {
        if (index == pageIndex || index < 0 || index >= pages.Count) return;
        animFrom = pages[pageIndex];
        animTo = pages[index];
        pageIndex = index;
        animT = 0f;
        animTo.rt.gameObject.SetActive(true);
        animTo.group.alpha = 0f;
        RefreshAll(); // face page previews without headwear
        var first = animTo.firstSelectable;
        if (first != null && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(first.gameObject);
    }

    private void Close(bool confirmed)
    {
        isOpen = false;
        // Confirm: the panel stays up under the scene-load wipe. Back: hide it
        // behind the slabs so the swap to the menu is never seen bare.
        if (confirmed) { onConfirm?.Invoke(build); return; }
        PersonaTransition.Run(() =>
        {
            if (panel != null) panel.gameObject.SetActive(false);
            onBack?.Invoke();
        });
    }

    /// <summary>RowNav still reports selection here — the info box is gone,
    /// so this is a no-op kept for the hook.</summary>
    public void ShowInfo(string text) { }

    // ---------- construction ----------

    private void BuildPanel()
    {
        // Parent to the menu's pixel canvas so the panel is pixelated with the
        // rest of the Persona UI (a plain FindFirstObjectByType<Canvas> could
        // grab the display overlay instead).
        var menu = FindFirstObjectByType<MainMenuController>();
        var canvas = menu != null && menu.MenuCanvas != null ? menu.MenuCanvas : FindFirstObjectByType<Canvas>();
        if (canvas == null)
        {
            Debug.LogError("[CharCreate] no canvas ??MainMenuController must Awake first.");
            return;
        }

        panel = SoulsUi.Rect(canvas.transform, "CharCreate",
            new Vector2(0f, 0f), new Vector2(0.46f, 1f), Vector2.zero, Vector2.zero);
        MenuParallax.Attach(panel, 0.4f); // gentle — the preview drag lives beside it
        // Leaning slab panel: ink drop, violet body, heart-red edge.
        var drop = PersonaUi.Poly(panel, "Shadow", PolyGraphic.Shape.Parallelogram, PersonaUi.Ink, 0.1f);
        ((RectTransform)drop.transform).anchoredPosition = new Vector2(22f, 0f);
        var edgeRt = SoulsUi.Rect(panel, "Edge", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-8f, 0f), new Vector2(16f, 0f));
        PersonaUi.Poly(edgeRt, "Slab", PolyGraphic.Shape.Parallelogram, PersonaUi.Heart, 0.1f);
        var body = PersonaUi.Poly(panel, "Body", PolyGraphic.Shape.Parallelogram, SoulsUi.PanelBg, 0.1f);
        body.raycastTarget = true; // blocks drag-to-spin over the panel

        PersonaUi.Heading(panel, "Header", "CHARACTER CREATION", 44, new Vector2(0.5f, 1f),
            new Vector2(0f, -72f), new Vector2(720f, 96f), PersonaUi.Heart);

        // Pages fill the space between the header and the confirm buttons.
        pageHost = SoulsUi.Rect(panel, "Pages", new Vector2(0f, 0f), new Vector2(1f, 1f),
            new Vector2(0f, 96f), new Vector2(0f, -128f));

        var rig = preview != null ? preview.Rig : null;
        BuildHubPage(rig);
        BuildStatsPage();
        BuildFacePage(rig);
        BuildArmourPage(rig);
        BuildExtrasPage(rig);
        BuildColoursPage();

        // Confirm / Cancel ??Persona slabs.
        const float margin = 60f;
        var confRt = SoulsUi.Rect(panel, "Confirm", new Vector2(0f, 0f), new Vector2(0.5f, 0f),
            new Vector2(margin, 22f), new Vector2(-14f, 84f));
        PersonaUi.Button(confRt, "Btn", "CONFIRM", 36, () => Close(true), 0);
        var backRt = SoulsUi.Rect(panel, "Cancel", new Vector2(0.5f, 0f), new Vector2(1f, 0f),
            new Vector2(14f, 22f), new Vector2(-margin, 84f));
        PersonaUi.Button(backRt, "Btn", "CANCEL", 36, () => Close(false), 1);
    }

    // ---------- pages ----------

    private void BuildHubPage(Transform rig)
    {
        var pg = NewPage("HUB");
        float y = -6f;
        var nameRt = RowRect(pg, "Name", ref y, RowH, pg.emitted++);
        BuildNameRow(pg, nameRt);

        AddRow(pg, ref y, "Sex",
            () => build.female ? "Female" : "Male",
            d => { build.female = !build.female; build.EnsureParts(); RefreshAll(); },
            () => "Body type. Looks only ??no stat change.");

        LinkRow(pg, ref y, "Stats", StatsPage, "Vigor, Endurance, Mind.");

        y -= 30f; // gap before the section links
        LinkRow(pg, ref y, "Appearance", FacePage, "Face, hair, brows, beard, skin, eyes.");
        LinkRow(pg, ref y, "Armour", FacePage + 1, "Outfit, helmet, chest, arms, legs, hands.");
        LinkRow(pg, ref y, "Extras", FacePage + 2, "Cape, crest, pauldrons, elbows, knees, belt.");
        LinkRow(pg, ref y, "Colours", FacePage + 3, "Cloth, trim, leather, metal, war paint.");

        y -= 20f;
        AddRow(pg, ref y, "Randomise", () => "", d => { if (d > 0) Randomize(rig); },
            () => "Re-roll every cosmetic.", link: true, h: 54f);
    }

    // Page order: Hub, Stats, Face, Armour, Extras, Colours.
    private const int StatsPage = 1, FacePage = 2;

    private void BuildStatsPage()
    {
        var pg = NewPage("STATS");
        float y = -48f;
        StatRow(pg, ref y, "Vigor", () => build.vigor, v => build.vigor = v,
            v => $"Max HP {(int)CharacterCatalog.StatRules.Hp(v)}. How much punishment you walk off.");
        StatRow(pg, ref y, "Endurance", () => build.endurance, v => build.endurance = v,
            v => $"Max Stamina {(int)CharacterCatalog.StatRules.Stamina(v)}. Swings, rolls and sprints before you gasp.");
        StatRow(pg, ref y, "Mind", () => build.mind, v => build.mind = v,
            v => $"Max Core Energy {(int)CharacterCatalog.StatRules.Mana(v)}. Your Violet Core's reserve — arts, suit power, Overdrive containment.");

        y -= 20f;
        AddRow(pg, ref y, "Points",
            () => CharacterCatalog.StatRules.Remaining(build.vigor, build.endurance, build.mind).ToString(),
            d => { }, () => "Points left to spend.", isEnabled: () => false);
        AddRow(pg, ref y, "Reset", () => "",
            d => { if (d > 0) { build.vigor = build.endurance = build.mind = CharacterCatalog.StatRules.Base; RefreshAll(); } },
            () => "Return every stat to 10.", link: true, h: 54f);
        BackRow(pg, ref y);
    }

    /// <summary>A ±1 stat row bounded by the cap and the shared point pool.</summary>
    private void StatRow(Page pg, ref float y, string label, Func<int> get, Action<int> set, Func<int, string> desc)
    {
        AddRow(pg, ref y, label,
            () => get().ToString(),
            d =>
            {
                var v = get();
                if (d > 0 && CharacterCatalog.StatRules.CanRaise(v, build.vigor, build.endurance, build.mind)) set(v + 1);
                else if (d < 0 && CharacterCatalog.StatRules.CanLower(v)) set(v - 1);
                RefreshAll();
            },
            () => desc(get()));
    }

    private void BuildFacePage(Transform rig)
    {
        var pg = NewPage("FACE");
        float y = -48f;

        AddRow(pg, ref y, "Face",
            () => $"{build.headIndex + 1} / {Mathf.Max(1, HeroLibrary.GenderedPartCount(rig, "Chr_Head_", build.female))}",
            d => { build.headIndex = Wrap(build.headIndex + d, HeroLibrary.GenderedPartCount(rig, "Chr_Head_", build.female)); RefreshAll(); },
            () => "Face shape. Headwear hides while you edit here.");

        AddRow(pg, ref y, "Ears",
            () => $"{build.ears + 1} / {Mathf.Max(1, HeroLibrary.PartCount(rig, "Chr_Ear_Ear_"))}",
            d => { build.ears = Wrap(build.ears + d, HeroLibrary.PartCount(rig, "Chr_Ear_Ear_")); RefreshAll(); },
            () => "Ear shape.");

        AddRow(pg, ref y, "Hair",
            () => build.hairIndex <= 0 ? "Bald" : $"{build.hairIndex} / {HeroLibrary.PartCount(rig, "Chr_Hair_")}",
            d => { build.hairIndex = Wrap(build.hairIndex + d, HeroLibrary.PartCount(rig, "Chr_Hair_") + 1); RefreshAll(); },
            () => "Hair style. Hidden by full helmets.");

        AddRow(pg, ref y, "Hair Color",
            () => "",
            d => { build.hairColorIndex = Wrap(build.hairColorIndex + d, CharacterCatalog.HairColors.Length); RefreshAll(); },
            () => "Hair and brow color.", swatch: () => CharacterCatalog.HairColors[Wrap(build.hairColorIndex, CharacterCatalog.HairColors.Length)]);

        AddRow(pg, ref y, "Brows",
            () => $"{build.eyebrowIndex + 1} / {Mathf.Max(1, BrowCount(rig))}",
            d => { build.eyebrowIndex = Wrap(build.eyebrowIndex + d, BrowCount(rig)); RefreshAll(); },
            () => "Brow shape.");

        AddRow(pg, ref y, "Beard",
            () => !build.female ? (build.facialHairIndex <= 0 ? "Clean" : $"{build.facialHairIndex} / {HeroLibrary.PartCount(rig, "Chr_FacialHair_Male_")}") : "None",
            d => { if (build.female) return; build.facialHairIndex = Wrap(build.facialHairIndex + d, HeroLibrary.PartCount(rig, "Chr_FacialHair_Male_") + 1); RefreshAll(); },
            () => build.female ? "Male only. Hidden by masks." : "Beard style. Hidden by masks.",
            isEnabled: () => !build.female);

        AddRow(pg, ref y, "Skin",
            () => "",
            d => { build.skinIndex = Wrap(build.skinIndex + d, CharacterCatalog.SkinTones.Length); RefreshAll(); },
            () => "Skin color.", swatch: () => CharacterCatalog.SkinTones[Wrap(build.skinIndex, CharacterCatalog.SkinTones.Length)]);

        AddRow(pg, ref y, "Eyes",
            () => "",
            d => { build.eyeColorIndex = Wrap(build.eyeColorIndex + d, CharacterCatalog.EyeColors.Length); RefreshAll(); },
            () => "Eye color.", swatch: () => CharacterCatalog.EyeColors[Wrap(build.eyeColorIndex, CharacterCatalog.EyeColors.Length)]);

        BackRow(pg, ref y);
    }

    private void BuildArmourPage(Transform rig)
    {
        var pg = NewPage("ARMOUR");
        float y = -48f;

        // Outfit = preset; picking one reseeds every piece below.
        AddRow(pg, ref y, "Outfit",
            () => CharacterCatalog.Attires[Wrap(build.attireIndex, CharacterCatalog.Attires.Length)].name,
            d => { build.attireIndex = Wrap(build.attireIndex + d, CharacterCatalog.Attires.Length); build.SeedFromAttire(); RefreshAll(); },
            () => "Full preset. Reseeds every armour piece.");

        AddRow(pg, ref y, "Headwear",
            () => HeadwearText(rig),
            d => { build.headwear = WrapOpt(build.headwear + d, HeadwearCount(rig)); RefreshAll(); },
            () => "Helmets hide hair, hoods keep it, masks hide beard.");

        AddRow(pg, ref y, "Head",
            () => $"{build.headIndex + 1} / {Mathf.Max(1, HeroLibrary.GenderedPartCount(rig, "Chr_Head_", build.female))}",
            d => { build.headIndex = Wrap(build.headIndex + d, HeroLibrary.GenderedPartCount(rig, "Chr_Head_", build.female)); RefreshAll(); },
            () => "Head shape ??same as Face in APPEARANCE.");

        GenderedPart(pg, ref y, rig, "Chest", "Chr_Torso_", () => build.torso, v => build.torso = v, "Chest piece.");
        GenderedPart(pg, ref y, rig, "Waist", "Chr_Hips_", () => build.hips, v => build.hips = v, "Waist / tassets.");
        GenderedPart(pg, ref y, rig, "Leg L", "Chr_LegLeft_", () => build.legL, v => build.legL = v, "Left leg.");
        GenderedPart(pg, ref y, rig, "Leg R", "Chr_LegRight_", () => build.legR, v => build.legR = v, "Right leg.");
        GenderedPart(pg, ref y, rig, "Arm L", "Chr_ArmUpperLeft_", () => build.armUpL, v => build.armUpL = v, "Left upper arm.");
        GenderedPart(pg, ref y, rig, "Arm R", "Chr_ArmUpperRight_", () => build.armUpR, v => build.armUpR = v, "Right upper arm.");
        GenderedPart(pg, ref y, rig, "Forearm L", "Chr_ArmLowerLeft_", () => build.armLowL, v => build.armLowL = v, "Left forearm.");
        GenderedPart(pg, ref y, rig, "Forearm R", "Chr_ArmLowerRight_", () => build.armLowR, v => build.armLowR = v, "Right forearm.");
        GenderedPart(pg, ref y, rig, "Hand L", "Chr_HandLeft_", () => build.handL, v => build.handL = v, "Left glove.");
        GenderedPart(pg, ref y, rig, "Hand R", "Chr_HandRight_", () => build.handR, v => build.handR = v, "Right glove.");

        BackRow(pg, ref y);
    }

    private void BuildExtrasPage(Transform rig)
    {
        var pg = NewPage("EXTRAS");
        float y = -48f;

        OptPart(pg, ref y, rig, "Cape", "Chr_BackAttachment_", () => build.back, v => build.back = v, "Back attachment ??capes, packs.");
        OptPart(pg, ref y, rig, "Crest", "Chr_HelmetAttachment_", () => build.crest, v => build.crest = v, "Helmet ornament.");
        OptPart(pg, ref y, rig, "Shoulder L", "Chr_ShoulderAttachLeft_", () => build.shoulderL, v => build.shoulderL = v, "Left pauldron.");
        OptPart(pg, ref y, rig, "Shoulder R", "Chr_ShoulderAttachRight_", () => build.shoulderR, v => build.shoulderR = v, "Right pauldron.");
        OptPart(pg, ref y, rig, "Elbow L", "Chr_ElbowAttachLeft_", () => build.elbowL, v => build.elbowL = v, "Left elbow piece.");
        OptPart(pg, ref y, rig, "Elbow R", "Chr_ElbowAttachRight_", () => build.elbowR, v => build.elbowR = v, "Right elbow piece.");
        OptPart(pg, ref y, rig, "Knee L", "Chr_KneeAttachLeft_", () => build.kneeL, v => build.kneeL = v, "Left knee piece.");
        OptPart(pg, ref y, rig, "Knee R", "Chr_KneeAttachRight_", () => build.kneeR, v => build.kneeR = v, "Right knee piece.");
        OptPart(pg, ref y, rig, "Belt", "Chr_HipsAttachment_", () => build.hipsAttach, v => build.hipsAttach = v, "Belt / waist gear.");

        BackRow(pg, ref y);
    }

    private void BuildColoursPage()
    {
        var pg = NewPage("COLOURS");
        float y = -48f;

        ColorRow(pg, ref y, "Cloth", CharacterCatalog.ClothColors, () => build.clothColorIndex, v => build.clothColorIndex = v, "Main cloth colour.");
        ColorRow(pg, ref y, "Trim", CharacterCatalog.TrimColors, () => build.trimColorIndex, v => build.trimColorIndex = v, "Secondary / trim colour.");
        ColorRow(pg, ref y, "Leather", CharacterCatalog.LeatherColors, () => build.leatherColorIndex, v => build.leatherColorIndex = v, "Leather straps and gear.");
        ColorRow(pg, ref y, "Metal", CharacterCatalog.MetalColors, () => build.metalColorIndex, v => build.metalColorIndex = v, "Armour metal tint.");
        ColorRow(pg, ref y, "Paint", CharacterCatalog.PaintColors, () => build.paintColorIndex, v => build.paintColorIndex = v, "War paint & scars ??visible on painted faces.");

        BackRow(pg, ref y);
    }

    // ---------- row builders ----------

    private const float RowH = 46f, RowGap = 12f, Margin = 60f;

    private Page NewPage(string title)
    {
        var pg = new Page();
        pg.rt = SoulsUi.Stretch(pageHost, title);
        pg.group = pg.rt.gameObject.AddComponent<CanvasGroup>();
        pg.cascade = title == "HUB";
        // The hub keeps the tilted list; sub-panel rows drop in a straight
        // column (the cascade + tilt read as diagonal drift there).
        pg.list = SoulsUi.Stretch(pg.rt, "List");
        pg.list.localRotation = Quaternion.Euler(0f, 0f, pg.cascade ? PersonaUi.Tilt * 0.5f : 0f);
        if (title != "HUB")
            PersonaUi.Heading(pg.rt, "Header", title, 30, new Vector2(0f, 1f),
                new Vector2(190f, -34f), new Vector2(360f, 62f), PersonaUi.Violet, -PersonaUi.Tilt);
        pages.Add(pg);
        return pg;
    }

    /// <summary>Hub rows cascade right and narrow down the list ??the same
    /// rhythm the main menu's slabs use. Sub-panel rows are a straight,
    /// full-width column.</summary>
    private RectTransform RowRect(Page pg, string name, ref float y, float h, int index)
    {
        var step = pg.cascade ? index : 0;
        var rt = SoulsUi.Rect(pg.list, name, new Vector2(0f, 1f), new Vector2(1f, 1f),
            new Vector2(Margin + step * 10f, y - h), new Vector2(-Margin - step * 16f, y));
        y -= h + RowGap;
        return rt;
    }

    /// <summary>Every row is a tilted Persona slab ??same hover slam, star and
    /// float as the main menu. Option rows add ????steppers + a value (or
    /// swatch) inside the slab's motion rect; link rows are plain slabs.</summary>
    private void EmitRow(Page pg, Row r, RectTransform rowRt, int index)
    {
        var btnLabel = r.label;
        if (r.chevron == "«") btnLabel = "« " + btnLabel;
        else if (r.chevron != null) btnLabel += " " + r.chevron;
        var btn = PersonaUi.Button(rowRt, "Btn", btnLabel.ToUpperInvariant(),
            r.link ? 33 : 27, () => { if (r.isEnabled()) r.cycle(1); }, index, 0.35f);
        r.selectable = btn;

        if (!r.link)
        {
            var lab = btn.LabelText;
            lab.rectTransform.anchorMax = new Vector2(0.44f, 1f);
            lab.color = SoulsUi.Dim;
            lab.fontStyle = FontStyle.Bold;

            MiniArrow(btn.Motion, false, 0.50f, () => { if (r.isEnabled()) r.cycle(-1); });
            MiniArrow(btn.Motion, true, 0.90f, () => { if (r.isEnabled()) r.cycle(1); });
            if (r.swatchSource != null)
            {
                // Ink frame behind the swatch so the colour reads on any slab.
                var frRt = SoulsUi.Rect(btn.Motion, "SwatchFrame", new Vector2(0.60f, 0.22f), new Vector2(0.88f, 0.78f), Vector2.zero, Vector2.zero);
                var frame = frRt.gameObject.AddComponent<Image>();
                frame.color = PersonaUi.Ink;
                var swRt = SoulsUi.Rect(frRt, "Swatch", Vector2.zero, Vector2.one,
                    new Vector2(4f, 4f), new Vector2(-4f, -4f));
                r.swatch = swRt.gameObject.AddComponent<Image>();
            }
            else
            {
                var vrt = SoulsUi.Rect(btn.Motion, "Value", new Vector2(0.56f, 0f), new Vector2(0.90f, 1f), Vector2.zero, Vector2.zero);
                r.valueText = SoulsUi.Label(vrt, "Text", "", 27, PersonaUi.Bone, TextAnchor.MiddleCenter, FontStyle.Bold);
            }
        }

        var nav = btn.gameObject.AddComponent<RowNav>();
        nav.row = r; nav.owner = this;
        if (pg.firstSelectable == null) pg.firstSelectable = btn;
    }

    /// <summary>Small triangle-slab button inside a row slab — click target
    /// only, kept out of the nav chain. Drawn geometry, not a glyph, so it
    /// renders in any font.</summary>
    private static void MiniArrow(Transform parent, bool right, float x, UnityEngine.Events.UnityAction act)
    {
        var rt = SoulsUi.Rect(parent, right ? "ArrowR" : "ArrowL",
            new Vector2(x - 0.02f, 0f), new Vector2(x + 0.045f, 1f), Vector2.zero, Vector2.zero);
        var inner = SoulsUi.Rect(rt, "TriArea", new Vector2(0.18f, 0.34f), new Vector2(0.82f, 0.66f),
            Vector2.zero, Vector2.zero);
        var tri = PersonaUi.Poly(inner, "Tri", PolyGraphic.Shape.Triangle, PersonaUi.Bone);
        ((RectTransform)tri.transform).localRotation = Quaternion.Euler(0f, 0f, right ? -90f : 90f);
        tri.raycastTarget = true;
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = tri;
        var c = b.colors;
        c.normalColor = PersonaUi.Bone;
        c.highlightedColor = PersonaUi.Heart;
        c.pressedColor = PersonaUi.Heart;
        b.colors = c;
        b.navigation = new Navigation { mode = Navigation.Mode.None };
        b.onClick.AddListener(act);
    }

    private Row AddRow(Page pg, ref float y, string label, Func<string> getText, Action<int> cycle,
        Func<string> describe, Func<bool> isEnabled = null, Func<Color> swatch = null,
        bool link = false, string chevron = null, float h = RowH)
    {
        var r = new Row
        {
            label = label, describe = describe, cycle = cycle,
            isEnabled = isEnabled ?? (() => true), swatchSource = swatch, getText = getText,
            link = link, chevron = chevron,
        };
        rows.Add(r);
        var idx = pg.emitted++;
        EmitRow(pg, r, RowRect(pg, label, ref y, h, idx), idx);
        return r;
    }

    /// <summary>Hub link row — cycles nowhere, right/click opens the page.</summary>
    private void LinkRow(Page pg, ref float y, string label, int page, string desc)
    {
        AddRow(pg, ref y, label, () => "", d => { if (d > 0) OpenPage(page); }, () => desc,
            link: true, chevron: "»", h: 56f);
    }

    private void BackRow(Page pg, ref float y)
    {
        y -= 8f;
        AddRow(pg, ref y, "Back", () => "", d => OpenPage(0), () => "Return.",
            link: true, chevron: "«", h: 46f);
    }

    /// <summary>Required gendered armour piece (never empty).</summary>
    private void GenderedPart(Page pg, ref float y, Transform rig, string label, string category,
        Func<int> get, Action<int> set, string desc)
    {
        int Count() => HeroLibrary.GenderedPartCount(rig, category, build.female);
        AddRow(pg, ref y, label,
            () => $"{get() + 1} / {Mathf.Max(1, Count())}",
            d => { set(Wrap(get() + d, Count())); RefreshAll(); },
            () => desc);
    }

    /// <summary>Optional attachment (index -1 = none).</summary>
    private void OptPart(Page pg, ref float y, Transform rig, string label, string prefix,
        Func<int> get, Action<int> set, string desc)
    {
        int Count() => HeroLibrary.PartCount(rig, prefix);
        AddRow(pg, ref y, label,
            () => get() < 0 ? "None" : $"{get() + 1} / {Count()}",
            d => { set(WrapOpt(get() + d, Count())); RefreshAll(); },
            () => desc);
    }

    private void ColorRow(Page pg, ref float y, string label, Color[] palette,
        Func<int> get, Action<int> set, string desc)
    {
        AddRow(pg, ref y, label,
            () => "",
            d => { set(Wrap(get() + d, palette.Length)); RefreshAll(); },
            () => desc, swatch: () => palette[Wrap(get(), palette.Length)]);
    }

    // ---------- helpers ----------

    private int HeadwearCount(Transform rig)
    {
        return HeroLibrary.PartCount(rig, HeroLibrary.HelmetPrefix)
             + HeroLibrary.PartCount(rig, HeroLibrary.HoodPrefix)
             + HeroLibrary.PartCount(rig, HeroLibrary.MaskPrefix);
    }

    private string HeadwearText(Transform rig)
    {
        if (build.headwear < 0) return "None";
        var n = HeadwearCount(rig);
        var helmets = HeroLibrary.PartCount(rig, HeroLibrary.HelmetPrefix);
        var hoods = HeroLibrary.PartCount(rig, HeroLibrary.HoodPrefix);
        var kind = build.headwear < helmets ? "Helm"
                 : build.headwear < helmets + hoods ? "Hood" : "Mask";
        return $"{kind} {build.headwear + 1} / {n}";
    }

    private void BuildNameRow(Page pg, RectTransform rowRt)
    {
        // Same slab look as the buttons, minus the click anim (it's a field).
        var motion = SoulsUi.Stretch(rowRt, "Motion");
        var sh = PersonaUi.Poly(motion, "Shadow", PolyGraphic.Shape.Parallelogram, PersonaUi.Ink, 0.35f);
        ((RectTransform)sh.transform).anchoredPosition = new Vector2(10f, -10f);
        PersonaUi.Poly(motion, "Slab", PolyGraphic.Shape.Parallelogram, PersonaUi.Night, 0.35f);

        var labelRt = SoulsUi.Rect(motion, "Label", new Vector2(0f, 0f), new Vector2(0.42f, 1f),
            new Vector2(36f, 0f), Vector2.zero);
        SoulsUi.Label(labelRt, "Text", "NAME", 27, SoulsUi.Dim, TextAnchor.MiddleLeft, FontStyle.Bold);

        var fieldRt = SoulsUi.Rect(motion, "Field", new Vector2(0.44f, 0f), new Vector2(0.96f, 1f),
            Vector2.zero, new Vector2(-10f, 0f));
        var fieldBg = fieldRt.gameObject.AddComponent<Image>();
        fieldBg.color = new Color(0f, 0f, 0f, 0.35f);

        var phRt = SoulsUi.Stretch(fieldRt, "Placeholder");
        var ph = phRt.gameObject.AddComponent<Text>();
        ph.font = SoulsUi.Font; ph.text = "Enter a name"; ph.fontSize = 28;
        ph.color = new Color(SoulsUi.Dim.r, SoulsUi.Dim.g, SoulsUi.Dim.b, 0.5f);
        ph.alignment = TextAnchor.MiddleCenter; ph.fontStyle = FontStyle.Italic;

        var txRt = SoulsUi.Stretch(fieldRt, "Text");
        var tx = txRt.gameObject.AddComponent<Text>();
        tx.font = SoulsUi.Font; tx.fontSize = 28; tx.color = SoulsUi.Parchment; tx.fontStyle = FontStyle.Bold;
        tx.alignment = TextAnchor.MiddleCenter;

        nameField = fieldRt.gameObject.AddComponent<InputField>();
        nameField.textComponent = tx;
        nameField.placeholder = ph;
        nameField.characterLimit = 16;
        nameField.contentType = InputField.ContentType.Standard;
        nameField.onValueChanged.AddListener(v => build.characterName =
            string.IsNullOrWhiteSpace(v) ? "Adventurer" : v.Trim());
        if (pg.firstSelectable == null) pg.firstSelectable = nameField;
    }

    private int BrowCount(Transform rig)
    {
        return build.female
            ? HeroLibrary.PartCount(rig, "Chr_Female_Eyebrow_")
            : HeroLibrary.PartCount(rig, "Chr_Eyebrow_Male_");
    }

    private void RefreshAll()
    {
        foreach (var r in rows)
        {
            if (r.valueText != null) r.valueText.text = r.getText();
            if (r.swatch != null && r.swatchSource != null) r.swatch.color = r.swatchSource();
            if (r.selectable != null) r.selectable.interactable = r.isEnabled();
        }
        // Headwear hides on the FACE page so face/hair edits stay visible.
        if (preview != null) preview.Apply(build, pageIndex == FacePage);
    }

    /// <summary>Re-roll every cosmetic field (name and stats are kept).</summary>
    private void Randomize(Transform rig)
    {
        int Roll(int count) => count <= 0 ? 0 : UnityEngine.Random.Range(0, count);
        // -2/-1 both land on None so empty slots stay common, not rare.
        int RollOpt(int count) => count <= 0 ? -1 : Mathf.Max(-1, UnityEngine.Random.Range(-2, count));

        build.female = UnityEngine.Random.value < 0.5f;
        build.headIndex = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_Head_", build.female));
        build.ears = Roll(HeroLibrary.PartCount(rig, "Chr_Ear_Ear_"));
        build.hairIndex = Roll(HeroLibrary.PartCount(rig, "Chr_Hair_") + 1);
        build.hairColorIndex = Roll(CharacterCatalog.HairColors.Length);
        build.eyebrowIndex = Roll(BrowCount(rig));
        build.facialHairIndex = build.female ? 0 : Roll(HeroLibrary.PartCount(rig, "Chr_FacialHair_Male_") + 1);
        build.skinIndex = Roll(CharacterCatalog.SkinTones.Length);
        build.eyeColorIndex = Roll(CharacterCatalog.EyeColors.Length);

        // A random outfit preset first, then re-roll each piece over it.
        build.attireIndex = Roll(CharacterCatalog.Attires.Length);
        build.SeedFromAttire();
        build.torso = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_Torso_", build.female));
        build.hips = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_Hips_", build.female));
        build.legL = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_LegLeft_", build.female));
        build.legR = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_LegRight_", build.female));
        build.armUpL = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_ArmUpperLeft_", build.female));
        build.armUpR = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_ArmUpperRight_", build.female));
        build.armLowL = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_ArmLowerLeft_", build.female));
        build.armLowR = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_ArmLowerRight_", build.female));
        build.handL = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_HandLeft_", build.female));
        build.handR = Roll(HeroLibrary.GenderedPartCount(rig, "Chr_HandRight_", build.female));
        build.headwear = RollOpt(HeadwearCount(rig));
        build.crest = RollOpt(HeroLibrary.PartCount(rig, "Chr_HelmetAttachment_"));
        build.back = RollOpt(HeroLibrary.PartCount(rig, "Chr_BackAttachment_"));
        build.shoulderL = RollOpt(HeroLibrary.PartCount(rig, "Chr_ShoulderAttachLeft_"));
        build.shoulderR = RollOpt(HeroLibrary.PartCount(rig, "Chr_ShoulderAttachRight_"));
        build.elbowL = RollOpt(HeroLibrary.PartCount(rig, "Chr_ElbowAttachLeft_"));
        build.elbowR = RollOpt(HeroLibrary.PartCount(rig, "Chr_ElbowAttachRight_"));
        build.kneeL = RollOpt(HeroLibrary.PartCount(rig, "Chr_KneeAttachLeft_"));
        build.kneeR = RollOpt(HeroLibrary.PartCount(rig, "Chr_KneeAttachRight_"));
        build.hipsAttach = RollOpt(HeroLibrary.PartCount(rig, "Chr_HipsAttachment_"));

        build.clothColorIndex = Roll(CharacterCatalog.ClothColors.Length);
        build.trimColorIndex = Roll(CharacterCatalog.TrimColors.Length);
        build.leatherColorIndex = Roll(CharacterCatalog.LeatherColors.Length);
        build.metalColorIndex = Roll(CharacterCatalog.MetalColors.Length);
        build.paintColorIndex = Roll(CharacterCatalog.PaintColors.Length);
        RefreshAll();
    }

    private static int Wrap(int v, int count)
    {
        if (count <= 0) return 0;
        return ((v % count) + count) % count;
    }

    /// <summary>Wrap over -1..count-1 (the slot can be empty).</summary>
    private static int WrapOpt(int v, int count)
    {
        return Wrap(v + 1, count + 1) - 1;
    }
}
