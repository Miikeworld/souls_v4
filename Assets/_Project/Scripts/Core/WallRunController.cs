using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Titanfall-style wall run. While airborne and moving near a vertical wall, the player
/// magnetically attaches (no button needed), keeps entry momentum plus a boost, and rides
/// the wall tangentially with gravity suspended until the wall ends, a duration cap hits,
/// or the player wall-jumps off. Touching a wall refreshes one air jump; a cooldown stops
/// instantly re-attaching to the same wall so chaining means alternating walls.
/// Root motion stays disabled; the controller matches the visual root to the character.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerState))]
[RequireComponent(typeof(CameraTiltController))]
[RequireComponent(typeof(PlayerLocomotion))]
[DefaultExecutionOrder(-15)]
public sealed class WallRunController : MonoBehaviour
{
    [Header("State names")]
    [SerializeField] private string wallRunLeftStateName = "Base Layer.WallRunLeft";
    [SerializeField] private string wallRunRightStateName = "Base Layer.WallRunRight";
    [SerializeField] private string wallRunStartLeftStateName = "Base Layer.WallRunStartLeft";
    [SerializeField] private string wallRunStartRightStateName = "Base Layer.WallRunStartRight";
    [SerializeField] private string locomotionStateName = "Base Layer.Locomotion";
    [SerializeField] private string jumpAirStateName = "Base Layer.JumpAir";

    [Header("Wall detection")]
    [Tooltip("Layers that count as walls.")]
    [SerializeField] private LayerMask wallLayers = ~0;
    [Tooltip("How far from the player to check for a side wall.")]
    [SerializeField, Min(0.1f)] private float wallCheckDistance = 1.2f;
    [Tooltip("Radius of the sphere used to probe the wall.")]
    [SerializeField, Min(0.05f)] private float wallCheckSphereRadius = 0.3f;
    [Tooltip("Minimum horizontal speed toward the wall to allow a wall run.")]
    [SerializeField, Min(0f)] private float minApproachSpeed = 1.5f;
    [Tooltip("Maximum slope of the wall normal in degrees (0 = perfectly vertical).")]
    [SerializeField, Range(0f, 45f)] private float maxWallSlope = 15f;
    [Tooltip("Height above the capsule center for the upper-body wall check. The wall must be within reach at this height too, so low ledges and leg-only contact can't start a wall run.")]
    [SerializeField, Min(0.1f)] private float handCheckHeight = 0.5f;

    [Header("Wall run movement")]
    [Tooltip("Attach automatically on wall contact while airborne, Titanfall-style. Off = requires pressing Jump near a wall.")]
    [SerializeField] private bool autoAttach = true;
    [Tooltip("Multiplier applied to your entry speed when the wall run begins. >1 feels like a boost.")]
    [SerializeField, Min(0.5f)] private float wallRunSpeedMultiplier = 1.15f;
    [Tooltip("Minimum wall-run speed regardless of entry speed.")]
    [SerializeField, Min(0.1f)] private float minWallRunSpeed = 4f;
    [Tooltip("Maximum wall-run speed. Chained wall runs converge to this instead of stacking without bound.")]
    [SerializeField, Min(0.1f)] private float maxWallRunSpeed = 8.5f;
    [Tooltip("Speed gained per second while wall-running, so the run builds momentum instead of staying flat.")]
    [SerializeField, Min(0f)] private float wallRunAcceleration = 2f;
    [Tooltip("Absolute ceiling for wall-run speed once acceleration builds up.")]
    [SerializeField, Min(0.1f)] private float wallRunTopSpeed = 10f;
    [Tooltip("Bonus speed granted the first time you wall-jump onto a different wall. Applies once per airborne chain so it cannot compound.")]
    [SerializeField, Min(0f)] private float wallChainBoost = 2f;
    [Tooltip("Hard cap on how far the player can move per frame during the wall run.")]
    [SerializeField, Min(0.1f)] private float maxWallRunMoveSpeed = 50f;
    [Tooltip("Playback speed for the wall run animation.")]
    [SerializeField, Min(0.1f)] private float wallRunAnimSpeed = 1.15f;
    [Tooltip("Maximum time attached to the wall before being ejected.")]
    [SerializeField, Min(0.2f)] private float wallRunDuration = 3f;
    [Tooltip("Initial upward drift speed when the wall run starts; decays into the sink below.")]
    [SerializeField, Min(0f)] private float wallRunRise = 0.35f;
    [Tooltip("Downward acceleration applied during the run, so altitude bleeds off like gravity returning.")]
    [SerializeField, Min(0f)] private float wallRunSinkAccel = 4f;
    [Tooltip("Terminal sink speed during a wall run; the run ends by reaching the ground.")]
    [SerializeField, Min(0f)] private float wallRunMaxSink = 3f;
    [Tooltip("Distance maintained between the player center and wall surface.")]
    [SerializeField, Min(0.1f)] private float wallClearance = 0.65f;
    [Tooltip("How fast the player is pulled toward the wall when attaching.")]
    [SerializeField, Min(0.1f)] private float wallAttachPullSpeed = 7f;

