using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerState))]
[DefaultExecutionOrder(-10)]
public sealed class PlayerLocomotion : MonoBehaviour
{
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField] private Animator animator;
    internal Animator BodyAnimator => animator;
    [SerializeField] private Transform view;
    [SerializeField, Min(0.05f)] private float slowWalkSpeed = 1f;
    [SerializeField, Min(0.05f)] private float walkSpeed = 3f;
    [SerializeField, Min(0.05f)] private float runSpeed = 6f;
    [SerializeField] private Vector4 cycleDurations = Vector4.one;
    [SerializeField, Min(0.1f)] private float acceleration = 12f;
    [Tooltip("Deceleration rate when slowing/stopping — higher than acceleration kills the end-of-movement glide.")]
    [SerializeField, Min(0.1f)] private float deceleration = 45f;
    [SerializeField, Min(0.1f)] private float turnSpeed = 540f;
    [SerializeField] private float gravity = -20f;
    [Tooltip("Damp time for the Animator Speed blend parameter. Higher = smoother transitions between idle/walk/run, but more foot sliding."), Min(0f)]
    [SerializeField] private float speedParamDampTime = 0.1f;
    [SerializeField, Min(0f)] private float jumpImpulse = 6f;
    [Tooltip("Maximum number of jumps before touching the ground. 2 = standard double jump."), Min(1)]
    [SerializeField] private int maxJumps = 2;
    [Tooltip("How many random double-jump variants are in the JumpDoubleAir blend tree."), Min(1)]
    [SerializeField] private int doubleJumpVariantCount = 2;
    [Tooltip("Cooldown after a double jump before another double jump can be performed.")]
    [SerializeField, Min(0f)] private float doubleJumpCooldown = 0.35f;
    [Tooltip("Gravity multiplier after the second jump. Lower values make it floatier."), Range(0.1f, 1f)]
    [SerializeField] private float doubleJumpGravityScale = 0.7f;
    [Tooltip("Extra gravity reduction near the second-jump apex."), Range(0.05f, 1f)]
    [SerializeField] private float doubleJumpApexGravityScale = 0.35f;
    [Tooltip("Vertical-speed range around the apex that receives extra float."), Min(0.1f)]
    [SerializeField] private float doubleJumpApexSpeed = 1.5f;
    [Tooltip("Gravity multiplier reached during the second-jump descent."), Min(0.1f)]
    [SerializeField] private float doubleJumpFallGravityScale = 1.2f;
    [Tooltip("Downward speed at which the descent reaches its full gravity multiplier."), Min(0.1f)]
    [SerializeField] private float doubleJumpFallGravityRampSpeed = 5f;

    [Header("Slide boost")]
    [Tooltip("Temporary run-speed multiplier after a slide ends. 1 = no boost."), Min(1f)]
    [SerializeField] private float slideRunBoost = 1.2f;
    [Tooltip("How long the post-slide run boost lasts. 0 = disabled."), Min(0f)]
    [SerializeField] private float slideRunBoostDuration = 0.8f;

    [Header("Lock-on movement")]
    [Tooltip("Move speed while locked on when no WeaponSet overrides it (strafe/jog pace).")]
    [SerializeField, Min(0.05f)] private float lockedMoveSpeed = 2.8f;
    [Tooltip("Turn-speed multiplier while locked; facing the target should feel snappier than free turning.")]
    [SerializeField, Min(0.1f)] private float lockedTurnMultiplier = 1.5f;
    [Tooltip("Damp time for the MoveX/MoveY strafe-blend parameters.")]
    [SerializeField, Min(0f)] private float moveParamDampTime = 0.08f;

    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int LocomotionStateId = Animator.StringToHash("Base Layer.Locomotion");
    private static readonly int JumpId = Animator.StringToHash("Jump");
    private static readonly int JumpForwardId = Animator.StringToHash("JumpForward");
    private static readonly int DoubleJumpId = Animator.StringToHash("DoubleJump");
    private static readonly int JumpVariantId = Animator.StringToHash("JumpVariant");
    private static readonly int VerticalSpeedId = Animator.StringToHash("VerticalSpeed");
    private static readonly int InAirId = Animator.StringToHash("InAir");
    private static readonly int JumpStartStateId = Animator.StringToHash("Base Layer.JumpStart");
    private static readonly int JumpAirStateId = Animator.StringToHash("Base Layer.JumpAir");
    private static readonly int JumpLandStateId = Animator.StringToHash("Base Layer.JumpLand");
    private static readonly int JumpForwardStartStateId = Animator.StringToHash("Base Layer.JumpForwardStart");
    private static readonly int JumpForwardAirStateId = Animator.StringToHash("Base Layer.JumpForwardAir");
    private static readonly int JumpForwardLandStateId = Animator.StringToHash("Base Layer.JumpForwardLand");
    private static readonly int JumpDoubleAirStateId = Animator.StringToHash("Base Layer.JumpDoubleAir");
    private static readonly int JumpDoubleFallStateId = Animator.StringToHash("Base Layer.JumpDoubleFall");
    private static readonly int LockedId = Animator.StringToHash("Locked");
    private static readonly int SprintingId = Animator.StringToHash("Sprinting");
    private static readonly int MoveXId = Animator.StringToHash("MoveX");
    private static readonly int MoveYId = Animator.StringToHash("MoveY");
    private static readonly int MoveSpeedScaleId = Animator.StringToHash("MoveSpeedScale");
    private static readonly int SprintSpeedScaleId = Animator.StringToHash("SprintSpeedScale");
    private static readonly float OneThird = 1f / 3f;
    private static readonly float TwoThirds = 2f / 3f;
    private static readonly float InputSplit = 0.5f;
    private CharacterController character;
    private PlayerState state;
    private PlayerStamina stamina;
    private InputActionAsset ownedActions;
    private InputAction moveAction;
    private InputAction sprintAction;
    private InputAction jumpAction;

    public InputAction MoveAction => moveAction;
    public InputAction SprintAction => sprintAction;
    public InputAction JumpAction => jumpAction;

    private float locomotionSpeed;
    private float verticalSpeed;
    private bool ready;
    private bool hasVerticalSpeedParameter;
    private float runBoostTimer;
    private bool toggleSprint;
    private bool sprintToggled;
    private bool sprintMoving;
    private float sprintTime;
    private SlideController slide;
    private LockOnController lockOn;
    private WeaponSocket weaponSocket;
    private float jumpAge;
    private bool wasInAir;
    private bool landingPending; // touched down, waiting for the second grounded frame to confirm
    private float landedLock;
    private float jumpAirGrace;
    private float airMomentum;
    private int jumpsPerformed;
    private bool nextAirJumpUsesNormalAnim;
    private int ungroundedFrames;
    private int groundFrames;
    private int jumpConsumeFrame = -1;
    private float doubleJumpCooldownTimer;
    private const float LandedLockDuration = 0.15f;
    private const float JumpAirGrace = 0.15f;

    public float ActualPlanarSpeed { get; private set; }
    public Vector3 ActualPlanarVelocity { get; private set; }
    public float WalkSpeed => walkSpeed;
    public float RunSpeed => runSpeed;
    public bool IsDashing => state != null && state.IsDisplacing;
    /// <summary>Live sprint state incl. the stamina gate — AttackController
    /// reads it to route sprint presses into the dash-attack variant.</summary>
    public bool Sprinting => IsSprinting();
    /// <summary>Continuous seconds spent sprint-moving while grounded. Resets on
    /// release, stop, airtime or a displacement — gates the sprint attack.</summary>
    public float SprintTime => sprintTime;

    /// <summary>Sprint accumulator — static so SoulsSelfChecks can verify it
    /// grows while sprinting and drops to zero the frame it isn't.</summary>
    public static float SprintClockStep(float current, bool sprinting, float dt)
        => sprinting ? current + dt : 0f;
    public float JumpAge => jumpAge;
    public bool HasTraversalBoost => runBoostTimer > 0f;
    /// <summary>Live vertical speed (negative = falling) — the dive attack gates
    /// on the fall having really started, and WallRun reads the jump action.</summary>
    public float VerticalSpeed => verticalSpeed;

    private float EffectiveRunSpeed
    {
        get
        {
            return runBoostTimer > 0f ? runSpeed * slideRunBoost : runSpeed;
        }
    }

    public void TriggerSlideRunBoost()
    {
        runBoostTimer = slideRunBoostDuration;
    }

    /// <summary>
    /// Lets a jump carry horizontal momentum (e.g., from a slide) instead of being recalculated from input.
    /// </summary>
    public void PreserveAirMomentum(float speed)
    {
        airMomentum = Mathf.Max(airMomentum, speed);
        locomotionSpeed = Mathf.Max(locomotionSpeed, airMomentum);
    }

    public void SetImmediateBlendForCurrentInput()
    {
        if (!ready || moveAction == null || sprintAction == null)
            return;
        var move = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
        var param = ComputeTargetParameter(move.magnitude, IsSprinting());
        animator.SetFloat(SpeedId, param);
    }

    /// <summary>
    /// Prevents the next Update from treating this frame's jump press as a locomotion jump.
    /// Traversal controllers (e.g. wall jump) consume the press for their own launch.
    /// </summary>
    public void ConsumeJumpPress()
    {
        jumpConsumeFrame = Time.frameCount;
    }

    /// <summary>
    /// Applies an external airborne velocity (e.g. a wall-run exit or wall-jump launch) through
    /// the normal gravity/momentum path so the player keeps flying and falling naturally.
    /// </summary>
    public void ApplyAirVelocity(Vector3 velocity)
    {
        var planar = Vector3.ProjectOnPlane(velocity, Vector3.up);
        airMomentum = Mathf.Max(airMomentum, planar.magnitude);
        locomotionSpeed = Mathf.Max(locomotionSpeed, airMomentum);
        verticalSpeed = velocity.y;
        jumpAge = 0f;
        jumpAirGrace = JumpAirGrace;
        wasInAir = true;
        landedLock = 0f;
        animator.SetBool(InAirId, true);
    }

    /// <summary>Return vertical ownership after anticipation/attack without refreshing jumps or air grace.</summary>
    public void ResumeVerticalMotion(float speed)
    {
        verticalSpeed = character != null && character.isGrounded ? -2f : Mathf.Clamp(speed, -50f, 50f);
        if (animator != null && hasVerticalSpeedParameter) animator.SetFloat(VerticalSpeedId, verticalSpeed);
    }

    /// <summary>Ends an externally owned, verified landing without replaying
    /// the ordinary jump-land transition or granting another airborne jump.</summary>
    public void CompleteExternalLanding()
    {
        verticalSpeed = -2f;
        wasInAir = false;
        jumpAirGrace = 0f;
        landedLock = LandedLockDuration;
        ungroundedFrames = 0;
        groundFrames = 2;
        jumpsPerformed = 0;
        airMomentum = 0f;
        nextAirJumpUsesNormalAnim = false;
        jumpAge = 0f;
        if (animator == null) return;
        animator.SetBool(InAirId, false);
        if (hasVerticalSpeedParameter) animator.SetFloat(VerticalSpeedId, verticalSpeed);
    }

    /// <summary>
    /// Wall contact refreshes air mobility: after a wall run the player always has at least
    /// one air jump remaining, as if only the initial ground jump had been used.
    /// </summary>
    public void RefreshAirJumps()
    {
        jumpsPerformed = Mathf.Clamp(jumpsPerformed, 1, Mathf.Max(1, maxJumps - 1));
        doubleJumpCooldownTimer = 0f;
    }

    /// <summary>
    /// Marks the next airborne jump to replay the normal jump animation instead of the
    /// double-jump flip — used after a wall jump so the chain reads as one fluid vault.
    /// Clears when the player lands.
    /// </summary>
    public void UseNormalAnimForNextAirJump()
    {
        nextAirJumpUsesNormalAnim = true;
    }

    /// <summary>
    /// Overrides the internal locomotion speed, e.g. to carry a slide's exit speed into normal movement.
    /// Clamped to the current effective run speed so traversal states cannot push locomotion above its configured range.
    /// </summary>
    public void SetLocomotionSpeed(float speed)
    {
        locomotionSpeed = Mathf.Clamp(speed, 0f, EffectiveRunSpeed);
    }

    /// <summary>Armed strafe/jog speed cap — the weapon's own pace or the default.</summary>
    private float ArmedSpeedCap() =>
        weaponSocket != null && weaponSocket.Set != null ? weaponSocket.Set.moveSpeed : lockedMoveSpeed;

    private bool IsSprinting()
    {
        if (sprintAction == null)
            return false;
        // A committed sip never sprints (detail §drinks).
        if (state != null && state.IsDrinking)
            return false;
        // Hysteresis gate — once the bar empties, sprint stays off until the
        // pool refills past the resume fraction (PlayerStamina.CanSprint).
        if (stamina != null && !stamina.CanSprint)
            return false;
        if (toggleSprint)
            return sprintToggled;
        return sprintAction.IsPressed();
    }

    public void SetSprintKeyboardBinding(string path)
    {
        if (sprintAction == null || string.IsNullOrEmpty(path))
            return;
        sprintAction.Disable();
        if (sprintAction.bindings.Count > 0)
            sprintAction.ApplyBindingOverride(0, path);
        if (enabled) sprintAction.Enable();
    }

    public void SetJumpKeyboardBinding(string path)
    {
        if (jumpAction == null || string.IsNullOrEmpty(path))
            return;
        jumpAction.Disable();
        if (jumpAction.bindings.Count > 0)
            jumpAction.ApplyBindingOverride(0, path);
        if (enabled) jumpAction.Enable();
    }

    public void SetSprintMode(bool toggle)
    {
        toggleSprint = toggle;
        if (!toggle)
            sprintToggled = false;
    }

    private void Awake()
    {
        character = GetComponent<CharacterController>();
        stamina = GetComponent<PlayerStamina>();

        if (animator == null || animator.runtimeAnimatorController == null || animator.avatar == null || !animator.avatar.isHuman)
            animator = FindValidAnimator();

        if (inputActions == null || animator == null || view == null ||
            slowWalkSpeed <= 0f || walkSpeed <= slowWalkSpeed || runSpeed <= walkSpeed ||
            cycleDurations.x <= 0f || cycleDurations.y <= 0f || cycleDurations.z <= 0f || cycleDurations.w <= 0f)
        {
            var reason = inputActions == null ? "InputActionAsset missing" :
                         animator == null ? "Animator missing" :
                         view == null ? "View (camera) Transform missing" :
                         slowWalkSpeed <= 0f ? "slowWalkSpeed invalid" :
                         walkSpeed <= slowWalkSpeed ? "walkSpeed <= slowWalkSpeed" :
                         runSpeed <= walkSpeed ? "runSpeed <= walkSpeed" :
                         "cycleDurations must be > 0";
            Debug.LogError("PlayerLocomotion disabled: " + reason + ". Run Step 6 to re-apply references.", this);
            enabled = false;
            return;
        }

        ownedActions = Instantiate(inputActions);
        ownedActions.Disable();
        moveAction = ownedActions.FindAction("Player/Move");
        sprintAction = ownedActions.FindAction("Player/Sprint");
        jumpAction = ownedActions.FindAction("Player/Jump");
        if (moveAction == null || sprintAction == null || jumpAction == null)
        {
            Debug.LogError("PlayerLocomotion requires Player/Move, Player/Sprint and Player/Jump actions.", this);
            enabled = false;
            return;
        }
        // Root motion is generated (deltas reach OnAnimatorMove) but only the
        // RootMotionRelay applies them — attack/dodge/skill owners only. With
        // no owner the deltas are dropped, same as applyRootMotion=false.
        animator.applyRootMotion = true;
        if (animator.GetComponent<RootMotionRelay>() == null)
            animator.gameObject.AddComponent<RootMotionRelay>();
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        foreach (var parameter in animator.parameters)
        {
            if (parameter.nameHash == VerticalSpeedId && parameter.type == AnimatorControllerParameterType.Float)
            {
                hasVerticalSpeedParameter = true;
                break;
            }
        }
        ready = true;
        if (!GetComponent<TraversalEffects>()) gameObject.AddComponent<TraversalEffects>();
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

    private void Start()
    {
        if (!ready)
            return;
        state = GetComponent<PlayerState>();
        slide = GetComponent<SlideController>();
        lockOn = GetComponent<LockOnController>();
        weaponSocket = GetComponent<WeaponSocket>();
        if (state == null)
            Debug.LogWarning("PlayerLocomotion expects a PlayerState on the same object for rooted/displacement checks.", this);
    }

    private void OnEnable()
    {
        if (!ready)
            return;
        if (animator != null && animator.GetCurrentAnimatorStateInfo(0).fullPathHash != LocomotionStateId)
            animator.Play(LocomotionStateId, 0, 0f);
        wasInAir = false;
        landedLock = 0f;
        jumpAge = 0f;
        jumpAirGrace = 0f;
        airMomentum = 0f;
        moveAction.Enable();
        sprintAction.Enable();
        jumpAction.Enable();
    }

    private void OnDisable()
    {
        moveAction?.Disable();
        sprintAction?.Disable();
        jumpAction?.Disable();
        locomotionSpeed = 0f;
        verticalSpeed = 0f;
        ActualPlanarSpeed = 0f;
        jumpAge = 0f;
        wasInAir = false;
        landedLock = 0f;
        jumpAirGrace = 0f;
        airMomentum = 0f;
        if (ready)
            animator.SetFloat(SpeedId, 0f);
    }

    private void OnDestroy()
    {
        if (ownedActions != null)
            Destroy(ownedActions);
    }

    private void Update()
    {
        var dt = Time.deltaTime;
        if (!ready || dt <= 0f || !character.enabled)
            return;

        if (state != null && (state.IsRooted || state.IsDead))
        {
            // Let the Speed parameter stay at the last valid input value so a crouch->locomotion
            // transition blends to the correct pose instead of snapping to idle.
            return;
        }

        var move = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
        var moveMag = move.magnitude;

        var drinking = state != null && state.IsDrinking;
        if (toggleSprint && sprintAction != null && sprintAction.WasPressedThisFrame() && !drinking)
            sprintToggled = !sprintToggled;
        if (drinking) sprintToggled = false; // a sip never leaves a latched sprint

        var jumpPressed = !drinking && jumpAction != null && jumpAction.WasPressedThisFrame() && jumpConsumeFrame != Time.frameCount;
        var hasJumpStates = animator.HasState(0, JumpStartStateId) &&
                            animator.HasState(0, JumpAirStateId) &&
                            animator.HasState(0, JumpLandStateId);
        var hasJumpForwardStates = animator.HasState(0, JumpForwardStartStateId) &&
                                   animator.HasState(0, JumpForwardAirStateId) &&
                                   animator.HasState(0, JumpForwardLandStateId);

        if (jumpPressed)
        {
            var cancelledFromSlide = state != null && state.IsDisplacing && slide != null && slide.TryCancelIntoJump();
            var blockedBySlide = state != null && state.IsDisplacing && !cancelledFromSlide;
            if (!blockedBySlide)
            {
                bool canFirstJump = character.isGrounded && (hasJumpStates || hasJumpForwardStates);
                bool canDoubleJump = !character.isGrounded && jumpsPerformed >= 1 && jumpsPerformed < maxJumps &&
                                     doubleJumpCooldownTimer <= 0f &&
                                     animator.HasState(0, JumpDoubleAirStateId);

                if (canFirstJump)
                {
                    jumpAge = 0f;
                    jumpAirGrace = JumpAirGrace;
                    verticalSpeed = jumpImpulse;
                    jumpsPerformed = 1;
                    GetComponent<TraversalEffects>()?.JumpBurst(false);

                    // If this jump cancelled out of a slide, the slide already passed its exit speed
                    // into PreserveAirMomentum; don't overwrite it with the current CharacterController
                    // velocity (which may be spiking due to the slide root motion).
                    if (!cancelledFromSlide)
                    {
                        var actualHorizontal = Vector3.ProjectOnPlane(character.velocity, Vector3.up).magnitude;
                        PreserveAirMomentum(actualHorizontal);
                    }

                    // The slide controller handles the cross-fade into jump; don't re-fire the trigger
                    // or the animator may queue a second jump transition.
                    if (!cancelledFromSlide)
                    {
                        if (moveMag > 0.1f && hasJumpForwardStates)
                            animator.SetTrigger(JumpForwardId);
                        else if (hasJumpStates)
                            animator.SetTrigger(JumpId);
                    }
                }
                else if (canDoubleJump)
                {
                    jumpAge = 0f;
                    jumpAirGrace = JumpAirGrace;
                    verticalSpeed = jumpImpulse;
                    jumpsPerformed++;
                    GetComponent<TraversalEffects>()?.JumpBurst(true);

                    // Preserve horizontal momentum into the second jump.
                    PreserveAirMomentum(ActualPlanarSpeed);

                    if (nextAirJumpUsesNormalAnim)
                    {
                        // Wall-jump chain: replay the normal forward jump start instead of
                        // the TurnAir flip. CrossFade is unconditional, so it works from
                        // any air state and re-fires the start clip.
                        nextAirJumpUsesNormalAnim = false;
                        animator.CrossFadeInFixedTime(
                            hasJumpForwardStates ? JumpForwardStartStateId : JumpStartStateId,
                            0.08f, 0);
                    }
                    else
                    {
                        // Pick a random variant for the JumpDoubleAir blend tree.
                        var variant = doubleJumpVariantCount > 1 ? Random.Range(0, doubleJumpVariantCount) : 0;
                        animator.SetFloat(JumpVariantId, variant);
                        animator.SetTrigger(DoubleJumpId);
                    }
                    doubleJumpCooldownTimer = doubleJumpCooldown;
                }
            }
        }

        if (state != null && state.IsDisplacing)
        {
            // Do not zero the Speed parameter; EndSlide sets the correct blend target and we want
            // the transition back to Locomotion to continue from a run blend rather than idle.
            // The sprint clock dies here, though — attacks/dodges/slides all set
            // IsDisplacing, and the sprint-attack gate demands an uninterrupted
            // sprint: a banked ≥threshold value would make EVERY later press a
            // launcher instead of re-arming for a fresh clean window.
            sprintTime = 0f;
            return;
        }

        var forward = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        var right = Vector3.Cross(Vector3.up, forward);
        var direction = forward * move.y + right * move.x;
        var isSprinting = IsSprinting();

        // Sprint-move gate with hysteresis: enter past 0.25, release below 0.12.
        // A bare threshold let stick noise flicker Sprinting every frame, which
        // ping-ponged CombatMove↔CombatSprint and made the armed run look glitchy.
        sprintMoving = isSprinting && moveMag > (sprintMoving ? 0.12f : 0.25f);

        // Continuous sprint duration — only "sprinted for a while" presses may
        // become dash attacks. Resets on release, a stop, airtime or a
        // displacement (slide/dodge) so banking it isn't possible.
        sprintTime = SprintClockStep(sprintTime,
            sprintMoving && character.isGrounded && (state == null || !state.IsDisplacing), dt);

        // Locked-on movement: face the target and drive the CombatMove strafe blend
        // with local-space input. Sprint drops the posture (Souls-style) — the
        // character faces the velocity and uses the normal run while sprinting,
        // then snaps back to facing the target on release. The lock itself holds.
        // Gate on actual sprint movement, not just the latched toggle — otherwise a
        // toggled-on sprint permanently disables target facing and the strafe blend.
        var inCombat = weaponSocket != null && weaponSocket.InCombat;
        var lockedMove = lockOn != null && lockOn.IsLockedOn && character.isGrounded && !sprintMoving;
        // Locked = armed moveset — enemies aggroing counts as combat, so the
        // weapon-drawn stance runs whenever a fight is on, not just on lock-on.
        animator.SetBool(LockedId, inCombat);
        // Sprint intent (not measured speed) drives CombatSprint — waiting for the
        // physical speed ramp made the armed-run transition feel late.
        animator.SetBool(SprintingId, sprintMoving);
        // Sprint drains the stamina economy — IsSprinting() drops to false the
        // frame the bar empties, so this stays self-limiting.
        if (sprintMoving)
            stamina?.ConsumeSprint(Time.deltaTime);

        Vector3 moveDir;
        float targetSpeed;
        var targetParameter = ComputeTargetParameter(moveMag, isSprinting);
        if (lockedMove)
        {
            var toTarget = Vector3.ProjectOnPlane(lockOn.AimPosition - transform.position, Vector3.up);
            if (toTarget.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(toTarget.normalized), turnSpeed * lockedTurnMultiplier * dt);

            var local = transform.InverseTransformDirection(direction);
            animator.SetFloat(MoveXId, local.x, moveParamDampTime, dt);
            animator.SetFloat(MoveYId, local.z, moveParamDampTime, dt);

            var speedCap = ArmedSpeedCap();
            // The strafe blend is direction-only — scale its playback rate by actual
            // velocity or the feet outpace the capsule during acceleration.
            animator.SetFloat(MoveSpeedScaleId, Mathf.Max(0.3f, ActualPlanarSpeed / Mathf.Max(0.01f, speedCap)),
                              moveParamDampTime, dt);
            moveDir = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;
            targetSpeed = moveMag * speedCap;
        }
        else
        {
            if (direction.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), turnSpeed * dt);
            moveDir = transform.forward;
            targetSpeed = SpeedFromBlend(targetParameter, slowWalkSpeed, walkSpeed, EffectiveRunSpeed, cycleDurations);

            if (inCombat)
            {
                // Armed but not locked: face the travel direction and feed the
                // strafe blend straight-forward input — plays the weapon-held jog.
                animator.SetFloat(MoveXId, 0f, moveParamDampTime, dt);
                animator.SetFloat(MoveYId, moveMag, moveParamDampTime, dt);
                animator.SetFloat(MoveSpeedScaleId, Mathf.Max(0.3f, ActualPlanarSpeed / Mathf.Max(0.01f, ArmedSpeedCap())),
                                  moveParamDampTime, dt);
            }
            else
            {
                // CombatMove isn't driving — decay its params toward idle so it
                // re-enters on neutral values after sprint or airtime.
                animator.SetFloat(MoveXId, 0f, moveParamDampTime, dt);
                animator.SetFloat(MoveYId, 0f, moveParamDampTime, dt);
                animator.SetFloat(MoveSpeedScaleId, 1f, moveParamDampTime, dt);
            }
        }

        var airborne = jumpAirGrace > 0f || !character.isGrounded;
        if (airborne && airMomentum > 0f)
            targetSpeed = Mathf.Max(targetSpeed, airMomentum);
        // Committed sip: half-speed walk/strafe (detail §drinks) — neutral
        // input still stands still; no sprint/jump reach this point.
        if (drinking) targetSpeed *= 0.5f;

        var accel = targetSpeed < locomotionSpeed && character.isGrounded ? deceleration : acceleration;
        locomotionSpeed = Mathf.MoveTowards(locomotionSpeed, targetSpeed, accel * dt);

        var gravityScale = 1f;
        if (jumpsPerformed >= 2 && !character.isGrounded)
        {
            if (verticalSpeed >= 0f)
            {
                gravityScale = verticalSpeed <= doubleJumpApexSpeed ? doubleJumpApexGravityScale : doubleJumpGravityScale;
            }
            else
            {
                var fallRamp = Mathf.InverseLerp(0f, doubleJumpFallGravityRampSpeed, -verticalSpeed);
                gravityScale = Mathf.Lerp(doubleJumpApexGravityScale, doubleJumpFallGravityScale, fallRamp);
            }
        }

        // The -2 stick force only applies once the ground contact holds for a
        // second frame — a one-frame isGrounded flicker mid-fall must not dump
        // accumulated fall speed (it read as a mid-air stall).
        if (character.isGrounded) groundFrames++; else groundFrames = 0;
        verticalSpeed = groundFrames >= 2 && verticalSpeed < 0f ? -2f : Mathf.Max(verticalSpeed + gravity * gravityScale * dt, -50f);
        if (hasVerticalSpeedParameter)
            animator.SetFloat(VerticalSpeedId, verticalSpeed);
        var before = transform.position;
        var collisions = character.Move((moveDir * locomotionSpeed + Vector3.up * verticalSpeed) * dt);
        if ((collisions & CollisionFlags.Above) != 0 && verticalSpeed > 0f)
            verticalSpeed = 0f;
        ActualPlanarVelocity = Vector3.ProjectOnPlane(transform.position - before, Vector3.up) / dt;
        ActualPlanarSpeed = ActualPlanarVelocity.magnitude;

        if (hasJumpStates || hasJumpForwardStates)
        {
            var inAirNow = !character.isGrounded;
            if (inAirNow)
            {
                ungroundedFrames++;
                // Mask 1-2 frame isGrounded flicker while running; otherwise the InAir
                // bool bounces and the animator stutters between locomotion and fall.
                // Only while falling SLOWLY (running/landing) — a real fall flicker
                // at speed must stay "in air" or the land state plays mid-flight.
                if (ungroundedFrames < 3 && !wasInAir && jumpAirGrace <= 0f && verticalSpeed > -4f)
                    inAirNow = false;
            }
            else
            {
                ungroundedFrames = 0;
            }
            if (jumpAirGrace > 0f)
            {
                // A new jump overrides any landing lock so the next jump branch can play immediately.
                landedLock = 0f;
                jumpAirGrace = Mathf.Max(0f, jumpAirGrace - dt);
                inAirNow = true;
            }
            else if (landedLock > 0f)
            {
                landedLock = Mathf.Max(0f, landedLock - dt);
                inAirNow = false;
            }
            else if ((wasInAir || landingPending) && !inAirNow)
            {
                // Require the contact to hold a second frame — a mid-fall
                // isGrounded flicker must not arm the landing lock, or the
                // land state (CombatLand) plays while still falling.
                // groundFrames is counted BEFORE this frame's Move, so the
                // touchdown frame reads 0: latch it and confirm a frame later
                // (wasInAir alone was already false by then — the landing
                // effect/lock never fired).
                if (groundFrames >= 2)
                {
                    landingPending = false;
                    landedLock = LandedLockDuration;
                    inAirNow = false;
                    GetComponent<TraversalEffects>()?.Land();
                }
                else landingPending = true;
            }
            if (inAirNow && jumpAirGrace <= 0f && ungroundedFrames >= 3) landingPending = false;
            animator.SetBool(InAirId, inAirNow);
            wasInAir = inAirNow;

            if (!inAirNow)
            {
                airMomentum = 0f;
                nextAirJumpUsesNormalAnim = false;
                if (character.isGrounded && !IsInJumpState())
                    jumpsPerformed = 0;
            }
        }

        if (IsInJumpState())
            jumpAge += dt;
        else
            jumpAge = 0f;

        var speedParam = BlendFromSpeed(ActualPlanarSpeed, slowWalkSpeed, walkSpeed, runSpeed, cycleDurations);
        if (runBoostTimer > 0f && character.isGrounded && ActualPlanarSpeed > runSpeed + 0.05f)
            speedParam = Mathf.Lerp(1f, 1.5f, Mathf.InverseLerp(runSpeed, EffectiveRunSpeed, ActualPlanarSpeed));
        animator.SetFloat(SpeedId, speedParam, speedParamDampTime, dt);
        // CombatSprint's clip rate tracks actual velocity the same way — full rate
        // at run speed, slower through the acceleration ramp, slightly overdrive on
        // slide boosts. Floored so a blocked sprint pose doesn't freeze solid.
        animator.SetFloat(SprintSpeedScaleId,
                          Mathf.Max(0.3f, ActualPlanarSpeed / Mathf.Max(0.01f, runSpeed)),
                          moveParamDampTime, dt);

        // Only count the run boost while grounded, so a slide-jump still has its full boost
        // after the player lands.
        if (runBoostTimer > 0f && character.isGrounded)
            runBoostTimer = Mathf.Max(0f, runBoostTimer - dt);

        if (doubleJumpCooldownTimer > 0f)
            doubleJumpCooldownTimer = Mathf.Max(0f, doubleJumpCooldownTimer - dt);
    }

    private bool IsInJumpState()
    {
        if (animator == null) return false;
        var hash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
        return hash == JumpStartStateId || hash == JumpAirStateId || hash == JumpLandStateId ||
               hash == JumpForwardStartStateId || hash == JumpForwardAirStateId || hash == JumpForwardLandStateId ||
               hash == JumpDoubleAirStateId || hash == JumpDoubleFallStateId;
    }

    private static float ComputeTargetParameter(float moveMagnitude, bool sprinting)
    {
        moveMagnitude = Mathf.Clamp01(moveMagnitude);
        if (moveMagnitude < InputSplit)
        {
            return (moveMagnitude / InputSplit) * OneThird;
        }

        var fraction = (moveMagnitude - InputSplit) / (1f - InputSplit);
        if (sprinting)
        {
            return TwoThirds + fraction * OneThird;
        }
        return OneThird + fraction * OneThird;
    }

    public static float SpeedFromBlend(float parameter, float slowWalk, float walk, float run, Vector4 durations)
    {
        parameter = Mathf.Clamp01(parameter);
        if (parameter < OneThird)
        {
            var weight = Mathf.Clamp01(parameter / OneThird);
            return CycleSpeed(weight, 0f, slowWalk, durations.x, durations.y);
        }

        if (parameter < TwoThirds)
        {
            var weight = Mathf.Clamp01((parameter - OneThird) / OneThird);
            return CycleSpeed(weight, slowWalk, walk, durations.y, durations.z);
        }

        var runWeight = Mathf.Clamp01((parameter - TwoThirds) / OneThird);
        return CycleSpeed(runWeight, walk, run, durations.z, durations.w);
    }

    public static float BlendFromSpeed(float speed, float slowWalk, float walk, float run, Vector4 durations)
    {
        speed = Mathf.Clamp(speed, 0f, run);
        if (speed <= slowWalk)
        {
            var weight = CycleWeight(speed, 0f, slowWalk, durations.x, durations.y);
            return weight * OneThird;
        }

        if (speed <= walk)
        {
            var weight = CycleWeight(speed, slowWalk, walk, durations.y, durations.z);
            return OneThird + weight * OneThird;
        }

        var runWeight = CycleWeight(speed, walk, run, durations.z, durations.w);
        return TwoThirds + runWeight * OneThird;
    }

    private static float CycleSpeed(float weight, float slow, float fast, float slowDuration, float fastDuration)
    {
        return Mathf.Lerp(slow * slowDuration, fast * fastDuration, weight) / Mathf.Lerp(slowDuration, fastDuration, weight);
    }

    private static float CycleWeight(float speed, float slow, float fast, float slowDuration, float fastDuration)
    {
        var numerator = (speed - slow) * slowDuration;
        return numerator / Mathf.Max(0.0001f, (fast - speed) * fastDuration + numerator);
    }
}
// Lightweight, pooled traversal cues. Attachments read the finished humanoid rig;
// no mesh, material, texture or bone assets are changed.
[DefaultExecutionOrder(120)]
public sealed class TraversalEffects : MonoBehaviour
{
    [Header("Arcane traversal")]
    public Color colour = new Color(.55f, .21f, 1f, 1f);
    [Range(.1f, 4f)] public float intensity = 1.8f;
    [Range(.05f, .5f)] public float handRadius = .16f;
    [Range(.05f, .5f)] public float footRadius = .2f;
    [Range(.005f, .12f)] public float ringWidth = .075f;
    [Range(0f, 1f)] public float burstStrength = 1f;
    [Tooltip("Overall see-through-ness of every player effect (rings, shards, blade forge, afterimages). 1 = fully solid.")]
    [Range(.1f, 1f)] public float opacity = .55f;
    [Tooltip("Strength of the dark ink underlay relative to its coloured stroke.")]
    [Range(0f, 1f)] public float inkStrength = .45f;
    [Tooltip("Fall speed (m/s, negative) past which a landing reads as a hard landing.")]
    public float hardLandSpeed = -10f;
    [Header("Dodge afterimages")]
    [Tooltip("Metres travelled between afterimages during a dodge.")]
    [Range(.2f, 1.2f)] public float afterimageSpacing = .5f;
    [Tooltip("Afterimages per dodge (each is a full baked pose — keep it modest).")]
    [Range(1, 8)] public int afterimageCount = 5;
    [Tooltip("Afterimages dropped along a double jump's rise (0 = off).")]
    [Range(0, 6)] public int doubleJumpAfterimages = 3;
    [Tooltip("Metres risen/travelled between double-jump afterimages.")]
    [Range(.15f, 1f)] public float doubleJumpSpacing = .35f;

