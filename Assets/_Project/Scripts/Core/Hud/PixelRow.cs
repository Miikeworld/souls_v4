using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// A selectable list row in the main menu's Persona style (checkpoint,
/// inventory and shop lists): an ink drop slab under a night parallelogram.
/// Hover or keyboard/pad selection flashes it white, slams it heart-red,
/// slides it right with a slight tilt and glints a star off its tip — the
/// <see cref="PersonaButton"/> language, sized for dense lists. Optional
/// left icon and right-aligned value text. Rows stagger in with overshoot.
/// Submit/click fires <see cref="onClick"/>.
/// </summary>
public sealed class PixelRow : Selectable, ISubmitHandler, IPointerClickHandler
{
    public Action onClick;

    private RectTransform motion, shadowRt;
    private CanvasGroup group;
    private PolyGraphic slab, star;
    private Image icon;
    private Text label, value;
    private float sel, enterT = 1f, enterDelay, hotT = 10f;
    private bool wasSelected;

    public static PixelRow Create(Transform parent, string name, string text, Action click,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, int fontSize = 30)
    {
        var rt = PersonaUi.Rect(parent, name, anchorMin, anchorMax, offsetMin, offsetMax);
        var hit = rt.gameObject.AddComponent<Image>();
        hit.color = new Color(1f, 1f, 1f, 0f); // invisible hit area — the slab draws the look
        var row = rt.gameObject.AddComponent<PixelRow>();
        row.targetGraphic = hit;
        row.transition = Transition.None;
        row.onClick = click;
        row.motion = PersonaUi.Stretch(rt, "Motion");
        row.group = row.motion.gameObject.AddComponent<CanvasGroup>();
        row.group.blocksRaycasts = false;
        row.shadowRt = (RectTransform)PersonaUi.Poly(row.motion, "Shadow", PolyGraphic.Shape.Parallelogram, PersonaUi.Ink, 0.3f).transform;
        row.slab = PersonaUi.Poly(row.motion, "Slab", PolyGraphic.Shape.Parallelogram, PersonaUi.Night, 0.3f);
        row.label = PersonaUi.Label(row.motion, "Text", text, fontSize, PersonaUi.Bone, TextAnchor.MiddleLeft, dropShadow: 3f);
        ((RectTransform)row.label.transform).offsetMin = new Vector2(30f, 0f);
        row.star = PersonaUi.Poly(row.motion, "Star", PolyGraphic.Shape.Star, PersonaUi.Bone);
        var srt = row.star.rectTransform;
        srt.anchorMin = srt.anchorMax = new Vector2(1f, 0.5f);
        srt.sizeDelta = new Vector2(56f, 56f);
        srt.anchoredPosition = new Vector2(6f, 12f);
        row.star.StarPoints = 4;
        row.star.StarInner = 0.22f;
        row.star.enabled = false;
        return row;
    }

    public void SetLabel(string text) { if (label != null) label.text = text; }

    /// <summary>Right-aligned value column (counts, costs, "12 › 13").</summary>
    public void SetValue(string text)
    {
        if (value == null)
        {
            value = PersonaUi.Label(motion, "Value", "", label.fontSize, PersonaUi.Bone, TextAnchor.MiddleRight, dropShadow: 3f);
            ((RectTransform)value.transform).offsetMax = new Vector2(-30f, 0f);
        }
        value.text = text;
    }

    /// <summary>Left icon (HudArt sprite at integer texel scale); the label shifts past it.</summary>
    public void SetIcon(Sprite sp, float scale = 2f)
    {
        if (sp == null) return;
        if (icon == null)
        {
            icon = HudArt.Icon(motion, "Icon", sp, new Vector2(0f, 0.5f), Vector2.zero, scale);
            icon.transform.SetSiblingIndex(label.transform.GetSiblingIndex());
        }
        icon.sprite = sp;
        var size = new Vector2(sp.rect.width, sp.rect.height) * scale;
        icon.rectTransform.sizeDelta = size;
        icon.rectTransform.anchoredPosition = new Vector2(24f + size.x * 0.5f, 0f);
        ((RectTransform)label.transform).offsetMin = new Vector2(36f + size.x, 0f);
    }