    [Header("Wall jump and chaining")]
    [Tooltip("Push-off speed away from the wall on a wall jump.")]
    [SerializeField, Min(0f)] private float wallJumpPush = 4.5f;
    [SerializeField, Min(0f)] private float wallJumpBoost = 2.5f;
    [Tooltip("Upward impulse on a wall jump.")]
    [SerializeField, Min(0f)] private float wallJumpUp = 5.5f;
    [Tooltip("How long the wall you just left refuses to re-attach. Other walls are unaffected.")]
    [SerializeField, Min(0f)] private float sameWallCooldown = 1.25f;
    [Tooltip("Minimum time after any wall run before another can begin.")]
    [SerializeField, Min(0f)] private float reattachDelay = 0.25f;

    [Header("Visual recovery")]
    [Tooltip("Cross-fade time into the wall run start state.")]
    [SerializeField, Min(0f)] private float enterFadeDuration = 0.1f;
    [Tooltip("Seconds into the wall-run start clip to begin playback. Skips the wind-up so the get-on-the-wall motion reads instantly.")]
    [SerializeField, Min(0f)] private float enterTimeOffset = 0.25f;
    [SerializeField, Min(0f)] private float recoveryFadeDuration = 0.1f;
    [SerializeField, Min(0f)] private float recoveryDuration = 0.1f;

    [Header("Camera tilt profile for this state")]
    [SerializeField] private float tiltAmount = 10f;
    [SerializeField] private float tiltInDuration = 0.15f;
    [SerializeField] private float tiltOutDuration = 0.3f;

    [Header("Wall dash — short burst across a narrow two-wall gap")]
    [Tooltip("Jump press while airborne with a wall on one side and another WallRunSurface within this range on the opposite side = the burst, not a run.")]
    [SerializeField, Min(1f)] private float wallDashGap = 5.5f;
    [SerializeField, Min(0.1f)] private float wallDashDuration = 0.38f;
    [SerializeField, Min(1f)] private float wallDashSpeed = 15f;
    [Tooltip("Forward-along-the-wall blend in the burst direction — 0 = straight across.")]
    [SerializeField, Range(0f, 1f)] private float wallDashForward = 0.35f;
    [Tooltip("Slight rise baked into the burst direction.")]
    [SerializeField, Min(0f)] private float wallDashRise = 1.2f;
    [Tooltip("Planar exit speed when the burst releases.")]
    [SerializeField, Min(0f)] private float wallDashExitSpeed = 7f;
    [Tooltip("Camera roll during the burst — the reference's ~90° roll, signed by side.")]
    [SerializeField, Range(0f, 90f)] private float wallDashTilt = 80f;
    [Tooltip("Grace before either wall may re-attach after a burst, so the hop reads as discrete.")]
    [SerializeField, Min(0f)] private float wallDashReattachDelay = 0.35f;

    private static readonly int InAirId = Animator.StringToHash("InAir");

    private int wallRunLeftStateId;
    private int wallRunRightStateId;
    private int wallRunStartLeftStateId;
    private int wallRunStartRightStateId;
    private int locomotionStateId;
    private int jumpAirStateId;
    private int jumpForwardAirStateId;
    private int jumpLandStateId;
    private int jumpForwardLandStateId;

    private enum WallSide { None, Left, Right }

    private CharacterController character;
    private Animator animator;
    private PlayerState state;
    private CameraTiltController tilt;
    private PlayerLocomotion locomotion;
    private Transform hips;
    private Transform visual;

    private bool ready;
    private bool wallRunning;
    private bool wallRunRecovering;
    private bool wallRunRequested;
    private bool sawWallRunState;
    private float wallRunRequestAge;
    private float wallRunAge;
    private float recoveryAge;

