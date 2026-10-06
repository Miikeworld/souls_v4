using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Vess, the Warden — the final boss, three phases behind a fog gate.
///
///   P1 samurai          DrawSlash / TwinCut / HeavenCut / RushDraw (katana reads).
///   P2 "still in control" (HP ≤ phaseAt): precise swordsmanship and controlled
///        Crimson magic — Crimson Sweep (jump), King's Spear (side-step),
///        Executioner's Delay (wait, then dodge), Crown of Blades (short
///        levitation, six swords on a rhythm), Shattered Step (teleport + marked
///        dash), and once, Thousand-Blade Judgment (3 m up, sword rain).
///        The arena foreshadows: stones and dust rise, fragments orbit him.
///   P3 "the Core has taken control" (on the first death): he collapses, his
///        sword breaks, the Core beats three times, he rips a greatsword out of
///        the floor and the sanctum tears apart into floating wall-run slabs.
///        Heavy greatsword strings plus Twin Rupture, Crimson Flood, Ruinous
///        Sweep, King's Fall, Worldsplitter, Grave of Kings — and at finaleAt,
///        End of the Warden: the movement exam ending in an aerial strike on
///        the exposed Core.
///
/// Every dangerous attack reads body pose → colour/VFX tell → sound → attack;
/// a red floor marker is never the only warning. Crimson = damage here, purple =
/// the suit can use this, pale red = a two-frame impact peak.
/// Simple swings are Move-table entries with normalized hit windows (Attack mode,
/// authored XZ via the RootMotionRelay); signature attacks are coroutines
/// (Sequence mode) that own motion, levitation and hazards. ResetAll() rides
/// GameLoop's death/rest resets; a felled Warden stays felled.
/// </summary>
[RequireComponent(typeof(Health))]
public sealed class BossLord : MonoBehaviour, IRootMotionOwner, IBossEngage
{
    private enum Mode { Dormant, Chase, Attack, Roar, Staggered, Dodge, Sequence }

    private sealed class Move
    {
        public int id;
        public Vector2[] windows = System.Array.Empty<Vector2>();
        public float range, arc, damage;
        public float cooldown, nextAllowed;
        public float pickMin, pickMax, weight = 1f;
        public float trackAt;
        public int win;
        public bool effectPlayed, struck;
        public float sampledTime;
        // Warden additions
        public System.Func<IEnumerator> seq;      // signature attack — runs as a coroutine
        public float maxHeight = 3f;               // melee only connects below this (low sweeps are jumpable)
        public float impact, shake, hitstop;       // layered ground impact at the window
        public float ringRadius, ringDamage;       // a jumpable Core ring leaves the impact
        public float speed = 1f;                   // animator speed while it plays (P3 weight)
        public bool needsSanctum;
    }

    [Header("Identity")]
    [SerializeField] private string displayName = "Vess, Hollow Blade";
    [Tooltip("HP fraction where Phase 2 ('still in control') begins.")]
    [SerializeField, Range(0.1f, 0.9f)] private float phaseAt = 0.5f;
    [Tooltip("HP fraction restored when the Core takes control (Phase 3).")]
    [SerializeField, Range(0.1f, 0.9f)] private float reviveAt = 0.4f;
    [Tooltip("Phase 2: HP fraction of max where Thousand-Blade Judgment fires, once.")]
    [SerializeField, Range(0.02f, 0.45f)] private float judgmentAt = 0.22f;
    [Tooltip("Phase 3: HP fraction of max where End of the Warden begins.")]
    [SerializeField, Range(0.02f, 0.35f)] private float finaleAt = 0.12f;

    [Header("Movement")]
    [SerializeField, Min(0.5f)] private float walkSpeed = 2.2f;
    [SerializeField, Min(1f)] private float phase2Haste = 1.3f;
    [SerializeField, Min(1f)] private float phase3Haste = 1.1f;
    [SerializeField, Min(0.5f)] private float leashRange = 45f;
    [SerializeField, Min(60f)] private float turnSpeed = 320f;
    [SerializeField] private float gravity = -20f;
    [Tooltip("Phase 3 walk/turn multiplier — the greatsword makes every step heavy.")]
    [SerializeField, Range(0.3f, 1f)] private float heavyWalk = 0.72f;

    [Header("Shattered Step")]
    [SerializeField, Min(1f)] private float teleportRange = 3.5f;
    [SerializeField, Min(10f)] private float flankAngle = 75f;
    [SerializeField, Min(0f)] private float dodgeChance = 0.2f;
    [SerializeField, Min(0.5f)] private float dodgeSpeed = 2.6f;

    [Header("Poise")]
    [SerializeField, Min(0f)] private float poiseMax = 80f;
    [SerializeField, Min(0f)] private float poiseRegen = 8f;
    [SerializeField, Min(0f)] private float poiseRegenDelay = 1.8f;
    [SerializeField, Min(0.5f)] private float staggerTime = 1.6f;
    [Tooltip("Phase 3 poise — only a real break staggers the Core-driven body.")]
    [SerializeField, Min(0f)] private float corePoiseMax = 140f;

    [Header("Presentation")]
    [SerializeField] private Animator bossAnimator;
    [SerializeField] private FxCue roarFx;
    [SerializeField] private FxCue slamFx;
    [SerializeField] private FxCue reviveFx;
    [SerializeField] private FxCue teleportFx;

    [Header("Warden arena (Setup Warden Fight wires this)")]
    [SerializeField] private CoreSanctum sanctum;

    // ---------- animator states (BossLordBase.controller) ----------

    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int LocomotionId = Animator.StringToHash("Locomotion");
    private static readonly int LocomotionHeavyId = Animator.StringToHash("LocomotionHeavy");
    private static readonly int RoarId = Animator.StringToHash("Roar");
    private static readonly int StaggerId = Animator.StringToHash("Stagger");
    private static readonly int StaggerHeavyId = Animator.StringToHash("StaggerHeavy");
    private static readonly int DieId = Animator.StringToHash("Die");
    private static readonly int TeleportOutId = Animator.StringToHash("TeleportOut");
    private static readonly int TeleportInId = Animator.StringToHash("TeleportIn");
    private static readonly int DrawSlashId = Animator.StringToHash("DrawSlash");
    private static readonly int TwinCutId = Animator.StringToHash("TwinCut");
    private static readonly int HeavenCutId = Animator.StringToHash("HeavenCut");
    private static readonly int RushDrawId = Animator.StringToHash("RushDraw");
    private static readonly int CrimsonSweepId = Animator.StringToHash("CrimsonSweep");
    private static readonly int KingsSpearId = Animator.StringToHash("KingsSpear");
    private static readonly int ExecutionerId = Animator.StringToHash("Executioner");
    private static readonly int CrownRaiseId = Animator.StringToHash("CrownRaise");
    private static readonly int CrownHoldId = Animator.StringToHash("CrownHold");
    private static readonly int CrownLandId = Animator.StringToHash("CrownLand");
    private static readonly int HeavySweepId = Animator.StringToHash("HeavySweep");
    private static readonly int HeavySmashId = Animator.StringToHash("HeavySmash");
    private static readonly int HeavyComboId = Animator.StringToHash("HeavyCombo");
    private static readonly int TwinRuptureId = Animator.StringToHash("TwinRupture");
    private static readonly int CorePlantId = Animator.StringToHash("CorePlant");
    private static readonly int RuinousSweepId = Animator.StringToHash("RuinousSweep");
    private static readonly int KingsFallCrouchId = Animator.StringToHash("KingsFallCrouch");
    private static readonly int KingsFallHangId = Animator.StringToHash("KingsFallHang");
    private static readonly int KingsFallDropId = Animator.StringToHash("KingsFallDrop");
    private static readonly int KingsFallLandId = Animator.StringToHash("KingsFallLand");
    private static readonly int WorldsplitterId = Animator.StringToHash("Worldsplitter");
    private static readonly int CollapseId = Animator.StringToHash("Collapse");
    private static readonly int StandStruggleId = Animator.StringToHash("StandStruggle");
    private static readonly int GreatswordPullId = Animator.StringToHash("GreatswordPull");
    private static readonly int FinalFallId = Animator.StringToHash("FinalFall");
    private static readonly int DodgeLId = Animator.StringToHash("DodgeL");
    private static readonly int DodgeRId = Animator.StringToHash("DodgeR");

    /// <summary>When a Warden state is missing (setup not re-run) the nearest
    /// older state plays instead — timing falls back to each beat's timeout.</summary>
    private static int Fallback(int id)
    {
        if (id == CrimsonSweepId) return TwinCutId;
        if (id == KingsSpearId) return RushDrawId;
        if (id == ExecutionerId || id == HeavySmashId || id == TwinRuptureId || id == CorePlantId || id == WorldsplitterId) return HeavenCutId;
        if (id == HeavySweepId || id == HeavyComboId || id == RuinousSweepId) return TwinCutId;
        if (id == CrownRaiseId || id == CrownHoldId || id == CrownLandId || id == GreatswordPullId) return RoarId;
        if (id == LocomotionHeavyId) return LocomotionId;
        if (id == StaggerHeavyId || id == CollapseId || id == StandStruggleId) return StaggerId;
        if (id == KingsFallCrouchId || id == KingsFallHangId || id == KingsFallDropId || id == KingsFallLandId) return StaggerId;
        if (id == FinalFallId) return DieId;
        return 0;
    }

    private static readonly List<BossLord> all = new List<BossLord>();

    /// <summary>Any living, engaged Warden — WeaponSocket reads this beside BossGolem.AnyEngaged.</summary>
    public static bool AnyEngaged
    {
        get
        {
            for (var i = 0; i < all.Count; i++) if (all[i].Engaged) return true;
            return false;
        }
    }
    public bool Engaged => health != null && !health.IsDead && mode != Mode.Dormant;

    // Strikes and the scripted lunges consume authored XZ; teleports, levitation
    // and dodges are code-owned so the relay's gravity can't drag him down.
    public bool DriveRootMotion => (mode == Mode.Attack || seqRootMotion) && !levitating;
    public bool AllowRootY => false;

    // ---------- runtime ----------

    private Health health;
    private CharacterController cc;
    private Targetable targetable;
    private FootGrounding grounding;
    private WardenBody body;
    private Transform player;
    private PlayerHealth playerHealth;
    private PlayerState playerState;
    private PlayerLocomotion playerLoco;
    private AttackController playerAttack;
    private PlayerCameraController cam;
    private readonly List<Renderer> visuals = new List<Renderer>();

    private Mode mode = Mode.Dormant;
    private int phase;                 // 0 samurai, 1 in control, 2 Core
    private bool revived, roared, judgmentDone, judgmentPending, finaleDone, finalePending, defeated, finishing;
    private Move current;
    private int lastMoveId;
    private float modeT, poise, poiseWait, verticalSpeed, stepT;
    private int dodgeSide;
    private bool levitating, seqRootMotion, seqMotion;
    private float floorY;
    private Move[] p1Moves, p2Moves, p3Moves;
    private Move crown, step, spear, sweep, executioner, flood, ruinous, kingsFall, splitter, grave, twin;
    private AudioSource drone;
    private Light sun;
    private Color fog0;
    private float sun0 = -1f;

    private CoreSanctum Sanctum => sanctum != null ? sanctum : (sanctum = CoreSanctum.Active);
    private float BodyHeight => cc != null ? cc.height : 2.4f;
    private Vector3 Chest => body != null ? body.ChestPosition : transform.position + Vector3.up * 1.4f;
    private Vector3 ArenaCenter => Sanctum != null ? Sanctum.Center : home;
    private Vector3 home;

    private void OnEnable() => all.Add(this);
    private void OnDisable() => all.Remove(this);

