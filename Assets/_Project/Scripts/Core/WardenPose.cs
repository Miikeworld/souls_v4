using UnityEngine;

/// <summary>
/// Signature silhouettes for the Warden, layered over whatever clip is playing —
/// added at runtime by <see cref="BossLord"/> on the humanoid Animator's object
/// (OnAnimatorIK only fires there; the BossLordBase layer has its IK pass on).
/// Each big move is recognisable before any VFX appears:
///   Overhead   — Worldsplitter / Executioner: both hands high, the blade straight up;
///   Planted    — Crimson Flood / Twin Rupture / Grave of Kings: blade driven
///                point-down into the floor, both hands on the hilt;
///   RaiseHand  — Crown of Blades / Judgment: the empty left hand lifted high
///                while the blades form behind him;
///   Drag       — Ruinous Sweep: the greatsword trailing low behind him, tip on stone;
///   Thrust     — King's Spear: blade drawn back at the hip, level, aimed;
///   Coil       — Cyclone / sweeps: torso wound away from the swing.
/// Hand goals are placed so the SWORD lands where the pose wants it (the hand →
/// grip offset and the goal→bone rotation offset are measured live), then a small
/// spine lean/twist is added after the pose, pre-compensated so the hands stay put.
/// Poses blend in and out; with weight 0 the clip plays untouched.
/// </summary>
[DefaultExecutionOrder(60)]
public sealed class WardenPose : MonoBehaviour
{
    public enum Kind { None, Overhead, Planted, RaiseHand, Drag, Thrust, Coil }

    private Animator anim;
    private Transform frame;           // the boss root — poses are authored in its space
    private Transform handR, handL, spine, head, sword;
    private Kind kind;
    private float weight, target, rate = 6f;
    private Vector3 plantPoint;
    private float headLocalY = 1.6f;
    private Quaternion goalToBoneR = Quaternion.identity;
    private bool goalSampled, offsetKnown, goalKnown;
    private Quaternion sampledGoalR;
    private Vector3 gripInHand;        // sword pivot in the hand bone's local frame (world-scaled)
    private Quaternion swordInHand = Quaternion.identity;
    private float lean, twist;         // degrees, applied after the pose (spine)
    private Vector3 leanPivot;

    public Kind Current => weight > 0.01f ? kind : Kind.None;
    public float Weight => weight;

    public void Init(Animator a, Transform root)
    {
        anim = a;
        frame = root != null ? root : transform;
        if (anim == null || !anim.isHuman) return;
        handR = anim.GetBoneTransform(HumanBodyBones.RightHand);
        handL = anim.GetBoneTransform(HumanBodyBones.LeftHand);
        spine = anim.GetBoneTransform(HumanBodyBones.Spine);
        head = anim.GetBoneTransform(HumanBodyBones.Head);
        if (head != null) headLocalY = Mathf.Max(0.5f, transform.InverseTransformPoint(head.position).y);
    }

    /// <summary>The weapon in the right hand (its pivot is the grip, +Y the blade).</summary>
    public void SetSword(Transform s)
    {
        sword = s;
        // A child of the hand keeps the same hand-local grip in every pose, so it can be read now.
        if (s != null) LearnGrip();
    }

    /// <summary>Where the sword's grip sits in the hand. gripInHand is world-sized (a
    /// rotation-only inverse), so it is compared with the RIG's scale — never multiplied
    /// by the hand bone's ×100 lossy scale. A grip that isn't in the fist (a bad seat)
    /// leaves the offset unknown: the hand-at-grip branch drives the pose instead of an
    /// IK goal yanked toward a far point.</summary>
    private void LearnGrip()
    {
        if (sword == null || handR == null || sword.parent != handR) return;
        var g = Quaternion.Inverse(handR.rotation) * (sword.position - handR.position);
        offsetKnown = g.magnitude < 0.4f * Mathf.Max(0.01f, anim.transform.lossyScale.x);
        if (!offsetKnown) return;
        gripInHand = g;
        swordInHand = Quaternion.Inverse(handR.rotation) * sword.rotation;
    }

    /// <summary>Blend into <paramref name="k"/> over <paramref name="blend"/> seconds.</summary>
    public void Set(Kind k, float blend = 0.25f)
    {
        if (k == Kind.None) { Clear(blend); return; }
        // Pose to pose keeps the current weight and swaps targets.
        kind = k;
        target = 1f;
        rate = 1f / Mathf.Max(0.04f, blend);
    }

