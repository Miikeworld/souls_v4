using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// The hero standing on the pedestal in the menu scene. Applies
/// CharacterBuildData live while the user edits, plays the base idle via the
/// assigned controller, and turns only when the player drags the mouse
/// (disabled while the pointer is over UI).
/// </summary>
public sealed class MenuCharacterPreview : MonoBehaviour
{
    [SerializeField] private Transform rig;
    [SerializeField] private RuntimeAnimatorController idleController;
    [SerializeField] private float autoSpinDegPerSec = 0f;
    [SerializeField] private float dragSensitivity = 0.45f;

    private float yaw;
    private bool dragging;

    public Transform Rig => rig;

    private void Awake()
    {
        if (rig == null) rig = transform;
        var anim = rig.GetComponentInChildren<Animator>(true);
        if (anim != null)
        {
            if (idleController != null) anim.runtimeAnimatorController = idleController;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
    }

    private void Start()
    {
        // Show the saved hero on the title screen — or the default build when
        // no character exists yet.
        Apply(CharacterBuildData.Load());
    }

    public void Apply(CharacterBuildData build, bool hideHeadwear = false)
    {
        HeroLibrary.Apply(rig, build, hideHeadwear);
    }

    private void Update()
    {
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame &&
            (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()))
            dragging = true;
        if (mouse == null || !mouse.leftButton.isPressed) dragging = false;

        if (dragging && mouse != null)
            yaw -= mouse.delta.ReadValue().x * dragSensitivity; // drag right = hero turns to its left
        else if (autoSpinDegPerSec != 0f)
            yaw += autoSpinDegPerSec * Time.deltaTime;
        if (rig != null) rig.rotation = Quaternion.Euler(0f, yaw, 0f);
    }
}