    private void Awake()
    {
        health = GetComponent<Health>();
        health.Damaged += OnDamaged;
        health.Died += OnDied;
        cc = GetComponent<CharacterController>();
        if (cc != null) { ccHeight0 = cc.height; ccRadius0 = cc.radius; ccCenter0 = cc.center; }
        targetable = GetComponent<Targetable>();
        if (bossAnimator == null) bossAnimator = GetComponentInChildren<Animator>();
        if (bossAnimator != null)
        {
            health.SuppressDeathMotion = true;
            grounding = bossAnimator.GetComponent<FootGrounding>();
        }
        body = gameObject.AddComponent<WardenBody>();
        body.Init(bossAnimator);
        if (bossAnimator != null)
            foreach (var r in bossAnimator.GetComponentsInChildren<Renderer>(true))
                if (r.enabled && !(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer)) visuals.Add(r);
        poise = poiseMax;
        home = transform.position;
        BuildMoves();
        var p = FindFirstObjectByType<PlayerLocomotion>();
        if (p != null)
        {
            player = p.transform;
            playerLoco = p;
            playerHealth = p.GetComponent<PlayerHealth>();
            playerState = p.GetComponent<PlayerState>();
            playerAttack = p.GetComponent<AttackController>();
            cam = p.GetComponent<PlayerCameraController>();
        }
    }

    private void Start() => floorY = Sanctum != null ? Sanctum.Center.y : transform.position.y;

    private void OnDestroy()
    {
        if (health != null) { health.Damaged -= OnDamaged; health.Died -= OnDied; }
        WardenAudio.StopLoop(drone);
        Mood(0f);
    }

    // ================================================================== move tables

    private void BuildMoves()
    {
        // pickMin/pickMax = distance band; trackAt = last normalized point he still steers.
        var draw = new Move { id = DrawSlashId, range = 4f, arc = 150f, damage = 26f, cooldown = 1.6f,
                              pickMin = 0f, pickMax = 4.4f, weight = 1.2f, trackAt = 0.34f,
                              windows = new[] { new Vector2(0.40f, 0.56f) } };
        var twinCut = new Move { id = TwinCutId, range = 3.8f, arc = 150f, damage = 20f, cooldown = 2.2f,
                                 pickMin = 0f, pickMax = 4.2f, weight = 1.0f, trackAt = 0.26f,
                                 windows = new[] { new Vector2(0.28f, 0.40f), new Vector2(0.60f, 0.72f) } };
        var heaven = new Move { id = HeavenCutId, range = 4.2f, arc = 120f, damage = 38f, cooldown = 3.6f,
                                pickMin = 1.5f, pickMax = 4.6f, weight = 0.7f, trackAt = 0.42f,
                                windows = new[] { new Vector2(0.52f, 0.66f) } };
        var rush = new Move { id = RushDrawId, range = 4.5f, arc = 90f, damage = 30f, cooldown = 5f,
                              pickMin = 4.0f, pickMax = 10f, weight = 1.0f, trackAt = 0.30f,
                              windows = new[] { new Vector2(0.45f, 0.62f) } };
        p1Moves = new[] { draw, twinCut, heaven, rush };

        // P2 — still in control. Precise cuts stay; every new threat is a read.
        sweep = new Move { seq = CrimsonSweep, cooldown = 6f, pickMin = 0f, pickMax = 6f, weight = 1f };
        spear = new Move { seq = KingsSpear, cooldown = 7f, pickMin = 3f, pickMax = 15f, weight = 1f };
        executioner = new Move { seq = ExecutionersDelay, cooldown = 9f, pickMin = 0f, pickMax = 4.8f, weight = 0.8f };
        crown = new Move { seq = CrownOfBlades, cooldown = 16f, pickMin = 2.5f, pickMax = 22f, weight = 1f };
        step = new Move { seq = ShatteredStep, cooldown = 8f, pickMin = 2f, pickMax = 18f, weight = 0.8f };
        var draw2 = new Move { id = DrawSlashId, range = 4f, arc = 150f, damage = 26f, cooldown = 2f,
                               pickMin = 0f, pickMax = 4.4f, weight = 1.1f, trackAt = 0.34f,
                               windows = new[] { new Vector2(0.40f, 0.56f) } };
        var twin2 = new Move { id = TwinCutId, range = 3.8f, arc = 150f, damage = 20f, cooldown = 2.6f,
                               pickMin = 0f, pickMax = 4.2f, weight = 0.9f, trackAt = 0.26f,
                               windows = new[] { new Vector2(0.28f, 0.40f), new Vector2(0.60f, 0.72f) } };
        p2Moves = new[] { draw2, twin2, sweep, spear, executioner, crown, step };

        // P3 — the Core drives a body too heavy for it. Slow, huge, grounded.
        var heavySweep = new Move { id = HeavySweepId, range = 5.2f, arc = 200f, damage = 32f, cooldown = 2.6f,
                                    pickMin = 0f, pickMax = 5.2f, weight = 1.1f, trackAt = 0.36f, speed = 0.8f, shake = 0.12f,
                                    windows = new[] { new Vector2(0.44f, 0.60f) } };
        var heavySmash = new Move { id = HeavySmashId, range = 4.8f, arc = 70f, damage = 44f, cooldown = 3.8f,
                                    pickMin = 1.5f, pickMax = 5.6f, weight = 1f, trackAt = 0.45f, speed = 0.75f,
                                    impact = 1.6f, shake = 0.32f, hitstop = 0.06f, ringRadius = 9f, ringDamage = 16f,
                                    windows = new[] { new Vector2(0.55f, 0.68f) } };
        var heavyCombo = new Move { id = HeavyComboId, range = 4.6f, arc = 150f, damage = 24f, cooldown = 3.4f,
                                    pickMin = 0f, pickMax = 4.8f, weight = 0.9f, trackAt = 0.26f, speed = 0.8f, shake = 0.12f,
                                    windows = new[] { new Vector2(0.30f, 0.42f), new Vector2(0.62f, 0.75f) } };
        twin = new Move { seq = TwinRupture, cooldown = 9f, pickMin = 0f, pickMax = 8f, weight = 1f };
        flood = new Move { seq = CrimsonFlood, cooldown = 24f, pickMin = 0f, pickMax = 30f, weight = 0.9f, needsSanctum = true };
        ruinous = new Move { seq = RuinousSweep, cooldown = 17f, pickMin = 0f, pickMax = 12f, weight = 0.9f, needsSanctum = true };
        kingsFall = new Move { seq = KingsFall, cooldown = 14f, pickMin = 4f, pickMax = 28f, weight = 1f };
        splitter = new Move { seq = Worldsplitter, cooldown = 13f, pickMin = 3f, pickMax = 22f, weight = 1f };
        grave = new Move { seq = GraveOfKings, cooldown = 21f, pickMin = 0f, pickMax = 22f, weight = 0.8f };
        p3Moves = new[] { heavySweep, heavySmash, heavyCombo, twin, flood, ruinous, kingsFall, splitter, grave };
    }

    private Move[] PhaseMoves() => phase == 0 ? p1Moves : phase == 1 ? p2Moves : p3Moves;

    // ================================================================== engage / reset

    /// <summary>Fog gate / trigger calls this — the fight starts.</summary>
    public void Engage()
    {
        if (defeated || health.IsDead || mode != Mode.Dormant) return;
        mode = Mode.Chase;
        floorY = Sanctum != null ? Sanctum.Center.y : home.y;
        GameHud.Boss(health, displayName, phaseAt);
    }

    /// <summary>Souls reset: back to spawn, full HP, dormant, arena whole again.</summary>
    public void Respawn()
    {
        if (defeated) return;
        StopAllCoroutines();
        WardenHazard.ClearAll();
        WardenAudio.StopLoop(drone);
        drone = null;
        Mood(0f);
        EndSequenceState();
        health.Invulnerable = false;
        SetTangible(true);
        SetVisible(true);
        if (bossAnimator != null) bossAnimator.transform.localRotation = Quaternion.identity;
        health.ResetHealth();
        mode = Mode.Dormant;
        phase = 0;
        revived = roared = judgmentDone = judgmentPending = finaleDone = finalePending = finishing = false;
        current = null;
        poise = poiseMax;
        poiseWait = 0f;
        verticalSpeed = 0f;
        foreach (var m in p1Moves) m.nextAllowed = 0f;
        body.ResetAll();
        SetBodySize(1f);
        Sanctum?.ResetArena();
        if (bossAnimator != null) { AnimSpeed(1f); bossAnimator.Rebind(); }
        GameHud.BossClear();
    }

    /// <summary>Every Warden back to its spawn — called beside BossGolem.ResetAll.</summary>
    public static void ResetAll()
    {
        for (var i = 0; i < all.Count; i++) all[i].Respawn();
    }

    private void OnDied()
    {
        current = null;
        if (finishing)
        {
            defeated = true;
            SoulsWallet.Add(health.SoulsReward);
            GameHud.BossClear();
            return;
        }
        if (!revived)
        {
            // The kill "lands" — then the Core takes the body.
            revived = true;
            health.Revive(reviveAt);
            StopAllCoroutines();
            WardenHazard.ClearAll();
            EndSequenceState();
            StartSequence(PhaseThreeTransition());
            return;
        }
        defeated = true;
        StopAllCoroutines();
        WardenHazard.ClearAll();
        EndSequenceState();
        WardenAudio.StopLoop(drone);
        Mood(0f);
        SoulsWallet.Add(health.SoulsReward);
        GameHud.BossClear();
        GameHud.Banner("GREAT ENEMY FELLED", 3f);
        Play(DieId, 0.15f);
    }

    private void OnDamaged(float amount, Vector3 from, float poiseDamage, ReactionProfile reaction)
    {
        if (health.IsDead && !finishing) return;
        if (mode == Mode.Dormant) return;
        // The two set pieces can't be skipped by one big hit: hold the HP floor.
        if (phase == 1 && !judgmentDone && health.Current <= health.Max * judgmentAt)
        {
            judgmentPending = true;
            if (health.Current <= 0.5f) health.Revive(judgmentAt * 0.95f);
        }
        if (phase == 2 && !finaleDone && health.Current <= health.Max * finaleAt)
        {
            finalePending = true;
            health.Invulnerable = true;
            if (health.Current <= 0.5f) health.Revive(finaleAt * 0.95f);
        }
        if (mode == Mode.Sequence || mode == Mode.Dodge) return;
        // In control: sometimes he simply isn't there for the follow-up.
        if (phase == 1 && mode == Mode.Chase && Random.value < dodgeChance)
        {
            EnterDodge(from);
            return;
        }
        var max = phase == 2 ? corePoiseMax : poiseMax;
        if (max <= 0f) return;
        poise -= poiseDamage;
        poiseWait = poiseRegenDelay;
        if (poise > 0f || mode == Mode.Staggered) return;
        poise = max;
        mode = Mode.Staggered;
        modeT = 0f;
        current = null;
        Play(phase == 2 ? StaggerHeavyId : StaggerId, 0.12f);
        if (phase == 2) GameHud.Toast("POSTURE BROKEN");
    }

    // ================================================================== tick

