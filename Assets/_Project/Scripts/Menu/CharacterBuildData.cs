using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Everything character creation chose, serialized to JSON at
/// persistentDataPath/character.json. MenuCharacterPreview applies it live in
/// the menu; PlayerCustomizer applies it to the player when the game scene
/// loads (including Continue).
/// </summary>
[Serializable]
public sealed class CharacterBuildData
{
    public string characterName = "Adventurer";
    public bool female = true;
    // Point-buy stats (CharacterCatalog.StatRules). Old saves load as 10/10/10.
    public int vigor = 10, endurance = 10, mind = 10;
    // Appearance indices are the numeric suffix of the Chr_* part name
    // (Chr_Head_No_Elements_Female_03 → 3). 0-based values map to real parts.
    public int headIndex;
    public int hairIndex = 1;   // 0 = shaven (no hair mesh); else ordinal into Chr_Hair_*
    public int hairColorIndex;
    public int eyebrowIndex;    // ordinal into the gender's eyebrow variants
    public int facialHairIndex; // 0 = clean-shaven (male only)
    public int skinIndex;
    public int attireIndex;     // index into CharacterCatalog.Attires
    public int clothColorIndex;
    public int eyeColorIndex;

    // Per-piece armour (ordinals into the gendered Chr_* variant lists). Seeded
    // from the chosen outfit preset, then tweakable piece by piece. Saves from
    // before these fields existed have partsSeeded = false and get seeded on
    // first use, so they keep looking exactly like their outfit.
    public bool partsSeeded;
    public int torso, hips, legL, legR, armUpL, armUpR, armLowL, armLowR, handL, handR;
    // Optional slots: -1 = none.
    public int headwear = -1;   // helmets, then hoods, then masks (one combined list)
    public int crest = -1, back = -1, shoulderL = -1, shoulderR = -1;
    public int elbowL = -1, elbowR = -1, kneeL = -1, kneeR = -1, hipsAttach = -1;
    public int ears;
    // Extra shader colour channels (indices into CharacterCatalog palettes).
    public int trimColorIndex, leatherColorIndex, metalColorIndex, paintColorIndex;

    /// <summary>Copy the outfit preset into the per-piece slots.</summary>
    public void SeedFromAttire()
    {
        var a = CharacterCatalog.Attires[Mathf.Clamp(attireIndex, 0, CharacterCatalog.Attires.Length - 1)];
        torso = a.torso; hips = a.hips; legL = a.legL; legR = a.legR;
        armUpL = a.armUpL; armUpR = a.armUpR; armLowL = a.armLowL; armLowR = a.armLowR;
        handL = a.handL; handR = a.handR;
        back = a.back; shoulderL = a.shoulderL; shoulderR = a.shoulderR;
        elbowL = a.elbowL; elbowR = a.elbowR; kneeL = a.kneeL; kneeR = a.kneeR;
        hipsAttach = a.hipsAttach;
        headwear = a.headCovering; // helmets lead the combined headwear list
        crest = -1;
        partsSeeded = true;
    }

    public void EnsureParts()
    {
        if (!partsSeeded) SeedFromAttire();
    }

    private static string SavePath => Path.Combine(Application.persistentDataPath, "character.json");

    public static bool HasSave => File.Exists(SavePath);

    public void Save()
    {
        try
        {
            File.WriteAllText(SavePath, JsonUtility.ToJson(this));
            Debug.Log("[CharacterBuild] saved to " + SavePath);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CharacterBuild] save failed: " + e.Message);
        }
    }

    public static CharacterBuildData Load()
    {
        try
        {
            if (File.Exists(SavePath))
            {
                var d = JsonUtility.FromJson<CharacterBuildData>(File.ReadAllText(SavePath));
                if (d != null) return d;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CharacterBuild] load failed: " + e.Message);
        }
        return new CharacterBuildData();
    }
}
