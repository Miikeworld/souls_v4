using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// End-of-level lift/door. Locked (no prompt) until every <see cref="requires"/>
/// Health is dead — typically the area boss. Then E shows the banner and
/// loads <see cref="nextScene"/> through the Persona slab wipe.
/// </summary>
public sealed class LevelExit : MonoBehaviour
{
    [SerializeField] private Health[] requires;
    [SerializeField] private string promptText = "ASCEND";
    [SerializeField] private string banner = "AREA CLEARED";
    [SerializeField] private string nextScene = "00_MainMenu";
    [SerializeField, Min(0.5f)] private float radius = 2.8f;

    private InputAction interact;
    private Transform player;
    private PlayerState playerState;
    private bool used, promptOwner;

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

    public bool Unlocked
    {
        get
        {
            if (requires == null) return true;
            foreach (var h in requires) if (h != null && !h.IsDead) return false;
            return true;
        }
    }

    private void Update()
    {
        if (player == null || used || UiGates.MenuOpen) return;
        var busy = GameLoop.IsResting || (playerState != null && (playerState.IsDead || playerState.IsDisplacing));
        var near = !busy && Unlocked && Vector3.Distance(player.position, transform.position) <= radius;
        if (!near)
        {
            if (promptOwner) { GameHud.HidePrompt(); promptOwner = false; }
            return;
        }
        promptOwner = true;
        GameHud.ShowPrompt("E", promptText);
        if (!interact.WasPressedThisFrame()) return;
        used = true;
        promptOwner = false;
        GameHud.HidePrompt();
        GameHud.Banner(banner, 2.4f);
        Invoke(nameof(Leave), 2.6f);
    }

    private void Leave() => PersonaTransition.LoadScene(nextScene);
}
