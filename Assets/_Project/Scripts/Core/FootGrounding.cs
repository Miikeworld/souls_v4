using UnityEngine;

/// <summary>
/// Keeps feet on the floor through retargeted humanoid clips. Two channels:
///   1. A pelvis offset on the visual root's local Y (LateUpdate) — absorbs
///      the gross hip-height error retargeting introduces (~0.5m on the Synty
///      skeleton under Grruzam clips).
///   2. Per-foot IK (OnAnimatorIK — this component MUST sit on the humanoid
///      Animator's GameObject and the controller layer needs iKPass=true) —
///      corrects the residual per-foot gap and aligns soles to ground normals.
///
/// Ground truth comes from a per-foot downcast, NOT CharacterController.isGrounded
/// (which is false on idle/dead enemies whose CC never moves). Sole position is
/// calibrated once at Start by baking the skinned mesh and finding the lowest
/// foot-weighted vertex — pose-consistent, so a spawn-time float can't bake
/// itself into the estimate like floor calibration did.
///
/// Enemies are treated as always grounded (they can't jump). The player keeps
/// isGrounded + a speed gate so jumps/runs don't get the IK fight.
/// </summary>
[DefaultExecutionOrder(50)] // after locomotion/animator writes pose each frame
public sealed class FootGrounding : MonoBehaviour
{
    [SerializeField] private Transform visualRoot;
    [SerializeField] private CharacterController character;
    [Tooltip("Max pelvis correction applied (metres) — bounds the effect.")]
    [SerializeField, Min(0.01f)] private float pelvisOffsetMax = 0.5f;
    [SerializeField, Min(0.01f)] private float attackPelvisOffsetMax = 0.9f;
    [SerializeField, Min(1f)] private float smooth = 14f;
    [Tooltip("Only correct below this planar speed — running looks fine without it.")]
    [SerializeField, Min(0f)] private float maxSpeed = 3.5f;
    [Tooltip("Residual gap per foot that foot IK is allowed to correct — bigger lifts belong to the pelvis solve.")]
    [SerializeField, Range(0.05f, 0.4f)] private float footIkRange = 0.22f;
    [Tooltip("Foot IK weight while grounded (attacks get 60% so authored stances survive).")]
    [SerializeField, Range(0f, 1f)] private float footIkWeight = 0.85f;
    [Tooltip("Disable for rigs whose imported renderer bounds do not track the animated soles.")]
    [SerializeField] private bool useMeshBoundsGuard = true;

    private Animator anim;
    private Transform ankleL, ankleR, toeL, toeR;
    // The toe joint's skin depth is roughly constant across rigs — a fixed pad,
    // NOT a calibrated offset (a toe pad measured in a raised-toe pose reads the
    // sole permanently wrong — that trap is why toes weren't the reference).
    private const float ToeSolePad = 0.02f;
    private PlayerState state;
    private AttackController attack;
    private SlideController slide;
    private WallRunController wallRun;
    private IRootMotionOwner boss; // boss swings pose like attacks — same budget rule (any IBossEngage brain)
    private Health health;
    private float noContactT; // how long both soles have been off the floor
    private bool speedOk = true; // speed gate with hysteresis — no boundary toggling
    private Transform[] corpseBones;
    private SkinnedMeshRenderer[] bodySmrs;
    private float soleOffsetL = -1f, soleOffsetR = -1f;
    private float meshSlack; // AABB overhang below the sole line at calibration
    private float baseLocalY;
    private float correction;
    private float ikBlend; // 0..1 current foot-IK weight envelope
    private float diagT = 2f; // one-shot live solve dump a beat after spawn
    private bool isPlayer;
    private bool grounded, probing;
    private float groundYL, groundYR;
    private Vector3 normalL = Vector3.up, normalR = Vector3.up;
    private float soleYL, soleYR;
    private float floorY, soleY;
    private float residualL, residualR; // foot gap left after the pelvis solve
    private bool plungeContactCorrectionPending;
    private Vector3 plungeFloorPoint;

