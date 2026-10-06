/// <summary>
/// One gate for "a full-screen menu owns input right now". Gameplay
/// listeners early-out on <see cref="MenuOpen"/> so adding a menu never
/// means touching every controller. Menus that sit on top of others
/// (PauseMenu's Esc) keep their own explicit checks — MenuOpen includes
/// IsPaused, so pause can't test it against itself.
/// </summary>
public static class UiGates
{
    public static bool MenuOpen =>
        PauseMenu.IsPaused || CheckpointMenu.IsOpen || InventoryMenu.IsOpen || ShopMenu.IsOpen;
}