    // Effect palette = the Core Energy colours (CoreConduit) so world FX and the
    // HUD read as one system: highlight-pale takeoff, main violet energy, shadow
    // violet sprint, white cores.
    static readonly Color PaleViolet = Color.Lerp(Color.white, CoreConduit.Bright, .4f);
    static readonly Color DarkViolet = CoreConduit.Shadow;
    static readonly Color Dust = new Color(.4f, .36f, .34f, .55f);

    // Visual language (matches the Mechanical Environment / pixel HUD): faceted
    // polygons, not smooth neon circles; every coloured stroke sits on a darker
    // ink underlay like the environment's crease ink; fades are STEPPED (four
    // bands, the banded-lighting read) and glow stays near flat colour. Shards
    // are low-poly triangles tumbling in 3D, like chips of the faceted world.
    static readonly Color Ink = new Color(.05f, .02f, .09f, 1f);
    sealed class Ring
    {
        public LineRenderer line, ink;
        public float age, life, radius, expansion, width, spin;
        public int sides;
        public Vector3 centre, normal;
        public Color col;
    }
    /// A Core sigil stamped on the wall where a hand/foot plants during a wall-run:
    /// hex rim + inner diamond, each over ink. Stays put, so the run leaves a trail.
    sealed class Stamp
    {
        public LineRenderer hex, hexInk, dia, diaInk;
        public Vector3 centre, normal;
        public float age = 9f, life, radius, spin;
    }
    /// Blade materialise / dissolve: a scan line along the blade (follows the
    /// weapon while it plays), ink underlay, shards gathering in or breaking off.
    sealed class Forge
    {
        public LineRenderer line, ink;
        public Transform blade;
        public Vector3 baseLocal, tipLocal, baseWorld, tipWorld;
        public float age = 9f, life;
        public bool appear;
        public Color col;
    }
    readonly System.Collections.Generic.List<Stamp> stamps = new System.Collections.Generic.List<Stamp>();
    readonly System.Collections.Generic.List<Forge> forges = new System.Collections.Generic.List<Forge>();
    float stampClock;
    int stampSide;
    bool wasAttached;
    const float StampLife = 1.1f;
    /// One baked pose of the player, left on the dodge path.
    sealed class Ghost
    {
        public GameObject go;
        public Mesh mesh;
        public MeshRenderer renderer;
        public float age = 9f;
    }
    readonly System.Collections.Generic.List<Ring> pool = new System.Collections.Generic.List<Ring>();
    readonly System.Collections.Generic.List<Ghost> ghosts = new System.Collections.Generic.List<Ghost>();
    readonly Transform[] limbs = new Transform[4];         // LH, RH, LF, RF — wall side picked per-frame
    readonly Vector3[] circle = new Vector3[33];
    const float GhostLife = .42f;
    static readonly int TintId = Shader.PropertyToID("_Tint");
    static readonly int RimId = Shader.PropertyToID("_Rim");
    static readonly int InkId = Shader.PropertyToID("_Ink");
    static readonly int FadeId = Shader.PropertyToID("_Fade");
    static readonly int DissolveId = Shader.PropertyToID("_Dissolve");
    static Mesh shardMesh;
    MaterialPropertyBlock block;
    Color32[] whites = System.Array.Empty<Color32>();
    SkinnedMeshRenderer[] skins;
    Animator animator;
    PlayerLocomotion locomotion;
    WallRunController wall;
    DodgeController dodge;
    PlayerState state;
    CharacterController capsule;
    Material material, ghostMaterial;
    ParticleSystem sparks;
    Coroutine gallery;
    bool boosted, dodging, sprinting;
    float peakFall;
    // The running afterimage chain (dodge or double jump): ghosts still to drop,
    // their spacing, and an age limit so a stalled chain stops.
    int ghostsLeft;
    bool chainIsDodge, doubleJumpChainPending;
    float ghostSpacing, ghostClock, chainAge, chainMaxAge;
    Vector3 lastGhostAt;
    // BakeMesh's output frame/scale, measured once against a hand-skinned vertex
    // (see CalibrateBake) instead of trusted.
    bool bakeCalibrated;
    float bakeScale = 1f;
    Vector3 bakeOffset;
    readonly RaycastHit[] floorHits = new RaycastHit[8];
    public int JumpBursts { get; private set; }
    public int DoubleJumpBursts { get; private set; }
    public int BoostBursts { get; private set; }
    public int Landings { get; private set; }
    public int WallJumpBursts { get; private set; }
    public int VisibleWallRings { get; private set; }

