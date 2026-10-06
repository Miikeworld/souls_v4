using UnityEngine;

/// <summary>
/// The dodge press. Locked on: a directional quickstep in character-facing
/// space (the character keeps facing the target — forward approaches, back
/// retreats, left/right sidestep, Bloodborne-style, no body turn). Unlocked:
/// a free roll — the body turns to the camera-relative input direction and
/// darts forward; neutral input is a short backstep hop. All timing/distance/
/// i-frame values are read from the equipped WeaponSet so equipment can
/// change dodge style. Sets IsDisplacing while active and IsInvulnerable
/// during the i-frame window.
/// </summary>
[RequireComponent(typeof(CharacterController), typeof(PlayerState))]
public sealed class DodgeController : MonoBehaviour, IRootMotionOwner
{
    [Header("Fallback tuning (used when no WeaponSet overrides)")]
    [SerializeField, Min(0.5f)] private float dodgeDistance = 3.4f;
    [SerializeField, Min(0.5f)] private float backstepDistance = 1.6f;
    [SerializeField, Min(0.1f)] private float dodgeDuration = 0.5f;
    [Tooltip("Scales every dodge/backstep distance (weapon-set or local). New field so the longer default reaches existing scenes.")]
    [SerializeField, Min(0.1f)] private float dodgeDistanceScale = 1.5f;
    [SerializeField, Min(0.1f)] private float backstepDuration = 0.32f;
    [SerializeField, Min(0f)] private float iFrameStart = 0.06f;
    [SerializeField, Min(0f)] private float iFrameEnd = 0.23f;
    [SerializeField, Min(0f)] private float dodgeCooldown = 0.18f;
    [SerializeField, Min(0f)] private float entrySpeedMultiplier = 1.4f;
    [SerializeField, Min(0f)] private float exitSpeedMultiplier = 0.7f;

    [Header("Shared")]
    [SerializeField, Min(0.01f)] private float fadeDuration = 0.08f;
    [Tooltip("Crossfade back to locomotion when the dodge ends — longer than the entry fade so the tail reads smoothly.")]
    [SerializeField, Min(0.01f)] private float endFadeDuration = 0.22f;
    [SerializeField] private float gravity = -8f;

    public bool IsDodging { get; private set; }
    /// <summary>unscaledTime the dodge last ended — technique contexts hold a
    /// short grace window so a counter pressed on the recovery edge counts.</summary>
    public float DodgeEndTime { get; private set; } = -9f;
    /// <summary>True while the dodge wants clip-driven travel — the relay moves
    /// the capsule at the authored direction with our tuned speed.</summary>
    public bool DriveRootMotion => IsDodging && useRootMotion;
    /// <summary>Authored-space dodge direction (world) — relay fallback when the
    /// clip carries no root motion.</summary>
    public Vector3 DodgeDir => dodgeDir;
    /// <summary>Current code-side speed with the entry→exit ease applied.</summary>
    public float CurrentSpeed => dodgeSpeed * Mathf.Lerp(actEntryMult, actExitMult,
        dodgeTime > 0f ? Mathf.Clamp01(dodgeAge / dodgeTime) : 1f);
    /// <summary>Dodges stay grounded — relay gravity owns vertical.</summary>
    public bool AllowRootY => false;

    [Tooltip("Root motion drives dodge travel (the _Root clip takes) at tuned speed.")]
    [SerializeField] private bool useRootMotion = true;

    private CharacterController character;
    private Animator animator;
    private PlayerState state;
    private LockOnController lockOn;
    private WeaponSocket weaponSocket;
    private PlayerLocomotion locomotion;
    private AttackController attack;
    private PlayerStamina stamina;
    private CameraTiltController tilt;

    private Vector3 dodgeDir;
    private float dodgeAge, dodgeTime, dodgeSpeed;
    private float cooldownTimer;
    private bool isBackstep;
    private float actIFrameStart, actIFrameEnd, actEntryMult, actExitMult;

    private static readonly int DodgeFrontId = Animator.StringToHash("Base Layer.DodgeFront");
    private static readonly int DodgeBackId = Animator.StringToHash("Base Layer.DodgeBack");
    private static readonly int DodgeLeftId = Animator.StringToHash("Base Layer.DodgeLeft");
    private static readonly int DodgeRightId = Animator.StringToHash("Base Layer.DodgeRight");
    private static readonly int CombatMoveId = Animator.StringToHash("Base Layer.CombatMove");
    private static readonly int LocomotionId = Animator.StringToHash("Base Layer.Locomotion");
    private static readonly int MoveXId = Animator.StringToHash("MoveX");
    private static readonly int MoveYId = Animator.StringToHash("MoveY");