    private void Update()
    {
        if (player == null || defeated) return;
        if (health.IsDead) return;
        if (playerHealth != null && playerHealth.IsDead)
        {
            if (mode != Mode.Dormant) Respawn();
            return;
        }
        if (mode == Mode.Dormant) return;

        var dt = Time.deltaTime;
        modeT += dt;
        var max = phase == 2 ? corePoiseMax : poiseMax;
        if (poiseWait > 0f) poiseWait -= dt;
        else if (poise < max) poise = Mathf.Min(max, poise + poiseRegen * dt);

        var toPlayer = player.position - transform.position;
        toPlayer.y = 0f;
        var dist = toPlayer.magnitude;
        if (dist > leashRange && mode != Mode.Sequence) { Respawn(); return; }

        if (phase == 1 && !inTransition)
        {
            // Foreshadowing scales with how far phase 2 has been pushed.
            var k = 1f - Mathf.Clamp01(health.Current / Mathf.Max(1f, health.Max * phaseAt));
            Sanctum?.Instability(0.25f + 0.75f * k);
            body.SetOrbit(0.2f + 0.8f * k);
        }

        // Set pieces interrupt whatever is running (never mid-transition).
        if (finalePending && !finaleDone && phase == 2 && !InTransition)
        {
            finalePending = false;
            Interrupt();
            StartSequence(EndOfTheWarden());
            return;
        }
        if (judgmentPending && !judgmentDone && phase == 1 && mode != Mode.Sequence)
        {
            judgmentPending = false;
            Interrupt();
            StartSequence(ThousandBladeJudgment());
            return;
        }

        var haste = phase == 1 ? phase2Haste : phase == 2 ? phase3Haste * heavyWalk : 1f;
        switch (mode)
        {
            case Mode.Chase:
                Face(toPlayer, dt, phase == 2 ? turnSpeed * 0.5f : turnSpeed);
                if (!roared && phase == 0 && health.Current <= health.Max * phaseAt) { EnterRoar(); break; }
                if (ReadyAny(dist))
                {
                    var move = PickMove(dist);
                    if (move != null) { EnterMove(move); break; }
                }
                SetSpeed(1f);
                Step(toPlayer.normalized * walkSpeed * haste * dt);
                if (phase == 2) HeavyStep(dt);
                break;

            case Mode.Attack:
                TickAttack(dt);
                break;

            case Mode.Dodge:
                if (DodgeDone()) { ToChase(); break; }
                Step(transform.right * dodgeSide * dodgeSpeed * dt);
                break;

            case Mode.Roar:
                if (bossAnimator == null || modeT >= 3f || StateDone(RoarId)) ToChase();
                ApplyGravity();
                break;

            case Mode.Staggered:
                if (modeT >= staggerTime * (phase == 2 ? 1.4f : 1f)) ToChase();
                ApplyGravity();
                break;

            case Mode.Sequence:
                if (!levitating && !seqMotion && !seqRootMotion && cc != null && cc.enabled) ApplyGravity();
                break;
        }
    }

    private bool inTransition;
    private bool InTransition => inTransition;

    private void LateUpdate()
    {
        // The Core-driven body never walks off the surviving platform.
        if (phase == 2 && Sanctum != null && cc != null && cc.enabled && !levitating)
        {
            var clamped = Sanctum.ClampToPlatform(transform.position, 0.9f);
            var push = clamped - transform.position;
            push.y = 0f;
            if (push.sqrMagnitude > 1e-6f) cc.Move(push);
        }
    }

    private void EnterRoar()
    {
        roared = true;
        phase = 1;
        mode = Mode.Roar;
        modeT = 0f;
        current = null;
        Play(RoarId, 0.2f);
        GameHud.Toast(displayName + " — SHATTERED");
        ArtFx.Spawn(roarFx, transform, bossAnimator, player);
        body.SetCore(WardenBody.CoreMode.Glimmer);
        body.Beat(1f);
        WardenFx.Shake(0.4f);
        WardenAudio.Play("thump", Chest, 0.8f, 1f);
        // The new reads open gradually, never all at once.
        var now = Time.time;
        spear.nextAllowed = now + 3f;
        sweep.nextAllowed = now + 4f;
        step.nextAllowed = now + 6f;
        executioner.nextAllowed = now + 8f;
        crown.nextAllowed = now + 10f;
    }

    /// <summary>P3 footfalls: every step lands — a puff and a dull thud, no shake.</summary>
    private void HeavyStep(float dt)
    {
        stepT -= dt;
        if (stepT > 0f) return;
        stepT = 0.62f;
        WardenFx.Dust(transform.position, 2, 0.7f, 0.55f);
        WardenAudio.Play("thud", transform.position, 0.22f, Random.Range(0.55f, 0.65f));
    }

    private bool ReadyAny(float dist)
    {
        foreach (var m in PhaseMoves())
            if (ReadyInBand(m, dist)) return true;
        return false;
    }

    private bool ReadyInBand(Move m, float dist)
        => Time.time >= m.nextAllowed && dist >= m.pickMin && dist <= PickReach(m) * 1.15f
           && (!m.needsSanctum || Sanctum != null);

    private static float PickReach(Move m) => m.pickMax > 0f ? m.pickMax : m.range;

    private Move PickMove(float dist)
    {
        var moves = PhaseMoves();
        var eligible = 0;
        var total = 0f;
        foreach (var m in moves) if (ReadyInBand(m, dist)) eligible++;
        foreach (var m in moves)
            if (ReadyInBand(m, dist) && !(eligible > 1 && Key(m) == lastMoveId)) total += m.weight;
        if (total <= 0f) return null;
        var roll = Random.value * total;
        foreach (var m in moves)
        {
            if (!ReadyInBand(m, dist) || (eligible > 1 && Key(m) == lastMoveId)) continue;
            roll -= m.weight;
            if (roll <= 0f) return m;
        }
        return null;
    }

    private static int Key(Move m) => m.seq != null ? m.seq.Method.Name.GetHashCode() : m.id;

    private void EnterMove(Move m)
    {
        current = m;
        lastMoveId = Key(m);
        m.nextAllowed = Time.time + m.cooldown;
        SetSpeed(0f);
        if (m.seq != null)
        {
            StartSequence(m.seq());
            return;
        }
        m.win = 0;
        m.struck = false;
        m.effectPlayed = false;
        m.sampledTime = 0f;
        mode = Mode.Attack;
        modeT = 0f;
        Play(m.id, 0.15f, m.speed);
    }

    private void EnterDodge(Vector3 from)
    {
        mode = Mode.Dodge;
        modeT = 0f;
        current = null;
        dodgeSide = transform.InverseTransformDirection(from - transform.position).x < 0f ? 1 : -1;
        Play(dodgeSide > 0 ? DodgeRId : DodgeLId, 0.1f, 1.2f);
    }

    private bool DodgeDone()
    {
        if (bossAnimator == null) return modeT >= 0.8f;
        var info = bossAnimator.GetCurrentAnimatorStateInfo(0);
        var id = dodgeSide > 0 ? DodgeRId : DodgeLId;
        if (info.shortNameHash != id) return modeT >= 1.6f;
        return info.normalizedTime >= 1f || modeT >= 1.6f;
    }

    private void TickAttack(float dt)
    {
        var m = current;
        if (m == null || bossAnimator == null) { ToChase(); return; }
        var info = bossAnimator.GetCurrentAnimatorStateInfo(0);
        if (info.shortNameHash != m.id && info.shortNameHash != Fallback(m.id))
        {
            if (modeT > 4f) { ToChase(); current = null; }
            return;
        }
        var nt = info.normalizedTime;
        var trackEnd = m.trackAt > 0f ? m.trackAt : (m.windows.Length > 0 ? m.windows[0].x : 1f);
        if (nt < trackEnd)
        {
            var to = player.position - transform.position;
            to.y = 0f;
            Face(to.normalized, dt, phase == 2 ? turnSpeed * 0.5f : turnSpeed);
        }
        while (m.win < m.windows.Length && nt >= m.windows[m.win].x)
        {
            if (!m.struck && m.sampledTime <= m.windows[m.win].y)
            {
                m.struck = Strike(m, !m.effectPlayed);
                m.effectPlayed = true;
            }
            if (nt <= m.windows[m.win].y) break;
            m.win++;
            m.struck = false;
            m.effectPlayed = false;
        }
        m.sampledTime = nt;
        if (nt >= 1f)
        {
            ToChase();
            current = null;
        }
    }

    private void ToChase()
    {
        mode = Mode.Chase;
        modeT = 0f;
        if (bossAnimator == null) return;
        AnimSpeed(1f);
        var loco = phase == 2 && bossAnimator.HasState(0, LocomotionHeavyId) ? LocomotionHeavyId : LocomotionId;
        if (bossAnimator.GetCurrentAnimatorStateInfo(0).shortNameHash != loco)
            bossAnimator.CrossFadeInFixedTime(loco, 0.3f, 0);
    }

    /// <summary>Arc sweep for table moves, plus the impact language (flash →
    /// debris → dust → cracks, shake on contact) and an optional jumpable ring.</summary>
    private bool Strike(Move m, bool present)
    {
        if (playerHealth == null) return false;
        var impactPoint = transform.position + transform.forward * Mathf.Min(m.range * 0.6f, 3.5f);
        if (present)
        {
            if (m.impact > 0f)
            {
                WardenFx.Impact(impactPoint, m.impact, m.shake, m.hitstop);
                WardenAudio.Play("slam", impactPoint, 0.8f, 0.8f);
            }
            else if (m.shake > 0f) WardenFx.Shake(m.shake);
            if (phase == 2 && m.impact <= 0f) WardenAudio.Play("swish", impactPoint, 0.5f, 0.7f);
            if (m.ringRadius > 0f)
            {
                GroundWave.Spawn(impactPoint, transform.forward, 360f, m.ringRadius, 10f, 0.9f, m.ringDamage);
                WardenAudio.Play("pulse", impactPoint, 0.8f, 1f);
            }
        }
        var to = player.position - transform.position;
        var h = to.y;
        to.y = 0f;
        var hit = to.magnitude <= m.range && Vector3.Angle(transform.forward, to) <= m.arc * 0.5f
                  && h > -2f && h < m.maxHeight;
        return hit && WardenHazard.Damage(m.damage, transform.position);
    }

    // ================================================================== sequence plumbing

    private void StartSequence(IEnumerator routine)
    {
        mode = Mode.Sequence;
        modeT = 0f;
        current = null;
        SetSpeed(0f);
        StartCoroutine(RunSequence(routine));
    }

    private IEnumerator RunSequence(IEnumerator routine)
    {
        yield return routine;
        EndSequenceState();
        if (mode == Mode.Sequence && !health.IsDead && !defeated) ToChase();
    }

    /// <summary>Clears every flag a sequence may have left set.</summary>
    private void EndSequenceState()
    {
        if (levitating) EndLevitate();
        seqRootMotion = seqMotion = false;
        inTransition = false;
        if (bossAnimator != null)
        {
            AnimSpeed(1f);
            bossAnimator.transform.localRotation = Quaternion.identity;
        }
        if (!finaleDone || defeated) health.Invulnerable = false;
        if (!health.IsDead) SetTangible(true);
        SetVisible(true);
        body.Charge(0f);
        body.ClearCountdown();
    }

    private void AnimSpeed(float s)
    {
        if (bossAnimator != null) bossAnimator.speed = s;
    }

    /// <summary>Drop whatever is running (sequence, hazards, flags) — set pieces take over.</summary>
    private void Interrupt()
    {
        StopAllCoroutines();
        WardenHazard.ClearAll();
        EndSequenceState();
        Sanctum?.SetFlood(0f);
        Sanctum?.ChargeWalls(0f);
    }

    private int Play(int id, float fade = 0.15f, float speed = 1f)
    {
        if (bossAnimator == null) return 0;
        bossAnimator.speed = speed;
        if (!bossAnimator.HasState(0, id)) id = Fallback(id);
        if (id == 0 || !bossAnimator.HasState(0, id)) return 0;
        bossAnimator.CrossFadeInFixedTime(id, fade, 0);
        return id;
    }

