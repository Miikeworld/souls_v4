using UnityEngine;

/// <summary>
/// Expanding ground shockwave from a boss slam: a crimson ring races outward
/// from <see cref="center"/>; the player is hit once if the ring's band passes
/// under their feet while they're on the ground. Jump over it, or dodge
/// through it with i-frames. Self-destroys at <see cref="maxRadius"/>.
/// Drawn in the player's effect language (WardenFx / TraversalEffects): a faceted
/// 24-gon front over an ink underlay, a dimmer trailing edge marking the back of
/// the hit band, four-band stepped fade × the player's opacity, near-flat colour,
/// and shard chips kicked off the front as it travels.
/// </summary>
public sealed class ShockRing : MonoBehaviour
{
    private const int Sides = 24;
    private readonly Vector3[] pts = new Vector3[Sides + 1];
    private Vector3 center;
    private float radius, speed, maxRadius, damage, band, spin, chipClock;
    private Color color;
    private LineRenderer front, frontInk, back, backInk;
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
        r.color = color;
        r.color.a = 1f;
        r.spin = Random.value * Mathf.PI;
        r.frontInk = r.Line("Front ink", 0);
        r.front = r.Line("Front", 1);
        r.backInk = r.Line("Back ink", 0);
        r.back = r.Line("Back", 1);
        var loco = FindFirstObjectByType<PlayerLocomotion>();
        if (loco != null) { r.player = loco.GetComponent<PlayerHealth>(); r.state = loco.GetComponent<PlayerState>(); }
        // Launch: a white facet ring snaps open where the wave leaves the ground.
        WardenFx.Pulse(r.center, Vector3.up, 0.2f, 1.1f, 0.24f, Color.white, 1.1f);
        return r;
    }

    private LineRenderer Line(string name, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = WardenFx.GlowMaterial;
        lr.loop = false;
        lr.useWorldSpace = true;
        lr.numCornerVertices = 0;   // hard facet corners
        lr.numCapVertices = 0;
        lr.positionCount = Sides + 1;
        lr.sortingOrder = order;    // ink (0) under its stroke (1)
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.enabled = false;
        return lr;
    }

    private void Update()
    {
        radius += speed * Time.deltaTime;
        var k = Mathf.Clamp01(radius / maxRadius);
        var op = WardenFx.Opacity;
        var inkK = WardenFx.InkStrength;
        var a = WardenFx.Stepped(1f - k) * op;
        // Bigger wave = wider geometry, never more alpha.
        var w = 0.13f * Mathf.Clamp(Mathf.Sqrt(radius), 1f, 2.2f);
        Draw(front, frontInk, radius, w, a, inkK);
        Draw(back, backInk, Mathf.Max(0.2f, radius - band), w * 0.55f, WardenFx.Stepped((1f - k) * 0.5f) * op, inkK);
        if ((chipClock -= Time.deltaTime) <= 0f && a > 0f)
        {
            chipClock = 0.07f;
            for (var i = 0; i < 3; i++)
            {
                var ang = Random.value * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                WardenFx.Chips(center + dir * radius, 1, 1.8f, Vector3.up * 0.5f + dir * 0.35f, 0.35f, color, 0.9f);
            }
        }

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

    /// <summary>Faceted polygon over its ink underlay (TraversalEffects.Draw).</summary>
    private void Draw(LineRenderer line, LineRenderer ink, float r, float width, float alpha, float inkK)
    {
        var on = alpha > 0.005f;
        line.enabled = ink.enabled = on;
        if (!on) return;
        for (var i = 0; i <= Sides; i++)
        {
            var ang = spin + i * Mathf.PI * 2f / Sides;
            pts[i] = center + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
        }
        line.SetPositions(pts);
        ink.SetPositions(pts);
        line.startWidth = line.endWidth = width;
        line.startColor = line.endColor = WardenFx.Glow(color, alpha);
        var k = WardenFx.Ink;
        k.a = alpha * inkK;
        ink.startWidth = ink.endWidth = width * 2.3f;
        ink.startColor = ink.endColor = k;
    }
}