    /// <summary>Only the attack's verified capsule-floor event can request an
    /// immediate anti-penetration correction. This component remains the Y owner.</summary>
    public void NotifyPlungeContact(Vector3 point, Vector3 normal)
    {
        if (normal.y <= 0f) return;
        plungeFloorPoint = point;
        plungeContactCorrectionPending = true;
    }

    private void Start()
    {
        var loco = GetComponentInParent<PlayerLocomotion>();
        isPlayer = loco != null;
        state = GetComponentInParent<PlayerState>();
        attack = GetComponentInParent<AttackController>();
        slide = GetComponentInParent<SlideController>();
        wallRun = GetComponentInParent<WallRunController>();
        boss = GetComponentInParent<IBossEngage>() as IRootMotionOwner;
        health = GetComponentInParent<Health>();
        if (character == null)
            character = loco != null ? loco.GetComponent<CharacterController>()
                                     : GetComponentInParent<CharacterController>();

        // Find the HUMANOID animator first, then take its transform as the visual
        // root. Picking the first Animator child hits the inert avatar-less Animator
        // that RequireComponent adds to the player root.
        anim = GetComponent<Animator>();
        if (anim == null || anim.avatar == null || !anim.avatar.isHuman)
        {
            // A scene grouping root can contain many independent enemy rigs.
            // Never borrow a sibling's Animator for this component's IK callback.
            var searchRoot = loco != null ? loco.transform : transform;
            foreach (var a in searchRoot.GetComponentsInChildren<Animator>(true))
            {
                if (a != null && a.avatar != null && a.avatar.isHuman) { anim = a; break; }
            }
        }
        if (anim == null) anim = GetComponent<Animator>();
        if (anim != null && anim.avatar != null && anim.avatar.isHuman)
        {
            if (visualRoot == null) visualRoot = anim.transform;
            ankleL = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            ankleR = anim.GetBoneTransform(HumanBodyBones.RightFoot);
            toeL = anim.GetBoneTransform(HumanBodyBones.LeftToes);
            toeR = anim.GetBoneTransform(HumanBodyBones.RightToes);
        }
        if (GetComponent<Animator>() != anim)
            Debug.LogWarning("[FootGrounding] not on the humanoid Animator GO — foot IK won't run (pelvis offset still works).", this);
        // Never let visualRoot resolve to the CharacterController's own transform.
        if (visualRoot != null && character != null && visualRoot == character.transform)
            visualRoot = null;
        if (visualRoot == null) visualRoot = transform;
        if (visualRoot != null) baseLocalY = visualRoot.localPosition.y;
        // Ankles optional — the mesh-bottom pelvis solve doesn't need them;
        // IK just idles on whichever foot is missing.
        enabled = visualRoot != null && character != null;
        if (!enabled) { Debug.LogWarning("[FootGrounding] missing visual/ankles/CC — disabled.", this); return; }
        bodySmrs = anim.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        CalibrateSoles();
    }

    /// <summary>Ankle→sole distance is MEASURED: the lowest baked vertex in a
    /// 12.5cm column under each ankle. The avatar's feetBottomHeight is only
    /// a fallback — it can be authored for different proportions and float the
    /// whole body when it over-reads.</summary>
    private void CalibrateSoles()
    {
        var bl = anim != null ? anim.leftFeetBottomHeight : 0f;
        var br = anim != null ? anim.rightFeetBottomHeight : 0f;
        var ml = GroundingSolver.MeasureSoleOffset(bodySmrs, ankleL);
        var mr = GroundingSolver.MeasureSoleOffset(bodySmrs, ankleR);
        soleOffsetL = ml > 0.02f ? ml : (bl > 0.005f ? bl : 0.02f);
        soleOffsetR = mr > 0.02f ? mr : (br > 0.005f ? br : 0.02f);
        // bounds.min.y pads past the true sole line by a few cm. The dip guard
        // uses that same AABB metric — without this slack it lifts until the
        // PADDED bounds clears the floor, parking the soles (slack - 2cm)
        // above ground: the constant slight float.
        var meshLow = LowestMeshY();
        var spawnSole = Mathf.Min(
            ankleL != null ? ankleL.position.y - soleOffsetL : float.MaxValue,
            ankleR != null ? ankleR.position.y - soleOffsetR : float.MaxValue);
        meshSlack = meshLow < float.MaxValue && spawnSole < float.MaxValue
            ? Mathf.Max(0f, spawnSole - meshLow) : 0f;
        Debug.Log($"[FootGrounding] {name} soleOffsets L={soleOffsetL:F3} R={soleOffsetR:F3} slack={meshSlack:F3} (mesh L={ml:F3} R={mr:F3}, avatar L={bl:F3} R={br:F3})");
    }

