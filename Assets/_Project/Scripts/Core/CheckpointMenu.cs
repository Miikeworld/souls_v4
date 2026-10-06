using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// The menu while seated at a checkpoint, in the main menu's Persona style:
/// a compact tilted backdrop with rising embers, the checkpoint's name on a
/// heading slab, a cascading slab list — REST (repeat the refill/reset),
/// LEVEL UP (spend souls on vigor/endurance/mind through
/// <see cref="PlayerStats"/>), ARTS (attune the three quick-slots), TRAVEL
/// (warp between lit checkpoints) and LEAVE (stand up) — and a side card
/// for the sub-pages. LEVEL UP keeps a live preview block (level, souls,
/// cost, pool before » after) for whichever stat is highlighted. Its own
/// interactive <see cref="PixelCanvas"/>; keyboard/pad through the
/// EventSystem, mouse through the pixel raycaster. Esc/B backs out a level,
/// then leaves. Time keeps running — resting is not pausing.
/// </summary>
public sealed class CheckpointMenu : MonoBehaviour
{
    public static bool IsOpen { get; private set; }
    /// <summary>Frame the menu last closed on — PauseMenu ignores that frame's Esc.</summary>
    public static int ClosedFrame { get; private set; } = -1;

    private static CheckpointMenu instance;

    private enum View { Main, Arts, Travel, LevelUp }

    private PixelCanvas pixels;
    private RectTransform sideRoot, sideMotion, levelBlock;
    private PixelScrollList side;
    private Text header, sideTitle, blockLabels, blockValues;
    private readonly List<PersonaButton> mainRows = new();
    private readonly PixelRow[] statRows = new PixelRow[3];
    private Checkpoint current;
    private View view;
    private int pendingSlot = -1, shownStat = -2;
    private float sideT = 1f;
    private IAbilitySlots abilities;
    private PlayerStats stats;

    private static readonly string HeartHex = "#" + ColorUtility.ToHtmlStringRGB(PersonaUi.Heart);

