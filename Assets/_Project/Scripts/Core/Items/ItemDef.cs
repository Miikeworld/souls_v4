using UnityEngine;

/// <summary>
/// One inventory item: a stackable consumable or a key item. Assets live in
/// Assets/_Project/Items/ (authored by Setup Shop + Inventory, not by hand).
/// `itemId` is the save identity — renaming the asset must never change it.
/// </summary>
[CreateAssetMenu(menuName = "Project Restart/Item", fileName = "ItemDef")]
public sealed class ItemDef : ScriptableObject
{
    public enum Effect { Heal, Mana, Stamina, Souls, Throw }
    public enum Kind { Consumable, Key }

    public string itemId = "item";
    public string itemName = "Item";
    [Tooltip("≤7 chars — HUD quick slot.")]
    public string shortName = "ITEM";
    [Tooltip("HudArt item icon id: Vial, Starwater, Feather, Bomb, Cask, Medal (or a skill icon id).")]
    public string icon = "Gem";
    [TextArea] public string description = "";
    public Kind kind = Kind.Consumable;
    [Min(0)] public int price = 50;
    [Min(1)] public int stackMax = 99;
    public Effect effect = Effect.Heal;
    [Min(0f)] public float magnitude = 45f;
    [Tooltip("Throw items only — ArtProjectile spec (prefab, speed, damage…).")]
    public ProjectileSpec projectile;
}