    /// <summary>Planted pose: where the blade's point goes into the floor.</summary>
    public void Plant(Vector3 floorPoint, float blend = 0.25f)
    {
        plantPoint = floorPoint;
        Set(Kind.Planted, blend);
    }

    public void Clear(float blend = 0.25f)
    {
        target = 0f;
        rate = 1f / Mathf.Max(0.04f, blend);
    }

    /// <summary>Snap off (resets, deaths).</summary>
    public void Kill()
    {
        target = weight = 0f;
        kind = Kind.None;
    }

    private float H => headLocalY * transform.lossyScale.y / 0.92f;

    private Vector3 Local(float x, float y, float z)
    {
        var h = H;
        return frame.TransformPoint(Vector3.zero) + frame.rotation * new Vector3(x * h, y * h, z * h);
    }

    private Vector3 Dir(float x, float y, float z) => (frame.rotation * new Vector3(x, y, z)).normalized;

    private void Update() => weight = Mathf.MoveTowards(weight, target, rate * Time.deltaTime);

    private void OnAnimatorIK(int layerIndex)
    {
        if (anim == null || handR == null) return;
        if (weight <= 0.001f || kind == Kind.None)
        {
            Zero();
            // Sample the animated goal so LateUpdate can learn goal→bone.
            sampledGoalR = anim.GetIKRotation(AvatarIKGoal.RightHand);
            goalSampled = true;
            return;
        }
        goalSampled = false;
        var w = Mathf.SmoothStep(0f, 1f, weight);
        PoseTargets(out var swordDir, out var grip, out var wR, out var leftPos, out var wL, out var elbowR, out var elbowL,
                    out var leanDeg, out var twistDeg);
        lean = leanDeg * w;
        twist = twistDeg * w;
        // Pre-compensate the spine rotation LateUpdate adds, so the hands land on target.
        leanPivot = spine != null ? spine.position : frame.position + Vector3.up * H * 0.5f;
        var inv = Quaternion.Inverse(LeanRotation());
        Vector3 Comp(Vector3 p) => leanPivot + inv * (p - leanPivot);

        if (wR > 0f && sword != null && offsetKnown && goalKnown)
        {
            // Sword world rotation: blade along swordDir, flat facing across the body.
            var across = Vector3.Cross(swordDir, frame.forward);
            if (across.sqrMagnitude < 0.01f) across = frame.right;
            var swordRot = Quaternion.LookRotation(across.normalized, swordDir);
            var handRot = swordRot * Quaternion.Inverse(swordInHand);
            var handPos = grip - handRot * gripInHand;
            anim.SetIKPositionWeight(AvatarIKGoal.RightHand, w * wR);
            anim.SetIKRotationWeight(AvatarIKGoal.RightHand, w * wR);
            anim.SetIKPosition(AvatarIKGoal.RightHand, Comp(handPos));
            anim.SetIKRotation(AvatarIKGoal.RightHand, inv * handRot * Quaternion.Inverse(goalToBoneR));
            anim.SetIKHintPositionWeight(AvatarIKHint.RightElbow, w * wR * 0.7f);
            anim.SetIKHintPosition(AvatarIKHint.RightElbow, Comp(elbowR));
        }
        else if (wR > 0f)
        {
            // No sword measured yet: place the hand itself at the grip.
            anim.SetIKPositionWeight(AvatarIKGoal.RightHand, w * wR);
            anim.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
            anim.SetIKPosition(AvatarIKGoal.RightHand, Comp(grip));
        }
        else
        {
            anim.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
            anim.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
            anim.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 0f);
        }
        if (wL > 0f)
        {
            anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, w * wL);
            anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0f);
            anim.SetIKPosition(AvatarIKGoal.LeftHand, Comp(leftPos));
            anim.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, w * wL * 0.7f);
            anim.SetIKHintPosition(AvatarIKHint.LeftElbow, Comp(elbowL));
        }
        else
        {
            anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f);
            anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0f);
            anim.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, 0f);
        }
    }

    /// <summary>Per-pose targets, in the boss's own frame scaled by his height.</summary>
    private void PoseTargets(out Vector3 swordDir, out Vector3 grip, out float wR, out Vector3 left, out float wL,
                             out Vector3 elbowR, out Vector3 elbowL, out float leanDeg, out float twistDeg)
    {
        var h = H;
        wR = 1f; wL = 1f;
        leanDeg = 0f; twistDeg = 0f;
        elbowR = Local(0.42f, 0.8f, 0f);
        elbowL = Local(-0.42f, 0.8f, 0f);
        switch (kind)
        {
            case Kind.Overhead:
                // Straight up, a hair back: the read of the move to fear.
                swordDir = Dir(0f, 1f, -0.1f);
                grip = Local(0.06f, 1.06f, 0.08f);
                left = grip - swordDir * 0.09f * h;
                elbowR = Local(0.4f, 0.95f, 0.05f);
                elbowL = Local(-0.4f, 0.95f, 0.05f);
                leanDeg = -7f;
                break;
            case Kind.Planted:
            {
                // Blade point-down into the floor at plantPoint; hands on the hilt above it.
                swordDir = Vector3.down;
                var len = SwordLength();
                var floor = plantPoint;
                grip = floor + Vector3.up * (len * 0.8f);
                // Keep the hilt within reach: lower and pull the plant point in if he is small.
                var maxGrip = frame.position.y + 0.72f * h;
                if (grip.y > maxGrip) grip.y = maxGrip;
                left = grip + Vector3.up * 0.085f * h;
                elbowR = Local(0.38f, 0.62f, 0.1f);
                elbowL = Local(-0.38f, 0.62f, 0.1f);
                leanDeg = 14f;
                break;
            }
            case Kind.RaiseHand:
                // The empty hand up; the sword hand keeps the clip.
                swordDir = Dir(0.3f, -1f, 0.2f);
                grip = Local(0.3f, 0.5f, 0.1f);
                wR = 0f;
                left = Local(-0.22f, 1.14f, 0.2f);
                elbowL = Local(-0.5f, 0.98f, 0f);
                leanDeg = -4f;
                break;
            case Kind.Drag:
                // Greatsword trailing behind, tip low on the stone.
                swordDir = Dir(0.22f, -0.5f, -1f);
                grip = Local(0.36f, 0.44f, -0.3f);
                left = grip;
                wL = 0f;
                elbowR = Local(0.5f, 0.6f, -0.1f);
                leanDeg = 10f;
                twistDeg = 22f;
                break;
            case Kind.Thrust:
                // Drawn back at the hip, level, aimed down the line.
                swordDir = Dir(0f, 0.03f, 1f);
                grip = Local(0.26f, 0.52f, -0.28f);
                left = Local(0.02f, 0.56f, 0.1f);
                wL = 0.6f;
                elbowR = Local(0.45f, 0.55f, -0.35f);
                leanDeg = 6f;
                twistDeg = 18f;
                break;
            case Kind.Coil:
                // Wound up away from the swing: twist and a low guard.
                swordDir = Dir(-0.4f, 0.15f, -1f);
                grip = Local(-0.2f, 0.62f, -0.1f);
                left = grip - swordDir * 0.09f * h;
                elbowR = Local(0.15f, 0.7f, -0.3f);
                twistDeg = -38f;
                leanDeg = 6f;
                break;
            default:
                swordDir = frame.up;
                grip = Local(0.3f, 0.5f, 0.1f);
                wR = wL = 0f;
                left = grip;
                break;
        }
    }

    private float SwordLength()
    {
        if (sword == null) return 1.4f;
        WardenBlade.Measure(sword, out _, out var tipL);
        return Vector3.Distance(sword.position, sword.TransformPoint(tipL));
    }

    private Quaternion LeanRotation()
    {
        if (frame == null) return Quaternion.identity;
        return Quaternion.AngleAxis(twist, frame.up) * Quaternion.AngleAxis(lean, frame.right);
    }

    private void Zero()
    {
        anim.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
        anim.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
        anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0f);
        anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0f);
        anim.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 0f);
        anim.SetIKHintPositionWeight(AvatarIKHint.LeftElbow, 0f);
        lean = twist = 0f;
    }

    private void LateUpdate()
    {
        if (handR == null) return;
        // Learn the constant offsets on clean frames (no pose, no lean): goal→bone
        // rotation, and where the sword's grip sits in the hand.
        if (goalSampled && weight <= 0.001f)
        {
            goalToBoneR = Quaternion.Inverse(sampledGoalR) * handR.rotation;
            goalKnown = true;
            LearnGrip();
        }
        goalSampled = false;
        if (spine != null && (Mathf.Abs(lean) > 0.01f || Mathf.Abs(twist) > 0.01f))
        {
            var rot = LeanRotation();
            // Rotate the upper body about the spine joint (world space).
            spine.rotation = rot * spine.rotation;
        }
    }
}
