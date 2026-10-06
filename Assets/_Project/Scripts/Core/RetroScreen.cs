using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The N64 trick for real: renders the camera into a ~240-line point-filtered
/// RenderTexture and upscales it through a fullscreen RawImage on an overlay
/// canvas (sorting -10, under every HUD/menu canvas so UI stays crisp).
/// No renderer feature or render-graph plumbing — the RT IS the low-res
/// framebuffer, the RetroN64Ui material adds dither + 5-bit colour on the way up.
/// Wired to the Main Camera by Tools > Project Restart > Setup Retro Post (N64).
/// </summary>
[RequireComponent(typeof(Camera))]
public sealed class RetroScreen : MonoBehaviour
{
    [Tooltip("Framebuffer height in pixels — 240 = heavy retro, 640 = barely-visible softness, 1080 = native.")]
    [SerializeField, Min(16)] private int retroHeight = 640;
    [Tooltip("Bilinear RT = authentic N64 smear; Point = sharp PS1-style blocks.")]
    [SerializeField] private bool softUpscale = true;
    [Tooltip("Frame rate cap (0 = uncapped). 30 feels N64-era without OoT's real 20fps slog.")]
    [SerializeField, Min(0)] private int targetFps;
    [SerializeField] private Material material; // RetroN64Ui; falls back to Resources load

    private Camera cam;
    private RenderTexture rt;
    private RawImage display;

    private void OnEnable()
    {
        cam = GetComponent<Camera>();
        if (material == null) material = Resources.Load<Material>("Materials/RetroN64Ui");
        if (material == null) { Debug.LogError("[RetroScreen] RetroN64Ui material missing — run Tools > Project Restart > Setup Retro Post (N64)"); enabled = false; return; }
        if (targetFps > 0) Application.targetFrameRate = targetFps;
        Build();
        Rebuild();
    }

    private void Build()
    {
        var canvasGo = new GameObject("RetroScreen_Display", typeof(Canvas));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -10;

        var imgGo = new GameObject("Image", typeof(RectTransform));
        imgGo.transform.SetParent(canvasGo.transform, false);
        var rt2 = (RectTransform)imgGo.transform;
        rt2.anchorMin = Vector2.zero;
        rt2.anchorMax = Vector2.one;
        rt2.offsetMin = rt2.offsetMax = Vector2.zero;
        display = imgGo.AddComponent<RawImage>();
        display.raycastTarget = false;
        display.material = material;
    }

    private void Rebuild()
    {
        var w = Mathf.Max(16, Mathf.RoundToInt(retroHeight * (float)Screen.width / Mathf.Max(1, Screen.height)));
        if (rt != null && rt.width == w && rt.height == retroHeight && rt.filterMode == (softUpscale ? FilterMode.Bilinear : FilterMode.Point)) return;
        Release();
        rt = new RenderTexture(w, retroHeight, 24, RenderTextureFormat.ARGB32)
        {
            name = "RetroScreen_RT",
            filterMode = softUpscale ? FilterMode.Bilinear : FilterMode.Point,
            antiAliasing = 1,
            useMipMap = false,
        };
        rt.Create();
        cam.targetTexture = rt;
        if (display != null) display.texture = rt;
    }

    private void Update() => Rebuild();

    private void Release()
    {
        if (rt == null) return;
        if (cam != null && cam.targetTexture == rt) cam.targetTexture = null;
        rt.Release();
        Destroy(rt);
        rt = null;
    }

    private void OnDisable() => Release();
    private void OnDestroy() => Release();
}