    private float StateTime(int id)
    {
        if (bossAnimator == null || id == 0) return -1f;
        var info = bossAnimator.GetCurrentAnimatorStateInfo(0);
        return info.shortNameHash == id ? info.normalizedTime : -1f;
    }

    /// <summary>Wait until <paramref name="id"/> reaches normalized time <paramref name="nt"/>
    /// (timeout covers missing states), optionally steering toward the player.</summary>
    private IEnumerator Until(int id, float nt, float timeout, float trackDeg = 0f)
    {
        var t = 0f;
        while (t < timeout && StateTime(id) < nt)
        {
            t += Time.deltaTime;
            if (trackDeg > 0f) Face(ToPlayerFlat, Time.deltaTime, trackDeg);
            yield return null;
        }
    }

    private IEnumerator Wait(float seconds, float trackDeg = 0f)
    {
        var t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            if (trackDeg > 0f) Face(ToPlayerFlat, Time.deltaTime, trackDeg);
            yield return null;
        }
    }

    private Vector3 ToPlayerFlat => player != null ? FlatDir(player.position - transform.position) : FlatDir(transform.forward);
    private float PlayerDistance => player != null ? Vector3.ProjectOnPlane(player.position - transform.position, Vector3.up).magnitude : 99f;

    private void BeginLevitate()
    {
        levitating = true;
        if (grounding != null) grounding.enabled = false;
    }

    private void EndLevitate()
    {
        levitating = false;
        verticalSpeed = 0f;
        if (grounding != null) grounding.enabled = true;
    }

    private void SetY(float y)
    {
        var p = transform.position;
        if (cc != null && cc.enabled) cc.Move(new Vector3(0f, y - p.y, 0f));
        else transform.position = new Vector3(p.x, y, p.z);
    }

    private void MoveFlat(Vector3 delta)
    {
        delta.y = 0f;
        if (cc != null && cc.enabled) cc.Move(delta);
        else transform.position += delta;
    }

    private IEnumerator LiftTo(float height, float seconds, bool violent = false)
    {
        BeginLevitate();
        var from = transform.position.y;
        var to = floorY + height;
        var t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            var k = Mathf.Clamp01(t / seconds);
            var e = violent ? 1f - Mathf.Pow(1f - k, 4f) : k * k * (3f - 2f * k);
            SetY(Mathf.Lerp(from, to, e));
            yield return null;
        }
        SetY(to);
    }

    private IEnumerator Descend(float seconds, bool puff = true)
    {
        var from = transform.position.y;
        var t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            var k = Mathf.Clamp01(t / seconds);
            SetY(Mathf.Lerp(from, floorY, k * k));
            yield return null;
        }
        SetY(floorY);
        EndLevitate();
        if (puff)
        {
            WardenFx.Dust(transform.position, 5, 1.3f, 0.9f);
            WardenAudio.Play("thud", transform.position, 0.45f, 0.8f);
        }
    }

    private float ccHeight0, ccRadius0, bodySize = 1f;
    private Vector3 ccCenter0;

    /// <summary>The Forsaken form is larger: visual and capsule scale together, feet fixed.</summary>
    private void SetBodySize(float k)
    {
        bodySize = k;
        body.SetScale(k);
        if (cc == null || ccHeight0 <= 0f) return;
        cc.height = ccHeight0 * k;
        cc.radius = ccRadius0 * k;
        cc.center = new Vector3(ccCenter0.x, ccCenter0.y + (cc.height - ccHeight0) * 0.5f, ccCenter0.z);
    }

    private IEnumerator GrowTo(float k, float seconds)
    {
        var from = bodySize;
        var t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            var e = Mathf.SmoothStep(0f, 1f, t / seconds);
            SetBodySize(Mathf.Lerp(from, k, e));
            yield return null;
        }
        SetBodySize(k);
    }

    private void SetVisible(bool on)
    {
        foreach (var r in visuals) if (r != null) r.enabled = on;
    }

    private void SetTangible(bool on)
    {
        if (cc != null) cc.enabled = on;
        if (targetable != null) targetable.enabled = on;
    }

    /// <summary>Melee check from the boss: range, arc, height band. True when it landed.</summary>
    private bool MeleeHit(float range, float arc, float damage, float maxHeight = 3f, Vector3? origin = null)
    {
        if (player == null) return false;
        var o = origin ?? transform.position;
        var to = player.position - o;
        var h = player.position.y - floorY;
        to.y = 0f;
        if (to.magnitude > range || h > maxHeight) return false;
        if (arc < 359f && Vector3.Angle(transform.forward, to) > arc * 0.5f) return false;
        return WardenHazard.Damage(damage, o);
    }

    /// <summary>Where a planted / swung blade meets the floor in front of him.</summary>
    private Vector3 FrontPoint(float forward) => new Vector3(transform.position.x, floorY, transform.position.z) + FlatDir(transform.forward) * forward;

    private Vector3 FloorPoint(Vector3 p) => new Vector3(p.x, floorY, p.z);

    /// <summary>Teleport: shatter into shards, gone, reform at <paramref name="pos"/>.</summary>
    private IEnumerator BlinkTo(Vector3 pos, Vector3 face)
    {
        Play(TeleportOutId, 0.1f, 1.3f);
        yield return Wait(0.18f);
        WardenFx.Shards(Chest, 26, 4.5f, WardenFx.Crimson, false, 1.3f, 0.6f, null, 0.7f);
        WardenFx.Shards(Chest, 10, 1.5f, WardenFx.Crimson, true, 1f, 0.9f, null, 0.5f);
        WardenAudio.Play("shatter", Chest, 0.7f, 1.2f);
        ArtFx.Spawn(teleportFx, transform, bossAnimator, player);
        SetVisible(false);
        SetTangible(false);
        yield return Wait(0.3f);
        if (Sanctum != null) pos = Sanctum.ClampToPlatform(pos, 1.2f);
        transform.SetPositionAndRotation(FloorPoint(pos), Quaternion.LookRotation(FlatDir(face), Vector3.up));
        WardenFx.Converge(Chest, 22, 1.3f, 0.16f, WardenFx.Crimson);
        yield return Wait(0.16f);
        SetTangible(true);
        SetVisible(true);
        WardenFx.Shards(Chest, 10, 2.5f, WardenFx.Crimson, false, 1f, 0.4f);
        Play(TeleportInId, 0.06f, 1.3f);
    }

    // ================================================================== Phase 2 — "still in control"

    /// <summary>Sword low, body twisting; a thin red arc across the floor; then
    /// a low sweep and a red floor wave. Jump it.</summary>
    private IEnumerator CrimsonSweep()
    {
        var id = Play(CrimsonSweepId, 0.15f, 0.85f);
        yield return Until(id, 0.24f, 0.9f, 260f);
        AnimSpeed(0.04f);
        var arcPts = new List<Vector3>();
        for (var i = 0; i <= 14; i++)
        {
            var d = Quaternion.AngleAxis(-85f + 170f * i / 14f, Vector3.up) * FlatDir(transform.forward);
            arcPts.Add(FloorPoint(transform.position) + d * 3.4f + Vector3.up * 0.04f);
        }
        WardenFx.Line(arcPts, WardenFx.Crimson, 0.07f, 0.75f, 0.12f, 0.7f);
        WardenAudio.Play("scrape", transform.position, 0.35f, 1.4f);
        WardenFx.Shards(FrontPoint(1.4f) + Vector3.up * 0.2f, 6, 1.2f, WardenFx.Crimson, false, 0.8f, 0.4f);
        yield return Wait(0.38f);
        AnimSpeed(1.15f);
        yield return Until(id, 0.34f, 0.6f);
        WardenAudio.Play("slash", transform.position, 0.9f, 0.85f);
        MeleeHit(4.2f, 175f, 24f, 1.0f);
        GroundWave.Spawn(FloorPoint(transform.position), transform.forward, 170f, 9.5f, 13f, 0.85f, 16f, false, 1.2f);
        WardenFx.Shards(FrontPoint(2f) + Vector3.up * 0.15f, 14, 4f, WardenFx.Crimson, false, 0.9f, 0.45f, transform.right);
        WardenFx.Shake(0.12f);
        yield return Until(id, 0.95f, 1.6f);
    }

    /// <summary>Sword drawn back to the hip in a vertical stance; a narrow strip
    /// marks the line; the thrust sends a thin blade-wave down it. Side-step.</summary>
    private IEnumerator KingsSpear()
    {
        var id = Play(KingsSpearId, 0.15f, 0.75f);
        var mark = WardenMark.Line(transform.position, transform.position + FlatDir(transform.forward) * 15f, 0.38f, WardenFx.Crimson, 0.05f, 0.14f);
        var t = 0f;
        while (t < 1.2f && StateTime(id) < 0.32f)
        {
            t += Time.deltaTime;
            Face(ToPlayerFlat, Time.deltaTime, 200f);
            var o = FloorPoint(transform.position);
            mark.SetLine(o, o + FlatDir(transform.forward) * 15f, 0.38f);
            mark.SetPulse(t / 1.2f);
            yield return null;
        }
        // Aim locked.
        mark.SetPulse(1f);
        WardenAudio.Play("chime", Chest, 0.4f, 0.6f);
        AnimSpeed(0.04f);
        yield return Wait(0.22f);
        AnimSpeed(1.2f);
        yield return Until(id, 0.40f, 0.5f);
        mark.Release(0.25f);
        var origin = FloorPoint(transform.position);
        var dir = FlatDir(transform.forward);
        BladeWave.Spawn(origin + dir * 0.8f, dir, 18f, 26f, 0.7f, 2.2f, 20f);
        WardenAudio.Play("slash", transform.position, 1f, 1.2f);
        seqRootMotion = true;
        var hitDone = false;
        while (StateTime(id) < 0.68f && t < 3f)
        {
            t += Time.deltaTime;
            if (!hitDone) hitDone = MeleeHit(3.6f, 50f, 26f, 2.4f);
            yield return null;
        }
        seqRootMotion = false;
        yield return Until(id, 0.95f, 1.5f);
    }

    /// <summary>Sword raised overhead and HELD while the Core brightens and the hum
    /// rises — variable hold. Wait for it, then dodge the release.</summary>
    private IEnumerator ExecutionersDelay()
    {
        var id = Play(ExecutionerId, 0.15f, 1f);
        yield return Until(id, 0.38f, 1.2f, 280f);
        AnimSpeed(0.02f);
        var hold = Random.Range(0.65f, 1.55f);
        WardenAudio.Play("hum", Chest, 0.8f, 1.6f / hold);
        var reach = WardenMark.Line(FloorPoint(transform.position), FrontPoint(4.8f), 0.9f, WardenFx.Crimson, 0.04f, 0.08f);
        var t = 0f;
        while (t < hold)
        {
            t += Time.deltaTime;
            var k = t / hold;
            body.Charge(k);
            reach.SetAlpha(0.4f + 0.6f * k);
            Face(ToPlayerFlat, Time.deltaTime, 50f);
            reach.SetLine(FloorPoint(transform.position), FrontPoint(4.8f), 0.9f);
            yield return null;
        }
        reach.Release(0.1f);
        AnimSpeed(1.8f);
        yield return Until(id, 0.47f, 0.4f);
        body.Charge(0f);
        body.Beat(1f);
        var p = FrontPoint(2.2f);
        WardenFx.Impact(p, 1.3f, 0.28f, 0.05f, 7, 0.8f, 1f);
        WardenAudio.Play("slam", p, 1f, 0.9f);
        MeleeHit(4.6f, 75f, 38f, 3f);
        AnimSpeed(1f);
        yield return Until(id, 0.95f, 1.8f);
    }

    /// <summary>He stops, raises an empty hand and lifts half a metre; six swords
    /// form behind him one chime at a time, then fire on a rhythm — 1 · 2 · 3-4 ·
    /// 5 · … 6 (the late sixth punishes six panic rolls). Then he drops, heavily.</summary>
    private IEnumerator CrownOfBlades()
    {
        var id = Play(CrownRaiseId, 0.2f, 1f);
        yield return Wait(0.35f, 200f);
        Play(CrownHoldId, 0.25f, 1f);
        body.SetCore(WardenBody.CoreMode.Glimmer);
        body.Charge(0.35f);
        WardenFx.Dust(transform.position, 6, 0.6f, 0.8f, null, 1.8f);
        WardenFx.Shards(transform.position + Vector3.up * 0.1f, 8, 0.6f, WardenFx.StoneCol, true, 1.8f, 2.2f, null, 1.2f);
        yield return LiftTo(0.6f, 0.6f);

        var blades = new SpectralBlade[6];
        for (var i = 0; i < blades.Length; i++)
        {
            blades[i] = SpectralBlade.Spawn(CrownSlot(i), CrownTilt(i), 1f, transform, 0.2f, true, 0.9f + i * 0.09f);
            var t = 0f;
            while (t < 0.22f) { t += Time.deltaTime; HoldCrown(blades, -1); yield return null; }
        }
        var lull = 0f;
        while (lull < 0.45f) { lull += Time.deltaTime; HoldCrown(blades, -1); yield return null; }

        float[] gap = { 0f, 0.75f, 0.75f, 0.22f, 0.75f, 1.25f };
        for (var i = 0; i < blades.Length; i++)
        {
            var t = 0f;
            while (t < gap[i]) { t += Time.deltaTime; HoldCrown(blades, -1); yield return null; }
            // Tell: the blade turns to you and swells; its path flashes on the floor.
            var b = blades[i];
            t = 0f;
            while (t < 0.3f && b != null)
            {
                t += Time.deltaTime;
                HoldCrown(blades, i);
                b.TurnToward(WardenHazard.Chest, 900f);
                b.SetGlow(t / 0.3f);
                yield return null;
            }
            if (b == null) continue;
            var aim = WardenHazard.Chest;
            var dir = (aim - b.Tip).normalized;
            var g0 = FloorPoint(b.Tip);
            WardenFx.Line(new[] { g0 + Vector3.up * 0.05f, FloorPoint(aim) + FlatDir(dir) * 4f + Vector3.up * 0.05f },
                          WardenFx.Crimson, 0.05f, 0.35f, 0.05f, 0.5f);
            b.Fire(dir, 30f, 16f, 34f);
        }
        yield return Wait(0.3f);
        Play(CrownLandId, 0.2f, 1f);
        yield return Descend(0.45f);
        body.Charge(0f);
        // Punish window: he settles from the hover.
        yield return Wait(1.3f);
    }

    private Vector3 CrownSlot(int i)
    {
        var fwd = FlatDir(transform.forward);
        var right = Vector3.Cross(Vector3.up, fwd);
        var a = Mathf.Lerp(-70f, 70f, i / 5f) * Mathf.Deg2Rad;
        var centre = Chest + Vector3.up * 0.75f - fwd * 0.55f;
        return centre + (right * Mathf.Sin(a) + Vector3.up * Mathf.Cos(a)) * 1.5f;
    }

    private Quaternion CrownTilt(int i)
    {
        var fwd = FlatDir(transform.forward);
        var right = Vector3.Cross(Vector3.up, fwd);
        var a = Mathf.Lerp(-70f, 70f, i / 5f) * Mathf.Deg2Rad;
        return Quaternion.FromToRotation(Vector3.up, (right * Mathf.Sin(a) + Vector3.up * Mathf.Cos(a)).normalized);
    }

    /// <summary>Unfired crown blades ride their slots while he slowly turns.</summary>
    private void HoldCrown(SpectralBlade[] blades, int aiming)
    {
        Face(ToPlayerFlat, Time.deltaTime, 90f);
        for (var j = 0; j < blades.Length; j++)
        {
            var b = blades[j];
            if (b == null || b.State == SpectralBlade.Phase.Flying || b.State == SpectralBlade.Phase.Stuck
                || b.State == SpectralBlade.Phase.Dissolving) continue;
            b.transform.position = Vector3.Lerp(b.transform.position, CrownSlot(j), 1f - Mathf.Exp(-14f * Time.deltaTime));
            if (j != aiming) b.transform.rotation = Quaternion.Slerp(b.transform.rotation, CrownTilt(j), 1f - Mathf.Exp(-10f * Time.deltaTime));
        }
    }

    /// <summary>His body fractures into shards; a red line marks where he'll come
    /// back through you. He reforms at its end and dashes the line — time the dodge.</summary>
    private IEnumerator ShatteredStep()
    {
        Play(TeleportOutId, 0.1f, 1.3f);
        yield return Wait(0.16f);
        WardenFx.Shards(Chest, 28, 4.5f, WardenFx.Crimson, false, 1.3f, 0.6f, null, 0.7f);
        WardenAudio.Play("shatter", Chest, 0.7f, 1.2f);
        ArtFx.Spawn(teleportFx, transform, bossAnimator, player);
        SetVisible(false);
        SetTangible(false);

        // Reappear on a flank, a dash-length away, aimed through where you stand now.
        var target = FloorPoint(player.position);
        var ang = Random.value < 0.5f ? -flankAngle : flankAngle;
        var away = Quaternion.AngleAxis(ang, Vector3.up) * -FlatDir(player.forward);
        var start = target + away * (teleportRange + 3.5f);
        if (Sanctum != null) start = Sanctum.ClampToPlatform(start, 1.2f);
        var dir = FlatDir(target - start);
        var len = Vector3.Distance(FloorPoint(start), target) + 4f;
        var mark = WardenMark.Line(start, start + dir * len, 0.95f, WardenFx.Crimson, 0.07f, 0.18f);
        WardenAudio.Play("crack", start, 0.6f, 1.2f);
        var t = 0f;
        while (t < 0.65f) { t += Time.deltaTime; mark.SetPulse(t / 0.65f); yield return null; }

        transform.SetPositionAndRotation(FloorPoint(start), Quaternion.LookRotation(dir, Vector3.up));
        SetTangible(true);
        SetVisible(true);
        WardenFx.Shards(Chest, 12, 2.5f, WardenFx.Crimson, false, 1f, 0.4f);
        WardenFx.Peak(Chest, 0.5f);
        mark.Release(0.3f);
        Play(RushDrawId, 0.05f, 1.35f);
        WardenAudio.Play("slash", transform.position, 1f, 1.1f);
        seqMotion = true;
        var travelled = 0f;
        var hit = false;
        while (travelled < len - 1f)
        {
            var stepLen = Mathf.Min(17f * Time.deltaTime, len - 1f - travelled);
            var before = transform.position;
            MoveFlat(dir * stepLen);
            travelled += stepLen;
            if (!hit && WardenHazard.SegmentToBody(before + Vector3.up * 1f, transform.position + Vector3.up * 1f) < 1.2f)
            {
                hit = true;
                WardenHazard.Damage(28f, transform.position);
            }
            if (Random.value < 0.5f) WardenFx.Shards(Chest, 1, 0.5f, WardenFx.Crimson, false, 0.8f, 0.35f);
            ApplyGravity();
            yield return null;
        }
        seqMotion = false;
        yield return Until(RushDrawId, 0.9f, 1.2f);
    }

    /// <summary>The Phase 2 ultimate: arms spread, three metres up, his sword hanging
    /// below him; dozens of blades overhead; each strike's circle shows 0.75s ahead.
    /// Small strikes stay small — only the last giant blade gets the full impact.</summary>
    private IEnumerator ThousandBladeJudgment()
    {
        judgmentDone = true;
        var centre = ArenaCenter;
        if (Vector3.ProjectOnPlane(transform.position - centre, Vector3.up).magnitude > 2f)
            yield return BlinkTo(centre, ToPlayerFlat);
        Play(CrownHoldId, 0.3f, 1f);
        body.FloatSword(true);
        body.Charge(0.5f);
        GameHud.Toast("THOUSAND-BLADE JUDGMENT");
        WardenAudio.Play("hum", Chest, 0.9f, 1f);
        StartCoroutine(MoodTo(1f, 1.2f));
        yield return LiftTo(3f, 1.6f);

        // The host: a slow halo of blades overhead.
        var halo = new List<SpectralBlade>();
        for (var i = 0; i < 24; i++)
        {
            var a = i * Mathf.PI * 2f / 24f;
            var r = 3f + (i % 3) * 3.5f;
            var p = centre + new Vector3(Mathf.Cos(a) * r, 11f + (i % 4) * 0.8f, Mathf.Sin(a) * r);
            halo.Add(SpectralBlade.Spawn(p, Quaternion.FromToRotation(Vector3.up, Vector3.down), 1.1f, transform, 0.25f, i % 4 == 0, 0.7f + (i % 5) * 0.05f));
            if (i % 2 == 0) yield return Wait(0.05f);
        }

        var rain = 0f;
        var next = 0f;
        while (rain < 5.4f)
        {
            rain += Time.deltaTime;
            Face(ToPlayerFlat, Time.deltaTime, 60f);
            SpinHalo(halo, centre);
            if (rain >= next)
            {
                next = rain + 0.15f;
                Vector3 g;
                if (Random.value < 0.35f)
                {
                    var lead = playerLoco != null ? Vector3.ProjectOnPlane(playerLoco.ActualPlanarVelocity, Vector3.up) * 0.4f : Vector3.zero;
                    var r2 = Random.insideUnitCircle * 1.3f;
                    g = WardenHazard.Feet + lead + new Vector3(r2.x, 0f, r2.y);
                }
                else g = Sanctum != null ? Sanctum.RandomFloorPoint() : centre + Random.insideUnitSphere * 10f;
                if (WardenHazard.FloorAt(g + Vector3.up * 2f, 6f, out var floor))
                    RainStrike.Spawn(floor, 0.75f, 1.25f, 14f, 1f, transform);
            }
            yield return null;
        }

        // The final, giant sword — it gets the big read and the big impact.
        var giantAt = WardenHazard.FloorAt(WardenHazard.Feet + Vector3.up * 2f, 6f, out var gf) ? gf : FloorPoint(WardenHazard.Feet);
        WardenAudio.Play("hum", giantAt, 1f, 0.9f);
        RainStrike.Spawn(giantAt, 1.5f, 4.2f, 42f, 4f, transform, big: true);
        var w = 0f;
        while (w < 1.6f) { w += Time.deltaTime; SpinHalo(halo, centre); yield return null; }
        foreach (var b in halo) if (b != null) b.Dissolve(0.35f);
        yield return Wait(0.5f);
        StartCoroutine(MoodTo(0f, 1f));
        body.Charge(0f);
        yield return Descend(0.8f);
        body.FloatSword(false);
        Play(CrownLandId, 0.2f, 1f);
        yield return Wait(2f);
    }

    private void SpinHalo(List<SpectralBlade> halo, Vector3 centre)
    {
        var rot = Quaternion.AngleAxis(14f * Time.deltaTime, Vector3.up);
        foreach (var b in halo)
        {
            if (b == null || b.State == SpectralBlade.Phase.Dissolving) continue;
            b.transform.position = centre + rot * (b.transform.position - centre);
        }
    }

    // ================================================================== Phase 2 → 3

    /// <summary>He collapses; silence; the sword breaks and its fragments float UP;
    /// the chest splits; THUMP · THUMP · THUMP; the greatsword rises out of the
    /// floor; he rips it free and the sanctum tears apart.</summary>
    private IEnumerator PhaseThreeTransition()
    {
        inTransition = true;
        health.Invulnerable = true;
        GameHud.BossClear();
        Mood(0f);
        Sanctum?.Instability(0f);
        body.SetOrbit(0f);
        var feet = FloorPoint(transform.position);
        var fwd = FlatDir(transform.forward);
        PlayTransitionShot();

        Play(CollapseId, 0.15f, 1f);
        body.BreakP2Sword();
        WardenFx.Dust(feet, 6, 1.4f, 1.1f);
        WardenAudio.Play("thud", feet, 0.8f, 0.7f);
        yield return Wait(1.3f);                                  // silence

        Play(StandStruggleId, 0.3f, 0.55f);
        yield return Wait(0.5f);
        body.SplitChest();
        yield return Wait(0.6f);

        for (var n = 1; n <= 3; n++)
        {
            body.Beat(1.3f);
            WardenAudio.Play("thump", Chest, 1f, 1f - n * 0.05f);
            Sanctum?.Thump(n, feet);
            Sanctum?.Pulse(1f);
            WardenFx.Shake(0.08f * n);
            PostPulse.Pulse(0.4f * n, 0.4f);
            if (n == 3) body.RiseGreatsword(feet + fwd * 1.4f, 1.0f);
            yield return Wait(0.9f);
        }

        Play(GreatswordPullId, 0.2f, 1f);
        yield return Wait(0.55f);
        body.GrabGreatsword();
        // The pull tears the arena apart.
        Sanctum?.Shatter(feet);
        WardenFx.Impact(feet + fwd * 1.2f, 2.6f, 0.7f, 0.1f, 16, 2f, 2f);
        WardenAudio.Play("boom", feet, 1f, 0.7f);
        PostPulse.AberrationPulse(0.5f, 0.2f);
        StartCoroutine(GrowTo(1.15f, 1.4f));
        phase = 2;
        poise = corePoiseMax;
        yield return Wait(2.2f);

        GameHud.Boss(health, displayName + " — FORSAKEN", finaleAt);
        health.Invulnerable = false;
        inTransition = false;
        var now = Time.time;
        twin.nextAllowed = now + 3f;
        flood.nextAllowed = now + 6f;
        splitter.nextAllowed = now + 9f;
        kingsFall.nextAllowed = now + 13f;
        ruinous.nextAllowed = now + 16f;
        grave.nextAllowed = now + 20f;
    }

    private void PlayTransitionShot()
    {
        BossCinematics.Play(transform, 1f, new[]
        {
            new BossCinematics.Key(0f,   new Vector3(2.2f, 1.0f, 4.2f),  new Vector3(0f, 0.9f, 0f), 46f),
            new BossCinematics.Key(2.4f, new Vector3(1.2f, 1.5f, 3.0f),  new Vector3(0f, 1.3f, 0f), 38f),
            new BossCinematics.Key(4.6f, new Vector3(-3.5f, 2.6f, 6.5f), new Vector3(0f, 1.2f, 1.5f), 50f),
            new BossCinematics.Key(6.2f, new Vector3(-6.5f, 5.5f, 10f),  new Vector3(0f, 1.0f, 0f), 60f),
            new BossCinematics.Key(8.4f, new Vector3(-5.5f, 4.0f, 9f),   new Vector3(0f, 1.6f, 0f), 52f),
        }, null, blendIn: 0.5f, blendOut: 1.0f);
    }

    // ================================================================== Phase 3 — "the Core has taken control"

    /// <summary>Greatsword planted downward; two concentric rings show on the
    /// floor; two pulses leave it — jump, then a delayed double jump.</summary>
    private IEnumerator TwinRupture()
    {
        var id = Play(TwinRuptureId, 0.2f, 0.85f);
        var c = FloorPoint(transform.position);
        var inner = WardenMark.Circle(c, 2.4f, WardenFx.Crimson, 0.08f, 0.06f, 36);
        var outer = WardenMark.Circle(c, 3.6f, WardenFx.Crimson, 0.08f, 0.04f, 36);
        var t = 0f;
        while (StateTime(id) < 0.413f && t < 2.2f)
        {
            t += Time.deltaTime;
            if (StateTime(id) < 0.3f) Face(ToPlayerFlat, Time.deltaTime, 140f);
            c = FloorPoint(transform.position);
            var pulse = 0.5f + 0.5f * Mathf.Sin(t * 12f);
            inner.SetCircle(c, 2.4f); inner.SetPulse(pulse);
            outer.SetCircle(c, 3.6f); outer.SetPulse(1f - pulse);
            yield return null;
        }
        var impact = FrontPoint(1.4f);
        WardenFx.Impact(impact, 1.6f, 0.3f, 0.06f, 9, 1.2f, 1.2f);
        WardenAudio.Play("slam", impact, 1f, 0.75f);
        MeleeHit(2.6f, 360f, 30f, 2.5f, impact);
        inner.Release(0.1f);
        GroundWave.Spawn(c, transform.forward, 360f, OuterReach, 9f, 0.9f, 22f);
        WardenAudio.Play("pulse", c, 1f, 1f);
        yield return Wait(1.1f);
        outer.Release(0.1f);
        GroundWave.Spawn(c, transform.forward, 360f, OuterReach, 9f, 0.9f, 22f);
        WardenAudio.Play("pulse", c, 1f, 0.82f);
        yield return Until(id, 0.95f, 1.2f);
    }

    private float OuterReach => (Sanctum != null ? Sanctum.OuterRadius : 14f) + 1.5f;

    /// <summary>The signature: sword stabbed in, he crouches over it, Core showing;
    /// red cracks spread over the whole floor while the walls flare purple; the
    /// floor erupts — get on a wall (blades chase you along it). He's still
    /// dragging the greatsword out of the stone afterwards: punish.</summary>
    private IEnumerator CrimsonFlood()
    {
        var id = Play(CorePlantId, 0.2f, 0.8f);
        yield return Until(id, 0.42f, 1.6f, 120f);
        var swordAt = FrontPoint(1.4f);
        WardenFx.Impact(swordAt, 1.3f, 0.2f, 0f, 8, 1f, 1.4f);
        WardenAudio.Play("slam", swordAt, 1f, 0.7f);
        AnimSpeed(0.02f);
        body.SetCore(WardenBody.CoreMode.Open);
        body.Charge(0.6f);
        var hum = WardenAudio.Loop("drone", Chest, 0.4f, 0.9f);
        const float warn = 1.8f, burn = 1.8f;
        FloodField.Spawn(Sanctum, swordAt, warn, burn, 14f);
        WallChase.Run(transform, warn + burn);
        var t = 0f;
        var beat = 0f;
        while (t < warn + burn)
        {
            t += Time.deltaTime;
            beat -= Time.deltaTime;
            if (beat <= 0f) { beat = 0.55f; body.Beat(0.8f); Sanctum?.Pulse(0.6f); }
            yield return null;
        }
        WardenAudio.StopLoop(hum);
        body.SetCore(WardenBody.CoreMode.Exposed);
        body.Charge(0f);
        // Still prying the blade out of the floor — the long punish window.
        AnimSpeed(0.35f);
        WardenAudio.Play("stone", swordAt, 0.7f, 0.6f);
        yield return Until(id, 0.95f, 3.2f);
    }

    /// <summary>Greatsword dragged behind him, sparks off the stone; the walls light
    /// purple — then one huge sweep sends a tall wave across the platform. Get on
    /// a wall above it.</summary>
    private IEnumerator RuinousSweep()
    {
        Sanctum?.ChargeWalls(1f);
        var id = Play(RuinousSweepId, 0.25f, 0.3f);
        var scrape = WardenAudio.Loop("scrape", transform.position, 0.6f, 0.8f);
        var t = 0f;
        var spark = 0f;
        while (t < 2.4f && StateTime(id) < 0.36f)
        {
            t += Time.deltaTime;
            Face(ToPlayerFlat, Time.deltaTime, 70f);
            spark -= Time.deltaTime;
            if (spark <= 0f)
            {
                spark = 0.06f;
                var behind = FloorPoint(transform.position) - FlatDir(transform.forward) * 1.6f + Vector3.up * 0.05f;
                WardenFx.Shards(behind, 2, 3f, Random.value < 0.3f ? WardenFx.PaleRed : WardenFx.Crimson, false, 0.7f, 0.3f, Vector3.up * 0.5f);
            }
            if (scrape != null) scrape.transform.position = transform.position;
            yield return null;
        }
        WardenAudio.StopLoop(scrape);
        AnimSpeed(1.15f);
        yield return Until(id, 0.39f, 0.6f);
        var c = FloorPoint(transform.position);
        WardenAudio.Play("slash", c, 1f, 0.6f);
        WardenAudio.Play("boom", c, 0.7f, 1.2f);
        WardenFx.Shake(0.22f);
        MeleeHit(5f, 240f, 34f, 2.4f);
        GroundWave.Spawn(c, transform.forward, 360f, OuterReach, 12f, 2.4f, 30f, true, 1.2f, 0.8f);
        yield return Wait(1.1f);
        Sanctum?.ChargeWalls(0f);
        yield return Until(id, 0.95f, 1.6f);
    }

    /// <summary>He crouches, the Core flashes, armour is pulled off him — then the
    /// Core YANKS him into the air like a chain on his chest. A target forms under
    /// you: the outer ring contracts onto the inner one; when they meet, he falls
    /// sword-first. Move, then jump the shockwave (and the second, when enraged).</summary>
    private IEnumerator KingsFall()
    {
        Play(KingsFallCrouchId, 0.2f, 0.5f);
        body.Beat(1.5f);
        WardenAudio.Play("thump", Chest, 1f, 0.85f);
        yield return Wait(0.35f, 180f);
        body.ShedArmour(14);
        yield return Wait(0.3f);

        var hangId = Play(KingsFallHangId, 0.1f, 1f);
        WardenAudio.Play("whoom", Chest, 0.8f, 1.6f);
        WardenFx.Dust(transform.position, 8, 2f, 1.2f);
        if (bossAnimator != null) bossAnimator.transform.localRotation = Quaternion.Euler(24f, 0f, 0f);
        yield return LiftTo(8f, 0.42f, violent: true);
        if (hangId != 0) AnimSpeed(0.05f);

        const float inner = 3.6f, outerStart = 9.5f, window = 2.2f, lockAt = 1.35f;
        var target = LandingTarget(FloorPoint(WardenHazard.Feet));
        var innerMark = WardenMark.Circle(target, inner, WardenFx.Crimson, 0.1f, 0.12f, 40);
        var outerMark = WardenMark.Circle(target, outerStart, WardenFx.Crimson, 0.09f, 0f, 40);
        var t = 0f;
        while (t < window)
        {
            t += Time.deltaTime;
            if (t < lockAt && WardenHazard.FloorAt(WardenHazard.Feet + Vector3.up, 4f, out var f)) target = LandingTarget(f);
            var k = t / window;
            innerMark.SetCircle(target, inner);
            innerMark.SetPulse(k);
            outerMark.SetCircle(target, Mathf.Lerp(outerStart, inner, k));
            // Hang above the target, swaying like something on a line.
            var over = new Vector3(target.x, transform.position.y, target.z);
            MoveFlat((over - transform.position) * (1f - Mathf.Exp(-3f * Time.deltaTime)));
            if (bossAnimator != null) bossAnimator.transform.localRotation = Quaternion.Euler(24f + Mathf.Sin(t * 5f) * 5f, 0f, Mathf.Sin(t * 3.3f) * 6f);
            yield return null;
        }
        innerMark.Release(0.08f);
        outerMark.Release(0.05f);

        Play(KingsFallDropId, 0.05f, 1.6f);
        if (bossAnimator != null) bossAnimator.transform.localRotation = Quaternion.identity;
        var from = transform.position.y;
        var fall = 0f;
        while (transform.position.y > floorY + 0.02f && fall < 0.6f)
        {
            fall += Time.deltaTime;
            SetY(Mathf.Max(floorY, from - 40f * fall));
            yield return null;
        }
        SetY(floorY);
        EndLevitate();
        var land = FloorPoint(transform.position);
        WardenFx.Impact(land, 2.4f, 0.5f, 0.09f, 14, 1.8f, 1.6f);
        WardenAudio.Play("boom", land, 1f, 0.8f);
        WardenAudio.Play("metal", land, 0.9f, 0.7f);
        MeleeHit(inner, 360f, 40f, 3f, land);
        yield return Wait(0.5f);
        GroundWave.Spawn(land, transform.forward, 360f, OuterReach, 10f, 0.9f, 22f);
        WardenAudio.Play("pulse", land, 1f, 1f);
        if (health.Current <= health.Max * 0.25f)
        {
            yield return Wait(1.0f);
            GroundWave.Spawn(land, transform.forward, 360f, OuterReach, 10f, 0.9f, 22f);
            WardenAudio.Play("pulse", land, 1f, 0.82f);
        }
        Play(KingsFallLandId, 0.15f, 0.7f);
        yield return Wait(1.4f);
    }

    /// <summary>Where he can actually land: the player's floor point, pulled onto the
    /// surviving platform (the circle never promises a landing over the void).</summary>
    private Vector3 LandingTarget(Vector3 floor)
    {
        if (Sanctum == null) return floor;
        var clamped = Sanctum.ClampToPlatform(floor, 1.2f);
        return (clamped - floor).sqrMagnitude > 0.01f ? FloorPoint(clamped) : floor;
    }

    /// <summary>The move to fear: sword raised vertically while red light climbs it
    /// segment by segment — handle, guard, lower, middle, tip. Tip lit = swing.
    /// The slam is only the first hit: a thin crack shoots across the arena, sits
    /// 0.6s, then erupts along its whole length. Side-step the line.</summary>
    private IEnumerator Worldsplitter() => WorldsplitterRoutine(0.32f, true);

    private IEnumerator WorldsplitterRoutine(float segmentTime, bool track)
    {
        var id = Play(WorldsplitterId, 0.2f, 1f);
        yield return Until(id, 0.3f, 1.2f, 160f);
        AnimSpeed(0.02f);
        WardenAudio.Play("hum", Chest, 0.9f, 1.6f / (segmentTime * 5f));
        for (var s = 1; s <= 5; s++)
        {
            body.Countdown(s);
            body.Charge(s / 5f);
            var t = 0f;
            while (t < segmentTime)
            {
                t += Time.deltaTime;
                if (track && s < 5) Face(ToPlayerFlat, Time.deltaTime, 120f);
                yield return null;
            }
        }
        AnimSpeed(1.5f);
        yield return Until(id, 0.476f, 0.5f);
        body.ClearCountdown();
        body.Charge(0f);
        body.Beat(1.5f);
        var impact = FrontPoint(2.2f);
        WardenFx.Impact(impact, 2.2f, 0.6f, 0.1f, 14, 1.6f, 1.2f);
        WardenAudio.Play("slam", impact, 1f, 0.7f);
        MeleeHit(3.6f, 60f, 44f, 3f);
        var dir = FlatDir(transform.forward);
        CrackEruption.Spawn(impact, dir, CrackLength(impact, dir), 0.6f, 1.1f, 36f);
        AnimSpeed(0.8f);
        yield return Until(id, 0.95f, 2f);
    }

    /// <summary>Distance from <paramref name="from"/> along <paramref name="dir"/> to the arena's far edge.</summary>
    private float CrackLength(Vector3 from, Vector3 dir)
    {
        if (Sanctum == null) return 24f;
        var c = Sanctum.Center;
        var r = Sanctum.Shattered ? Sanctum.PlatformRadius : Sanctum.OuterRadius;
        var o = new Vector2(from.x - c.x, from.z - c.z);
        var d = new Vector2(dir.x, dir.z).normalized;
        var b = Vector2.Dot(o, d);
        var disc = b * b - (o.sqrMagnitude - r * r);
        return disc > 0f ? Mathf.Max(4f, -b + Mathf.Sqrt(disc)) : 8f;
    }

    /// <summary>The greatsword stays planted; he raises a hand; eight colossal blades
    /// rise from the floor around the arena; the red glow travels through them in
    /// order — each bursts half a second after it lights. Read the sequence.</summary>
    private IEnumerator GraveOfKings()
    {
        body.PlantGreatsword(FrontPoint(1.2f));
        Play(CrownRaiseId, 0.2f, 1f);
        yield return Wait(0.35f);
        Play(CrownHoldId, 0.25f, 1f);
        body.Charge(0.4f);
        var centre = ArenaCenter;
        var r = Mathf.Min(6.4f, (Sanctum != null ? Sanctum.PlatformRadius : 9f) - 2.6f);
        var swords = new List<GraveBlade>();
        var playerAng = Mathf.Atan2(WardenHazard.Feet.z - centre.z, WardenHazard.Feet.x - centre.x);
        var startAng = playerAng + Mathf.PI;               // the far side lights first
        var sense = Random.value < 0.5f ? 1f : -1f;
        for (var i = 0; i < 8; i++)
        {
            var a = startAng + sense * i * Mathf.PI * 2f / 8f;
            var p = FloorPoint(centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r);
            swords.Add(GraveBlade.Spawn(p, 4.2f, 3.3f, 26f, transform));
            yield return Wait(0.12f);
        }
        yield return Wait(0.6f);
        WardenAudio.Play("hum", Chest, 0.8f, 1.3f);
        for (var i = 0; i < swords.Count; i++)
        {
            if (swords[i] != null) swords[i].Prime(0.5f);
            yield return Wait(0.42f, 60f);
        }
        yield return Wait(0.8f);
        foreach (var g in swords) if (g != null) g.DissolveUp();
        body.Charge(0f);
        Play(CrownLandId, 0.2f, 1f);
        yield return Wait(0.4f);
        body.TakeGreatsword();
        yield return Wait(0.9f);
    }

    // ================================================================== the finale

    /// <summary>End of the Warden: he stops fighting and floats up with the whole
    /// arena; drone + heartbeat. Sword rain → ground rings → arena collapse (wall-run,
    /// blades chasing) → final approach: chest open, a last Worldsplitter charging.
    /// Jump toward him from a wall (or double jump close) and the suit's purple
    /// pulls you to the Core — strike it.</summary>
    private IEnumerator EndOfTheWarden()
    {
        finaleDone = true;
        inTransition = false;
        health.Invulnerable = true;
        WardenHazard.ClearAll();
        var centre = ArenaCenter;
        var floorCentre = FloorPoint(centre);
        Play(LocomotionHeavyId, 0.4f, 1f);
        SetSpeed(0f);
        GameHud.Toast("END OF THE WARDEN");
        if (Vector3.ProjectOnPlane(transform.position - centre, Vector3.up).magnitude > 1.5f)
            yield return BlinkTo(centre, ToPlayerFlat);
        drone = WardenAudio.Loop("drone", centre + Vector3.up * 3f, 0.6f, 1f);
        StartCoroutine(MoodTo(1f, 2f));
        Sanctum?.Ascend(1f);
        body.SetCore(WardenBody.CoreMode.Exposed);
        var lift = LiftTo(4.6f, 3.2f);
        var beat = 0f;
        while (lift.MoveNext())
        {
            Heartbeat(ref beat, 1.1f);
            Face(ToPlayerFlat, Time.deltaTime, 40f);
            yield return lift.Current;
        }

        // Part 1 — sword rain.
        var t = 0f;
        var next = 0f;
        while (t < 5f)
        {
            t += Time.deltaTime;
            Heartbeat(ref beat, 1f);
            Face(ToPlayerFlat, Time.deltaTime, 40f);
            if (t >= next)
            {
                next = t + 0.17f;
                var jitter = Random.insideUnitCircle * 1.4f;
                var g = Random.value < 0.4f ? WardenHazard.Feet + new Vector3(jitter.x, 0f, jitter.y)
                      : Sanctum != null ? Sanctum.RandomFloorPoint() : floorCentre + new Vector3(jitter.x, 0f, jitter.y) * 6f;
                if (WardenHazard.FloorAt(g + Vector3.up * 2f, 6f, out var floor))
                    RainStrike.Spawn(floor, 0.8f, 1.25f, 14f, 1f, transform);
            }
            yield return null;
        }
        yield return Wait(0.8f);

        // Part 2 — ground rings (jump → delayed double jump).
        var raise = Play(WorldsplitterId, 0.25f, 1f);
        yield return Until(raise, 0.3f, 1f);
        AnimSpeed(0.02f);
        var r1 = WardenMark.Circle(floorCentre, 2.6f, WardenFx.Crimson, 0.1f, 0.06f, 40);
        var r2 = WardenMark.Circle(floorCentre, 4f, WardenFx.Crimson, 0.1f, 0.04f, 40);
        t = 0f;
        while (t < 0.9f) { t += Time.deltaTime; r1.SetPulse(t / 0.9f); r2.SetPulse(t / 0.9f); Heartbeat(ref beat, 1f); yield return null; }
        r1.Release(0.1f);
        GroundWave.Spawn(floorCentre, Vector3.forward, 360f, OuterReach, 9f, 0.9f, 22f);
        WardenAudio.Play("pulse", floorCentre, 1f, 0.9f);
        WardenFx.Shake(0.2f);
        yield return Wait(1.15f);
        r2.Release(0.1f);
        GroundWave.Spawn(floorCentre, Vector3.forward, 360f, OuterReach, 9f, 0.9f, 22f);
        WardenAudio.Play("pulse", floorCentre, 1f, 0.75f);
        yield return Wait(1.8f);

        // Parts 3 & 4 — arena collapse, then the final approach. Loops until the Core is struck.
        var warn = 2f;
        while (true)
        {
            FloodField.Spawn(Sanctum, floorCentre, warn, 3.4f, 16f, dischargeWalls: false);
            WallChase.Run(transform, warn + 3.4f);
            t = 0f;
            while (t < warn + 1.6f) { t += Time.deltaTime; Heartbeat(ref beat, 0.8f); yield return null; }

            // Final approach: the chest opens, the last Worldsplitter charges.
            body.SetCore(WardenBody.CoreMode.Open);
            Sanctum?.ChargeWalls(1f);
            GameHud.ShowPrompt("SPC", "LEAP TO THE CORE");
            var countdown = 6.5f;
            t = 0f;
            var lit = 0;
            var struck = false;
            while (t < countdown)
            {
                t += Time.deltaTime;
                Heartbeat(ref beat, Mathf.Lerp(0.8f, 0.35f, t / countdown));
                var want = Mathf.Min(5, Mathf.FloorToInt(t / countdown * 5f) + 1);
                if (want != lit) { lit = want; body.Countdown(lit); body.Charge(lit / 5f); }
                if (CoreLeapRequested())
                {
                    var leap = CoreLeap();
                    while (leap.MoveNext()) yield return leap.Current;
                    if (coreStruck) { struck = true; break; }
                }
                if (CoreStrikeInReach()) { struck = true; break; }
                yield return null;
            }
            GameHud.HidePrompt();
            if (struck || coreStruck)
            {
                yield return CoreStrike();
                yield break;
            }

            // Countdown complete — the last Worldsplitter: four lines and a ring. Then again.
            body.ClearCountdown();
            body.Charge(0f);
            AnimSpeed(1.5f);
            WardenFx.Impact(floorCentre, 2.6f, 0.65f, 0.1f, 16, 2f, 2f);
            WardenAudio.Play("boom", floorCentre, 1f, 0.6f);
            var baseDir = FlatDir(WardenHazard.Feet - floorCentre);
            for (var i = 0; i < 4; i++)
                CrackEruption.Spawn(floorCentre, Quaternion.AngleAxis(i * 90f, Vector3.up) * baseDir, CrackLength(floorCentre, Quaternion.AngleAxis(i * 90f, Vector3.up) * baseDir), 0.6f, 1.2f, 38f);
            GroundWave.Spawn(floorCentre, Vector3.forward, 360f, OuterReach, 10f, 0.9f, 24f);
            body.SetCore(WardenBody.CoreMode.Exposed);
            yield return Wait(2.2f);
            Play(WorldsplitterId, 0.25f, 1f);
            yield return Until(WorldsplitterId, 0.3f, 1f);
            AnimSpeed(0.02f);
            warn = 1.6f;
        }
    }

    private bool coreStruck;

    private void Heartbeat(ref float timer, float period)
    {
        timer -= Time.deltaTime;
        if (timer > 0f) return;
        timer = period;
        body.Beat(1f);
        Sanctum?.Pulse(0.8f);
        WardenAudio.Play("thump", Chest, 0.9f, 1f);
    }

    /// <summary>A jump press in the air (double jump or wall jump) within reach of
    /// the floating Core — the suit answers the Core.</summary>
    private bool CoreLeapRequested()
    {
        if (playerLoco == null || playerLoco.JumpAction == null || !WardenHazard.Alive) return false;
        if (!playerLoco.JumpAction.WasPressedThisFrame()) return false;
        if (WardenHazard.Grounded && !WardenHazard.WallRunning) return false;
        var flat = Vector3.ProjectOnPlane(body.CorePosition - WardenHazard.Feet, Vector3.up).magnitude;
        return flat <= 17f;
    }

    /// <summary>Already airborne beside the Core (a double jump from below) — an
    /// attack press strikes directly.</summary>
    private bool CoreStrikeInReach()
    {
        if (playerAttack == null || playerAttack.AttackAction == null || WardenHazard.Grounded) return false;
        if (!playerAttack.AttackAction.WasPressedThisFrame()) return false;
        return Vector3.Distance(WardenHazard.Chest, body.CorePosition) <= 3.4f;
    }

    /// <summary>Purple gathers at the boots and the suit carries you up to the Core;
    /// you hang there a breath — attack to strike, or fall.</summary>
    private IEnumerator CoreLeap()
    {
        coreStruck = false;
        var pt = WardenHazard.Player;
        var pcc = WardenHazard.Capsule;
        var st = WardenHazard.State;
        if (pt == null || pcc == null || st == null) yield break;
        playerAttack?.Cancel();
        st.IsRooted = true;
        var start = pt.position;
        var core = body.CorePosition;
        var toCore = FlatDir(core - start);
        var end = core - toCore * 1.3f - Vector3.up * 1.15f;
        WardenAudio.Play("whoom", start, 1f, 1.4f);
        WardenFx.Ring(start + Vector3.up * 0.1f, Vector3.up, 0.2f, 1.4f, 0.35f, WardenFx.PurpleBright, 0.1f, 12);
        WardenFx.Shards(start, 14, 2.5f, WardenFx.Purple, true, 1.1f, 0.5f);
        const float T = 0.55f;
        var t = 0f;
        var pressed = false;
        while (t < T)
        {
            t += Time.deltaTime;
            var k = Mathf.Clamp01(t / T);
            var e = 1f - (1f - k) * (1f - k);
            var pos = Vector3.Lerp(start, end, e) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 1.2f;
            pcc.Move(pos - pt.position);
            pt.rotation = Quaternion.LookRotation(toCore, Vector3.up);
            WardenFx.Shards(pt.position + Vector3.up * 0.2f, 1, 0.6f, WardenFx.Purple, false, 0.9f, 0.3f);
            pressed |= playerAttack != null && playerAttack.AttackAction != null && playerAttack.AttackAction.WasPressedThisFrame();
            yield return null;
        }
        GameHud.ShowPrompt("LMB", "STRIKE THE CORE");
        var hang = 0f;
        while (hang < 0.75f && !pressed)
        {
            hang += Time.deltaTime;
            pressed = playerAttack != null && playerAttack.AttackAction != null && playerAttack.AttackAction.WasPressedThisFrame();
            WardenFx.Shards(pt.position, 1, 0.4f, WardenFx.Purple, true, 0.9f, 0.4f);
            yield return null;
        }
        GameHud.HidePrompt();
        if (pressed)
        {
            coreStruck = true;
            yield break;
        }
        st.IsRooted = false;
        playerLoco?.ResumeVerticalMotion(0f);
    }

    /// <summary>The final strike: everything stops for a breath; impact frames; the
    /// Core bursts; he drops; the floating arena holds… then gravity returns.</summary>
    private IEnumerator CoreStrike()
    {
        WardenHazard.ClearAll();
        GameHud.HidePrompt();
        var pt = WardenHazard.Player;
        var pcc = WardenHazard.Capsule;
        var st = WardenHazard.State;
        playerAttack?.Cancel();
        if (st != null) { st.IsRooted = true; st.IsInvulnerable = true; }
        var core = body.CorePosition;
        if (pt != null && pcc != null)
        {
            var toCore = FlatDir(core - pt.position);
            var place = core - toCore * 1.2f - Vector3.up * 1.15f;
            pcc.enabled = false;
            pt.SetPositionAndRotation(place, Quaternion.LookRotation(toCore, Vector3.up));
            pcc.enabled = true;
            var anim = playerLoco != null ? playerLoco.BodyAnimator : null;
            var strikeId = Animator.StringToHash("Base Layer.AirStrike3");
            if (anim != null && anim.HasState(0, strikeId)) anim.CrossFadeInFixedTime(strikeId, 0.04f, 0);
        }
        yield return new WaitForSecondsRealtime(0.14f);

        // Everything stops.
        Sanctum?.Freeze(true);
        if (Mathf.Approximately(Time.timeScale, 1f)) Time.timeScale = 0.02f;
        GameHud.Flash(0.08f);
        StartCoroutine(GameHud.ImpactFrames());
        body.ShatterCore();
        WardenFx.Shake(0.8f);
        yield return new WaitForSecondsRealtime(0.12f);
        if (Mathf.Approximately(Time.timeScale, 0.02f)) Time.timeScale = 1f;
        WardenAudio.StopLoop(drone);
        drone = null;

        // The killing blow, through the normal pipeline (numeral, credit, Died).
        finishing = true;
        health.Invulnerable = false;
        health.TakeDamage(health.Current + 1f, core, 999f, DamageKind.Crit, playerAttack);

        // He drops; the player falls free.
        if (st != null) { st.IsRooted = false; st.IsInvulnerable = false; }
        playerLoco?.ResumeVerticalMotion(0f);
        Play(FinalFallId, 0.1f, 1f);
        body.ShowSword(0);
        SetTangible(false);
        var vy = 0f;
        while (transform.position.y > floorY + 0.01f)
        {
            vy -= 22f * Time.deltaTime;
            var p = transform.position;
            transform.position = new Vector3(p.x, Mathf.Max(floorY, p.y + vy * Time.deltaTime), p.z);
            yield return null;
        }
        levitating = false;
        if (grounding != null) grounding.enabled = true;
        WardenFx.Impact(FloorPoint(transform.position), 2f, 0.4f, 0f, 12, 1.6f, 1f);
        WardenAudio.Play("boom", transform.position, 1f, 0.6f);
        Play(DieId, 0.2f, 1f);

        // The arena holds its breath, then everything comes down.
        yield return new WaitForSeconds(0.6f);
        Sanctum?.Crash();
        StartCoroutine(MoodTo(0f, 2.5f));
        yield return new WaitForSeconds(1.2f);
        GameHud.Banner("GREAT ENEMY FELLED", 3.2f);
        mode = Mode.Dormant;
    }

