using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Pointer-driven depth layer for the menus: the rect drifts opposite the
/// cursor (or the right stick while it's held), scaled by <see cref="depth"/>,
/// so stacked layers slide at different rates. Writes base + offset every
/// frame — only attach to rects no other script positions (panel/heading
/// roots, background layers), never to animated children.
/// </summary>
public sealed class MenuParallax : MonoBehaviour
{
    private static readonly Vector2 MaxOffset = new Vector2(28f, 16f);
    private const float MaxTilt = 0.6f;

    [SerializeField, Range(0f, 3f)] private float depth = 1f;
    [Tooltip("Pull the rect back inside the canvas. Panels/menus want it; background layers that intentionally span off-screen leave it off.")]
    [SerializeField] private bool clampInside;

    private RectTransform rt;
    private Vector2 basePos;
    private Quaternion baseRot;
    private Vector2 input;
    private bool started;

    public static MenuParallax Attach(RectTransform rt, float depth, bool clamp = false)
    {
        var p = rt.gameObject.AddComponent<MenuParallax>();
        p.depth = depth;
        p.clampInside = clamp;
        return p;
    }

    private void Start()
    {
        rt = transform as RectTransform;
        if (rt == null) { enabled = false; return; }
        basePos = rt.anchoredPosition;
        baseRot = rt.localRotation;
        started = true;
    }

    private void Update()
    {
        if (!started) return;
        var target = Vector2.zero;
        var stick = Gamepad.current != null ? Gamepad.current.rightStick.ReadValue() : Vector2.zero;
        if (stick.sqrMagnitude > 0.04f) target = stick;
        else if (Mouse.current != null && Screen.width > 0 && Screen.height > 0)
        {
            var m = Mouse.current.position.ReadValue();
            target = new Vector2(m.x / Screen.width * 2f - 1f, m.y / Screen.height * 2f - 1f);
        }
        target = Vector2.ClampMagnitude(target, 1.2f);
        input = Vector2.Lerp(input, target, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
        rt.anchoredPosition = basePos - Vector2.Scale(input, MaxOffset) * depth;
        rt.localRotation = baseRot * Quaternion.Euler(0f, 0f, -input.x * MaxTilt * depth);
        if (clampInside) HudParallax.ClampInside(rt);
    }
}
