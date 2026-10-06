using System.Collections.Generic;
using UnityEngine;

/// <summary>The weapon families the Warden can summon. <see cref="Blade"/> is the
/// procedural faceted blade (always available); the rest are Synty weapons baked
/// by Tools > Project Restart > Warden > Bake Spectral Arsenal.</summary>
public enum ArsenalKind { Blade, Sword, Greatsword, Axe, Spear, Halberd, Scythe }

/// <summary>
/// The Warden's spectral arsenal: Synty sword / greatsword / axe / spear /
/// halberd / scythe meshes, baked once in the editor into
/// Resources/WardenArsenal/&lt;Kind&gt;_&lt;n&gt; as unit-length "ghost" meshes
/// (grip at the origin, +Y to the tip at y = 1, flat side on X) carrying the
/// polygon-wire vertex colours the Souls/Afterimage shader reads. Drawn in the
/// fight's language: a faint crimson fill, a pale-red rim + wire, an ink
/// silhouette — the same banded ghost the player's dodge leaves behind, in the
/// danger colour. Anything missing falls back to the procedural blade, so a
/// fresh checkout without the bake still fights correctly.
/// </summary>
public static class WardenArsenal
{
    public static readonly Color Fill = new Color(1f, 0.05f, 0.08f, 0.34f);
    public static readonly Color Rim = new Color(1f, 0.62f, 0.6f, 1f);
    public static readonly Color InkEdge = new Color(0.05f, 0.02f, 0.035f, 1f);

    private static readonly Dictionary<ArsenalKind, Mesh[]> cache = new Dictionary<ArsenalKind, Mesh[]>();
    private static Material material;
    private static bool materialTried;

    /// <summary>Resource folder (under any Resources/) the editor bake writes to.</summary>
    public const string Folder = "WardenArsenal";

    /// <summary>Shared ghost material (Souls/Afterimage); null when the shader is missing.</summary>
    public static Material Material
    {
        get
        {
            if (material != null || materialTried) return material;
            materialTried = true;
            var shader = Shader.Find("Souls/Afterimage");
            if (shader == null) return null;
            material = new Material(shader) { name = "Warden arsenal (runtime)" };
            material.SetColor("_Tint", Fill);
            material.SetColor("_Rim", Rim);
            material.SetColor("_Ink", InkEdge);
            material.SetFloat("_Fade", 1f);
            material.SetFloat("_Dissolve", 0f);
            material.SetFloat("_Slices", 7f);
            material.SetFloat("_WireThickness", 0.7f);
            return material;
        }
    }

    /// <summary>All baked variants of a kind (empty when not baked).</summary>
    public static Mesh[] Variants(ArsenalKind kind)
    {
        if (cache.TryGetValue(kind, out var list)) return list;
        var found = new List<Mesh>();
        if (kind != ArsenalKind.Blade)
            for (var i = 0; i < 16; i++)
            {
                var m = Resources.Load<Mesh>(Folder + "/" + kind + "_" + i);
                if (m == null) break;
                found.Add(m);
            }
        list = found.ToArray();
        cache[kind] = list;
        return list;
    }

    /// <summary>True when this kind can be drawn as a baked Synty ghost.</summary>
    public static bool Has(ArsenalKind kind) => kind != ArsenalKind.Blade && Variants(kind).Length > 0 && Material != null;

    /// <summary>A baked unit mesh for <paramref name="kind"/> (variant wraps), or null.</summary>
    public static Mesh Get(ArsenalKind kind, int variant)
    {
        if (!Has(kind)) return null;
        var list = Variants(kind);
        if (variant < 0) variant = Random.Range(0, list.Length);
        return list[variant % list.Length];
    }

    /// <summary>The length a kind reads best at, for a given "size" (1 = a sword in the crown).</summary>
    public static float NaturalLength(ArsenalKind kind, float size = 1f) => size * kind switch
    {
        ArsenalKind.Greatsword => 2.3f,
        ArsenalKind.Axe => 1.5f,
        ArsenalKind.Spear => 2.6f,
        ArsenalKind.Halberd => 2.6f,
        ArsenalKind.Scythe => 2.2f,
        ArsenalKind.Sword => 1.6f,
        _ => 1.55f,
    };

    /// <summary>A crown/volley mix: swords mostly, a greatsword or axe for weight.</summary>
    public static ArsenalKind Mixed(int i) => (i % 5) switch
    {
        1 => ArsenalKind.Axe,
        3 => ArsenalKind.Greatsword,
        4 => ArsenalKind.Spear,
        _ => ArsenalKind.Sword,
    };
}
