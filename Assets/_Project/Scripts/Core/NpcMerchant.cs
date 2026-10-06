using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The shopkeeper: an idle-only NPC (tool-placed Synty visual + single-state
/// controller) with an E TALK prompt that opens <see cref="ShopMenu"/>.
/// Interacts by distance like Checkpoint — no trigger collider needed.
/// Leans toward the player while they're in reach, barks when the shop opens.
/// </summary>
public sealed class NpcMerchant : MonoBehaviour
{
    [Tooltip("BUY stock — the shop sells every ItemDef listed here.")]
    public ItemDef[] stock;
    public float radius = 3.2f;
    public string merchantName = "GRAVEDIGGER";
    [TextArea] public string[] barks = {
        "Souls for wares. Fair trade, no refunds.",
        "Ah. A customer with a pulse.",
        "Buy something before the dungeon takes it all.",
    };

    private InputAction talk;
    private Transform player;
    private PlayerState playerState;
    private bool promptOwner;

    private void Awake()
    {
        talk = new InputAction("Talk", InputActionType.Button);
        talk.AddBinding("<Keyboard>/e");
        talk.AddBinding("<Gamepad>/buttonNorth");
        var loco = FindFirstObjectByType<PlayerLocomotion>();
        if (loco != null)
        {
            player = loco.transform;
            playerState = loco.GetComponent<PlayerState>();
        }
    }

    private void OnEnable() => talk.Enable();
    private void OnDisable()
    {
        talk.Disable();
        if (promptOwner) { GameHud.HidePrompt(); promptOwner = false; }
    }
    private void OnDestroy() => talk.Dispose();

    private void Update()
    {
        if (player == null || UiGates.MenuOpen || GameLoop.IsResting)
        {
            if (promptOwner) { GameHud.HidePrompt(); promptOwner = false; }
            return;
        }
        var busy = playerState != null
            && (playerState.IsDead || playerState.IsDisplacing || playerState.IsRooted);
        var near = !busy && (player.position - transform.position).sqrMagnitude <= radius * radius;
        if (!near)
        {
            if (promptOwner) { GameHud.HidePrompt(); promptOwner = false; }
            return;
        }

        // Slow shopkeeper lean — face the customer.
        var to = player.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                Quaternion.LookRotation(to), 90f * Time.deltaTime);

        promptOwner = true;
        GameHud.ShowPrompt("E", "TALK");
        if (talk.WasPressedThisFrame()) ShopMenu.Open(this);
    }

    /// <summary>One bark line, deterministic-ish per open.</summary>
    public string Bark() => barks == null || barks.Length == 0
        ? "…"
        : barks[Random.Range(0, barks.Length)];
}
