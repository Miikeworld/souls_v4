using UnityEngine;

/// <summary>
/// Timed VFX spawner for weapon arts — resolves the cue's attach point (a
/// hand bone, the body, the locked target), instantiates the pack prefab,
/// optionally follows the bone, applies a Persona palette tint and cleans up.
/// Pure data-driven: art assets carry the cues, this just plays them.
/// </summary>
public static class ArtFx
{
    // Persona-tinted palette — index 0 keeps the prefab's authored colors.
    private static readonly Color[] Palette =
    {
        Color.white,                              // 0 authored
        new Color(0.92f, 0.08f, 0.12f),           // 1 persona red
        new Color(1.00f, 0.20f, 0.55f),           // 2 magenta slash
        new Color(1.00f, 0.85f, 0.35f),           // 3 gold
        new Color(0.25f, 0.88f, 1.00f),           // 4 cyan
        new Color(0.55f, 0.40f, 1.00f),           // 5 violet
    };

    /// <summary>The one crimson every DarkCrimson art tints toward — keeps the
    /// big-sword skill FX on a single red regardless of the equipped weapon.</summary>
    public static readonly Color DarkCrimson = new Color(0.92f, 0.08f, 0.12f);

    public static GameObject Spawn(FxCue cue, Transform owner, Animator anim, Transform target, Color? forceTint = null)
    {
        if (cue == null || cue.prefab == null || owner == null) return null;
        var attach = Resolve(cue.attach, owner, anim, target);
        var pos = attach != null
            ? attach.TransformPoint(cue.offset)
            : owner.TransformPoint(cue.offset);
        var rot = (attach != null ? attach.rotation : owner.rotation) * Quaternion.Euler(cue.euler);
        if (cue.attach == "floor")
        {
            // Authored scar cues are short visual accents, never damage owners.
            // A near-floor probe avoids placing them in the air during leap arts.
            var floorHits = Physics.RaycastAll(owner.position + Vector3.up * 0.4f, Vector3.down,
                0.85f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(floorHits, (a, b) => a.distance.CompareTo(b.distance));
            var found = false;
            foreach (var hit in floorHits)
            {
                if (hit.transform.IsChildOf(owner) || hit.collider.GetComponentInParent<Health>() != null ||
                    hit.collider.GetComponentInParent<PlayerHealth>() != null || hit.normal.y < 0.5f) continue;
                pos = hit.point + hit.normal * 0.02f + owner.TransformVector(cue.offset);
                rot = Quaternion.FromToRotation(Vector3.up, hit.normal) * owner.rotation * Quaternion.Euler(cue.euler);
                found = true; break;
            }
            if (!found) return null;
            attach = null;
        }

        var go = Object.Instantiate(cue.prefab, pos, rot);
        if (cue.follow && attach != null)
            go.transform.SetParent(attach, worldPositionStays: true);
        if (!Mathf.Approximately(cue.scale, 1f))
            go.transform.localScale *= cue.scale;
        if (forceTint.HasValue) Tint(go, forceTint.Value, force: true);
        else if (cue.palette > 0 && cue.palette < Palette.Length)
            Tint(go, Palette[cue.palette]);
        Object.Destroy(go, cue.life > 0f ? cue.life : LongestLife(go) + 0.5f);
        return go;
    }

    /// <summary>One-shot world-positioned burst — plain melee hits don't need
    /// a full cue, just a prefab, a spot and a facing.</summary>
    public static GameObject SpawnAt(GameObject prefab, Vector3 pos, Quaternion rot, float life = 0f)
    {
        if (prefab == null) return null;
        var go = Object.Instantiate(prefab, pos, rot);
        Object.Destroy(go, life > 0f ? life : LongestLife(go) + 0.5f);
        return go;
    }

    /// <summary>Anchor by cue.attach id. Null attach = spawn at owner's feet frame.</summary>
    private static Transform Resolve(string attach, Transform owner, Animator anim, Transform target)
    {
        switch (attach)
        {
            case "handR": return Bone(anim, HumanBodyBones.RightHand) ?? owner;
            case "handL": return Bone(anim, HumanBodyBones.LeftHand) ?? owner;
            case "body": return Bone(anim, HumanBodyBones.Chest) ?? Bone(anim, HumanBodyBones.Spine) ?? owner;
            case "target": return target; // may be null — caller still gets a valid pos
            case "root": return owner;
            default: return owner; // "world"/unset — owner frame, unparented
        }
    }

    private static Transform Bone(Animator anim, HumanBodyBones bone)
        => anim != null && anim.avatar != null && anim.avatar.isHuman ? anim.GetBoneTransform(bone) : null;

    /// <summary>Multiplies constant startColors + light colors by the palette
    /// tint. Gradient-driven systems keep their authored ramp — forcing a
    /// gradient into a flat color washes out the layered Synty FX — unless
    /// <paramref name="force"/>, which remaps EVERY colour channel (gradients,
    /// colour-over-lifetime, trails, sprites) toward the tint by luminance, so
    /// a blue-authored pack FX reads red under a DarkCrimson art. ponytail:
    /// MeshRenderer material colours are not touched (per-spawn material
    /// instancing); Synty FX here are particle/trail-driven.</summary>
    /// <summary>Public entry to the full remap (projectile prefabs et al).</summary>
    public static void ForceTint(GameObject go, Color tint) => Tint(go, tint, force: true);

    private static void Tint(GameObject go, Color tint, bool force = false)
    {
        foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            var m = ps.main;
            if (force) m.startColor = Remap(m.startColor, tint);
            else if (m.startColor.mode == ParticleSystemGradientMode.Color)
                m.startColor = m.startColor.color * tint;
            if (force && ps.colorOverLifetime.enabled)
            {
                var col = ps.colorOverLifetime;
                col.color = Remap(col.color, tint);
            }
        }
        if (!force)
        {
            foreach (var l in go.GetComponentsInChildren<Light>(true))
                l.color *= tint;
            return;
        }
        foreach (var l in go.GetComponentsInChildren<Light>(true))
            l.color = Remap(l.color, tint);
        foreach (var tr in go.GetComponentsInChildren<TrailRenderer>(true))
        { tr.startColor = Remap(tr.startColor, tint); tr.endColor = Remap(tr.endColor, tint); }
        foreach (var lr in go.GetComponentsInChildren<LineRenderer>(true))
        { lr.startColor = Remap(lr.startColor, tint); lr.endColor = Remap(lr.endColor, tint); }
        foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true))
            sr.color = Remap(sr.color, tint);
    }

    /// <summary>Remap a MinMaxGradient toward the tint — alpha keys/ramp times
    /// survive, only the hue shifts.</summary>
    private static ParticleSystem.MinMaxGradient Remap(ParticleSystem.MinMaxGradient g, Color tint)
    {
        switch (g.mode)
        {
            case ParticleSystemGradientMode.Color:
                return new ParticleSystem.MinMaxGradient(Remap(g.color, tint));
            case ParticleSystemGradientMode.TwoColors:
                return new ParticleSystem.MinMaxGradient(Remap(g.colorMin, tint), Remap(g.colorMax, tint));
            case ParticleSystemGradientMode.Gradient:
                return new ParticleSystem.MinMaxGradient(Remap(g.gradient, tint));
            case ParticleSystemGradientMode.TwoGradients:
                return new ParticleSystem.MinMaxGradient(Remap(g.gradientMin, tint), Remap(g.gradientMax, tint));
            default:
                return new ParticleSystem.MinMaxGradient(tint);
        }
    }

    private static Gradient Remap(Gradient g, Color tint)
    {
        var keys = g.colorKeys;
        for (var i = 0; i < keys.Length; i++) keys[i].color = Remap(keys[i].color, tint);
        var copy = new Gradient();
        copy.SetKeys(keys, g.alphaKeys);
        copy.mode = g.mode;
        return copy;
    }

    /// <summary>Max-component "luminance" — saturated pack blues stay bright
    /// when swung to red instead of going muddy.</summary>
    private static Color Remap(Color c, Color tint)
    {
        var m = Mathf.Max(c.r, c.g, c.b);
        return new Color(tint.r * m, tint.g * m, tint.b * m, c.a);
    }

    private static float LongestLife(GameObject go)
    {
        var max = 0f;
        foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            var m = ps.main;
            max = Mathf.Max(max, m.duration + m.startLifetime.constantMax + m.startDelay.constantMax);
        }
        return Mathf.Max(max, 1f);
    }
}
