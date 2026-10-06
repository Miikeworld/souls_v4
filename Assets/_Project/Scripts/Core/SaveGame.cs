using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Session persistence for the souls economy: wallet contents, inventory
/// stacks and owned weapon sets, serialized to persistentDataPath/save.json.
/// Written on checkpoint rest, menu closes and quit; loaded by GameLoop.
/// Unknown item ids are skipped so tool-authored assets can be renamed.
/// </summary>
public static class SaveGame
{
    private const int Version = 1;
    private static string SavePath => Path.Combine(Application.persistentDataPath, "save.json");

    [Serializable]
    private sealed class Blob
    {
        public int version;
        public int souls;
        public List<string> items = new();    // "itemId:count"
        public List<string> weapons = new();  // WeaponSet asset names
        public string equipped;               // current WeaponSet name
    }

    /// <summary>Snapshot wallet + inventory to disk.</summary>
    public static void Save(Inventory inv, WeaponSet equipped)
    {
        try
        {
            var blob = new Blob
            {
                version = Version,
                souls = SoulsWallet.Souls,
                items = inv != null ? inv.Serialize() : new List<string>(),
                weapons = WeaponNames(inv),
                equipped = equipped != null ? equipped.name : "",
            };
            File.WriteAllText(SavePath, JsonUtility.ToJson(blob));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SaveGame] save failed: " + e.Message);
        }
    }

    /// <summary>Apply the save onto wallet + inventory. Missing file = fresh
    /// run; unknown item ids and weapon names are skipped silently. The catalog
    /// is whatever ItemDef assets are loaded — merchant stock + starter items
    /// keep them referenced.</summary>
    public static void Load(Inventory inv)
    {
        try
        {
            if (!File.Exists(SavePath) || inv == null) return;
            var blob = JsonUtility.FromJson<Blob>(File.ReadAllText(SavePath));
            if (blob == null || blob.version != Version) return;
            SoulsWallet.Add(Mathf.Max(0, blob.souls)); // wallet starts empty — Add sets it
            inv.ClearAll();
            inv.Restore(blob.items, Resources.FindObjectsOfTypeAll<ItemDef>());
            inv.RestoreWeapons(blob.weapons);
            // Re-equip the saved weapon if it's still owned.
            var socket = inv.GetComponent<WeaponSocket>();
            if (socket != null && !string.IsNullOrEmpty(blob.equipped))
                for (var i = 0; i < inv.OwnedCount; i++)
                    if (inv.OwnedAt(i).name == blob.equipped) { socket.Equip(inv.OwnedAt(i)); break; }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SaveGame] load failed: " + e.Message);
        }
    }

    public static bool HasSave => File.Exists(SavePath);

    private static List<string> WeaponNames(Inventory inv)
    {
        var list = new List<string>();
        if (inv == null) return list;
        for (var i = 0; i < inv.OwnedCount; i++)
            if (inv.OwnedAt(i) != null) list.Add(inv.OwnedAt(i).name);
        return list;
    }
}
