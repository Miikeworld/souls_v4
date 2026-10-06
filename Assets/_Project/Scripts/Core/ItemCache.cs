using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// One-time loot spot (crate, corpse, rack): E grants <see cref="count"/> ×
/// <see cref="item"/> into the player's <see cref="Inventory"/> and toasts it.
/// The marker ember dims once looted. Session-scoped like the level's gates.
/// </summary>
public sealed class ItemCache : MonoBehaviour
{
    [SerializeField] private ItemDef item;
    [SerializeField, Min(1)] private int count = 1;
    [SerializeField] private string promptText = "SEARCH";
    [SerializeField, Min(0.5f)] private float radius = 2.2f;
    [Tooltip("Optional glow (light / ember) switched off once looted.")]
    [SerializeField] private GameObject marker;

    private InputAction interact;
    private Transform player;
    private PlayerState playerState;
    private bool looted, promptOwner;

    private void Awake()
    {
        interact = new InputAction("Interact", InputActionType.Button);
        interact.AddBinding("<Keyboard>/e");
        interact.AddBinding("<Gamepad>/buttonNorth");
        var loco = FindFirstObjectByType<PlayerLocomotion>();
        if (loco != null)
        {
            player = loco.transform;
            playerState = loco.GetComponent<PlayerState>();
        }
    }

    private void OnEnable() => interact.Enable();
    private void OnDisable()
    {
        interact.Disable();
        if (promptOwner) { GameHud.HidePrompt(); promptOwner = false; }
    }
    private void OnDestroy() => interact.Dispose();

    private void Update()
    {
        if (looted || player == null || item == null || UiGates.MenuOpen) return;
        var busy = GameLoop.IsResting || (playerState != null && (playerState.IsDead || playerState.IsDisplacing || playerState.IsRooted));
        var near = !busy && Vector3.Distance(player.position, transform.position) <= radius;
        if (!near)
        {
            if (promptOwner) { GameHud.HidePrompt(); promptOwner = false; }
            return;
        }
        promptOwner = true;
        GameHud.ShowPrompt("E", promptText);
        if (!interact.WasPressedThisFrame()) return;
        var inv = player.GetComponent<Inventory>();
        if (inv == null) inv = player.gameObject.AddComponent<Inventory>();
        inv.Add(item, count);
        looted = true;
        promptOwner = false;
        GameHud.HidePrompt();
        GameHud.Toast(count > 1 ? $"{item.shortName} ×{count}" : item.shortName);
        if (marker != null) marker.SetActive(false);
    }
}
