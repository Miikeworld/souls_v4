using System;
using Unity.Cinemachine;
using UnityEngine;

/// <summary>
/// Drives Dutch/roll on the active Cinemachine 3.x virtual camera when a movement
/// state displaces the player. Only responds to ICameraDisplacementEvent, which
/// keeps combat/ability rooted states from accidentally tilting the camera.
/// </summary>
[RequireComponent(typeof(PlayerState))]
[DefaultExecutionOrder(5)]
public sealed class CameraTiltController : MonoBehaviour
{
    [Header("Defaults (overridden by event values if non-zero)")]
    [SerializeField] private float defaultTiltAmount = 15f;
    [Tooltip("Smooth time for leaning into a displacement tilt. Roughly the time to reach 90% of the target.")]
    [SerializeField] private float defaultTiltInDuration = 0.25f;
    [Tooltip("Smooth time for returning the camera to level after a displacement ends.")]
    [SerializeField] private float defaultTiltOutDuration = 0.4f;
    [Tooltip("Forward pitch target in degrees when an event does not specify one. Negative = dip forward.")]
    [SerializeField] private float defaultPitchAmount = 0f;
    [SerializeField] private float defaultPitchInDuration = 0.15f;
    [SerializeField] private float defaultPitchOutDuration = 0.25f;

    [Header("Optional: explicit camera to drive (else uses active brain camera)")]
    [SerializeField] private CinemachineCamera explicitCamera;

    public CinemachineCamera ExplicitCamera
    {
        get => explicitCamera;
        set => explicitCamera = value;
    }

    private float currentTilt;
    private float currentTargetTilt;
    private float currentInDuration;
    private float currentOutDuration;
    private float tiltVelocity;
    private float currentPitch;
    private float currentTargetPitch;
    private float currentPitchInDuration;
    private float currentPitchOutDuration;
    private float pitchVelocity;
    private bool isTilting;
    private CinemachineBrain brain;

    /// <summary>Current pitch displacement in degrees (negative = dipped forward).</summary>
    public float CurrentPitch => currentPitch;

    private void Awake()
    {
        brain = FindFirstObjectByType<CinemachineBrain>();
        if (brain == null)
            Debug.LogWarning("[CameraTiltController] No CinemachineBrain found in the scene. Camera tilt will not apply.", this);
    }

    private void LateUpdate()
    {
        var tiltTarget = isTilting ? currentTargetTilt : 0f;
        var tiltSmoothTime = isTilting ? currentInDuration : currentOutDuration;
        tiltSmoothTime = Mathf.Max(0.001f, tiltSmoothTime);

        currentTilt = Mathf.SmoothDamp(currentTilt, tiltTarget, ref tiltVelocity, tiltSmoothTime);
        ApplyDutch(currentTilt);

        var pitchTarget = isTilting ? currentTargetPitch : 0f;
        var pitchSmoothTime = isTilting ? currentPitchInDuration : currentPitchOutDuration;
        pitchSmoothTime = Mathf.Max(0.001f, pitchSmoothTime);

        currentPitch = Mathf.SmoothDamp(currentPitch, pitchTarget, ref pitchVelocity, pitchSmoothTime);
    }

    /// <summary>Call when a displacement state starts (e.g., Slide, WallDash enter).</summary>
    public void BeginTilt(ICameraDisplacementEvent evt)
    {
        if (evt == null)
            throw new ArgumentNullException(nameof(evt));

        currentTargetTilt = float.IsNaN(evt.TiltAmount) ? defaultTiltAmount : evt.TiltAmount;
        currentInDuration = evt.TiltInDuration > 0.001f ? evt.TiltInDuration : defaultTiltInDuration;
        currentOutDuration = evt.TiltOutDuration > 0.001f ? evt.TiltOutDuration : defaultTiltOutDuration;

        currentTargetPitch = float.IsNaN(evt.PitchAmount) ? defaultPitchAmount : evt.PitchAmount;
        currentPitchInDuration = evt.PitchInDuration > 0.001f ? evt.PitchInDuration : defaultPitchInDuration;
        currentPitchOutDuration = evt.PitchOutDuration > 0.001f ? evt.PitchOutDuration : defaultPitchOutDuration;

        isTilting = true;
        tiltVelocity = 0f;
        pitchVelocity = 0f;
    }

    /// <summary>Call when the displacement state ends and the camera should level out.</summary>
    public void EndTilt()
    {
        isTilting = false;
        tiltVelocity = 0f;
    }

    private void ApplyDutch(float dutch)
    {
        var vcam = GetActiveVirtualCamera();
        if (vcam == null)
            return;
        var lens = vcam.Lens;
        lens.Dutch = dutch;
        vcam.Lens = lens;
    }

    private CinemachineCamera GetActiveVirtualCamera()
    {
        if (explicitCamera != null)
            return explicitCamera;
        if (brain != null && brain.ActiveVirtualCamera is CinemachineCamera vcam)
            return vcam;
        return null;
    }

    /// <summary>Reset Dutch immediately. Use on state machine reset / scene load.</summary>
    public void ResetTilt()
    {
        isTilting = false;
        currentTilt = 0f;
        currentPitch = 0f;
        tiltVelocity = 0f;
        pitchVelocity = 0f;
        ApplyDutch(0f);
    }
}
