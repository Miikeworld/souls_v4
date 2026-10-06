using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Persona-style menu slab. Idle: a dark parallelogram with bone text and an
/// ink drop slab, gently floating. Selected (mouse hover = selection; keyboard
/// focus too): it punches up in scale, flashes white, SLAMS down while turning
/// heart-red, tilts a few degrees, and a star bursts off its tip for a split
/// second. Enabling it replays a staggered overshoot entrance.
/// </summary>
public sealed class PersonaButton : Button
{
    private RectTransform motion;
    private RectTransform shadowRt;
    private PolyGraphic slab, star;
    private Text label;
    private CanvasGroup group;
    private int index;

    private bool hot;
    private float hotT = 10f;   // seconds since becoming selected/deselected
    private float starT = 10f;
    private float enterT = -1f; // < 0 = entrance finished
    private float clickT = 10f;
    private float heat;         // 0 idle → 1 selected, eased

    /// <summary>Entrance playback rate — the pause menu runs it faster.</summary>
    public float EntranceSpeed { get; set; } = 1f;

    /// <summary>The animated content rect (slab, shadow, text all live under
    /// it). Composite widgets (creator option rows) attach their value text,
    /// arrows and swatches here so everything moves with the punch/float.</summary>
    public RectTransform Motion => motion;
    public Text LabelText => label;

    public void SetLabel(string text)
    {
        if (label != null) label.text = text;
    }

    private const float EnterDuration = 0.5f;
    private const float Stagger = 0.07f;
    private const float EnterDistance = 900f;

    public void Build(string text, int size, int idx, float skew)
    {
        index = idx;
        group = gameObject.AddComponent<CanvasGroup>();
        motion = PersonaUi.Stretch(transform, "Motion");
        shadowRt = (RectTransform)PersonaUi.Poly(motion, "Shadow", PolyGraphic.Shape.Parallelogram, PersonaUi.Ink, skew).transform;
        slab = PersonaUi.Poly(motion, "Slab", PolyGraphic.Shape.Parallelogram, PersonaUi.Night, skew);
        label = PersonaUi.Label(motion, "Text", text, size, PersonaUi.Bone, TextAnchor.MiddleLeft);
        var lrt = (RectTransform)label.transform;
        lrt.offsetMin = new Vector2(36f, 0f);
        star = PersonaUi.Poly(motion, "Star", PolyGraphic.Shape.Star, PersonaUi.Bone);
        var srt = (RectTransform)star.transform;
        srt.anchorMin = srt.anchorMax = new Vector2(1f, 0.5f);
        srt.sizeDelta = new Vector2(90f, 90f);
        srt.anchoredPosition = new Vector2(10f, 18f);
        star.StarPoints = 4;
        star.StarInner = 0.22f;
        star.enabled = false;
        enterT = 0f; // first show: the OnEnable before Build skipped this
        Apply(0f);
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        if (slab == null) return; // AddComponent enables before Build() runs
        enterT = 0f; // replay the entrance every time the panel shows
        hot = false;
        heat = 0f;
        hotT = 10f;
        Apply(0f);
    }

