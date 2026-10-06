using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// The satchel screen — Tab/I/select or the pause menu's ITEMS row, in the
/// main menu's Persona style (<see cref="ItemShell"/>). Four tabs
/// (CONSUMABLES / KEY ITEMS / WEAPONS / ARTS), icon rows on the left and a
/// detail card on the right. E/Enter uses or equips the selected row; A/D or
/// the shoulders switch tabs; Esc/Tab backs out. Real-time like the
/// checkpoint menu — opening your bag does not stop the dungeon.
/// </summary>
public sealed class InventoryMenu : MonoBehaviour
{
    public static bool IsOpen { get; private set; }
    /// <summary>Frame the menu last closed on — PauseMenu ignores that frame's Esc.</summary>
    public static int ClosedFrame { get; private set; } = -1;

    private static InventoryMenu instance;

    private enum Tab { Consumables, KeyItems, Weapons, Arts }
    private static readonly string[] TabNames = { "CONSUMABLES", "KEY ITEMS", "WEAPONS", "ARTS" };

    private PixelCanvas pixels;
    private ItemShell shell;
    private PixelScrollList list;

    private Inventory inv;
    private IAbilitySlots abilities;
    private WeaponSocket socket;
    private Tab tab;
    private int lastSel = -1;
    private int openedFrame = -1; // the opening Tab/E press must not read as this menu's close/confirm

    // One payload per list row — what the detail pane describes and E acts on.
    private struct RowData { public ItemDef item; public WeaponSet weapon; public int art; }
    private readonly List<RowData> rowData = new();

    public static void Toggle()
    {
        if (instance == null)
        {
            instance = new GameObject("InventoryMenu").AddComponent<InventoryMenu>();
            instance.Build();
        }
        if (IsOpen) instance.Hide();
        else instance.Show();
    }

    public static void Close()
    {
        if (instance != null && IsOpen) instance.Hide();
    }

    // ---------- build ----------

    private void Build()
    {
        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        pixels = PixelCanvas.Create("InventoryUi", 62, 1.25f, transform); // above PauseMenu's 60 — ITEMS opens over it
        shell = ItemShell.Build(pixels.Root, "ITEMS", TabNames,
            "A/D  TAB      W/S  SELECT      E  USE      ESC  BACK", i => { tab = (Tab)i; Rebuild(0); });
        list = shell.List;
        PersonaCursor.Build(pixels);
        SetVisible(false);
    }

    private void SetVisible(bool v)
    {
        pixels.Canvas.gameObject.SetActive(v);
        pixels.SetVisible(v);
    }

    // ---------- open / close ----------

    private void Show()
    {
        var loco = FindFirstObjectByType<PlayerLocomotion>();
        if (loco != null)
        {
            inv = loco.GetComponent<Inventory>();
            abilities = loco.GetComponent<IAbilitySlots>();
            socket = loco.GetComponent<WeaponSocket>();
        }
        IsOpen = true;
        tab = Tab.Consumables;
        openedFrame = Time.frameCount;
        SetVisible(true);
        Cursor.lockState = CursorLockMode.None;
        shell.PlayEntrance();
        Rebuild(0);
    }

    private void Hide()
    {
        IsOpen = false;
        ClosedFrame = Time.frameCount;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        SetVisible(false);
        if (!PauseMenu.IsPaused) // pause menu restores its own cursor state
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        SaveGame.Save(inv, socket != null ? socket.Set : null);
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
        IsOpen = false;
    }

    // ---------- content ----------

    private void Rebuild(int keepSel)
    {
        list.Clear();
        rowData.Clear();
        shell.SetTab((int)tab);
        shell.Souls.text = SoulsWallet.Souls.ToString();

        switch (tab)
        {
            case Tab.Consumables: BuildItems(ItemDef.Kind.Consumable); break;
            case Tab.KeyItems: BuildItems(ItemDef.Kind.Key); break;
            case Tab.Weapons: BuildWeapons(); break;
            case Tab.Arts: BuildArts(); break;
        }

        list.FinishRebuild();
        var focus = list.FirstInteractable(Mathf.Clamp(keepSel, 0, list.Count));
        StartCoroutine(SelectNextFrame(focus));
        lastSel = -2; // force detail refresh
    }

