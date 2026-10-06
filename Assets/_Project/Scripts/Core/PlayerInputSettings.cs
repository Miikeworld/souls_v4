using UnityEngine;

/// <summary>
/// Control schemes: Default (Ctrl = toggle sprint, Shift = slide/backstep,
/// Space = jump) and Alt (Shift = hold sprint, Space = slide, F = jump). The
/// choice persists via PlayerPrefs and is switched from the Persona pause menu
/// (Esc), which this component attaches at startup. Uses the New Input
/// System's runtime binding overrides — the .inputactions asset on disk stays
/// untouched.
/// </summary>
public sealed class PlayerInputSettings : MonoBehaviour
{
    public enum ControlScheme { Default, Alt }

    [SerializeField] private PlayerLocomotion locomotion;
    [SerializeField] private SlideController slide;

    private const string PrefsKey = "PlayerControlScheme";

    private ControlScheme scheme;

    public ControlScheme Scheme => scheme;

    private void Awake()
    {
        if (locomotion == null) locomotion = GetComponent<PlayerLocomotion>();
        if (slide == null) slide = GetComponent<SlideController>();
        scheme = (ControlScheme)Mathf.Clamp(PlayerPrefs.GetInt(PrefsKey, 0), 0, 1);
    }

    private void Start()
    {
        // Start (not Awake) so every component's input actions already exist —
        // binding overrides would no-op against a null action otherwise.
        Apply(scheme);
        // Arriving from the menu leaves the cursor free — lock it for play.
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        PauseMenu.Attach(this);
    }

    public void SetScheme(ControlScheme s)
    {
        scheme = s;
        PlayerPrefs.SetInt(PrefsKey, (int)s);
        PlayerPrefs.Save();
        Apply(s);
    }

    private void Apply(ControlScheme s)
    {
        if (s == ControlScheme.Alt)
        {
            slide?.SetSlideBinding("<Keyboard>/space");
            locomotion?.SetSprintMode(false);
            locomotion?.SetSprintKeyboardBinding("<Keyboard>/leftShift");
            locomotion?.SetJumpKeyboardBinding("<Keyboard>/f");
        }
        else
        {
            slide?.SetSlideBinding("<Keyboard>/leftShift");
            locomotion?.SetSprintMode(true);
            locomotion?.SetSprintKeyboardBinding("<Keyboard>/leftCtrl");
            locomotion?.SetJumpKeyboardBinding("<Keyboard>/space");
        }
        Debug.Log($"[PlayerInputSettings] Scheme {s} applied.");
    }
}