    void Awake()
    {
        locomotion = GetComponent<PlayerLocomotion>(); wall = GetComponent<WallRunController>();
        dodge = GetComponent<DodgeController>();
        state = GetComponent<PlayerState>(); capsule = GetComponent<CharacterController>();
        // Reuse the animator locomotion already validated — a second search that
        // skips inactive children can come up empty here and silently kill all FX.
        animator = locomotion != null ? locomotion.BodyAnimator : null;
        if (animator == null)
            foreach (var a in GetComponentsInChildren<Animator>(true))
                if (a.isHuman && a.runtimeAnimatorController) { animator = a; break; }
        var shader = Shader.Find("Souls/TraversalGlow");
        if (!shader) { enabled = false; return; }
        material = new Material(shader) { name = "Traversal glow (runtime)" };
        var ghostShader = Shader.Find("Souls/Afterimage");
        if (ghostShader) ghostMaterial = new Material(ghostShader) { name = "Dodge afterimage (runtime)" };
        block = new MaterialPropertyBlock();
        // Wall-run: stamped Core sigils on the wall face (pooled) — no floating pads.
        for (int i = 0; i < 14; i++)
            stamps.Add(new Stamp { hexInk = MakeLine("Wall sigil ink", 0), hex = MakeLine("Wall sigil", 1),
                                   diaInk = MakeLine("Wall sigil core ink", 0), dia = MakeLine("Wall sigil core", 1) });
        for (int i = 0; i < 3; i++)
            forges.Add(new Forge { ink = MakeLine("Blade forge ink", 0), line = MakeLine("Blade forge", 1) });
        if (animator != null)
        {
            var bones = new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
                                HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
            for (int i = 0; i < limbs.Length; i++)
                limbs[i] = animator.GetBoneTransform(bones[i]);
        }
        for (int i = 0; i < 24; i++) pool.Add(new Ring { ink = MakeLine("Traversal pulse ink", 0), line = MakeLine("Traversal pulse", 1) });
        var obj = new GameObject("Traversal sparks"); obj.transform.SetParent(transform, false);
        sparks = obj.AddComponent<ParticleSystem>(); sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = sparks.main; main.playOnAwake = false; main.loop = true; main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 256; main.startLifetime = .35f; main.startSize = .06f; main.startSpeed = 0f;
        main.startRotation3D = true; main.gravityModifier = .6f;
        var emission = sparks.emission; emission.enabled = false;
        var shape = sparks.shape; shape.enabled = false;
        var overLife = sparks.colorOverLifetime; overLife.enabled = true;
        var gradient = new Gradient(); gradient.SetKeys(new[] { new GradientColorKey(Color.white,0), new GradientColorKey(Color.white,1) },
            new[] { new GradientAlphaKey(1,0), new GradientAlphaKey(0,1) }); overLife.color = gradient;
        var size = sparks.sizeOverLifetime; size.enabled = true; size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0,1,1,0));
        var renderer = sparks.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = material;
        // Low-poly chips tumbling in 3D (they flip and catch like flakes of the
        // faceted world) — not flat pixel squares, not stretched neon.
        renderer.renderMode = ParticleSystemRenderMode.Mesh; renderer.mesh = ShardMesh();
        renderer.alignment = ParticleSystemRenderSpace.World;
        var rot = sparks.rotationOverLifetime; rot.enabled = true; rot.separateAxes = true;
        rot.x = new ParticleSystem.MinMaxCurve(-9f, 9f); rot.y = new ParticleSystem.MinMaxCurve(-9f, 9f); rot.z = new ParticleSystem.MinMaxCurve(-6f, 6f);
        var drag = sparks.limitVelocityOverLifetime; drag.enabled = true; drag.drag = 3f;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
    }

    /// An irregular five-sided chip, ~1 unit across (particle size scales it) —
    /// enough facets to read as a broken stone flake, not a cartoon triangle.
    static Mesh ShardMesh()
    {
        if (shardMesh) return shardMesh;
        shardMesh = new Mesh { name = "Traversal shard" };
        shardMesh.vertices = new[] { Vector3.zero, new Vector3(.04f, .52f, .03f), new Vector3(-.4f, .2f, -.04f),
                                     new Vector3(-.3f, -.38f, .05f), new Vector3(.24f, -.44f, -.03f), new Vector3(.47f, .06f, .02f) };
        shardMesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
        shardMesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 1 };
        shardMesh.RecalculateBounds();
        return shardMesh;
    }

    // Facet counts: rings are 12-gons and inner marks 8-gons — still visibly
    // faceted (low-poly) but no longer read as hex/diamond symbols.
    const int RingSides = 12, CoreSides = 8;

    static Vector3 Tumble() => new Vector3(Random.value, Random.value, Random.value) * 360f;

    LineRenderer MakeLine(string name, int order = 1)
    {
        var obj = new GameObject(name); obj.transform.SetParent(transform, false);
        var line = obj.AddComponent<LineRenderer>(); line.sharedMaterial = material;
        line.useWorldSpace = true; line.positionCount = circle.Length; line.enabled = false;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; line.receiveShadows = false;
        line.numCornerVertices = 0; line.numCapVertices = 0; // hard facet corners
        line.sortingOrder = order; // ink (0) always under its coloured stroke (1)
        return line;
    }

    /// Near-flat colour: only a third of the old glow multiplier survives so the
    /// strokes read as paint, not bloom.
    Color Glow(Color c, float alpha) { var t = c * Mathf.Lerp(1f, intensity, .35f); t.a = alpha; return t; }

    /// Four-band stepped alpha — fades click down like the banded lighting.
    static float Stepped(float a) => a <= .01f ? 0f : Mathf.Ceil(Mathf.Clamp01(a) * 4f) / 4f;

    /// Faceted ring (polygon) over an ink underlay. <paramref name="spin"/> rotates the facets.
    void Draw(LineRenderer line, LineRenderer ink, Vector3 centre, Vector3 normal, float radius, float width, Color tint,
              int sides = RingSides, float spin = 0f)
    {
        if (normal.sqrMagnitude < .01f) normal = Vector3.up;
        var rotation = Quaternion.FromToRotation(Vector3.up, normal.normalized);
        sides = Mathf.Clamp(sides, 3, circle.Length - 1);
        for (int i = 0; i <= sides; i++)
        {
            float angle = spin + i * Mathf.PI * 2f / sides;
            circle[i] = centre + rotation * new Vector3(Mathf.Cos(angle)*radius, 0f, Mathf.Sin(angle)*radius);
        }
        var stepped = Stepped(tint.a);
        tint.a = stepped * opacity;
        line.positionCount = sides + 1; line.SetPositions(circle);
        line.startWidth = line.endWidth = width;
        line.startColor = line.endColor = tint; line.enabled = stepped > .01f;
        if (ink == null) return;
        ink.positionCount = sides + 1; ink.SetPositions(circle);
        ink.startWidth = ink.endWidth = width * 2.3f;
        var k = Ink; k.a = tint.a * inkStrength;
        ink.startColor = ink.endColor = k; ink.enabled = line.enabled;
    }

    void Pulse(Vector3 centre, Vector3 normal, float radius, float expansion, float life, Color col, float width = 1f, int sides = RingSides)
    {
        Ring chosen = null;
        foreach (var r in pool) if (r.life <= 0f) { chosen = r; break; }
        if (chosen == null) chosen = pool[0];
        chosen.age = 0; chosen.life = life; chosen.centre = centre; chosen.normal = normal;
        chosen.radius = radius; chosen.expansion = expansion; chosen.col = col; chosen.width = width;
        chosen.sides = sides; chosen.spin = Random.value * Mathf.PI;
    }

    /// Stamp a Core sigil on the wall (hand = small, foot = larger).
    void StampWall(Vector3 centre, Vector3 normal, float radius)
    {
        Stamp chosen = null;
        foreach (var s in stamps) if (s.age >= s.life) { chosen = s; break; }
        if (chosen == null) { chosen = stamps[0]; foreach (var s in stamps) if (s.age > chosen.age) chosen = s; }
        chosen.centre = centre + normal * .02f; chosen.normal = normal; chosen.radius = radius;
        chosen.age = 0f; chosen.life = StampLife; chosen.spin = Random.Range(-.2f, .2f);
        // Chips knocked off the wall face — flat-colour shards.
        Emit(centre, 3, 1.5f, normal * .5f + Vector3.down * .2f, .45f, colour, .8f, false);
        Emit(centre, 2, 1.1f, normal * .4f, .4f, DarkViolet, .9f, false);
    }

    void Emit(Vector3 point, int count, float speed, Vector3 bias, float life, Color col, float size = 1f, bool glow = true)
    {
        if (!sparks) return;
        var tint = glow ? Glow(col, burstStrength) : col;
        tint.a *= opacity;
        for (int i = 0; i < count; i++)
        {
            var direction = Random.insideUnitSphere; direction.y = Mathf.Abs(direction.y)*.6f;
            if (bias != Vector3.zero) direction = (direction + bias * 2f).normalized;
            sparks.Emit(new ParticleSystem.EmitParams { position = point, velocity = direction * speed * Random.Range(.6f, 1f),
                startColor = tint, startSize = Random.Range(.06f,.11f) * size, rotation3D = Tumble(),
                startLifetime = Random.Range(life*.6f, life) }, 1);
        }
    }

    Vector3 Feet => transform.TransformPoint(capsule.center) - Vector3.up * capsule.height * .5f;

    /// Covers the weapon materialize/despawn cut — a puff plus a small
    /// summon-ring at the hand so the blade never just pops in.
    public void WeaponFlash(Vector3 at)
    {
        if(Application.isPlaying) SfxBank.Play("weapon.draw",at);
        if (!enabled || !material) return;
        Emit(at, 8, 1.2f, Vector3.up * .3f, .35f, colour, 1f, false);
        Pulse(at, Vector3.up, .06f, .35f, .24f, colour, .8f, CoreSides);
    }

    /// Blade materialise (appear) / dissolve (vanish). Appear: shards converge
    /// onto the blade line, then a scan runs hilt → tip leaving the drawn edge,
    /// which flashes and clicks out. Vanish: the edge recedes tip-ward and the
    /// blade breaks into shards along its length. Tracks the weapon transform
    /// while it plays; <paramref name="tint"/> = the set's impact colour.
    public void WeaponForge(Transform blade, WeaponSet set, bool appear, Color? tint = null)
    {
        if (Application.isPlaying) SfxBank.Play("weapon.draw", blade != null ? blade.position : transform.position);
        if (!enabled || !material) return;
        if (blade == null || !BladeGeometry.TryGet(blade, set, out var b, out var tip)) { WeaponFlash(blade != null ? blade.position : transform.position); return; }
        Forge f = null;
        foreach (var x in forges) if (x.age >= x.life) { f = x; break; }
        if (f == null) f = forges[0];
        f.blade = blade; f.baseLocal = b; f.tipLocal = tip;
        f.baseWorld = blade.TransformPoint(b); f.tipWorld = blade.TransformPoint(tip);
        f.appear = appear; f.age = 0f; f.life = appear ? .34f : .28f;
        f.col = tint ?? (set != null ? set.impactTint : colour); f.col.a = 1f;
        var len = Vector3.Distance(f.baseWorld, f.tipWorld);
        var n = Mathf.Clamp(Mathf.RoundToInt(len * 9f), 5, 14);
        for (int i = 0; i < n; i++)
        {
            var p = Vector3.Lerp(f.baseWorld, f.tipWorld, (i + .5f) / n);
            if (appear)
            {
                // Born a hand-span out, flying in onto the edge as it forms.
                var off = Random.onUnitSphere * Random.Range(.25f, .45f);
                if (sparks) sparks.Emit(new ParticleSystem.EmitParams { position = p + off, velocity = -off / .14f,
                    startColor = Glow(i % 3 == 0 ? Color.white : f.col, opacity), startSize = Random.Range(.06f, .11f),
                    rotation3D = Tumble(), startLifetime = .14f }, 1);
            }
            else Emit(p, 1, 1.4f, Vector3.down * .25f, .45f, i % 3 == 0 ? Ink : f.col, 1.1f, false);
        }
    }

    void TickForges(float dt)
    {
        foreach (var f in forges)
        {
            if (f.age >= f.life) { if (f.line.enabled) { f.line.enabled = f.ink.enabled = false; } continue; }
            f.age += dt;
            var t = Mathf.Clamp01(f.age / f.life);
            // Follow the weapon while it exists; a destroyed summon keeps its last spot.
            if (f.blade != null) { f.baseWorld = f.blade.TransformPoint(f.baseLocal); f.tipWorld = f.blade.TransformPoint(f.tipLocal); }
            Vector3 a, z; float alpha, width;
            if (f.appear)
            {
                // 0–60%: scan hilt → tip; 60–100%: full edge flash, stepped out.
                var scan = Mathf.Clamp01(t / .6f);
                a = f.baseWorld; z = Vector3.Lerp(f.baseWorld, f.tipWorld, 1f - (1f - scan) * (1f - scan));
                alpha = t < .6f ? 1f : 1f - (t - .6f) / .4f;
                width = t < .6f ? .05f : .07f;
            }
            else
            {
                var k = t * t;
                a = Vector3.Lerp(f.baseWorld, f.tipWorld, k); z = f.tipWorld;
                alpha = 1f - t; width = .05f;
            }
            alpha = Stepped(alpha) * opacity;
            var c = Glow(t < .6f && f.appear ? Color.Lerp(Color.white, f.col, t / .6f) : f.col, alpha);
            f.line.positionCount = 2; f.line.SetPosition(0, a); f.line.SetPosition(1, z);
            f.line.startWidth = width; f.line.endWidth = width * .6f;
            f.line.startColor = f.line.endColor = c; f.line.enabled = alpha > 0f;
            f.ink.positionCount = 2; f.ink.SetPosition(0, a); f.ink.SetPosition(1, z);
            f.ink.startWidth = width * 2.4f; f.ink.endWidth = width * 1.5f;
            var ink = Ink; ink.a = alpha * inkStrength;
            f.ink.startColor = f.ink.endColor = ink; f.ink.enabled = f.line.enabled;
        }
    }

    /// Wall-jump kick: cone of sparks along the launch direction off the wall.
    public void KickBurst(Vector3 at, Vector3 dir)
    {
        WallJumpBursts++;
        if (!enabled || !material) return;
        Emit(at, 16, 2.6f, dir, .4f, colour);
        Pulse(at, dir, .12f, .7f, .3f, colour);
    }

    /// Plunge landing hit — the hard landing, at full strength.
    public void ImpactBurst(Vector3 point, Vector3 normal)
    {
        if (!enabled || !material) return;
        HardLand(point, normal, 1.3f);
    }

    /// Hit enemy: tiny flash at the exact contact — white core, purple rim.
    public void HitFlash(Vector3 point, Color tint) => HitFlash(point, tint, Vector3.zero, 1f);

    /// Hit enemy: white core + tint ring, a radial burst of ink-backed impact
    /// spikes in the camera plane (longest along the swing), and shards thrown
    /// along the blade's travel. <paramref name="strength"/> 1 = normal, ~2 = backstab/finisher.
    public void HitFlash(Vector3 point, Color tint, Vector3 swingDir, float strength)
    {
        if (!enabled || !material) return;
        var cam = Camera.main;
        var n = cam != null ? cam.transform.position - point : Vector3.up;
        Emit(point, 1, 0f, Vector3.zero, .07f, Color.white, 3.2f);
        Pulse(point, n, .04f, .3f, .12f, Color.white, .7f);
        Pulse(point, n, .08f, .45f, .18f, tint, .9f);

        strength = Mathf.Clamp(strength, .5f, 2.5f);
        var normal = n.sqrMagnitude > 1e-4f ? n.normalized : Vector3.up;
        var swing = Vector3.ProjectOnPlane(swingDir, normal);
        var hasSwing = swing.sqrMagnitude > 1e-4f;
        if (!hasSwing) swing = Vector3.ProjectOnPlane(Vector3.right, normal);
        swing.Normalize();
        var count = Mathf.RoundToInt(6 + 2 * strength);
        var spin = Random.value * Mathf.PI * 2f;
        for (int i = 0; i < count; i++)
        {
            var dir = Quaternion.AngleAxis((spin + i * Mathf.PI * 2f / count) * Mathf.Rad2Deg + Random.Range(-12f, 12f), normal) * swing;
            var along = Mathf.Abs(Vector3.Dot(dir, swing));
            var len = (.22f + .3f * along * along) * Random.Range(.8f, 1.15f) * Mathf.Lerp(1f, 1.5f, strength - 1f);
            Spike(point, dir, len, i % 3 == 0 ? Color.white : tint, .16f + .04f * strength);
        }
        if (hasSwing)
            Emit(point, Mathf.RoundToInt(5 * strength), 3.2f, swing * .9f, .3f, tint, .8f);
    }

    sealed class SpikeFx
    {
        public LineRenderer line, ink;
        public Vector3 origin, dir;
        public float age = 9f, life, length;
        public Color col;
    }
    readonly System.Collections.Generic.List<SpikeFx> spikes = new System.Collections.Generic.List<SpikeFx>();

    void Spike(Vector3 origin, Vector3 dir, float length, Color col, float life)
    {
        SpikeFx s = null;
        foreach (var x in spikes) if (x.age >= x.life) { s = x; break; }
        if (s == null)
        {
            if (spikes.Count >= 40) { s = spikes[0]; foreach (var x in spikes) if (x.age > s.age) s = x; }
            else { s = new SpikeFx { ink = MakeLine("Impact spike ink", 0), line = MakeLine("Impact spike", 1) }; spikes.Add(s); }
        }
        s.origin = origin; s.dir = dir; s.length = length; s.col = col; s.life = life; s.age = 0f;
    }

    /// Spikes shoot out fast (ease-out), the tail chases the head, they thin to
    /// a point and click out in steps — a drawn impact star, not a glow.
    void TickSpikes(float dt)
    {
        foreach (var s in spikes)
        {
            if (s.age >= s.life) { if (s.line.enabled) s.line.enabled = s.ink.enabled = false; continue; }
            s.age += dt;
            var t = Mathf.Clamp01(s.age / s.life);
            var head = 1f - (1f - t) * (1f - t) * (1f - t);
            var tail = t * t;
            var a = s.origin + s.dir * (s.length * (.12f + .5f * tail));
            var b = s.origin + s.dir * (s.length * (.3f + .7f * head));
            var stepped = Stepped(1f - t);
            var c = Glow(s.col, stepped * opacity);
            var w = Mathf.Lerp(.05f, .012f, t);
            s.line.positionCount = 2; s.line.SetPosition(0, a); s.line.SetPosition(1, b);
            s.line.startWidth = w; s.line.endWidth = w * .25f;
            s.line.startColor = s.line.endColor = c; s.line.enabled = stepped > 0f;
            s.ink.positionCount = 2; s.ink.SetPosition(0, a); s.ink.SetPosition(1, b);
            s.ink.startWidth = w * 2.2f; s.ink.endWidth = w * .8f;
            var k = Ink; k.a = c.a * inkStrength;
            s.ink.startColor = s.ink.endColor = k; s.ink.enabled = s.line.enabled;
        }
    }

    /// Called by locomotion once the landing has been confirmed on the ground.
    public void Land()
    {
        if (!enabled || !material) return;
        var fall = peakFall; peakFall = 0f;
        Landings++;
        SfxBank.Play("move.land", Feet, fall >= hardLandSpeed ? 1.4f : 0.7f);
        if (fall < hardLandSpeed) { HardLand(Feet, Vector3.up, 1f); return; }
        // Scaled by the drop: a hop gets a light hex tap; a real landing stamps a
        // Core sigil under the boots (the wall-run trail's mark), snaps a hex ring
        // open and kicks shards + dust — faceted, inked, stepped.
        var p = Feet + Vector3.up * .04f;
        var k = Mathf.InverseLerp(-3f, hardLandSpeed, fall); // 0 = hop … 1 = almost hard
        if (k <= .05f)
        {
            Pulse(p, Vector3.up, .12f, .38f, .2f, colour, .8f);
            Emit(p, 3, 1f, Vector3.up * .3f, .3f, Dust, 1.2f, false);
            return;
        }
        StampWall(p, Vector3.up, Mathf.Lerp(.3f, .48f, k));
        Pulse(p, Vector3.up, .12f, Mathf.Lerp(.6f, 1.1f, k), .26f, Color.white, 1.1f);
        Pulse(p, Vector3.up, .22f, Mathf.Lerp(1f, 1.7f, k), .4f, colour, 1.4f);
        Emit(p, Mathf.RoundToInt(Mathf.Lerp(5f, 12f, k)), 2.2f, Vector3.up * .25f, .38f, colour, .9f, false);
        Emit(p, Mathf.RoundToInt(Mathf.Lerp(4f, 9f, k)), 1.2f, Vector3.up * .35f, .5f, Dust, 1.5f, false);
    }

    /// Effect gallery / tuning: play the landing cue as if falling at <paramref name="fallSpeed"/> (m/s, negative).
    public void PreviewLand(float fallSpeed) { peakFall = fallSpeed; Land(); }

    void HardLand(Vector3 point, Vector3 normal, float scale)
    {
        var p = point + normal * .04f;
        StampWall(p, normal, .6f * scale); // big ground sigil under the impact
        Pulse(p, normal, .1f, 1.1f * scale, .28f, Color.white, 1.1f);
        Pulse(p, normal, .25f, 2f * scale, .5f, colour, 1.5f);
        Pulse(p + normal * .06f, normal, .15f, 1.3f * scale, .38f, colour);
        Emit(p, Mathf.RoundToInt(18 * scale), 4.2f, normal * .25f, .35f, colour);       // shards
        Emit(p, Mathf.RoundToInt(14 * scale), 1.4f, normal * .35f, .85f, Dust, 2.6f, false); // dust
    }

    public void JumpBurst(bool second)
    {
        if (!enabled || !material) return;
        var bottom = Feet;
        SfxBank.Play("move.jump", bottom, second ? 0.8f : 0.5f);
        peakFall = 0f;
        if (second)
        {
            // Stronger, mostly-purple burst in mid-air — a flat deck ring to
            // kick off plus a downward spray.
            Pulse(bottom + Vector3.up*.045f, Vector3.up, .25f, .9f, .5f, colour, 1.3f);
            Pulse(bottom + Vector3.up*.11f, Vector3.up, .15f, .65f, .35f, colour);
            Pulse(bottom + Vector3.up*.02f, Vector3.up, .34f, 1.35f, .45f, colour, 1.2f);
            Emit(bottom + Vector3.up*.09f, 24, 2.8f, Vector3.zero, .45f, colour);
            Emit(bottom, 16, 3f, Vector3.down * 1.6f, .5f, colour);
            // Afterimages trail the rise; started in LateUpdate so the bake
            // reads this frame's animated pose.
            doubleJumpChainPending = doubleJumpAfterimages > 0;
            DoubleJumpBursts++;
            return;
        }
        // Takeoff: small compressed puff/ring directly under each foot.
        for (int i = 2; i < 4; i++)
        {
            var foot = limbs[i] != null ? limbs[i].position : bottom;
            var at = new Vector3(foot.x, bottom.y + .03f, foot.z);
            Pulse(at, Vector3.up, .05f, .26f, .22f, PaleViolet, .8f);
            Emit(at, 4, .6f, Vector3.up * .2f, .25f, PaleViolet, .9f);
            Emit(at, 5, 1.6f, Vector3.up * .3f, .3f, colour, .7f);
        }
        JumpBursts++;
    }

    /// Bake the current pose into a pooled afterimage, shifted by <paramref name="offset"/>
    /// and pre-aged by <paramref name="age"/> (the gallery lays out a fake chain).
    void SpawnGhosts(Vector3 offset, float age = 0f)
    {
        if (animator == null) return;
        if (skins == null) skins = animator.GetComponentsInChildren<SkinnedMeshRenderer>();
        foreach (var smr in skins)
        {
            if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy || smr.sharedMesh == null) continue;
            Ghost g = null;
            foreach (var x in ghosts) if (x.age >= GhostLife) { g = x; break; }
            if (g == null)
            {
                g = new Ghost { go = new GameObject("Dodge afterimage"), mesh = new Mesh { name = "Afterimage" } };
                g.mesh.MarkDynamic();
                g.go.AddComponent<MeshFilter>().sharedMesh = g.mesh;
                g.renderer = g.go.AddComponent<MeshRenderer>();
                g.renderer.sharedMaterial = ghostMaterial ? ghostMaterial : material;
                g.renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; g.renderer.receiveShadows = false;
                ghosts.Add(g);
            }
            // The bake keeps COLOR — the player's wire metadata, which the
            // Afterimage shader draws. Only the flat fallback needs white colours.
            smr.BakeMesh(g.mesh, true);
            if (!ghostMaterial)
            {
                if (whites.Length != g.mesh.vertexCount)
                {
                    whites = new Color32[g.mesh.vertexCount];
                    for (int i = 0; i < whites.Length; i++) whites[i] = new Color32(255, 255, 255, 255);
                }
                g.mesh.colors32 = whites;
            }
            if (!bakeCalibrated) CalibrateBake(smr, g.mesh);
            g.mesh.RecalculateBounds();
            var tr = smr.transform;
            g.go.transform.SetPositionAndRotation(tr.position + tr.rotation * bakeOffset + offset, tr.rotation);
            g.go.transform.localScale = Vector3.one * bakeScale;
            LiftAboveFloor(g);
            g.go.SetActive(true);
            g.age = age;
            TickGhost(g);
        }
    }

    /// Measure where BakeMesh actually puts the pose: skin the highest and lowest
    /// bind-pose vertices by hand (world space) and solve the uniform scale +
    /// offset that maps the baked copies onto them. The visual is scaled
    /// (×1.6 on the protagonist) and BakeMesh's useScale/rootBone frame has
    /// shifted between Unity versions — a wrong guess sinks or floats every
    /// afterimage. Runs once, on the first bake.
    void CalibrateBake(SkinnedMeshRenderer smr, Mesh baked)
    {
        bakeCalibrated = true;
        var src = smr.sharedMesh; var bones = smr.bones;
        if (src == null || !src.isReadable || bones == null || bones.Length == 0) return;
        var srcVerts = src.vertices; var bakedVerts = baked.vertices;
        if (srcVerts.Length == 0 || srcVerts.Length != bakedVerts.Length) return;
        var bind = src.bindposes;
        var counts = src.GetBonesPerVertex(); var weights = src.GetAllBoneWeights();
        if (counts.Length != srcVerts.Length) return;
        int hi = 0, lo = 0;
        for (int i = 1; i < srcVerts.Length; i++)
        {
            if (srcVerts[i].y > srcVerts[hi].y) hi = i;
            if (srcVerts[i].y < srcVerts[lo].y) lo = i;
        }
        Vector3 Skin(int v)
        {
            int start = 0; for (int i = 0; i < v; i++) start += counts[i];
            var sum = Vector3.zero; float total = 0f;
            for (int k = 0; k < counts[v]; k++)
            {
                var w = weights[start + k];
                if (w.boneIndex >= bones.Length || w.boneIndex >= bind.Length || bones[w.boneIndex] == null) continue;
                sum += w.weight * (bones[w.boneIndex].localToWorldMatrix * bind[w.boneIndex]).MultiplyPoint3x4(srcVerts[v]);
                total += w.weight;
            }
            return total > 1e-4f ? sum / total : sum;
        }
        Vector3 w1 = Skin(hi), w2 = Skin(lo), b1 = bakedVerts[hi], b2 = bakedVerts[lo];
        var span = (b1 - b2).magnitude;
        if (span < 1e-4f) return;
        var r = smr.transform.rotation; var p = smr.transform.position;
        var s = (w1 - w2).magnitude / span;
        var off = Quaternion.Inverse(r) * (w1 - (p + r * (b1 * s)));
        var residual = (p + r * (b2 * s + off) - w2).magnitude;
        if (residual > .05f)
        {
            Debug.LogWarning($"[FX] Afterimage bake frame not resolvable (residual {residual:0.000} m); using the raw bake.");
            return;
        }
        bakeScale = Mathf.Abs(s - 1f) < .01f ? 1f : s;
        bakeOffset = off.magnitude < .005f ? Vector3.zero : off;
        Debug.Log($"[FX] Afterimage bake calibrated: scale x{bakeScale:0.###}, offset {bakeOffset.ToString("F3")} " +
                  $"(renderer lossy scale {smr.transform.lossyScale.x:0.###}).");
    }

    /// A pose that dips through the floor (the dart crouch while FootGrounding is
    /// released) would leave a half-buried afterimage — stand it ON the ground.
    void LiftAboveFloor(Ghost g)
    {
        var tr = g.go.transform; var b = g.mesh.bounds;
        float min = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            min = Mathf.Min(min, tr.TransformPoint(corner).y);
        }
        var origin = new Vector3(tr.position.x, Mathf.Max(min, capsule != null ? Feet.y : min) + .6f, tr.position.z);
        int n = Physics.RaycastNonAlloc(origin, Vector3.down, floorHits, 2.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float floor = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            var c = floorHits[i].collider;
            if (c == null || c.transform.IsChildOf(transform) || c.GetComponentInParent<Health>() != null) continue;
            floor = Mathf.Max(floor, floorHits[i].point.y);
        }
        var lift = floor + .005f - min;
        if (lift > .02f) tr.position += Vector3.up * Mathf.Min(lift, .6f);
    }

    /// Begin an afterimage chain: one ghost now, the rest every `spacing` metres.
    void StartChain(int count, float spacing, float maxAge, bool isDodge)
    {
        if (count <= 0) return;
        ghostsLeft = count; ghostSpacing = spacing; chainMaxAge = maxAge; chainIsDodge = isDodge;
        chainAge = ghostClock = 0f;
        SpawnGhosts(Vector3.zero); ghostsLeft--;
        lastGhostAt = transform.position;
    }

    /// Drop the chain's next ghost once the body has moved `ghostSpacing` (3D, so a
    /// vertical rise counts); slow moves fall back to a 0.1s cadence.
    void TickChain(float dt, bool keep)
    {
        if (ghostsLeft <= 0) return;
        chainAge += dt; ghostClock += dt;
        if (!keep || chainAge > chainMaxAge) { ghostsLeft = 0; return; }
        var d = Vector3.Distance(transform.position, lastGhostAt);
        if (d >= ghostSpacing || ghostClock >= .1f && d >= ghostSpacing * .4f)
        {
            SpawnGhosts(Vector3.zero); ghostsLeft--;
            lastGhostAt = transform.position; ghostClock = 0f;
        }
    }

    /// Hold, then fade in three steps while horizontal slices drop out; the rim
    /// cools from the pale highlight (newest, at the body) to main violet and
    /// the fill to shadow violet (oldest, at the tail of the dash).
    void TickGhost(Ghost g)
    {
        var t = Mathf.Clamp01(g.age / GhostLife);
        var hold = t < .3f ? 1f : 1f - (t - .3f) / .7f;
        var fade = hold <= .01f ? 0f : Mathf.Ceil(hold * 3f) / 3f;
        block.Clear();
        if (ghostMaterial)
        {
            var rim = Color.Lerp(PaleViolet, colour, Mathf.Clamp01(t * 1.6f)) * Mathf.Lerp(1f, intensity, .5f); rim.a = 1f;
            var fill = Color.Lerp(colour, DarkViolet, t); fill.a = .16f;
            var ink = Ink; ink.a = inkStrength;
            block.SetColor(TintId, fill); block.SetColor(RimId, rim); block.SetColor(InkId, ink);
            block.SetFloat(FadeId, fade * opacity); block.SetFloat(DissolveId, Mathf.Clamp01((t - .4f) / .6f) * .92f);
        }
        else
        {
            var tint = Color.Lerp(Ink, colour, .55f); tint.a = fade * .45f * opacity;
            block.SetColor(TintId, tint);
        }
        g.renderer.SetPropertyBlock(block);
    }

    /// Effect gallery: a fake dodge chain beside the player (current pose).
    public void PreviewAfterimages() => PreviewChain(-transform.right * afterimageSpacing, Vector3.zero, afterimageCount);

    /// Effect gallery: a fake double-jump chain rising beside the player.
    public void PreviewJumpAfterimages() =>
        PreviewChain(Vector3.up * doubleJumpSpacing, transform.right * 1.2f, doubleJumpAfterimages);

    void PreviewChain(Vector3 step, Vector3 origin, int count)
    {
        if (!enabled || !material) return;
        for (int i = count; i >= 1; i--) SpawnGhosts(origin + step * i, (i - 1) * .06f);
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        bool alive = state == null || !state.IsDead;
        bool attached = alive && wall && wall.AttachedToWall;
        bool grounded = capsule == null || capsule.isGrounded;
        if (!grounded && capsule != null) peakFall = Mathf.Min(peakFall, capsule.velocity.y);
        // Wall-run: stamp a Core sigil where the wall-side foot / hand plants,
        // alternating, on a stride cadence. The stamps stay on the wall and
        // click out in steps, so the run leaves a readable trail.
        if (attached && wall)
        {
            var normal = wall.WallSurfaceNormal.normalized;
            if (!wasAttached)
            {
                stampClock = 0f; stampSide = 0;
                var foot = limbs[(wall.WallOnRight ? 1 : 0) + 2];
                var at = foot != null ? foot.position : transform.position;
                at -= normal * Vector3.Dot(at - wall.WallSurfacePoint, normal);
                StampWall(at, normal, footRadius * 1.5f); // the attach stamp is the big one
                Pulse(at + normal * .03f, normal, footRadius, footRadius * 1.6f, .32f, colour, .8f);
            }
            if ((stampClock -= dt) <= 0f)
            {
                stampClock = .17f;
                var limb = limbs[(wall.WallOnRight ? 1 : 0) + (stampSide == 0 ? 2 : 0)];
                if (limb != null)
                {
                    var at = limb.position - normal * Vector3.Dot(limb.position - wall.WallSurfacePoint, normal);
                    StampWall(at, normal, stampSide == 0 ? footRadius : handRadius);
                }
                stampSide ^= 1;
            }
        }
        wasAttached = attached;
        VisibleWallRings = 0;
        foreach (var s in stamps)
        {
            if (s.age >= s.life) { if (s.hex.enabled) s.hex.enabled = s.hexInk.enabled = s.dia.enabled = s.diaInk.enabled = false; continue; }
            s.age += dt;
            var t = Mathf.Clamp01(s.age / s.life);
            // Pop in over 0.06s, hold, then click down in four steps.
            var grow = Mathf.Clamp01(s.age / .06f);
            var a = t < .35f ? 1f : 1f - (t - .35f) / .65f;
            var r = s.radius * (.7f + .3f * grow) * (1f - .1f * t);
            Draw(s.hex, s.hexInk, s.centre, s.normal, r, ringWidth * 1.1f, Glow(colour, a), RingSides, s.spin);
            Draw(s.dia, s.diaInk, s.centre, s.normal, r * .42f, ringWidth * .9f, Glow(t < .2f ? Color.white : PaleViolet, a), CoreSides, s.spin + Mathf.PI / CoreSides);
            if (a > 0f) VisibleWallRings++;
        }
        bool boost = alive && locomotion && locomotion.HasTraversalBoost && locomotion.ActualPlanarSpeed > locomotion.RunSpeed*.85f;
        var feet = Feet;
        if (boost && !boosted) { Pulse(feet+Vector3.up*.05f, Vector3.up,.2f,.85f,.35f, colour); Emit(feet,18,2.5f, Vector3.zero, .4f, colour); BoostBursts++; }
        boosted = boost;

        // Sprint start: one very short dark-violet streak/puff behind each boot.
        bool sprint = alive && grounded && locomotion && locomotion.Sprinting && locomotion.ActualPlanarSpeed > locomotion.RunSpeed * .7f;
        if (sprint && !sprinting)
            for (int i = 2; i < 4; i++)
                if (limbs[i] != null) Emit(limbs[i].position, 5, 2.4f, -transform.forward, .16f, DarkViolet, .8f);
        sprinting = sprint;

        // Dodge = an afterimage chain: a baked pose every `afterimageSpacing`
        // metres along the path (first one at the push-off), each holding then
        // slicing away. A small ring + chips mark the push-off; no hip streak.
        // Double jump = a shorter chain trailing the rise (started by JumpBurst).
        bool dodgeNow = alive && dodge != null && dodge.IsDodging;
        if (dodgeNow && !dodging)
        {
            StartChain(afterimageCount, afterimageSpacing, 1f, true);
            var dir = dodge.DodgeDir; dir.y = 0f;
            if (dir.sqrMagnitude < .01f) dir = transform.forward;
            var p = feet + Vector3.up * .04f;
            Pulse(p, Vector3.up, .16f, .75f, .28f, colour, 1f);
            Emit(p, 6, 2.2f, -dir.normalized * .8f + Vector3.up * .2f, .3f, colour, .9f, false);
            Emit(p, 3, 1.5f, -dir.normalized * .6f, .3f, DarkViolet, 1f, false);
        }
        else if (doubleJumpChainPending && alive)
            StartChain(doubleJumpAfterimages, doubleJumpSpacing, .4f, false);
        doubleJumpChainPending = false;
        TickChain(dt, alive && (chainIsDodge ? dodgeNow : !grounded && !attached && !dodgeNow));
        if (!dodgeNow && dodging)
        {
            var p = feet + Vector3.up * .04f;
            Pulse(p, Vector3.up, .12f, .45f, .22f, colour, .8f); // brake skid
        }
        dodging = dodgeNow;
        foreach (var g in ghosts)
        {
            if (g.age >= GhostLife) continue;
            g.age += dt;
            if (g.age >= GhostLife || !alive) { g.go.SetActive(false); g.age = GhostLife; continue; }
            TickGhost(g);
        }

        foreach (var r in pool)
        {
            if (r.life <= 0f) continue;
            r.age += dt; float t = Mathf.Clamp01(r.age/r.life);
            if (t >= 1f || !alive) { r.line.enabled = false; if (r.ink) r.ink.enabled = false; r.life = 0f; continue; }
            // Ease-out expansion so the facets snap open then settle.
            var e = 1f - (1f - t) * (1f - t);
            Draw(r.line, r.ink, r.centre, r.normal, r.radius + r.expansion * e, ringWidth * 1.3f * (1f - t * .5f) * r.width,
                Glow(r.col, (1f-t)*burstStrength*r.col.a), r.sides, r.spin);
        }
        TickForges(dt);
        TickSpikes(Time.unscaledDeltaTime); // real time: the burst plays through the hit freeze
        if (!alive && sparks) sparks.Clear();
    }

    // ---------- effect gallery ----------

    /// Play Mode tuning aid (Tools > Project Restart > FX > Play Effect Gallery):
    /// fires every traversal/combat cue in front of the player, each with a toast.
    public static void PlayGallery()
    {
        if (!Application.isPlaying) return;
        var fx = FindFirstObjectByType<TraversalEffects>();
        if (fx == null || !fx.isActiveAndEnabled) { Debug.LogWarning("[FX] No enabled TraversalEffects in the scene."); return; }
        if (fx.gallery != null) fx.StopCoroutine(fx.gallery);
        fx.gallery = fx.StartCoroutine(fx.Gallery());
    }

    System.Collections.IEnumerator Gallery()
    {
        var socket = GetComponent<WeaponSocket>();
        var t = transform;
        Vector3 Ahead(float d, float h = 0f) => t.position + t.forward * d + Vector3.up * h;
        Color? impact = socket != null && socket.Set != null ? socket.Set.impactTint : (Color?)null;
        var steps = new (string label, System.Action play)[]
        {
            ("JUMP", () => JumpBurst(false)),
            ("DOUBLE JUMP", () => { JumpBurst(true); doubleJumpChainPending = false; }),
            ("DOUBLE JUMP AFTERIMAGES", PreviewJumpAfterimages),
            ("LANDING — SMALL", () => PreviewLand(-4f)),
            ("LANDING — NORMAL", () => PreviewLand(-8f)),
            ("LANDING — HARD", () => PreviewLand(-14f)),
            ("DODGE AFTERIMAGES", PreviewAfterimages),
            ("WALL-JUMP KICK", () => KickBurst(Ahead(1.2f, 1f), (t.forward + Vector3.up * .4f).normalized)),
            ("HIT SPARKS", () => { HitFx.Spawn(Ahead(1.4f, 1.2f), t.right, 1.5f, impact); HitFlash(Ahead(1.4f, 1.2f), impact ?? colour); }),
            ("SWORD DRAW", () => { if (socket != null && socket.SpawnedWeapon != null) WeaponForge(socket.SpawnedWeapon.transform, socket.Set, true); }),
            ("SWORD SHEATHE", () => { if (socket != null && socket.SpawnedWeapon != null) WeaponForge(socket.SpawnedWeapon.transform, socket.Set, false); }),
            ("BOSS SHOCKWAVE", () => ShockRing.Spawn(Ahead(4f), 8f, 8f, 0f, new Color(1f, .12f, .1f))),
        };
        foreach (var (label, play) in steps)
        {
            GameHud.Toast("FX: " + label);
            play();
            var w = 0f; while (w < 1.8f) { w += Time.unscaledDeltaTime; yield return null; }
        }
        GameHud.Toast("FX GALLERY DONE");
        gallery = null;
    }

    void OnEnable() { if (sparks) sparks.Play(); }
    void OnDisable()
    {
        foreach (var r in pool) { if (r.line) r.line.enabled = false; if (r.ink) r.ink.enabled = false; r.life = 0f; }
        foreach (var s in stamps) { s.age = s.life = 9f; if (s.hex) s.hex.enabled = s.hexInk.enabled = s.dia.enabled = s.diaInk.enabled = false; }
        foreach (var f in forges) { f.age = f.life = 9f; if (f.line) f.line.enabled = f.ink.enabled = false; }
        foreach (var s in spikes) { s.age = s.life = 9f; if (s.line) s.line.enabled = s.ink.enabled = false; }
        foreach (var g in ghosts) { if (g.go) g.go.SetActive(false); g.age = GhostLife; }
        if (sparks) sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); boosted = false;
        gallery = null;
    }
    void OnDestroy()
    {
        foreach (var g in ghosts) { if (g.go) Destroy(g.go); if (g.mesh) Destroy(g.mesh); }
        if (material) Destroy(material);
        if (ghostMaterial) Destroy(ghostMaterial);
    }
}
