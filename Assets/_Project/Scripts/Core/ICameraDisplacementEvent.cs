using UnityEngine;

/// <summary>
/// Payload used by movement states that displace the character and therefore
/// earn camera roll/dutch tilt. Heavy/anchored ability states must not implement
/// or fire this event.
/// </summary>
public interface ICameraDisplacementEvent
{
    float Magnitude { get; }
    float TiltAmount { get; }
    float TiltInDuration { get; }
    float TiltOutDuration { get; }
    AnimationCurve TiltCurve { get; }

    float PitchAmount { get; }
    float PitchInDuration { get; }
    float PitchOutDuration { get; }
}
