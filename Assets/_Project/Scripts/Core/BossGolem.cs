using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// FortGolem boss brain — two phases behind a fog gate. Dormant until
/// Engage() (the gate calls it); then chases at a menace-walk and runs a
/// move table: phase 1 = greatsword reads, phase 2 (HP &lt;= phaseAt) adds a
/// radial SLAM + a multi-hit BARRAGE after a Roar transition beat.
///
/// Attacks are CrossFade'd clips from BossBase.controller; hit resolution
/// uses each clip's NORMALIZED window against AnimatorStateInfo so the table
/// never hardcodes clip lengths. Authored root XZ drives the golem through
/// its swings via RootMotionRelay (IRootMotionOwner below) — code never adds
/// a scripted lunge on top. Walk cycles are scripted cc.Move (baked clips).
///
/// Poise mirrors EnemyAI: hits drain the meter, a break Staggered-locks it —
/// the punish window. ResetAll() rides GameLoop's death/rest resets beside
/// EnemyAI.RespawnAll.
/// </summary>
[RequireComponent(typeof(Health))]
public sealed class BossGolem : MonoBehaviour, IRootMotionOwner, IBossEngage
{
    private enum Mode { Dormant, Chase, Attack, Roar, Staggered }

    private sealed class Move
    {
        public int id;                      // Animator.StringToHash(state name)
        public Vector2[] windows;           // normalized hit windows in the clip
        public float range, arc, damage;
        public float cooldown;
        public float nextAllowed;           // time.time gate
        public float aoeRadius;             // >0 = radial slam, arc ignored
        public float pickMin, pickMax;      // selection band — pickMax 0 = use range
        public float weight = 1f;           // selection odds vs other candidates
        public float trackAt;               // face until this nt; 0 = first window start
        public int win;                     // per-activation window cursor
        public bool effectPlayed;
        public float sampledTime;
        public bool struck;                 // hit already applied this window
    }

    [Header("Identity")]
    [SerializeField] private string displayName = "Warden of the Deep Ruin";
    [Tooltip("HP fraction where the Roar transition fires and phase 2 moves unlock.")]
    [SerializeField, Range(0.1f, 0.9f)] private float phaseAt = 0.5f;

    [Header("Movement")]
    [SerializeField, Min(0.5f)] private float walkSpeed = 1.6f;
    [Tooltip("Phase-2 move/chase multiplier — the golem stops shambling.")]
    [SerializeField, Min(1f)] private float phase2Haste = 1.25f;
    [SerializeField, Min(0.5f)] private float leashRange = 45f;
    [SerializeField, Min(60f)] private float turnSpeed = 300f;
    [SerializeField] private float gravity = -20f;

    [Header("Poise")]
    [Tooltip("Bosses resist flinching — hits drain this; a break staggers.")]
    [SerializeField, Min(0f)] private float poiseMax = 60f;
    [SerializeField, Min(0f)] private float poiseRegen = 7f;
    [SerializeField, Min(0f)] private float poiseRegenDelay = 1.6f;
    [SerializeField, Min(0.5f)] private float staggerTime = 1.7f;

    [Header("Presentation")]
    [SerializeField] private Animator bossAnimator;
    [Tooltip("Rivals FX burst played on Roar — the phase-change beat.")]
    [SerializeField] private FxCue roarFx;
    [Tooltip("FX spawned under the slam's impact point each Slam window.")]
    [SerializeField] private FxCue slamFx;

    // ---------- move tables (state names match BossBase.controller) ----------

    private Move[] PhaseMoves(int phase) => phase == 0 ? p1Moves : p2Moves;
    private Move[] p1Moves, p2Moves;