    private void BuildItems(ItemDef.Kind kind)
    {
        var any = false;
        if (inv != null)
            for (var i = 0; i < inv.EntryCount; i++)
            {
                var e = inv.Get(i);
                if (e.item == null || e.item.kind != kind || e.count <= 0) continue;
                any = true;
                var item = e.item;
                var row = list.AddRow(item.itemName.ToUpperInvariant(), () => UseItem(item));
                row.SetIcon(HudArt.ItemIcon(item.icon, item.itemName));
                row.SetValue("x" + e.count);
                rowData.Add(new RowData { item = item, art = -1 });
            }
        if (!any) list.AddRow("EMPTY", null).interactable = false;
    }

    private void BuildWeapons()
    {
        var any = false;
        if (inv != null)
            for (var i = 0; i < inv.OwnedCount; i++)
            {
                var set = inv.OwnedAt(i);
                if (set == null) continue;
                any = true;
                var equip = set;
                var row = list.AddRow(set.displayName.ToUpperInvariant(), () => EquipWeapon(equip));
                row.SetIcon(HudArt.ItemIcon(null, set.displayName));
                if (socket != null && socket.Set == set) row.SetValue("EQUIPPED");
                rowData.Add(new RowData { weapon = set, art = -1 });
            }
        if (!any) list.AddRow("NO WEAPONS OWNED", null).interactable = false;
    }

    private void BuildArts()
    {
        var any = false;
        if (abilities != null)
            for (var i = 0; i < abilities.LibraryCount; i++)
            {
                any = true;
                var row = list.AddRow(abilities.LibraryName(i).ToUpperInvariant(), () => GameHud.Toast("ATTUNE AT A CHECKPOINT"));
                row.SetIcon(HudArt.SkillIcon(null, abilities.LibraryName(i)));
                row.SetValue((int)abilities.LibraryCost(i) + " CE");
                rowData.Add(new RowData { art = i });
            }
        if (!any) list.AddRow("NO ARTS KNOWN", null).interactable = false;
    }

    private void UseItem(ItemDef item)
    {
        if (inv == null || item == null) return;
        var idx = SelectedIndex();
        inv.UseInstant(item);
        Rebuild(idx);
    }

    private void EquipWeapon(WeaponSet set)
    {
        if (socket == null || set == null) return;
        socket.Equip(set);
        Rebuild(SelectedIndex());
    }

    private int SelectedIndex()
    {
        var sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        var row = sel != null ? sel.GetComponent<PixelRow>() : null;
        for (var i = 0; i < list.Count; i++) if (list[i] == row) return i;
        return -1;
    }

    // ---------- detail pane ----------

    private void UpdateDetail()
    {
        var idx = SelectedIndex();
        if (idx == lastSel) return;
        lastSel = idx;
        if (idx < 0 || idx >= rowData.Count) { shell.ShowDetail(null, "", "", ""); return; }
        var d = rowData[idx];
        if (d.item != null)
            shell.ShowDetail(HudArt.ItemIcon(d.item.icon, d.item.itemName), d.item.itemName.ToUpperInvariant(),
                (d.item.kind == ItemDef.Kind.Key ? "KEY ITEM" : "CONSUMABLE") + "   ·   x" + inv.Count(d.item),
                string.IsNullOrEmpty(d.item.description) ? EffectLine(d.item) : d.item.description + "\n\n" + EffectLine(d.item));
        else if (d.weapon != null)
            shell.ShowDetail(HudArt.ItemIcon(null, d.weapon.displayName), d.weapon.displayName.ToUpperInvariant(),
                socket != null && socket.Set == d.weapon ? "EQUIPPED" : "WEAPON", "E  equip.");
        else if (d.art >= 0 && abilities != null)
            shell.ShowDetail(HudArt.SkillIcon(null, abilities.LibraryName(d.art)), abilities.LibraryName(d.art).ToUpperInvariant(),
                "WEAPON ART   ·   " + (int)abilities.LibraryCost(d.art) + " CORE ENERGY", "Attuned at a checkpoint's ARTS page.");
    }