#if UNITY_EDITOR
    // ================================================================== editor debug (Tools > Project Restart > Warden)

    /// <summary>1 = Phase 2, 2 = the Phase 3 transition, 3 = End of the Warden.</summary>
    public void DebugStage(int stage)
    {
        if (defeated || health.IsDead) return;
        if (mode == Mode.Dormant) Engage();
        Interrupt();
        mode = Mode.Chase;
        switch (stage)
        {
            case 1:
                if (phase == 0) health.Revive(phaseAt - 0.02f);
                break;
            case 2:
                if (phase == 0) { phase = 1; roared = true; body.SetCore(WardenBody.CoreMode.Glimmer); }
                judgmentDone = true;
                revived = false;
                health.TakeDamage(health.Current + 1f, transform.position, 0f);
                break;
            case 3:
                if (phase < 2)
                {
                    phase = 2;
                    roared = revived = judgmentDone = true;
                    body.SplitChest();
                    body.GrabGreatsword();
                    SetBodySize(1.15f);
                    Sanctum?.Shatter(FloorPoint(transform.position));
                    GameHud.Boss(health, displayName + " — FORSAKEN", finaleAt);
                }
                health.Revive(finaleAt * 0.9f);
                finalePending = true;
                break;
        }
    }

    private int debugMove;

    /// <summary>Runs the next signature attack of the current phase; returns its name.</summary>
    public string DebugNextMove()
    {
        if (defeated || health.IsDead) return "(dead)";
        if (mode == Mode.Dormant) Engage();
        var list = new List<Move>();
        foreach (var m in PhaseMoves()) if (m.seq != null) list.Add(m);
        if (list.Count == 0) return "(no signature moves in phase " + phase + ")";
        var pick = list[debugMove++ % list.Count];
        Interrupt();
        StartSequence(pick.seq());
        return pick.seq.Method.Name + " (phase " + (phase + 1) + ")";
    }