    private void LateUpdate()
    {
        if (ankleL == null && ankleR == null || character == null || visualRoot == null)
        {
            enabled = false;
            return;
        }
        var dt = Time.deltaTime;
        probing = false;

        // Dead: the corpse lies flat — lowest body bone is the contact point,
        // and the dead CC reports isGrounded=false. Ground by bones directly.
        if ((health != null && health.IsDead) || (state != null && state.IsDead))
        {
            if (corpseBones == null)
            {
                var list = new System.Collections.Generic.List<Transform>();
                foreach (var b in CorpseBones)
                {
                    var t = anim.GetBoneTransform(b);
                    if (t != null) list.Add(t);
                }
                corpseBones = list.ToArray();
            }
            var low = float.MaxValue;
            foreach (var t in corpseBones)
                if (t != null && t.position.y < low) low = t.position.y;
            if (low < float.MaxValue)
            {
                var groundY = GroundY();
                var ct = Mathf.Clamp(groundY + 0.04f - low, -attackPelvisOffsetMax, attackPelvisOffsetMax);
                correction = Mathf.Lerp(correction,
                                        Mathf.Clamp(correction + ct, -attackPelvisOffsetMax, attackPelvisOffsetMax),
                                        1f - Mathf.Exp(-22f * dt));
                visualRoot.localPosition = new Vector3(visualRoot.localPosition.x,
                                                       baseLocalY + correction, visualRoot.localPosition.z);
            }
            ikBlend = Mathf.Lerp(ikBlend, 0f, 1f - Mathf.Exp(-14f * dt));
            return;
        }

        // Per-foot probes — per-foot floor Y for IK, and the body's contact
        // floor: the LOWER valid foot (a trailing foot over a step/void probes
        // nothing and is ignored). Both miss → the ray under the body.
        var rootT = character.transform;
        probing = true;
        groundYL = ProbeFoot(ankleL, rootT, ref normalL);
        groundYR = ProbeFoot(ankleR, rootT, ref normalR);
        // Sole estimate = the LOWER of the two candidates: the ankle's fixed
        // drop (exact on a flat foot) and the live toe joint minus its pad
        // (exact on a pitched foot — toe-off puts the toe below the under-ankle
        // estimate by ~9cm at 30°, which the fixed offset reads as a high sole
        // and answers by sinking the pelvis: the locomotion sink).
        soleYL = SoleEstimate(ankleL, toeL, soleOffsetL);
        soleYR = SoleEstimate(ankleR, toeR, soleOffsetR);
        floorY = Mathf.Min(groundYL, groundYR);
        if (floorY == float.MaxValue) floorY = plungeContactCorrectionPending ? plungeFloorPoint.y : GroundY();

        // Ground truth = the deeper foot's sole (calibrated ankle→sole
        // offsets), not the whole mesh bottom — the lowest mesh point can be
        // a coat tail or cape, and planting THAT on the floor floats the
        // feet. Mesh bottom stays as a fallback when no ankles resolve.
        soleY = Mathf.Min(soleYL, soleYR);
        if (soleY == float.MaxValue) soleY = LowestMeshY();

        // Enemies can't jump — sole-near-floor is grounded enough for them.
        // The player gates on the CC so jumps release the grounding.
        grounded = isPlayer ? character.isGrounded || (attack != null && attack.UsesLandingGrounding)
                            : soleY < float.MaxValue && soleY - floorY < 0.5f;
        var inAttack = (attack != null && (attack.IsAttacking))
                       || (boss != null && boss.DriveRootMotion);

        // Slide, wall-run and landing-roll set IsDisplacing — but a slide whose
        // capsule is grounded is the same standing-pose problem as an attack:
        // releasing exposes the authored hip height, which floats/sinks on rigs
        // whose slide clip doesn't sit the soles down. Keep the solve there.
        // A grounded crouch is the same: keep the soles planted (IsRooted would release them).
        var groundedSlide = slide != null && (slide.IsSliding || slide.IsCrouching) && grounded;
        var wallRunning = wallRun != null && wallRun.IsWallRunning;
        if (state != null && (state.IsDisplacing || state.IsRooted) && !(inAttack && grounded) && !groundedSlide)
        {
            correction = Mathf.Lerp(correction, 0f, 1f - Mathf.Exp(-smooth * dt));
            ikBlend = Mathf.Lerp(ikBlend, 0f, 1f - Mathf.Exp(-smooth * dt));
            // Released is not unbounded: decay is free upward (authored bounce
            // survives) but the visible sole may never be left under the probed
            // floor. Skipped during wall-run — feet ride the wall face and their
            // downcasts read the wall's TOP EDGE as a false floor 1-2m up (the
            // up-down oscillation).
            if (!wallRunning)
            {
                var floor = SoleFloor(attackPelvisOffsetMax);
                if (floor > float.MinValue) correction = Mathf.Max(correction, floor);
            }
            visualRoot.localPosition = new Vector3(visualRoot.localPosition.x, baseLocalY + correction, visualRoot.localPosition.z);
            plungeContactCorrectionPending = false;
            return;
        }

        var planar = isPlayer ? Vector3.ProjectOnPlane(character.velocity, Vector3.up).magnitude : 0f;
        var budget = inAttack ? attackPelvisOffsetMax : pelvisOffsetMax;

        // Active = grounded now (and slow enough on the player). The speed gate
        // latches with hysteresis — an analog stick parked on the boundary must
        // not toggle the solve every frame. Anything else lerps the offset back
        // out so jumps/runs don't fight the IK.
        if (isPlayer)
        {
            if (speedOk && planar > maxSpeed * 1.25f) speedOk = false;
            else if (!speedOk && planar < maxSpeed * 0.8f) speedOk = true;
        }
        var active = grounded && (isPlayer ? speedOk || inAttack || groundedSlide : true);

        // Chase only on CONTACT. Both soles above their floors = mid-stride
        // flight — the solve HOLDS the offset instead of dipping into every
        // stride (the rhythmic sag that read as jitter). But a sustained
        // no-contact pose is a clip riding high, not flight — after a beat the
        // solve pulls it down again.
        var contact = (soleYL < float.MaxValue && groundYL < float.MaxValue && soleYL <= groundYL + 0.06f)
                   || (soleYR < float.MaxValue && groundYR < float.MaxValue && soleYR <= groundYR + 0.06f);
        if (contact) noContactT = 0f; else noContactT += dt;
        // The no-contact hold covers locomotion flight phases (~0.1s strides).
        // An attack swing is a third of a second — a pose riding both feet off
        // the floor must ground within a couple of frames or it reads as a
        // hover, so the hold shrinks while a swing owns the body.
        var noContactHold = inAttack ? 0.07f : 0.25f;
        // gap is the residual error WITH the current offset already applied —
        // it's a DELTA to chase, not the target offset. Lerping the offset
        // toward the gap settles at half the error (the offset feeds back
        // into the measured bottom) — that was the persistent float.
        var gap = !active || soleY == float.MaxValue ? -correction
                : contact || noContactT > noContactHold ? Mathf.Clamp(floorY - soleY, -budget, budget)
                : 0f;
        var target = Mathf.Clamp(correction + gap, -budget, budget);
        // The sole offset was measured once, at spawn — foot flex can drift
        // the true sole a few cm. Never let the VISIBLE bottom pass more than
        // ~2cm through the floor: that's a sink the offset math can't see.
        // bounds.min.y is AABB slack, so only trust it when a sole is near
        // contact height — on attack flares/kicks it reads a swept coat or a
        // pose far above the floor and would levitate the planted foot.
        var meshY = LowestMeshY();
        var nearFloor = soleYL - groundYL < 0.35f || soleYR - groundYR < 0.35f;
        // meshY + meshSlack estimates the TRUE lowest surface — the AABB alone
        // over-reads by its padding and would lift the resting pose.
        var lowSurf = meshY + meshSlack;
        if (useMeshBoundsGuard && active && nearFloor && meshY < float.MaxValue && lowSurf < floorY - 0.02f)
            target = Mathf.Max(target, Mathf.Clamp(correction + floorY - 0.02f - lowSurf,
                                                   -budget, budget));
        // The anti-sink floor is a CONTINUOUS lower bound on the target, not a
        // penetration trigger — it pins the sole at floor+5mm steadily instead
        // of ping-ponging across a threshold (the per-frame buzz it fixed badly
        // the first time). It binds while the solve runs AND while suspended.
        var soleFloor = SoleFloor(budget);
        if (soleFloor > float.MinValue) target = Mathf.Max(target, soleFloor);
        var oldCorrection = correction;
        // A confirmed plunge landing may not spend several frames smoothing
        // upward out of the floor; the same for a real authored dip (>6cm —
        // a slam, not toe noise). Lowering into the authored stance still eases.
        var snap = (active && (plungeContactCorrectionPending || (attack != null && attack.UsesLandingGrounding)))
                   || soleY < floorY - 0.06f;
        if (snap && target > correction)
            correction = target;
        else correction = Mathf.Lerp(correction, target, 1f - Mathf.Exp(-smooth * dt));
        plungeContactCorrectionPending = false;
        visualRoot.localPosition = new Vector3(visualRoot.localPosition.x,
                                               baseLocalY + correction,
                                               visualRoot.localPosition.z);

        // Residual per-foot gap after the pelvis solve — what foot IK cleans up.
        residualL = soleYL + correction - oldCorrection - groundYL;
        residualR = soleYR + correction - oldCorrection - groundYR;
        var wantIk = active ? footIkWeight * (inAttack ? 0.6f : 1f) : 0f;
        ikBlend = Mathf.Lerp(ikBlend, wantIk, 1f - Mathf.Exp(-smooth * dt));

        if (diagT > 0f)
        {
            diagT -= dt;
            if (diagT <= 0f)
            {
                var visibleSole = Mathf.Min(GroundingSolver.MeasureFootLowest(bodySmrs, ankleL, 0.2f),
                    GroundingSolver.MeasureFootLowest(bodySmrs, ankleR, 0.2f));
                Debug.Log($"[FootGrounding] {name} live: grounded={grounded} floor={floorY:F3} soleY={soleY:F3} meshY={meshY:F3} corr={correction:F3} visibleSoleGap={visibleSole - floorY:F3}");
            }
        }
    }