    public static void Open(Checkpoint cp)
    {
        if (instance == null)
        {
            instance = new GameObject("CheckpointMenu").AddComponent<CheckpointMenu>();
            instance.Build();
        }
        instance.Show(cp);
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

        pixels = PixelCanvas.Create("CheckpointUi", 55, 1.25f, transform);
        var root = pixels.Root;

        var dim = PersonaUi.Stretch(root, "Dim").gameObject.AddComponent<Image>();
        dim.color = PersonaUi.WithAlpha(PersonaUi.Ink, 0.3f);
        dim.raycastTarget = false;
        PersonaBackground.Build(root, compact: true);

        header = PersonaUi.HeadingText(PersonaUi.Heading(root, "Header", "CHECKPOINT", 64,
            new Vector2(0.17f, 0.85f), Vector2.zero, new Vector2(620f, 116f), PersonaUi.Heart));

        // Main column — the main menu's tilted cascade of slabs.
        var list = PersonaUi.Box(root, "List", new Vector2(0.2f, 0.42f), Vector2.zero, new Vector2(700f, 540f));
        list.localRotation = Quaternion.Euler(0f, 0f, PersonaUi.Tilt);
        MenuParallax.Attach(list, 0.7f);
        var labels = new (string text, UnityEngine.Events.UnityAction act)[]
        {
            ("REST", OnRest),
            ("LEVEL UP", () => ShowSide(View.LevelUp)),
            ("ARTS", () => ShowSide(View.Arts)),
            ("TRAVEL", () => ShowSide(View.Travel)),
            ("LEAVE", () => GameLoop.Ensure().Leave()),
        };
        const float rowH = 86f, gap = 18f, cascade = 30f;
        for (var i = 0; i < labels.Length; i++)
        {
            var y = -i * (rowH + gap);
            var rowRt = PersonaUi.Rect(list, labels[i].text, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(i * cascade, y - rowH), new Vector2(i * cascade + 540f - i * 18f, y));
            var b = PersonaUi.Button(rowRt, "Btn", labels[i].text, 46, labels[i].act, i);
            b.EntranceSpeed = 1.6f;
            mainRows.Add(b);
        }

        // Side card (LEVEL UP / ARTS / TRAVEL).
        sideRoot = PersonaUi.Box(root, "Side", new Vector2(0.67f, 0.47f), Vector2.zero, new Vector2(800f, 780f));
        MenuParallax.Attach(sideRoot, 0.9f, clamp: true);
        sideMotion = PersonaUi.Stretch(sideRoot, "Motion");
        var card = PersonaUi.Card(sideMotion, "Card", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(800f, 780f));
        sideTitle = PersonaUi.HeadingText(PersonaUi.Heading(card, "Title", "", 34, new Vector2(0f, 1f),
            new Vector2(230f, 0f), new Vector2(440f, 72f), PersonaUi.Violet, -PersonaUi.Tilt));
        side = PixelScrollList.Create(card, "List", Vector2.zero, Vector2.one, new Vector2(48f, 40f), new Vector2(-40f, -78f));
        side.rowHeight = 62f;

        // LEVEL UP preview block under the stat rows.
        levelBlock = PersonaUi.Rect(card, "LevelBlock", Vector2.zero, new Vector2(1f, 0f), new Vector2(48f, 40f), new Vector2(-40f, 300f));
        var rule = PersonaUi.Rect(levelBlock, "Rule", new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -10f), Vector2.zero);
        PersonaUi.Poly(rule, "Slab", PolyGraphic.Shape.Parallelogram, PersonaUi.Heart, 1.2f);
        var cols = PersonaUi.Rect(levelBlock, "Cols", Vector2.zero, Vector2.one, new Vector2(20f, 0f), new Vector2(-30f, -30f));
        blockLabels = PersonaUi.Label(cols, "Labels", "", 28, PersonaUi.Copper, TextAnchor.UpperLeft, dropShadow: 3f);
        blockValues = PersonaUi.Label(cols, "Values", "", 28, PersonaUi.Bone, TextAnchor.UpperRight, dropShadow: 3f);
        blockLabels.lineSpacing = blockValues.lineSpacing = 1.25f;
        levelBlock.gameObject.SetActive(false);
        sideRoot.gameObject.SetActive(false);

