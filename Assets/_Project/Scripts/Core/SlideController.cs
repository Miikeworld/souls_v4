using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

/// <summary>
/// Ground-lunge slide state. Plays the Dash/slide clip and moves the player over a
/// distance that scales with entry speed, while keeping the visual root counter-offset
/// so the body stays with the player. Fires ICameraDisplacementEvent on entry.
/// Camera tilt is direction-aware: forward/back gives zero Dutch, right/left gives signed roll.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerState))]
[RequireComponent(typeof(CameraTiltController))]
[RequireComponent(typeof(PlayerLocomotion))]
[DefaultExecutionOrder(10)]
public sealed class SlideController : MonoBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;
    [Tooltip("If true, releasing the slide button while sliding ends the slide immediately.")]
    [SerializeField] private bool stopSlideOnRelease = false;
    [Tooltip("Minimum slide age before a release can end the slide, so a quick tap still performs the full lunge.")]
    [SerializeField, Min(0f)] private float stopSlideMinAge = 0.1f;

    [Header("Traversal animation")]
    [Tooltip("Hash for the Animator state that plays the slide. Named Dash in the single-layer controller.")]
    [SerializeField] private string slideStateName = "Base Layer.Dash";
    [Tooltip("Animator trigger name used to enter the slide state.")]
    [SerializeField] private string slideTriggerName = "Dash";
    [Tooltip("Hash to blend back to when the slide is cancelled or finished.")]
    [SerializeField] private string locomotionStateName = "Base Layer.Locomotion";

    [Header("Slide distance")]
    [Tooltip("Slide distance when triggered from standstill or very slow movement.")]
    [SerializeField, Min(0f)] private float standingSlideDistance = 2f;
    [Tooltip("Slide distance when triggered at full walk speed.")]
    [SerializeField, Min(0f)] private float walkingSlideDistance = 5f;
    [Tooltip("Slide distance when triggered at full run speed.")]
    [SerializeField, Min(0f)] private float runningSlideDistance = 7f;
    [Tooltip("Scales all three slide distances above (new field so the longer default reaches existing scenes).")]
    [SerializeField, Min(0.1f)] private float slideDistanceScale = 1.6f;
    [Tooltip("Multiplies the player's entry speed when sliding while walking. 1 = no boost.")]
    [SerializeField, Min(0f)] private float walkingSlideSpeedBoost = 1.25f;
    [Tooltip("Cap on the walking-slide boosted speed. 0 = use walkSpeed.")]
    [SerializeField, Min(0f)] private float maxWalkingSlideBoostSpeed = 0f;
    [Tooltip("Multiplies the player's entry speed when sliding while running. 1 = no boost.")]
    [SerializeField, Min(0f)] private float slideSpeedBoost = 1.15f;
    [Tooltip("Cap on the running-slide boosted speed. 0 = use runSpeed.")]
    [SerializeField, Min(0f)] private float maxSlideBoostSpeed = 12f;
    [Tooltip("Hard cap on how far the player can move per frame during the slide.")]
    [SerializeField, Min(0.1f)] private float maxSlideSpeed = 50f;
    [Tooltip("Scales the slide animation playback speed with entry momentum (1 = no scaling).")]
    [SerializeField, Min(0.1f)] private float minSlideAnimSpeed = 1.1f;
    [Tooltip("Slide animation playback speed at full walk speed.")]
    [SerializeField, Min(0.1f)] private float walkingSlideAnimSpeed = 1.1f;
    [SerializeField, Min(0.1f)] private float maxSlideAnimSpeed = 1.35f;
    [SerializeField, Min(0.1f)] private float maxBoostSlideAnimSpeed = 1.6f;
    [Tooltip("Cross-fade duration when the slide is interrupted or finishes.")]
    [SerializeField, Min(0f)] private float recoveryFadeDuration = 0.15f;
    [Tooltip("Minimum extra time after the slide state ends before control returns.")]
    [SerializeField, Min(0f)] private float recoveryDuration = 0.15f;
    [Tooltip("How closely the slide must be aligned with the camera forward to trigger a forward dip. 1 = exactly forward."), Range(0f, 1f)]
    [SerializeField] private float frontSlidePitchThreshold = 0.7f;
    [Tooltip("Camera pitch dip in degrees for a forward slide (negative = dip down).")]
    [SerializeField, Range(-30f, 30f)] private float frontSlidePitch = -8f;
    [Tooltip("Camera pitch back in degrees for a backward slide (positive = lean back / look up).")]
    [SerializeField, Range(-30f, 30f)] private float backSlidePitch = 5f;

    [Header("Camera tilt profile for this state")]
    [SerializeField] private float tiltAmount = 12f;
    [SerializeField] private float tiltInDuration = 0.25f;
    [SerializeField] private float tiltOutDuration = 0.4f;

    [Header("Cancel window (driven by Animation Events on the slide clip)")]
    [SerializeField] private UnityEvent onCancelWindowOpen;
    [SerializeField] private UnityEvent onCancelWindowClose;

    [Header("Events")]
    [SerializeField] private UnityEvent onSlideStarted;
    [SerializeField] private UnityEvent onSlideEnded;

    private static readonly float VerticalTerminal = -50f;

    private int slideStateId;
    private int slideTriggerId;
    private int locomotionStateId;
    private int jumpStartStateId;
    private int jumpForwardStartStateId;
    private int jumpLandStateId;
    private int jumpForwardLandStateId;
    private int crouchStateId;
    private int crouchLoopStateId;
    private int crouchEndStateId;
    private int crouchTriggerId;
    private int crouchingParamId;
    private static readonly int InAirId = Animator.StringToHash("InAir");

    [Header("Cancel windows")]
    [Tooltip("Earliest time in a slide that a jump cancel is allowed."), Min(0f)]
    [SerializeField] private float slideToJumpCancelStart = 0.05f;
    [Tooltip("Latest time in a slide that a jump cancel is allowed."), Min(0f)]
    [SerializeField] private float slideToJumpCancelEnd = 2f;
    [Tooltip("Minimum jump age before a jump can be cancelled into a slide."), Min(0f)]
    [SerializeField] private float jumpToSlideCancelDelay = 0.15f;
    [Tooltip("Earliest slide age that an attack press cancels into the dash lunge."), Min(0f)]
    [SerializeField] private float slideToAttackCancelStart = 0.08f;
    [Tooltip("Latest slide age that an attack press cancels into the dash lunge."), Min(0f)]
    [SerializeField] private float slideToAttackCancelEnd = 0.7f;

    private CharacterController character;
    private Animator animator;
    private PlayerState state;
    private CameraTiltController tilt;
    private PlayerLocomotion locomotion;
    private DodgeController dodge;
    private WeaponSocket weaponSocket;
    private InputActionAsset ownedActions;
    private InputAction slideAction;
    private InputAction moveAction;

    private bool ready;
    private bool sliding;
    private bool recovering;
    private bool crouching;
    private bool sawSlideState;
    private bool cancelJumpVisualRecover;
    private float slideAge;
    private float recoveryAge;
    private float cancelJumpVisualRecoverTime;
    private float verticalSpeed;
    private bool cancelWindowOpen;

    private const float CancelJumpVisualRecoverDuration = 0.1f;

    private const float FootLiftSmooth = 15f;
    private const float FootLiftMargin = 0.01f;
    private float currentFootLift;

    private Vector3 visualOffset;
    private Vector3 initialHipsPose;
    private Vector3 initialPlayerPos;
    private Vector3 slideDirection;
    private float distanceLimit;
    private float lastSlideDistance;
    private float currentSlideSpeed;
    private float slideStartSpeed;
    private float slideExitSpeed;
    private float slideTravelDuration;
    private Transform hips;
    private Transform visual;

    public bool IsSliding => sliding;
    public bool IsCrouching => crouching;
    public bool InCancelWindow => cancelWindowOpen;
    /// <summary>Time-driven window for the slide→attack lunge (the clip carries
    /// no authored anim events, so the flag stacks with the age window).</summary>
    public bool CanCancelIntoAttack =>
        cancelWindowOpen || (sliding && slideAge >= slideToAttackCancelStart && slideAge <= slideToAttackCancelEnd);
    public float SlideAge => slideAge;

    public bool CanCancelIntoJump()
    {
        return sliding && slideAge >= slideToJumpCancelStart && slideAge <= slideToJumpCancelEnd;
    }

    public bool TryCancelIntoJump()
    {
        if (!CanCancelIntoJump()) return false;
        sliding = false;
        recovering = false;
        sawSlideState = false;
        slideAge = 0f;
        cancelWindowOpen = false;
        cancelJumpVisualRecover = true;
        cancelJumpVisualRecoverTime = CancelJumpVisualRecoverDuration;
        animator.speed = 1f;
        animator.ResetTrigger(slideTriggerId);
        state.IsDisplacing = false;

        // Cross-fade directly to the appropriate jump state so the slide animation is cancelled
        // immediately and the transition looks smooth rather than waiting on a trigger.
        var move = moveAction != null ? Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f) : Vector2.zero;
        var jumpHash = move.magnitude > 0.1f && jumpForwardStartStateId != 0 ? jumpForwardStartStateId : jumpStartStateId;
        if (jumpHash != 0)
            animator.CrossFadeInFixedTime(jumpHash, recoveryFadeDuration, 0);

        if (locomotion != null)
        {
            locomotion.SetImmediateBlendForCurrentInput();
            locomotion.TriggerSlideRunBoost();
            locomotion.PreserveAirMomentum(currentSlideSpeed);
        }
        tilt.EndTilt();
        Debug.Log("[Slide] cancel into jump", this);
        return true;
    }

    /// <summary>
    /// Returns a distance between standing and running values based on the player's
    /// current planar entry speed and configured run speed.
    /// </summary>
    public static float MomentumDistance(float entrySpeed, float runSpeed, float standingDistance, float runningDistance)
    {
        if (runSpeed <= 0.001f) return standingDistance;
        var t = Mathf.Clamp01(entrySpeed / runSpeed);
        return Mathf.Lerp(Mathf.Max(0f, standingDistance), Mathf.Max(standingDistance, runningDistance), t);
    }

    /// <summary>
    /// Returns a value between standing, walking and running values based on the player's
    /// current planar entry speed and configured walk/run speeds.
    /// </summary>
    public static float MomentumValue(float entrySpeed, float walkSpeed, float runSpeed, float standingValue, float walkingValue, float runningValue)
    {
        if (runSpeed <= 0.001f) return walkingValue;
        if (entrySpeed <= 0f) return standingValue;
        if (walkSpeed <= 0.001f) return Mathf.Lerp(standingValue, runningValue, Mathf.Clamp01(entrySpeed / runSpeed));
        if (entrySpeed <= walkSpeed)
            return Mathf.Lerp(standingValue, walkingValue, Mathf.Clamp01(entrySpeed / walkSpeed));

        var t = Mathf.Clamp01((entrySpeed - walkSpeed) / (runSpeed - walkSpeed));
        return Mathf.Lerp(walkingValue, runningValue, t);
    }

    /// <summary>
    /// Computes Dutch/roll sign from slide direction relative to camera forward.
    /// Straight forward/back gives zero; right gives positive (roll right); left negative.
    /// </summary>
    public static float DirectionalTilt(Vector3 direction, Vector3 cameraForward, float amount)
    {
        var forward = Vector3.ProjectOnPlane(cameraForward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        var right = Vector3.Cross(Vector3.up, forward);
        return Vector3.Dot(direction.normalized, right) * Mathf.Abs(amount);
    }

    private void Awake()
    {
        character = GetComponent<CharacterController>();
        animator = FindValidAnimator();
        state = GetComponent<PlayerState>();
        tilt = GetComponent<CameraTiltController>();
        locomotion = GetComponent<PlayerLocomotion>();
        dodge = GetComponent<DodgeController>();
        weaponSocket = GetComponent<WeaponSocket>();
        if (animator == null || state == null || tilt == null || character == null || locomotion == null)
        {
            Debug.LogError("SlideController needs a valid humanoid Animator child, PlayerState, CameraTiltController, PlayerLocomotion and CharacterController.", this);
            enabled = false;
            return;
        }

        visual = animator.transform;
        // The protagonist's skinned mesh measures its bounds from an un-animated
        // "Root" bone, while root-in-pose clips (slide, the old crouch) carry the
        // hips metres forward and this controller counter-offsets the visual. The
        // bounds then trail the body by the lunge distance and the renderer gets
        // frustum-culled — the slide flash / vanishing crouch. Per-frame bounds
        // follow the real pose.
        foreach (var skin in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            skin.updateWhenOffscreen = true;
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips == null)
        {
            Debug.LogError("SlideController could not find Hips on the humanoid rig.", this);
            enabled = false;
            return;
        }

        slideStateId = Animator.StringToHash(slideStateName);
        slideTriggerId = Animator.StringToHash(slideTriggerName);
        locomotionStateId = Animator.StringToHash(locomotionStateName);
        jumpStartStateId = Animator.StringToHash("Base Layer.JumpStart");
        jumpForwardStartStateId = Animator.StringToHash("Base Layer.JumpForwardStart");
        jumpLandStateId = Animator.StringToHash("Base Layer.JumpLand");
        jumpForwardLandStateId = Animator.StringToHash("Base Layer.JumpForwardLand");
        crouchStateId = Animator.StringToHash("Base Layer.CrouchStart");
        crouchLoopStateId = Animator.StringToHash("Base Layer.CrouchLoop");
        crouchEndStateId = Animator.StringToHash("Base Layer.CrouchEnd");
        crouchTriggerId = Animator.StringToHash("Crouch");
        crouchingParamId = Animator.StringToHash("Crouching");

        if (inputActions != null)
        {
            ownedActions = Instantiate(inputActions);
            ownedActions.Disable();
            slideAction = ownedActions.FindAction("Player/Dash");
            moveAction = ownedActions.FindAction("Player/Move");
        }

        if (slideAction == null)
        {
            slideAction = new InputAction("Slide", InputActionType.Button);
            slideAction.AddBinding("<Keyboard>/space");
            slideAction.AddBinding("<Gamepad>/buttonEast");
        }

        ready = true;
    }

    private void OnEnable()
    {
        if (!ready) return;
        slideAction?.Enable();
        moveAction?.Enable();
    }

    private void OnDisable()
    {
        slideAction?.Disable();
        moveAction?.Disable();
        cancelJumpVisualRecover = false;
        cancelJumpVisualRecoverTime = 0f;
        if (crouching && ready)
            EndCrouch();
        if ((sliding || recovering) && ready)
            EndSlide();
        if (recovering && ready)
            EndRecovery();
    }

    private void OnDestroy()
    {
        slideAction?.Dispose();
        if (ownedActions != null)
            Destroy(ownedActions);
    }

    public void SetSlideBinding(string keyboardPath, string gamepadPath = "<Gamepad>/buttonEast")
    {
        if (slideAction == null)
            return;
        slideAction.Disable();
        if (slideAction.bindings.Count > 0 && !string.IsNullOrEmpty(keyboardPath))
            slideAction.ApplyBindingOverride(0, keyboardPath);
        if (slideAction.bindings.Count > 1 && !string.IsNullOrEmpty(gamepadPath))
            slideAction.ApplyBindingOverride(1, gamepadPath);
        if (enabled) slideAction.Enable();
    }

    private void Start()
    {
        if (ready)
            visualOffset = visual.localPosition;
    }

    private void Update()
    {
        if (!ready || Time.deltaTime <= 0f) return;
        if (!character.enabled || !animator.isActiveAndEnabled || !locomotion.isActiveAndEnabled || state.IsDead || (state.IsRooted && !crouching))
        {
            if (crouching) EndCrouch();
            if (sliding) EndSlide();
            if (recovering) EndRecovery();
            return;
        }

        if (!character.isGrounded && !sliding && !recovering && !crouching)
            return;

        var dt = Time.deltaTime;
        var currentSlide = IsInSlideState();
        var nextSlide = animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).fullPathHash == slideStateId;

        if (crouching)
        {
            var move = moveAction != null ? Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f) : Vector2.zero;
            if (!character.isGrounded || move.magnitude > 0.1f)
            {
                EndCrouch();
                if (!character.isGrounded)
                    return;
            }
            else
            {
                // The crouch takes are in place (Player_Crouch: feet planted, hips
                // sit back) — hold the visual X/Z. Counter-offsetting the hips here
                // (needed for the old root-in-pose slide clips) would drag the
                // planted feet forward. Y belongs to FootGrounding.
                visual.localPosition = new Vector3(visualOffset.x, visual.localPosition.y, visualOffset.z);
            }
        }

        if (recovering)
        {
            recoveryAge += dt;
            if (recoveryAge >= Mathf.Max(recoveryFadeDuration, recoveryDuration) &&
                !SlidePoseContributing() && PlanarVisualError() < 0.025f)
                EndRecovery();
        }

        if (sliding)
        {
            slideAge += dt;
            sawSlideState |= currentSlide || nextSlide;
            if (!currentSlide && !nextSlide && !sawSlideState && slideAge > 0.5f)
            {
                Debug.LogWarning("Slide state was not observed; clearing the request so movement cannot remain locked.", this);
                EndSlide();
            }
        }

        var slidePressed = slideAction.WasPressedThisFrame();
        if (slidePressed && GetComponent<AttackController>() is { IsAttacking: true } activeAttack)
        {
            activeAttack.RequestDodge(moveAction != null ? moveAction.ReadValue<Vector2>() : Vector2.zero);
            return;
        }

        if (stopSlideOnRelease && sliding && slideAction != null && slideAction.WasReleasedThisFrame() && slideAge >= stopSlideMinAge)
        {
            EndSlide();
            return;
        }

        if (slidePressed && crouching)
        {
            EndCrouch();
            return;
        }

        if (slidePressed && !sliding && !recovering && !state.IsDisplacing && !state.IsDrinking && !currentSlide && !nextSlide && !animator.IsInTransition(0) && character.isGrounded)
        {
            var move = moveAction != null ? Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f) : Vector2.zero;
            // Combat owns the press (quickstep/roll/backstep); out of combat a
            // moving press is the traversal slide and an idle press crouches.
            var inCombat = weaponSocket != null && weaponSocket.InCombat;
            if (!inCombat && move.magnitude > 0.1f)
            {
                if (IsInJumpLandState() && locomotion.JumpAge < jumpToSlideCancelDelay)
                {
                    Debug.Log($"[Slide] jump cancel blocked: jumpAge={locomotion.JumpAge:F3}s < {jumpToSlideCancelDelay:F3}s", this);
                }
                else
                {
                    var velocity = locomotion.ActualPlanarVelocity;
                    slideDirection = velocity.sqrMagnitude > 0.01f ? velocity.normalized :
                        TryGetSlideDirection(out var direction) ? direction.normalized : transform.forward;
                    transform.rotation = Quaternion.LookRotation(slideDirection);
                    BeginSlide();
                }
            }
            else if (dodge != null && (inCombat || !IsInJumpLandState()))
            {
                // In combat: quickstep/roll/backstep. Out of combat a neutral
                // press is the backstep dodge (the crouch was removed by request).
                dodge.TryDodge(move);
            }
        }
        else if (slidePressed)
        {
            Debug.Log($"[Slide] input blocked: sliding={sliding} recovering={recovering} isDisplacing={state.IsDisplacing} currentSlide={currentSlide} nextSlide={nextSlide} inTransition={animator.IsInTransition(0)} grounded={character.isGrounded}", this);
        }
    }

    private Vector3 ApplyVisualCounterOffset()
    {
        // Reset visual to its base local position before reading the skeleton pose.
        visual.localPosition = visualOffset;

        // The slide clip has root XZ baked into the humanoid pose, so the visual root does not
        // move. Read the Hips world position in the visual root's local space and counter-offset
        // the visual root so the Hips stays at a fixed XZ world offset from the player. Y is
        // preserved from the clip so the body can crouch/lower during the slide (stays grounded).
        var currentPose = visual.InverseTransformPoint(hips.position);
        var hipsOffset = currentPose - initialHipsPose;
        var worldOffset = visual.TransformVector(hipsOffset);

        // Only cancel the planar lunge; keep the clip's vertical (crouch/bob).
        var rootLocalOffset = transform.InverseTransformVector(new Vector3(worldOffset.x, 0f, worldOffset.z));
        visual.localPosition = visualOffset - rootLocalOffset;
        return worldOffset;
    }

    private void LateUpdate()
    {
        if (!ready) return;

        // Don't fight landing-roll or wall-run visual offsets while those states are active.
        if (state != null && state.IsDisplacing && !sliding && !recovering && !crouching)
            return;

        var dt = Time.deltaTime;

        if (sliding)
        {
            ApplyVisualCounterOffset();
        }
        else if (recovering)
        {
            // The outgoing baked lunge still lives in the blended hips pose.
            // Removing its counter-offset early throws the mesh ahead of the capsule/camera.
            if (SlidePoseContributing()) ApplyVisualCounterOffset();
            else RestoreVisualPlanar(dt);
        }
        else if (cancelJumpVisualRecover)
        {
            var inSlide = IsInSlideState();
            var inSlideTransition = animator.IsInTransition(0) && (animator.GetNextAnimatorStateInfo(0).fullPathHash == slideStateId);
            if (inSlide || inSlideTransition)
            {
                // Keep the body aligned while the slide clip is still blended in.
                ApplyVisualCounterOffset();
            }
            else
            {
                cancelJumpVisualRecoverTime -= dt;
                if (cancelJumpVisualRecoverTime <= 0f)
                {
                    visual.localPosition = visualOffset;
                    cancelJumpVisualRecover = false;
                }
                else
                {
                    var t = Mathf.Clamp01(dt / cancelJumpVisualRecoverTime);
                    visual.localPosition = Vector3.Lerp(visual.localPosition, visualOffset, t);
                }
            }
        }

        if (sliding)
        {
            var currentSlide = IsInSlideState();
            var nextSlide = animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).fullPathHash == slideStateId;
            sawSlideState |= currentSlide || nextSlide;

            var normalizedAge = Mathf.Clamp01(slideAge / Mathf.Max(0.01f, slideTravelDuration));
            var averageSpeed = Mathf.Max(0.01f, (slideStartSpeed + slideExitSpeed) * 0.5f);
            var easedProgress = (slideStartSpeed * normalizedAge +
                                 0.5f * (slideExitSpeed - slideStartSpeed) * normalizedAge * normalizedAge) / averageSpeed;
            var targetDistance = distanceLimit * Mathf.Clamp01(easedProgress);
            currentSlideSpeed = dt > 0f ? Mathf.Max(0f, targetDistance - lastSlideDistance) / dt : slideExitSpeed;
            lastSlideDistance = targetDistance;

            var target = initialPlayerPos + slideDirection * targetDistance;
            var delta = target - transform.position;
            var planarMagnitude = new Vector3(delta.x, 0f, delta.z).magnitude;
            if (planarMagnitude > maxSlideSpeed * dt)
            {
                var planar = new Vector3(delta.x, 0f, delta.z);
                planar = planar.normalized * (maxSlideSpeed * dt);
                delta.x = planar.x;
                delta.z = planar.z;
            }

            if (character.isGrounded && verticalSpeed < 0f)
                verticalSpeed = -2f;
            else
                verticalSpeed = Mathf.Max(verticalSpeed + Physics.gravity.y * dt, VerticalTerminal);

            delta.y = verticalSpeed * dt;
            character.Move(delta);

            var remaining = Vector3.ProjectOnPlane(target - transform.position, Vector3.up).magnitude;
            if ((normalizedAge >= 1f && remaining <= Mathf.Max(0.05f, 0.05f * distanceLimit)) || slideAge > slideTravelDuration + 0.25f)
                EndSlide();
        }

        // Outside of crouch/slide, reset the visual root X/Z — but NEVER Y:
        // FootGrounding owns the height via its pelvis correction; a stale
        // visualOffset write here stomps it and floats the idle pose.
        if (!sliding && !recovering && !cancelJumpVisualRecover && !crouching)
            visual.localPosition = new Vector3(visualOffset.x, visual.localPosition.y, visualOffset.z);

        // Foot-lift only where this controller owns the visual (slide/crouch —
        // FootGrounding releases on IsDisplacing/IsRooted there). In normal
        // locomotion it would fight the sole-authority solve.
        if (sliding || crouching)
            PlantFeet(dt);
    }

    private void PlantFeet(float dt)
    {
        if (animator == null || character == null || visual == null || hips == null)
            return;

        var leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        var rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        var leftToes = animator.GetBoneTransform(HumanBodyBones.LeftToes);
        var rightToes = animator.GetBoneTransform(HumanBodyBones.RightToes);
        if (leftFoot == null || rightFoot == null)
            return;

        // Reset the visual to the base height so we can measure the foot height
        // without the previous frame's foot-lift affecting the reading.
        var basePos = visual.localPosition;
        basePos.y = visualOffset.y;
        visual.localPosition = basePos;

        var groundY = SampleGroundY();
        var lowestY = Mathf.Min(leftFoot.position.y, rightFoot.position.y);
        if (leftToes != null) lowestY = Mathf.Min(lowestY, leftToes.position.y);
        if (rightToes != null) lowestY = Mathf.Min(lowestY, rightToes.position.y);
        var targetLift = groundY + FootLiftMargin - lowestY;

        // While in the air we only lift (never pull the body down to the floor).
        if (animator.GetBool(InAirId))
            targetLift = Mathf.Max(0f, targetLift);

        currentFootLift = Mathf.Lerp(currentFootLift, targetLift, FootLiftSmooth * dt);

        // Apply the foot-lift as an absolute offset from the visual base.
        var pos = visual.localPosition;
        pos.y = visualOffset.y + currentFootLift;
        visual.localPosition = pos;
    }

    private float SampleGroundY()
    {
        if (character == null)
            return transform.position.y;

        // Cast a ray from the player center down to the first non-player surface.
        // This handles the common case where the CharacterController hovers a few cm
        // above the floor and the visual feet need to meet the actual ground.
        var origin = transform.position + character.center;
        var hits = Physics.RaycastAll(origin, Vector3.down, character.height * 0.5f + character.radius + 2f);

        float closest = float.MaxValue;
        float groundY = transform.position.y + character.center.y - character.height * 0.5f;

        foreach (var hit in hits)
        {
            if (hit.collider == null || hit.collider == character)
                continue;

            if (hit.distance < closest)
            {
                closest = hit.distance;
                groundY = hit.point.y;
            }
        }

        return groundY;
    }

    private void BeginSlide()
    {
        if (sliding || recovering) return;
        sliding = true;
        sawSlideState = false;
        slideAge = 0f;
        cancelWindowOpen = false;
        verticalSpeed = -2f;
        state.IsDisplacing = true;

        // Reset visual to base before capturing the reference pose; this guarantees both
        // currentPose and initialHipsPose are in the same visual-root local space.
        visual.localPosition = visualOffset;
        initialHipsPose = visual.InverseTransformPoint(hips.position);
        initialPlayerPos = transform.position;
        lastSlideDistance = 0f;

        var entrySpeed = locomotion.ActualPlanarSpeed;
        var walk = locomotion.WalkSpeed;
        var run = locomotion.RunSpeed;

        // Blend the speed boost and cap based on whether the player is walking or running.
        var t = run > walk ? Mathf.Clamp01((entrySpeed - walk) / (run - walk)) : 0f;
        var speedBoost = Mathf.Lerp(walkingSlideSpeedBoost, slideSpeedBoost, t);
        var walkCap = maxWalkingSlideBoostSpeed > 0f ? maxWalkingSlideBoostSpeed : walk;
        var runCap = maxSlideBoostSpeed > 0f ? maxSlideBoostSpeed : run;
        var speedCap = Mathf.Lerp(walkCap, runCap, t);
        var boostedSpeed = Mathf.Min(entrySpeed * speedBoost, speedCap);
        slideStartSpeed = Mathf.Min(boostedSpeed, maxSlideSpeed);
        slideExitSpeed = Mathf.Min(Mathf.Max(entrySpeed, boostedSpeed * 0.7f), maxSlideSpeed);
        currentSlideSpeed = slideStartSpeed;

        distanceLimit = MomentumValue(boostedSpeed, walk, run, standingSlideDistance, walkingSlideDistance, runningSlideDistance)
                        * slideDistanceScale;
        // Cap raised 0.75 → 1.1s so the longer slide travels at the same speed instead of rushing.
        slideTravelDuration = Mathf.Clamp(2f * distanceLimit / Mathf.Max(1f, slideStartSpeed + slideExitSpeed), 0.3f, 1.1f);

        // Base animation speed follows walk/run blend. If the player is already above run speed (e.g. slide boost),
        // ramp up toward maxBoostSlideAnimSpeed so the slide feels as fast as the current momentum.
        var baseT = run > walk ? Mathf.Clamp01((entrySpeed - walk) / (run - walk)) : 0f;
        var baseAnimSpeed = Mathf.Lerp(walkingSlideAnimSpeed, maxSlideAnimSpeed, baseT);
        var overdrive = maxSlideBoostSpeed > run ? Mathf.Clamp01((entrySpeed - run) / (maxSlideBoostSpeed - run)) : 0f;
        var slideAnimSpeed = Mathf.Lerp(baseAnimSpeed, maxBoostSlideAnimSpeed, overdrive);
        slideAnimSpeed = Mathf.Max(slideAnimSpeed, minSlideAnimSpeed);
        animator.speed = slideAnimSpeed;

        Debug.Log($"[Slide] begin dir={slideDirection:F2} limit={distanceLimit:F2}m entrySpeed={entrySpeed:F2} boosted={boostedSpeed:F2} cap={speedCap:F2} animSpeed={slideAnimSpeed:F2}", this);

        animator.ResetTrigger(slideTriggerId);
        animator.CrossFadeInFixedTime(slideStateId, recoveryFadeDuration, 0, 0f);

        var cameraForward = Camera.main != null ? Camera.main.transform.forward : transform.forward;
        var signedTilt = DirectionalTilt(slideDirection, cameraForward, tiltAmount);
        var planarCameraForward = Vector3.ProjectOnPlane(cameraForward, Vector3.up).normalized;
        if (planarCameraForward.sqrMagnitude < 0.001f) planarCameraForward = transform.forward;
        var forwardAlignment = Vector3.Dot(slideDirection, planarCameraForward);
        var pitchAmount = 0f;
        if (forwardAlignment >= frontSlidePitchThreshold)
            pitchAmount = frontSlidePitch;
        else if (forwardAlignment <= -frontSlidePitchThreshold)
            pitchAmount = backSlidePitch;

        var evt = new CameraDisplacementEvent(
            magnitude: 1f,
            tiltAmount: signedTilt,
            tiltInDuration: tiltInDuration,
            tiltOutDuration: tiltOutDuration,
            tiltCurve: null,
            pitchAmount: pitchAmount,
            pitchInDuration: 0.1f,
            pitchOutDuration: 0.2f);
        tilt.BeginTilt(evt);

        onSlideStarted?.Invoke();
    }

    private void EndSlide()
    {
        if (!sliding) return;
        sliding = false;
        sawSlideState = false;
        cancelWindowOpen = false;
        recovering = true;
        recoveryAge = 0f;
        animator.speed = 1f;
        animator.ResetTrigger(slideTriggerId);
        if (locomotion != null)
        {
            locomotion.SetImmediateBlendForCurrentInput();
            // Release the movement lock immediately and hand off the slide's exit speed so
            // the player can run during the visual cross-fade instead of being frozen.
            // The post-slide speed boost is only granted when the slide is cancelled into a jump.
            locomotion.SetLocomotionSpeed(Mathf.Max(currentSlideSpeed, slideExitSpeed));
        }
        if (IsInSlideState())
        {
            // Cross-fade back to locomotion for a smoother return. A dedicated recovery state will replace this in item 6.
            animator.CrossFadeInFixedTime(locomotionStateId, recoveryFadeDuration, 0);
        }

        // Let the player move immediately; visual recovery (counter-offset + cross-fade)
        // will finish in LateUpdate/EndRecovery without freezing input.
        state.IsDisplacing = false;

        tilt.EndTilt();
        onSlideEnded?.Invoke();
        Debug.Log($"[Slide] end after {slideAge:F3}s; authored lunge={lastSlideDistance:F2}m; player pos={transform.position:F2}", this);
    }

    private bool SlidePoseContributing() => IsInSlideState() ||
        (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).fullPathHash == slideStateId);

    private float PlanarVisualError() => Vector2.Distance(
        new Vector2(visual.localPosition.x, visual.localPosition.z), new Vector2(visualOffset.x, visualOffset.z));

    private void RestoreVisualPlanar(float dt)
    {
        var pos = visual.localPosition;
        var target = new Vector3(visualOffset.x, pos.y, visualOffset.z);
        visual.localPosition = Vector3.MoveTowards(pos, target, 12f * dt);
    }

    private void EndRecovery()
    {
        if (!recovering) return;
        recovering = false;
        if (visual != null)
            visual.localPosition = new Vector3(visualOffset.x, visual.localPosition.y, visualOffset.z);
    }

    private bool HasCrouchStates()
    {
        return animator != null &&
               animator.HasState(0, crouchStateId) &&
               animator.HasState(0, crouchLoopStateId) &&
               animator.HasState(0, crouchEndStateId);
    }

    private bool IsInCrouchState()
    {
        if (animator == null) return false;
        var info = animator.GetCurrentAnimatorStateInfo(0);
        var hash = info.fullPathHash;
        return hash == crouchStateId || hash == crouchLoopStateId || hash == crouchEndStateId;
    }

    private bool IsInJumpLandState()
    {
        if (animator == null) return false;
        var info = animator.GetCurrentAnimatorStateInfo(0);
        var hash = info.fullPathHash;
        return hash == jumpLandStateId || hash == jumpForwardLandStateId;
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

    private void BeginCrouch()
    {
        if (crouching || !HasCrouchStates()) return;
        crouching = true;
        state.IsRooted = true;
        visual.localPosition = new Vector3(visualOffset.x, visual.localPosition.y, visualOffset.z);
        if (locomotion != null)
            locomotion.SetImmediateBlendForCurrentInput();
        animator.SetBool(crouchingParamId, true);
        animator.SetTrigger(crouchTriggerId);
        Debug.Log("[Slide] begin crouch", this);
    }

    private void EndCrouch()
    {
        if (!crouching) return;
        crouching = false;
        state.IsRooted = false;
        if (visual != null)
            visual.localPosition = new Vector3(visualOffset.x, visual.localPosition.y, visualOffset.z);
        animator.SetBool(crouchingParamId, false);
        animator.ResetTrigger(crouchTriggerId);
        if (locomotion != null)
            locomotion.SetImmediateBlendForCurrentInput();
        Debug.Log("[Slide] end crouch", this);
    }

    public void OpenCancelWindow()
    {
        cancelWindowOpen = true;
        onCancelWindowOpen?.Invoke();
    }

    public void CloseCancelWindow()
    {
        cancelWindowOpen = false;
        onCancelWindowClose?.Invoke();
    }

    public bool TryCancelIntoAttack()
    {
        if (!sliding || !CanCancelIntoAttack) return false;
        EndSlide();
        return true;
    }

    private bool IsInSlideState()
    {
        return animator.GetCurrentAnimatorStateInfo(0).fullPathHash == slideStateId;
    }

    private bool TryGetSlideDirection(out Vector3 direction)
    {
        direction = Vector3.zero;
        if (moveAction == null) return false;
        var move = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
        if (Camera.main == null) return false;
        var forward = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        var right = Vector3.Cross(Vector3.up, forward);
        direction = forward * move.y + right * move.x;
        return direction.sqrMagnitude > 0.0001f;
    }
}
