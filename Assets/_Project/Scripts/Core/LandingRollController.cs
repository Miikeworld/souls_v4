 using System;
using UnityEngine;

/// <summary>
/// Forward landing roll state. When the player lands while moving fast enough, this takes over
/// from the normal land state, plays a rolling clip, and moves the player to match the authored
/// root lunge while keeping the visual root counter-offset so the body stays with the character.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerState))]
[RequireComponent(typeof(CameraTiltController))]
[RequireComponent(typeof(PlayerLocomotion))]
[DefaultExecutionOrder(-15)]
public sealed class LandingRollController : MonoBehaviour
{
    [Header("State names")]
    [SerializeField] private string landingRollStateName = "Base Layer.LandingRoll";
    [SerializeField] private string landingRollTriggerName = "LandingRoll";
    [SerializeField] private string locomotionStateName = "Base Layer.Locomotion";

    [Header("Trigger conditions")]
    [Tooltip("Landing roll only triggers after a double jump. Uncheck this to disable landing rolls entirely.")]
    [SerializeField] private bool enableLandingRoll = false;
    [Tooltip("Minimum time spent in the air before a landing can become a roll.")]
    [SerializeField, Min(0f)] private float minAirTime = 0.15f;
    [Tooltip("Minimum horizontal landing speed to trigger a roll.")]
    [SerializeField, Min(0f)] private float minLandSpeed = 2f;
    [Tooltip("Minimum speed the roll itself moves the player, so a slow landing still produces a visible roll.")]
    [SerializeField, Min(0f)] private float minRollSpeed = 3f;

    [Header("Roll movement")]
    [Tooltip("Maximum horizontal distance the roll can cover from its start point.")]
    [SerializeField, Min(0.1f)] private float maxRollDistance = 5f;
    [Tooltip("Hard cap on how far the player can move per frame during the roll.")]
    [SerializeField, Min(0.1f)] private float maxRollMoveSpeed = 50f;
    [Tooltip("Playback speed for the roll animation. 1 = authored speed.")]
    [SerializeField, Min(0.1f)] private float rollAnimSpeed = 1f;
    [Tooltip("Base roll speed (m/s) the clip represents at rollAnimSpeed = 1. Higher entry speeds scale the playback up.")]
    [SerializeField, Min(0.1f)] private float baseRollSpeed = 3.5f;
    [SerializeField, Min(0.1f)] private float minRollAnimSpeed = 0.7f;
    [SerializeField, Min(0.1f)] private float maxRollAnimSpeed = 2f;

    [Header("Visual recovery")]
    [Tooltip("Cross-fade duration when the roll is interrupted or finishes.")]
    [SerializeField, Min(0f)] private float recoveryFadeDuration = 0.1f;
    [Tooltip("Minimum extra time after the roll state ends before control returns.")]
    [SerializeField, Min(0f)] private float recoveryDuration = 0.1f;

    [Header("Camera tilt timing for this state")]
    [SerializeField] private float tiltInDuration = 0.1f;
    [SerializeField] private float tiltOutDuration = 0.25f;

    private static readonly float VerticalTerminal = -50f;
    private const float FootLiftSmooth = 15f;
    private const float FootLiftMargin = 0.01f;

    private int landingRollStateId;
    private int landingRollTriggerId;
    private int locomotionStateId;
    private int jumpAirStateId;
    private int jumpForwardAirStateId;
    private int jumpDoubleAirStateId;
    private int jumpDoubleFallStateId;
    private int jumpLandStateId;
    private int jumpForwardLandStateId;

    private CharacterController character;
    private Animator animator;
    private PlayerState state;
    private CameraTiltController tilt;
    private PlayerLocomotion locomotion;
    private Transform hips;
    private Transform visual;

    private bool ready;
    private bool rolling;
    private bool recovering;
    private bool sawRollState;
    private bool rollRequested;
    private float rollRequestAge;
    private float rollAge;
    private float recoveryAge;
    private float airTime;
    private bool sawDoubleJump;
    private float verticalSpeed;

