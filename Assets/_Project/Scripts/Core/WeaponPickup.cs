using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A weapon standing in the world — E takes it, leaving your current set on the
/// stand (a self-maintaining swap pedestal: no inventory, no drop bookkeeping).
/// The player's WeaponSet owns displayName/prefab; the pickup just reflects
/// whichever set it currently holds.
/// </summary>
public sealed class WeaponPickup : MonoBehaviour
{
    [SerializeField] private WeaponSet set;
    [Tooltip("Lean applied to the displayed weapon mesh (tip-down planted look).")]
    [SerializeField] private Vector3 displayEuler = new Vector3(-60f, 0f, 0f);
    [SerializeField, Min(0.5f)] private float promptRange = 2.4f;

    private static WeaponPickup promptOwner;

    private InputAction interact;
    private Transform player;
    private WeaponSocket socket;
    private float findT;
    private GameObject shown;

    private void Awake()
    {
        interact = new InputAction("Interact", InputActionType.Button);
        interact.AddBinding("<Keyboard>/e");
        interact.AddBinding("<Gamepad>/buttonNorth");
    }

    private void OnEnable()
    {
        interact.Enable();
        Rebuild();
    }

    private void OnDisable()
    {
        interact.Disable();
        if (promptOwner == this) { GameHud.HidePrompt(); promptOwner = null; }
    }

    private void OnDestroy() => interact.Dispose();

    private void Update()
    {
        if (set == null) return;

        if ((findT -= Time.deltaTime) <= 0f || player == null)
        {
            findT = 0.5f;
            var loco = FindFirstObjectByType<PlayerLocomotion>();
            if (loco == null) return;
            player = loco.transform;
            socket = loco.GetComponent<WeaponSocket>();
        }
        if (player == null || socket == null) return;

        var near = (transform.position - player.position).sqrMagnitude <= promptRange * promptRange;
        if (!near)
        {
            if (promptOwner == this) { GameHud.HidePrompt(); promptOwner = null; }
            return;
        }

        if (promptOwner != null && promptOwner != this) return; // another pickup owns the slot
        promptOwner = this;
        GameHud.ShowPrompt("E", "TAKE " + set.displayName.ToUpperInvariant());
        if (!interact.WasPressedThisFrame()) return;

        var prev = socket.Set;
        socket.Equip(set);
        set = prev; // the stand now offers what you were carrying — swaps both ways
        Rebuild();
        GameHud.Toast((socket.Set != null ? socket.Set.displayName : "WEAPON").ToUpperInvariant() + " EQUIPPED");
        GameHud.HidePrompt();
        promptOwner = null;
    }

    private void Rebuild()
    {
        if (shown != null) Destroy(shown);
        shown = null;
        if (set == null || set.weaponPrefab == null) return;
        shown = Instantiate(set.weaponPrefab, transform);
        shown.transform.localRotation = Quaternion.Euler(displayEuler);
    }
}
