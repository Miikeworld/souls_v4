using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// A masked, wheel/keyboard-scrollable column of <see cref="PixelRow"/>s —
/// the side-list mechanics extracted from CheckpointMenu so inventory and
/// shop menus scroll the same way. Rows stack downward from the content
/// top; the wheel steps <see cref="wheelStep"/> per notch and a keyboard/pad
/// selection outside the viewport clamps it back into view.
/// </summary>
public sealed class PixelScrollList : MonoBehaviour
{
    public RectTransform viewport;   // masked clip rect
    public RectTransform content;    // rows parent here; shifts on scroll
    public float rowHeight = 64f;
    public float rowGap = 8f;
    public int fontSize = 28;
    public float wheelStep = 58f;

    private readonly List<PixelRow> rows = new();
    private float contentH, scrollY, top;

    public int Count => rows.Count;
    public PixelRow this[int i] => rows[i];
    public bool Overflowing => viewport != null && contentH > viewport.rect.height;

    /// <summary>Builds the RectMask2D viewport + content child under `parent`.</summary>
    public static PixelScrollList Create(RectTransform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        var vp = PersonaUi.Rect(parent, name, anchorMin, anchorMax, offsetMin, offsetMax);
        vp.gameObject.AddComponent<RectMask2D>();
        var list = vp.gameObject.AddComponent<PixelScrollList>();
        list.viewport = vp;
        list.content = PersonaUi.Rect(vp, "Content", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        return list;
    }

    public void Clear()
    {
        foreach (var r in rows) if (r != null) Destroy(r.gameObject);
        rows.Clear();
        contentH = scrollY = top = 0f;
        if (content != null) content.anchoredPosition = Vector2.zero;
    }

    /// <summary>Vertical separator between row groups (slot list vs library).</summary>
    public void AddGap(float px) { top -= px; contentH = -top; }

    public PixelRow AddRow(string text, System.Action act, float h = 0f, int font = 0)
    {
        var hh = h > 0f ? h : rowHeight;
        var row = PixelRow.Create(content, "Row" + rows.Count, text, act,
            new Vector2(0f, 1f), new Vector2(1f, 1f),
            new Vector2(0f, top - hh), new Vector2(0f, top), font > 0 ? font : fontSize);
        top -= hh + rowGap;
        contentH = -top;
        rows.Add(row);
        return row;
    }

    /// <summary>Staggered entrances + scroll reset — call once a rebuild ends.</summary>
    public void FinishRebuild(float stagger = 0.03f)
    {
        scrollY = 0f;
        if (content != null) content.anchoredPosition = Vector2.zero;
        for (var i = 0; i < rows.Count; i++) rows[i].PlayEntrance(i * stagger);
    }

    /// <summary>First interactable row at/after `from`, else the first anywhere.</summary>
    public PixelRow FirstInteractable(int from = 0)
    {
        for (var i = from; i < rows.Count; i++) if (rows[i].interactable) return rows[i];
        for (var i = 0; i < rows.Count && i < from; i++) if (rows[i].interactable) return rows[i];
        return null;
    }

    /// <summary>Call each frame while open — wheel ticks one step per notch
    /// (sign only; delta magnitude is platform-dependent), then the selected
    /// row is kept inside the masked viewport.</summary>
    public void Tick()
    {
        if (content == null || contentH <= 0f) return;
        var viewH = viewport.rect.height;
        var max = Mathf.Max(0f, contentH - viewH);
        var w = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
        if (w > 0.01f) scrollY -= wheelStep;
        else if (w < -0.01f) scrollY += wheelStep;
        var sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        var selRow = sel != null ? sel.GetComponent<PixelRow>() : null;
        if (selRow != null && selRow.transform.parent == content)
        {
            var rt = (RectTransform)selRow.transform;
            var rowH = rt.offsetMax.y - rt.offsetMin.y;
            var bandTop = -rt.offsetMax.y; // row top's distance below the content top
            scrollY = Mathf.Clamp(scrollY, bandTop + rowH - viewH, bandTop);
        }
        content.anchoredPosition = new Vector2(0f, Mathf.Clamp(scrollY, 0f, max));
    }
}
