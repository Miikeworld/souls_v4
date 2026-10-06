using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Applies a CharacterBuildData to a Fantasy Hero modular rig — the prefab
/// ships every Chr_* variant as (mostly disabled) children, so a "character"
/// is a selection of enabled parts. We toggle GameObjects by name pattern and
/// write tint colors through MaterialPropertyBlock so the shared FantasyHero
/// material asset is never modified.
///
/// All part indices in the build are ORDINALS into the sorted variant list —
/// robust against gaps in the pack's numbering. Negative ordinal = none.
/// </summary>
public static class HeroLibrary
{
    // Covering families, in the order the creator's combined Headwear row lists
    // them: full helmets (hide hair), hoods (keep hair), masks (hide beard).
    public const string HelmetPrefix = "Chr_HeadCoverings_No_Hair_";
    public const string HoodPrefix = "Chr_HeadCoverings_Base_Hair_";
    public const string MaskPrefix = "Chr_HeadCoverings_No_FacialHair_";

    /// <param name="hideHeadwear">Preview override: force the covering slot off
    /// so face/hair edits are visible even while a helm is equipped.</param>
    public static void Apply(Transform root, CharacterBuildData b, bool hideHeadwear = false)
    {
        if (root == null || b == null) return;
        b.EnsureParts();

        // Head: the Chr_Head_(Male|Female)_NN ("All Elements") family carries
        // the actual faces — brows/beard/hair still layer on top, exactly as
        // the pack's own CharacterRandomizer does. Chr_Head_No_Elements_* is a
        // blank featureless base (reads as a mask/helmet) and stays disabled.
        DisableMatching(root, @"^Chr_Head_No_Elements_(Male|Female)_\d+$");
        SelectGendered(root, "Chr_Head_", b.female, b.headIndex);
        SelectPart(root, "Chr_Ear_Ear_", b.ears);

        // Resolve the combined headwear index into family + ordinal.
        var helmets = PartCount(root, HelmetPrefix);
        var hoods = PartCount(root, HoodPrefix);
        string hwPrefix = null;
        var hwOrd = -1;
        var h = hideHeadwear ? -1 : b.headwear;
        if (h >= 0 && h < helmets) { hwPrefix = HelmetPrefix; hwOrd = h; }
        else if (h >= helmets && h < helmets + hoods) { hwPrefix = HoodPrefix; hwOrd = h - helmets; }
        else if (h >= helmets + hoods) { hwPrefix = MaskPrefix; hwOrd = h - helmets - hoods; }
        var hidesHair = hwPrefix == HelmetPrefix;
        var hidesFace = hwPrefix == MaskPrefix;

        DisableMatching(root, @"^Chr_HeadCoverings_(Base_Hair|No_FacialHair|No_Hair)_\d+$");
        if (hwPrefix != null) SelectPart(root, hwPrefix, hwOrd);

        SelectGendered(root, "Chr_Torso_", b.female, b.torso);
        SelectGendered(root, "Chr_Hips_", b.female, b.hips);
        SelectGendered(root, "Chr_LegLeft_", b.female, b.legL);
        SelectGendered(root, "Chr_LegRight_", b.female, b.legR);
        SelectGendered(root, "Chr_ArmUpperLeft_", b.female, b.armUpL);
        SelectGendered(root, "Chr_ArmUpperRight_", b.female, b.armUpR);
        SelectGendered(root, "Chr_ArmLowerLeft_", b.female, b.armLowL);
        SelectGendered(root, "Chr_ArmLowerRight_", b.female, b.armLowR);
        SelectGendered(root, "Chr_HandLeft_", b.female, b.handL);
        SelectGendered(root, "Chr_HandRight_", b.female, b.handR);

        // Eyebrows: male/female live under different prefixes in this pack.
        DisableMatching(root, @"^Chr_Eyebrow_Male_\d+$");
        DisableMatching(root, @"^Chr_Female_Eyebrow_\d+$");
        if (b.female) SelectPart(root, "Chr_Female_Eyebrow_", b.eyebrowIndex);
        else SelectPart(root, "Chr_Eyebrow_Male_", b.eyebrowIndex);

        // Facial hair is male-only in this pack; a mask hides it too.
        DisableMatching(root, @"^Chr_FacialHair_Male_\d+$");
        if (!b.female && !hidesFace && b.facialHairIndex > 0)
            SelectPart(root, "Chr_FacialHair_Male_", b.facialHairIndex - 1);

        // Hair: ordinal 0 = shaven (all Chr_Hair_* off); a helmet hides it.
        if (b.hairIndex <= 0 || hidesHair) DisableMatching(root, @"^Chr_Hair_\d+$");
        else SelectPart(root, "Chr_Hair_", b.hairIndex - 1);

        // Optional attachments (ordinal -1 = slot empty).
        SelectPart(root, "Chr_BackAttachment_", b.back);
        SelectPart(root, "Chr_ShoulderAttachLeft_", b.shoulderL);
        SelectPart(root, "Chr_ShoulderAttachRight_", b.shoulderR);
        SelectPart(root, "Chr_ElbowAttachLeft_", b.elbowL);
        SelectPart(root, "Chr_ElbowAttachRight_", b.elbowR);
        SelectPart(root, "Chr_KneeAttachLeft_", b.kneeL);
        SelectPart(root, "Chr_KneeAttachRight_", b.kneeR);
        SelectPart(root, "Chr_HipsAttachment_", b.hipsAttach);
        SelectPart(root, "Chr_HelmetAttachment_", b.crest);

        ApplyColors(root, b);
    }

