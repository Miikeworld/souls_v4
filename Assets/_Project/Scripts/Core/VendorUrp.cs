using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Patches vendor materials that predate URP at runtime: a Standard shader
/// renders magenta, so it's re-authored as URP Lit keeping its main texture;
/// an untextured mesh (the Grruzam weapons embed a textureless material)
/// gets a steel look instead. Shared between the flask bottle and the
/// weapon socket. Never touches already-URP materials.
/// </summary>
public static class VendorUrp
{
    private static Shader urpLit;
    private static Material steel;
    private static readonly Dictionary<Texture, Material> texMats = new();

    public static void FixTree(GameObject go)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true)) Fix(r);
    }

    public static void Fix(Renderer r)
    {
        var m = r.sharedMaterial;
        if (m == null || m.shader == null) return;
        var urp = m.shader.name.StartsWith("Universal Render Pipeline");
        var tex = urp
            ? (m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null)
            : (m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null);
        if (urp && tex != null) return; // already a working URP material
        // Textureless URP (an FBX-embedded import) and legacy Standard both
        // land here: keep a texture if one exists, else the steel look.
        r.sharedMaterial = tex != null ? Textured(tex) : Steel();
    }

    private static Shader UrpLit => urpLit != null ? urpLit : urpLit = Shader.Find("Universal Render Pipeline/Lit");

    private static Material Steel()
    {
        if (steel == null)
        {
            steel = new Material(UrpLit) { name = "VendorUrp_Steel" };
            steel.SetColor("_BaseColor", new Color(0.58f, 0.62f, 0.68f));
            steel.SetFloat("_Metallic", 0.9f);
            steel.SetFloat("_Smoothness", 0.72f);
        }
        return steel;
    }

    private static Material Textured(Texture t)
    {
        if (texMats.TryGetValue(t, out var m) && m != null) return m;
        m = new Material(UrpLit) { name = "VendorUrp_" + t.name };
        m.SetTexture("_BaseMap", t);
        m.SetFloat("_Smoothness", 0.35f);
        texMats[t] = m;
        return m;
    }
}