        PersonaUi.HintBar(root, "W/S  SELECT      E/ENTER  CONFIRM      ESC  BACK");
        PersonaCursor.Build(pixels);
        SetVisible(false);
    }

    private void SetVisible(bool v)
    {
        pixels.Canvas.gameObject.SetActive(v);
        pixels.SetVisible(v);
    }

    // ---------- open / close ----------

    private void Show(Checkpoint cp)
    {
        current = cp;
        var loco = FindFirstObjectByType<PlayerLocomotion>();
        abilities = loco != null ? loco.GetComponent<IAbilitySlots>() : null;
        // Self-healing attach — no scene edit or tool run needed.
        stats = loco != null ? (loco.GetComponent<PlayerStats>() ?? loco.gameObject.AddComponent<PlayerStats>()) : null;
        header.text = cp != null ? cp.DisplayName.ToUpperInvariant() : "CHECKPOINT";
        IsOpen = true;
        SetVisible(true); // re-enabling replays every slab's entrance
        Cursor.lockState = CursorLockMode.None;
        view = View.Main;
        sideRoot.gameObject.SetActive(false);
        SetMainInteractable(true);
        StartCoroutine(SelectNextFrame(mainRows[0]));
    }

    private void Hide()
    {
        IsOpen = false;
        ClosedFrame = Time.frameCount;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        SetVisible(false); // disables PersonaCursor → it re-shows the OS cursor…
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false; // …so hide it again for gameplay
    }

    private IEnumerator SelectNextFrame(Selectable s)
    {
        yield return null;
        if (s == null || EventSystem.current == null) yield break;
        EventSystem.current.SetSelectedGameObject(null);
        EventSystem.current.SetSelectedGameObject(s.gameObject);
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
        IsOpen = false;
    }

    // ---------- actions ----------

    private void OnRest()
    {
        if (current != null) GameLoop.Ensure().RestTransaction(current);
    }

    private void SetMainInteractable(bool on)
    {
        foreach (var r in mainRows) r.interactable = on;
    }

    private void ShowSide(View v)
    {
        view = v;
        pendingSlot = -1;
        SetMainInteractable(false);
        sideRoot.gameObject.SetActive(true);
        sideT = 0f;
        RebuildSide();
    }

    private void BackToMain()
    {
        var focus = view == View.LevelUp ? mainRows[1] : view == View.Arts ? mainRows[2] : mainRows[3];
        view = View.Main;
        sideRoot.gameObject.SetActive(false);
        SetMainInteractable(true);
        StartCoroutine(SelectNextFrame(focus));
    }

    private void RebuildSide()
    {
        side.Clear();
        var leveling = view == View.LevelUp;
        levelBlock.gameObject.SetActive(leveling);
        side.viewport.offsetMin = new Vector2(48f, leveling ? 320f : 40f);
        shownStat = -2;

        if (view == View.Arts)
        {
            sideTitle.text = pendingSlot < 0 ? "CHOOSE A SLOT" : $"ATTUNE SLOT {pendingSlot + 1}";
            if (abilities == null)
            {
                side.AddRow("NO ARTS", null).interactable = false;
            }
            else
            {
                for (var s = 0; s < abilities.SlotCount; s++)
                {
                    var slot = s;
                    var name = abilities.SlotFullName(s);
                    var row = side.AddRow((pendingSlot == s ? "» " : "") + (string.IsNullOrEmpty(name) ? "EMPTY" : name.ToUpperInvariant()),
                        () => { pendingSlot = slot; RebuildSide(); }, 66f);
                    row.SetIcon(HudArt.SkillIcon(abilities.SlotIcon(s), name));
                    row.SetValue("SLOT " + (s + 1));
                }
                side.AddGap(22f);
                for (var i = 0; i < abilities.LibraryCount; i++)
                {
                    var idx = i;
                    var row = side.AddRow(abilities.LibraryName(i).ToUpperInvariant(),
                        () => { abilities.Attune(pendingSlot, idx); pendingSlot = -1; RebuildSide(); }, 56f, 24);
                    row.SetIcon(HudArt.SkillIcon(null, abilities.LibraryName(i)), 1.5f);
                    row.SetValue((int)abilities.LibraryCost(i) + " CE");
                    row.interactable = pendingSlot >= 0;
                }
            }
        }
        else if (leveling)
        {
            sideTitle.text = "LEVEL UP";
            if (stats == null)
            {
                side.AddRow("NO STATS", null).interactable = false;
            }
            else
            {
                for (var i = 0; i < 3; i++)
                {
                    var stat = (PlayerStats.Stat)i;
                    var row = side.AddRow(PlayerStats.Name(stat), () => Upgrade(stat), 70f, 32);
                    statRows[i] = row;
                }
                RefreshStatRows();
            }
        }
        else
        {
            sideTitle.text = "TRAVEL";
            var targets = Checkpoint.TravelTargets(current);
            if (targets.Count == 0) side.AddRow("NO OTHER CHECKPOINTS LIT", null).interactable = false;
            foreach (var t in targets)
            {
                var dest = t;
                side.AddRow(t.DisplayName.ToUpperInvariant(), () => GameLoop.Ensure().TravelTo(dest)).SetValue("WARP");
            }
        }

        side.FinishRebuild();
        // Focus: the first library entry once a slot is picked, else the first live row.
        var from = view == View.Arts && pendingSlot >= 0 && abilities != null ? abilities.SlotCount : 0;
        StartCoroutine(SelectNextFrame(side.FirstInteractable(from)));
    }

    // ---------- level up ----------

    private void Upgrade(PlayerStats.Stat stat)
    {
        if (stats.Capped(stat)) return;
        if (!stats.TryUpgrade(stat))
        {
            GameHud.Toast("NOT ENOUGH SOULS");
            return;
        }
        statRows[(int)stat].Pulse();
        RefreshStatRows();
    }

    /// <summary>In-place value refresh — a rebuild would replay entrances and drop focus.</summary>
    private void RefreshStatRows()
    {
        for (var i = 0; i < 3; i++)
        {
            var stat = (PlayerStats.Stat)i;
            var row = statRows[i];
            if (row == null) continue;
            var lv = stats.Level(stat);
            row.SetValue(stats.Capped(stat) ? $"{lv}   MAX" : lv.ToString());
            row.interactable = !stats.Capped(stat);
        }
        shownStat = -2;
    }

    private int SelectedStat()
    {
        var go = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        for (var i = 0; i < 3; i++) if (statRows[i] != null && statRows[i].gameObject == go) return i;
        return -1;
    }

    /// <summary>Level / souls / cost / pool preview for the highlighted stat;
    /// next values in heart red, a cost you can't pay in heart red too.</summary>
    private void RefreshLevelBlock(int sel)
    {
        shownStat = sel;
        var level = 0;
        for (var i = 0; i < 3; i++) level += stats.Level((PlayerStats.Stat)i);
        level -= 3 * CharacterCatalog.StatRules.Base - 1; // fresh 10/10/10 = level 1
        var held = SoulsWallet.Souls;
        string lvLine, costLine, poolName, poolLine;
        if (sel < 0)
        {
            lvLine = level.ToString();
            costLine = "-";
            poolName = "MAX HP";
            poolLine = ((int)stats.PoolAt(PlayerStats.Stat.Vigor)).ToString();
        }
        else
        {
            var stat = (PlayerStats.Stat)sel;
            var capped = stats.Capped(stat);
            var cost = stats.Cost(stat);
            lvLine = capped ? level.ToString() : $"{level}  <color={HeartHex}>» {level + 1}</color>";
            costLine = capped ? "MAX" : held >= cost ? cost.ToString() : $"<color={HeartHex}>{cost}</color>";
            poolName = stat == PlayerStats.Stat.Vigor ? "MAX HP" : stat == PlayerStats.Stat.Endurance ? "MAX STAMINA" : "MAX CORE ENERGY";
            poolLine = capped ? ((int)stats.PoolAt(stat)).ToString()
                : $"{(int)stats.PoolAt(stat)}  <color={HeartHex}>» {(int)stats.PoolNext(stat)}</color>";
        }
        blockLabels.text = $"LEVEL\nSOULS HELD\nCOST\n{poolName}";
        blockValues.text = $"{lvLine}\n{held}\n{costLine}\n{poolLine}";
    }

    // ---------- per-frame ----------

    private void Update()
    {
        if (!IsOpen) return;
        var dt = Time.unscaledDeltaTime;

        if (sideT < 1f && sideRoot.gameObject.activeSelf)
        {
            sideT = Mathf.Min(1f, sideT + dt / 0.28f);
            sideMotion.anchoredPosition = new Vector2(260f * (1f - PersonaUi.EaseOutBack(sideT, 1.8f)), 0f);
        }

        // Side-list scrolling: wheel + selection-follow inside the masked viewport.
        if (view != View.Main) side.Tick();
        if (view == View.LevelUp && stats != null)
        {
            var sel = SelectedStat();
            if (sel != shownStat) RefreshLevelBlock(sel);
        }

        var kb = Keyboard.current;
        var gp = Gamepad.current;
        var back = (kb != null && kb.escapeKey.wasPressedThisFrame) || (gp != null && gp.buttonEast.wasPressedThisFrame);
        if (back)
        {
            if (view == View.Arts && pendingSlot >= 0) { pendingSlot = -1; RebuildSide(); }
            else if (view != View.Main) BackToMain();
            else GameLoop.Ensure().Leave();
            return;
        }
        // E confirms too (the key that opened the menu) — the UI module only maps Enter/Space/South.
        if (kb != null && kb.eKey.wasPressedThisFrame) PersonaUi.SubmitSelected();
    }
}
