using System;
using UnityEngine;

/// <summary>
/// Concrete event payload for <see cref="ICameraDisplacementEvent"/>.
/// </summary>
[Serializable]
public sealed class CameraDisplacementEvent : ICameraDisplacementEvent
{
    [field: SerializeField]
    [field: Tooltip("0-1 normalized strength of the displacement; not used by the camera itself but useful for debug/logic.")]
    public float Magnitude { get; set; }

    [field: SerializeField]
    [field: Tooltip("Dutch/roll target in degrees. CameraTiltController uses this if nonzero, otherwise its own default.")]
    public float TiltAmount { get; set; }

    [field: SerializeField]
    [field: Tooltip("Time in seconds to ease in to the tilt.")]
    public float TiltInDuration { get; set; }

    [field: SerializeField]
    [field: Tooltip("Time in seconds to ease out after the displacement ends.")]
    public float TiltOutDuration { get; set; }

    [field: SerializeField]
    [field: Tooltip("Optional ease curve. Null or empty uses CameraTiltController's default.")]
    public AnimationCurve TiltCurve { get; set; }

    [field: SerializeField]
    [field: Tooltip("Forward/backward pitch target in degrees. Negative = dip forward. Not used by CameraTiltController directly; PlayerCameraController adds this to the orbit pitch.")]
    public float PitchAmount { get; set; }

    [field: SerializeField]
    [field: Tooltip("Time in seconds to ease in to the pitch displacement.")]
    public float PitchInDuration { get; set; }

    [field: SerializeField]
    [field: Tooltip("Time in seconds to ease out after the displacement ends.")]
    public float PitchOutDuration { get; set; }

    public CameraDisplacementEvent(float magnitude = 1f, float tiltAmount = float.NaN, float tiltInDuration = 0f, float tiltOutDuration = 0f, AnimationCurve tiltCurve = null,
        float pitchAmount = float.NaN, float pitchInDuration = 0f, float pitchOutDuration = 0f)
    {
        Magnitude = magnitude;
        TiltAmount = tiltAmount;
        TiltInDuration = tiltInDuration;
        TiltOutDuration = tiltOutDuration;
        TiltCurve = tiltCurve;
        PitchAmount = pitchAmount;
        PitchInDuration = pitchInDuration;
        PitchOutDuration = pitchOutDuration;
    }
}