    private void Awake()
    {
        character = GetComponent<CharacterController>();
        state = GetComponent<PlayerState>();
        lockOn = GetComponent<LockOnController>();
        weaponSocket = GetComponent<WeaponSocket>();
        locomotion = GetComponent<PlayerLocomotion>();
        attack = GetComponent<AttackController>();
        stamina = GetComponent<PlayerStamina>();
        tilt = GetComponent<CameraTiltController>();
        animator = FindValidAnimator();
    }

    /// <summary>
    /// Attempts a locked-on dodge. moveInput is the camera-relative move stick at the
    /// moment of the press; magnitude under ~0.2 performs a neutral backstep.
    /// Returns true if the dodge started (caller should consume the input).
    /// </summary>
    public bool TryDodge(Vector2 moveInput)
    {
        if (IsDodging || cooldownTimer > 0f || animator == null || state == null ||
            state.IsDead || state.IsRooted || UiGates.MenuOpen || GameLoop.IsResting ||
            !(character.isGrounded || (attack != null && attack.GroundedForAction)))
            return false;
        if (state.IsDisplacing)
        {
            // An attack's recovery can be dodge-cancelled; slide/dodge displacement can't.
            if (attack == null || !attack.InRecovery) return false;

        }
        var locked = lockOn != null && lockOn.IsLockedOn;


        var set = weaponSocket != null ? weaponSocket.Set : null;
        var dist = (set != null ? set.dodgeDistance : dodgeDistance) * dodgeDistanceScale;
        var backDist = (set != null ? set.backstepDistance : backstepDistance) * dodgeDistanceScale;
        var dur = set != null ? set.dodgeDuration : dodgeDuration;
        var backDur = set != null ? set.backstepDuration : backstepDuration;
        actIFrameStart = set != null ? set.iFrameStart : iFrameStart;
        actIFrameEnd = set != null ? set.iFrameEnd : iFrameEnd;

        actEntryMult = set != null ? set.dodgeEntrySpeed : entrySpeedMultiplier;
        actExitMult = set != null ? set.dodgeExitSpeed : exitSpeedMultiplier;

        var nextRotation = transform.rotation;
        int stateId;
        Vector3 dirLocal;
        if (moveInput.magnitude < 0.2f)
        {
            dirLocal = Vector3.back;
            stateId = DodgeBackId;
        }
        else
        {
            var cam = Camera.main;
            if (cam == null) return false;
            var camFwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            var camRight = Vector3.Cross(Vector3.up, camFwd);
            var worldDir = camFwd * moveInput.y + camRight * moveInput.x;

            if (!locked)
            {
                // Free roll: turn to the input direction and dart forward.
                worldDir.y = 0f;
                if (worldDir.sqrMagnitude < 0.001f) worldDir = transform.forward;
                nextRotation = Quaternion.LookRotation(worldDir.normalized);
                dirLocal = Vector3.forward;
                stateId = DodgeFrontId;
            }
            else
            {
                // Camera-space input → facing-space (the character faces the target while
                // locked, so dodge directions are enemy-relative: F approaches, B retreats).
                var local = transform.InverseTransformDirection(worldDir);
                if (Mathf.Abs(local.x) > Mathf.Abs(local.z))
                {
                    dirLocal = new Vector3(Mathf.Sign(local.x), 0f, 0f);
                    stateId = local.x > 0f ? DodgeRightId : DodgeLeftId;
                }
                else
                {
                    dirLocal = new Vector3(0f, 0f, Mathf.Sign(local.z));
                    stateId = local.z > 0f ? DodgeFrontId : DodgeBackId;
                }
            }
        }

        if (!animator.HasState(0, stateId)) return false;
        if (stamina != null && !stamina.TrySpendDodge()) return false;
        if (attack != null && attack.IsAttacking) attack.Cancel();
        transform.rotation = nextRotation;
        cooldownTimer = set != null ? set.dodgeCooldown : dodgeCooldown;

        // Backward movement (and the neutral press) is a backstep hop — short and
        // quick — while forward/lateral are the long dart.
        isBackstep = dirLocal == Vector3.back;

        dodgeDir = transform.TransformDirection(dirLocal).normalized;
        // Locked keeps facing the target through the dart; the free roll already
        // turned the body above.

        // Roll-axis tilt into the dart direction sells the turn — the long
        // dart only, not the backstep hop.
        if (!isBackstep)
        {
            var camFwd = Camera.main != null ? Camera.main.transform.forward : transform.forward;
            tilt?.BeginTilt(new CameraDisplacementEvent(
                magnitude: 0.35f,
                tiltAmount: SlideController.DirectionalTilt(dodgeDir, camFwd, 8f),
                tiltInDuration: 0.08f, tiltOutDuration: 0.3f,
                pitchAmount: -2f, pitchInDuration: 0.08f, pitchOutDuration: 0.3f));
        }

        dodgeTime = isBackstep ? backDur : dur;
        dodgeSpeed = (isBackstep ? backDist : dist) / (dodgeTime * Mathf.Max(0.1f, (actEntryMult + actExitMult) * 0.5f));
        dodgeAge = 0f;
        IsDodging = true;
        state.IsDisplacing = true;
        state.IsInvulnerable = false;

        // Snap the strafe blend to neutral so re-entry to CombatMove doesn't lerp
        // from a stale direction.
        animator.SetFloat(MoveXId, 0f);
        animator.SetFloat(MoveYId, 0f);
        animator.CrossFadeInFixedTime(stateId, fadeDuration, 0);
        return true;
    }

