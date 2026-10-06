using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Factory helpers for the list-style menu widgets (creator rows, arrows).
/// Colours follow the Persona pixel palette sampled from the HUD frame (see
/// <see cref="PersonaUi"/>): "Gold" is the highlight role (brightened heart
/// red for contrast on violet), "Parchment" the body text (bone white).
/// Built on plain uGUI Image/Text so everything renders without extra assets.
/// </summary>
public static class SoulsUi
{
    public static readonly Color Bg        = PersonaUi.Night;
    public static readonly Color PanelBg   = PersonaUi.WithAlpha(PersonaUi.Violet, 0.96f);
    public static readonly Color RowBg     = PersonaUi.WithAlpha(PersonaUi.Night, 0.8f);
    public static readonly Color Gold      = new Color(1f, 0.30f, 0.24f);
    public static readonly Color Parchment = PersonaUi.Bone;
    public static readonly Color Dim       = new Color(0.80f, 0.64f, 0.57f);
    public static readonly Color Crimson   = PersonaUi.Heart;

    private static Font font;
    public static Font Font
    {
        get
        {
            if (font == null)
            {
                // Drop any .ttf at Assets/_Project/Resources/Fonts/UI.ttf to
                // re-skin every menu/HUD label; falls back to the built-in.
                font = Resources.Load<Font>("Fonts/UI");
                if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return font;
        }
    }

    public static RectTransform Rect(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer; // pixel canvas renders the UI layer only
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        return rt;
    }

    public static RectTransform Stretch(Transform parent, string name)
    {
        return Rect(parent, name, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
    }

    public static Image Panel(Transform parent, string name, Color color)
    {
        var rt = Stretch(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = true;
        return img;
    }

    public static Text Label(Transform parent, string name, string content,
        int size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft,
        FontStyle style = FontStyle.Normal)
    {
        var rt = Stretch(parent, name);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = Font;
        t.text = content;
        t.fontSize = size;
        t.color = color;
        t.alignment = anchor;
        t.fontStyle = style;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    /// <summary>Borderless Souls-style menu entry — parchment text that turns
    /// gold under focus/selection.</summary>
    public static Button MenuButton(Transform parent, string name, string label,
        int size, UnityAction onClick, TextAnchor anchor = TextAnchor.MiddleLeft)
    {
        var rt = Stretch(parent, name);
        var bg = rt.gameObject.AddComponent<Image>();
        bg.color = new Color(1f, 1f, 1f, 0f); // invisible hit area
        var btn = rt.gameObject.AddComponent<Button>();

        var text = Label(rt, "Label", label, size, Parchment, anchor);
        var colors = btn.colors;
        colors.normalColor = Parchment;
        colors.highlightedColor = Gold;
        colors.selectedColor = Gold;
        colors.pressedColor = new Color(0.95f, 0.85f, 0.55f);
        colors.disabledColor = new Color(0.30f, 0.29f, 0.26f);
        colors.fadeDuration = 0.08f;
        btn.colors = colors;
        btn.targetGraphic = text; // tint the letters, not the bg
        btn.onClick.AddListener(onClick);
        return btn;
    }

    /// <summary>Small ‹/› arrow button for cycling rows.</summary>
    public static Button ArrowButton(Transform parent, string name, string glyph,
        UnityAction onClick)
    {
        var rt = Stretch(parent, name);
        var bg = rt.gameObject.AddComponent<Image>();
        bg.color = new Color(1f, 1f, 1f, 0f);
        var btn = rt.gameObject.AddComponent<Button>();
        var text = Label(rt, "Glyph", glyph, 30, Dim, TextAnchor.MiddleCenter, FontStyle.Bold);
        var colors = btn.colors;
        colors.normalColor = Dim;
        colors.highlightedColor = Gold;
        colors.selectedColor = Gold;
        colors.pressedColor = Color.white;
        colors.fadeDuration = 0.06f;
        btn.colors = colors;
        btn.targetGraphic = text;
        btn.onClick.AddListener(onClick);
        // Arrows are click targets only — keep them out of the nav chain so
        // up/down stops on rows, not on glyphs.
        btn.navigation = new Navigation { mode = Navigation.Mode.None };
        return btn;
    }

    /// <summary>A cycling option row: label on the left, ‹ value › on the
    /// right. Returns the value Text so callers can set it.</summary>
    public static Text OptionRow(RectTransform rowRt, string label, UnityAction onPrev, UnityAction onNext)
    {
        var bg = rowRt.gameObject.AddComponent<Image>();
        bg.color = RowBg;

        var labelRt = Rect(rowRt, "Label", new Vector2(0f, 0f), new Vector2(0.42f, 1f),
            new Vector2(18f, 0f), Vector2.zero);
        Label(labelRt, "Text", label.ToUpperInvariant(), 26, Dim, TextAnchor.MiddleLeft, FontStyle.Bold);

        var prevRt = Rect(rowRt, "Prev", new Vector2(0.42f, 0f), new Vector2(0.50f, 1f), Vector2.zero, Vector2.zero);
        ArrowButton(prevRt, "Btn", "◄", onPrev);

        var valRt = Rect(rowRt, "Value", new Vector2(0.50f, 0f), new Vector2(0.92f, 1f), Vector2.zero, Vector2.zero);
        var value = Label(valRt, "Text", "", 26, Parchment, TextAnchor.MiddleCenter, FontStyle.Bold);

        var nextRt = Rect(rowRt, "Next", new Vector2(0.92f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
        ArrowButton(nextRt, "Btn", "►", onNext);

        return value;
    }

    /// <summary>A thin horizontal divider line.</summary>
    public static void Divider(Transform parent, string name)
    {
        var rt = Stretch(parent, name);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(Gold.r, Gold.g, Gold.b, 0.25f);
        img.raycastTarget = false;
    }
}
