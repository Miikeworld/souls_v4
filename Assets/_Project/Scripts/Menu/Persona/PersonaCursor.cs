using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Persona-style pointer drawn inside the pixel canvas (so it's pixelated like
/// everything else): a heart-red arrowhead with a bone core, an ink drop and a
/// trailing diamond. It wobbles while idle and pops on click. The OS cursor is
/// hidden while this is active.
/// </summary>
public sealed class PersonaCursor : MonoBehaviour
{
    private PixelCanvas pixelCanvas;
    private RectTransform rt, body, tail;
    private float clickT = 10f;

    public static PersonaCursor Build(PixelCanvas pc)
    {
        var rt = PersonaUi.Box(pc.Root, "PersonaCursor", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(64f, 64f));
        rt.pivot = new Vector2(0.5f, 1f); // the tip is the hotspot
        var c = rt.gameObject.AddComponent<PersonaCursor>();
        c.pixelCanvas = pc;
        c.rt = rt;
        c.body = PersonaUi.Stretch(rt, "Body");
        c.body.pivot = new Vector2(0.5f, 1f);

        var shadow = PersonaUi.Poly(c.body, "Shadow", PolyGraphic.Shape.Triangle, PersonaUi.Ink);
        ((RectTransform)shadow.transform).anchoredPosition = new Vector2(7f, -7f);
        PersonaUi.Poly(c.body, "Fill", PolyGraphic.Shape.Triangle, PersonaUi.Heart);
        var core = PersonaUi.Poly(c.body, "Core", PolyGraphic.Shape.Triangle, PersonaUi.Bone);
        var crt = (RectTransform)core.transform;
        crt.anchorMin = new Vector2(0.3f, 0.35f);
        crt.anchorMax = new Vector2(0.7f, 0.85f);

        c.tail = PersonaUi.Box(c.body, "Tail", new Vector2(0.5f, 0f), new Vector2(0f, -10f), new Vector2(20f, 26f));
        var tg = c.tail.gameObject.AddComponent<PolyGraphic>();
        tg.ShapeType = PolyGraphic.Shape.Diamond;
        tg.color = PersonaUi.Bone;
        tg.raycastTarget = false;
        return c;
    }

    private void OnEnable() => Cursor.visible = false;
    private void OnDisable() => Cursor.visible = true;
    private void OnDestroy() => Cursor.visible = true;

    private void LateUpdate()
    {
        if (pixelCanvas == null) return;
        var mouse = Mouse.current;
        if (mouse == null) return;
        if (mouse.leftButton.wasPressedThisFrame) clickT = 0f;
        clickT += Time.unscaledDeltaTime;

        if (pixelCanvas.ScreenToLocal((RectTransform)rt.parent, mouse.position.ReadValue(), out var local))
            rt.anchoredPosition = local;

        var t = Time.unscaledTime;
        var pop = clickT < 0.14f ? 1f + 0.35f * Mathf.Sin(clickT / 0.14f * Mathf.PI) : 1f;
        body.localScale = new Vector3(pop, pop, 1f);
        body.localRotation = Quaternion.Euler(0f, 0f, 32f + Mathf.Sin(t * 3.1f) * 6f);
        tail.localRotation = Quaternion.Euler(0f, 0f, t * 180f);

        // Stay above anything built later (e.g. the character creator panel).
        if (rt.GetSiblingIndex() != rt.parent.childCount - 1) rt.SetAsLastSibling();
        if (Cursor.visible) Cursor.visible = false;
    }
}
