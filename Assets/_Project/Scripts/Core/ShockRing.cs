using UnityEngine;

/// <summary>
/// Expanding ground shockwave from a boss slam: a crimson ring races outward
/// from <see cref="center"/>; the player is hit once if the ring's band passes
/// under their feet while they're on the ground. Jump over it, or dodge
/// through it with i-frames. Self-destroys at <see cref="maxRadius"/>.
/// </summary>
public sealed class ShockRing : MonoBehaviour
{
    private const int Segments = 72;
    private Vector3 center;
    private float radius, speed, maxRadius, damage, band;
    private LineRenderer core, glow;
    private Material mat;
    private PlayerHealth player;
    private PlayerState state;
    private bool hit;

    public static ShockRing Spawn(Vector3 center, float maxRadius, float speed, float damage, Color color, float band = 0.7f)
    {
        var go = new GameObject("ShockRing");
        var r = go.AddComponent<ShockRing>();
        r.center = center + Vector3.up * 0.06f;
        r.maxRadius = maxRadius;
        r.speed = speed;
        r.damage = damage;
        r.band = band;
        r.radius = 0.4f;
        // Vertex colour × tint (TraversalGlow, the player-FX material) so the band can fade.
        var shader = Shader.Find("Souls/TraversalGlow") ?? Shader.Find("Sprites/Default");
        r.mat = new Material(shader);
        if (r.mat.HasProperty("_Tint")) r.mat.SetColor("_Tint", color * 2.2f);
        else r.mat.color = color;
        r.core = r.Line("Core", 0.22f);
        r.glow = r.Line("Glow", 0.9f);
        var loco = FindFirstObjectByType<PlayerLocomotion>();
        if (loco != null) { r.player = loco.GetComponent<PlayerHealth>(); r.state = loco.GetComponent<PlayerState>(); }
        return r;
    }

    private LineRenderer Line(string name, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = mat;
        lr.loop = true;
        lr.useWorldSpace = true;
        lr.positionCount = Segments;
        lr.widthMultiplier = width;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    private void Update()
    {
        radius += speed * Time.deltaTime;
        var k = Mathf.Clamp01(radius / maxRadius);
        for (var i = 0; i < Segments; i++)
        {
            var a = i * Mathf.PI * 2f / Segments;
            var p = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            core.SetPosition(i, p);
            glow.SetPosition(i, p);
        }
        core.startColor = core.endColor = new Color(1f, 1f, 1f, 1f - k * 0.6f);
        glow.startColor = glow.endColor = new Color(1f, 1f, 1f, 0.35f * (1f - k));

        if (!hit && damage > 0f && player != null && !player.IsDead)
        {
            var flat = player.transform.position - center;
            var height = flat.y;
            flat.y = 0f;
            var onGround = height < 0.9f; // a jump clears the wave
            var invuln = state != null && state.IsInvulnerable;
            if (onGround && !invuln && Mathf.Abs(flat.magnitude - radius) <= band)
            {
                hit = true;
                player.TakeDamage(damage, center);
            }
        }
        if (radius >= maxRadius) Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (mat != null) Destroy(mat);
    }
}
