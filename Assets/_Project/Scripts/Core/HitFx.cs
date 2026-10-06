using UnityEngine;

/// <summary>
/// Impact VFX at the point a blow lands: a spray of square pixel sparks
/// (velocity-stretched = smeared) in the health bar's bone/copper/heart
/// palette. Everything runs on real time, so it plays out inside the hit
/// freeze. One pooled host, built lazily — <see cref="Spawn"/> is the API.
/// No slash streak — a generic crescent can't track the swing's arc, so the
/// sparks carry the contact read on their own (per-art FX cues stay).
/// </summary>
public sealed class HitFx : MonoBehaviour
{
    private static HitFx instance;
    private static readonly Color Purple = new Color(.62f, .3f, 1f); // white centre → purple exterior (FX plan)
    private const float Opacity = 0.65f; // sparks stay see-through — solid white read as harsh

    private ParticleSystem sparks;

    /// <param name="point">World contact point.</param>
    /// <param name="swingDir">Blade travel direction — sparks spray along it.</param>
    /// <param name="strength">1 = normal hit; finishers ~1.5, backstab 2.</param>
    /// <param name="tint">Exterior colour around the white core — the weapon's impactTint.</param>
    public static void Spawn(Vector3 point, Vector3 swingDir, float strength, Color? tint = null)
    {
        if (instance == null) instance = new GameObject("HitFx").AddComponent<HitFx>().Build();
        instance.Retint(tint ?? Purple);
        instance.Emit(point, swingDir, Mathf.Max(0.3f, strength));
    }

    private Color lastTint = new Color(-1f, 0f, 0f);
    private void Retint(Color tint)
    {
        if (tint == lastTint) return;
        lastTint = tint;
        var main = sparks.main;
        main.startColor = new ParticleSystem.MinMaxGradient(Color.white, tint);
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(tint, 0.4f), new GradientColorKey(tint * 0.5f, 1f) },
            new[] { new GradientAlphaKey(Opacity, 0f), new GradientAlphaKey(Opacity, 0.6f), new GradientAlphaKey(0f, 1f) });
        var col = sparks.colorOverLifetime;
        col.color = g;
    }

    private HitFx Build()
    {
        var shader = Shader.Find("Sprites/Default"); // unlit, vertex-coloured, alpha — squares = pixel sparks

        var sGo = new GameObject("Sparks");
        sGo.transform.SetParent(transform, false);
        sparks = sGo.AddComponent<ParticleSystem>();
        sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = sparks.main;
        main.loop = false;
        main.playOnAwake = false;
        main.useUnscaledTime = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.32f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 14f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.075f);
        main.gravityModifier = 1.5f;
        main.maxParticles = 300;
        main.startColor = new ParticleSystem.MinMaxGradient(Color.white, Purple);
        var emission = sparks.emission;
        emission.enabled = false;
        var shape = sparks.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 55f;
        shape.radius = 0.04f;
        var col = sparks.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Purple, 0.4f), new GradientColorKey(new Color(.3f, .1f, .6f), 1f) },
            new[] { new GradientAlphaKey(Opacity, 0f), new GradientAlphaKey(Opacity, 0.6f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var pr = sGo.GetComponent<ParticleSystemRenderer>();
        pr.renderMode = ParticleSystemRenderMode.Stretch; // the smear
        pr.velocityScale = 0.06f;
        pr.lengthScale = 1f;
        pr.material = new Material(shader);
        return this;
    }

    private void Emit(Vector3 point, Vector3 swingDir, float strength)
    {
        if (swingDir.sqrMagnitude < 0.0001f) swingDir = Vector3.right;
        sparks.transform.SetPositionAndRotation(point, Quaternion.LookRotation(swingDir.normalized));
        sparks.Emit(Mathf.RoundToInt(14f * strength));
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }
}