    private static string EffectLine(ItemDef item)
    {
        return item.effect switch
        {
            ItemDef.Effect.Heal => "Restores " + (int)item.magnitude + " HP.",
            ItemDef.Effect.Mana => "Restores " + (int)item.magnitude + " Core Energy.",
            ItemDef.Effect.Stamina => "Restores stamina.",
            ItemDef.Effect.Souls => item.magnitude > 0f ? "Grants " + (int)item.magnitude + " souls." : "",
            ItemDef.Effect.Throw => "Throwing weapon: " + (int)item.magnitude + " damage.",
            _ => "",
        };
    }

    private System.Collections.IEnumerator SelectNextFrame(Selectable s)
    {
        yield return null;
        if (s == null || EventSystem.current == null) yield break;
        EventSystem.current.SetSelectedGameObject(null);
        EventSystem.current.SetSelectedGameObject(s.gameObject);
    }

    // ---------- per-frame ----------

    private void Update()
    {
        if (!IsOpen) return;
        shell.Tick(Time.unscaledDeltaTime);

        if (Time.frameCount == openedFrame) return; // the key that opened us is still "pressed this frame"

        var kb = Keyboard.current;
        var gp = Gamepad.current;

        // Tab switch: arrows / A,D / shoulders.
        var prev = (kb != null && (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame))
            || (gp != null && gp.leftShoulder.wasPressedThisFrame);
        var next = (kb != null && (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame))
            || (gp != null && gp.rightShoulder.wasPressedThisFrame);
        if (prev || next)
        {
            var n = TabNames.Length;
            tab = (Tab)(((((int)tab + (next ? 1 : -1)) % n) + n) % n);
            Rebuild(0);
            return;
        }

        // Close: Esc / B / Tab / I / select — same keys that open it.
        var back = (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame
            || kb.iKey.wasPressedThisFrame)) || (gp != null && gp.selectButton.wasPressedThisFrame);
        if (back) { Hide(); return; }

        list.Tick();
        UpdateDetail();

        // E confirms too — mirrors the checkpoint menu (the UI module only
        // maps Enter/Space/South).
        if (kb != null && kb.eKey.wasPressedThisFrame) PersonaUi.SubmitSelected();
    }
}

/// <summary>
/// The shared Persona layout of the satchel (<see cref="InventoryMenu"/>) and
/// the merchant (<see cref="ShopMenu"/>): compact tilted backdrop, title
/// heading, a souls tag, a tab strip of slabs, a list card holding a
/// <see cref="PixelScrollList"/> and a detail card with the item's icon
/// floating in an ink diamond, its name, a heart meta slab and the
/// description. Plain class — the owning menu calls <see cref="Tick"/>.
/// </summary>
public sealed class ItemShell
{
    public PixelScrollList List { get; private set; }
    public Text Souls { get; private set; }

    private RectTransform listMotion, detailMotion, iconHost, metaSlab;
    private Image icon;
    private Text name, meta, desc;
    private readonly List<(RectTransform rt, PolyGraphic slab, Text label)> tabs = new();
    private int tabIndex = -1;
    private float enterT = 1f, punch, tabPunch;