    /// <summary>Per-foot IK. Requires iKPass on layer 0 of the controller and
    /// this component on the humanoid Animator's GameObject.</summary>
    private void OnAnimatorIK(int layerIndex)
    {
        if (anim == null) return;
        if (!probing || ikBlend < 0.01f)
        {
            // Explicit zero so a stale weight can't freeze a foot mid-pose.
            anim.SetIKPositionWeight(AvatarIKGoal.LeftFoot, 0f);
            anim.SetIKRotationWeight(AvatarIKGoal.LeftFoot, 0f);
            anim.SetIKPositionWeight(AvatarIKGoal.RightFoot, 0f);
            anim.SetIKRotationWeight(AvatarIKGoal.RightFoot, 0f);
            return;
        }
        PlantFoot(AvatarIKGoal.LeftFoot, ankleL, groundYL, normalL, residualL, soleOffsetL);
        PlantFoot(AvatarIKGoal.RightFoot, ankleR, groundYR, normalR, residualR, soleOffsetR);
    }

    private void PlantFoot(AvatarIKGoal goal, Transform ankle, float groundY, Vector3 normal, float residual, float soleOffset)
    {
        if (ankle == null || ikBlend <= 0f) return;
        // Only residuals inside footIkRange get pulled — bigger errors belong
        // to the pelvis solve and dragging a leg far reads as a broken knee.
        var w = ikBlend * Mathf.Clamp01(1f - Mathf.Abs(residual) / footIkRange);
        anim.SetIKPositionWeight(goal, w);
        anim.SetIKRotationWeight(goal, w * 0.7f);
        if (w <= 0.01f) return;
        var pos = anim.GetIKPosition(goal);
        pos.y = groundY + soleOffset; // ankle sits sole-offset above the floor
        anim.SetIKPosition(goal, pos);
        var rot = Quaternion.FromToRotation(Vector3.up, normal) * anim.GetIKRotation(goal);
        anim.SetIKRotation(goal, rot);
    }