    private void BuildMoves()
    {
        // pickMin/pickMax = the distance band the move is chosen in (pickMax 0
        // = use range). trackAt = last normalized point the golem still steers
        // — after that the swing is committed and sidestepping beats it.
        // Charge is the gap-closer: picked only outside melee reach, the dash
        // attack's authored root motion carries it in.
        p1Moves = new[]
        {
            new Move { id = Animator.StringToHash("Jab"),    range = 3.0f, arc = 130f, damage = 14f, cooldown = 1.1f,
                       pickMin = 0f,   pickMax = 3.0f, weight = 1.3f, trackAt = 0.30f,
                       windows = new[] { new Vector2(0.36f, 0.50f) } },
            new Move { id = Animator.StringToHash("Swing"),  range = 3.8f, arc = 170f, damage = 24f, cooldown = 1.6f,
                       pickMin = 1.5f, pickMax = 4.6f, weight = 1.0f, trackAt = 0.36f,
                       windows = new[] { new Vector2(0.44f, 0.60f) } },
            new Move { id = Animator.StringToHash("Crush"),  range = 4.0f, arc = 110f, damage = 40f, cooldown = 3.4f,
                       pickMin = 2.0f, pickMax = 4.8f, weight = 0.7f, trackAt = 0.45f,
                       windows = new[] { new Vector2(0.55f, 0.68f) } },
            new Move { id = Animator.StringToHash("Charge"), range = 5.0f, arc = 80f,  damage = 30f, cooldown = 4.5f,
                       pickMin = 4.2f, pickMax = 11f,  weight = 1.0f, trackAt = 0.20f,
                       windows = new[] { new Vector2(0.42f, 0.58f) } },
        };
        p2Moves = new[]
        {
            new Move { id = Animator.StringToHash("Jab"),    range = 3.0f, arc = 130f, damage = 16f, cooldown = 0.9f,
                       pickMin = 0f,   pickMax = 3.0f, weight = 1.3f, trackAt = 0.30f,
                       windows = new[] { new Vector2(0.36f, 0.50f) } },
            new Move { id = Animator.StringToHash("Swing"),  range = 3.8f, arc = 170f, damage = 26f, cooldown = 1.3f,
                       pickMin = 1.5f, pickMax = 4.6f, weight = 1.0f, trackAt = 0.36f,
                       windows = new[] { new Vector2(0.44f, 0.60f) } },
            new Move { id = Animator.StringToHash("Crush"),  range = 4.0f, arc = 110f, damage = 42f, cooldown = 2.8f,
                       pickMin = 2.0f, pickMax = 4.8f, weight = 0.7f, trackAt = 0.45f,
                       windows = new[] { new Vector2(0.55f, 0.68f) } },
            new Move { id = Animator.StringToHash("Charge"), range = 5.0f, arc = 80f,  damage = 32f, cooldown = 3.5f,
                       pickMin = 4.0f, pickMax = 12f,  weight = 1.1f, trackAt = 0.20f,
                       windows = new[] { new Vector2(0.42f, 0.58f) } },
            new Move { id = Animator.StringToHash("Barrage"), range = 3.6f, arc = 140f, damage = 17f, cooldown = 3.4f,
                       pickMin = 0f,   pickMax = 4.2f, weight = 0.9f, trackAt = 0.26f,
                       windows = new[] { new Vector2(0.30f, 0.42f), new Vector2(0.62f, 0.75f) } },
            new Move { id = Animator.StringToHash("Slam"),   range = 0f,   arc = 360f, damage = 30f, cooldown = 5f,
                       pickMin = 0f,   pickMax = 5.2f, weight = 0.8f, trackAt = 0.40f,
                       windows = new[] { new Vector2(0.52f, 0.58f) }, aoeRadius = 5.2f },
        };
    }

    // ---------- state ----------

    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int LocomotionId = Animator.StringToHash("Locomotion");
    private static readonly int RoarId = Animator.StringToHash("Roar");
    private static readonly int StaggerId = Animator.StringToHash("Stagger");
    private static readonly int DieId = Animator.StringToHash("Die");

    private static readonly List<BossGolem> all = new();

    /// <summary>Any living, engaged boss — same contract as EnemyAI.AnyEngaged.</summary>
    public static bool AnyEngaged
    {
        get
        {
            for (var i = 0; i < all.Count; i++) if (all[i].Engaged) return true;
            return false;
        }
    }
    public bool Engaged => health != null && !health.IsDead && mode != Mode.Dormant;