#endif

    // ================================================================== mood

    /// <summary>The ultimate's slight environment shift: the sun dims, the fog reddens.</summary>
    private void Mood(float k)
    {
        if (sun0 < 0f)
        {
            fog0 = RenderSettings.fogColor;
            sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            sun0 = sun != null ? sun.intensity : 1f;
        }
        RenderSettings.fogColor = Color.Lerp(fog0, new Color(0.24f, 0.04f, 0.06f), k * 0.45f);
        if (sun != null) sun.intensity = sun0 * (1f - 0.3f * k);
        moodK = k;
    }

    private float moodK;

    private IEnumerator MoodTo(float target, float seconds)
    {
        var from = moodK;
        var t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            Mood(Mathf.Lerp(from, target, t / seconds));
            yield return null;
        }
        Mood(target);
    }

    // ================================================================== motion helpers

    private bool StateDone(int id)
    {
        if (bossAnimator == null) return true;
        var info = bossAnimator.GetCurrentAnimatorStateInfo(0);
        return info.shortNameHash == id && info.normalizedTime >= 1f;
    }

    private static Vector3 FlatDir(Vector3 v) { v.y = 0f; return v.sqrMagnitude < 0.001f ? Vector3.forward : v.normalized; }

    private void Face(Vector3 dir, float dt, float degPerSec)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;
        var want = Quaternion.LookRotation(dir.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, degPerSec * dt);
    }

    private void Step(Vector3 delta)
    {
        if (cc == null || !cc.enabled) { transform.position += delta; return; }
        verticalSpeed = cc.isGrounded ? -2f : Mathf.Max(verticalSpeed + gravity * Time.deltaTime, -50f);
        cc.Move(delta + Vector3.up * verticalSpeed * Time.deltaTime);
    }

    private void ApplyGravity() => Step(Vector3.zero);

    private void SetSpeed(float v)
    {
        if (bossAnimator != null) bossAnimator.SetFloat(SpeedId, v);
    }
}