    public static ItemShell Build(RectTransform root, string title, string[] tabNames, string hint, System.Action<int> onTab)
    {
        var s = new ItemShell();
        var dim = PersonaUi.Stretch(root, "Dim").gameObject.AddComponent<Image>();
        dim.color = PersonaUi.WithAlpha(PersonaUi.Ink, 0.3f);
        dim.raycastTarget = false;
        PersonaBackground.Build(root, compact: true);

        PersonaUi.Heading(root, "Header", title, 72, new Vector2(0.14f, 0.87f), Vector2.zero,
            new Vector2(420f, 112f), PersonaUi.Heart);

        // Souls tag beside the heading.
        var tag = PersonaUi.Box(root, "Souls", new Vector2(0.37f, 0.87f), Vector2.zero, new Vector2(300f, 58f));
        tag.localRotation = Quaternion.Euler(0f, 0f, PersonaUi.Tilt * 0.5f);
        MenuParallax.Attach(tag, 0.6f);
        var tagShadow = PersonaUi.Poly(tag, "Shadow", PolyGraphic.Shape.Parallelogram, PersonaUi.Heart, 0.5f);
        ((RectTransform)tagShadow.transform).anchoredPosition = new Vector2(8f, -8f);
        PersonaUi.Poly(tag, "Slab", PolyGraphic.Shape.Parallelogram, PersonaUi.Ink, 0.5f);
        HudArt.Icon(tag, "Ember", HudArt.Ember(), new Vector2(0f, 0.5f), new Vector2(40f, 0f), 2.5f);
        s.Souls = PersonaUi.Label(tag, "Text", "", 28, PersonaUi.Bone, TextAnchor.MiddleRight, dropShadow: 0f);
        ((RectTransform)s.Souls.transform).offsetMax = new Vector2(-30f, 0f);

        // List card + tab strip riding the same motion rect.
        var listRoot = PersonaUi.Box(root, "ListRoot", new Vector2(0.29f, 0.43f), Vector2.zero, new Vector2(760f, 760f));
        MenuParallax.Attach(listRoot, 0.7f, clamp: true);
        s.listMotion = PersonaUi.Stretch(listRoot, "Motion");
        var strip = PersonaUi.Rect(s.listMotion, "Tabs", new Vector2(0f, 1f), Vector2.one, new Vector2(10f, -56f), new Vector2(-10f, 0f));
        var n = Mathf.Max(1, tabNames.Length);
        for (var i = 0; i < tabNames.Length; i++)
        {
            var idx = i;
            var rt = PersonaUi.Rect(strip, "Tab" + i, new Vector2((float)i / n, 0f), new Vector2((float)(i + 1) / n, 1f),
                new Vector2(6f, 0f), new Vector2(-6f, 0f));
            var shadow = PersonaUi.Poly(rt, "Shadow", PolyGraphic.Shape.Parallelogram, PersonaUi.Ink, 0.5f);
            ((RectTransform)shadow.transform).anchoredPosition = new Vector2(6f, -6f);
            var slab = PersonaUi.Poly(rt, "Slab", PolyGraphic.Shape.Parallelogram, PersonaUi.Night, 0.5f);
            slab.raycastTarget = true;
            var label = PersonaUi.Label(rt, "Text", tabNames[i], 22, PersonaUi.Bone, TextAnchor.MiddleCenter, dropShadow: 2f);
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = slab;
            btn.transition = Selectable.Transition.None;
            btn.navigation = new Navigation { mode = Navigation.Mode.None };
            btn.onClick.AddListener(() => onTab?.Invoke(idx));
            s.tabs.Add((rt, slab, label));
        }
        var listCard = PersonaUi.Card(s.listMotion, "Card", new Vector2(0.5f, 0f), new Vector2(0f, 340f), new Vector2(760f, 680f));
        s.List = PixelScrollList.Create(listCard, "List", Vector2.zero, Vector2.one, new Vector2(48f, 34f), new Vector2(-40f, -34f));

        // Detail card.
        var detailRoot = PersonaUi.Box(root, "DetailRoot", new Vector2(0.72f, 0.46f), Vector2.zero, new Vector2(640f, 780f));
        MenuParallax.Attach(detailRoot, 0.9f, clamp: true);
        s.detailMotion = PersonaUi.Stretch(detailRoot, "Motion");
        var card = PersonaUi.Card(s.detailMotion, "Card", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 780f), 2f);
        s.iconHost = PersonaUi.Box(card, "IconHost", new Vector2(0.5f, 1f), new Vector2(0f, -175f), new Vector2(230f, 230f));
        var dShadow = PersonaUi.Poly(s.iconHost, "Shadow", PolyGraphic.Shape.Diamond, PersonaUi.Heart);
        ((RectTransform)dShadow.transform).anchoredPosition = new Vector2(12f, -12f);
        PersonaUi.Poly(s.iconHost, "Ink", PolyGraphic.Shape.Diamond, PersonaUi.Ink);
        var inner = PersonaUi.Rect(s.iconHost, "Inner", Vector2.zero, Vector2.one, new Vector2(16f, 16f), new Vector2(-16f, -16f));
        PersonaUi.Poly(inner, "Well", PolyGraphic.Shape.Diamond, PersonaUi.Violet);
        s.icon = HudArt.Icon(s.iconHost, "Icon", HudArt.Gem(), new Vector2(0.5f, 0.5f), Vector2.zero, 8f);