    // IRootMotionOwner — attacks consume authored root XZ; Y stays code gravity.
    public bool DriveRootMotion => mode == Mode.Attack;
    public bool AllowRootY => false;

    private Health health;
    private CharacterController cc;
    private Transform player;
    private PlayerHealth playerHealth;
    private PlayerState playerState;
    private Mode mode = Mode.Dormant;
    private int phase;
    private Move current;
    private int lastMoveId;                 // anti-repeat — same move can't chain-pick
    private float modeT, poise, poiseWait, verticalSpeed;
    private bool roared;

    private void OnEnable() => all.Add(this);
    private void OnDisable() => all.Remove(this);

    private void Awake()
    {
        health = GetComponent<Health>();
        health.Damaged += OnDamaged;
        health.Died += OnDied;
        cc = GetComponent<CharacterController>();
        if (bossAnimator == null) bossAnimator = GetComponentInChildren<Animator>();
        if (bossAnimator != null) health.SuppressDeathMotion = true;
        poise = poiseMax;
        BuildMoves();
        var p = FindFirstObjectByType<PlayerLocomotion>();
        if (p != null)
        {
            player = p.transform;
            playerHealth = p.GetComponent<PlayerHealth>();
            playerState = p.GetComponent<PlayerState>();
        }
    }

    private void OnDestroy()
    {
        if (health != null) { health.Damaged -= OnDamaged; health.Died -= OnDied; }
    }

    /// <summary>Fog gate / trigger calls this — the fight starts.</summary>
    public void Engage()
    {
        if (health.IsDead || mode != Mode.Dormant) return;
        mode = Mode.Chase;
        GameHud.Boss(health, displayName, phaseAt);
    }

    /// <summary>Souls reset: back to spawn, full HP, dormant, gate reopens.</summary>
    public void Respawn()
    {
        health.ResetHealth();
        mode = Mode.Dormant;
        phase = 0;
        roared = false;
        current = null;
        poise = poiseMax;
        poiseWait = 0f;
        verticalSpeed = 0f;
        if (bossAnimator != null) bossAnimator.Rebind();
        GameHud.BossClear();
    }

    /// <summary>Every boss back to its spawn — called beside EnemyAI.RespawnAll.</summary>
    public static void ResetAll()
    {
        for (var i = 0; i < all.Count; i++) all[i].Respawn();
    }

    private void OnDied()
    {
        SoulsWallet.Add(health.SoulsReward);
        current = null;
        GameHud.BossClear();
        GameHud.Banner("GREAT ENEMY FELLED", 3f);
        if (bossAnimator != null) bossAnimator.CrossFadeInFixedTime(DieId, 0.15f, 0);
    }

    private void OnDamaged(float amount, Vector3 from, float poiseDamage, ReactionProfile reaction)
    {
        if (health.IsDead || mode == Mode.Dormant) return;
        if (poiseMax <= 0f) return;
        poise -= poiseDamage;
        poiseWait = poiseRegenDelay;
        if (poise > 0f || mode == Mode.Staggered) return;
        // Posture break — the punish window.
        poise = poiseMax;
        mode = Mode.Staggered;
        modeT = 0f;
        current = null;
        if (bossAnimator != null) bossAnimator.CrossFadeInFixedTime(StaggerId, 0.12f, 0);
    }