    public override void OnPointerEnter(PointerEventData eventData)
    {
        base.OnPointerEnter(eventData);
        // Hover IS selection — keeps a single hot slab between mouse and keys.
        if (IsInteractable() && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(gameObject);
    }

    public override void OnPointerClick(PointerEventData eventData)
    {
        clickT = 0f;
        base.OnPointerClick(eventData);
    }

    public override void OnSubmit(BaseEventData eventData)
    {
        clickT = 0f;
        base.OnSubmit(eventData);
    }

    protected override void DoStateTransition(SelectionState state, bool instant)
    {
        base.DoStateTransition(state, instant);
        if (slab == null) return;
        var nowHot = IsInteractable() &&
                     (state == SelectionState.Selected || state == SelectionState.Highlighted || state == SelectionState.Pressed);
        if (nowHot == hot) return;
        hot = nowHot;
        hotT = 0f;
        if (hot) starT = 0f;
    }

    private void Update()
    {
        if (slab == null) return;
        var dt = Time.unscaledDeltaTime;
        hotT += dt;
        starT += dt;
        clickT += dt;
        heat = Mathf.MoveTowards(heat, hot ? 1f : 0f, dt * (hot ? 14f : 8f));
        if (enterT >= 0f)
        {
            enterT += dt * EntranceSpeed;
            if (enterT > EnterDuration + index * Stagger) enterT = -1f;
        }
        Apply(dt);
    }

    private void Apply(float dt)
    {
        // Entrance: slide in from the left with overshoot, staggered per index.
        float enterX = 0f, alpha = 1f;
        if (enterT >= 0f)
        {
            var local = Mathf.Clamp01((enterT - index * Stagger) / EnterDuration);
            enterX = -EnterDistance * (1f - PersonaUi.EaseOutBack(local, 2.2f));
            alpha = Mathf.Clamp01(local * 3f);
        }
        group.alpha = interactable ? alpha : alpha * 0.45f;

        // Selection punch: scale up fast, then shoot down and settle.
        float scale = 1f, dropY = 0f, rot = 0f, flash = 0f;
        if (hot)
        {
            if (hotT < 0.07f)
            {
                var k = hotT / 0.07f;
                scale = Mathf.Lerp(1f, 1.2f, PersonaUi.EaseOutCubic(k));
                dropY = Mathf.Lerp(0f, 10f, k);
                flash = 1f;
            }
            else if (hotT < 0.16f)
            {
                var k = (hotT - 0.07f) / 0.09f;
                scale = Mathf.Lerp(1.2f, 1.07f, PersonaUi.EaseInCubic(k));
                dropY = Mathf.Lerp(10f, -16f, PersonaUi.EaseInCubic(k)); // the slam
                flash = 1f - k;
            }
            else
            {
                var k = Mathf.Clamp01((hotT - 0.16f) / 0.18f);
                scale = 1.07f;
                dropY = Mathf.Lerp(-16f, 0f, PersonaUi.EaseOutBack(k, 2.6f));
            }
            rot = -3.5f * PersonaUi.EaseOutBack(Mathf.Clamp01(hotT / 0.2f));
        }
        else
        {
            scale = Mathf.Lerp(1f, 1.07f, heat);
            rot = -3.5f * heat;
        }

        // Press squash.
        if (clickT < 0.12f) scale *= 1f - 0.08f * Mathf.Sin(clickT / 0.12f * Mathf.PI);

        // Idle float — everything breathes.
        var t = Time.unscaledTime + index * 0.9f;
        var bob = new Vector2(Mathf.Sin(t * 1.3f) * 3f, Mathf.Sin(t * 1.9f) * 4f) * (1f - heat * 0.6f);
        var sway = Mathf.Sin(t * 0.8f) * 0.6f;

        motion.anchoredPosition = new Vector2(enterX + heat * 26f, dropY) + bob;
        motion.localScale = new Vector3(scale, scale, 1f);
        motion.localRotation = Quaternion.Euler(0f, 0f, rot + sway);

        // Colours: idle night slab → white flash → heart red; text inverts.
        var target = Color.Lerp(PersonaUi.Night, PersonaUi.Heart, heat);
        slab.color = Color.Lerp(target, PersonaUi.Bone, flash);
        label.color = Color.Lerp(PersonaUi.Bone, PersonaUi.Ink, flash);
        var so = Mathf.Lerp(8f, 18f, heat);
        shadowRt.anchoredPosition = new Vector2(so, -so);

        // Star: a split-second 4-point glint off the slab's tip.
        const float starLife = 0.2f;
        star.enabled = starT < starLife;
        if (star.enabled)
        {
            var k = starT / starLife;
            var s = k < 0.35f ? Mathf.Lerp(0.2f, 1.35f, k / 0.35f) : Mathf.Lerp(1.35f, 0f, (k - 0.35f) / 0.65f);
            star.rectTransform.localScale = new Vector3(s, s, 1f);
            star.rectTransform.localRotation = Quaternion.Euler(0f, 0f, k * 120f);
            star.color = k < 0.3f ? PersonaUi.Bone : Color.Lerp(PersonaUi.Bone, PersonaUi.Heart, (k - 0.3f) / 0.7f);
        }
    }
}
