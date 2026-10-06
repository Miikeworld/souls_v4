using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Renders a uGUI canvas into a low-res RenderTexture and point-upscales it to
/// the screen, so every element — rotated slabs, text, shapes, the cursor —
/// gets real pixel stair-steps. The canvas keeps a 1920x1080 reference layout;
/// the scaler maps it onto the small target. Input still works through the
/// normal EventSystem: <see cref="PixelRaycaster"/> remaps pointer positions
/// from screen pixels to render-texture pixels.
/// </summary>
public sealed class PixelCanvas : MonoBehaviour
{
    public const int UiLayer = 5; // built-in "UI" layer

    [Tooltip("Screen pixels per UI pixel. 1.5 = mildly chunky + readable at 1080p, 2 = crunchy.")]
    [SerializeField, Min(1f)] private float pixelScale = 1.25f;

    public Canvas Canvas { get; private set; }
    public Camera Camera { get; private set; }
    public RectTransform Root => (RectTransform)Canvas.transform;

    private RenderTexture rt;
    private RawImage display;
    private GameObject displayGo;
    private static Material premulMat;

    public static PixelCanvas Create(string name, int sortingOrder, float pixelScale, Transform parent, bool interactive = true)
    {
        var go = new GameObject(name);
        go.layer = UiLayer;
        if (parent != null) go.transform.SetParent(parent, false);
        var pc = go.AddComponent<PixelCanvas>();
        pc.pixelScale = Mathf.Max(1f, pixelScale);
        pc.Build(sortingOrder, interactive);
        return pc;
    }

    private void Build(int sortingOrder, bool interactive)
    {
        // Parked far below the world so it can never frame the 3D stage; its
        // culling mask only admits the UI layer anyway.
        var camGo = new GameObject(name + "_Camera");
        camGo.layer = UiLayer;
        camGo.transform.SetParent(transform, false);
        camGo.transform.position = new Vector3(0f, -10000f, 0f);
        Camera = camGo.AddComponent<Camera>();
        Camera.orthographic = true;
        Camera.clearFlags = CameraClearFlags.SolidColor;
        Camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        Camera.cullingMask = 1 << UiLayer;
        Camera.nearClipPlane = 0.1f;
        Camera.farClipPlane = 100f;
        Camera.allowHDR = false;
        Camera.allowMSAA = false;
        Camera.depth = 50f;

        var canvasGo = interactive
            ? new GameObject(name + "_Ui", typeof(Canvas), typeof(CanvasScaler), typeof(PixelRaycaster))
            : new GameObject(name + "_Ui", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.layer = UiLayer;
        canvasGo.transform.SetParent(transform, false);
        Canvas = canvasGo.GetComponent<Canvas>();
        Canvas.renderMode = RenderMode.ScreenSpaceCamera;
        Canvas.worldCamera = Camera;
        Canvas.planeDistance = 10f;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        displayGo = new GameObject(name + "_Display", typeof(Canvas));
        displayGo.transform.SetParent(transform, false);
        var dc = displayGo.GetComponent<Canvas>();
        dc.renderMode = RenderMode.ScreenSpaceOverlay;
        dc.sortingOrder = sortingOrder;
        var imgGo = new GameObject("Image", typeof(RectTransform));
        imgGo.transform.SetParent(displayGo.transform, false);
        var irt = (RectTransform)imgGo.transform;
        irt.anchorMin = Vector2.zero;
        irt.anchorMax = Vector2.one;
        irt.offsetMin = irt.offsetMax = Vector2.zero;
        display = imgGo.AddComponent<RawImage>();
        display.raycastTarget = false;
        if (premulMat == null)
        {
            var sh = Resources.Load<Shader>("Shaders/PixelUiPremul");
            if (sh != null) premulMat = new Material(sh);
        }
        display.material = premulMat;

        Resize();
    }

    private void Resize()
    {
        var w = Mathf.Max(1, Mathf.RoundToInt(Screen.width / pixelScale));
        var h = Mathf.Max(1, Mathf.RoundToInt(Screen.height / pixelScale));
        if (rt != null && rt.width == w && rt.height == h) return;
        if (rt != null)
        {
            Camera.targetTexture = null;
            rt.Release();
            Destroy(rt);
        }
        rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
        {
            name = name + "_RT",
            filterMode = FilterMode.Point,
            antiAliasing = 1,
            useMipMap = false,
        };
        rt.Create();
        Camera.targetTexture = rt;
        display.texture = rt;
    }

    private void Update() => Resize();

    public void SetVisible(bool visible)
    {
        if (displayGo != null) displayGo.SetActive(visible);
        if (Camera != null) Camera.enabled = visible;
    }

    /// <summary>Screen pixel → render-texture pixel.</summary>
    public Vector2 ScreenToPixel(Vector2 screen)
    {
        if (rt == null) return screen;
        return new Vector2(screen.x * rt.width / Mathf.Max(1f, Screen.width),
                           screen.y * rt.height / Mathf.Max(1f, Screen.height));
    }

    /// <summary>Screen pixel → local point inside <paramref name="rect"/>.</summary>
    public bool ScreenToLocal(RectTransform rect, Vector2 screen, out Vector2 local)
    {
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, ScreenToPixel(screen), Camera, out local);
    }

    private void OnDestroy()
    {
        if (rt != null)
        {
            if (Camera != null) Camera.targetTexture = null;
            rt.Release();
            Destroy(rt);
        }
    }
}