    private void Update()
    {
        if (health.IsDead || player == null) return;
        if (playerHealth != null && playerHealth.IsDead)
        {
            // Player died — the golem lumbers back to guard its arena.
            Respawn();
            return;
        }
        if (mode == Mode.Dormant) return;

        var dt = Time.deltaTime;
        modeT += dt;
        if (poiseWait > 0f) poiseWait -= dt;
        else if (poise < poiseMax) poise = Mathf.Min(poiseMax, poise + poiseRegen * dt);

        var toPlayer = player.position - transform.position;
        toPlayer.y = 0f;
        var dist = toPlayer.magnitude;
        if (dist > leashRange) { Respawn(); return; }

        var haste = phase == 1 ? phase2Haste : 1f;

        switch (mode)
        {
            case Mode.Chase:
                Face(toPlayer, dt);
                if (!roared && health.Current <= health.Max * phaseAt) { EnterRoar(); break; }
                // Attack whenever a move is ready and in its band — the Charge
                // gap-closer's band reaches past melee, so engageRange isn't
                // the hard gate it used to be.
                if (ReadyAny(dist))
                {
                    var move = PickMove(dist);
                    if (move != null) { EnterAttack(move); break; }
                }
                SetSpeed(1f);
                Step(toPlayer.normalized * walkSpeed * haste * dt);
                break;

            case Mode.Attack:
                TickAttack(dt);
                break;

            case Mode.Roar:
                // Roar ends with its clip — fall back to a timeout if the
                // animator/state is missing so the fight can't soft-lock.
                if (bossAnimator == null || modeT >= 3f
                    || (IsState(bossAnimator.GetCurrentAnimatorStateInfo(0), RoarId)
                        && bossAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime >= 1f))
                    ToChase();
                ApplyGravity(dt);
                break;

            case Mode.Staggered:
                if (modeT >= staggerTime) ToChase();
                ApplyGravity(dt);
                break;
        }
    }

    private void EnterRoar()
    {
        roared = true;
        phase = 1;              // phase-2 table + haste from the roar onward
        mode = Mode.Roar;
        modeT = 0f;
        current = null;
        if (bossAnimator != null) bossAnimator.CrossFadeInFixedTime(RoarId, 0.2f, 0);
        GameHud.Toast(displayName + " — AWAKENED");
        ArtFx.Spawn(roarFx, transform, bossAnimator, player);
        var cam = FindFirstObjectByType<PlayerCameraController>();
        if (cam != null) cam.Shake(0.5f);
    }

    /// <summary>Any ready move whose distance band brackets the player — the
    /// single gate for entering Attack (melee AND the far-reaching Charge).</summary>
    private bool ReadyAny(float dist)
    {
        foreach (var m in PhaseMoves(phase))
            if (ReadyInBand(m, dist)) return true;
        return false;
    }

    private static bool ReadyInBand(Move m, float dist)
        => Time.time >= m.nextAllowed && dist >= m.pickMin && dist <= PickReach(m) * 1.15f;

    private static float PickReach(Move m)
        => m.pickMax > 0f ? m.pickMax : m.aoeRadius > 0f ? m.aoeRadius : m.range;

    /// <summary>Weighted roll among moves that are off cooldown AND inside
    /// their distance band; the last move used is excluded when alternatives
    /// exist so the fight can't Jab-Jab-Jab.</summary>
    private Move PickMove(float dist)
    {
        var moves = PhaseMoves(phase);
        var eligible = 0;
        var total = 0f;
        foreach (var m in moves) if (ReadyInBand(m, dist)) eligible++;
        foreach (var m in moves)
            if (ReadyInBand(m, dist) && !(eligible > 1 && m.id == lastMoveId)) total += m.weight;
        if (total <= 0f) return null;

        var roll = Random.value * total;
        foreach (var m in moves)
        {
            if (!ReadyInBand(m, dist) || (eligible > 1 && m.id == lastMoveId)) continue;
            roll -= m.weight;
            if (roll <= 0f) return m;
        }
        return null;
    }

    private void EnterAttack(Move m)
    {
        current = m;
        lastMoveId = m.id;
        m.win = 0;
        m.struck = false;
        m.effectPlayed = false;
        m.sampledTime = 0f;
        m.nextAllowed = Time.time + m.cooldown;
        mode = Mode.Attack;
        modeT = 0f;
        SetSpeed(0f);
        // Longer blend-in than a human swing — mass eases into the pose.
        if (bossAnimator != null) bossAnimator.CrossFadeInFixedTime(m.id, 0.22f, 0);
    }

