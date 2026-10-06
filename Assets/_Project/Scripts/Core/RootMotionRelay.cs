using UnityEngine;

/// <summary>A displacement action that wants clip root motion applied to the
/// capsule while it's the current owner (attacks, skills, boss moves).</summary>
public interface IRootMotionOwner
{
    bool DriveRootMotion { get; }
    /// <summary>True lets the clip's root-Y reach the capsule; false means
    /// relay gravity owns vertical motion.</summary>
    bool AllowRootY { get; }
}

/// <summary>
/// Single owner of root-motion application. Sits on the humanoid Animator's
/// GameObject (OnAnimatorMove only fires there) with `applyRootMotion = true` —
/// the deltas go to this script instead of the transform, and it forwards them
/// to the CharacterController ONLY while a displacement action claims them.
///
/// Owners: any `IRootMotionOwner` on the physics root that reports driving
/// (AttackController swings/skills flagged useRootMotion; DodgeController moves
/// at the clip's authored direction with tuned code speed; boss moves).
///
/// Vertical policy: clip Y is dropped unless the owner allows it; gravity is
/// applied every frame an owner drives so displacement stays on the floor —
/// the same single-gravity rule the scripted movers used.
///
/// With no owner the deltas are DISCARDED — equivalent to applyRootMotion=false.
/// Never set applyRootMotion without this component or non-inplace clips will
/// walk the visual away from the capsule.
/// </summary>
public sealed class RootMotionRelay : MonoBehaviour
{
    [SerializeField] private float gravity = -8f;

    private Animator anim;
    private CharacterController cc;
    private Transform physicsRoot;
    private DodgeController dodge;
    private IRootMotionOwner[] owners;

    /// <summary>True while an owner is consuming root motion this frame.</summary>
    public bool Driving { get; private set; }

    private void Awake()
    {
        anim = GetComponent<Animator>();
        cc = GetComponentInParent<CharacterController>();
        physicsRoot = cc != null ? cc.transform : null;
        dodge = GetComponentInParent<DodgeController>();
        owners = GetComponentsInParent<IRootMotionOwner>();
    }

    private void OnAnimatorMove()
    {
        Driving = false;
        // Boss death disables the CharacterController while the Animator keeps
        // firing OnAnimatorMove for the death clip — Move on an inactive
        // controller throws, so bail before either branch can call it.
        if (anim == null || cc == null || !cc.enabled || !cc.gameObject.activeInHierarchy) return;

        // Dodge travels the INTENDED direction at the tuned speed — the clip's
        // authored delta can't be trusted: standstill sidesteps (StandDodge_L/R)
        // step out and return to center, so clip-dir steering nets ~0 and the
        // dodge visibly animates while the capsule never moves.
        if (dodge != null && dodge.DriveRootMotion)
        {
            Driving = true;
            var move = dodge.DodgeDir * dodge.CurrentSpeed * Time.deltaTime;
            move.y = gravity * Time.deltaTime;
            cc.Move(move);
            return;
        }

        IRootMotionOwner owner = null;
        if (owners != null)
            foreach (var o in owners)
                // Interface refs bypass Unity's fake-null check — a destroyed
                // owner would slip past o != null and throw on the getter.
                if ((o as Component) != null && o.DriveRootMotion) { owner = o; break; }
        if (owner == null && (owners == null || owners.Length == 0))
            owners = GetComponentsInParent<IRootMotionOwner>(); // late-added owners (skills, boss) get one retry
        if (owner == null) return;
        Driving = true;

        var delta = anim.deltaPosition;
        if (owner.AllowRootY)
        {
            // Authored Y reaches the capsule VERBATIM — the clip owns the whole
            // arc (uppercut rise + descent). Adding gravity here would double it
            // up and squash the rise: ~8m/s of pull vs a ~4m/s authored hop.
        }
        else
        {
            // Clip Y dropped — substitute gravity so displacement stays floored.
            delta.y = 0f;
            delta.y += gravity * Time.deltaTime;
        }
        cc.Move(delta);

        var rot = anim.deltaRotation;
        if (rot != Quaternion.identity && physicsRoot != null)
            physicsRoot.rotation = rot * physicsRoot.rotation;
    }
}
