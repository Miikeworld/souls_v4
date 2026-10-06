using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Fix Synty URP Materials.
/// One-shot converter for the vendor packs: their .mat assets still point at
/// legacy BiRP shaders (Standard, Particles/*) which render MAGENTA under
/// URP. Each gets re-authored onto the URP equivalent in place — textures,
/// colors and emission survive — so every prefab referencing them (boss,
/// level deco, FX) fixes itself without touching the prefabs.
/// Already-URP and unrecognized custom shaders are skipped; idempotent.
/// Boss/level setup call FixAll() too so a fresh checkout can't ship purple.
/// </summary>
public static class ProjectRestartUrpFix
{
    private const string SyntyRoot = "Assets/ThirdParty/Synty";

    [MenuItem("Tools/Project Restart/Fix Synty URP Materials")]
    public static void Run() => Debug.Log($"[ProjectRestart] URP material pass: {FixAll()} converted.");

    /// <summary>Re-shade every legacy material under the Synty root. Returns the count changed.</summary>
    public static int FixAll()
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        var unlit = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        var fixedCount = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { SyntyRoot }))
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (mat == null) continue;
            var shader = mat.shader != null ? mat.shader.name : "";
            if (shader.StartsWith("Universal Render Pipeline")) continue;
            var particle = shader.StartsWith("Particles/") || shader.Contains("Particle");
            var legacyLit = shader == "Standard" || shader.StartsWith("Legacy Shaders/")
                            || mat.HasProperty("_MainTex"); // error-shader fallback: textured = lit surface
            var target = particle ? unlit : lit;
            if (target == null || (!particle && !legacyLit)) continue;

            // Read BEFORE swapping — the shader change clears unmatched slots.
            var tex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            var col = LegacyColor(mat);
            var bump = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
            var emission = mat.HasProperty("_EmissionMap") ? mat.GetTexture("_EmissionMap") : null;
            var emitCol = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor") : Color.black;

            mat.shader = target;
            if (tex != null) mat.SetTexture("_BaseMap", tex);
            if (col.HasValue) mat.SetColor("_BaseColor", col.Value);
            if (!particle)
            {
                if (bump != null) mat.SetTexture("_BumpMap", bump);
                if (emission != null)
                {
                    mat.SetTexture("_EmissionMap", emission);
                    mat.SetColor("_EmissionColor", emitCol);
                    mat.EnableKeyword("_EMISSION");
                }
            }
            else
            {
                // Legacy additive/alpha → URP particle blend modes.
                var additive = shader.Contains("Additive") || shader.Contains("Add");
                mat.SetFloat("_Surface", 1f);                        // transparent
                mat.SetFloat("_Blend", additive ? 2f : 0f);          // additive : alpha
            }
            EditorUtility.SetDirty(mat);
            fixedCount++;
        }
        if (fixedCount > 0) AssetDatabase.SaveAssets();
        return fixedCount;
    }

    private static Color? LegacyColor(Material mat)
    {
        if (mat.HasProperty("_Color")) return mat.GetColor("_Color");
        if (mat.HasProperty("_TintColor")) return mat.GetColor("_TintColor");
        return null;
    }
}
