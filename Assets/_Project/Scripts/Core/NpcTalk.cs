using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Talkable NPC / readable inscription. E within radius opens a dialogue card
/// (speaker + line, Persona card on the HUD world layer); E advances; walking
/// away, menus, rest or death close it. After the first conversation the
/// <see cref="repeatLines"/> play instead (falls back to the full set).
/// Same prompt plumbing as Checkpoint/LeverGate. Placeholder Synty models are
/// re-shaded to URP at runtime (<see cref="VendorUrp"/>) — vendor materials
/// stay untouched.
/// </summary>
public sealed class NpcTalk : MonoBehaviour
{
    [SerializeField] private string speaker = "STRANGER";
    [SerializeField] private string promptText = "TALK";
    [SerializeField, TextArea(2, 4)] private string[] lines = { "..." };
    [SerializeField, TextArea(2, 4)] private string[] repeatLines;
    [SerializeField, Min(0.5f)] private float radius = 2.6f;
    [Tooltip("Turn the body toward the player while talking (off for seated NPCs).")]
    [SerializeField] private bool facePlayer = true;
    [Tooltip("Model root to re-shade (legacy Standard → URP). Null = this object.")]
    [SerializeField] private GameObject model;

    private static NpcTalk promptOwner, speaking;
    private InputAction interact;
    private Transform player;
    private PlayerState playerState;
    private RectTransform card;
    private Text nameText, lineText;
    private CanvasGroup fade;
    private string[] active;
    private int index = -1;
    private bool talkedOnce;
    private float typeT, shownChars;
    private Quaternion restRotation;

    public bool Talking => index >= 0;

    private void Awake()
    {
        interact = new InputAction("Interact", InputActionType.Button);
        interact.AddBinding("<Keyboard>/e");
        interact.AddBinding("<Gamepad>/buttonNorth");
        restRotation = transform.rotation;
        VendorUrp.FixTree(model != null ? model : gameObject);
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
        Close();
        if (promptOwner == this) { GameHud.HidePrompt(); promptOwner = null; }
    }
    private void OnDestroy() => interact.Dispose();

    private void Update()
    {
        if (player == null) return;
        var busy = UiGates.MenuOpen || GameLoop.IsResting
                   || (playerState != null && (playerState.IsDead || playerState.IsDisplacing || playerState.IsRooted));
        var near = Vector3.Distance(player.position, transform.position) <= radius;
        if (busy || !near)
        {
            Close();
            if (promptOwner == this) { GameHud.HidePrompt(); promptOwner = null; }
            Turn(restRotation);
            return;
        }

        if (Talking)
        {
            TickCard();
            if (facePlayer) Turn(Quaternion.LookRotation(Flat(player.position - transform.position), Vector3.up));
            if (interact.WasPressedThisFrame())
            {
                // First press completes a typing line; the next advances.
                if (shownChars < active[index].Length) shownChars = active[index].Length;
                else Advance();
            }
            return;
        }

        if (speaking != null && speaking != this) return;
        promptOwner = this;
        GameHud.ShowPrompt("E", promptText);
        if (!interact.WasPressedThisFrame()) return;
        GameHud.HidePrompt();
        promptOwner = null;
        Open();
    }

    private void Open()
    {
        active = talkedOnce && repeatLines != null && repeatLines.Length > 0 ? repeatLines : lines;
        if (active == null || active.Length == 0) return;
        speaking = this;
        index = 0;
        shownChars = 0f;
        BuildCard();
    }

    private void Advance()
    {
        index++;
        shownChars = 0f;
        if (index >= active.Length) { talkedOnce = true; Close(); }
    }

    private void Close()
    {
        if (index < 0 && card == null) return;
        index = -1;
        if (speaking == this) speaking = null;
        if (card != null) Destroy(card.gameObject);
        card = null; nameText = null; lineText = null; fade = null;
    }

    private void BuildCard()
    {
        if (card != null) Destroy(card.gameObject);
        card = PersonaUi.Card(GameHud.WorldLayer, "Dialogue", new Vector2(0.5f, 0f), new Vector2(0f, 150f), new Vector2(860f, 132f));
        fade = card.gameObject.AddComponent<CanvasGroup>();
        fade.blocksRaycasts = false;
        fade.alpha = 0f;
        nameText = PersonaUi.Label(card, "Speaker", speaker, 22, PersonaUi.Heart, TextAnchor.UpperLeft, true, 3f);
        nameText.rectTransform.offsetMin = new Vector2(40f, 0f);
        nameText.rectTransform.offsetMax = new Vector2(-24f, -14f);
        lineText = PersonaUi.Label(card, "Line", "", 20, PersonaUi.Bone, TextAnchor.UpperLeft, false, 2f);
        lineText.horizontalOverflow = HorizontalWrapMode.Wrap;
        lineText.rectTransform.offsetMin = new Vector2(40f, 18f);
        lineText.rectTransform.offsetMax = new Vector2(-32f, -46f);
        var hint = PersonaUi.Label(card, "Next", "E  >", 16, PersonaUi.Copper, TextAnchor.LowerRight, true, 2f);
        hint.rectTransform.offsetMin = new Vector2(0f, 10f);
        hint.rectTransform.offsetMax = new Vector2(-22f, 0f);
    }

    private void TickCard()
    {
        if (card == null || index < 0) return;
        var dt = Time.unscaledDeltaTime;
        fade.alpha = Mathf.MoveTowards(fade.alpha, 1f, dt * 6f);
        var line = active[index];
        shownChars = Mathf.Min(line.Length, shownChars + dt * 55f);
        lineText.text = line.Substring(0, Mathf.FloorToInt(shownChars));
    }

    private void Turn(Quaternion target)
    {
        if (!facePlayer) return;
        transform.rotation = Quaternion.RotateTowards(transform.rotation, target, 240f * Time.deltaTime);
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 0.0001f ? Vector3.forward : v;
    }
}
