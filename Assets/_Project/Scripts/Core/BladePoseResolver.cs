using UnityEngine;

/// <summary>Shared authored socket mapping for gameplay and isolated animation previews.</summary>
public static class BladePoseResolver
{
    public readonly struct SourcePose
    {
        // Rotation and hand-to-socket displacement in the source root's axes.
        // Displacement stays in metres; source and Hero skeleton scales can differ.
        public readonly Quaternion rotation;
        public readonly Vector3 handToSocket;
        public SourcePose(Quaternion rotation, Vector3 handToSocket)
        { this.rotation = rotation; this.handToSocket = handToSocket; }
    }

    public readonly struct BladePose
    {
        public readonly Vector3 position;
        public readonly Quaternion rotation;
        public BladePose(Vector3 position, Quaternion rotation)
        { this.position = position; this.rotation = rotation; }
    }

    public static SourcePose Capture(Quaternion sourceFrame, Vector3 socketPosition,
        Quaternion socketRotation, Vector3 sourceHandPosition)
    {
        var inverse = Quaternion.Inverse(sourceFrame);
        return new SourcePose(inverse * socketRotation, inverse * (socketPosition - sourceHandPosition));
    }

    public static SourcePose Blend(SourcePose outgoing, SourcePose incoming, float progress)
    {
        var weight = Mathf.Clamp01(progress);
        return new SourcePose(Quaternion.Slerp(outgoing.rotation, incoming.rotation, weight),
            Vector3.Lerp(outgoing.handToSocket, incoming.handToSocket, weight));
    }

    public static BladePose Resolve(SourcePose source, Quaternion targetFrame, Vector3 palm,
        Quaternion targetHandRotation, WeaponSet set)
    {
        var position = palm;
        var rotation = targetFrame * source.rotation;
        if (set != null)
        {
            position += targetFrame * source.handToSocket * Mathf.Clamp01(set.socketDeltaScale);
            position += targetHandRotation * set.weaponPosOffset;
            rotation *= Quaternion.Euler(set.weaponRotOffset) *
                        Quaternion.AngleAxis(set.bladeRoll, BladeAxis(set));
        }
        return new BladePose(position, rotation);
    }

    public static Vector3 Palm(Transform hand, Transform knuckle, float grip)
        => knuckle != null ? Vector3.Lerp(hand.position, knuckle.position, Mathf.Clamp01(grip)) : hand.position;

    public static Vector3 BladeAxis(WeaponSet set)
        => set != null && set.bladeAxis.sqrMagnitude > 0.001f ? set.bladeAxis.normalized : Vector3.forward;

    /// <summary>Compensates centimetre-scaled humanoid hands once, without resizing vendor rigs.</summary>
    public static Vector3 LocalScale(Vector3 worldIntent, Vector3 parentScale)
        => new Vector3(worldIntent.x / NonZero(parentScale.x), worldIntent.y / NonZero(parentScale.y),
            worldIntent.z / NonZero(parentScale.z));

    private static float NonZero(float value)
        => Mathf.Abs(value) > 0.0001f ? value : value < 0f ? -0.0001f : 0.0001f;
}