    private Vector3 visualOffset;
    private Vector3 initialHipsPose;
    private Vector3 initialPlayerPos;
    private Vector3 rollDirection;
    private float distanceLimit;
    private float lastRollDistance;
    private float currentRollSpeed;
    private float entrySpeed;
    private float rollTravelDuration;
    private float currentFootLift;

    private float measuredClipLength;
    private float measuredBaseSpeed;

    private void Awake()
    {
        character = GetComponent<CharacterController>();
        animator = FindValidAnimator();
        state = GetComponent<PlayerState>();
        tilt = GetComponent<CameraTiltController>();
        locomotion = GetComponent<PlayerLocomotion>();

        if (character == null || state == null || locomotion == null)
        {
            Debug.LogError("LandingRollController missing required components.", this);
            enabled = false;
            return;
        }

        if (animator == null)
        {
            Debug.LogError("LandingRollController could not find a valid humanoid Animator with a runtime controller.", this);
            enabled = false;
            return;
        }

        visual = animator.transform;
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips == null)
        {
            Debug.LogError("LandingRollController could not find Hips on the humanoid rig.", this);
            enabled = false;
            return;
        }

        landingRollStateId = Animator.StringToHash(landingRollStateName);
        landingRollTriggerId = Animator.StringToHash(landingRollTriggerName);
        locomotionStateId = Animator.StringToHash(locomotionStateName);
        jumpAirStateId = Animator.StringToHash("Base Layer.JumpAir");
        jumpForwardAirStateId = Animator.StringToHash("Base Layer.JumpForwardAir");
        jumpDoubleAirStateId = Animator.StringToHash("Base Layer.JumpDoubleAir");
        jumpDoubleFallStateId = Animator.StringToHash("Base Layer.JumpDoubleFall");
        jumpLandStateId = Animator.StringToHash("Base Layer.JumpLand");
        jumpForwardLandStateId = Animator.StringToHash("Base Layer.JumpForwardLand");

        ready = true;