    /// <summary>How many variants of a part exist on this rig (for UI ranges).</summary>
    public static int PartCount(Transform root, string prefix)
    {
        if (root == null) return 0;
        var rx = new Regex("^" + Regex.Escape(prefix) + @"\d+$");
        var n = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (rx.IsMatch(t.name)) n++;
        return n;
    }

    public static int GenderedPartCount(Transform root, string category, bool female)
    {
        return PartCount(root, category + (female ? "Female_" : "Male_"));
    }

    /// <summary>Disable every variant of a gendered category, then enable the
    /// ordinal-th variant of the chosen gender. ordinal &lt; 0 = none.</summary>
    private static void SelectGendered(Transform root, string category, bool female, int ordinal)
    {
        DisableMatching(root, "^" + Regex.Escape(category) + @"(Male|Female)_\d+$");
        if (ordinal < 0) return;
        SelectPart(root, category + (female ? "Female_" : "Male_"), ordinal);
    }

    /// <summary>Disable all parts matching prefix+NN, enable the ordinal-th.
    /// ordinal &lt; 0 leaves everything disabled.</summary>
    private static void SelectPart(Transform root, string prefix, int ordinal)
    {
        var list = Variants(root, prefix);
        foreach (var v in list) v.gameObject.SetActive(false);
        if (ordinal < 0 || list.Count == 0) return;
        var chosen = list[Mathf.Clamp(ordinal, 0, list.Count - 1)];
        // If the variant sits under a disabled group, wake its ancestors —
        // siblings keep their own inactive flags.
        for (var p = chosen.parent; p != null && p != root && p != root.root; p = p.parent)
            if (!p.gameObject.activeSelf) p.gameObject.SetActive(true);
        chosen.gameObject.SetActive(true);
    }

    private static void DisableMatching(Transform root, string pattern)
    {
        var rx = new Regex(pattern);
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (rx.IsMatch(t.name)) t.gameObject.SetActive(false);
    }

    /// <summary>All children named prefix+NN, sorted by NN.</summary>
    private static List<Transform> Variants(Transform root, string prefix)
    {
        var rx = new Regex("^" + Regex.Escape(prefix) + @"(\d+)$");
        var found = new List<(int num, Transform t)>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            var m = rx.Match(t.name);
            if (m.Success) found.Add((int.Parse(m.Groups[1].Value), t));
        }
        found.Sort((a, b) => a.num.CompareTo(b.num));
        var list = new List<Transform>(found.Count);
        foreach (var f in found) list.Add(f.t);
        return list;
    }

    private static void ApplyColors(Transform root, CharacterBuildData b)
    {
        var skin = Pick(CharacterCatalog.SkinTones, b.skinIndex);
        var hair = Pick(CharacterCatalog.HairColors, b.hairColorIndex);
        var eyes = Pick(CharacterCatalog.EyeColors, b.eyeColorIndex);
        var cloth = Pick(CharacterCatalog.ClothColors, b.clothColorIndex);
        var trim = Pick(CharacterCatalog.TrimColors, b.trimColorIndex);
        var leather = Pick(CharacterCatalog.LeatherColors, b.leatherColorIndex);
        var metal = Pick(CharacterCatalog.MetalColors, b.metalColorIndex);
        var paint = Pick(CharacterCatalog.PaintColors, b.paintColorIndex);

        var mpb = new MaterialPropertyBlock();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_Color_Skin", skin);
            mpb.SetColor("_Color_Hair", hair);
            // Stubble is a texture channel baked into the head, not a mesh
            // part — tinting it skin-coloured is the only way "Clean" works.
            mpb.SetColor("_Color_Stubble", !b.female && b.facialHairIndex > 0 ? hair : skin);
            mpb.SetColor("_Color_Eyes", eyes);
            mpb.SetColor("_Color_Primary", cloth);
            mpb.SetColor("_Color_Secondary", trim);
            mpb.SetColor("_Color_Leather_Primary", leather);
            mpb.SetColor("_Color_Leather_Secondary", Color.Lerp(leather, Color.black, 0.3f));
            mpb.SetColor("_Color_Metal_Primary", metal);
            mpb.SetColor("_Color_Metal_Secondary", Color.Lerp(metal, Color.black, 0.3f));
            mpb.SetColor("_Color_Metal_Dark", Color.Lerp(metal, Color.black, 0.55f));
            mpb.SetColor("_Color_BodyArt", paint);
            r.SetPropertyBlock(mpb);
        }
    }

    private static Color Pick(Color[] palette, int index)
    {
        if (palette.Length == 0) return Color.white;
        return palette[Mathf.Clamp(index, 0, palette.Length - 1)];
    }
}
