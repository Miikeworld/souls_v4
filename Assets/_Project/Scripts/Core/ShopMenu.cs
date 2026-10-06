using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// The merchant screen — BUY (his stock at ItemDef.price) / SELL (your
/// consumables at half price). Arrows or shoulder buttons switch tabs,
/// E/Enter trades, Esc/Tab backs out. Same shell as InventoryMenu: scroll
/// list left, detail pane right, real-time — the dungeon does not wait.
/// </summary>
public sealed class ShopMenu : MonoBehaviour
{
    public static bool IsOpen { get; private set; }
    /// <summary>Frame the menu last closed on — PauseMenu ignores that frame's Esc.</summary>
    public static int ClosedFrame { get; private set; } = -1;

    private static ShopMenu instance;

    private enum Tab { Buy, Sell }
    private static readonly string[] TabNames = { "BUY", "SELL" };
    private const float SellRate = 0.5f;

    private PixelCanvas pixels;
    private ItemShell shell;
    private PixelScrollList list;

    private NpcMerchant merchant;
    private Inventory inv;
    private Tab tab;
    private int lastSel = -1;
    private int openedFrame = -1; // the E that opened us must not confirm a row

    private struct RowData { public ItemDef item; public int price; }
    private readonly List<RowData> rowData = new();

    public static void Open(NpcMerchant merchant)
    {
        if (instance == null)
        {
            instance = new GameObject("ShopMenu").AddComponent<ShopMenu>();
            instance.Build();
        }
        instance.merchant = merchant;
        instance.Show();
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

        pixels = PixelCanvas.Create("ShopUi", 57, 1.25f, transform);
        shell = ItemShell.Build(pixels.Root, "SHOP", TabNames,
            "A/D  TAB      W/S  SELECT      E  TRADE      ESC  LEAVE", i => { tab = (Tab)i; Rebuild(0); });
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
        inv = loco != null ? loco.GetComponent<Inventory>() : null;
        IsOpen = true;
        tab = Tab.Buy;
        openedFrame = Time.frameCount;
        SetVisible(true);
        Cursor.lockState = CursorLockMode.None;
        shell.PlayEntrance();
        if (merchant != null) GameHud.Toast(merchant.Bark());
        Rebuild(0);
    }

    private void Hide()
    {
        IsOpen = false;
        ClosedFrame = Time.frameCount;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        SetVisible(false);
        if (!PauseMenu.IsPaused)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        var socket = inv != null ? inv.GetComponent<WeaponSocket>() : null;
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

        if (tab == Tab.Buy) BuildBuy();
        else BuildSell();

        list.FinishRebuild();
        StartCoroutine(SelectNextFrame(list.FirstInteractable(Mathf.Clamp(keepSel, 0, list.Count))));
        lastSel = -2;
    }

    private void BuildBuy()
    {
        var any = false;
        var stock = merchant != null ? merchant.stock : null;
        if (stock != null)
            foreach (var item in stock)
            {
                if (item == null) continue;
                any = true;
                var buy = item;
                var short_ = SoulsWallet.Souls < item.price;
                var row = list.AddRow(item.itemName.ToUpperInvariant(), () => Buy(buy));
                row.SetIcon(HudArt.ItemIcon(item.icon, item.itemName));
                row.SetValue(item.price.ToString());
                row.interactable = !short_;
                rowData.Add(new RowData { item = item, price = item.price });
            }
        if (!any) list.AddRow("SOLD OUT", null).interactable = false;
    }

    private void BuildSell()
    {
        var any = false;
        if (inv != null)
            for (var i = 0; i < inv.EntryCount; i++)
            {
                var e = inv.Get(i);
                // Consumables only — key items can't be fenced.
                if (e.item == null || e.item.kind != ItemDef.Kind.Consumable || e.count <= 0) continue;
                any = true;
                var item = e.item;
                var sell = Mathf.Max(1, Mathf.RoundToInt(item.price * SellRate));
                var row = list.AddRow($"{item.itemName.ToUpperInvariant()}  x{e.count}", () => Sell(item, sell));
                row.SetIcon(HudArt.ItemIcon(item.icon, item.itemName));
                row.SetValue(sell.ToString());
                rowData.Add(new RowData { item = item, price = sell });
            }
        if (!any) list.AddRow("NOTHING TO SELL", null).interactable = false;
    }

    private void Buy(ItemDef item)
    {
        if (inv == null || item == null) return;
        if (SoulsWallet.Souls < item.price) { GameHud.Toast("NOT ENOUGH SOULS"); return; }
        SoulsWallet.Add(-item.price);
        inv.Add(item);
        Rebuild(SelectedIndex());
    }

    private void Sell(ItemDef item, int price)
    {
        if (inv == null || item == null) return;
        if (!inv.TryConsume(item)) return;
        SoulsWallet.Add(price);
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
        var metaText = tab == Tab.Buy
            ? d.price + " SOULS" + (SoulsWallet.Souls >= d.price ? "" : "   ·   SHORT " + (d.price - SoulsWallet.Souls))
            : "SELLS FOR " + d.price + " SOULS";
        shell.ShowDetail(HudArt.ItemIcon(d.item.icon, d.item.itemName), d.item.itemName.ToUpperInvariant(),
            metaText, d.item.description);
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

        if (Time.frameCount == openedFrame) return; // the opening E press is still "pressed this frame"

        var kb = Keyboard.current;
        var gp = Gamepad.current;

        var prev = (kb != null && (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame))
            || (gp != null && gp.leftShoulder.wasPressedThisFrame);
        var next = (kb != null && (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame))
            || (gp != null && gp.rightShoulder.wasPressedThisFrame);
        if (prev || next) { tab = tab == Tab.Buy ? Tab.Sell : Tab.Buy; Rebuild(0); return; }

        var back = (kb != null && (kb.escapeKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame))
            || (gp != null && gp.buttonEast.wasPressedThisFrame);
        if (back) { Hide(); return; }

        list.Tick();
        UpdateDetail();

        if (kb != null && kb.eKey.wasPressedThisFrame) PersonaUi.SubmitSelected();
    }
}