    /// <summary>Replay the selection flash/slam — confirmation feedback.</summary>
    public void Pulse() => hotT = 0f;

    /// <summary>Restart the staggered entrance.</summary>
    public void PlayEntrance(float delay)
    {
        enterDelay = delay;
        enterT = 0f;
        if (group != null) group.alpha = 0f;
    }

    public override void OnPointerEnter(PointerEventData e)
    {
        base.OnPointerEnter(e);
        if (IsInteractable()) Select();
    }

    public void OnSubmit(BaseEventData e) { if (IsInteractable()) onClick?.Invoke(); }
    public void OnPointerClick(PointerEventData e) { if (IsInteractable() && e.button == PointerEventData.InputButton.Left) onClick?.Invoke(); }

    private void Update()
    {
        if (motion == null) return;
        var dt = Time.unscaledDeltaTime;
        var live = IsInteractable();
        var selected = live && EventSystem.current != null && EventSystem.current.currentSelectedGameObject == gameObject;
        if (selected != wasSelected)
        {
            wasSelected = selected;
            if (selected) hotT = 0f;
        }
        hotT += dt;
        sel = Mathf.MoveTowards(sel, selected ? 1f : 0f, dt * (selected ? 12f : 7f));

        var enterX = 0f;
        if (enterT < 1f)
        {
            if (enterDelay > 0f) enterDelay -= dt;
            else enterT = Mathf.Min(1f, enterT + dt / 0.26f);
            enterX = -90f * (1f - PersonaUi.EaseOutBack(enterT, 2.2f));
            group.alpha = Mathf.Clamp01(enterT * 2.5f);
        }
        else group.alpha = live ? 1f : 0.45f;

        // Selection punch: white flash + lift, then the slam down.
        float flash = 0f, dropY = 0f, scale = 1f + 0.04f * sel;
        if (selected && hotT < 0.07f) { flash = 1f; dropY = 6f * hotT / 0.07f; scale += 0.06f; }
        else if (selected && hotT < 0.16f)
        {
            var k = (hotT - 0.07f) / 0.09f;
            flash = 1f - k;
            dropY = Mathf.Lerp(6f, -6f, PersonaUi.EaseInCubic(k));
        }
        else if (selected && hotT < 0.32f) dropY = Mathf.Lerp(-6f, 0f, PersonaUi.EaseOutBack((hotT - 0.16f) / 0.16f, 2.6f));

        motion.anchoredPosition = new Vector2(18f * sel + enterX, dropY);
        motion.localScale = new Vector3(scale, scale, 1f);
        motion.localRotation = Quaternion.Euler(0f, 0f, -1.5f * sel);
        var so = Mathf.Lerp(6f, 12f, sel);
        shadowRt.anchoredPosition = new Vector2(so, -so);

        slab.color = Color.Lerp(Color.Lerp(PersonaUi.WithAlpha(PersonaUi.Night, 0.94f), PersonaUi.Heart, sel), PersonaUi.Bone, flash);
        var text = live ? PersonaUi.Bone : PersonaUi.Copper;
        label.color = Color.Lerp(text, PersonaUi.Ink, flash);
        if (value != null) value.color = label.color;

        const float starLife = 0.2f;
        star.enabled = selected && hotT < starLife;
        if (star.enabled)
        {
            var k = hotT / starLife;
            var s = k < 0.35f ? Mathf.Lerp(0.2f, 1.3f, k / 0.35f) : Mathf.Lerp(1.3f, 0f, (k - 0.35f) / 0.65f);
            star.rectTransform.localScale = new Vector3(s, s, 1f);
            star.rectTransform.localRotation = Quaternion.Euler(0f, 0f, k * 120f);
        }
    }
}