    private Vector3 visualOffset;
    private Vector3 initialHipsPose;
    private Vector3 wallRunDirection;
    private Vector3 activeWallNormal;
    private Vector3 activeWallPoint;
    private Collider activeWallCollider;
    private WallRunSurface activeSurface;
    private Collider blockedWall;
    private WallSide currentSide;
    private float blockedUntil;
    private float reattachUntil;
    private float wallRunSpeed;
    private float currentWallRunSpeed;
    private int groundedFrames;
    private bool chainBoostAvailable;
    private bool chainBoostUsed;
    private float pendingEntrySpeed;
    private float wallRunVerticalVel;

    private bool wallDashing;
    private float dashAge;
    private Vector3 dashDir;

    /// <summary>True while attached to a wall or mid wall-dash — locomotion is
    /// frozen (IsDisplacing), so HUD FX gates must read this to see the run.</summary>
    public bool IsWallRunning => wallRunning || wallDashing;
    public bool AttachedToWall => wallRunning;
    public Vector3 WallSurfaceNormal => activeWallNormal;
    /// <summary>Live re-probed contact point on the wall face (TraveraEffects
    /// parks the palm rings just off it).</summary>
    public Vector3 WallSurfacePoint => activeWallPoint;
    /// <summary>True when the active wall is on the player's RIGHT.</summary>
    public bool WallOnRight => currentSide == WallSide.Right;
    /// <summary>The marker of the wall being run (null when none).</summary>
    public WallRunSurface ActiveSurface => wallRunning ? activeSurface : null;
    private float ActiveCharge => activeSurface != null ? Mathf.Clamp01(activeSurface.Charge) : 0f;

    private void Awake()
    {
        character = GetComponent<CharacterController>();
        animator = FindValidAnimator();
        state = GetComponent<PlayerState>();
        tilt = GetComponent<CameraTiltController>();
        locomotion = GetComponent<PlayerLocomotion>();

        if (character == null || state == null || locomotion == null)
        {
            Debug.LogError("WallRunController missing required components.", this);
            enabled = false;
            return;
        }

        if (animator == null)
        {
            Debug.LogError("WallRunController could not find a valid humanoid Animator with a runtime controller.", this);
            enabled = false;
            return;
        }

        visual = animator.transform;
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips == null)
        {
            Debug.LogError("WallRunController could not find Hips on the humanoid rig.", this);
            enabled = false;
            return;
        }

        wallRunLeftStateId = Animator.StringToHash(wallRunLeftStateName);
        wallRunRightStateId = Animator.StringToHash(wallRunRightStateName);
        wallRunStartLeftStateId = Animator.StringToHash(wallRunStartLeftStateName);
        wallRunStartRightStateId = Animator.StringToHash(wallRunStartRightStateName);
        locomotionStateId = Animator.StringToHash(locomotionStateName);
        jumpAirStateId = Animator.StringToHash(jumpAirStateName);
        jumpForwardAirStateId = Animator.StringToHash("Base Layer.JumpForwardAir");
        jumpLandStateId = Animator.StringToHash("Base Layer.JumpLand");
        jumpForwardLandStateId = Animator.StringToHash("Base Layer.JumpForwardLand");