    private float ProbeFoot(Transform ankle, Transform physicsRoot, ref Vector3 normal)
    {
        if (ankle == null) return float.MaxValue;
        return GroundingSolver.ProbeFootGround(ankle.position, physicsRoot, visualRoot.root,
                                               out _, out normal);
    }

    private float GroundY()
    {
        var p = character.transform.position;
        var phys = character.transform.root;
        var vis = visualRoot.root;
        var hits = Physics.RaycastAll(p + Vector3.up * 0.5f, Vector3.down, 1.5f,
                                      ~0, QueryTriggerInteraction.Ignore);
        var best = float.MaxValue; var bd = float.MaxValue;
        foreach (var h in hits)
        {
            var r = h.collider.transform.root;
            if (r == phys || r == vis) continue;
            if (h.distance < bd) { bd = h.distance; best = h.point.y; }
        }
        return best < float.MaxValue ? best : p.y;
    }

    /// <summary>Estimated sole Y for one foot — the lower of the ankle's fixed
    /// drop and the toe joint's live height minus its skin pad. A flat foot
    /// reads the ankle path (unchanged); a pitched foot reads the toe, which
    /// is the actual contact point the fixed offset misses.</summary>
    private static float SoleEstimate(Transform ankle, Transform toe, float ankleOffset)
    {
        var s = ankle != null ? ankle.position.y - ankleOffset : float.MaxValue;
        if (toe != null) s = Mathf.Min(s, toe.position.y - ToeSolePad);
        return s;
    }

    /// <summary>The absolute correction that holds the estimated sole 5mm above
    /// the probed floor — a continuous lower bound, not a trigger. soleY already
    /// reflects `correction`, so when the sole reads below floor+5mm this is the
    /// lift that puts it back; above the floor it drops below `correction` and
    /// stops binding. MinValue when no sole/floor estimate exists.</summary>
    private float SoleFloor(float budget)
        => soleY < float.MaxValue && floorY < float.MaxValue
            ? Mathf.Clamp(correction + floorY + 0.005f - soleY, -budget, budget)
            : float.MinValue;

    /// <summary>Lowest point of the visible body this frame — the ground truth
    /// the pelvis solve plants. MaxValue when no renderer is live.</summary>
    private float LowestMeshY()
    {
        var low = float.MaxValue;
        if (bodySmrs == null) return low;
        foreach (var r in bodySmrs)
            if (r != null && r.enabled && r.gameObject.activeInHierarchy && r.bounds.min.y < low)
                low = r.bounds.min.y;
        return low;
    }

    // The body bones that can end up lowest in a lying corpse pose.
    private static readonly HumanBodyBones[] CorpseBones =
    {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Head,
        HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
        HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
        HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
        HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
    };
}
