using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// Persona-style pixel UI kit. The palette is sampled straight from the
/// hand-drawn HUD frame (IMG_0040): ink outline, night/violet/plum shadow
/// tones, rust/blood/copper trim, the heart's red, and the slots' bone white.
/// Everything leans on <see cref="Tilt"/>-degree rotations and parallelogram
/// slabs.
/// </summary>
public static class PersonaUi
{
    public static readonly Color Ink    = new Color(0f, 0f, 0f);
    public static readonly Color Night  = new Color(0.106f, 0.071f, 0.114f);   // #1b121d
    public static readonly Color Violet = new Color(0.220f, 0.153f, 0.278f);   // #382747
    public static readonly Color Plum   = new Color(0.278f, 0.180f, 0.212f);   // #472e36
    public static readonly Color Rust   = new Color(0.353f, 0.204f, 0.153f);   // #5a3427
    public static readonly Color Blood  = new Color(0.235f, 0.125f, 0.122f);   // #3c201f
    public static readonly Color Copper = new Color(0.498f, 0.345f, 0.298f);   // #7f584c
    public static readonly Color Heart  = new Color(0.796f, 0.055f, 0.043f);   // #cb0e0b
    public static readonly Color Bone   = new Color(0.965f, 0.945f, 0.905f);   // the slot white

    public const float Tilt = 8f;

    public static Color WithAlpha(Color c, float a) { c.a = a; return c; }

    public static RectTransform Rect(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        return rt;
    }

    public static RectTransform Stretch(Transform parent, string name) =>
        Rect(parent, name, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

    /// <summary>A fixed-size rect centred on an anchor point.</summary>
    public static RectTransform Box(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        var rt = Rect(parent, name, anchor, anchor, Vector2.zero, Vector2.zero);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
        return rt;
    }

    public static PolyGraphic Poly(Transform parent, string name, PolyGraphic.Shape shape, Color color, float skew = 0.35f)
    {
        var rt = Stretch(parent, name);
        var g = rt.gameObject.AddComponent<PolyGraphic>();
        g.ShapeType = shape;
        g.Skew = skew;
        g.color = color;
        g.raycastTarget = false;
        return g;
    }

    /// <summary>Bold label with a hard ink drop — the pixel "slab text".</summary>
    public static Text Label(Transform parent, string name, string content, int size, Color color,
        TextAnchor anchor = TextAnchor.MiddleLeft, bool bold = true, float dropShadow = 4f)
    {
        var rt = Stretch(parent, name);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = SoulsUi.Font;
        t.text = content;
        t.fontSize = size;
        t.color = color;
        t.alignment = anchor;
        t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        if (dropShadow > 0f)
        {
            var sh = rt.gameObject.AddComponent<Shadow>();
            sh.effectColor = Ink;
            sh.effectDistance = new Vector2(dropShadow, -dropShadow);
        }
        return t;
    }

    /// <summary>Tilted slab heading: ink shadow slab, coloured slab, bone text.</summary>
    public static RectTransform Heading(Transform parent, string name, string text, int size,
        Vector2 anchor, Vector2 pos, Vector2 slabSize, Color slab, float rotation = Tilt)
    {
        var rt = Box(parent, name, anchor, pos, slabSize);
        rt.localRotation = Quaternion.Euler(0f, 0f, rotation);
        var motion = Stretch(rt, "Motion");
        var shadow = Poly(motion, "Shadow", PolyGraphic.Shape.Parallelogram, Ink, 0.5f);
        ((RectTransform)shadow.transform).anchoredPosition = new Vector2(14f, -14f);
        Poly(motion, "Slab", PolyGraphic.Shape.Parallelogram, slab, 0.5f);
        Label(motion, "Text", text, size, Bone, TextAnchor.MiddleCenter);
        motion.gameObject.AddComponent<UiFloat>().Configure(3f, 4f, 0.8f, Random.value * 6f);
        // Headings float in front of their panel — the root is never animated
        // (UiFloat drives Motion), so it's a safe parallax layer.
        MenuParallax.Attach(rt, 0.5f);
        return rt;
    }

    /// <summary>Panel card (the controls/creator panel look): ink drop slab,
    /// near-opaque night body, heart edge down the left side.</summary>
    public static RectTransform Card(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size,
        float rotation = 0f, float skew = 0.08f)
    {
        var box = Box(parent, name, anchor, pos, size);
        box.localRotation = Quaternion.Euler(0f, 0f, rotation);
        var shadow = Poly(box, "Shadow", PolyGraphic.Shape.Parallelogram, Ink, skew);
        ((RectTransform)shadow.transform).anchoredPosition = new Vector2(20f, -20f);
        Poly(box, "Slab", PolyGraphic.Shape.Parallelogram, WithAlpha(Night, 0.96f), skew);
        var edge = Rect(box, "Edge", Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(18f, 0f));
        Poly(edge, "Slab", PolyGraphic.Shape.Parallelogram, Heart, skew);
        return box;
    }

    /// <summary>Screen-wide ink hint strip along the bottom edge (main menu, game menus).</summary>
    public static Text HintBar(Transform parent, string text)
    {
        var bar = Box(parent, "Hints", new Vector2(0.5f, 0.035f), Vector2.zero, new Vector2(2200f, 58f));
        bar.localRotation = Quaternion.Euler(0f, 0f, 1.5f);
        Poly(bar, "Slab", PolyGraphic.Shape.Parallelogram, Ink, 0.6f);
        var lrt = Rect(bar, "Text", new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(-520f, 0f), new Vector2(520f, 0f));
        return Label(lrt, "Label", text, 26, Bone, TextAnchor.MiddleCenter, true, 0f);
    }

    /// <summary>The Text inside a <see cref="Heading"/> — for headings whose words change.</summary>
    public static Text HeadingText(RectTransform heading) => heading.Find("Motion/Text").GetComponent<Text>();

    /// <summary>Fire Submit on whatever the EventSystem has selected — the E-key
    /// confirm the game menus add on top of Enter/Space/South.</summary>
    public static void SubmitSelected()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        var go = es != null ? es.currentSelectedGameObject : null;
        if (go != null)
            UnityEngine.EventSystems.ExecuteEvents.Execute(go, new UnityEngine.EventSystems.BaseEventData(es),
                UnityEngine.EventSystems.ExecuteEvents.submitHandler);
    }

    public static PersonaButton Button(Transform parent, string name, string label, int size,
        UnityAction onClick, int index, float slabSkew = 0.45f)
    {
        var rt = Stretch(parent, name);
        var hit = rt.gameObject.AddComponent<Image>();
        hit.color = new Color(1f, 1f, 1f, 0f); // invisible hit area — the slab draws the look
        var btn = rt.gameObject.AddComponent<PersonaButton>();
        btn.Build(label, size, index, slabSkew);
        btn.targetGraphic = hit;
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(onClick);
        return btn;
    }

    // ---------- easing ----------

    public static float EaseOutBack(float t, float overshoot = 1.9f)
    {
        t = Mathf.Clamp01(t) - 1f;
        return 1f + t * t * ((overshoot + 1f) * t + overshoot);
    }

    public static float EaseOutCubic(float t)
    {
        t = 1f - Mathf.Clamp01(t);
        return 1f - t * t * t;
    }

    public static float EaseInCubic(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * t;
    }
}