    private void TickAttack(float dt)
    {
        var m = current;
        if (m == null || bossAnimator == null) { mode = Mode.Chase; return; }
        var info = bossAnimator.GetCurrentAnimatorStateInfo(0);
        // The CrossFade takes a frame to land — skip until the move's state owns it.
        // modeT is the bail-out if the state never arrives (missing/misnamed).
        if (!IsState(info, m.id))
        {
            if (modeT > 4f) { ToChase(); current = null; }
            return;
        }
        var nt = info.normalizedTime;

        // Track during the windup only — once the swing commits (trackAt, by
        // default the first hit window) the blade can't follow a sidestep.
        if (nt < (m.trackAt > 0f ? m.trackAt : m.windows[0].x))
        {
            var to = player.position - transform.position;
            to.y = 0f;
            Face(to.normalized, dt);
        }

        // Sweep for the entire authored contact window, once per successful hit.
        // Include a crossed window on slow frames instead of dropping that attack.
        while (m.win < m.windows.Length && nt >= m.windows[m.win].x)
        {
            if (!m.struck && m.sampledTime <= m.windows[m.win].y)
            {
                m.struck = Strike(m, !m.effectPlayed);
                m.effectPlayed = true;
            }
            if (nt <= m.windows[m.win].y) break;
            m.win++; m.struck = false; m.effectPlayed = false;
        }
        m.sampledTime = nt;
        if (nt >= 1f) { ToChase(); current = null; }
    }

    /// <summary>Back to the locomotion state — every mode that can resume the
    /// fight funnels through here so the CrossFade can't be forgotten.</summary>
    private void ToChase()
    {
        mode = Mode.Chase;
        modeT = 0f;
        if (bossAnimator != null
            && !IsState(bossAnimator.GetCurrentAnimatorStateInfo(0), LocomotionId))
            bossAnimator.CrossFadeInFixedTime(LocomotionId, 0.35f, 0);
    }

    /// <summary>Apply the move's hit at the window — arc sweep vs player, or
    /// radial shockwave for AOE slams. Dodge i-frames whiff both.</summary>
    private bool Strike(Move m, bool present = true)
    {
        if (playerHealth == null) return false;
        var invuln = (playerState != null && playerState.IsInvulnerable)
                     || playerHealth.Invulnerable;
        bool hit;
        if (m.aoeRadius > 0f)
        {
            if (present && slamFx != null && slamFx.prefab != null)
            {
                slamFx.offset = transform.forward * 2f; // impact point ahead of the fists
                ArtFx.Spawn(slamFx, transform, bossAnimator, player);
            }
            var cam = FindFirstObjectByType<PlayerCameraController>();
            if (present && cam != null) cam.Shake(0.35f);
            var flat = Vector3.ProjectOnPlane(player.position - transform.position, Vector3.up);
            hit = flat.magnitude <= m.aoeRadius && Mathf.Abs(player.position.y - transform.position.y) < 2.5f;
        }
        else
        {
            var to = player.position - transform.position;
            to.y = 0f;
            hit = to.magnitude <= m.range
                  && Vector3.Angle(transform.forward, to) <= m.arc * 0.5f
                  && Mathf.Abs(player.position.y - transform.position.y) < 3f;
        }
        if (hit && !invuln) { playerHealth.TakeDamage(m.damage, transform.position); return true; }
        return false;
    }

    private bool IsState(AnimatorStateInfo info, int id) => info.shortNameHash == id;

    private void Face(Vector3 dir, float dt)
    {
        if (dir.sqrMagnitude < 0.0001f) return;
        var want = Quaternion.LookRotation(dir.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * dt);
    }

    private void Step(Vector3 delta)
    {
        if (cc == null) { transform.position += delta; return; }
        verticalSpeed = cc.isGrounded ? -2f : Mathf.Max(verticalSpeed + gravity * Time.deltaTime, -50f);
        cc.Move(delta + Vector3.up * verticalSpeed * Time.deltaTime);
    }

    private void ApplyGravity(float dt) => Step(Vector3.zero);

    private void SetSpeed(float v)
    {
        if (bossAnimator != null) bossAnimator.SetFloat(SpeedId, v);
    }
}