    private void Update()
    {
        if (state != null && state.IsDead)
        {
            if (IsDodging)
            {
                IsDodging = false;
                state.IsDisplacing = false;
                state.IsInvulnerable = false;
            }
            return;
        }
        if (cooldownTimer > 0f)
            cooldownTimer -= Time.deltaTime;
        if (!IsDodging) return;
        var dt = Time.deltaTime;
        dodgeAge += dt;

        state.IsInvulnerable = dodgeAge >= actIFrameStart && dodgeAge <= actIFrameEnd;

        if (dodgeAge >= dodgeTime)
        {
            EndDodge();
            DodgeEndTime = Time.unscaledTime;
            return;
        }

        // Quickstep ease: punchy entry, fast settle. With root motion the relay
        // moves the capsule at the clip's authored direction + this speed.
        if (!DriveRootMotion)
        {
            var t = dodgeAge / dodgeTime;
            var speedNow = dodgeSpeed * Mathf.Lerp(actEntryMult, actExitMult, t);
            character.Move((dodgeDir * speedNow + Vector3.up * gravity) * dt);
        }
    }

    /// <summary>Another action takes over mid-dodge (the dodge-counter
    /// technique): drops the dodge without the locomotion hand-off — the
    /// caller owns IsDisplacing from here.</summary>
    public void Interrupt()
    {
        if (!IsDodging) return;
        IsDodging = false;
        DodgeEndTime = Time.unscaledTime;
        state.IsDisplacing = false;
        state.IsInvulnerable = false;
        tilt?.EndTilt();
    }

    private void EndDodge()
    {
        IsDodging = false;
        cooldownTimer = weaponSocket != null && weaponSocket.Set != null ? weaponSocket.Set.dodgeCooldown : dodgeCooldown;
        state.IsDisplacing = false;
        state.IsInvulnerable = false;
        tilt?.EndTilt();
        // Hand the dodge's exit speed to locomotion — it decays via the grounded
        // deceleration instead of snapping straight back to strafe speed.
        if (locomotion != null)
            locomotion.SetLocomotionSpeed(dodgeSpeed * actExitMult);
        var back = lockOn != null && lockOn.IsLockedOn ? CombatMoveId : LocomotionId;
        animator.CrossFadeInFixedTime(back, endFadeDuration, 0);
    }

    private void OnDisable()
    {
        if (!IsDodging) return;
        IsDodging = false;
        tilt?.EndTilt();
        if (state != null)
        {
            state.IsDisplacing = false;
            state.IsInvulnerable = false;
        }
    }

    private Animator FindValidAnimator()
    {
        foreach (var a in GetComponentsInChildren<Animator>(true))
        {
            if (a != null && a.enabled && a.runtimeAnimatorController != null && a.avatar != null && a.avatar.isHuman)
                return a;
        }
        return null;
    }
}
