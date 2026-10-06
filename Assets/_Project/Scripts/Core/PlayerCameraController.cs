using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Third-person action camera controller.
/// - Mouse / right-stick orbit around the player.
/// - FOV scaling with speed and displacement (sprint/slide).
/// - Small landing kick when the jump land state starts.
/// </summary>
[DefaultExecutionOrder(10)]
public sealed class PlayerCameraController : MonoBehaviour
{
    [Header("Cinemachine")]
    [Tooltip("The active CinemachineCamera to drive (auto-detected if null).")]
    [SerializeField] private CinemachineCamera cinemachineCamera;
    [Tooltip("Optional CinemachineFollow to move (auto-detected if null).")]
    [SerializeField] private CinemachineFollow cinemachineFollow;
    [Tooltip("Optional CinemachineHardLookAt to aim (auto-detected if null).")]
    [SerializeField] private CinemachineHardLookAt cinemachineLookAt;
    [Tooltip("Target to orbit around (defaults to this object).")]
    [SerializeField] private Transform target;

    [Header("Input")]
    [Tooltip("Input action asset containing a 'Look' Vector2 action.")]
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField] private string lookActionName = "Look";
    [SerializeField, Range(0.01f, 2f)] private float mouseSensitivity = 0.2f;
    [SerializeField, Range(10f, 500f)] private float gamepadSensitivity = 120f;
    [SerializeField] private bool invertLookY;

    [Header("Orbit")]
    [SerializeField, Range(1f, 20f)] private float distance = 6f;
    [SerializeField, Range(-90f, 0f)] private float pitchMin = -45f;
    [SerializeField, Range(0f, 90f)] private float pitchMax = 70f;

    [Header("Motion")]
    [SerializeField, Range(30f, 120f)] private float baseFOV = 55f;
    [SerializeField, Range(30f, 120f)] private float sprintFOV = 62f;
    [SerializeField, Range(30f, 120f)] private float slideFOV = 70f;
    [SerializeField, Range(0.1f, 15f)] private float fovSmooth = 5f;
    [Tooltip("Stable melee framing, independent of attack root motion and sprint speed.")]
    [SerializeField, Range(30f, 120f)] private float combatFOV = 55f;
    [Tooltip("Orbit radius during a fight or attack anticipation. Exploration retains the scene's follow distance.")]
    [SerializeField, Range(2f, 10f)] private float combatDistance = 4.8f;
    [SerializeField] private bool scaleFOVWithSpeed = true;
    [SerializeField, Range(0f, 1.5f)] private float landingKick = 0.25f;
    [SerializeField, Range(0.1f, 20f)] private float landingKickDecay = 8f;

    [Header("Jump camera kick")]
    [Tooltip("Camera-relative Dutch/roll used on jumps: left movement tilts left, right movement tilts right.")]
    [SerializeField, Range(-30f, 30f)] private float jumpTiltAmount = 4f;
    [Tooltip("Negative dips the camera forward; positive pulls it back.")]
    [SerializeField, Range(-20f, 20f)] private float jumpPitchAmount = -5f;
    [SerializeField, Range(0f, 1f)] private float jumpKickInDuration = 0.1f;
    [SerializeField, Range(0f, 1f)] private float jumpKickOutDuration = 0.35f;

    [Header("Lock-on")]
    [Tooltip("Seconds of smoothing when the camera yaw tracks a locked target. Lower = snappier.")]
    [SerializeField, Min(0.01f)] private float lockOnYawSmooth = 0.12f;
    [Tooltip("Seconds of smoothing when the camera pitch tracks a locked target's elevation.")]
    [SerializeField, Min(0.01f)] private float lockOnPitchSmooth = 0.18f;
    [Tooltip("Baseline camera pitch while locked (positive = camera stays above, mild top-down combat framing).")]
    [SerializeField, Range(-30f, 60f)] private float lockOnBasePitch = 18f;
    [Tooltip("How strongly the camera tilts toward the target's elevation on top of the baseline. 1 = fully tracks height.")]
    [SerializeField, Range(0f, 1f)] private float lockOnPitchFollow = 0.7f;
    [Tooltip("Height above the player pivot used as the camera's eye reference for lock-on pitch.")]
    [SerializeField, Min(0f)] private float lockOnChestHeight = 1.3f;
    [Tooltip("Lateral camera shift while locked — frames the player slightly off-center, souls-style.")]
    [SerializeField, Range(-1.5f, 1.5f)] private float lockOnShoulderOffset = 0.35f;
    [Tooltip("Distance scale at point-blank vs far target range — the camera pulls back as the enemy gets farther so both stay framed.")]
    [SerializeField, Range(0.5f, 1.5f)] private float lockOnNearDistanceScale = 0.9f;
    [SerializeField, Range(0.5f, 1.5f)] private float lockOnFarDistanceScale = 1.2f;
    [SerializeField, Min(1f)] private float lockOnScaleDistance = 8f;
    [SerializeField, Min(0.01f)] private float lockOnDistanceSmooth = 0.25f;

    [Header("Collision")]
    [Tooltip("Radius of the sphere used for camera obstacle checks.")]
    [SerializeField, Min(0.01f)] private float cameraRadius = 0.2f;
    [Tooltip("Extra distance to keep between the camera and a surface it nearly touches.")]
    [SerializeField, Min(0f)] private float cameraClipPadding = 0.05f;
    [Tooltip("Layers to treat as camera obstacles. The player CharacterController is ignored automatically.")]
    [SerializeField] private LayerMask cameraObstacleLayers = ~0;
    [Tooltip("How fast the camera eases back out after an obstruction clears — pull-in stays instant so it never clips.")]
    [SerializeField, Range(1f, 20f)] private float collisionReleaseSpeed = 4f;

    private PlayerLocomotion locomotion;
    private PlayerState state;
    private LockOnController lockOn;
    private AttackController attack;
    private SlideController slide;
    private WallRunController wall;
    private WeaponSocket weapon;
    private Animator animator;
    private CharacterController playerCharacter;
    private CameraTiltController tiltController;
    private InputAction lookAction;
    private InputActionAsset ownedActions;

    private float yaw;
    private float pitch;
    private float lockOnYawVel;
    private float lockOnPitchVel;
    private float targetFOV;
    private float currentKick;
    private float lastFallSpeed; // fastest fall speed this air time — scales the land kick
    private bool wasLanding;
    private bool wasJumping;
    private float shakeAmp;
    private float shakeSeed;
    private float smoothedDistance;
    private float distanceVel;
    private float pullFrac;
    private float airFocusRise; // launch-session framing lift (detail §212)

    // Big-boss framing (Genshin-style giant fights): the orbit pulls back, lifts a
    // little, and the look target slides from the player's chest toward the boss's
    // upper body — weighted by how much the camera already faces the boss, so the
    // player keeps full orbit control and the frame never yanks away.
    private Transform bossFocus;
    private float bossFocusHeight = 3f, bossFocusDistance = 1.6f, bossFocusBlend;
    private Transform focusProxy, originalLookAt;

    /// <summary>Adds a decaying positional camera shake — impact feedback for
    /// landed hits. Larger strength = harder hit (combo finishers shake most).</summary>
    public void Shake(float strength)
    {
        shakeAmp = Mathf.Max(shakeAmp, strength);
        shakeSeed = Random.value * 100f;
    }

    /// <summary>Frame a giant: <paramref name="height"/> = the boss's aim height above
    /// its pivot, <paramref name="distanceScale"/> = orbit pull-back while engaged.</summary>
    public void SetBossFocus(Transform boss, float height, float distanceScale)
    {
        bossFocus = boss;
        bossFocusHeight = Mathf.Max(1f, height);
        bossFocusDistance = Mathf.Max(1f, distanceScale);
    }

    public void ClearBossFocus() => bossFocus = null;

    // Giant-attack framing: a slow, small widen + pull-back + lift that eases in,
    // holds, eases out. Player orbit/pitch stay theirs — the frame breathes, it never swings.
    private float frameFov, framePull, frameLift, frameStart = -99f, frameIn = 0.35f, frameHold, frameOut = 0.6f;

    /// <summary>Widen the frame for a huge attack: +<paramref name="fovAdd"/>° FOV, orbit ×(1 +
    /// <paramref name="pullBack"/>), camera +<paramref name="lift"/> m, for <paramref name="seconds"/>
    /// (real time, including the ease in/out). A stronger call overrides a weaker running one.</summary>
    public void Frame(float fovAdd, float pullBack, float lift, float seconds, float easeIn = 0.35f, float easeOut = 0.6f)
    {
        var running = FrameWeight() > 0.05f;
        if (running && fovAdd < frameFov && pullBack < framePull) return;
        frameFov = Mathf.Clamp(fovAdd, 0f, 14f);
        framePull = Mathf.Clamp(pullBack, 0f, 0.6f);
        frameLift = Mathf.Clamp(lift, 0f, 2f);
        frameIn = Mathf.Max(0.05f, easeIn);
        frameOut = Mathf.Max(0.05f, easeOut);
        frameHold = Mathf.Max(0f, seconds - frameIn - frameOut);
        frameStart = Time.unscaledTime - (running ? frameIn : 0f);
    }

    private float FrameWeight()
    {
        var t = Time.unscaledTime - frameStart;
        if (t < 0f) return 0f;
        if (t < frameIn) return Mathf.SmoothStep(0f, 1f, t / frameIn);
        t -= frameIn;
        if (t < frameHold) return 1f;
        t -= frameHold;
        return t < frameOut ? Mathf.SmoothStep(1f, 0f, t / frameOut) : 0f;
    }

    private void TickBossFocus()
    {
        var want = bossFocus != null && bossFocus.gameObject.activeInHierarchy ? 1f : 0f;
        bossFocusBlend = Mathf.MoveTowards(bossFocusBlend, want, Time.unscaledDeltaTime * (want > 0f ? 0.8f : 1.2f));
        if (cinemachineCamera == null || target == null) return;
        if (bossFocusBlend <= 0.001f)
        {
            if (focusProxy != null && originalLookAt != null && cinemachineCamera.LookAt == focusProxy)
                cinemachineCamera.LookAt = originalLookAt;
            return;
        }
        if (focusProxy == null)
        {
            focusProxy = new GameObject("CameraBossFocus").transform;
            focusProxy.hideFlags = HideFlags.DontSave;
        }
        if (cinemachineCamera.LookAt != focusProxy)
        {
            originalLookAt = cinemachineCamera.LookAt != null ? cinemachineCamera.LookAt : target;
            cinemachineCamera.LookAt = focusProxy;
        }
        var basePoint = originalLookAt != null ? originalLookAt.position : target.position;
        var k = 0f;
        var bossPoint = basePoint;
        if (bossFocus != null)
        {
            bossPoint = bossFocus.position + Vector3.up * bossFocusHeight * 0.6f;
            var camFwd = Camera.main != null ? Camera.main.transform.forward : target.forward;
            var toBoss = Vector3.ProjectOnPlane(bossPoint - basePoint, Vector3.up);
            var facing = toBoss.sqrMagnitude > 0.01f ? Vector3.Dot(Vector3.ProjectOnPlane(camFwd, Vector3.up).normalized, toBoss.normalized) : 0f;
            k = 0.4f * Mathf.Clamp01(facing + 0.35f);
        }
        var shift = (bossPoint - basePoint) * k * bossFocusBlend;
        shift.y = Mathf.Clamp(shift.y, -1f, 2.6f);
        var planar = new Vector3(shift.x, 0f, shift.z);
        if (planar.magnitude > 3.5f) planar = planar.normalized * 3.5f;
        focusProxy.position = basePoint + planar + Vector3.up * shift.y;
    }

    private void Awake()
    {
        locomotion = GetComponent<PlayerLocomotion>();
        state = GetComponent<PlayerState>();
        lockOn = GetComponent<LockOnController>();
        attack = GetComponent<AttackController>();
        slide = GetComponent<SlideController>();
        wall = GetComponent<WallRunController>();
        weapon = GetComponent<WeaponSocket>();
        animator = FindValidAnimator();
        tiltController = GetComponent<CameraTiltController>();
        if (target == null) target = transform;
        if (target != null)
            playerCharacter = target.GetComponent<CharacterController>();
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
        if (cinemachineCamera == null)
        {
            var brain = FindFirstObjectByType<CinemachineBrain>();
            if (brain != null && brain.ActiveVirtualCamera is CinemachineCamera vcam)
                cinemachineCamera = vcam;
        }

        if (cinemachineCamera == null)
            cinemachineCamera = FindFirstObjectByType<CinemachineCamera>();

        if (playerCharacter == null)
            playerCharacter = GetComponent<CharacterController>();

        if (cinemachineCamera != null)
        {
            if (cinemachineFollow == null)
                cinemachineFollow = cinemachineCamera.GetComponent<CinemachineFollow>();
            if (cinemachineLookAt == null)
                cinemachineLookAt = cinemachineCamera.GetComponent<CinemachineHardLookAt>();
        }

        if (inputActions != null)
        {
            ownedActions = Instantiate(inputActions);
            ownedActions.Disable();
            lookAction = ownedActions.FindAction(lookActionName);
            if (lookAction != null)
                lookAction.Enable();
        }

        if (cinemachineFollow != null)
        {
            Vector3 off = cinemachineFollow.FollowOffset;
            distance = off.magnitude;
            yaw = Mathf.Atan2(off.x, -off.z) * Mathf.Rad2Deg;
            pitch = Mathf.Asin(off.y / distance) * Mathf.Rad2Deg;
        }

        targetFOV = baseFOV;
        smoothedDistance = distance;
    }

    private void LateUpdate()
    {
        if (Time.timeScale <= 0f)
            return;

        ReadOrbitInput();
        ApplyLockOnYaw();
        UpdateFOV();
        UpdateLandingKick();
        TickBossFocus();
        ApplyCameraState();
    }



    private void ReadOrbitInput()
    {
        if (lookAction == null)
            return;

        Vector2 look = lookAction.ReadValue<Vector2>();
        if (UiGates.MenuOpen) look = Vector2.zero; // the cursor is driving a menu, not the camera
        bool isGamepad = lookAction.activeControl != null && lookAction.activeControl.device is Gamepad;
        float sensitivity = isGamepad ? gamepadSensitivity : mouseSensitivity;
        float scale = isGamepad ? Time.deltaTime : 1f;
        float yMul = invertLookY ? 1f : -1f;

        // While locked the orbit is driven by the target bearing/elevation — look input
        // is a target-switch flick consumed by LockOnController, so it is not applied here.
        var locked = lockOn != null && lockOn.IsLockedOn;
        if (!locked)
        {
            yaw += look.x * sensitivity * scale;
            pitch += look.y * sensitivity * yMul * scale;
            pitch = Mathf.Clamp(pitch, pitchMin, pitchMax);
        }

        if (yaw > 180f) yaw -= 360f;
        else if (yaw < -180f) yaw += 360f;
    }

    /// <summary>
    /// While locked the camera yaw tracks the bearing from the player to the target,
    /// so the enemy stays framed. Yaw input is ignored (flicks switch targets);
    /// pitch remains fully manual.
    /// </summary>
    private void ApplyLockOnYaw()
    {
        if (lockOn == null || !lockOn.IsLockedOn || target == null)
        {
            lockOnYawVel = 0f;
            return;
        }

        // Measure from the player's chest, not the pivot — keeps the camera level for
        // same-height targets and only tilts when the aim point is truly above/below.
        var from = target.position + Vector3.up * lockOnChestHeight;
        var to = lockOn.AimPosition - from;
        var planar = new Vector3(to.x, 0f, to.z);
        if (planar.sqrMagnitude >= 0.01f)
        {
            var desiredYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            yaw = Mathf.SmoothDampAngle(yaw, desiredYaw, ref lockOnYawVel, lockOnYawSmooth);
        }

        // Baseline downward pitch for combat framing (pitch drives camera height —
        // pitch 0 puts the camera at the feet pivot, i.e. ground level), then tilt
        // up/down by the target's elevation: same-level targets stay framed normally,
        // elevated targets tilt the camera up.
        var elevation = Mathf.Atan2(to.y, Mathf.Max(0.1f, planar.magnitude)) * Mathf.Rad2Deg;
        var desiredPitch = Mathf.Clamp(lockOnBasePitch - elevation * lockOnPitchFollow, pitchMin, pitchMax);
        pitch = Mathf.SmoothDampAngle(pitch, desiredPitch, ref lockOnPitchVel, lockOnPitchSmooth);
    }

    private void UpdateFOV()
    {
        float desired = CombatFraming ? combatFOV : baseFOV;

        if (TraversalFraming)
        {
            desired = slideFOV;
        }
        else if (attack != null && (attack.IsAttacking))
        {
            desired = combatFOV;
        }
        else if (scaleFOVWithSpeed && locomotion != null)
        {
            float walk = locomotion.WalkSpeed;
            float run = locomotion.RunSpeed;
            if (locomotion.ActualPlanarSpeed > walk * 0.75f)
            {
                float t = Mathf.InverseLerp(walk, run, locomotion.ActualPlanarSpeed);
                desired = Mathf.Lerp(desired, sprintFOV, t);
            }
        }

        targetFOV = Mathf.Lerp(targetFOV, desired, fovSmooth * Time.deltaTime);
    }

    private bool TraversalFraming => (slide != null && slide.IsSliding) || (wall != null && wall.IsWallRunning);
    private bool CombatFraming => (lockOn != null && lockOn.IsLockedOn)
        || (attack != null && (attack.IsAttacking))
        || (weapon != null && weapon.ResourceCombat);

    private void UpdateLandingKick()
    {
        bool landing = false;
        bool inAir = false;
        bool jumping = false;
        bool doubleJumping = false;

        if (animator != null)
        {
            var s = animator.GetCurrentAnimatorStateInfo(0);
            inAir = s.IsName("Base Layer.JumpAir") || s.IsName("Base Layer.JumpForwardAir") || s.IsName("Base Layer.JumpDoubleAir") ||
                    s.IsName("Base Layer.JumpDoubleFall") || s.IsName("Base Layer.JumpStart") || s.IsName("Base Layer.JumpForwardStart");
            landing = s.IsName("Base Layer.JumpLand") || s.IsName("Base Layer.JumpForwardLand");
            doubleJumping = s.IsName("Base Layer.JumpDoubleAir");
            jumping = s.IsName("Base Layer.JumpStart") || s.IsName("Base Layer.JumpForwardStart") || doubleJumping;
        }

        // Track the fastest fall so touchdown weight scales with the drop —
        // a hop barely dips, a ledge drop punches the camera down.
        if (inAir && locomotion != null)
            lastFallSpeed = Mathf.Min(lastFallSpeed, locomotion.VerticalSpeed);
        if (landing && !wasLanding && !inAir)
        {
            currentKick = landingKick * Mathf.Clamp(-lastFallSpeed / 14f, 0.5f, 2f);
            lastFallSpeed = 0f;
        }

        if (jumping && !wasJumping && tiltController != null)
        {
            var jumpDirection = locomotion != null ? locomotion.ActualPlanarVelocity : Vector3.zero;
            if (jumpDirection.sqrMagnitude < 0.001f)
                jumpDirection = transform.forward;
            var cameraForward = Camera.main != null ? Camera.main.transform.forward : transform.forward;
            var jumpTilt = SlideController.DirectionalTilt(jumpDirection, cameraForward, jumpTiltAmount);
            tiltController.BeginTilt(new CameraDisplacementEvent(
                magnitude: 0.6f,
                tiltAmount: jumpTilt,
                tiltInDuration: jumpKickInDuration,
                tiltOutDuration: jumpKickOutDuration,
                pitchAmount: jumpPitchAmount,
                pitchInDuration: jumpKickInDuration,
                pitchOutDuration: jumpKickOutDuration));
        }
        else if (!jumping && wasJumping && tiltController != null)
        {
            tiltController.EndTilt();
        }

        currentKick = Mathf.Lerp(currentKick, 0f, landingKickDecay * Time.deltaTime);
        wasLanding = landing;
        wasJumping = jumping;
    }

    private void ApplyCameraState()
    {
        if (cinemachineFollow != null)
        {
            // Lock-on framing: pull back as the target gets farther so player
            // and enemy stay in frame together — smoothed so it doesn't pump
            // while strafing.
            var locked = lockOn != null && lockOn.IsLockedOn;
            var orbitDistance = CombatFraming && !TraversalFraming ? combatDistance : distance;
            var d = orbitDistance;
            if (locked)
            {
                var planar = lockOn.AimPosition - target.position;
                planar.y = 0f;
                d = orbitDistance * Mathf.Lerp(lockOnNearDistanceScale, lockOnFarDistanceScale,
                        Mathf.Clamp01(planar.magnitude / lockOnScaleDistance));
            }
            d *= Mathf.Lerp(1f, bossFocusDistance, bossFocusBlend);
            var framing = FrameWeight();
            d *= 1f + framePull * framing;
            smoothedDistance = Mathf.SmoothDamp(smoothedDistance, d, ref distanceVel, lockOnDistanceSmooth);
            d = smoothedDistance;

            // A giant reads from slightly above the shoulder, not at knee height.
            float displacementPitch = (tiltController != null ? tiltController.CurrentPitch : 0f) + 6f * bossFocusBlend;
            float effectivePitch = Mathf.Clamp(pitch + displacementPitch, pitchMin - 10f, pitchMax);
            float radX = effectivePitch * Mathf.Deg2Rad;
            float radY = yaw * Mathf.Deg2Rad;

            float cosX = Mathf.Cos(radX);
            float sinX = Mathf.Sin(radX);
            float cosY = Mathf.Cos(radY);
            float sinY = Mathf.Sin(radY);

            Vector3 offset = new Vector3(
                -d * sinY * cosX,
                d * sinX - currentKick,
                -d * cosY * cosX);

            // Aerial-session framing (detail §212): lift the follow offset
            // toward the hero/primary-victim midpoint — +1.5m max, in over
            // 0.15s and out over 0.25s. Player control, pitch and distance all
            // stay the player's; no forced orbit or pan.
            var focusTarget = 0f;
            if (attack != null && attack.InAirSession && attack.SessionPrimary != null && target != null)
            {
                var mid = (attack.SessionPrimary.transform.position.y - target.position.y) * 0.5f;
                focusTarget = Mathf.Clamp(mid, 0f, 1.5f);
            }
            airFocusRise = Mathf.MoveTowards(airFocusRise, focusTarget,
                Time.unscaledDeltaTime * (focusTarget > airFocusRise ? 1.5f / 0.15f : 1.5f / 0.25f));
            offset.y += airFocusRise + frameLift * framing;

            // Souls shoulder framing: shift the camera right while locked so
            // the player sits slightly left-of-centre with the target clear.
            if (locked && lockOnShoulderOffset != 0f)
                offset += new Vector3(cosY, 0f, -sinY) * lockOnShoulderOffset;

            ResolveCollision(ref offset);

            // Impact shake: fast perlin jitter decaying back to zero — applied
            // after collision so connects still read through walls.
            if (shakeAmp > 0.001f)
            {
                var t = Time.unscaledTime * 45f;
                offset += new Vector3(
                    (Mathf.PerlinNoise(shakeSeed, t) - 0.5f) * 2f,
                    (Mathf.PerlinNoise(shakeSeed + 31.7f, t) - 0.5f) * 2f,
                    (Mathf.PerlinNoise(shakeSeed + 74.3f, t) - 0.5f) * 2f) * shakeAmp;
                // Real-time decay: the hit freeze drops timeScale to ~0.02 and
                // the shake must keep ringing through it.
                shakeAmp = Mathf.Lerp(shakeAmp, 0f, 10f * Time.unscaledDeltaTime);
            }
            else shakeAmp = 0f;

            cinemachineFollow.FollowOffset = offset;
        }

        if (cinemachineCamera != null)
        {
            var lens = cinemachineCamera.Lens;
            lens.FieldOfView = targetFOV + frameFov * FrameWeight();
            cinemachineCamera.Lens = lens;
        }
    }

    private void ResolveCollision(ref Vector3 offset)
    {
        if (target == null || playerCharacter == null)
            return;

        Vector3 origin = target.position + playerCharacter.center;
        Vector3 desiredPos = target.position + offset;
        Vector3 toDesired = desiredPos - origin;
        float desiredDistance = toDesired.magnitude;
        if (desiredDistance < playerCharacter.radius + 0.001f)
            return;

        Vector3 direction = toDesired / desiredDistance;
        int layerMask = cameraObstacleLayers.value == 0 ? ~0 : cameraObstacleLayers.value;

        // Start the cast just outside the player capsule so the controller itself is not the first hit.
        Vector3 castOrigin = origin + direction * playerCharacter.radius;
        float castDistance = desiredDistance - playerCharacter.radius;

        // Ignore triggers — tutorial zones/kill volumes/fog gates are gameplay
        // volumes, not geometry. They'd otherwise collapse the camera indoors.
        RaycastHit[] hits = Physics.SphereCastAll(castOrigin, cameraRadius, direction, castDistance,
                                                  layerMask, QueryTriggerInteraction.Ignore);
        float minDistance = castDistance;

        var playerRoot = playerCharacter.transform.root;
        foreach (var hit in hits)
        {
            if (hit.collider == null || hit.collider == playerCharacter)
                continue;
            // Anything under the player root (socketed weapon, gear colliders)
            // is the player, not a wall — a stray sword collider in the cast
            // corridor would otherwise pin the camera at point-blank forever.
            if (hit.collider.transform.root == playerRoot)
                continue;
            // Characters aren't walls — the camera goes through them.
            if (hit.collider.GetComponentInParent<Health>() != null)
                continue;
            // Anything targetable (boss parts, breakables) rides its owner's
            // colliders — never an obstacle either.
            if (hit.collider.GetComponentInParent<Targetable>() != null)
                continue;
            // The fog-gate blocker is an invisible seal — it stops the PLAYER,
            // not the camera, or fights read as the lens hitting thin air.
            if (hit.collider.GetComponentInParent<FogGate>() != null)
                continue;

            if (hit.distance < minDistance)
                minDistance = hit.distance;
        }

        // Obstructed fraction: 0 = clear line, →1 = fully blocked. Pull-in is
        // instant (never clip through the wall); release eases back out so the
        // camera doesn't snap when the obstruction clears.
        var frac = minDistance < castDistance
            ? 1f - Mathf.Clamp01((minDistance - cameraRadius - cameraClipPadding) / castDistance)
            : 0f;
        pullFrac = frac > pullFrac ? frac : Mathf.Lerp(pullFrac, frac, collisionReleaseSpeed * Time.deltaTime);
        if (pullFrac > 0.0001f)
        {
            var safeDistance = playerCharacter.radius + castDistance * (1f - pullFrac);
            offset = origin + direction * safeDistance - target.position;
        }
    }

    private void OnDisable()
    {
        if (lookAction != null)
            lookAction.Disable();
        if (ownedActions != null)
            ownedActions.Disable();
    }

    private void OnDestroy()
    {
        if (ownedActions != null)
            Destroy(ownedActions);
    }
}
