using UnityEngine;

/// <summary>
/// Keeps a UI element subtly alive: a slow positional bob and a tiny rotation
/// sway around its authored pose. Put it on a dedicated child ("Motion") so it
/// never fights layout or entrance animations on the parent.
/// </summary>
public sealed class UiFloat : MonoBehaviour
{
    [SerializeField] private float ampX = 3f;
    [SerializeField] private float ampY = 5f;
    [SerializeField] private float rotAmp = 0.8f;
    [SerializeField] private float speed = 1f;
    [SerializeField] private float phase;

    private RectTransform rt;
    private Vector2 basePos;
    private Quaternion baseRot;
    private bool captured;

    public UiFloat Configure(float x, float y, float rot, float phaseOffset, float spd = 1f)
    {
        ampX = x; ampY = y; rotAmp = rot; phase = phaseOffset; speed = spd;
        return this;
    }

    private void OnEnable()
    {
        rt = (RectTransform)transform;
        if (!captured)
        {
            basePos = rt.anchoredPosition;
            baseRot = rt.localRotation;
            captured = true;
        }
    }

    private void Update()
    {
        var t = Time.unscaledTime * speed + phase;
        rt.anchoredPosition = basePos + new Vector2(Mathf.Sin(t * 1.13f) * ampX, Mathf.Sin(t * 1.61f) * ampY);
        rt.localRotation = baseRot * Quaternion.Euler(0f, 0f, Mathf.Sin(t * 0.87f) * rotAmp);
    }
}
