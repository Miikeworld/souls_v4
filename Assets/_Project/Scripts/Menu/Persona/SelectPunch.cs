using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Lighter Persona selection kick for dense rows (character creator): hover
/// selects, selection punches the scale past its rest and tilts the row a
/// little. Leaves layout untouched — only localScale/localRotation move.
/// </summary>
public sealed class SelectPunch : MonoBehaviour, ISelectHandler, IDeselectHandler, IPointerEnterHandler
{
    private bool hot;
    private float t = 10f;
    private float heat;

    public void OnSelect(BaseEventData e) { hot = true; t = 0f; }
    public void OnDeselect(BaseEventData e) { hot = false; }

    public void OnPointerEnter(PointerEventData e)
    {
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(gameObject);
    }

    private void OnDisable()
    {
        hot = false;
        heat = 0f;
        transform.localScale = Vector3.one;
        transform.localRotation = Quaternion.identity;
    }

    private void Update()
    {
        var dt = Time.unscaledDeltaTime;
        t += dt;
        heat = Mathf.MoveTowards(heat, hot ? 1f : 0f, dt * 10f);
        var s = 1f + 0.025f * heat;
        if (hot && t < 0.18f) s += 0.05f * Mathf.Sin(t / 0.18f * Mathf.PI);
        transform.localScale = new Vector3(s, s, 1f);
        transform.localRotation = Quaternion.Euler(0f, 0f, -1.2f * heat);
    }
}
