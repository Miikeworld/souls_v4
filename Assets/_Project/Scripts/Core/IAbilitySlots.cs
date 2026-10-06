/// <summary>
/// The three quick-slots the HUD and the checkpoint ARTS page display: a
/// library of abilities, three slots pointing into it, one selected. Whatever
/// the player's ability system is (spells today, weapon arts next) implements
/// this, so the UI never changes when the system underneath does.
/// </summary>
public interface IAbilitySlots
{
    int SlotCount { get; }
    int Selected { get; }
    string SlotName(int slot);      // short, ≤7 chars — fits a slot frame
    string SlotFullName(int slot);
    float SlotCost(int slot);
    string SlotIcon(int slot);      // HudArt skill icon id ("" = resolve by name)
    int LibraryCount { get; }
    string LibraryName(int index);
    float LibraryCost(int index);
    void Attune(int slot, int libraryIndex);
    event System.Action Changed;
}
