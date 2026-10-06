using UnityEngine;

/// <summary>
/// Drop on the HUD canvas (or a HUDRoot container RectTransform). The rect
/// drifts and tilts against the player's velocity and the camera's orbit
/// speed — a loose, instrument-like lag: strafe pushes it sideways, sprint
/// sinks it, falling dips it, and swinging the camera makes it counter-swing.
/// Player is auto-found via PlayerLocomotion if unassigned.
/// </summary>
public sealed class HudParallax : MonoBehaviour
{
    [SerializeField] private Transform player;
    [SerializeField, Min(0f)] private float strength = 13f;
    [Tooltip("HUD counter-drift per degree/sec of camera orbit — the part you notice even standing still.")]
    [SerializeField, Min(0f)] private float cameraSwing = 0.06f;
    [SerializeField, Range(0f, 4f)] private float tiltDeg = 0.9f;
    [SerializeField, Min(0.1f)] private float smooth = 9f;
    [Tooltip("Layer depth — scales drift and tilt. Foreground elements (prompts) >1, background (death stripes) <1.")]
    [SerializeField, Range(0f, 3f)] private float depth = 1f;

    /// <summary>Hangs a parallax layer on <paramref name="rt"/> at the given depth.</summary>
    public static HudParallax Attach(RectTransform rt, float depth)
    {
        var p = rt.gameObject.AddComponent<HudParallax>();
        p.depth = depth;
        return p;
    }

    private RectTransform rt;
    private CharacterController cc;
    private Vector2 basePos, offset;
    private float tilt, lastYaw = float.NaN;

    private void Awake()
    {
        rt = transform as RectTransform;

        // A ScreenSpaceOverlay canvas root is screen-locked — writing
        // anchoredPosition to it does nothing. If this component sits on the
        // Canvas itself, wrap the existing children in a movable root and
        // drive that instead.
        if (GetComponent<Canvas>() != null)
        {
            var root = new GameObject("HudParallaxRoot", typeof(RectTransform));
            var rrt = (RectTransform)root.transform;
            rrt.SetParent(transform, false);
            rrt.anchorMin = Vector2.zero;
            rrt.anchorMax = Vector2.one;
            rrt.offsetMin = rrt.offsetMax = Vector2.zero;
            // Take children front-to-back so sibling (render) order survives —
            // the frame must stay LAST to draw on top of the fills.
            while (transform.childCount > 1)
                transform.GetChild(0).SetParent(rrt, true); // world pos preserved
            rt = rrt;
        }

        if (rt == null)
        {
            Debug.LogWarning("[HudParallax] needs a RectTransform (Canvas or UI container) — nothing to move.", this);
            enabled = false;
            return;
        }
        basePos = rt.anchoredPosition;

        if (player == null)
        {
            var loco = FindFirstObjectByType<PlayerLocomotion>();
            if (loco != null) player = loco.transform;
        }
        if (player != null) cc = player.GetComponent<CharacterController>();
        if (cc == null)
            Debug.LogWarning("[HudParallax] no player/CharacterController found — only camera swing will apply.", this);
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;
        var v = cc != null ? player.InverseTransformDirection(cc.velocity) : Vector3.zero;

        float yawRate = 0f;
        var cam = Camera.main;
        if (cam != null)
        {
            float yaw = cam.transform.eulerAngles.y;
            if (!float.IsNaN(lastYaw))
                yawRate = Mathf.DeltaAngle(lastYaw, yaw) / Mathf.Max(dt, 0.0001f);
            lastYaw = yaw;
        }

        var target = new Vector2(
            -v.x * strength * 0.5f - Mathf.Clamp(yawRate * cameraSwing, -34f, 34f),
            -v.z * strength * 0.10f - Mathf.Clamp(v.y, -5f, 5f) * strength * 0.25f);
        float k = 1f - Mathf.Exp(-smooth * dt);
        offset = Vector2.Lerp(offset, target, k);
        // Elements anchored to a low edge (bottom rows, left column) flip that
        // axis's drift inward — the bottom HUD rises on a sprint instead of
        // sinking out of frame. Top/right-anchored elements keep the natural
        // counter-drift, which already leans them into the screen.
        var sx = rt.anchorMin.x == 0f && rt.anchorMax.x == 0f ? -1f : 1f;
        var sy = rt.anchorMin.y == 0f && rt.anchorMax.y == 0f ? -1f : 1f;
        rt.anchoredPosition = basePos + new Vector2(offset.x * sx, offset.y * sy) * depth;
        tilt = Mathf.Lerp(tilt,
            Mathf.Clamp(-v.x * 0.15f - yawRate * 0.004f, -tiltDeg, tiltDeg) * depth, k);
        rt.localRotation = Quaternion.Euler(0f, 0f, tilt);
        ClampInside(rt);
    }

    private static readonly Vector3[] corners = new Vector3[4];

    /// <summary>Pulls the rect back inside its parent Canvas — drift can push
    /// edge-anchored elements off the monitor otherwise. Call after position
    /// and rotation are written so the corners reflect both.</summary>
    public static void ClampInside(RectTransform rt)
    {
        var canvas = rt.GetComponentInParent<Canvas>();
        if (canvas == null) return;
        var c = (RectTransform)canvas.transform;
        var cr = c.rect;
        const float margin = 8f;
        rt.GetWorldCorners(corners);
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var w in corners)
        {
            var p = c.InverseTransformPoint(w);
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        var d = new Vector2(
            Mathf.Min(0f, cr.xMax - margin - maxX) + Mathf.Max(0f, cr.xMin + margin - minX),
            Mathf.Min(0f, cr.yMax - margin - maxY) + Mathf.Max(0f, cr.yMin + margin - minY));
        rt.anchoredPosition += d;
    }
}