        if (enableLandingRoll)
            CalibrateRollClip();
    }

    // Measures the natural planar lunge of the LandingRoll clip so we can scale
    // playback to match the player's landing speed instead of guessing a Base Roll Speed.
    private void CalibrateRollClip()
    {
        if (animator == null || hips == null || !animator.HasState(0, landingRollStateId))
            return;

        var previousCulling = animator.cullingMode;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var restoreState = 0;
        float restoreTime = 0f;
        var current = animator.GetCurrentAnimatorStateInfo(0);
        if (current.fullPathHash == locomotionStateId || current.fullPathHash != landingRollStateId)
        {
            restoreState = current.fullPathHash;
            restoreTime = current.normalizedTime;
        }

        try
        {
            animator.Play(landingRollStateId, 0, 0f);
            animator.Update(0f);
            var startPose = visual.InverseTransformPoint(hips.position);

            var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
            measuredClipLength = stateInfo.length;

            if (measuredClipLength > 0.001f)
            {
                animator.Play(landingRollStateId, 0, 1f);
                animator.Update(0f);
                var endPose = visual.InverseTransformPoint(hips.position);

                var localLunge = endPose - startPose;
                var worldLunge = visual.TransformVector(new Vector3(localLunge.x, 0f, localLunge.z));
                var planarLunge = new Vector3(worldLunge.x, 0f, worldLunge.z).magnitude;
                measuredBaseSpeed = planarLunge / measuredClipLength;

                Debug.Log($"[LandingRoll] calibrated clip length={measuredClipLength:F3}s lunge={planarLunge:F3}m baseSpeed={measuredBaseSpeed:F2}", this);
            }
        }
        catch (Exception exception)
        {
            Debug.LogError("[LandingRoll] clip calibration failed: " + exception.Message, this);
        }
        finally
        {
            if (restoreState != 0)
            {
                animator.Play(restoreState, 0, restoreTime);
                animator.Update(0f);
            }
            animator.cullingMode = previousCulling;
        }
    }

    private void OnDisable()
    {
        if (rolling || recovering)
            EndRoll();
        else if (rollRequested && state != null)
            state.IsDisplacing = false;
        rollRequested = false;
        rollRequestAge = 0f;
    }

    private void Update()
    {
        if (!ready) return;
        var dt = Time.deltaTime;

        if (rolling)
        {
            rollAge += dt;

            var currentRoll = IsInLandingRollState();
            var nextRoll = animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).fullPathHash == landingRollStateId;
            sawRollState |= currentRoll || nextRoll;

            if (!currentRoll && !nextRoll && (sawRollState || rollAge > 1f))
            {
                if (!sawRollState)
                    Debug.LogWarning("Landing roll state was not observed; ending to avoid a lock.", this);
                EndRoll();
            }
        }
        else if (recovering)
        {
            recoveryAge += dt;
            if (recoveryAge >= recoveryDuration || !animator.IsInTransition(0))
                EndRecovery();
        }
        else
        {
            TrackAirTimeAndTrigger();
        }

        // Only release a displacement request owned by this controller. Previously this cleared
        // IsDisplacing during unrelated states such as Slide, allowing two movement controllers
        // to move the CharacterController at the same time.
        if (rollRequested && !rolling)
        {
            rollRequestAge += dt;
            var hash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
            if (rollRequestAge >= 0.5f || (hash != jumpLandStateId && hash != jumpForwardLandStateId &&
                hash != jumpDoubleAirStateId && hash != jumpDoubleFallStateId && hash != landingRollStateId))
            {
                rollRequested = false;
                rollRequestAge = 0f;
                if (state != null)
                    state.IsDisplacing = false;
            }
        }
    }

    private void TrackAirTimeAndTrigger()
    {
        if (!enableLandingRoll)
        {
            airTime = 0f;
            return;
        }

        var current = animator.GetCurrentAnimatorStateInfo(0);
        var currentHash = current.fullPathHash;

        if (currentHash == jumpDoubleAirStateId || currentHash == jumpDoubleFallStateId)
            sawDoubleJump = true;

        if (sawDoubleJump && CanRollOnLand())
        {
            rollRequested = true;
            rollRequestAge = 0f;
            if (state != null)
                state.IsDisplacing = true;
            animator.SetTrigger(landingRollTriggerId);
            airTime = 0f;
            sawDoubleJump = false;
            Debug.Log("[LandingRoll] triggered", this);
            return;
        }

        if (currentHash == jumpAirStateId || currentHash == jumpForwardAirStateId ||
            currentHash == jumpDoubleAirStateId || currentHash == jumpDoubleFallStateId)
        {
            airTime += Time.deltaTime;
        }
        else if (currentHash == jumpLandStateId || currentHash == jumpForwardLandStateId)
        {
            airTime = 0f;
            sawDoubleJump = false;
        }
        else
        {
            airTime = 0f;
            sawDoubleJump = false;
        }
    }

    private bool CanRollOnLand()
    {
        if (locomotion == null) return false;
        return sawDoubleJump && airTime >= minAirTime && character.isGrounded &&
               Mathf.Max(locomotion.ActualPlanarSpeed, minRollSpeed) >= minLandSpeed;
    }

    private void LateUpdate()
    {
        if (!ready) return;

        // Fallback start if OnAnimatorMove was not called (e.g. applyRootMotion is disabled).
        if (!rolling && !recovering && animator.GetCurrentAnimatorStateInfo(0).fullPathHash == landingRollStateId)
            BeginRoll();

        var dt = Time.deltaTime;
        var worldOffset = Vector3.zero;

        if (rolling || recovering)
            worldOffset = ApplyVisualCounterOffset();

        // Keep the rolling body from clipping through the floor as the clip lowers the hips.
        if (rolling || recovering)
            PlantFeet(dt);

        if (rolling)
        {
            var currentRoll = IsInLandingRollState();
            var nextRoll = animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).fullPathHash == landingRollStateId;
            sawRollState |= currentRoll || nextRoll;

            // Drive the player forward at the landing speed for the duration of the roll.
            // The visual root is counter-offset by ApplyVisualCounterOffset, so the body follows
            // the player even if the clip's root motion loops back to the start.
            var authoredDistance = entrySpeed * rollAge;

            authoredDistance = Mathf.Max(authoredDistance, lastRollDistance);
            lastRollDistance = authoredDistance;
            lastRollDistance = Mathf.Min(lastRollDistance, distanceLimit);

            if (rollAge > 0.001f)
                currentRollSpeed = lastRollDistance / rollAge;

            var target = initialPlayerPos + rollDirection * lastRollDistance;
            var delta = target - transform.position;
            var planarMagnitude = new Vector3(delta.x, 0f, delta.z).magnitude;
            if (planarMagnitude > maxRollMoveSpeed * dt)
            {
                var planar = new Vector3(delta.x, 0f, delta.z).normalized * (maxRollMoveSpeed * dt);
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
            var stateEnded = !currentRoll && !nextRoll && sawRollState;
            var reachedCap = remaining <= 0.05f * distanceLimit && lastRollDistance >= distanceLimit * 0.99f;

            if ((reachedCap && remaining <= Mathf.Max(0.05f, 0.05f * distanceLimit)) ||
                (stateEnded && rollAge >= rollTravelDuration * 0.75f) || rollAge > rollTravelDuration + 0.25f)
                EndRoll();
        }
    }

    private void BeginRoll()
    {
        if (rolling) return;

        rolling = true;
        recovering = false;
        rollRequested = false;
        rollRequestAge = 0f;
        sawRollState = false;
        rollAge = 0f;
        currentRollSpeed = 0f;
        lastRollDistance = 0f;

        rollDirection = transform.forward;
        if (locomotion != null)
        {
            var velocity = locomotion.ActualPlanarVelocity;
            if (velocity.sqrMagnitude > 0.01f)
                rollDirection = Vector3.ProjectOnPlane(velocity, Vector3.up).normalized;
        }

        initialPlayerPos = transform.position;

        // Capture the landing speed so we can keep momentum through and after the roll.
        // Clamp to a minimum so the roll always moves the player visibly.
        var landed = locomotion != null ? locomotion.ActualPlanarSpeed : 0f;
        entrySpeed = Mathf.Max(landed, minRollSpeed);

        // Use the calibrated clip speed (lunge / clip length) if available, otherwise the
        // inspector Base Roll Speed. Scale playback so the root lunge rate matches the
        // landing speed as closely as the animation bounds allow.
        var effectiveBase = measuredBaseSpeed > 0.001f ? measuredBaseSpeed : baseRollSpeed;
        var speedScale = effectiveBase > 0.001f ? entrySpeed / effectiveBase : 1f;
        animator.speed = Mathf.Clamp(rollAnimSpeed * speedScale, minRollAnimSpeed, maxRollAnimSpeed);

        rollTravelDuration = measuredClipLength > 0.001f
            ? measuredClipLength / Mathf.Max(0.1f, animator.speed)
            : 0.65f;
        rollTravelDuration = Mathf.Clamp(rollTravelDuration, 0.35f, 1f);
        distanceLimit = Mathf.Min(maxRollDistance, entrySpeed * rollTravelDuration);

        // Visual reference: where the visual root is now and where the hips are now.
        visualOffset = visual.localPosition;
        initialHipsPose = visual.InverseTransformPoint(hips.position);

        verticalSpeed = 0f;
        currentFootLift = 0f;

        if (state != null)
            state.IsDisplacing = true;

        tilt.BeginTilt(new CameraDisplacementEvent(
            magnitude: 1f,
            tiltAmount: 0f,
            tiltInDuration: tiltInDuration,
            tiltOutDuration: tiltOutDuration,
            tiltCurve: null,
            pitchAmount: 0f,
            pitchInDuration: 0.1f,
            pitchOutDuration: 0.2f));

        Debug.Log($"[LandingRoll] begin dir={rollDirection:F2} limit={distanceLimit:F2}m speed={locomotion?.ActualPlanarSpeed:F2}", this);
    }

    private void EndRoll()
    {
        if (!rolling && !recovering) return;

        rolling = false;
        recovering = true;
        recoveryAge = 0f;

        animator.speed = 1f;
        if (locomotion != null)
        {
            locomotion.SetImmediateBlendForCurrentInput();
            // Preserve the landing speed so the roll does not bleed away momentum.
            var exitSpeed = Mathf.Max(currentRollSpeed, entrySpeed);
            locomotion.SetLocomotionSpeed(exitSpeed);
        }

        if (animator.GetCurrentAnimatorStateInfo(0).fullPathHash == landingRollStateId ||
            (animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).fullPathHash == landingRollStateId))
        {
            animator.CrossFadeInFixedTime(locomotionStateId, recoveryFadeDuration, 0);
        }

        if (state != null)
            state.IsDisplacing = false;

        tilt.EndTilt();
        Debug.Log("[LandingRoll] end", this);
    }

    private void EndRecovery()
    {
        recovering = false;
        currentFootLift = 0f;
        visual.localPosition = visualOffset;
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

        // Reset the visual to the base height before measuring so the previous lift
        // does not corrupt the ground reading.
        var basePos = visual.localPosition;
        basePos.y = visualOffset.y;
        visual.localPosition = basePos;

        var groundY = SampleGroundY();
        var lowestY = Mathf.Min(leftFoot.position.y, rightFoot.position.y);
        if (leftToes != null) lowestY = Mathf.Min(lowestY, leftToes.position.y);
        if (rightToes != null) lowestY = Mathf.Min(lowestY, rightToes.position.y);

        // A forward roll can dip the hip/back below the floor more than the feet.
        lowestY = Mathf.Min(lowestY, hips.position.y);

        // The clip is on the ground, but the skeleton may dip below the floor. Lift the
        // visual root so the lowest foot/toe stays just above the actual ground.
        var targetLift = groundY + FootLiftMargin - lowestY;
        currentFootLift = Mathf.Lerp(currentFootLift, targetLift, FootLiftSmooth * dt);

        var pos = visual.localPosition;
        pos.y = visualOffset.y + currentFootLift;
        visual.localPosition = pos;
    }

    private float SampleGroundY()
    {
        var origin = transform.position + character.center;
        var hits = Physics.RaycastAll(origin, Vector3.down, character.height * 0.5f + character.radius + 2f, ~0, QueryTriggerInteraction.Ignore);

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

    private Vector3 ApplyVisualCounterOffset()
    {
        var currentPose = visual.InverseTransformPoint(hips.position);
        var hipsOffset = currentPose - initialHipsPose;
        var worldOffset = visual.TransformVector(hipsOffset);

        var rootLocalOffset = transform.InverseTransformVector(new Vector3(worldOffset.x, 0f, worldOffset.z));
        visual.localPosition = visualOffset - rootLocalOffset;
        return worldOffset;
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

    private bool IsInLandingRollState()
    {
        return animator.GetCurrentAnimatorStateInfo(0).fullPathHash == landingRollStateId;
    }

    private void OnAnimatorMove()
    {
        // Root motion is disabled, but we still need to detect when the LandingRoll state has
        // actually started so the controller can capture the initial pose. OnAnimatorMove runs
        // after the animator has evaluated for the frame, so the state is reliable here.
        if (!ready) return;
        if (rolling) return;

        var current = animator.GetCurrentAnimatorStateInfo(0);
        if (current.fullPathHash == landingRollStateId)
            BeginRoll();
    }
}
