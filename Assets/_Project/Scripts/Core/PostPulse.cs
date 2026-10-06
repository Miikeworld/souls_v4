using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The combat post stack: an always-on Bloom baseline (the scene's dark fantasy
/// reads clean without it) plus two pulse channels — a bloom spike on casts and
/// a chromatic-aberration burst on heavy connects. Lazily builds a global URP
/// Volume with a runtime VolumeProfile the first time anything pulses (or on
/// scene load) — no scene wiring. Requires the camera's renderPostProcessing=1
/// (the test-scene camera already has it).
/// </summary>
public sealed class PostPulse : MonoBehaviour
{
    private const float BaseBloom = 1.2f;   // rest state — arcs/sparks glow into the scene
    private const float BloomThreshold = 0.8f;

    private static PostPulse instance;

    private Bloom bloom;
    private ChromaticAberration chroma;
    private float bAge = -1f, bDur, bPeak;
    private float cAge = -1f, cDur, cPeak;
    private float cFloor;

    /// <summary>Punch bloom to <paramref name="intensity"/> over the baseline and
    /// decay over <paramref name="seconds"/>. Re-entry peaks at the max.</summary>
    public static void Pulse(float intensity, float seconds = 0.5f)
    {
        if (intensity <= 0f) return;
        var p = Ensure();
        if (p?.bloom == null) return;
        p.bPeak = Mathf.Max(p.bPeak, intensity);
        p.bDur = Mathf.Max(0.05f, seconds);
        if (p.bAge < 0f) p.bAge = 0f;
    }

    /// <summary>Chromatic-aberration snap — heavy connects, counters. Short and
    /// violent: 0 → peak → 0 across ~0.08s reads as an impact flash, not a blur.</summary>
    public static void AberrationPulse(float peak = 0.35f, float seconds = 0.09f)
    {
        var p = Ensure();
        if (p?.chroma == null) return;
        p.cPeak = Mathf.Max(p.cPeak, peak);
        p.cDur = Mathf.Max(0.03f, seconds);
        if (p.cAge < 0f) p.cAge = 0f;
    }

    /// <summary>Held aberration level (sprint speed-lines push edge chroma).
    /// The caller eases k itself; pulses still add on top.</summary>
    public static void SustainAberration(float k)
    {
        var p = Ensure();
        if (p != null) p.cFloor = Mathf.Clamp01(k);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Warmup() => Ensure(); // baseline bloom exists from scene start

    private static PostPulse Ensure()
    {
        if (instance != null) return instance;
        instance = FindFirstObjectByType<PostPulse>();
        if (instance == null)
            instance = new GameObject("PostPulse").AddComponent<PostPulse>();
        instance.Build();
        return instance;
    }

    private void Build()
    {
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        bloom = profile.Add<Bloom>(true);
        bloom.active = true;
        bloom.intensity.overrideState = true;
        bloom.intensity.value = BaseBloom;
        bloom.threshold.overrideState = true;
        bloom.threshold.value = BloomThreshold;
        bloom.scatter.overrideState = true;
        bloom.scatter.value = 0.6f;
        chroma = profile.Add<ChromaticAberration>(true);
        chroma.active = true;
        chroma.intensity.overrideState = true;
        chroma.intensity.value = 0f;
        var vol = gameObject.AddComponent<Volume>();
        vol.isGlobal = true;
        vol.priority = 100f;
        vol.profile = profile;
    }

    private void Update()
    {
        var dt = Time.unscaledDeltaTime;
        // Same envelope for both: snap in over 20%, ease the tail out.
        if (bloom != null)
            bloom.intensity.value = BaseBloom + Channel(ref bAge, bDur, ref bPeak, dt);
        if (chroma != null)
            chroma.intensity.value = Mathf.Clamp01(cFloor + Channel(ref cAge, cDur, ref cPeak, dt));
    }

    private static float Channel(ref float age, float dur, ref float peak, float dt)
    {
        if (age < 0f) return 0f;
        age += dt;
        var k = Mathf.Clamp01(age / dur);
        var v = peak * (k < 0.2f ? k / 0.2f : 1f - (k - 0.2f) / 0.8f);
        if (k >= 1f) { age = -1f; peak = 0f; }
        return v;
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }
}