        s.name = PersonaUi.Label(card, "Name", "", 40, PersonaUi.Bone, TextAnchor.MiddleCenter, dropShadow: 4f);
        var nrt = (RectTransform)s.name.transform;
        nrt.anchorMin = new Vector2(0f, 1f);
        nrt.anchorMax = Vector2.one;
        nrt.offsetMax = new Vector2(-30f, -318f);
        nrt.offsetMin = new Vector2(30f, -378f);
        s.metaSlab = PersonaUi.Box(card, "Meta", new Vector2(0.5f, 1f), new Vector2(0f, -412f), new Vector2(440f, 46f));
        PersonaUi.Poly(s.metaSlab, "Slab", PolyGraphic.Shape.Parallelogram, PersonaUi.Heart, 0.5f);
        s.meta = PersonaUi.Label(s.metaSlab, "Text", "", 22, PersonaUi.Bone, TextAnchor.MiddleCenter, dropShadow: 2f);
        s.desc = PersonaUi.Label(card, "Desc", "", 26, PersonaUi.Bone, TextAnchor.UpperCenter, dropShadow: 2f);
        var drt = (RectTransform)s.desc.transform;
        drt.offsetMin = new Vector2(56f, 60f);
        drt.offsetMax = new Vector2(-46f, -460f);
        s.desc.horizontalOverflow = HorizontalWrapMode.Wrap;
        s.desc.lineSpacing = 1.15f;

        PersonaUi.HintBar(root, hint);
        return s;
    }

    /// <summary>Slide both cards in again (call on open).</summary>
    public void PlayEntrance() => enterT = 0f;

    public void SetTab(int i)
    {
        if (i != tabIndex) tabPunch = 0.18f;
        tabIndex = i;
    }

    /// <summary>Describe a row; null sprite hides the icon well.</summary>
    public void ShowDetail(Sprite sp, string title, string metaText, string body)
    {
        iconHost.gameObject.SetActive(sp != null);
        if (sp != null)
        {
            icon.sprite = sp;
            icon.rectTransform.sizeDelta = new Vector2(sp.rect.width, sp.rect.height) * 8f;
        }
        name.text = title ?? "";
        meta.text = metaText ?? "";
        metaSlab.gameObject.SetActive(!string.IsNullOrEmpty(metaText));
        desc.text = body ?? "";
        punch = 0.22f;
    }

    public void Tick(float dt)
    {
        if (enterT < 1f)
        {
            enterT = Mathf.Min(1f, enterT + dt / 0.3f);
            var e = 1f - PersonaUi.EaseOutBack(enterT, 1.8f);
            listMotion.anchoredPosition = new Vector2(-300f * e, 0f);
            detailMotion.anchoredPosition = new Vector2(300f * e, 0f);
        }
        punch = Mathf.Lerp(punch, 0f, 1f - Mathf.Exp(-14f * dt));
        tabPunch = Mathf.Lerp(tabPunch, 0f, 1f - Mathf.Exp(-14f * dt));
        var t = Time.unscaledTime;
        iconHost.localScale = Vector3.one * (1f + punch);
        iconHost.anchoredPosition = new Vector2(0f, -175f + Mathf.Sin(t * 1.9f) * 5f);
        iconHost.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(t * 0.8f) * 2f);
        for (var i = 0; i < tabs.Count; i++)
        {
            var on = i == tabIndex;
            tabs[i].slab.color = on ? PersonaUi.Heart : PersonaUi.WithAlpha(PersonaUi.Night, 0.94f);
            tabs[i].label.color = on ? PersonaUi.Bone : PersonaUi.Copper;
            tabs[i].rt.localScale = Vector3.one * (on ? 1.06f + tabPunch : 1f);
        }
    }
}
