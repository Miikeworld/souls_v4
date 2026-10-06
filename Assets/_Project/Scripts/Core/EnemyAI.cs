using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Crowd-capable melee brain (vision aggro, pack alert, ring-circling, turn-taking
/// attacks, knockdowns). The player is noticed inside a sight cone with clear line
/// (or by touch); alerting one enemy alerts its pack. Engaged enemies that don't
/// hold one of the two melee tokens (detail §252) CIRCLE the player on a spaced
/// ring — separation keeps them from stacking — and step in when a token frees.
/// Moves: slash, two-hit combo, a long-telegraph heavy and a gap-closing lunge;
/// tracking stops late in each windup so a read dodge works. Finisher hits and
/// launcher landings knock trash down (root-motion fall → get-up).
///
/// Grounding rules (the "floating enemies" fixes): ONE CharacterController move
/// per frame always carries gravity — idle/windup/recover used to skip Move, so a
/// body pushed off a step hung in the air; spawn/respawn snap the capsule to the
/// floor; enemy capsules ignore each other so they can't step onto heads.
/// Animator: Speed/MoveX/MoveY/Attack/Hit/Dead; optional states Attack2, Heavy,
/// Lunge, Knockdown, GetUp (missing states fall back to the Attack trigger /
/// stagger so old controllers keep working).
/// </summary>
[RequireComponent(typeof(Health))]
public sealed class EnemyAI : MonoBehaviour, IRootMotionOwner
{
    private enum Phase { Idle, Chase, Circle, Windup, Active, Recover, Stagger, Airborne, Knockdown }

    [Header("Perception")]
    [Tooltip("Sight distance — the enemy only notices the player inside this range.")]
    [SerializeField, Min(1f)] private float sightRange = 11f;
    [Tooltip("Frontal vision cone in degrees — the player must be inside it to aggro.")]
    [SerializeField, Range(30f, 360f)] private float viewAngle = 120f;
    [Tooltip("Proximity that aggros regardless of facing (walking into its back).")]
    [SerializeField, Min(0f)] private float peripheralRange = 1.1f;
    [SerializeField, Min(0.5f)] private float deaggroRange = 16f;
    [SerializeField, Min(0.5f)] private float eyeHeight = 1.5f;
    [Tooltip("Alerting this enemy alerts idle allies within this radius — packs fight together.")]
    [SerializeField, Min(0f)] private float packAlertRadius = 9f;

    [Header("Movement")]
    [SerializeField, Min(0.5f)] private float moveSpeed = 3.4f;
    [SerializeField, Min(60f)] private float turnSpeed = 540f;
    [Tooltip("Stops closing in this far out — attacks start from this ring.")]
    [SerializeField, Min(0.5f)] private float engageRange = 1.8f;
    [Tooltip("Seconds between A* repaths while chasing around obstacles.")]
    [SerializeField, Min(0.1f)] private float repathInterval = 0.4f;
    [Tooltip("Radius the token-less crowd circles at while waiting its turn.")]
    [SerializeField, Min(1.5f)] private float ringRadius = 3.6f;
    [Tooltip("Personal spacing — allies inside this push apart.")]
    [SerializeField, Min(0.3f)] private float separation = 1.5f;

    [Header("Attack")]
    [Tooltip("Base telegraph time — each move scales it. Damage lands at the end.")]
    [SerializeField, Min(0.1f)] private float windupTime = 0.55f;
    [SerializeField, Min(0.5f)] private float strikeRange = 2.3f;
    [SerializeField, Range(30f, 360f)] private float strikeArc = 120f;
    [SerializeField, Min(0f)] private float damage = 15f;
    [SerializeField, Min(0.05f)] private float strikeLunge = 0.35f;
    [SerializeField, Min(0f)] private float recoverTime = 0.55f;
    [Tooltip("Seconds of circling before this enemy asks for a token again (randomised ±40%).")]
    [SerializeField, Min(0f)] private float repositionTime = 1.1f;
    [SerializeField, Min(0f)] private float staggerTime = 0.45f;
    [Tooltip("Lunge reach — the gap-closer is picked from engageRange up to this.")]
    [SerializeField, Min(2f)] private float lungeReach = 6.5f;
    [Tooltip("Posture meter — hits drain it, a break staggers. 0 = no meter: every hit staggers (trash-mob feel). Bosses set it high.")]
    [SerializeField, Min(0f)] private float poiseMax = 0f;
    [Tooltip("Poise refilled per second toward poiseMax (0 = instant refill on break only).")]
    [SerializeField, Min(0f)] private float poiseRegen = 8f;
    [Tooltip("Poise refill pause after taking a hit (Souls: posture recovers only while unpressured).")]
    [SerializeField, Min(0f)] private float poiseRegenDelay = 1.2f;
    [Tooltip("How long a backstab pins the victim (the Stabbed clip plays through).")]
    [SerializeField, Min(0.2f)] private float stabbedTime = 2.2f;

    [Header("Presentation")]
    [SerializeField] private Animator enemyAnimator;
    [Tooltip("Named elite — tougher, pays the elite kill rewards. Empty = trash mob.")]
    [SerializeField] private string eliteName;

    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int MoveXId = Animator.StringToHash("MoveX");
    private static readonly int MoveYId = Animator.StringToHash("MoveY");
    private static readonly int AttackId = Animator.StringToHash("Attack");
    private static readonly int HitId = Animator.StringToHash("Hit");
    private static readonly int DeadId = Animator.StringToHash("Dead");
    private static readonly int StabDeathId = Animator.StringToHash("StabDeath");
    private static readonly int AttackStateId = Animator.StringToHash("Base Layer.Attack");
    private static readonly int Attack2StateId = Animator.StringToHash("Base Layer.Attack2");
    private static readonly int HeavyStateId = Animator.StringToHash("Base Layer.Heavy");
    private static readonly int LungeStateId = Animator.StringToHash("Base Layer.Lunge");
    private static readonly int KnockdownStateId = Animator.StringToHash("Base Layer.Knockdown");
    private static readonly int GetUpStateId = Animator.StringToHash("Base Layer.GetUp");
    private static readonly int HitStateId = Animator.StringToHash("Base Layer.Hit");
    private static readonly int HitBackStateId = Animator.StringToHash("Base Layer.HitBack");
    private static readonly int StabbedStateId = Animator.StringToHash("Base Layer.Stabbed");
    private static readonly Color HeavyTell = new Color(1f, 0.16f, 0.14f);

    /// <summary>One attack: telegraph, one or more hits (seconds after the
    /// windup ends), reach, and the animator state each hit plays.</summary>
    private sealed class Move
    {
        public string name;
        public int[] states;          // state per hit (0 = Attack trigger fallback)
        public float[] hits;          // seconds after windup end
        public float windupScale = 1f, damageScale = 1f, rangeAdd, arc, lunge, recoverScale = 1f;
        public float trackUntil = 0.7f; // fraction of the windup the body still turns
        public float minDist, maxDist, weight = 1f;
        public bool dash;             // closes the gap during the active phase
        public bool heavyTell;
    }

    private Move[] moves;
    private Move current;
    private int hitCursor;

    private float currentStagger;
    private Vector3 knockVel;
    private Vector3 airVel;
    private float airSuspendT;
    private bool stabbed;
    private bool hasStabDeathParam;
    private bool canKnockdown;
    private bool kdGettingUp;
    private float poise;
    private float poiseWait;
    /// <summary>Frame a genuine metered poise break landed — the launcher's
    /// sweep reads it so a break on THIS hit counts as vulnerable (detail §118).</summary>
    public int LastPoiseBreakFrame { get; private set; } = -1;

    private Phase phase = Phase.Idle;

    /// <summary>Live enemies, for shared queries (combat state, HUD pings, crowd steering).</summary>
    private static readonly List<EnemyAI> all = new List<EnemyAI>();
    // Crowd-pressure coordinator (detail §252): at most two melee commitments
    // run at once and strike starts sit ≥0.25s apart. Enemies denied a slot
    // circle instead of stacking attacks; stagger/launch/death/disengage
    // release the reservation. Boss brains never reach this — EnemyAI is off them.
    private static readonly List<EnemyAI> meleeCommits = new(2);
    private static float lastStrikeStart = -9f;
    private const int MaxMeleeCommits = 2;
    private const float StrikeSpacing = 0.25f;

    /// <summary>Fired after <see cref="RespawnAll"/> (death / checkpoint rest) —
    /// encounter volumes re-arm on it.</summary>
    public static event System.Action WorldReset;

    private bool TryReserveStrike()
    {
        if (meleeCommits.Contains(this)) return true;
        if (meleeCommits.Count >= MaxMeleeCommits || Time.time - lastStrikeStart < StrikeSpacing) return false;
        meleeCommits.Add(this);
        lastStrikeStart = Time.time;
        return true;
    }

    /// <summary>True while this enemy is alerted — chasing, attacking, circling, staggered.</summary>
    public bool Engaged => health != null && !health.IsDead && phase != Phase.Idle;
    /// <summary>Any living enemy currently aggroed on the player.</summary>
    public static bool AnyEngaged
    {
        get
        {
            for (var i = 0; i < all.Count; i++) if (all[i].Engaged) return true;
            return false;
        }
    }

    /// <summary>Ballistic — launched or spiked; the state machine is suspended
    /// until the capsule lands (corpses keep falling through the dead branch).</summary>
    public bool IsAirborne => phase == Phase.Airborne;
    /// <summary>In the flinch (or knocked down) — execute techniques key off this.</summary>
    public bool Staggered => phase == Phase.Stagger || phase == Phase.Knockdown;
    /// <summary>A launch connects only when posture can't resist: trash mobs
    /// always fly; metered enemies must be broken or already staggered.</summary>
    public bool PoiseVulnerable => poiseMax <= 0f || poise <= 0f || Staggered;

    // IRootMotionOwner: only the knockdown fall travels with its clip.
    public bool DriveRootMotion => phase == Phase.Knockdown && !kdGettingUp && !health.IsDead;
    public bool AllowRootY => false;

    /// <summary>Pop the enemy up — ballistic flight until the capsule lands,
    /// then a knockdown (or hard stagger) opens the punish window.</summary>
    public void Launch(Vector3 vel)
    {
        if (IsBossBrain) return;
        airVel = vel;
        knockVel = Vector3.zero;
        kdGettingUp = false;
        SetPulse(0f);
        if (!health.IsDead) Enter(Phase.Airborne);
    }

    /// <summary>Air-chase finisher: an airborne victim is slammed straight down.</summary>
    public void Spike()
    {
        if (!IsAirborne) return;
        airVel = new Vector3(airVel.x * 0.3f, -16f, airVel.z * 0.3f);
    }

    /// <summary>A confirmed aerial-session hit arrests this victim's fall for a
    /// bounded beat (detail §142) — suspension, not relaunch.</summary>
    public void SuspendAir(float seconds)
    {
        if (!IsAirborne) return;
        airSuspendT = Mathf.Max(airSuspendT, seconds);
        if (airVel.y < 0f) airVel.y = 0f;
    }

    public bool AirSuspended => airSuspendT > 0f;

    /// <summary>Wake up and hunt the player now (encounter waves, pack alerts).</summary>
    public void Alert()
    {
        if (health == null || health.IsDead || phase != Phase.Idle) return;
        Enter(Phase.Chase);
    }

    /// <summary>Boss brains are EnemyAI-shaped holes — keep the component off
    /// the registry (backstab candidates, respawn resets) too.</summary>
    private bool IsBossBrain => GetComponent<IBossEngage>() != null;

    private void OnEnable()
    {
        if (IsBossBrain) return;
        all.Add(this);
        IgnoreAllies();
    }

    private void OnDisable() { all.Remove(this); meleeCommits.Remove(this); }

    /// <summary>Souls reset: back home (Health snaps the transform), heal, forget the player.</summary>
    public void Respawn()
    {
        health.ResetHealth();
        phase = Phase.Idle;
        phaseT = 0f;
        path.Clear();
        stabbed = false;
        current = null;
        knockVel = Vector3.zero;
        airVel = Vector3.zero;
        verticalSpeed = 0f;
        poise = poiseMax;
        poiseWait = 0f;
        meleeCommits.Remove(this);
        if (hasStabDeathParam) enemyAnimator.SetBool(StabDeathId, false);
        if (enemyAnimator != null) enemyAnimator.Rebind();
        SetPulse(0f);
        SetMoveAnim(Vector3.zero);
        SnapToGround();
    }

    /// <summary>Every live enemy in the scene back to its spawn — death/checkpoint rule.</summary>
    public static void RespawnAll()
    {
        for (var i = 0; i < all.Count; i++) all[i].Respawn();
        WorldReset?.Invoke();
    }

    /// <summary>Nearest living enemy whose back the player is standing behind —
    /// rear hemisphere + range, the souls backstab condition.</summary>
    public static EnemyAI FindBackstabTarget(Vector3 playerPos, float range)
    {
        EnemyAI best = null;
        var bestDist = range;
        for (var i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (e.health == null || e.health.IsDead) continue;
            var toPlayer = Vector3.ProjectOnPlane(playerPos - e.transform.position, Vector3.up);
            var dist = toPlayer.magnitude;
            if (dist > bestDist) continue;
            // Player behind the enemy's back: enemy forward points away from the player.
            if (dist > 0.01f && Vector3.Dot(e.transform.forward, toPlayer / dist) > -0.35f) continue;
            best = e;
            bestDist = dist;
        }
        return best;
    }

    private float phaseT;
    private float strafeDir = 1f;
    private float circleUntil;
    private float strafeFlipAt;
    private float nextDecisionAt;
    private bool wasDead;
    private Health health;
    private PlayerHealth playerHealth;
    private PlayerState playerState;
    private Transform player;
    private Renderer rend;
    private Color baseColor;
    private CharacterController cc;
    private float verticalSpeed;
    private Vector3 pendingMove;
    private PathGrid nav;
    private readonly List<Vector3> path = new();
    private int pathIndex;
    private float repathT;
    private bool IsElite => !string.IsNullOrEmpty(eliteName);

    private void Awake()
    {
        // The golem boss is a Targetable, but BossGolem owns its brain — and
        // BossBase.controller has no MoveX/MoveY/Attack params, so a stray
        // EnemyAI (Setup Combat Locomotion wires EVERY Targetable) spams
        // missing-parameter errors and would shrink the boss capsule.
        if (IsBossBrain) { enabled = false; return; }
        health = GetComponent<Health>();
        health.Damaged += OnDamaged;
        health.Died += OnDied;
        // transform.position += teleports through geometry — the CharacterController
        // sweeps the capsule so walls block and slide instead. Auto-add and copy the
        // existing capsule's shape so no scene setup is needed; the CC capsule itself
        // still satisfies lock-on overlap and attack sweeps.
        cc = GetComponent<CharacterController>();
        if (cc == null)
        {
            var capsule = GetComponent<CapsuleCollider>();
            cc = gameObject.AddComponent<CharacterController>();
            if (capsule != null)
            {
                cc.center = capsule.center;
                cc.radius = capsule.radius;
                cc.height = capsule.height;
            }
            else
            {
                cc.center = Vector3.up * 0.9f;
                cc.radius = 0.35f;
                cc.height = 1.8f;
            }
            cc.slopeLimit = 50f;
            // The CC capsule replaces the plain capsule — a second live collider on
            // the same object would block the CC's own sweep. Detection paths
            // (lock-on overlap, attack sweeps) still find the CC collider.
            if (capsule != null) capsule.enabled = false;
        }
        // A step this tall let a crowd climb onto each other's heads (and hover there).
        cc.stepOffset = Mathf.Min(cc.stepOffset, 0.3f);
        if (enemyAnimator == null) enemyAnimator = GetComponentInChildren<Animator>();
        if (enemyAnimator != null)
        {
            foreach (var param in enemyAnimator.parameters)
                if (param.nameHash == StabDeathId) { hasStabDeathParam = true; break; }
            canKnockdown = enemyAnimator.HasState(0, KnockdownStateId) && enemyAnimator.HasState(0, GetUpStateId);
            if (canKnockdown)
            {
                // The knockdown fall carries the body back with its clip; the relay
                // discards every other delta, so baked/in-place clips behave as before.
                enemyAnimator.applyRootMotion = true;
                if (enemyAnimator.GetComponent<RootMotionRelay>() == null)
                    enemyAnimator.gameObject.AddComponent<RootMotionRelay>();
            }
        }
        // A skeletal death anim replaces the capsule tip-over; capsule-only
        // dummies keep the tip-over fallback.
        health.SuppressDeathMotion = enemyAnimator != null;
        poise = poiseMax;
        rend = GetComponentInChildren<Renderer>();
        if (rend != null) baseColor = rend.material.color;
        var p = FindFirstObjectByType<PlayerLocomotion>();
        if (p != null)
        {
            player = p.transform;
            playerHealth = p.GetComponent<PlayerHealth>();
            playerState = p.GetComponent<PlayerState>();
        }
        BuildMoves();
        strafeDir = Random.value < 0.5f ? -1f : 1f;
    }

    private void Start()
    {
        nav = PathGrid.Instance;
        SnapToGround();
    }

    private void OnDestroy()
    {
        if (health != null)
        {
            health.Damaged -= OnDamaged;
            health.Died -= OnDied;
        }
    }

    private void BuildMoves()
    {
        bool Has(int id) => enemyAnimator != null && enemyAnimator.HasState(0, id);
        var list = new List<Move>
        {
            new Move { name = "Slash", states = new[] { AttackStateId }, hits = new[] { 0f },
                       arc = strikeArc, lunge = strikeLunge, maxDist = engageRange + 0.3f, weight = 1f },
        };
        if (Has(Attack2StateId))
            list.Add(new Move { name = "Combo", states = new[] { AttackStateId, Attack2StateId }, hits = new[] { 0f, 0.42f },
                                damageScale = 0.7f, arc = strikeArc, lunge = strikeLunge * 0.8f, recoverScale = 1.25f,
                                maxDist = engageRange + 0.3f, weight = IsElite ? 1.2f : 0.8f });
        if (Has(HeavyStateId))
            list.Add(new Move { name = "Heavy", states = new[] { HeavyStateId }, hits = new[] { 0f },
                                windupScale = 1.75f, damageScale = 1.8f, rangeAdd = 0.4f, arc = 90f, lunge = 0.7f,
                                trackUntil = 0.6f, recoverScale = 1.6f, maxDist = engageRange + 0.6f,
                                weight = IsElite ? 1f : 0.35f, heavyTell = true });
        if (Has(LungeStateId))
            list.Add(new Move { name = "Lunge", states = new[] { LungeStateId }, hits = new[] { 0.22f },
                                windupScale = 0.9f, damageScale = 1.2f, arc = 70f, lunge = 0f, dash = true,
                                trackUntil = 0.85f, recoverScale = 1.4f, minDist = engageRange + 1.2f, maxDist = lungeReach,
                                weight = 0.6f });
        moves = list.ToArray();
    }

    /// <summary>Kill payout — souls flow to the wallet even while the player
    /// is mid-death (dying to a simultaneous hit still counts the kill). Mana
    /// is stricter: player-owned kills only (the credit window covers punts
    /// into hazards), and none while the player is dead. Boss brains never
    /// reach this — IsBossBrain keeps EnemyAI off them, so a phase fake-death
    /// can't pay an ordinary reward.</summary>
    private void OnDied()
    {
        SoulsWallet.Add(health.SoulsReward);
        if (health.ManaReward > 0 && !(playerState != null && playerState.IsDead)
            && health.TryKillCredit(out var pool, out var originAction))
        {
            pool.Restore(health.ManaReward);
            // Kill charge for the earned ultimate: elites (manaReward ≥12) pay
            // 8, trash pays 4 — capped per originating action inside Credit.
            if (pool.TryGetComponent<UltCharge>(out var ult))
                ult.Credit(originAction, health.ManaReward >= 12 ? 8f : 4f);
        }
    }

    private void Update()
    {
        if (health.IsDead != wasDead)
        {
            wasDead = health.IsDead;
            SetAnimBool(DeadId, wasDead);
            if (!wasDead && enemyAnimator != null) enemyAnimator.Rebind();
            if (wasDead) { phase = Phase.Idle; current = null; meleeCommits.Remove(this); }
        }
        var dt = Time.deltaTime;
        pendingMove = Vector3.zero;
        if (health.IsDead || player == null)
        {
            SetMoveAnim(Vector3.zero);
            // A punted corpse still falls — the state machine is off but the
            // ballistic arc finishes so bodies land instead of hovering.
            if (airVel != Vector3.zero)
            {
                airVel.y -= 14f * dt;
                if (cc != null && cc.enabled) cc.Move(airVel * dt);
                else transform.position += airVel * dt;
                if (cc == null || (cc.isGrounded && airVel.y <= 0f)) airVel = Vector3.zero;
            }
            else if (cc != null && cc.enabled) ApplyMotion(dt); // corpses settle onto the floor too
            return;
        }
        if (playerHealth != null && playerHealth.IsDead)
        {
            Enter(Phase.Idle); // disengagement releases the strike reservation too
            SetPulse(0f);
            SetMoveAnim(Vector3.zero);
            ApplyMotion(dt);
            return;
        }

        phaseT += dt;
        // Posture recovers only while unpressured — the Souls poise rule.
        if (poiseMax > 0f)
        {
            if (poiseWait > 0f) poiseWait -= dt;
            else poise = Mathf.Min(poiseMax, poise + poiseRegen * dt);
        }
        var toPlayer = player.position - transform.position;
        toPlayer.y = 0f;
        var dist = toPlayer.magnitude;

        switch (phase)
        {
            case Phase.Idle:
                SetMoveAnim(Vector3.zero);
                // Front-cone vision + LOS, or a physical bump from any side.
                if (CanSeePlayer(dist, toPlayer) || dist <= peripheralRange)
                {
                    Enter(Phase.Chase);
                    AlertPack();
                }
                break;

            case Phase.Chase:
            {
                if (dist > deaggroRange)
                {
                    path.Clear();
                    Enter(Phase.Idle);
                    SetAnimFloat(SpeedId, 0f);
                    break;
                }
                // A token walked in from the ring is spent if the swing never comes.
                if (meleeCommits.Contains(this) && phaseT > 3f) meleeCommits.Remove(this);
                Move move = null;
                if (dist <= engageRange) move = PickMove(dist);
                else if (Time.time >= nextDecisionAt)
                {
                    // Out of reach the only option is the gap-closer — rolled a few
                    // times a second, not every frame, so a closing pack doesn't all lunge.
                    nextDecisionAt = Time.time + 0.4f;
                    move = PickMove(dist);
                    if (move != null && (!move.dash || Random.value > 0.35f)) move = null;
                }
                if (move != null && Time.time >= circleUntil)
                {
                    if (TryReserveStrike()) { BeginWindup(move); break; }
                    EnterCircle();
                    break;
                }
                // Token holders (or nobody waiting) close in; the rest join the ring.
                if (dist <= ringRadius + 0.6f && !meleeCommits.Contains(this) && meleeCommits.Count >= MaxMeleeCommits)
                {
                    EnterCircle();
                    break;
                }
                var dir = ChaseDirection(dist);
                Face(dir, dt);
                Step((dir + Separation() * 0.8f).normalized * moveSpeed * dt);
                SetMoveAnim(cc != null ? cc.velocity : dir * moveSpeed);
                break;
            }

            case Phase.Circle:
            {
                if (dist > deaggroRange) { Enter(Phase.Idle); break; }
                Face(toPlayer, dt);
                if (Time.time >= strafeFlipAt)
                {
                    strafeFlipAt = Time.time + Random.Range(1.6f, 3.4f);
                    strafeDir = -strafeDir;
                }
                if (Time.time >= circleUntil)
                {
                    var move = PickMove(dist);
                    // From the ring: usually walk in for a melee string, sometimes lunge.
                    if (move != null && move.dash && Random.value > 0.35f) move = null;
                    if ((move != null || dist <= ringRadius + 1.2f) && TryReserveStrike())
                    {
                        if (move != null && (move.dash || dist <= engageRange)) BeginWindup(move);
                        else Enter(Phase.Chase); // token held: walk in for the swing
                        break;
                    }
                    circleUntil = Time.time + repositionTime * Random.Range(0.6f, 1.4f);
                }
                var radialDir = toPlayer.sqrMagnitude > 0.001f ? toPlayer.normalized : transform.forward;
                var tangent = Vector3.Cross(Vector3.up, radialDir) * strafeDir;
                // Hold the ring: drift in when too far, back off when crowding.
                var radial = dist > ringRadius + 0.6f ? radialDir : dist < ringRadius - 0.6f ? -radialDir : Vector3.zero;
                var steer = tangent * 0.75f + radial + Separation() * 1.4f;
                var speed = moveSpeed * (radial == Vector3.zero ? 0.45f : 0.7f);
                Step(steer.normalized * speed * dt);
                SetMoveAnim(cc != null ? cc.velocity : steer.normalized * speed);
                break;
            }

            case Phase.Windup:
            {
                var m = current;
                var wind = windupTime * m.windupScale;
                // Commit: after trackUntil the swing no longer follows a sidestep.
                if (phaseT < wind * m.trackUntil) Face(toPlayer, dt);
                SetMoveAnim(Vector3.zero);
                SetPulse(Mathf.Clamp01(phaseT / wind));
                if (m.heavyTell && phaseT - dt < wind * 0.55f && phaseT >= wind * 0.55f)
                    HitFx.Spawn(transform.position + Vector3.up * 1.6f + transform.forward * 0.4f, transform.right, 0.7f, HeavyTell);
                if (phaseT >= wind) { hitCursor = 0; Enter(Phase.Active); }
                break;
            }

            case Phase.Active:
            {
                var m = current;
                if (m.dash && hitCursor == 0)
                {
                    // Gap-closer: cover the distance to just short of the player.
                    var reach = Mathf.Max(0f, dist - engageRange * 0.8f);
                    var dashSpeed = Mathf.Min(reach / Mathf.Max(0.05f, m.hits[0] - phaseT + dt), 14f);
                    Step(transform.forward * dashSpeed * dt);
                }
                while (hitCursor < m.hits.Length && phaseT >= m.hits[hitCursor])
                {
                    if (hitCursor > 0) PlayState(m.states[Mathf.Min(hitCursor, m.states.Length - 1)]);
                    Strike(m, toPlayer, dist);
                    hitCursor++;
                }
                if (hitCursor >= m.hits.Length && phaseT >= m.hits[m.hits.Length - 1] + 0.05f)
                {
                    SetPulse(0f);
                    Enter(Phase.Recover);
                }
                break;
            }

            case Phase.Recover:
                if (phaseT >= recoverTime * (current != null ? current.recoverScale : 1f))
                {
                    current = null;
                    strafeDir = Random.value < 0.5f ? -1f : 1f;
                    EnterCircle(); // hand the token back and drift aside — no face-hugging
                }
                break;

            case Phase.Stagger:
                SetMoveAnim(Vector3.zero);
                if (knockVel.sqrMagnitude > 0.0001f)
                {
                    Step(knockVel * dt);
                    knockVel = Vector3.Lerp(knockVel, Vector3.zero, 1f - Mathf.Exp(-10f * dt));
                }
                if (phaseT >= currentStagger) { stabbed = false; Enter(Phase.Chase); }
                break;

            case Phase.Airborne:
                // Ballistic: the launcher/spike owns airVel; on touchdown the
                // hard landing becomes a knockdown (or long stagger) — the punish window.
                // Suspended victims hold their height (arrested fall, no lift).
                SetMoveAnim(Vector3.zero);
                if (airSuspendT > 0f)
                {
                    airSuspendT -= dt;
                    airVel.y = Mathf.Max(airVel.y, 0f);
                }
                else airVel.y -= 14f * dt;
                if (cc != null && cc.enabled) cc.Move(airVel * dt); else transform.position += airVel * dt;
                if ((cc == null || cc.isGrounded) && airVel.y <= 0f)
                {
                    airVel = Vector3.zero;
                    verticalSpeed = 0f;
                    if (!BeginKnockdown())
                    {
                        currentStagger = staggerTime * 1.6f;
                        Enter(Phase.Stagger);
                    }
                }
                return; // the ballistic move already carried gravity

            case Phase.Knockdown:
                SetMoveAnim(Vector3.zero);
                TickKnockdown();
                if (DriveRootMotion) { verticalSpeed = 0f; return; } // the relay moves (and grounds) the capsule
                break;
        }
        ApplyMotion(dt);
    }

    // ---------- crowd steering ----------

    private void EnterCircle()
    {
        meleeCommits.Remove(this);
        circleUntil = Time.time + repositionTime * Random.Range(0.6f, 1.4f);
        strafeFlipAt = Time.time + Random.Range(1.6f, 3.4f);
        Enter(Phase.Circle);
    }

    /// <summary>Push away from allies inside <see cref="separation"/> — the crowd
    /// spreads into a ring instead of a scrum.</summary>
    private Vector3 Separation()
    {
        var push = Vector3.zero;
        var me = transform.position;
        for (var i = 0; i < all.Count; i++)
        {
            var o = all[i];
            if (o == this || o.health == null || o.health.IsDead) continue;
            var d = me - o.transform.position;
            d.y = 0f;
            var m = d.magnitude;
            if (m >= separation || m < 0.0001f) continue;
            push += d / m * (1f - m / separation);
        }
        return push;
    }

    private void AlertPack()
    {
        if (packAlertRadius <= 0f) return;
        var me = transform.position;
        for (var i = 0; i < all.Count; i++)
        {
            var o = all[i];
            if (o != this && o.phase == Phase.Idle && (o.transform.position - me).sqrMagnitude <= packAlertRadius * packAlertRadius)
                o.Alert();
        }
    }

    /// <summary>Enemy capsules never collide with each other: CharacterControllers
    /// step onto whatever is shorter than stepOffset — another enemy's head — and
    /// ride there. Separation steering keeps them apart instead.</summary>
    private void IgnoreAllies()
    {
        if (cc == null) cc = GetComponent<CharacterController>();
        if (cc == null) return;
        for (var i = 0; i < all.Count; i++)
        {
            var o = all[i];
            if (o == this || o.cc == null) continue;
            Physics.IgnoreCollision(cc, o.cc, true);
        }
    }

    // ---------- attacks ----------

    private Move PickMove(float dist)
    {
        if (moves == null || moves.Length == 0) return null;
        var total = 0f;
        foreach (var m in moves) if (dist >= m.minDist && dist <= Mathf.Max(m.maxDist, engageRange)) total += m.weight;
        if (total <= 0f) return null;
        var roll = Random.value * total;
        foreach (var m in moves)
        {
            if (dist < m.minDist || dist > Mathf.Max(m.maxDist, engageRange)) continue;
            roll -= m.weight;
            if (roll <= 0f) return m;
        }
        return moves[0];
    }

    private void BeginWindup(Move m)
    {
        current = m;
        hitCursor = 0;
        Enter(Phase.Windup);
        PlayState(m.states[0]); // the swing clip's windup = the telegraph
    }

    private void PlayState(int stateId)
    {
        if (enemyAnimator == null) return;
        if (enemyAnimator.HasState(0, stateId)) enemyAnimator.CrossFadeInFixedTime(stateId, 0.08f, 0, 0f);
        else enemyAnimator.SetTrigger(AttackId);
    }

    /// <summary>The hit resolves at this instant — inside reach and arc and not
    /// i-framed, the player takes it. Dodge timing is the counter.</summary>
    private void Strike(Move m, Vector3 toPlayer, float dist)
    {
        var dir = toPlayer.sqrMagnitude > 0.0001f ? toPlayer.normalized : transform.forward;
        if (m.lunge > 0f) Step(dir * m.lunge);
        var inRange = dist <= strikeRange + m.rangeAdd + (m.dash ? 0.6f : 0f);
        var inArc = Vector3.Angle(transform.forward, dir) <= m.arc * 0.5f;
        var inHeight = Mathf.Abs(player.position.y - transform.position.y) < 2f;
        var invuln = (playerState != null && playerState.IsInvulnerable)
                     || (playerHealth != null && playerHealth.Invulnerable);
        if (inRange && inArc && inHeight && !invuln && playerHealth != null)
            playerHealth.TakeDamage(damage * m.damageScale, transform.position);
    }

    // ---------- knockdown ----------

    private bool BeginKnockdown()
    {
        if (!canKnockdown || health.IsDead) return false;
        kdGettingUp = false;
        stabbed = false;
        knockVel = Vector3.zero;
        current = null;
        Enter(Phase.Knockdown);
        enemyAnimator.CrossFadeInFixedTime(KnockdownStateId, 0.06f, 0, 0f);
        return true;
    }

    private void TickKnockdown()
    {
        var info = enemyAnimator.GetCurrentAnimatorStateInfo(0);
        if (!kdGettingUp)
        {
            var inFall = info.fullPathHash == KnockdownStateId;
            // Lie a beat after the fall lands; the time cap covers a missing/looping clip.
            if ((inFall && info.normalizedTime >= 0.98f && phaseT > 0.6f) || phaseT > 2.2f)
            {
                kdGettingUp = true;
                phaseT = 0f;
                enemyAnimator.CrossFadeInFixedTime(GetUpStateId, 0.12f, 0, 0f);
            }
        }
        else
        {
            var inGetUp = info.fullPathHash == GetUpStateId;
            if ((inGetUp && info.normalizedTime >= 0.9f) || phaseT > 2.4f)
            {
                kdGettingUp = false;
                Enter(Phase.Chase);
            }
        }
    }

    // ---------- perception / pathing ----------

    /// <summary>Sight cone + line of sight — the "sees you" test for aggro.</summary>
    private bool CanSeePlayer(float dist, Vector3 toPlayer)
    {
        if (dist > sightRange) return false;
        if (Mathf.Abs(player.position.y - transform.position.y) > 6f) return false; // a gallery far above isn't "seen"
        if (dist > peripheralRange &&
            Vector3.Angle(transform.forward, toPlayer) > viewAngle * 0.5f) return false;
        var eye = transform.position + Vector3.up * eyeHeight;
        if (nav != null) return nav.CanSee(eye, player);
        var chest = player.position + Vector3.up * 1.2f;
        var d = chest - eye;
        return !Physics.Raycast(eye, d.normalized, out var hit, d.magnitude, ~0, QueryTriggerInteraction.Ignore)
               || hit.collider.GetComponentInParent<PlayerLocomotion>() != null
               || hit.collider.GetComponentInParent<EnemyAI>() != null;
    }

    /// <summary>Direct chase when the player is visible; A* waypoints around cover.</summary>
    private Vector3 ChaseDirection(float dist)
    {
        var eye = transform.position + Vector3.up * eyeHeight;
        var clear = nav == null
            ? !Physics.Linecast(eye, player.position + Vector3.up * 1.2f)
            : nav.HasLine(eye, player.position + Vector3.up * 0.9f);
        if (clear || nav == null)
        {
            path.Clear();
            return (player.position - transform.position).FlatPlanar();
        }

        repathT -= Time.deltaTime;
        if (path.Count == 0 || repathT <= 0f)
        {
            repathT = repathInterval;
            nav.FindPath(transform.position, player.position, path);
            pathIndex = 0;
        }
        while (pathIndex < path.Count &&
               Vector3.ProjectOnPlane(path[pathIndex] - transform.position, Vector3.up).magnitude < 0.45f)
            pathIndex++;
        if (pathIndex >= path.Count) return (player.position - transform.position).FlatPlanar();
        var d = path[pathIndex] - transform.position;
        d.y = 0f;
        return d.normalized;
    }

    private void Enter(Phase p)
    {
        // Windup/Active/Recover hold the melee reservation, and so does the walk-in
        // (Chase) that follows winning a token on the ring; anything else frees it.
        if (p is not (Phase.Windup or Phase.Active or Phase.Recover or Phase.Chase)) meleeCommits.Remove(this);
        if (p is Phase.Stagger or Phase.Airborne or Phase.Knockdown or Phase.Idle) current = null;
        phase = p;
        phaseT = 0f;
    }

    private void OnDamaged(float amount, Vector3 from, float poiseDamage, ReactionProfile reaction)
    {
        if (health.IsDead) return;
        SetPulse(0f); // an interrupted windup would otherwise leave the tint stuck
        if (phase == Phase.Idle) AlertPack(); // ambushed from out of sight — the pack answers
        if (stabbed) return; // the Stabbed clip owns the body until it ends
        // Juggle: hits on an airborne body keep it flying instead of snapping
        // it to the ground flinch — the air-chase window stays open.
        if (phase == Phase.Airborne) return;
        // Downed: hits still deal damage but never restart the fall.
        if (phase == Phase.Knockdown) return;

        // Poise: hits drain the meter and only a break staggers. poiseMax 0 =
        // trash mob — every hit breaks, keeping the flinch-per-hit feel.
        var broken = poiseMax <= 0f || (poise -= poiseDamage) <= 0f;
        // A metered break on THIS hit marks the frame — launch eligibility
        // reads it before the refill below resets poise (detail §118).
        if (broken && poiseMax > 0f) LastPoiseBreakFrame = Time.frameCount;
        var toAttacker = Vector3.ProjectOnPlane(from - transform.position, Vector3.up);
        if (poiseMax > 0f)
        {
            poiseWait = poiseRegenDelay;
            if (broken) poise = poiseMax;
        }
        // Reaction profile (detail §184): displacement and stagger duration
        // scale with the hit class — normals keep enemies in follow-up reach,
        // finishers knock trash down (beat-'em-up payoff for a full string).
        var (push, stagger) = reaction switch
        {
            ReactionProfile.Sweep => (0.6f, 0.32f),
            ReactionProfile.Spin => (0.15f, 0.2f),
            ReactionProfile.Finisher => (1.1f, 0.65f),
            _ => (0.22f, 0.2f),
        };
        if (!broken)
        {
            // Hyperarmor — no flinch, no interrupt; a token shove sells contact.
            knockVel = toAttacker.sqrMagnitude > 0.0001f
                ? -toAttacker.normalized * (push * 0.3f / 0.12f) : Vector3.zero;
            return;
        }

        if (reaction == ReactionProfile.Finisher && !IsElite)
        {
            // Face the attacker so the authored fall carries the body away from them.
            if (toAttacker.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(toAttacker.normalized, Vector3.up);
            if (BeginKnockdown()) return;
        }

        // Directional flinch, restarted on EVERY hit (CrossFade from t=0) so a
        // combo reads as a string of impacts — the trigger path couldn't
        // re-enter Hit mid-flinch. Trigger fallback for capsule/old controllers.
        var fromFront = Vector3.Dot(transform.forward, toAttacker) >= 0f;
        var stateId = fromFront ? HitStateId : HitBackStateId;
        if (enemyAnimator != null && enemyAnimator.HasState(0, stateId))
            enemyAnimator.CrossFadeInFixedTime(stateId, 0.05f, 0, 0f);
        else if (enemyAnimator != null && enemyAnimator.HasState(0, HitStateId))
            enemyAnimator.CrossFadeInFixedTime(HitStateId, 0.05f, 0, 0f);
        else SetAnimTrigger(HitId);

        // Profile shove away from the attacker, spent over the flinch.
        knockVel = toAttacker.sqrMagnitude > 0.0001f ? -toAttacker.normalized * (push / 0.12f) : Vector3.zero;
        // Getting hit interrupts windup/circling — a flinch, not a stunlock.
        currentStagger = Mathf.Max(staggerTime, stagger);
        Enter(Phase.Stagger);
    }

    /// <summary>Backstab victim: pinned for the Stabbed clip, facing away from
    /// the stabber, no knockback. The follow-up hit won't restart a flinch.</summary>
    public void OnBackstabbed(Transform stabber)
    {
        if (health.IsDead) return;
        var away = Vector3.ProjectOnPlane(transform.position - stabber.position, Vector3.up);
        if (away.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(away.normalized, Vector3.up);
        stabbed = true;
        knockVel = Vector3.zero;
        SetPulse(0f);
        if (enemyAnimator != null && enemyAnimator.HasState(0, StabbedStateId))
            enemyAnimator.CrossFadeInFixedTime(StabbedStateId, 0.08f, 0, 0f);
        currentStagger = stabbedTime;
        Enter(Phase.Stagger);
    }

    /// <summary>Set just before the backstab blow lands: a killing stab routes
    /// the death to StabbedDeath instead of the generic Die.</summary>
    public void MarkStabKill(bool lethal)
    {
        if (hasStabDeathParam) enemyAnimator.SetBool(StabDeathId, lethal);
    }

    // ---------- motion ----------

    private void Face(Vector3 dir, float dt)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;
        var yaw = Quaternion.LookRotation(dir.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, yaw, turnSpeed * dt);
    }

    /// <summary>Queue planar travel — applied once per frame with gravity in ApplyMotion.</summary>
    private void Step(Vector3 delta)
    {
        delta.y = 0f;
        pendingMove += delta;
    }

    /// <summary>The single CharacterController move of the frame. Gravity rides
    /// EVERY frame — standing, winding up, recovering — so nothing hovers.</summary>
    private void ApplyMotion(float dt)
    {
        if (cc == null || !cc.enabled || !cc.gameObject.activeInHierarchy)
        {
            transform.position += pendingMove;
            pendingMove = Vector3.zero;
            return;
        }
        verticalSpeed = cc.isGrounded ? -2f : Mathf.Max(verticalSpeed - 20f * dt, -50f);
        cc.Move(pendingMove + Vector3.up * verticalSpeed * dt);
        pendingMove = Vector3.zero;
    }

    /// <summary>Drop (or lift) the capsule onto the floor under it — authored or
    /// spawned heights are never trusted (a capsule placed half in the floor pops
    /// up on its first move; one placed high hovers until it moves).</summary>
    private void SnapToGround()
    {
        if (cc == null) return;
        var scale = transform.lossyScale.y;
        var bottom = (cc.center.y - cc.height * 0.5f) * scale;
        var from = transform.position + Vector3.up * (cc.center.y * scale + 1.5f);
        var hits = Physics.RaycastAll(from, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore);
        var best = float.MaxValue;
        var floorY = 0f;
        foreach (var h in hits)
        {
            if (h.collider.transform.IsChildOf(transform) || h.collider.GetComponentInParent<Health>() != null) continue;
            if (h.distance < best) { best = h.distance; floorY = h.point.y; }
        }
        if (best == float.MaxValue) return;
        var was = cc.enabled;
        cc.enabled = false;
        transform.position = new Vector3(transform.position.x, floorY - bottom + cc.skinWidth, transform.position.z);
        cc.enabled = was;
        verticalSpeed = -2f;
    }

    private void SetPulse(float k)
    {
        if (enemyAnimator != null || rend == null) return; // skeletal bodies animate instead
        rend.material.color = Color.Lerp(baseColor, new Color(1f, 0.25f, 0.15f), k);
    }

    /// <summary>Drives the 8-way move blend: Speed = planar magnitude (Idle↔Move
    /// gate), MoveX/MoveY = velocity direction in enemy-local space so strafing
    /// picks the side-jog clip instead of moonwalking a forward jog.</summary>
    private void SetMoveAnim(Vector3 worldVel)
    {
        if (enemyAnimator == null) return;
        var planar = Vector3.ProjectOnPlane(worldVel, Vector3.up);
        enemyAnimator.SetFloat(SpeedId, planar.magnitude);
        var local = planar.sqrMagnitude > 0.001f ? transform.InverseTransformDirection(planar.normalized)
                                                : Vector3.zero;
        enemyAnimator.SetFloat(MoveXId, local.x);
        enemyAnimator.SetFloat(MoveYId, local.z);
    }

    private void SetAnimFloat(int id, float v) { if (enemyAnimator != null) enemyAnimator.SetFloat(id, v); }
    private void SetAnimBool(int id, bool v) { if (enemyAnimator != null) enemyAnimator.SetBool(id, v); }
    private void SetAnimTrigger(int id) { if (enemyAnimator != null) enemyAnimator.SetTrigger(id); }
}

internal static class EnemyAiVectorExt
{
    public static Vector3 FlatPlanar(this Vector3 v) { v.y = 0f; return v.normalized; }
}
