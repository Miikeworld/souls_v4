using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

/// <summary>Immediate attacks and one expiring recovery request for combo, art or dodge.
/// Costs commit only after destination validation; each contact window deduplicates health owners.</summary>
[RequireComponent(typeof(CharacterController), typeof(PlayerState))]
public sealed class AttackController : MonoBehaviour, IRootMotionOwner
{
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField, Min(0.01f)] private float fadeOut = 0.15f;
    [Tooltip("Swing reach/arc when no WeaponSet is equipped.")]
    [SerializeField, Min(0.5f)] private float attackRange = 2.6f;
    [Tooltip("Unarmed (kick) swings are shorter and weaker.")]
    [SerializeField, Min(0.5f)] private float unarmedRange = 1.9f;
    [SerializeField, Range(30f, 360f)] private float unarmedArc = 120f;
    [SerializeField] private float gravity = -8f;
    [Tooltip("Continuous sprint seconds before an attack press becomes the sprint art (Mooncleaver Rush). SprintTime zeroes on release, air and displacement — an attack mid-sprint re-arms the clock.")]
    [SerializeField, Min(0f)] private float sprintArtMinSprint = 0.6f;

    [Header("Crowd aim assist")]
    [Tooltip("Movement-led swings bend toward the nearest enemy along the stick; neutral + unlocked faces the nearest enemy in front.")]
    [SerializeField] private bool crowdAimAssist = true;
    [SerializeField, Min(1f)] private float assistRange = 4.5f;
    [Tooltip("Full cone (degrees) around the stick direction that magnetism may bend toward.")]
    [SerializeField, Range(0f, 90f)] private float assistCone = 60f;
    [Header("Backstab")]
    [Tooltip("Standing in a victim's rear hemisphere within this range turns an armed attack press into a backstab.")]
    [SerializeField, Min(0.5f)] private float backstabRange = 1.8f;
    [SerializeField, Min(1f)] private float backstabMultiplier = 3f;

    public bool IsAttacking { get; private set; }
    /// <summary>Technique branch context at press time: 0 = not in a normal
    /// swing, N = the Nth committed normal of the combo. Queued-but-uncommitted
    /// clicks never inflate the branch (detail §36, §96).</summary>
    public int ComboBranch => !IsAttacking || actArt != null ? 0 : comboIndex + 1;
    public enum PlungePhase { None, Start, Fall, Land }
    public PlungePhase Phase { get; private set; }
    public int ActionRevision { get; private set; }
    /// <summary>Confirmed blade contacts, for tutorial goals; never raw input presses.</summary>
    public event System.Action<Health, DamageKind, bool> ContactLanded;
    /// <summary>Verified floor contact remains valid through the one-frame CC grounding flicker.</summary>
    public bool GroundedForAction => character != null && (character.isGrounded ||
        (Phase == PlungePhase.Land && plungeContactHeld));
    public bool UsesLandingGrounding => Phase == PlungePhase.Land && plungeContactHeld;
    private enum RequestKind { None, Light, Art, Dodge, Ultimate }
    private sealed class Request
    {
        public RequestKind kind;
        public WeaponArt art;
        public System.Func<bool> held;
        public Vector2 dodge;
        /// <summary>Modifier state sampled at press time (detail §75).</summary>
        public bool mod;
    }
    /// One pending request (detail §98): latest valid press replaces it and it
    /// expires 0.25 gameplay seconds after the press — its age stops while our
    /// own hitstop/burst owns the clock, and menus/death clear it.
    private Request pending;
    private float pendingUntil;
    private readonly System.Collections.Generic.Dictionary<WeaponArt, float> cooldowns = new();
    private readonly System.Collections.Generic.List<GameObject> attachedEffects = new();
    private WeaponSet actionWeapon;
    private int feedbackFrame = -1;
    public WeaponArt ActiveArt => actArt;
    /// <summary>The WeaponSet the running action started with (arts keep the
    /// equipped set — the summoned blade is visual only).</summary>
    public WeaponSet ActionWeapon => actionWeapon;
    /// <summary>Normalized hit window of a plain swing — arts carry their own
    /// normalized window table; this maps hitStart/hitEnd seconds onto duration.</summary>
    public Vector2 SwingWindow => actDuration > 0.01f
        ? new Vector2(actHitStart / actDuration, actHitEnd / actDuration)
        : new Vector2(-1f, -1f);
    /// <summary>Damage-number tint for the running action — the big sword
    /// (equipped or summoned by a skill) reads crimson, fists read dull,
    /// katana keeps the bone default.</summary>
    public Color? NumeralTint
    {
        get
        {
            var big = actArt != null && actArt.BigSwordSkill
                      || actionWeapon != null && (actionWeapon == rageWeaponSet || actionWeapon == artSwordSet);
            if (big) return new Color(0.94f, 0.28f, 0.3f);
            return actionWeapon != null ? (Color?)null : new Color(0.7f, 0.66f, 0.6f);
        }
    }
    public float NormalizedAge => actDuration > 0f ? attackAge / actDuration : 0f;
    public int ContactWindow => CurrentWindow();
    private float RecoveryAt => Mathf.Max(actHitEnd,
        actArt != null && actArt.projectile != null && (actArt.projectile.wave || actArt.projectile.prefab != null) ? actArt.projectile.spawnTime * actDuration : 0f)
        + (actArt != null ? actArt.recoveryTransitionDelay : 0.06f);
    /// <summary>True once the hit window has passed — recovery is dodge-cancellable.</summary>
    public bool InRecovery => IsAttacking && !channeling &&
        (actDive ? Phase == PlungePhase.Land && plungeContactHeld && plungeAge >= 0.16f : attackAge >= RecoveryAt);
    /// <summary>True only while a hit window is open — damage can connect.</summary>
    public bool InHitWindow => IsAttacking && !actDive && CurrentWindow() >= 0;
    /// <summary>True while the blade is in flight — the hit window plus a tight
    /// margin where the swing is actually moving fast. Gates the smear trail so
    /// windup and recovery stay clean.</summary>
    public bool Swinging => IsAttacking && !actDive && attackAge >= actHitStart - 0.08f && attackAge <= actHitEnd + 0.05f;
    /// <summary>True while the active swing wants clip root motion — the
    /// RootMotionRelay moves the capsule and the scripted step/advance skip.</summary>
    public bool DriveRootMotion => IsAttacking && actRootMotion;
    /// <summary>True when clip root-Y reaches the capsule (else relay gravity owns it).</summary>
    public bool AllowRootY => actRootMotionY;

    // Field names changed on purpose (3rd rename — detail.md §208 lightened the
    // table: quick .04 / final .10 / plunge .06; the scene still carried .09+).
    [Header("Hit feedback")]
    [Tooltip("Camera shake on connect — scales up through the combo.")]
    [SerializeField, Min(0f)] private float hitShakeBase = 0.2f;
    [SerializeField, Min(0f)] private float hitShakePerCombo = 0.1f;
    [Tooltip("Freeze-frame on a quick contact (seconds, real time).")]
    [SerializeField, Min(0f)] private float freezeQuick = 0.04f;
    [Tooltip("Freeze growth per combo step toward the finisher value.")]
    [SerializeField, Min(0f)] private float freezeStep = 0.02f;
    [Tooltip("Freeze-frame cap — the finisher/heavy value.")]
    [SerializeField, Min(0f)] private float freezeFinal = 0.10f;
    [SerializeField, Min(0f)] private float freezeCrit = 0.10f;
    [SerializeField, Range(0.005f, 1f)] private float freezeTimeScale = 0.02f;
    [Header("Swing tempo")]

    [Header("VFX")]
    [Tooltip("Trailing wind-streak burst fired when the slide cancels into the dash lunge.")]
    [SerializeField] private GameObject dashStreakFx;

    [Header("Plunge phases")]
    [SerializeField] private AnimationClip plungeStartClip;
    [SerializeField] private AnimationClip plungeFallClip;
    [SerializeField] private AnimationClip plungeLandClip;
    [SerializeField, Min(0.1f)] private float plungePreparationSeconds = 0.35f;
    [SerializeField, Min(0.01f)] private float plungeBrakeSeconds = 0.08f;
    [SerializeField, Min(0.01f)] private float plungeAccelerationSeconds = 0.18f;
    [SerializeField] private float plungeTerminalVelocity = -18f;
    [Tooltip("Camera-relative drift only; never pulls toward a target.")]
    [SerializeField, Range(0f, 1f)] private float plungeDriftSpeed = 1f;
    [Tooltip("Landing-sweep radius — a small AoE smash, not a single-target poke.")]
    [SerializeField, Min(0.5f)] private float diveRadius = 2.6f;
    [Tooltip("Ground-burst prefab spawned at the touchdown point (PolygonParticles).")]
    [SerializeField] private GameObject diveImpactFx;
    [Tooltip("WeaponSet summoned into the hand while a weapon art plays — the big sword IS the arts' weapon.")]
    [SerializeField] private WeaponSet artSwordSet;
    [Tooltip("Equipped while rage runs after an ult — the 7-hit big-sword chain. Reverts to the pre-ult weapon on expiry.")]
    [SerializeField] private WeaponSet rageWeaponSet;
    [Tooltip("Fired on touchdown — the hookup point for impact VFX/damage passes.")]
    [SerializeField] private UnityEvent onDiveImpact;

    private CharacterController character;
    private Animator animator;
    private PlayerState state;
    private LockOnController lockOn;
    private WeaponSocket weaponSocket;
    private PlayerCameraController cameraController;
    private PlayerStamina stamina;
    private PlayerLocomotion locomotion;
    private WeaponArtCaster artCaster;
    private SlideController slide;
    private CameraTiltController tilt;
    private Transform visual;
    private float hitstopTimer;
    private bool ownsHitstop;
    // Burst cinematic (Q+RMB ult): world freezes, hero + camera run unscaled
    // until just before the first hit window opens.
    private BurstSpec burstSpec;
    private bool burstActive, burstInvuln;
    private float burstEndAge;
    // Rage mode: a finishing burst equips rageWeaponSet for burst.rageSeconds;
    // the pre-ult set is restored when the timer runs out (or on death).
    private bool raging;
    private float rageT;
    private WeaponSet preRageSet;
    // Air chase: a launched enemy this art is rising to meet before the smash.
    private EnemyAI chaseTarget;
    private float chaseLiftEnd = -1f;

    // --- Aerial session (detail.md §4): the launcher's code-owned rise and the
    // three deliberate air strikes. One owner drives capsule Y; the ZeroHeight
    // clip plays on top and the relay keeps planar travel only.
    [Header("Aerial session (detail §4)")]
    [SerializeField, Min(0.5f)] private float launchRiseSpeed = 8f;
    [SerializeField, Min(1f)] private float launchGravity = 14f;
    [SerializeField, Min(0.5f)] private float launchMaxRise = 3f;
    [SerializeField, Min(0.5f)] private float sessionMaxAge = 2.4f;
    [SerializeField, Min(0.02f)] private float strikeSuspend = 0.12f;
    [SerializeField, Min(0.1f)] private float sessionSuspendMax = 0.36f;
    [SerializeField, Min(0f)] private float airCorrectionSpeed = 2f;
    [Tooltip("Cosmetic hop apex when the launcher launches nobody (miss / resisted / kill-without-survivor).")]
    [SerializeField, Min(0.05f)] private float failedLaunchHop = 0.35f;
    private bool airSession;
    private bool airHopOnly;
    private float airVy;
    private float airFloorY;
    private float airAge;
    private int airStepIndex = -1;
    private float airSuspendT, airSuspendUsed;
    private bool airStepSuspended;
    private EnemyAI airPrimary;
    private readonly System.Collections.Generic.List<EnemyAI> airVictims = new(4);
    public bool InAirSession => airSession;
    /// <summary>Living airborne victims of the current session — Skyfall's only valid targets.</summary>
    public bool HasLivingSessionVictim => LivingSessionVictim() != null;
    /// <summary>The session's tracked victim — the camera's vertical focus target.</summary>
    public EnemyAI SessionPrimary => airPrimary;
    /// <summary>Session elapsed for HUD hints/diagnostics.</summary>
    public float SessionAge => airSession ? airAge : 0f;
    /// <summary>How many of the three aerial strikes have been spent.</summary>
    public int AirStepsUsed => airSession ? Mathf.Max(airStepIndex, 0) : 0;

    // The three deliberate aerial strikes (detail §134-138): Big Sword
    // Jump_Attack_Combo parts 1/2/3 as discrete steps — either attack button
    // advances one. Stamina costs are the doc's, not the flat swing cost.
    private static readonly AttackStep[] AirSteps =
    {
        new() { duration = 0.42f, hitStart = 0.10f, hitEnd = 0.30f, damage = 18f, staminaCost = 8f, stepDistance = 0f, advanceDistance = 0f, reaction = ReactionProfile.Normal },
        new() { duration = 0.42f, hitStart = 0.10f, hitEnd = 0.30f, damage = 20f, staminaCost = 8f, stepDistance = 0f, advanceDistance = 0f, reaction = ReactionProfile.Sweep },
        new() { duration = 0.44f, hitStart = 0.12f, hitEnd = 0.32f, damage = 26f, staminaCost = 10f, stepDistance = 0f, advanceDistance = 0f, reaction = ReactionProfile.Finisher },
    };
    private static readonly int[] AirIds =
    {
        Animator.StringToHash("Base Layer.AirStrike1"),
        Animator.StringToHash("Base Layer.AirStrike2"),
        Animator.StringToHash("Base Layer.AirStrike3"),
    };

    private InputActionAsset ownedActions;
    private InputAction attackAction;
    private InputAction moveAction;
    /// <summary>The owned LMB/RT action — scripted boss beats read the press directly.</summary>
    public InputAction AttackAction => attackAction;

    private int comboIndex = -1;
    private float attackAge;
    private float actDuration, actHitStart, actHitEnd, actDamage, actRange, actArc, actAdvance, actPoise;
    private WeaponArt actArt;
    private int cueMask, sfxMask;
    private bool artProjFired;
    private System.Func<bool> channelHeld;
    private bool channeling;
    private float channelT, nextChannelHit, channelEndAt = -1f;
    private PlayerMana mana;
    private UltCharge ultMeter;
    private CrimsonInstability crimson;
    private EstusFlask flask;
    private float actFreeze, actShake;
    private bool actEndOnLand, actRootMotion, actRootMotionY;
    // Weapon arts can hit in several windows (seconds); null = the single
    // [actHitStart, actHitEnd] window combo swings use. A new window re-arms
    // targets so multi-hit arts land every hit.
    private Vector2[] actWindows;
    private int lastWindow = -1;
    private int rehitPass = -1;
    private float stepRemaining;
    private AttackStep[] activeSteps = FallbackSteps;
    private int[] activeIds = AttackIds;
    private Targetable[] candidates = System.Array.Empty<Targetable>();
    private readonly System.Collections.Generic.HashSet<Health> hitTargets = new();

    private static readonly int[] AttackIds =
    {
        Animator.StringToHash("Base Layer.Attack1"),
        Animator.StringToHash("Base Layer.Attack2"),
        Animator.StringToHash("Base Layer.Attack3"),
        Animator.StringToHash("Base Layer.Attack4"),
        Animator.StringToHash("Base Layer.Attack5"),
        Animator.StringToHash("Base Layer.Attack6"),
        Animator.StringToHash("Base Layer.Attack7"),
    };
    private static readonly int[] UnarmedIds =
    {
        Animator.StringToHash("Base Layer.UnarmedAttack1"),
        Animator.StringToHash("Base Layer.UnarmedAttack2"),
        Animator.StringToHash("Base Layer.UnarmedAttack3"),
    };
    private static readonly int CombatMoveId = Animator.StringToHash("Base Layer.CombatMove");
    private static readonly int LocomotionId = Animator.StringToHash("Base Layer.Locomotion");
    private static readonly int DashAttackId = Animator.StringToHash("Base Layer.DashAttack");
    private static readonly int JumpAirId = Animator.StringToHash("Base Layer.JumpForwardAir");
    private static readonly int BackstabId = Animator.StringToHash("Base Layer.Backstab");

    // Backstab owns no chain: a single committed stab (SoulslikeEssential
    // Backstab_Stab, state speed-fit to 1.5s by the generator), damage set
    // per-weapon at press time. No step/advance — the snap places the player.
    private static readonly AttackStep BackstabStep = new()
    {
        duration = 1.5f, hitStart = 0.6f, hitEnd = 0.8f,
        damage = 0f, stepDistance = 0f, advanceDistance = 0f,
    };
    [Tooltip("Distance behind the victim the player snaps to for the stab.")]
    [SerializeField, Min(0.3f)] private float backstabSnapDistance = 0.9f;
    private Targetable forcedVictim;

    // One-off swings outside the combo chain: dash attack lunges out of a
    // sprint, jump attack strikes while airborne. Both are armed-only — the
    // unarmed kick set has no air/sprint equivalents.
    private static readonly AttackStep DashAttackStep = new()
    {
        duration = 1.05f, hitStart = 0.34f, hitEnd = 0.54f,
        damage = 26f, stepDistance = 2.6f, advanceDistance = 2.0f,
        useRootMotion = true, // Dash_Attack_ver_B carries real authored travel
    };
    // Phase ownership replaces a dummy timed hit window. Damage is emitted
    // exactly once by the verified touchdown event, however long the fall is.
    private static readonly AttackStep DiveAttackStep = new()
    {
        duration = 0.35f, hitStart = 0f, hitEnd = 0f,
        damage = 32f, stepDistance = 0f, advanceDistance = 0f,
    };
    private static readonly int DiveStartId = Animator.StringToHash("Base Layer.DiveStart");
    private static readonly int DiveAttackId = Animator.StringToHash("Base Layer.DiveAttack");
    private static readonly int DiveLandId = Animator.StringToHash("Base Layer.DiveLand");
    private bool actDive;
    private DamageKind actDamageKind = DamageKind.Normal;
    private ReactionProfile actReaction = ReactionProfile.Normal;
    // Structured contact bookkeeping: meter credit lands once per action, and
    // a boss normal-melee hit pays 2 mana once per action (≤1 per 0.5s).
    private bool actionCredited;
    private bool actionContacted;
    private int bossManaAction = -1;
    private float lastBossManaT = -9f;
    private float plungeAge, plungeInheritedVelocity, plungeVelocity;
    private bool plungeContactHeld, plungeImpactFired, warnedPlungeSetup;
    private Vector3 plungeContactPoint, plungeContactNormal = Vector3.up;
    private FootGrounding grounding;
    [Tooltip("Developer-only: descent telemetry (phase, requested vs actual vertical speed, blocker) — off in ordinary play.")]
    [SerializeField] private bool plungeDiagnostics;
    // Enemy bodies the falling capsule currently ignores — a body can hold the
    // CC up while the floor probe (correctly) rejects it, hanging the descent.
    // Value = the pair's original ignore setting, restored on landing/cancel/
    // disable so crowd pressure resumes the moment we touch down.
    private readonly System.Collections.Generic.Dictionary<Collider, bool> plungeIgnored = new();
    private static readonly Collider[] plungeOverlap = new Collider[24];
    private float plungeDiagT, plungeStillT;
    private bool artBloomFired;
    private float artBloomAt = -1f;

    private void Awake()
    {
        character = GetComponent<CharacterController>();
        state = GetComponent<PlayerState>();
        lockOn = GetComponent<LockOnController>();
        weaponSocket = GetComponent<WeaponSocket>();
        cameraController = GetComponent<PlayerCameraController>();
        stamina = GetComponent<PlayerStamina>();
        mana = GetComponent<PlayerMana>();
        locomotion = GetComponent<PlayerLocomotion>();
        artCaster = GetComponent<WeaponArtCaster>();
        slide = GetComponent<SlideController>();
        tilt = GetComponent<CameraTiltController>();
        flask = GetComponent<EstusFlask>();
        ultMeter = GetComponent<UltCharge>();
        if (ultMeter == null) ultMeter = gameObject.AddComponent<UltCharge>();
        crimson = GetComponent<CrimsonInstability>();
        if (crimson == null) crimson = gameObject.AddComponent<CrimsonInstability>();
        animator = FindValidAnimator();
        if (animator != null) grounding = animator.GetComponent<FootGrounding>();
        if (GetComponent<BladeRibbon>() == null) gameObject.AddComponent<BladeRibbon>();
        if (animator != null) visual = animator.transform;
        // LMB/RT only (detail §62): the template asset's attack binding also
        // catches the west face button, which is the drink key — owning the
        // action outright removes that conflict. Move still rides the clone.
        attackAction = new InputAction("Attack", InputActionType.Button);
        attackAction.AddBinding("<Mouse>/leftButton");
        attackAction.AddBinding("<Gamepad>/rightTrigger");
        if (inputActions != null)
        {
            ownedActions = Instantiate(inputActions);
            moveAction = ownedActions.FindAction("Player/Move");
        }
    }

    private void OnEnable()
    {
        attackAction?.Enable();
        moveAction?.Enable();
    }

    private void OnDestroy()
    {
        attackAction?.Dispose();
        if (ownedActions != null) Destroy(ownedActions);
    }

    private void OnDisable()
    {
        ClearActionEffects();
        pending = null;
        EndAerialSession();
        attackAction?.Disable();
        moveAction?.Disable();
        ReleaseHitstop();
        if (animator != null) animator.speed = 1f;
        actArt = null;
        channeling = false;
        channelEndAt = -1f;
        ReleaseBurstOwnership();
        ReleasePlungeOwnership(false);
        if (!IsAttacking) return;
        ReleaseHitstop();
        IsAttacking = false;
        if (state != null) state.IsDisplacing = false;
    }

    private AttackStep[] Steps => weaponSocket != null && weaponSocket.Set != null && weaponSocket.Set.attacks != null && weaponSocket.Set.attacks.Length > 0
        ? weaponSocket.Set.attacks
        : FallbackSteps;

    private static readonly AttackStep[] FallbackSteps =
    {
        new AttackStep { duration = 0.7f, hitStart = 0.26f, hitEnd = 0.45f, damage = 20f, stepDistance = 1.4f },
        new AttackStep { duration = 0.75f, hitStart = 0.3f, hitEnd = 0.5f, damage = 24f, stepDistance = 1f },
        new AttackStep { duration = 0.95f, hitStart = 0.38f, hitEnd = 0.6f, damage = 35f, stepDistance = 0.8f },
    };

    // Unarmed chain (CLazy Attack_Kick_A/B/C states) — shorter, weaker, used
    // whenever the weapon is stowed so out-of-combat attacks need no sword.
    private static readonly AttackStep[] UnarmedSteps =
    {
        new AttackStep { duration = 0.8f, hitStart = 0.3f, hitEnd = 0.5f, damage = 8f, stepDistance = 0.8f },
        new AttackStep { duration = 0.8f, hitStart = 0.3f, hitEnd = 0.5f, damage = 8f, stepDistance = 0.6f },
        new AttackStep { duration = 0.95f, hitStart = 0.36f, hitEnd = 0.58f, damage = 14f, stepDistance = 0.5f },
    };

    private void Update()
    {
        // Real-time hitstop expires even with a non-pausing menu open. Only
        // restore the time scale we own, never a different pause owner's scale.
        if (hitstopTimer > 0f)
        {
            hitstopTimer -= Time.unscaledDeltaTime;
            if (hitstopTimer <= 0f) ReleaseHitstop();
        }
        // Dead: no presses, no sweep — the death sequence owns the animator.
        if (state != null && state.IsDead)
        {
            EndRage();
            ClearActionEffects();
            pending = null;
            EndAerialSession();
            if (IsAttacking) Cancel();
            return;
        }
        // The pending request's expiry pauses while our own hitstop or the
        // ultimate presentation owns the clock (detail §100).
        if (pending != null && (ownsHitstop || burstActive)) pendingUntil += Time.unscaledDeltaTime;
        TickRage();
        TickAerial(Time.deltaTime);
        ProcessAttackInput();
        if (!IsAttacking || Time.deltaTime <= 0f) return;
        if (animator == null || character == null || !character.enabled
            || actionWeapon != (weaponSocket != null ? weaponSocket.Set : null)) { Cancel(); return; }

        // Plunge owns animation, vertical motion and the landing recovery until
        // it hands them back together. The generic swing clock must not end it.
        if (actDive)
        {
            animator.speed = 1f;
            TickPlunge(Time.deltaTime);
            return;
        }

        var dt = burstActive ? Time.unscaledDeltaTime : Time.deltaTime;
        // The attack clock and Animator use the same normal-combo tempo.
        // Art playback is authored per state by the setup helper.
        var speed = 1f;
        if (actArt == null && !actDive && forcedVictim == null)
            speed = attackAge < actHitStart ? 1.25f : 1.35f;
        animator.speed = speed;
        var previousAge = attackAge;
        attackAge += dt * speed;
        // The freeze ends just before the blade lands — time and camera snap
        // back so the hit, its hitstop and the shake all land at full speed.
        if (burstActive && attackAge >= burstEndAge) EndBurst();
        var movedThisFrame = false;

        if (actArt != null) TickArt(dt);

        // Air chase: while the lift window runs, rise toward the launched enemy
        // — the falling smash (spike) then drives it back down on contact.
        if (chaseTarget != null && chaseLiftEnd > 0f && attackAge <= chaseLiftEnd)
        {
            var to = chaseTarget.transform.position - transform.position;
            var planar = Vector3.ProjectOnPlane(to, Vector3.up);
            if (planar.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(planar.normalized);
            var dy = Mathf.Min(Mathf.Max(to.y + 0.5f, 0f), 12f * dt);
            if (dy > 0f) { character.Move(Vector3.up * dy); movedThisFrame = true; }
        }

        // Aim policy (detail §171-176): anticipation correction is capped at
        // 360°/s and freezes the bearing at first contact — no counter-rotation
        // mid-spin. The bearing itself refreshes until it locks.
        if (!aimLocked && !actDive && attackAge <= actHitEnd)
        {
            aimBearing = ResolveAim(actAim);
            if (aimBearing.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation,
                    Quaternion.LookRotation(aimBearing.normalized, Vector3.up), 360f * dt);
        }

        // Aim at entry; authored root travel remains collision-safe through the relay.
        // Hit window — sweep every frame so targets entering the arc mid-window
        // still connect, and stride forward: the clips carry stepping legwork,
        // so the capsule advances with the blow instead of feet sliding.
        if (!actDive && (actArt == null || actArt.contactMode != ArtContactMode.ProjectileOnly))
        {
            var windows = actWindows ?? new[] { new Vector2(actHitStart, actHitEnd) };
            // Continuous-contact moves (the crowd spin): the blade sweeps
            // through bodies once per authored pass — re-arm the victim set on
            // each pass boundary inside the window instead of once per window.
            if (actArt != null && actArt.rehitInterval > 0f)
            {
                var w = CurrentWindow();
                if (w >= 0)
                {
                    var pass = Mathf.FloorToInt((attackAge - windows[w].x) / actArt.rehitInterval);
                    if (pass != rehitPass) { rehitPass = pass; hitTargets.Clear(); }
                }
            }
            for (var i = 0; i < windows.Length; i++)
            {
                if (!CombatContactClock.Crosses(previousAge, attackAge, windows[i].x, windows[i].y)) continue;
                if (lastWindow != i)
                {
                    hitTargets.Clear(); lastWindow = i;
                    // Weapon feel: a fresh window re-opens the whoosh on plain
                    // swings — arts schedule their own sfx cues instead.
                    if (actArt == null && actionWeapon != null)
                        SfxBank.Play(string.IsNullOrEmpty(actionWeapon.swingSfx) ? "art.slash" : actionWeapon.swingSfx, transform.position + Vector3.up * 1.2f);
                }
                Sweep();
            }
        }

        // The launcher art whose contact window closed with nobody launched
        // earns the cosmetic hop — no session, no aerial permission.
        if (actArt != null && actArt.launch && !airSession && !airHopOnly && attackAge > actHitEnd)
            BeginAirHop();

        // Airborne swings (jump attack) get no gravity from the other branches
        // — pull the capsule down so the strike doesn't float mid-air. The relay
        // applies the same gravity while root motion owns the capsule. The
        // aerial session owns the capsule's Y outright.
        if (!actRootMotion && !actDive && !movedThisFrame && !character.isGrounded && !airSession && !airHopOnly)
            character.Move(Vector3.up * gravity * dt);

        // Other authored attacks may opt into resolving on floor contact.
        if (actEndOnLand && character.isGrounded)
        {
            Sweep();
            EndAttack();
            return;
        }

        if (ExecutePending()) return;
        if (pending == null && InRecovery && moveAction != null
            && moveAction.ReadValue<Vector2>().sqrMagnitude > 0.04f)
        { EndAttack(); return; }
        var timedOut = !actDive && !channeling &&
            (channelEndAt >= 0f ? attackAge >= channelEndAt : attackAge >= actDuration);
        if (timedOut) EndAttack();
    }

    /// <summary>Index of the hit window containing attackAge, or -1.</summary>
    private int CurrentWindow()
    {
        if (actWindows == null)
            return attackAge >= actHitStart && attackAge <= actHitEnd ? 0 : -1;
        for (var i = 0; i < actWindows.Length; i++)
            if (attackAge >= actWindows[i].x && attackAge <= actWindows[i].y) return i;
        return -1;
    }

    /// <summary>Weapon art: mana is the caller's cost (no stamina), the blade is
    /// forced out, and the art's windows/damage/reach replace the swing's.
    /// `held` reports whether the cast key is still down (channelled arts).
    /// False when the player can't act or the art's state doesn't exist yet.</summary>
    /// <summary>Modifier state sampled at press time (detail §75): hold Left
    /// Alt or the gamepad left trigger — pressing it alone does nothing.</summary>
    public static bool ModifierHeld =>
        (Keyboard.current != null && Keyboard.current.leftAltKey.isPressed)
        || (Gamepad.current != null && Gamepad.current.leftTrigger.ReadValue() > 0.3f);

    public bool TryStartArt(WeaponArt art, System.Func<bool> held = null)
    {
        if (art == null || flask != null && flask.Sipping) return false;
        var req = new Request { kind = RequestKind.Art, art = art, held = held, mod = ModifierHeld };
        if (IsAttacking)
        {
            return Queue(req);
        }
        if (airSession) return CommitSessionArt(req);
        return CommitArt(art, held);
    }

    /// <summary>The dedicated-ultimate path (detail §225): the press joins the
    /// one pending request like any other input; the meter is validated and
    /// spent only at commit — never mana, never a chord.</summary>
    public bool TryUltimate(WeaponArt art)
    {
        if (art == null || ultMeter == null || (flask != null && flask.Sipping)) return false;
        if (IsAttacking) return Queue(new Request { kind = RequestKind.Ultimate, art = art, mod = ModifierHeld });
        if (!ultMeter.Full) { ultMeter.Deny(); return false; }
        if (!CommitArt(art, null)) return false;
        ultMeter.TrySpend();
        return true;
    }

    /// <summary>RMB while airborne outside a session: the request is a plunge,
    /// not a technique (detail §152). Modifier+RMB up there is denied — no
    /// Skyfall target exists outside a session.</summary>
    public bool TryAirbornePress(bool mod)
    {
        if (GroundedForAction || airSession) return false;
        if (mod) { mana?.Deny(); return false; }
        return IsAttacking ? Queue(new Request { kind = RequestKind.Light }) : CommitLight();
    }

    public float CooldownRemaining(WeaponArt art) => art != null && cooldowns.TryGetValue(art, out var until)
        ? Mathf.Max(0f, until - Time.time) : 0f;

    private bool CommitArt(WeaponArt art, System.Func<bool> held)
    {
        // A dodge is the one displacement an art may interrupt — that's the
        // dodge-counter technique; anything else (slide, wallrun) still blocks.
        var dodge = GetComponent<DodgeController>();
        var dodging = dodge != null && dodge.IsDodging;
        if (art == null || animator == null || state.IsRooted || state.IsDead ||
            (flask != null && flask.Sipping) ||
            (state.IsDisplacing && !InRecovery && !dodging && !airSession) || (!GroundedForAction && !art.airChase) ||
            UiGates.MenuOpen || GameLoop.IsResting)
            return false;
        if (dodging) dodge.Interrupt();
        var id = Animator.StringToHash("Base Layer." + art.stateName);
        if (!animator.HasState(0, id)) return false;
        if (CooldownRemaining(art) > 0f) { mana?.Deny(); return false; }
        // Stamina-priced moves (the launcher's 14, detail §118) pay stamina.
        if (art.staminaCost > 0f && stamina != null && !stamina.TrySpendAttack(art.staminaCost)) return false;
        // The ultimate spends its own meter — never mana (detail §225).
        if (art.family != ActionFamily.Ultimate && mana != null && !mana.TrySpend(art.manaCost)) return false;
        // All destination checks and payment precede releasing the source action.
        ClearActionEffects();
        cooldowns[art] = Time.time + art.cooldown;

        // Channelled arts can run loopMaxTime past the start clip — keep the
        // blade out for the whole possible span.
        var drawnFor = art.duration + 1f + (art.loopState != null ? art.loopMaxTime + 0.5f : 0f);
        if (weaponSocket != null)
        {
            weaponSocket.KeepDrawn(drawnFor);
            // Skills summon the big sword like a magic — it replaces the equipped
            // blade's visual for the cast and vanishes with it.

        }
        var windows = art.hitWindows ?? System.Array.Empty<Vector2>();
        var step = new AttackStep
        {
            duration = art.duration,
            hitStart = windows.Length > 0 ? windows[0].x * art.duration : 0f,
            hitEnd = windows.Length > 0 ? windows[windows.Length - 1].y * art.duration : 0f,
            damage = art.damagePerHit,
            stepDistance = 0.8f,
            advanceDistance = art.advance,
            useRootMotion = art.useRootMotion,
            rootMotionY = art.rootMotionY,
        };
        StartVariant(id, step, true, spendStamina: false);
        if (!IsAttacking) return false;
        actRange = art.range;
        actArc = art.arc;
        actPoise = art.poiseDamage;
        actFreeze = art.hitstop;
        actShake = art.shake;
        // Only big-sword skills summon the oversized spectral blade — ordinary
        // arts ride the equipped weapon (KeepDrawn above still arms it).
        if (artSwordSet != null && art.BigSwordSkill)
            weaponSocket?.ManifestArtSword(artSwordSet, drawnFor, art.visualTheme, art.summonScale);
        actWindows = new Vector2[windows.Length];
        for (var i = 0; i < windows.Length; i++) actWindows[i] = windows[i] * art.duration;

        actArt = art;
        actDamageKind = art.damageKind;
        actReaction = art.reaction;
        BeginAim(art.aim);
        // Air chase is a SESSION finisher (detail §150): its only target is a
        // living session victim — unrelated airborne enemies never grant it.
        chaseTarget = art.airChase ? LivingSessionVictim() : null;
        chaseLiftEnd = chaseTarget != null ? art.duration * 0.45f : -1f;
        artBloomFired = false;
        artBloomAt = art.bloomPunch > 0f && windows.Length > 0 ? windows[0].x : -1f;
        cueMask = sfxMask = 0;
        artProjFired = false;
        channelHeld = held;
        channeling = false;
        channelT = 0f;
        channelEndAt = -1f;
        if (art.burst != null && art.burst.enabled)
            BeginBurst(art.burst, windows.Length > 0 ? windows[0].x * art.duration : 0.6f);
        return true;
    }

    /// <summary>Burst intro (Q+RMB ult): the world freezes while the hero's
    /// animator runs on unscaled time — the windup plays full-speed against a
    /// frozen crowd. Camera swings into the scripted close-up; time and framing
    /// snap back just before the first hit window opens.</summary>
    private void BeginBurst(BurstSpec spec, float snapAt)
    {
        burstSpec = spec;
        burstActive = true;
        burstEndAge = Mathf.Max(0.15f, snapAt - 0.05f);
        Time.timeScale = spec.timeScale;
        if (animator != null) animator.updateMode = AnimatorUpdateMode.UnscaledTime;
        if (state != null) { state.IsInvulnerable = true; burstInvuln = true; }
        // The ult announces itself through the HUD orb + impact frames, never a banner.
        if (spec.banner && (actArt == null || actArt.family != ActionFamily.Ultimate)) GameHud.Banner(actArt != null ? actArt.artName.ToUpperInvariant() : "BURST", 1.2f);
        if (spec.flashTime > 0f) GameHud.Flash(spec.flashTime);
        SfxBank.Play(spec.introSfx, transform.position + Vector3.up * 1.2f);
    }

    /// <summary>Restores what BeginBurst owns — timeScale only when it's still
    /// ours (a pause that opened mid-burst is not ours to release). The
    /// i-frames span the whole cast and clear with the action, not here.</summary>
    private void EndBurst()
    {
        burstActive = false;
        if (animator != null) animator.updateMode = AnimatorUpdateMode.Normal;
        if (burstSpec != null && Mathf.Approximately(Time.timeScale, burstSpec.timeScale))
            Time.timeScale = 1f;
        burstSpec = null;
    }

    /// <summary>Burst i-frames + freeze — every action exit must pass through
    /// here so neither can stick past the cast.</summary>
    private void ReleaseBurstOwnership()
    {
        if (burstActive) EndBurst();
        if (burstInvuln && state != null) state.IsInvulnerable = false;
        burstInvuln = false;
    }

    /// <summary>Timed FX/SFX/projectile cues + channel bookkeeping for the
    /// running art — ticks on the slowed attack clock so cues land on contact.</summary>
    private void TickArt(float dt)
    {
        var nt = actDuration > 0f ? attackAge / actDuration : 1f;
        var lockTarget = lockOn != null && lockOn.CurrentTarget != null ? lockOn.CurrentTarget.transform : null;

        var crimson = actArt.visualTheme == ArtVisualTheme.DarkCrimson;
        var cues = actArt.fxCues;
        if (cues != null)
            for (var i = 0; i < cues.Length && i < 31; i++)
                if ((cueMask & (1 << i)) == 0 && nt >= cues[i].time)
                {
                    cueMask |= 1 << i;
                    var c = cues[i];
                    var effect = ArtFx.Spawn(c, transform, animator, lockTarget,
                        crimson ? ArtFx.DarkCrimson : (Color?)null);
                    // Burst cue inside the world freeze — particles must run
                    // unscaled or the charge crackle is invisible until release.
                    if (burstActive && effect != null)
                        foreach (var ps in effect.GetComponentsInChildren<ParticleSystem>())
                        { var m = ps.main; m.useUnscaledTime = true; }
                    if (c.follow && effect != null) attachedEffects.Add(effect);
                    if (c.shake > 0f) cameraController?.Shake(c.shake);
                    if (c.hitstop > 0f)
                    {
                        Freeze(c.hitstop);
                    }
                }
        var sfx = actArt.sfxCues;
        if (sfx != null)
            for (var i = 0; i < sfx.Length && i < 15; i++)
                if ((sfxMask & (1 << i)) == 0 && nt >= sfx[i].time)
                {
                    sfxMask |= 1 << i;
                    SfxBank.Play(sfx[i].id, transform.position + Vector3.up * 1.2f);
                }

        // Bloom punch rides the first hit window — the spike lands on the burst,
        // not the windup. PostPulse self-creates; nothing to wire.
        if (!artBloomFired && artBloomAt >= 0f && nt >= artBloomAt)
        {
            artBloomFired = true;
            PostPulse.Pulse(actArt.bloomPunch);
        }

        if (!artProjFired && actArt.projectile != null && (actArt.projectile.prefab != null || actArt.projectile.wave) && nt >= actArt.projectile.spawnTime)
        {
            artProjFired = true;
            var dir = lockTarget != null
                ? Vector3.ProjectOnPlane(lockTarget.position - transform.position, Vector3.up)
                : transform.forward;
            if (dir.sqrMagnitude < 0.001f) dir = transform.forward; // target on top of us
            dir.Normalize();
            ArtProjectile.Launch(actArt.projectile,
                transform.position + Vector3.up * 1.3f, dir, lockTarget, transform, actPoise,
                crimson ? ArtFx.DarkCrimson : (Color?)null);
        }

        // Channel entry: the start clip ends → while the cast key stays held
        // (and mana lasts), swap to the loop state and tick damage on interval.
        if (!channeling && channelEndAt < 0f && attackAge >= actDuration)
        {
            var wantsChannel = actArt.loopState != null
                               && (channelHeld == null || channelHeld())
                               && (mana == null || mana.Current > 0.01f);
            if (wantsChannel)
            {
                var loopId = Animator.StringToHash("Base Layer." + actArt.loopState);
                if (animator.HasState(0, loopId))
                {
                    animator.CrossFadeInFixedTime(loopId, 0.08f, 0);
                    channeling = true;
                    channelT = 0f;
                    nextChannelHit = 0.12f;
                }
            }
            else if (actArt.loopEndState != null)
            {
                var endId = Animator.StringToHash("Base Layer." + actArt.loopEndState);
                if (animator.HasState(0, endId))
                {
                    animator.CrossFadeInFixedTime(endId, 0.08f, 0);
                    channelEndAt = attackAge + Mathf.Max(0.3f, actArt.loopEndDuration);
                }
            }
        }
        if (!channeling) return;

        channelT += dt;
        if (mana != null && actArt.manaPerSecond > 0f)
            mana.TrySpend(actArt.manaPerSecond * dt);
        if (channelT >= nextChannelHit)
        {
            hitTargets.Clear(); // each tick can re-hit, like a new window
            Sweep();
            nextChannelHit += Mathf.Max(0.05f, actArt.loopHitInterval);
        }
        var released = channelHeld != null && !channelHeld();
        var empty = mana != null && mana.Current <= 0.01f;
        if (released || empty || channelT >= actArt.loopMaxTime)
        {
            channeling = false;
            var endId = Animator.StringToHash("Base Layer." + actArt.loopEndState);
            if (actArt.loopEndState != null && animator.HasState(0, endId))
                animator.CrossFadeInFixedTime(endId, 0.08f, 0);
            channelEndAt = attackAge + Mathf.Max(0.3f, actArt.loopEndDuration); // the loopEnd flourish owns the tail
        }
    }

    private void ReleaseHitstop()
    {
        hitstopTimer = 0f;
        if (ownsHitstop && Mathf.Approximately(Time.timeScale, freezeTimeScale)) Time.timeScale = 1f;
        ownsHitstop = false;
    }

    private void ProcessAttackInput()
    {
        if (UiGates.MenuOpen || GameLoop.IsResting || state.IsDead || state.IsRooted
            || (flask != null && flask.Sipping))
        { pending = null; return; }
        if (attackAction != null && attackAction.WasPressedThisFrame())
        {
            var mod = ModifierHeld;
            // Session context is resolved before ground routes (detail §94):
            // modifier+LMB is the plunge exit; a plain press queues/advances
            // the next aerial strike.
            if (airSession)
            {
                if (mod) { EndAerialSession(); StartPlunge(); }
                else if (IsAttacking) Queue(new Request { kind = RequestKind.Light });
                else AdvanceAirStep();
            }
            else if (mod && artCaster != null && artCaster.LauncherArt != null && GroundedForAction)
            {
                // Modifier + LMB = the launcher, direct (detail §85).
                CommitArt(artCaster.LauncherArt, null);
            }
            else if (IsAttacking) Queue(new Request { kind = RequestKind.Light });
            else CommitLight();
        }
    }


    private bool Queue(Request request)
    {
        if (state.IsDead || state.IsRooted || UiGates.MenuOpen || GameLoop.IsResting) return false;
        // Latest valid press owns the single slot; 0.25s of unfrozen gameplay
        // time to reach the safe exit (age is extended during owned freeze).
        pending = request;
        pendingUntil = Time.unscaledTime + 0.25f;
        return true;
    }

    public bool RequestDodge(Vector2 move)
    {
        if (flask != null && flask.Sipping) return false;
        if (!IsAttacking) return GetComponent<DodgeController>()?.TryDodge(move) == true;
        return Queue(new Request { kind = RequestKind.Dodge, dodge = move });
    }

    private bool ExecutePending()
    {
        var now = Time.unscaledTime;
        if (pending == null || !InRecovery) return false;
        if (now > pendingUntil) { pending = null; return false; }
        var request = pending;
        pending = null;
        if (request.kind == RequestKind.Ultimate)
        {
            // Validation + spend happen here, at commit (detail §225) — a
            // queued ultimate whose meter got spent elsewhere just fails.
            if (ultMeter == null || !ultMeter.Full) { ultMeter?.Deny(); return false; }
            if (!CommitArt(request.art, null)) return false;
            ultMeter.TrySpend();
            return true;
        }
        if (request.kind == RequestKind.Art)
            return airSession ? CommitSessionArt(request) : CommitArt(request.art, request.held);
        if (request.kind == RequestKind.Dodge) return GetComponent<DodgeController>()?.TryDodge(request.dodge) == true;
        // A light press inside a session advances the aerial chain; modifier+LMB
        // captured at press was the plunge exit (handled live, not queued).
        if (airSession) return AdvanceAirStep();
        if (!GroundedForAction) return StartPlunge();
        var next = actArt == null && activeSteps.Length > 0 && comboIndex + 1 < activeSteps.Length ? comboIndex + 1 : 0;
        return StartSwing(next, weaponSocket != null && weaponSocket.Set != null, true);
    }

    private bool CommitLight()
    {
        if (animator == null || !character.enabled) return false;
        var armed = weaponSocket != null && weaponSocket.Set != null;
        if (slide != null && slide.IsSliding)
        {
            if (!slide.CanCancelIntoAttack || !animator.HasState(0, DashAttackId)) return false;
            if (stamina != null && !stamina.TrySpendAttack()) return false;
            if (!slide.TryCancelIntoAttack()) return false;
            return StartVariant(DashAttackId, DashAttackStep, true, false);
        }
        if (state.IsDisplacing) return false;
        if (!character.isGrounded) return armed && StartPlunge();
        // Sprint attack = the dash strike (Mooncleaver Rush): a short clean
        // sprint re-arms it — SprintTime zeroes on release, airtime and
        // displacement, so attacking mid-sprint re-arms the clock. The
        // launcher moved to "two normals → RMB" (Upper Attack). An
        // unaffordable sprint art is a clear denial (copper flash), not a
        // silently different swing — plan §"Preserve the spinning combo feel".
        if (armed && locomotion != null && locomotion.SprintTime >= sprintArtMinSprint)
        {
            var sprintArt = artCaster != null ? artCaster.SprintArt : null;
            if (sprintArt != null)
            {
                CommitArt(sprintArt, null); // cooldown/spend failures flash Denied inside
                return true;
            }
        }
        if (armed && lockOn != null && lockOn.IsLockedOn)
        {
            var victim = EnemyAI.FindBackstabTarget(transform.position, backstabRange);
            if (victim != null && lockOn.CurrentTarget != null && victim.GetComponent<Targetable>() == lockOn.CurrentTarget)
                return DoBackstab(victim);
        }
        return StartSwing(0, armed);
    }

    private bool StartPlunge()
    {
        if (animator == null || character == null || !character.enabled ||
            plungeStartClip == null || plungeFallClip == null || plungeLandClip == null ||
            plungeStartClip.length <= 0f || plungeFallClip.length <= 0f || plungeLandClip.length <= 0f ||
            !animator.HasState(0, DiveStartId) || !animator.HasState(0, DiveAttackId) || !animator.HasState(0, DiveLandId))
        {
            if (!warnedPlungeSetup)
            {
                warnedPlungeSetup = true;
                Debug.LogWarning("[Plunge] Run Setup Fast Crowd Combat outside Play Mode to install all three authored phases.", this);
            }
            return false;
        }
        if (stamina != null && !stamina.TrySpendAttack()) return false;
        // Falling out of a session/hop hands the plunge the session velocity —
        // locomotion's tracker is stale while session owns Y.
        var inherited = airSession || airHopOnly ? airVy
            : locomotion != null ? locomotion.VerticalSpeed : character.velocity.y;
        EndAerialSession(resumeLocomotion: false); // the plunge owns vertical now
        if (!StartVariant(DiveStartId, DiveAttackStep, true, false, 0.12f)) return false;
        actDive = true; actRange = diveRadius; actArc = 360f; actFreeze = 0.06f;
        Phase = PlungePhase.Start; plungeAge = 0f;
        plungeInheritedVelocity = inherited; plungeVelocity = inherited;
        plungeContactHeld = plungeImpactFired = false;
        plungeStillT = plungeDiagT = 0f;
        animator.SetBool("InAir", true);
        return true;
    }

    /// <summary>Pure timing seams used by the focused plunge checks.</summary>
    public static float PlungePreparationVelocity(float inherited, float age, float brakeSeconds = 0.08f)
        => Mathf.Lerp(inherited, 0f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / Mathf.Max(0.01f, brakeSeconds))));

    public static float PlungeDescentVelocity(float age, float terminal = -18f, float accelerationSeconds = 0.18f)
        => Mathf.Lerp(0f, Mathf.Min(-1f, terminal), Mathf.Clamp01(age / Mathf.Max(0.01f, accelerationSeconds)));

    public static bool PlungeSurfaceWalkable(Vector3 normal, float slopeLimit)
        => Vector3.Dot(normal.normalized, Vector3.up) >= Mathf.Cos(Mathf.Clamp(slopeLimit, 0f, 89f) * Mathf.Deg2Rad);

    private void TickPlunge(float dt)
    {
        weaponSocket?.KeepDrawn(0.5f);
        plungeAge += dt;
        attackAge += dt;
        if (Phase == PlungePhase.Land)
        {
            // Pin the capsule while recovering, but release cleanly if a platform
            // moves away or authored external travel takes us off an edge.
            var flags = character.Move(Vector3.down * 2f * dt);
            var supported = TryPlungeGround(out var point, out var normal);
            if (!supported && (flags & CollisionFlags.Below) == 0 && !character.isGrounded)
            {
                plungeContactHeld = false;
                EndAttack();
                return;
            }
            if (supported) { plungeContactPoint = point; plungeContactNormal = normal; }
            animator.SetBool("InAir", false);
            if (ExecutePending()) return;
            var moving = moveAction != null && moveAction.ReadValue<Vector2>().sqrMagnitude > 0.04f;
            if (plungeAge >= plungeLandClip.length / 1.2f || (moving && plungeAge >= 0.24f)) EndAttack();
            return;
        }

        // Bodies never count as floor but can still hold the capsule up — slip
        // past them for the whole descent, collisions restored at touchdown.
        PassThroughEnemies();
        plungeVelocity = Phase == PlungePhase.Start
            ? PlungePreparationVelocity(plungeInheritedVelocity, plungeAge, plungeBrakeSeconds)
            : PlungeDescentVelocity(plungeAge, plungeTerminalVelocity, plungeAccelerationSeconds);
        var drift = PlungeDrift();
        var prevY = transform.position.y;
        var collisions = character.Move((drift + Vector3.up * plungeVelocity) * dt);
        var actualVy = (transform.position.y - prevY) / dt;
        if ((collisions & CollisionFlags.Above) != 0 && plungeVelocity > 0f)
        { plungeInheritedVelocity = 0f; plungeVelocity = 0f; }
        // Grounding is based on the capsule and a walkable contact, never a
        // lifted animated foot or isGrounded left over from another mover.
        Vector3 contact = Vector3.zero, contactNormal = Vector3.up;
        var onFloor = plungeVelocity <= 0f && TryPlungeGround(out contact, out contactNormal);
        if (plungeDiagnostics) LogPlunge(dt, actualVy, collisions, onFloor);
        if (onFloor)
        {
            BeginPlungeLanding(contact, contactNormal);
            return;
        }
        if (Phase == PlungePhase.Start && plungeAge >= plungePreparationSeconds)
        {
            Phase = PlungePhase.Fall; plungeAge = 0f; plungeVelocity = 0f;
            animator.CrossFadeInFixedTime(DiveAttackId, 0.08f, 0);
        }
        animator.SetBool("InAir", true);
        animator.SetFloat("VerticalSpeed", plungeVelocity);
    }

    private Vector3 PlungeDrift()
    {
        var move = moveAction != null ? Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f) : Vector2.zero;
        var cam = Camera.main;
        var forward = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized : transform.forward;
        if (forward.sqrMagnitude < 0.001f) forward = transform.forward;
        return (forward * move.y + Vector3.Cross(Vector3.up, forward) * move.x) * Mathf.Clamp(plungeDriftSpeed, 0f, 1f);
    }

    private bool TryPlungeGround(out Vector3 point, out Vector3 normal)
    {
        point = Vector3.zero; normal = Vector3.up;
        var bottom = character.bounds.min.y;
        var margin = character.skinWidth + 0.03f;
        var center = character.bounds.center;
        var radius = Mathf.Min(0.12f, character.radius * 0.4f);
        var origin = new Vector3(center.x, bottom + radius + margin, center.z);
        var hits = Physics.SphereCastAll(origin, radius, Vector3.down, margin * 2f + 0.03f, ~0, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var hit in hits)
        {
            if (hit.collider == null || hit.transform.IsChildOf(transform) || hit.collider.GetComponentInParent<Health>() != null ||
                hit.collider.GetComponentInParent<PlayerHealth>() != null || !PlungeSurfaceWalkable(hit.normal, character.slopeLimit)) continue;
            var gap = bottom - hit.point.y;
            if (gap > margin || gap < -margin) continue;
            point = hit.point; normal = hit.normal;
            return true;
        }
        return false;
    }

    // ---------- Aerial session (detail.md §4) ----------
    // One owner for capsule Y while the session lives: hero and victims launch
    // at 8 m/s under 14 m/s² (cap 3m rise); confirmed contacts suspend the fall
    // for ≤0.12s per step, 0.36s total. Either attack button advances the next
    // of three strikes; modifier+LMB plunges out, modifier+RMB spikes via
    // Skyfall Edge on a living session victim. The launcher clip is the
    // ZeroHeight take — the rise is entirely code-owned.

    /// <summary>Launcher connects on a living victim — the session starts and
    /// the hero rises with it. Called from Sweep, once.</summary>
    private void BeginAerialSession()
    {
        airSession = true;
        airHopOnly = false;
        airVy = launchRiseSpeed;
        airFloorY = transform.position.y;
        airAge = 0f;
        airStepIndex = 0;
        airSuspendT = airSuspendUsed = 0f;
        airStepSuspended = false;
        airPrimary = LivingSessionVictim();
        state.IsDisplacing = true; // the session is a displacement until landing
    }

    /// <summary>The launcher landed no launch (miss, all resisted, or
    /// kill-without-survivor): a short cosmetic hop, then ordinary falling —
    /// no aerial permission (detail §126).</summary>
    private void BeginAirHop()
    {
        airHopOnly = true;
        airVy = Mathf.Sqrt(2f * launchGravity * failedLaunchHop);
        airFloorY = transform.position.y;
        state.IsDisplacing = true;
    }

    private void EndAerialSession(bool resumeLocomotion = true)
    {
        if (!airSession && !airHopOnly) return;
        var falling = airVy;
        airSession = airHopOnly = false;
        airVictims.Clear();
        airPrimary = null;
        airStepIndex = -1;
        airSuspendT = airSuspendUsed = 0f;
        airStepSuspended = false;
        if (state != null && !IsAttacking) state.IsDisplacing = false;
        if (resumeLocomotion && character != null && !character.isGrounded)
            locomotion?.ResumeVerticalMotion(falling);
    }

    /// <summary>Nearest LIVING airborne victim of this session — the aerial
    /// chain's primary and Skyfall's only legal target (detail §144/150).</summary>
    private EnemyAI LivingSessionVictim()
    {
        EnemyAI best = null;
        var bd = float.MaxValue;
        for (var i = airVictims.Count - 1; i >= 0; i--)
        {
            var e = airVictims[i];
            var dead = e == null || e.GetComponent<Health>() == null || e.GetComponent<Health>().IsDead;
            if (dead) { airVictims.RemoveAt(i); continue; }
            if (!e.IsAirborne) continue;
            var d = (e.transform.position - transform.position).sqrMagnitude;
            if (d < bd) { bd = d; best = e; }
        }
        return best;
    }

    /// <summary>RMB inside a session (detail §148-150): unmodified advances the
    /// next aerial strike like LMB; modifier+RMB is Skyfall Edge on a living
    /// session victim — denied outright with none.</summary>
    public bool RequestSessionTechnique(WeaponArt skyfall)
    {
        if (!airSession) return false;
        if (LivingSessionVictim() == null) { mana?.Deny(); return false; }
        return TryStartArt(skyfall);
    }

    /// <summary>An art request committed inside a session: only the air-chase
    /// smash (Skyfall Edge) is legal up there — any other art press is denied.</summary>
    private bool CommitSessionArt(Request request)
    {
        if (request.art == null || !request.art.airChase || LivingSessionVictim() == null)
        { mana?.Deny(); return true; } // the press is consumed either way
        return CommitArt(request.art, request.held);
    }

    /// <summary>Session RMB path — the unmodified press joins LMB as the next
    /// aerial strike (detail §148): mid-strike it queues the one pending
    /// request, same as an attack-button press.</summary>
    public bool TryAdvanceAerial()
    {
        if (!airSession) return false;
        return IsAttacking ? Queue(new Request { kind = RequestKind.Light }) : AdvanceAirStep();
    }

    /// <summary>Advance the three-step aerial chain (Jump_Attack_Combo_1→2→3).
    /// Steps past the third are denied — ordinary presses after the chain do
    /// not restart it before landing (detail §153).</summary>
    private bool AdvanceAirStep()
    {
        if (!airSession || airStepIndex < 0 || airStepIndex >= AirSteps.Length) return false;
        if (!animator.HasState(0, AirIds[airStepIndex])) return false;
        var step = AirSteps[airStepIndex];
        if (stamina != null && !stamina.TrySpendAttack(step.staminaCost)) return false;
        if (!StartVariant(AirIds[airStepIndex], step, true, false, 0.06f)) return false;
        airStepIndex++;
        airStepSuspended = false;
        return true;
    }

    /// <summary>Session physics + bookkeeping — runs every frame while the
    /// session (or the cosmetic hop) owns the capsule's vertical, including
    /// the gaps between strikes when no action is playing.</summary>
    private void TickAerial(float dt)
    {
        if (!airSession && !airHopOnly) return;
        // A running art with authored Y (Skyfall's smash) owns the capsule
        // while it plays — the session only keeps its victim bookkeeping.
        var artOwnsY = IsAttacking && actArt != null && actArt.rootMotionY;
        if (!ownsHitstop) airAge += dt;

        if (!artOwnsY)
        {
            if (airSuspendT > 0f)
            {
                airSuspendT -= dt;
                airSuspendUsed += dt;
                airVy = Mathf.Max(airVy, 0f); // arrested fall, never added height
            }
            else airVy -= launchGravity * dt;

            // Supported-rise cap: 3m above the launch floor, ceilings respected.
            var vy = airVy;
            var pos = transform.position;
            if (vy > 0f && pos.y + vy * dt > airFloorY + launchMaxRise)
            { vy = (airFloorY + launchMaxRise - pos.y) / Mathf.Max(dt, 1e-5f); airVy = 0f; }
            var flags = character.Move(Vector3.up * vy * dt);
            if ((flags & CollisionFlags.Above) != 0 && airVy > 0f) airVy = 0f;

            // ≤2m/s horizontal correction toward the primary victim — only
            // closing to a non-overlapping strike range, never a snap.
            if (airSession)
            {
                if (airPrimary == null || !airPrimary.IsAirborne)
                    airPrimary = LivingSessionVictim();
                if (airPrimary != null)
                {
                    var to = Vector3.ProjectOnPlane(airPrimary.transform.position - transform.position, Vector3.up);
                    var gap = to.magnitude - 0.9f;
                    if (gap > 0f)
                        character.Move(to.normalized * Mathf.Min(airCorrectionSpeed * dt, gap));
                }
            }
        }

        // Session bounds: three strikes done OR 2.4s since launch OR touchdown.
        if (airAge >= sessionMaxAge || (airVy <= 0f && character.isGrounded))
        {
            var grounded = character.isGrounded;
            EndAerialSession(resumeLocomotion: !grounded);
        }
    }

    /// <summary>Drop capsule-vs-enemy collision pairs for the descent — the probe
    /// excludes bodies from counting as floor, so without this a dive onto an
    /// enemy's head hangs unsupported mid-air. Terrain, walls and stairs still
    /// block; restore happens the moment a real floor takes over.</summary>
    private void PassThroughEnemies()
    {
        var center = transform.TransformPoint(character.center);
        var half = Mathf.Max(0f, character.height * 0.5f - character.radius);
        // Reach below the feet — the capsule descends into bodies, catching them
        // before contact avoids even one supported frame.
        var count = Physics.OverlapCapsuleNonAlloc(
            center + Vector3.up * (half + 0.15f), center - Vector3.up * (half + 0.7f),
            character.radius + 0.15f, plungeOverlap, ~0, QueryTriggerInteraction.Ignore);
        for (var i = 0; i < count; i++)
        {
            var col = plungeOverlap[i];
            if (col == null || col.isTrigger || col.transform.IsChildOf(transform)
                || plungeIgnored.ContainsKey(col)) continue;
            // PlayerHealth is its own class (not a Health), so the player's own
            // body never matches — only enemy bodies do.
            if (col.GetComponentInParent<Health>() == null) continue;
            plungeIgnored[col] = Physics.GetIgnoreCollision(character, col);
            Physics.IgnoreCollision(character, col, true);
        }
    }

    /// <summary>Push the capsule sideways out of any body it descended into —
    /// before collisions come back, so depenetration can't pop us. Move() keeps
    /// the shove collision-safe against world geometry.</summary>
    private void ResolvePlungeOverlap()
    {
        var center = transform.TransformPoint(character.center);
        var half = Mathf.Max(0f, character.height * 0.5f - character.radius);
        var count = Physics.OverlapCapsuleNonAlloc(center + Vector3.up * half, center - Vector3.up * half,
            character.radius, plungeOverlap, ~0, QueryTriggerInteraction.Ignore);
        for (var i = 0; i < count; i++)
        {
            var col = plungeOverlap[i];
            if (col == null || col.isTrigger || col.transform.IsChildOf(transform)) continue;
            if (col.GetComponentInParent<Health>() == null) continue;
            if (!Physics.ComputePenetration(character, transform.position, transform.rotation,
                    col, col.transform.position, col.transform.rotation, out var dir, out var dist)) continue;
            var push = Vector3.ProjectOnPlane(dir, Vector3.up);
            if (push.sqrMagnitude < 1e-6f) // dead-centre on a head — exit any way out
                push = Vector3.ProjectOnPlane(center - col.bounds.center, Vector3.up);
            if (push.sqrMagnitude < 1e-6f) push = transform.forward;
            character.Move(push.normalized * Mathf.Min(dist + 0.02f, 0.6f));
        }
    }

    private void RestorePlungeCollisions()
    {
        if (plungeIgnored.Count == 0) return;
        foreach (var pair in plungeIgnored)
            if (pair.Key != null)
                Physics.IgnoreCollision(character, pair.Key, pair.Value);
        plungeIgnored.Clear();
    }

    /// <summary>Throttleable descent telemetry — the stall signature is a
    /// requested fall speed with ~0 real displacement and no floor probe hit.
    /// Names the supporting collider with an unfiltered cast (the normal probe
    /// deliberately skips bodies, which is what hid the blocker).</summary>
    private void LogPlunge(float dt, float actualVy, CollisionFlags collisions, bool onFloor)
    {
        var suspended = plungeVelocity <= -4f && actualVy > -1f;
        plungeStillT = suspended ? plungeStillT + dt : 0f;
        plungeDiagT -= dt;
        if (plungeDiagT > 0f) return;
        plungeDiagT = 0.1f;
        var support = "-";
        if (!onFloor && suspended && Physics.SphereCast(
                new Vector3(character.bounds.center.x, character.bounds.min.y + 0.05f, character.bounds.center.z),
                0.12f, Vector3.down, out var hit, 0.5f, ~0, QueryTriggerInteraction.Ignore))
            support = hit.collider.name;
        Debug.Log($"[Plunge] {Phase} age={plungeAge:0.00} vY req={plungeVelocity:0.0} act={actualVy:0.00} " +
                  $"flags={collisions} floor={(onFloor ? "hit" : "none")} stall={plungeStillT:0.00} support={support}", this);
    }

    private void BeginPlungeLanding(Vector3 point, Vector3 normal)
    {
        if (plungeImpactFired) return;
        ResolvePlungeOverlap();
        RestorePlungeCollisions();
        plungeImpactFired = true; plungeContactHeld = true;
        plungeContactPoint = point; plungeContactNormal = normal;
        Phase = PlungePhase.Land; plungeAge = 0f; plungeVelocity = 0f;
        animator.SetBool("InAir", false); animator.SetFloat("VerticalSpeed", -2f);
        animator.CrossFadeInFixedTime(DiveLandId, 0.06f, 0);
        GetComponent<BladeRibbon>()?.Clear();
        grounding?.NotifyPlungeContact(point, normal);
        hitTargets.Clear();
        var contacts = Sweep(false, point);
        feedbackFrame = Time.frameCount;
        cameraController?.Shake(contacts > 0 ? Mathf.Min(0.35f, Mathf.Max(0.25f, actShake)) : 0.25f);
        Freeze(0.06f); // plunge freeze (detail §208)
        ArtFx.SpawnAt(diveImpactFx, point + normal * 0.02f, Quaternion.FromToRotation(Vector3.up, normal), 0.6f);
        GetComponent<TraversalEffects>()?.ImpactBurst(point, normal);
        onDiveImpact?.Invoke();
    }

    private void ReleasePlungeOwnership(bool groundedHandoff)
    {
        if (!actDive) return;
        RestorePlungeCollisions();
        if (groundedHandoff && plungeContactHeld) locomotion?.CompleteExternalLanding();
        else locomotion?.ResumeVerticalMotion(plungeVelocity);
        actDive = false; Phase = PlungePhase.None; plungeContactHeld = false;
        plungeAge = 0f; tilt?.EndTilt();
    }

    private void ClearActionEffects()
    {
        foreach (var fx in attachedEffects) if (fx != null) { fx.SetActive(false); Destroy(fx); }
        attachedEffects.Clear();
        GetComponent<BladeRibbon>()?.Clear();
        weaponSocket?.ClearArtSword();
    }

    private bool armedSwing;

    /// <returns>False when the stamina spend failed — caller must end the
    /// attack instead of assuming a new swing started.</returns>
    private bool StartSwing(int index, bool armed, bool chained = false)
    {
        var steps = armed ? Steps : UnarmedSteps;
        var ids = armed ? AttackIds : UnarmedIds;
        if (animator == null || index < 0 || index >= steps.Length || index >= ids.Length
            || steps[index] == null || !animator.HasState(0, ids[index])) return false;
        // Every swing costs stamina — a failed spend simply doesn't chain,
        // so an empty bar ends the combo at this swing's follow-through.
        if (stamina != null && !stamina.TrySpendAttack())
            return false;
        ReleasePlungeOwnership(true);
        ClearActionEffects();
        forcedVictim = null;
        actionWeapon = weaponSocket != null ? weaponSocket.Set : null;
        animator.speed = 1f;
        if (armed) weaponSocket?.KeepDrawn(steps[index].duration + 0.5f);
        activeSteps = armed ? Steps : UnarmedSteps;
        activeIds = armed ? AttackIds : UnarmedIds;
        armedSwing = armed;
        actWindows = null;
        lastWindow = -1;
        rehitPass = -1;
        var step = activeSteps[index];
        var set = weaponSocket != null ? weaponSocket.Set : null;
        comboIndex = index;
        actDuration = step.duration;
        actHitStart = step.hitStart;
        actHitEnd = step.hitEnd;
        actDamage = step.damage;
        actPoise = step.poiseDamage > 0f ? step.poiseDamage : step.damage;
        actRange = armed ? (set != null ? set.attackRange : attackRange) : unarmedRange;
        actArc = armed ? (set != null ? set.attackArc : 180f) : unarmedArc;
        actAdvance = step.advanceDistance;
        actFreeze = Mathf.Min(freezeFinal, freezeQuick + freezeStep * index);
        actShake = hitShakeBase + hitShakePerCombo * index;
        // The finisher gets the held-beat slowdown; mid-chain swings stay snappy.
        actEndOnLand = step.endOnLand;
        actRootMotion = step.useRootMotion;
        actRootMotionY = step.rootMotionY;
        actDive = false;
        actDamageKind = DamageKind.Normal;
        stepRemaining = step.stepDistance;
        attackAge = 0f;
        hitTargets.Clear();
        chaseTarget = null;
        chaseLiftEnd = -1f;
        IsAttacking = true;
        ActionRevision++;
        state.IsDisplacing = true;
        actArt = null; // plain swings clear any stale art bookkeeping
        channeling = false;
        channelEndAt = -1f;
        candidates = FindObjectsByType<Targetable>(FindObjectsSortMode.None);
        actionCredited = false;
        actionContacted = false;
        actReaction = index == activeSteps.Length - 1 && activeSteps.Length > 1
            ? ReactionProfile.Finisher : step.reaction;

        BeginAim(airSession ? AimPolicy.LaunchSession : AimPolicy.MovementLed);

        animator.CrossFadeInFixedTime(activeIds[index], 0.06f, 0);
        return true;
    }

    /// <summary>One-off swings (dash/jump attacks) — same sweep/hit plumbing as
    /// a combo swing but no chain slots: it always exits through EndAttack.</summary>
    private bool StartVariant(int stateId, AttackStep step, bool armed, bool spendStamina = true, float blend = 0.06f)
    {
        if (animator == null || step == null || !animator.HasState(0, stateId)) return false;
        if (spendStamina && stamina != null && !stamina.TrySpendAttack()) return false;
        ReleasePlungeOwnership(true);
        ClearActionEffects();
        forcedVictim = null;
        actionWeapon = weaponSocket != null ? weaponSocket.Set : null;
        animator.speed = 1f;
        if (armed) weaponSocket?.KeepDrawn(step.duration + 0.5f);
        actWindows = null;
        lastWindow = -1;
        rehitPass = -1;
        activeSteps = System.Array.Empty<AttackStep>();
        activeIds = System.Array.Empty<int>();
        armedSwing = armed;
        var set = weaponSocket != null ? weaponSocket.Set : null;
        comboIndex = 0;
        actDuration = step.duration;
        actHitStart = step.hitStart;
        actHitEnd = step.hitEnd;
        actDamage = step.damage;
        actPoise = step.poiseDamage > 0f ? step.poiseDamage : step.damage;
        actRange = armed ? (set != null ? set.attackRange : attackRange) : unarmedRange;
        actArc = armed ? (set != null ? set.attackArc : 180f) : unarmedArc;
        actAdvance = step.advanceDistance;
        actFreeze = Mathf.Min(freezeFinal, freezeQuick * 1.5f);
        actShake = hitShakeBase * 1.5f;
        actEndOnLand = step.endOnLand;
        actRootMotion = step.useRootMotion;
        actRootMotionY = step.rootMotionY;
        actDive = false;
        actDamageKind = DamageKind.Normal;
        stepRemaining = step.stepDistance;
        attackAge = 0f;
        hitTargets.Clear();
        chaseTarget = null;
        chaseLiftEnd = -1f;
        IsAttacking = true;
        ActionRevision++;
        state.IsDisplacing = true;
        actArt = null;
        channeling = false;
        channelEndAt = -1f;
        candidates = FindObjectsByType<Targetable>(FindObjectsSortMode.None);
        actionCredited = false;
        actionContacted = false;
        actReaction = step.reaction;
        BeginAim(airSession ? AimPolicy.LaunchSession : AimPolicy.MovementLed);
        animator.CrossFadeInFixedTime(stateId, blend, 0);
        return true;
    }

    /// <summary>Faces the victim's back, plays the Backstab state (heavy finisher
    /// as fallback before the generator runs), and pins the sweep to just them.</summary>
    private bool DoBackstab(EnemyAI victim)
    {
        var hasStab = animator.HasState(0, BackstabId);
        if (!StartVariant(hasStab ? BackstabId : AttackIds[2], BackstabStep, true)) return false;

        // Snap into the stab pose: directly behind the victim, facing its back.
        // After StartVariant — its move-input facing would spin us off the victim.
        var back = Vector3.ProjectOnPlane(victim.transform.forward, Vector3.up).normalized;
        if (back.sqrMagnitude < 0.01f) back = Vector3.forward;
        var snap = victim.transform.position - back * backstabSnapDistance;
        snap.y = transform.position.y;
        character.enabled = false;
        transform.SetPositionAndRotation(snap, Quaternion.LookRotation(back, Vector3.up));
        character.enabled = true;
        var set = weaponSocket != null ? weaponSocket.Set : null;
        var baseDamage = set != null && set.attacks != null && set.attacks.Length > 0
            ? set.attacks[0].damage : FallbackSteps[0].damage;
        actDamage = baseDamage * backstabMultiplier;
        actPoise = actDamage; // a committed stab always hits posture hard
        actFreeze = freezeCrit;
        actShake = hitShakeBase * 3f;
        forcedVictim = victim.GetComponent<Targetable>();
        actRange = backstabRange + 0.6f; // victim may drift during the windup
        actArc = 360f; // rear arc was verified at press — the committed blow hits
        victim.OnBackstabbed(transform);
        return true;
    }

    private void EndAttack()
    {
        var wasDive = actDive;
        var wasGrounded = GroundedForAction;
        var rageSec = actArt != null && actArt.burst != null && actArt.burst.enabled
            ? actArt.burst.rageSeconds : 0f;
        ReleaseBurstOwnership();
        ReleasePlungeOwnership(Phase == PlungePhase.Land);
        pending = null;
        // A live session keeps owning the capsule — IsDisplacing stays set and
        // the velocity handoff waits for the session's own landing path.
        if (!wasDive && !airSession && !airHopOnly) locomotion?.ResumeVerticalMotion(character.velocity.y);
        IsAttacking = false;
        comboIndex = -1;
        forcedVictim = null;
        actArt = null;
        channeling = false;
        channelEndAt = -1f;
        animator.speed = 1f;
        if (!airSession && !airHopOnly) state.IsDisplacing = false;
        ClearActionEffects(); // // the summoned blade ends with the swing
        // Ult → rage: the weapon swaps to the big-sword chain for the burst's
        // window. A re-ult inside rage keeps the original pre-rage set.
        if (rageSec > 0f && rageWeaponSet != null && weaponSocket != null)
        {
            if (!raging) preRageSet = weaponSocket.Set;
            raging = true;
            rageT = rageSec;
            if (weaponSocket.Set != rageWeaponSet) weaponSocket.Equip(rageWeaponSet);
            GameHud.Toast("RAGE — BIG SWORD");
        }
        // Still airborne (jump attack): hand back to the air loop, not the
        // grounded blends — the controller's own transitions take the landing.
        // DiveLand already played under owned recovery; only now release it.
        var back = !wasGrounded ? JumpAirId
            : weaponSocket != null && weaponSocket.InCombat ? CombatMoveId : LocomotionId;
        animator.CrossFadeInFixedTime(back, wasDive ? 0.10f : fadeOut, 0);
    }

    /// <summary>Dodge-cancel hook: drops the swing's recovery so a dodge can start.</summary>
    public void Cancel()
    {
        ClearActionEffects();
        pending = null;
        EndAerialSession();
        if (!IsAttacking) { ReleaseBurstOwnership(); return; }
        ReleaseBurstOwnership();
        ReleasePlungeOwnership(Phase == PlungePhase.Land);
        ReleaseHitstop();
        IsAttacking = false;
        comboIndex = -1;
        forcedVictim = null;
        actArt = null;
        channeling = false;
        channelEndAt = -1f;
        animator.speed = 1f;
        state.IsDisplacing = false;
        ClearActionEffects(); //
    }

    /// <summary>Rage timer — real time so hitstop doesn't stretch the window.
    /// Expiry waits for the current swing to end before swapping back; a manual
    /// weapon change during rage is left alone.</summary>
    private void TickRage()
    {
        if (!raging) return;
        // A manual weapon change during rage ends it — whatever the player
        // swapped to stays equipped, no automatic restoration (detail §226).
        if (weaponSocket != null && weaponSocket.Set != rageWeaponSet)
        {
            raging = false;
            rageT = 0f;
            preRageSet = null;
            return;
        }
        // The 15s window pauses only while our own hitstop owns the clock —
        // menus and enemy freeze don't stretch it, but our freeze does.
        if (!ownsHitstop) rageT -= Time.unscaledDeltaTime;
        if (rageT > 0f || IsAttacking) return;
        EndRage();
    }

    private void EndRage()
    {
        raging = false;
        rageT = 0f;
        if (weaponSocket != null && weaponSocket.Set == rageWeaponSet)
            weaponSocket.Equip(preRageSet);
        preRageSet = null;
    }

    /// <summary>Planar direction the tracking step should move along: toward the
    /// locked target when locked, forward otherwise.</summary>
    private Vector3 PlanarToTarget()
    {
        if (lockOn != null && lockOn.IsLockedOn && lockOn.CurrentTarget != null)
            return Vector3.ProjectOnPlane(lockOn.CurrentTarget.transform.position - transform.position, Vector3.up);
        return Vector3.ProjectOnPlane(transform.forward, Vector3.up);
    }

    // ---------- Aim policies (detail.md §5) ----------
    private Vector3 aimBearing;
    private bool aimLocked;
    private AimPolicy actAim = AimPolicy.MovementLed;

    /// <summary>Compute the entry bearing per the move's policy and arm the
    /// capped anticipation turn — it runs during the windup at ≤360°/s and
    /// locks at first contact.</summary>
    private void BeginAim(AimPolicy policy)
    {
        actAim = policy;
        aimBearing = ResolveAim(policy);
        aimLocked = false;
    }

    private Vector3 ResolveAim(AimPolicy policy)
    {
        var move = MoveBearing();
        var locked = LockBearing();
        switch (policy)
        {
            case AimPolicy.TargetLed:
                return locked.sqrMagnitude > 0.001f ? locked
                    : move.sqrMagnitude > 0.001f ? move : transform.forward;
            case AimPolicy.LaunchSession:
            {
                if (airPrimary != null)
                {
                    var to = Vector3.ProjectOnPlane(airPrimary.transform.position - transform.position, Vector3.up);
                    if (to.sqrMagnitude > 0.001f) return to;
                }
                return locked.sqrMagnitude > 0.001f ? locked
                    : move.sqrMagnitude > 0.001f ? move : transform.forward;
            }
            default: // MovementLed — stick first, lock bearing is only the neutral fallback
                if (move.sqrMagnitude > 0.001f) return crowdAimAssist ? Magnetize(move, assistCone) : move;
                if (locked.sqrMagnitude > 0.001f) return locked;
                // Unlocked + neutral: the nearest enemy in front, else current facing.
                return crowdAimAssist ? Magnetize(transform.forward, 95f) : transform.forward;
        }
    }

    /// <summary>Crowd aim assist: bend <paramref name="dir"/> onto the nearest living
    /// enemy within <see cref="assistRange"/> and <paramref name="cone"/>° of it.
    /// Entry bearing only — the anticipation turn still caps at 360°/s and first
    /// contact locks the bearing, so it never re-targets mid-swing.</summary>
    private Vector3 Magnetize(Vector3 dir, float cone)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return transform.forward;
        var pool = candidates ?? FindObjectsByType<Targetable>(FindObjectsSortMode.None);
        Vector3 best = dir;
        var bestScore = float.MaxValue;
        foreach (var t in pool)
        {
            if (t == null || !t.IsTargetable) continue;
            var h = t.GetComponentInParent<Health>();
            if (h == null || h.IsDead || h.transform.IsChildOf(transform)) continue;
            var to = Vector3.ProjectOnPlane(h.transform.position - transform.position, Vector3.up);
            var d = to.magnitude;
            if (d < 0.05f || d > assistRange || Mathf.Abs(h.transform.position.y - transform.position.y) > 2.5f) continue;
            var angle = Vector3.Angle(dir, to);
            if (angle > cone * 0.5f) continue;
            // Prefer what the stick points at, then proximity.
            var score = d + angle * 0.05f;
            if (score < bestScore) { bestScore = score; best = to; }
        }
        return best;
    }

    /// <summary>Stick direction in camera space — deadzone 0.2 (detail §173).</summary>
    private Vector3 MoveBearing()
    {
        if (moveAction == null) return Vector3.zero;
        var move = moveAction.ReadValue<Vector2>();
        if (move.sqrMagnitude < 0.04f) return Vector3.zero;
        var cam = Camera.main;
        var fwd = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized : transform.forward;
        if (fwd.sqrMagnitude < 0.001f) fwd = transform.forward;
        var right = Vector3.Cross(Vector3.up, fwd);
        return fwd * move.y + right * move.x;
    }

    /// <summary>Planar bearing to the locked target — zero when unlocked.</summary>
    private Vector3 LockBearing()
    {
        if (lockOn == null || !lockOn.IsLockedOn || lockOn.CurrentTarget == null) return Vector3.zero;
        return Vector3.ProjectOnPlane(lockOn.CurrentTarget.transform.position - transform.position, Vector3.up);
    }

    private void Freeze(float seconds)
    {
        // One feedback event per rendered update; crowd contacts never stack
        // freezes. During a burst freeze the world is already slower — a
        // contact hitstop would just stomp the burst scale.
        if (seconds <= 0f || ownsHitstop || burstActive || Time.timeScale <= 0f) return;
        ownsHitstop = true; hitstopTimer = seconds; Time.timeScale = freezeTimeScale;
    }

    /// One contact of the current pass, collected before effects are applied —
    /// the launch wave orders by lock first then distance (detail §120), and
    /// the FX budget picks the three nearest the hero (detail §204).
    private sealed class Contact
    {
        public Health health;
        public EnemyAI enemy;
        public Vector3 point;
        public float dist;
        public bool locked;
        public bool boss;
    }
    private readonly System.Collections.Generic.List<Contact> sweepContacts = new(16);
    private readonly System.Collections.Generic.List<Contact> launchScratch = new(4);

    private int Sweep(bool emitFeedback = true, Vector3? contactOrigin = null)
    {
        var origin = contactOrigin ?? transform.position;
        var center = origin + Vector3.up * 1.0f;
        var lockTarget = lockOn != null && lockOn.IsLockedOn ? lockOn.CurrentTarget : null;

        // Collect eligible victims this pass (health-owner dedup, height band,
        // range/arc, solid-obstacle block) before any effect is applied.
        sweepContacts.Clear();
        foreach (var t in candidates)
        {
            if (t == null || !t.IsTargetable || (forcedVictim != null && t != forcedVictim)) continue;
            var health = t.GetComponentInParent<Health>();
            if (health == null || hitTargets.Contains(health)) continue;
            var contact = t.AimPosition;
            // Aim markers can sit above a large boss: intersect its body bounds at blade height.
            // Only an ENABLED solid collider: enemies keep their original CapsuleCollider
            // disabled (EnemyAI swaps in a CharacterController), and ClosestPoint on a
            // disabled collider returns the query point itself — surface distance 0, so
            // anything in the arc was hit from any range.
            Collider body = null;
            foreach (var c in health.GetComponents<Collider>())
                if (c != null && c.enabled && !c.isTrigger) { body = c; break; }
            if (body != null) contact = body.ClosestPoint(center);
            var to = Vector3.ProjectOnPlane(health.transform.position - origin, Vector3.up);
            var height = actDive ? 2f : actArt != null ? actArt.contactHeight : 1.6f;
            // Reach is measured to the body, not its pivot: unchanged for a normal
            // ~0.5m capsule, but a 1.3m-radius boss is hittable from blade range
            // instead of only from inside its legs. The bonus is capped at 2m so a bad
            // collider can never turn into unlimited reach.
            var surface = body != null ? Vector3.ProjectOnPlane(contact - origin, Vector3.up).magnitude : to.magnitude;
            var reachDist = Mathf.Max(Mathf.Min(to.magnitude, surface + 0.5f), to.magnitude - 2f);
            if (Mathf.Abs(contact.y - center.y) > height * 0.5f || reachDist > actRange) continue;
            var arc = actArt != null && actArt.contactMode == ArtContactMode.Spin ? 360f : actArc;
            if (Vector3.Angle(transform.forward, to) > arc * 0.5f) continue;
            var blocked = false;
            foreach (var obstacle in Physics.RaycastAll(center, contact - center, Vector3.Distance(center, contact),
                ~0, QueryTriggerInteraction.Ignore))
            {
                if (obstacle.transform.IsChildOf(transform) || obstacle.collider.GetComponentInParent<Health>() != null) continue;
                blocked = true; break;
            }
            if (blocked) continue;
            sweepContacts.Add(new Contact
            {
                health = health,
                enemy = health.GetComponent<EnemyAI>(),
                point = contact,
                dist = to.magnitude,
                locked = lockTarget != null && t == lockTarget,
                boss = health.GetComponent<IBossEngage>() != null,
            });
        }
        if (sweepContacts.Count == 0) return 0;

        // Nearest-first: the FX budget and the launch wave both prefer proximity.
        sweepContacts.Sort((a, b) => a.dist.CompareTo(b.dist));

        var contacts = 0;
        var firstContactThisAction = !actionContacted;
        actionContacted = true;
        var launchable = actArt != null && actArt.launch ? launchScratch : null;
        launchable?.Clear();
        for (var i = 0; i < sweepContacts.Count; i++)
        {
            var c = sweepContacts[i];
            var health = c.health;
            var backstab = forcedVictim != null;
            if (backstab && c.enemy != null) c.enemy.MarkStabKill(actDamage >= health.Current);
            hitTargets.Add(health);
            // Overdrive scales every greatsword contact (1 otherwise).
            health.TakeDamage(actDamage * (crimson != null ? crimson.DamageScale : 1f), origin,
                actPoise * (crimson != null ? crimson.PoiseScale : 1f), backstab ? DamageKind.Crit : actDamageKind,
                this, actReaction);
            ContactLanded?.Invoke(health, backstab ? DamageKind.Crit : actDamageKind, actDive);
            if(Application.isPlaying) SfxBank.Play("weapon.hit",health.transform.position+Vector3.up);
            contacts++;

            var enemy = c.enemy;
            if (enemy != null && actArt != null)
            {
                // Launcher eligibility: poise-vulnerable — a genuine break ON
                // THIS hit counts (OnDamaged stamps the frame before the refill
                // hides it). Living victims only, cap applied below.
                var vulnerable = enemy.PoiseVulnerable || enemy.LastPoiseBreakFrame == Time.frameCount;
                if (actArt.launch && vulnerable && !health.IsDead) launchable.Add(c);
                else if (actArt.spike && enemy.IsAirborne) enemy.Spike();
            }
            // Confirmed session contacts arrest the fall of struck victims —
            // suspension, never added height (detail §142).
            if (airSession && enemy != null && enemy.IsAirborne && !airStepSuspended)
                enemy.SuspendAir(Mathf.Min(strikeSuspend, sessionSuspendMax - airSuspendUsed));

            // Contact group FX: at most three full bursts, nearest the hero;
            // the rest get the lightweight spark only (detail §204).
            if (i < 3)
            {
                if (actArt != null && actArt.contactFx != null)
                    ArtFx.SpawnAt(actArt.contactFx, c.point, transform.rotation);
                else if (actArt == null && actionWeapon != null && actionWeapon.contactFx != null)
                    ArtFx.SpawnAt(actionWeapon.contactFx, c.point, transform.rotation);
            }
            var impactTint = actArt != null && actArt.visualTheme == ArtVisualTheme.DarkCrimson
                ? ArtFx.DarkCrimson
                : weaponSocket != null && weaponSocket.Set != null
                    ? weaponSocket.Set.impactTint : new Color(0.62f, 0.3f, 1f);
            HitFx.Spawn(c.point, transform.right, backstab ? 2f : 1f, impactTint);
            if (i < 3) GetComponent<TraversalEffects>()?.HitFlash(c.point, impactTint, transform.right,
                backstab ? 2.2f : actArt != null ? 1.4f : 1f);
        }

        // The launch wave: a valid locked contact first, then nearest (detail
        // §120). Victims past the cap keep the plain hit reaction; bosses
        // never enter this list (Launch no-ops on boss brains).
        if (launchable != null && launchable.Count > 0)
        {
            var li = launchable.FindIndex(c => c.locked);
            if (li > 0) { var tmp = launchable[li]; launchable.RemoveAt(li); launchable.Insert(0, tmp); }
            var cap = actArt.launchMax <= 0 ? int.MaxValue : actArt.launchMax;
            for (var i = 0; i < launchable.Count && i < cap; i++)
            {
                launchable[i].enemy.Launch(Vector3.up * launchRiseSpeed);
                if (!airSession && !airVictims.Contains(launchable[i].enemy)) airVictims.Add(launchable[i].enemy);
            }
        }

        // Hero suspension budget: one arrest per aerial step, 0.36s total.
        if (airSession && !airStepSuspended && airSuspendUsed < sessionSuspendMax)
        {
            airSuspendT = Mathf.Min(strikeSuspend, sessionSuspendMax - airSuspendUsed);
            airStepSuspended = true;
        }
        aimLocked = true; // first contact fixes the bearing for the move's remainder

        // The launcher promoted its successful victims into a session.
        if (!airSession && airVictims.Count > 0) BeginAerialSession();

        // Earned meter: once per successful action — normals +3, arts +4 —
        // never off the ultimate itself or while rage runs (detail §223).
        if (!actionCredited && ultMeter != null)
        {
            actionCredited = true;
            if (!raging && !(actArt != null && actArt.family == ActionFamily.Ultimate))
                ultMeter.Credit(ActionRevision, actArt != null ? 4f : 3f);
        }
        // Boss melee mana: plain swings against a boss pay 2 once per action
        // and at most once per 0.5s (detail §219).
        if (actArt == null && Time.time - lastBossManaT >= 0.5f && bossManaAction != ActionRevision)
        {
            for (var i = 0; i < sweepContacts.Count; i++)
                if (sweepContacts[i].boss)
                { bossManaAction = ActionRevision; lastBossManaT = Time.time; mana?.Restore(2f); break; }
        }

        if (emitFeedback && contacts > 0 && feedbackFrame != Time.frameCount)
        {
            feedbackFrame = Time.frameCount;
            cameraController?.Shake(actShake);
            // Spin feedback (detail §208): 0.02s on the first successful pass,
            // 0.05s on the final one, reactions only in between — and freezes
            // never stack across victims.
            var freeze = actFreeze;
            if (actArt != null && actArt.contactMode == ArtContactMode.Spin
                && actWindows != null && actWindows.Length > 0)
                freeze = lastWindow == actWindows.Length - 1 ? 0.05f
                    : firstContactThisAction ? 0.02f : 0f;
            Freeze(freeze);
        }
        return contacts;
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
