using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Persona slab wipe between menus and scenes: a handful of wide tilted
/// parallelogram slabs in the frame palette glide in from the right
/// (staggered, ease-in-out), hold while the swap happens, then glide out to
/// the left — few and wide so it reads as one smooth wipe, not stripes.
/// Lives across scene loads with its own pixel canvas, so it stays pixelated.
/// </summary>
public sealed class PersonaTransition : MonoBehaviour
{
    private static PersonaTransition instance;

    private PixelCanvas pixelCanvas;
    private RectTransform[] bands;
    private PolyGraphic[] bandG;
    private Color[] bandColor;
    private float[] coveredX;
    private bool busy;

    private const int BandCount = 7;
    private const float BandSpacing = 360f;
    private const float BandOverlap = 90f;
    private const float InTime = 0.34f;
    private const float OutTime = 0.36f;
    private const float Stagger = 0.035f;
    private const float OffRight = 2600f;
    private const float OffLeft = -2800f;
    private const float OffY = 1200f; // diagonal sweep: in from top-right, out bottom-left

    public static bool Busy => instance != null && instance.busy;

    private static PersonaTransition Instance
    {
        get
        {
            if (instance == null)
            {
                var go = new GameObject("PersonaTransition");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<PersonaTransition>();
                instance.Construct();
            }
            return instance;
        }
    }

    private void Construct()
    {
        pixelCanvas = PixelCanvas.Create("TransitionPixelUi", 1000, 1.25f, transform, interactive: false);
        var container = PersonaUi.Stretch(pixelCanvas.Root, "Bands");
        container.localRotation = Quaternion.Euler(0f, 0f, PersonaUi.Tilt * 3f); // ~24° — reads as diagonal bars, not vertical
        var palette = new[]
        {
            PersonaUi.Heart, PersonaUi.Ink, PersonaUi.Violet, PersonaUi.Night,
            PersonaUi.Heart, PersonaUi.Plum, PersonaUi.Ink, PersonaUi.Copper,
        };
        bands = new RectTransform[BandCount];
        bandG = new PolyGraphic[BandCount];
        bandColor = new Color[BandCount];
        coveredX = new float[BandCount];
        var first = -(BandCount - 1) * BandSpacing * 0.5f;
        for (var i = 0; i < BandCount; i++)
        {
            var b = PersonaUi.Box(container, "Band" + i, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(BandSpacing + BandOverlap, 2400f));
            bandColor[i] = palette[i % palette.Length];
            bandG[i] = PersonaUi.Poly(b, "Slab", PolyGraphic.Shape.Parallelogram, bandColor[i], 0.12f);
            coveredX[i] = first + i * BandSpacing;
            b.anchoredPosition = new Vector2(OffRight, 0f);
            bands[i] = b;
        }
        pixelCanvas.SetVisible(false);
    }

    /// <summary>Wipe in, run <paramref name="midpoint"/> behind the stripes, wipe out.</summary>
    public static void Run(Action midpoint)
    {
        var t = Instance;
        if (t.busy) { midpoint?.Invoke(); return; }
        t.StartCoroutine(t.RunRoutine(midpoint, null));
    }

    /// <summary>Wipe in, load the scene, wipe out once it's up.</summary>
    public static void LoadScene(string sceneName)
    {
        var t = Instance;
        if (t.busy) return;
        t.StartCoroutine(t.RunRoutine(null, sceneName));
    }

    /// <summary>Start fully covered and wipe out — scene-open reveal.</summary>
    public static void Reveal()
    {
        var t = Instance;
        if (t.busy) return;
        t.StartCoroutine(t.RevealRoutine());
    }

    private IEnumerator RunRoutine(Action midpoint, string sceneName)
    {
        busy = true;
        SetStyle(1f, 1f);
        pixelCanvas.SetVisible(true);
        yield return Sweep(true);
        midpoint?.Invoke();
        if (!string.IsNullOrEmpty(sceneName))
        {
            var op = SceneManager.LoadSceneAsync(sceneName);
            while (op != null && !op.isDone) yield return null;
        }
        yield return null; // let the new panel/scene build a frame behind the stripes
        yield return Sweep(false);
        Finish();
    }

    private IEnumerator RevealRoutine()
    {
        busy = true;
        SetStyle(1f, 1f);
        pixelCanvas.SetVisible(true);
        for (var i = 0; i < BandCount; i++) bands[i].anchoredPosition = new Vector2(coveredX[i], 0f);
        yield return null;
        yield return Sweep(false);
        Finish();
    }

    private void Finish()
    {
        for (var i = 0; i < BandCount; i++) bands[i].anchoredPosition = new Vector2(OffRight, OffY);
        pixelCanvas.SetVisible(false);
        busy = false;
    }

    /// <summary>Alpha and width multiplier for the band set.</summary>
    private void SetStyle(float alpha, float widthScale)
    {
        for (var i = 0; i < BandCount; i++)
        {
            bandG[i].color = PersonaUi.WithAlpha(bandColor[i], alpha);
            bands[i].sizeDelta = new Vector2((BandSpacing + BandOverlap) * widthScale, 2400f);
        }
    }

    private IEnumerator Sweep(bool covering)
    {
        var dur = covering ? InTime : OutTime;
        var total = dur + Stagger * BandCount;
        for (var t = 0f; t <= total; t += Time.unscaledDeltaTime)
        {
            for (var i = 0; i < BandCount; i++)
            {
                var k = EaseInOut(Mathf.Clamp01((t - i * Stagger) / dur));
                var x = covering ? Mathf.Lerp(OffRight, coveredX[i], k) : Mathf.Lerp(coveredX[i], OffLeft, k);
                var y = covering ? Mathf.Lerp(OffY, 0f, k) : Mathf.Lerp(0f, -OffY, k);
                bands[i].anchoredPosition = new Vector2(x, y);
            }
            yield return null;
        }
        for (var i = 0; i < BandCount; i++)
            bands[i].anchoredPosition = covering
                ? new Vector2(coveredX[i], 0f)
                : new Vector2(OffLeft, -OffY);
    }

    private static float EaseInOut(float t) => t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
}