        ready = true;
    }

    private void OnDisable()
    {
        if ((wallRunning || wallRunRecovering) && ready)
            EndWallRun();
        else if ((wallRunRequested || wallDashing) && state != null)
            state.IsDisplacing = false;
        if (wallDashing) tilt?.EndTilt();
        wallDashing = false;
        wallRunRequested = false;
        wallRunRequestAge = 0f;
    }

    private void Update()
    {
        if (!ready || state.IsDead) return;

        // Touching the ground resets the chain so the next wall-jump chain can boost again.
        if (!wallRunning && character.isGrounded)
        {
            chainBoostAvailable = false;
            chainBoostUsed = false;
        }

        if (wallDashing) return; // the burst owns the capsule; LateUpdate moves/ends it

        if (wallRunning)
        {
            wallRunAge += Time.deltaTime;

            if (locomotion?.JumpAction != null && locomotion.JumpAction.WasPressedThisFrame())
            {
                WallJump();
                return;
            }

            var current = IsInWallRunState();
            var next = IsNextStateWallRun();
            sawWallRunState |= current || next;

            if (!current && !next && (sawWallRunState || wallRunAge > wallRunDuration + 0.25f))
            {
                if (!sawWallRunState)
                    Debug.LogWarning("Wall run state was not observed; ending to avoid a lock.", this);
                EndWallRun();
            }
        }
        else if (wallRunRecovering)
        {
            recoveryAge += Time.deltaTime;
            if (recoveryAge >= recoveryDuration || !animator.IsInTransition(0))
                EndRecovery();
        }
        else
        {
            TryStartWallRun();
        }

        if (wallRunRequested && !wallRunning)
        {
            wallRunRequestAge += Time.deltaTime;
            if (wallRunRequestAge >= 0.5f)
            {
                wallRunRequested = false;
                wallRunRequestAge = 0f;
                if (state != null)
                    state.IsDisplacing = false;
            }
        }
    }

    private void LateUpdate()
    {
        if (!ready) return;

        // Fallback start if the Animator state started before Update could catch it.
        // Skipped while dashing — the burst plays a run pose but isn't a run.
        if (!wallRunning && !wallRunRecovering && !wallDashing)
        {
            var hash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
            if (IsWallRunHash(hash))
                BeginWallRun(hash == wallRunLeftStateId || hash == wallRunStartLeftStateId ? WallSide.Left : WallSide.Right);
        }

        if (wallRunning)
            ApplyVisualCounterOffset();
        else if (wallRunRecovering)
            visual.localPosition = Vector3.Lerp(visual.localPosition, visualOffset, Mathf.Clamp01(Time.deltaTime / Mathf.Max(0.01f, recoveryDuration)));

        if (wallDashing)
        {
            var dt = Time.deltaTime;
            dashAge += dt;
            var planar = new Vector3(dashDir.x, 0f, dashDir.z);
            var reached = planar.sqrMagnitude > 0.01f &&
                Physics.Raycast(transform.position + character.center, planar.normalized,
                                wallClearance + 0.35f, wallLayers, QueryTriggerInteraction.Ignore);
            if (reached || dashAge >= wallDashDuration || character.isGrounded)
                EndWallDash();
            else
                character.Move(dashDir * wallDashSpeed * dt);
            return;
        }

        if (wallRunning)
        {
            var current = IsInWallRunState();
            var next = IsNextStateWallRun();
            sawWallRunState |= current || next;

            var dt = Time.deltaTime;
            var origin = transform.position + character.center;

            // Re-probe the wall every frame: running off the end ejects you with full momentum.
            if (!ProbeActiveWall(origin))
            {
                Debug.Log("[WallRun] wall lost; ejecting.", this);
                EndWallRun();
                return;
            }

            // Steer along the wall tangent so curved or stepped walls track.
            var tangent = Vector3.ProjectOnPlane(wallRunDirection, activeWallNormal);
            if (tangent.sqrMagnitude > 0.001f)
                wallRunDirection = tangent.normalized;

            // Magnetic band: push out instantly when too close, pull in at a limited speed
            // when drifting away, so the player feels attached rather than orbiting.
            var distanceFromWall = Vector3.Dot(origin - activeWallPoint, activeWallNormal);
            var correction = wallClearance - distanceFromWall;
            var lateral = correction > 0f ? correction : Mathf.Max(correction, -wallAttachPullSpeed * dt);

            // Accelerate along the wall: entry speed ramps toward the top speed, so a
            // longer run keeps building momentum (still bounded by wallRunTopSpeed).
            wallRunSpeed = Mathf.Min(wallRunSpeed + wallRunAcceleration * dt, wallRunTopSpeed);

            // Vertical arc: the initial pop decays into an accelerating sink, so the run
            // bleeds altitude and ends by reaching the ground rather than floating.
            // A Core-charged surface holds the runner up (sink mostly cancelled).
            var charge = ActiveCharge;
            wallRunVerticalVel = Mathf.Max(wallRunVerticalVel - wallRunSinkAccel * Mathf.Lerp(1f, 0.15f, charge) * dt,
                                           -wallRunMaxSink * Mathf.Lerp(1f, 0.2f, charge));
            var delta = wallRunDirection * (wallRunSpeed * dt) + Vector3.up * (wallRunVerticalVel * dt) + activeWallNormal * lateral;
            currentWallRunSpeed = wallRunSpeed;
            if (delta.magnitude > maxWallRunMoveSpeed * dt)
                delta = delta.normalized * (maxWallRunMoveSpeed * dt);

            character.Move(delta);
            if (wallRunDirection.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(wallRunDirection);

            // Debounce isGrounded: the flag flickers near walls/ground, and a single-frame
            // true must not kill the run.
            groundedFrames = character.isGrounded ? groundedFrames + 1 : 0;
            if (wallRunAge >= wallRunDuration * (1f + charge) || (wallRunAge > 0.25f && groundedFrames >= 3))
                EndWallRun();
        }
    }

    private bool ProbeActiveWall(Vector3 origin)
    {
        if (activeWallCollider == null || (activeWallCollider is MeshCollider meshCollider && !meshCollider.convex))
            return false;

        // ClosestPoint tracks the same collider we attached to and still works when the
        // player is closer than a sphere radius, where a SphereCast would report nothing.
        var point = activeWallCollider.ClosestPoint(origin);
        var fromWall = origin - point;
        var distance = fromWall.magnitude;
        if (distance > wallCheckDistance + wallClearance)
            return false;
        if (distance <= 0.001f)
            return true;

        var normal = fromWall / distance;

        // Ran off the end: the closest point on the wall is now behind the run direction.
        if (Vector3.Dot(point - origin, wallRunDirection) < -wallClearance)
            return false;

        // A steep normal means ClosestPoint hit the wall's top edge or a floor lip while
        // the player is still beside it — keep the last good side normal instead of ejecting.
        if (Mathf.Abs(normal.y) <= Mathf.Sin(maxWallSlope * Mathf.Deg2Rad) + 0.001f)
        {
            activeWallPoint = point;
            activeWallNormal = normal;
        }
        return true;
    }

    private void OnAnimatorMove()
    {
        if (!ready || wallRunning || wallRunRecovering || wallDashing) return;

        var hash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
        if (hash == wallRunLeftStateId || hash == wallRunStartLeftStateId)
            BeginWallRun(WallSide.Left);
        else if (hash == wallRunRightStateId || hash == wallRunStartRightStateId)
            BeginWallRun(WallSide.Right);
    }

    /// <summary>A second WallRunSurface on the far side of a narrow gap, measured
    /// against the normal of the wall just detected.</summary>
    private bool DetectOppositeWall(Vector3 origin, Vector3 nearNormal, Collider near, out Vector3 point)
    {
        point = default;
        if (!Physics.Raycast(origin, -nearNormal, out var hit, wallDashGap, wallLayers, QueryTriggerInteraction.Ignore))
            return false;
        if (hit.collider == near || hit.collider.transform.IsChildOf(transform)) return false;
        if (hit.collider.GetComponentInParent<WallRunSurface>() == null) return false;
        point = hit.point;
        return true;
    }

    /// <summary>The reference's wall-to-wall burst: a short fast diagonal across a
    /// narrow gap, ~90° camera roll for the duration — not a sustained run.</summary>
    private void BeginWallDash(WallSide side, Vector3 toOpposite)
    {
        var across = new Vector3(toOpposite.x, 0f, toOpposite.z);
        if (across.sqrMagnitude < 0.01f) return;
        var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        dashDir = (across.normalized + forward * wallDashForward + Vector3.up * (wallDashRise / wallDashSpeed)).normalized;
        wallDashing = true;
        dashAge = 0f;
        if (state != null) state.IsDisplacing = true;
        locomotion?.ConsumeJumpPress();

        var stateId = side == WallSide.Left ? wallRunLeftStateId : wallRunRightStateId;
        if (stateId != 0 && animator.HasState(0, stateId))
            animator.CrossFadeInFixedTime(stateId, 0.08f, 0);
        animator.SetBool(InAirId, true);

        var sign = side == WallSide.Left ? -1f : 1f;
        tilt.BeginTilt(new CameraDisplacementEvent(
            magnitude: 1f,
            tiltAmount: wallDashTilt * sign,
            tiltInDuration: 0.08f,
            tiltOutDuration: 0.35f,
            tiltCurve: null,
            pitchAmount: 0f,
            pitchInDuration: 0.1f,
            pitchOutDuration: 0.2f));
        Debug.Log($"[WallRun] wall-dash burst dir={dashDir:F2} gap≈{across.magnitude:F1}m", this);
    }

    private void EndWallDash()
    {
        wallDashing = false;
        // The far wall may re-grab after the hop reads — not instantly mid-burst.
        reattachUntil = Time.time + wallDashReattachDelay;
        if (state != null) state.IsDisplacing = false;
        tilt.EndTilt();
        if (locomotion != null)
        {
            var exit = dashDir * wallDashExitSpeed;
            exit.y = Mathf.Max(exit.y, 2f);
            locomotion.ApplyAirVelocity(exit);
            locomotion.SetImmediateBlendForCurrentInput();
        }
        if (animator.gameObject.activeInHierarchy)
            animator.CrossFadeInFixedTime(jumpForwardAirStateId != 0 ? jumpForwardAirStateId : jumpAirStateId, recoveryFadeDuration, 0);
        Debug.Log("[WallRun] wall-dash end", this);
    }

    private void TryStartWallRun()
    {
        var jumpPressed = locomotion?.JumpAction != null && locomotion.JumpAction.WasPressedThisFrame();
        if (!autoAttach && !jumpPressed) return;
        if (state != null && (state.IsDisplacing || state.IsDrinking)) return;
        if (Time.time < reattachUntil) return;
        // InAir is driven by PlayerLocomotion with grace/lock logic, so it is more reliable
        // than the flickery CharacterController.isGrounded for "currently airborne".
        if (!animator.GetBool(InAirId)) return;
        if (locomotion.ActualPlanarSpeed < minApproachSpeed)
        {
            if (jumpPressed)
                Debug.Log($"[WallRun] blocked: speed {locomotion.ActualPlanarSpeed:F2} < {minApproachSpeed:F2}", this);
            return;
        }

        if (!DetectWall(out var side, out var wallNormal, out var wallPoint, out var wallCollider))
        {
            if (jumpPressed)
                Debug.Log($"[WallRun] blocked: no vertical wall within {wallCheckDistance + wallCheckSphereRadius:F2}m", this);
            return;
        }

        // Jump press + a second wall opposite inside the gap = the reference's
        // short diagonal burst — it shadows the double jump only in a narrow shaft.
        if (jumpPressed)
        {
            var gapOrigin = transform.position + character.center;
            if (DetectOppositeWall(gapOrigin, wallNormal, wallCollider, out var oppositePoint))
            {
                BeginWallDash(side, oppositePoint - gapOrigin);
                return;
            }
        }

        // Choose a tangent that keeps the wall on the correct side for the matching clip.
        // For a left-wall run the wall must end up on the character's left; for a right-wall
        // run it must end up on the character's right.
        var tangent = side == WallSide.Left
            ? Vector3.Cross(wallNormal, Vector3.up).normalized
            : Vector3.Cross(Vector3.up, wallNormal).normalized;

        var velocity = locomotion.ActualPlanarVelocity;
        if (Vector3.Dot(tangent, velocity) < 0f)
        {
            // The player is moving against the natural run direction for that wall side,
            // so do not start a wall run in this orientation.
            if (jumpPressed)
                Debug.Log("[WallRun] skipped: moving against the natural run direction.", this);
            return;
        }

        if (tangent.sqrMagnitude < 0.0001f)
            tangent = transform.forward;

        wallRunRequested = true;
        wallRunRequestAge = 0f;

        // Capture momentum BEFORE IsDisplacing freezes locomotion: the frozen frames read
        // ~0 speed from the position delta, which would drop the wall-run entry to the
        // minimum speed instead of carrying the approach momentum.
        pendingEntrySpeed = locomotion != null ? locomotion.ActualPlanarSpeed : 0f;

        if (state != null)
            state.IsDisplacing = true;

        // Pre-rotate the player to face along the wall.
        transform.rotation = Quaternion.LookRotation(tangent);
        wallRunDirection = tangent;
        activeWallNormal = wallNormal;
        activeWallPoint = wallPoint;
        activeWallCollider = wallCollider;
        activeSurface = wallCollider != null ? wallCollider.GetComponentInParent<WallRunSurface>() : null;
        currentSide = side;

        // Enter through the authored start state for a fresh attach — but when chaining
        // from a wall jump, snap straight into the loop with a shorter fade so
        // wall-to-wall feels instant (Titanfall-style re-stick).
        var chaining = chainBoostAvailable;
        var useStart = !chaining &&
                       animator.HasState(0, wallRunStartLeftStateId) && animator.HasState(0, wallRunStartRightStateId);
        var stateId = side == WallSide.Left
            ? (useStart ? wallRunStartLeftStateId : wallRunLeftStateId)
            : (useStart ? wallRunStartRightStateId : wallRunRightStateId);
        var fade = chaining ? enterFadeDuration * 0.5f : enterFadeDuration;
        var offset = useStart ? enterTimeOffset : 0f;
        animator.CrossFadeInFixedTime(stateId, fade, 0, offset);
        animator.SetBool(InAirId, true);

        // The actual movement capture happens in OnAnimatorMove / LateUpdate BeginWallRun.
        Debug.Log($"[WallRun] start side={side} dir={wallRunDirection:F2}", this);
    }

    private void BeginWallRun(WallSide side)
    {
        if (wallRunning) return;

        wallRunning = true;
        wallRunRecovering = false;
        wallRunRequested = false;
        wallRunRequestAge = 0f;
        sawWallRunState = false;
        wallRunAge = 0f;
        groundedFrames = 0;
        currentSide = side;

        // Momentum entry: use the speed captured at detection time — by the time the
        // state enters, locomotion was frozen by IsDisplacing so the live speed reads ~0.
        var entrySpeed = pendingEntrySpeed > 0.01f
            ? pendingEntrySpeed
            : (locomotion != null ? locomotion.ActualPlanarSpeed : 0f);
        pendingEntrySpeed = 0f;
        wallRunSpeed = Mathf.Clamp(
            entrySpeed * wallRunSpeedMultiplier,
            minWallRunSpeed, maxWallRunSpeed);

        // Chain boost: armed by WallJump, spent on the next attach, once per airborne
        // chain — further wall-to-wall jumps in the same chain do not re-boost.
        if (chainBoostAvailable && !chainBoostUsed)
        {
            wallRunSpeed = Mathf.Min(wallRunSpeed + wallChainBoost, wallRunTopSpeed);
            chainBoostUsed = true;
            Debug.Log($"[WallRun] chain boost applied, speed={wallRunSpeed:F2}", this);
        }
        chainBoostAvailable = false;
        currentWallRunSpeed = wallRunSpeed;

        // Touching the wall refreshes one air jump, so a wall jump can chain onward.
        locomotion?.RefreshAirJumps();
        wallRunVerticalVel = wallRunRise;

        visualOffset = visual.localPosition;
        initialHipsPose = visual.InverseTransformPoint(hips.position);

        animator.speed = wallRunAnimSpeed;

        if (state != null)
            state.IsDisplacing = true;

        var sign = side == WallSide.Left ? -1f : 1f;
        tilt.BeginTilt(new CameraDisplacementEvent(
            magnitude: 1f,
            tiltAmount: tiltAmount * sign,
            tiltInDuration: tiltInDuration,
            tiltOutDuration: tiltOutDuration,
            tiltCurve: null,
            pitchAmount: 0f,
            pitchInDuration: 0.1f,
            pitchOutDuration: 0.2f));

        Debug.Log($"[WallRun] begin side={side} speed={wallRunSpeed:F2}m/s", this);
    }

    /// <summary>
    /// Natural end: duration cap, wall ran out, or touching the ground.
    /// The player keeps full momentum; airborne exits resume a clean fall.
    /// </summary>
    private void EndWallRun()
    {
        if (!wallRunning && !wallRunRecovering) return;
        StopWallRun();

        // Play-mode teardown can call this from OnDisable with the visual child already
        // inactive; parameter sets are safe but CrossFade would throw, so skip playback.
        var animatorActive = animator.gameObject.activeInHierarchy;

        if (locomotion != null)
        {
            locomotion.SetImmediateBlendForCurrentInput();

            if (character.isGrounded)
            {
                locomotion.SetLocomotionSpeed(currentWallRunSpeed);
                if (animatorActive)
                {
                    animator.SetBool(InAirId, false);
                    animator.CrossFadeInFixedTime(locomotionStateId, recoveryFadeDuration, 0);
                }
            }
            else
            {
                var exitRise = wallRunRise / Mathf.Max(0.1f, wallRunDuration);
                locomotion.ApplyAirVelocity(wallRunDirection * currentWallRunSpeed + Vector3.up * exitRise);
                if (animatorActive)
                    animator.CrossFadeInFixedTime(jumpAirStateId, recoveryFadeDuration, 0);
            }
        }
        else if (animatorActive)
        {
            animator.CrossFadeInFixedTime(locomotionStateId, recoveryFadeDuration, 0);
        }

        Debug.Log("[WallRun] end", this);
    }

    /// <summary>
    /// Jump pressed while attached: launch off the wall — away + up + keep forward speed —
    /// through the normal gravity/momentum path. Titanfall's signature exit.
    /// </summary>
    private void WallJump()
    {
        var planar = wallRunDirection * (wallRunSpeed + wallJumpBoost) + activeWallNormal * wallJumpPush;
        var exit = planar + Vector3.up * wallJumpUp;
        chainBoostAvailable = true; // arm the one-time chain boost for the next wall
        GetComponent<TraversalEffects>()?.KickBurst(
            transform.position + Vector3.up * .2f, planar.normalized);
        StopWallRun(chainable: true); // no reattach delay after a deliberate wall jump

        if (planar.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.LookRotation(planar.normalized);

        if (locomotion != null)
        {
            locomotion.ConsumeJumpPress();
            locomotion.UseNormalAnimForNextAirJump();
            locomotion.ApplyAirVelocity(exit);
        }
        if (animator.gameObject.activeInHierarchy)
            animator.CrossFadeInFixedTime(jumpForwardAirStateId, recoveryFadeDuration, 0);
        Debug.Log($"[WallRun] wall jump exit={exit:F2}", this);
    }

    private void StopWallRun(bool chainable = false)
    {
        wallRunning = false;
        wallRunRecovering = true;
        recoveryAge = 0f;
        animator.speed = 1f;

        if (state != null)
            state.IsDisplacing = false;

        // The wall just left refuses to re-attach for a moment; other walls still can.
        if (activeWallCollider != null)
        {
            blockedWall = activeWallCollider;
            blockedUntil = Time.time + sameWallCooldown;
        }
        // A deliberate wall jump keeps chaining instant — the delay only guards
        // unintended re-grabs after natural ends.
        reattachUntil = Time.time + (chainable ? 0f : reattachDelay);

        tilt.EndTilt();
    }

    private void EndRecovery()
    {
        wallRunRecovering = false;
        visual.localPosition = visualOffset;
    }

    private Vector3 ApplyVisualCounterOffset()
    {
        var currentPose = visual.InverseTransformPoint(hips.position);
        var hipsOffset = currentPose - initialHipsPose;
        var worldOffset = visual.TransformVector(hipsOffset);

        // Cancel the full 3D lunge for a wall run, then lock the visual root back to the player.
        var rootLocalOffset = transform.InverseTransformVector(worldOffset);
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

    private bool IsInWallRunState()
    {
        return IsWallRunHash(animator.GetCurrentAnimatorStateInfo(0).fullPathHash);
    }

    private bool IsWallRunHash(int hash)
    {
        return hash == wallRunLeftStateId || hash == wallRunRightStateId ||
               hash == wallRunStartLeftStateId || hash == wallRunStartRightStateId;
    }

    private bool IsNextStateWallRun()
    {
        return animator.IsInTransition(0) && IsWallRunHash(animator.GetNextAnimatorStateInfo(0).fullPathHash);
    }

    private bool DetectWall(out WallSide side, out Vector3 wallNormal, out Vector3 wallPoint, out Collider wallCollider)
    {
        side = WallSide.None;
        wallNormal = Vector3.zero;
        wallPoint = Vector3.zero;
        wallCollider = null;

        if (character == null) return false;

        var origin = transform.position + character.center;
        var handOrigin = origin + Vector3.up * handCheckHeight;
        var velocity = locomotion != null ? locomotion.ActualPlanarVelocity : transform.forward;
        var slopeSin = Mathf.Sin(maxWallSlope * Mathf.Deg2Rad) + 0.001f;
        var closestDistance = float.MaxValue;

        foreach (var candidate in Physics.OverlapSphere(origin, wallCheckDistance + wallCheckSphereRadius, wallLayers, QueryTriggerInteraction.Ignore))
        {
            if (candidate == null || candidate == character || candidate.transform.IsChildOf(transform))
                continue;
            // Wall-running is opt-in: only surfaces carrying WallRunSurface
            // qualify — random crates/parapets/pillars can't start a run.
            if (candidate.GetComponentInParent<WallRunSurface>() == null)
                continue;
            if (candidate is MeshCollider meshCollider && !meshCollider.convex)
                continue;
            if (candidate == blockedWall && Time.time < blockedUntil)
                continue;

            var point = candidate.ClosestPoint(origin);
            var fromWall = origin - point;
            var distance = fromWall.magnitude;
            if (distance <= 0.001f || distance >= closestDistance)
                continue;

            var normal = fromWall / distance;
            if (Mathf.Abs(normal.y) > slopeSin)
                continue;

            // Upper-body check: the same wall must also reach hand height. If its top is
            // below the hand probe, the closest point is the top edge and fromHand points
            // upward — that means only the legs would touch, so reject it.
            var handPoint = candidate.ClosestPoint(handOrigin);
            var fromHand = handOrigin - handPoint;
            var handDistance = fromHand.magnitude;
            if (handDistance > wallCheckDistance + wallCheckSphereRadius)
                continue;
            if (handDistance > 0.001f && fromHand.y / handDistance > slopeSin)
                continue;

            closestDistance = distance;
            wallNormal = normal;
            wallPoint = point;
            wallCollider = candidate;
        }

        if (closestDistance == float.MaxValue)
            return false;

        var towardWall = wallPoint - origin;
        var lateral = Vector3.Dot(towardWall.normalized, transform.right);
        if (Mathf.Abs(lateral) > 0.35f)
        {
            side = lateral < 0f ? WallSide.Left : WallSide.Right;
        }
        else
        {
            var leftTangent = Vector3.Cross(wallNormal, Vector3.up).normalized;
            side = Vector3.Dot(leftTangent, velocity) >= 0f ? WallSide.Left : WallSide.Right;
        }

        return true;
    }
}
