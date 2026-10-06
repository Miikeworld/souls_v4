using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Pull-lever: E within radius → the linked GateDoor opens (latched) and the
/// lever arm snaps forward. Same prompt plumbing as Checkpoint — self-owned
/// InputAction, GameHud prompt, distance-gated.
/// </summary>
public sealed class LeverGate : MonoBehaviour
{
    [SerializeField] private GateDoor gate;
    [Tooltip("Lever arm that tips forward on pull — optional visual.")]
    [SerializeField] private Transform arm;
    [SerializeField, Min(0.5f)] private float radius = 2.2f;

    private InputAction interact;
    private Transform player;
    private PlayerState playerState;
    private bool pulled, promptOwner;
    private float armT;

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
        if (armT < 1f)
        {
            armT = Mathf.Min(1f, armT + Time.deltaTime * 3f);
            if (arm != null) arm.localRotation = Quaternion.Euler(armT * 55f, 0f, 0f);
        }
        if (player == null || UiGates.MenuOpen) return;
        var busy = pulled || GameLoop.IsResting
                   || (playerState != null && (playerState.IsDead || playerState.IsDisplacing || playerState.IsRooted));
        var near = !busy && Vector3.Distance(player.position, transform.position) <= radius;
        if (!near)
        {
            if (promptOwner) { GameHud.HidePrompt(); promptOwner = false; }
            return;
        }
        promptOwner = true;
        GameHud.ShowPrompt("E", "PULL");
        if (!interact.WasPressedThisFrame()) return;
        pulled = true;
        promptOwner = false;
        GameHud.HidePrompt();
        if (gate != null) gate.Open();
    }
}
