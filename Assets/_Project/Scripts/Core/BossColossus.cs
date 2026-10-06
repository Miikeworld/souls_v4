using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Giant boss brain (the Sunken Vault's swollen king): a Crimson-saturated
/// giant with a war hammer, behind a fog gate. Souls-hard and readable:
/// big committed swings with late tracking cut-offs, punishable recoveries,
/// poise breaks that drop it to a long kneel.
///   Phase 1 — Sweep (wide arc), Smash (overhead + ground shockwave ring),
///             Stomp (radial — punishes standing between its feet), Leap (gap-closer slam).
///   Phase 2 at <see cref="phaseAt"/> — cinematic roar; Combo joins, rings grow, faster.
///   Enraged at <see cref="enrageAt"/> — crimson aura, quicker cadence, double rings.
/// Camera: <see cref="BossCinematics"/> intro (once per session), phase shot and
/// death orbit; while engaged <see cref="PlayerCameraController.SetBossFocus"/>
/// pulls the orbit back and lifts the frame toward the king.
/// Attacks are CrossFade'd clips (ColossusBase.controller); hit windows are
/// normalized clip time; authored root XZ moves the capsule via RootMotionRelay.
/// Resets ride <see cref="EnemyAI.WorldReset"/> (death / checkpoint rest).
/// </summary>
[RequireComponent(typeof(Health))]
public sealed class BossColossus : MonoBehaviour, IRootMotionOwner, IBossEngage
{
    private enum Mode { Dormant, Intro, Chase, Attack, Roar, Staggered }
    private enum Hit { Arc, Radial }

    private sealed class Move
    {
        public string state;
        public int id;
        public Vector2[] windows;
        public Hit kind;
        public float range, arc, damage, cooldown, nextAllowed;
        public float pickMin, pickMax, weight = 1f, trackAt;
        public float aoeRadius, aoeForward;          // Radial: centre ahead of the pelvis
        public float ringRadius, ringDamage;         // >0 spawns a shockwave at the impact
        public float shake = 0.35f;
        public int win; public bool struck, effectPlayed; public float sampledTime;
    }

    [Header("Identity")]
    [SerializeField] private string displayName = "The Swollen King";
    [SerializeField, Range(0.1f, 0.9f)] private float phaseAt = 0.6f;
    [SerializeField, Range(0.05f, 0.5f)] private float enrageAt = 0.25f;

    [Header("Body")]
    [Tooltip("Standing height in metres — scales cinematic framing and the camera focus.")]
    [SerializeField, Min(1f)] private float height = 5.4f;
    [SerializeField, Min(0.5f)] private float walkSpeed = 2.2f;
    [SerializeField, Min(1f)] private float phase2Haste = 1.2f;
    [SerializeField, Min(1f)] private float enrageHaste = 1.15f;
    [SerializeField, Min(0.5f)] private float leashRange = 45f;
    [SerializeField, Min(30f)] private float turnSpeed = 120f;
    [SerializeField] private float gravity = -20f;

    [Header("Poise")]
    [SerializeField, Min(0f)] private float poiseMax = 140f;
    [SerializeField, Min(0f)] private float poiseRegen = 9f;
    [SerializeField, Min(0f)] private float poiseRegenDelay = 2f;
    [SerializeField, Min(0.5f)] private float staggerTime = 3.4f;
    [Tooltip("Seconds before the stagger ends that the GetUp clip starts.")]
    [SerializeField, Min(0f)] private float getUpLead = 1.5f;

    [Header("Presentation")]
    [SerializeField] private Animator bossAnimator;
    [Tooltip("Optional crimson aura object switched on when enraged.")]
    [SerializeField] private GameObject enrageAura;
    [SerializeField] private FxCue roarFx;
    [SerializeField] private FxCue slamFx;
    [SerializeField] private Color ringColor = new Color(1f, 0.12f, 0.1f);

    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int LocomotionId = Animator.StringToHash("Locomotion");
    private static readonly int RoarId = Animator.StringToHash("Roar");
    private static readonly int StaggerId = Animator.StringToHash("Stagger");
    private static readonly int DieId = Animator.StringToHash("Die");
    private static readonly int GetUpId = Animator.StringToHash("GetUp");

    private static readonly List<BossColossus> all = new();
    private static bool introSeen; // once per session — retries go straight to the fight

    public static bool AnyEngaged
    {
        get
        {
            foreach (var b in all) if (b.Engaged) return true;
            return false;
        }
    }

    public bool Engaged => health != null && !health.IsDead && mode != Mode.Dormant;
    public bool DriveRootMotion => mode == Mode.Attack;
    public bool AllowRootY => false;

    private Move[] p1, p2;
    private Move current;
    private string lastMove;
    private Health health;
    private CharacterController cc;
    private Transform player;
    private PlayerHealth playerHealth;
    private PlayerState playerState;
    private PlayerCameraController cam;
    private Mode mode = Mode.Dormant;
    private int phase; // 0, 1 (phase 2), 2 (enraged)
    private float modeT, poise, poiseWait, verticalSpeed;
    private Light auraLight;
    private bool defeated, gettingUp;

    private void OnEnable() { all.Add(this); EnemyAI.WorldReset += Respawn; }
    private void OnDisable() { all.Remove(this); EnemyAI.WorldReset -= Respawn; }

    private void Awake()
    {
        health = GetComponent<Health>();
        health.Damaged += OnDamaged;
        health.Died += OnDied;
        cc = GetComponent<CharacterController>();
        if (bossAnimator == null) bossAnimator = GetComponentInChildren<Animator>();
        if (bossAnimator != null)
        {
            health.SuppressDeathMotion = true;
            VendorUrp.FixTree(bossAnimator.gameObject); // legacy Standard vendor materials → URP, at runtime only
        }
        poise = poiseMax;
        BuildMoves();
        var p = FindFirstObjectByType<PlayerLocomotion>();
        if (p != null)
        {
            player = p.transform;
            playerHealth = p.GetComponent<PlayerHealth>();
            playerState = p.GetComponent<PlayerState>();
            cam = p.GetComponent<PlayerCameraController>();
        }
        if (enrageAura != null) enrageAura.SetActive(false);
    }

    private void OnDestroy()
    {
        if (health != null) { health.Damaged -= OnDamaged; health.Died -= OnDied; }
    }

    private Move M(string state, Hit kind, float range, float arc, float dmg, float cd, float pickMin, float pickMax,
                   float weight, float track, params Vector2[] windows)
        => new Move { state = state, id = Animator.StringToHash(state), kind = kind, range = range, arc = arc, damage = dmg,
                      cooldown = cd, pickMin = pickMin, pickMax = pickMax, weight = weight, trackAt = track, windows = windows };

    private void BuildMoves()
    {
        var r = height / 5.4f; // reach scales with the body
        p1 = new[]
        {
            M("Sweep", Hit.Arc, 7f * r, 200f, 30f, 2.2f, 0f, 6.5f * r, 1.2f, 0.36f, new Vector2(0.44f, 0.60f)),
            Ring(M("Smash", Hit.Arc, 6.5f * r, 70f, 46f, 3.6f, 2f * r, 7f * r, 1f, 0.45f, new Vector2(0.55f, 0.68f)), 9f * r, 18f, 0.6f),
            Radial(M("Stomp", Hit.Radial, 0f, 360f, 32f, 5f, 0f, 3.6f * r, 0.9f, 0.4f, new Vector2(0.52f, 0.58f)), 5f * r, 0f, 0.55f),
            Radial(M("Leap", Hit.Arc, 6f * r, 110f, 40f, 6f, 8f * r, 22f * r, 1f, 0.22f, new Vector2(0.42f, 0.58f)), 4.5f * r, 3f * r, 0.7f),
        };
        p2 = new[]
        {
            M("Sweep", Hit.Arc, 7f * r, 200f, 32f, 1.8f, 0f, 6.5f * r, 1.1f, 0.34f, new Vector2(0.44f, 0.60f)),
            Ring(M("Smash", Hit.Arc, 6.5f * r, 70f, 48f, 3f, 2f * r, 7f * r, 1f, 0.45f, new Vector2(0.55f, 0.68f)), 13f * r, 22f, 0.7f),
            Ring(Radial(M("Stomp", Hit.Radial, 0f, 360f, 34f, 4.2f, 0f, 3.6f * r, 0.9f, 0.4f, new Vector2(0.52f, 0.58f)), 5f * r, 0f, 0.6f), 10f * r, 18f, 0.6f),
            Radial(M("Leap", Hit.Arc, 6f * r, 110f, 42f, 4.8f, 7f * r, 22f * r, 1.1f, 0.22f, new Vector2(0.42f, 0.58f)), 4.5f * r, 3f * r, 0.75f),
            M("Combo", Hit.Arc, 6.5f * r, 150f, 24f, 3.4f, 0f, 6f * r, 1.1f, 0.26f, new Vector2(0.30f, 0.42f), new Vector2(0.62f, 0.75f)),
        };
    }

    private static Move Ring(Move m, float radius, float dmg, float shake) { m.ringRadius = radius; m.ringDamage = dmg; m.shake = Mathf.Max(m.shake, shake); return m; }
    private static Move Radial(Move m, float radius, float forward, float shake) { m.aoeRadius = radius; m.aoeForward = forward; m.shake = Mathf.Max(m.shake, shake); return m; }

    // ---------- engage / reset ----------

    public void Engage()
    {
        if (health.IsDead || mode != Mode.Dormant) return;
        cam?.SetBossFocus(transform, height, 1.35f + height * 0.06f); // bigger body, wider pull-back
        if (!introSeen)
        {
            introSeen = true;
            mode = Mode.Intro;
            modeT = 0f;
            PlayIntro();
            return;
        }
        BeginFight();
    }

    private void BeginFight()
    {
        mode = Mode.Chase;
        modeT = 0f;
        GameHud.Boss(health, displayName, phaseAt);
    }

    public void Respawn()
    {
        if (defeated) return; // a felled boss stays felled — rests and deaths never revive it
        health.ResetHealth();
        mode = Mode.Dormant;
        phase = 0;
        current = null;
        poise = poiseMax;
        poiseWait = 0f;
        verticalSpeed = 0f;
        if (bossAnimator != null) bossAnimator.Rebind();
        if (enrageAura != null) enrageAura.SetActive(false);
        if (auraLight != null) auraLight.enabled = false;
        cam?.ClearBossFocus();
        GameHud.BossClear();
    }

    public static void ResetAll()
    {
        foreach (var b in all) b.Respawn();
    }

    private void OnDied()
    {
        defeated = true;
        SoulsWallet.Add(health.SoulsReward);
        current = null;
        cam?.ClearBossFocus();
        GameHud.BossClear();
        if (bossAnimator != null) bossAnimator.CrossFadeInFixedTime(DieId, 0.15f, 0);
        PlayDeath();
        GameHud.Banner("GREAT ENEMY FELLED", 3f);
    }

    private void OnDamaged(float amount, Vector3 from, float poiseDamage, ReactionProfile reaction)
    {
        if (health.IsDead || mode is Mode.Dormant or Mode.Intro) return;
        if (poiseMax <= 0f) return;
        poise -= poiseDamage;
        poiseWait = poiseRegenDelay;
        if (poise > 0f || mode == Mode.Staggered) return;
        // Posture break — it drops to a knee: the punish window.
        poise = poiseMax;
        mode = Mode.Staggered;
        modeT = 0f;
        gettingUp = false;
        current = null;
        if (bossAnimator != null) bossAnimator.CrossFadeInFixedTime(StaggerId, 0.12f, 0);
        cam?.Shake(0.45f);
        GameHud.Toast("POSTURE BROKEN");
    }

    // ---------- tick ----------

    private void Update()
    {
        if (health.IsDead || player == null) return;
        if (playerHealth != null && playerHealth.IsDead) { if (mode != Mode.Dormant) Respawn(); return; }
        if (mode == Mode.Dormant) return;
        var dt = Time.deltaTime;
        modeT += dt;
        if (mode == Mode.Intro)
        {
            ApplyGravity(dt);
            if (!BossCinematics.Playing && modeT > 0.5f) BeginFight();
            return;
        }
        if (poiseWait > 0f) poiseWait -= dt;
        else if (poise < poiseMax) poise = Mathf.Min(poiseMax, poise + poiseRegen * dt);

        var toPlayer = player.position - transform.position;
        toPlayer.y = 0f;
        var dist = toPlayer.magnitude;
        if (dist > leashRange) { Respawn(); return; }
        var haste = (phase >= 1 ? phase2Haste : 1f) * (phase >= 2 ? enrageHaste : 1f);

        switch (mode)
        {
            case Mode.Chase:
                Face(toPlayer, dt);
                if (phase == 0 && health.Current <= health.Max * phaseAt) { EnterPhase2(); break; }
                if (phase == 1 && health.Current <= health.Max * enrageAt) Enrage();
                var move = PickMove(dist);
                if (move != null) { EnterAttack(move); break; }
                SetSpeed(1f);
                Step(toPlayer.normalized * walkSpeed * haste * dt);
                break;

            case Mode.Attack:
                if (bossAnimator != null) bossAnimator.speed = haste > 1f ? Mathf.Lerp(1f, haste, 0.6f) : 1f;
                TickAttack(dt);
                break;

            case Mode.Roar:
                ApplyGravity(dt);
                if (!BossCinematics.Playing && modeT > 0.8f) ToChase();
                break;

            case Mode.Staggered:
                ApplyGravity(dt);
                // Down on the floor, then the get-up eats the last stretch of the window.
                if (!gettingUp && modeT >= staggerTime - getUpLead && bossAnimator != null && bossAnimator.HasState(0, GetUpId))
                {
                    gettingUp = true;
                    bossAnimator.CrossFadeInFixedTime(GetUpId, 0.2f, 0);
                }
                if (modeT >= staggerTime) { gettingUp = false; ToChase(); }
                break;
        }
    }

    private void EnterPhase2()
    {
        phase = 1;
        mode = Mode.Roar;
        modeT = 0f;
        current = null;
        SetSpeed(0f);
        PlayPhaseShot();
    }

    private void Enrage()
    {
        phase = 2;
        if (enrageAura != null) enrageAura.SetActive(true);
        if (auraLight == null)
        {
            var go = new GameObject("EnrageLight");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.up * height * 0.55f;
            auraLight = go.AddComponent<Light>();
            auraLight.type = LightType.Point;
            auraLight.color = new Color(1f, 0.1f, 0.08f);
            auraLight.range = height * 2.2f;
            auraLight.intensity = 3f;
            auraLight.shadows = LightShadows.None;
        }
        auraLight.enabled = true;
        foreach (var m in p2) m.cooldown *= 0.75f;
        var smash = System.Array.Find(p2, m => m.state == "Smash");
        if (smash != null) smash.ringDamage *= 1.15f;
        GameHud.Toast(displayName.ToUpperInvariant() + " — ENRAGED");
        cam?.Shake(0.5f);
    }

    private Move[] Moves => phase == 0 ? p1 : p2;

    private Move PickMove(float dist)
    {
        var moves = Moves;
        var eligible = 0;
        foreach (var m in moves) if (Ready(m, dist)) eligible++;
        if (eligible == 0) return null;
        var total = 0f;
        foreach (var m in moves) if (Ready(m, dist) && !(eligible > 1 && m.state == lastMove)) total += m.weight;
        var roll = Random.value * total;
        foreach (var m in moves)
        {
            if (!Ready(m, dist) || (eligible > 1 && m.state == lastMove)) continue;
            roll -= m.weight;
            if (roll <= 0f) return m;
        }
        return null;
    }

    private static bool Ready(Move m, float dist) => Time.time >= m.nextAllowed && dist >= m.pickMin && dist <= m.pickMax;

    private void EnterAttack(Move m)
    {
        current = m;
        lastMove = m.state;
        m.win = 0; m.struck = false; m.effectPlayed = false; m.sampledTime = 0f;
        m.nextAllowed = Time.time + m.cooldown;
        mode = Mode.Attack;
        modeT = 0f;
        SetSpeed(0f);
        if (bossAnimator != null) bossAnimator.CrossFadeInFixedTime(m.id, 0.25f, 0);
    }

    private void TickAttack(float dt)
    {
        var m = current;
        if (m == null || bossAnimator == null) { ToChase(); return; }
        var info = bossAnimator.GetCurrentAnimatorStateInfo(0);
        if (info.shortNameHash != m.id)
        {
            if (modeT > 4f) { ToChase(); current = null; }
            return;
        }
        var nt = info.normalizedTime;
        if (nt < (m.trackAt > 0f ? m.trackAt : m.windows[0].x))
        {
            var to = player.position - transform.position;
            to.y = 0f;
            Face(to, dt);
        }
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

    private void ToChase()
    {
        mode = Mode.Chase;
        modeT = 0f;
        if (bossAnimator != null)
        {
            bossAnimator.speed = 1f;
            if (bossAnimator.GetCurrentAnimatorStateInfo(0).shortNameHash != LocomotionId)
                bossAnimator.CrossFadeInFixedTime(LocomotionId, 0.4f, 0);
        }
    }

    /// <summary>Resolve one window: the arc or radial hit, plus the impact
    /// presentation (FX, distance-scaled shake, optional shockwave ring) on
    /// the window's first sample.</summary>
    private bool Strike(Move m, bool present)
    {
        if (playerHealth == null) return false;
        var impact = transform.position + transform.forward * (m.aoeRadius > 0f ? m.aoeForward : Mathf.Min(m.range * 0.6f, 4f));
        if (present)
        {
            var d = Vector3.Distance(player.position, impact);
            cam?.Shake(m.shake * Mathf.Clamp01(1.2f - d / 28f));
            if (slamFx != null && slamFx.prefab != null && (m.aoeRadius > 0f || m.ringRadius > 0f))
            {
                slamFx.offset = transform.InverseTransformDirection(impact - transform.position);
                ArtFx.Spawn(slamFx, transform, bossAnimator, player);
            }
            if (m.ringRadius > 0f)
            {
                ShockRing.Spawn(impact, m.ringRadius, 10f, m.ringDamage, ringColor);
                if (phase >= 2 && m.state == "Smash") StartCoroutine(DelayedRing(impact, m.ringRadius, m.ringDamage, 0.45f));
            }
        }
        var invuln = (playerState != null && playerState.IsInvulnerable) || playerHealth.Invulnerable;
        bool hit;
        if (m.kind == Hit.Radial || m.aoeRadius > 0f && m.range <= 0f)
        {
            var flat = Vector3.ProjectOnPlane(player.position - impact, Vector3.up);
            hit = flat.magnitude <= m.aoeRadius && Mathf.Abs(player.position.y - transform.position.y) < 2.2f;
        }
        else
        {
            var to = Vector3.ProjectOnPlane(player.position - transform.position, Vector3.up);
            hit = to.magnitude <= m.range && Vector3.Angle(transform.forward, to) <= m.arc * 0.5f
                  && Mathf.Abs(player.position.y - transform.position.y) < height * 0.8f;
            // Leap also blasts the landing zone.
            if (!hit && m.aoeRadius > 0f)
                hit = Vector3.ProjectOnPlane(player.position - impact, Vector3.up).magnitude <= m.aoeRadius
                      && Mathf.Abs(player.position.y - transform.position.y) < 2.2f;
        }
        if (hit && !invuln) { playerHealth.TakeDamage(m.damage, transform.position); return true; }
        return false;
    }

    private System.Collections.IEnumerator DelayedRing(Vector3 at, float radius, float dmg, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (!health.IsDead) ShockRing.Spawn(at, radius, 12f, dmg, ringColor);
    }

    // ---------- cinematics ----------

    private void PlayIntro()
    {
        var h = height;
        BossCinematics.Play(transform, 1f, new[]
        {
            new BossCinematics.Key(0f,   new Vector3(h * 1.7f, 0.9f, h * 2.0f),  new Vector3(0f, h * 0.35f, 0f), 52f),
            new BossCinematics.Key(1.8f, new Vector3(h * 0.95f, 1.1f, h * 1.55f), new Vector3(0f, h * 0.62f, 0f), 42f),
            new BossCinematics.Key(3.0f, new Vector3(h * 0.12f, h * 0.5f, h * 0.8f), new Vector3(0f, h * 0.86f, 0f), 34f),
            new BossCinematics.Key(4.4f, new Vector3(0f, h * 0.58f, h * 0.68f),   new Vector3(0f, h * 0.9f, 0f), 30f),
        }, new (float, System.Action)[]
        {
            (2.3f, Roar),
            (2.6f, () => ShockRing.Spawn(transform.position, height * 2.2f, 14f, 0f, ringColor)),
        }, blendIn: 0.8f, blendOut: 1.1f);
    }

    private void PlayPhaseShot()
    {
        var h = height;
        BossCinematics.Play(transform, 1f, new[]
        {
            new BossCinematics.Key(0f,   new Vector3(h * 0.45f, h * 0.3f, h * 1.5f),  new Vector3(0f, h * 0.7f, 0f), 46f),
            new BossCinematics.Key(2.8f, new Vector3(-h * 0.45f, h * 0.36f, h * 1.35f), new Vector3(0f, h * 0.78f, 0f), 38f),
        }, new (float, System.Action)[]
        {
            (0.5f, Roar),
            (1.0f, () => ShockRing.Spawn(transform.position, height * 2.6f, 15f, 0f, ringColor)),
        }, blendIn: 0.45f, blendOut: 0.8f);
    }

    private void PlayDeath()
    {
        var h = height;
        BossCinematics.Play(transform, 1f, new[]
        {
            new BossCinematics.Key(0f,   new Vector3(h * 1.4f, h * 0.45f, h * 1.2f),  new Vector3(0f, h * 0.45f, 0f), 44f),
            new BossCinematics.Key(2.4f, new Vector3(-h * 1.3f, h * 0.55f, h * 1.4f), new Vector3(0f, h * 0.3f, 0f), 40f),
        }, null, blendIn: 0.35f, blendOut: 1f, slowMo: 0.35f);
    }

    private void Roar()
    {
        if (bossAnimator != null) bossAnimator.CrossFadeInFixedTime(RoarId, 0.2f, 0);
        ArtFx.Spawn(roarFx, transform, bossAnimator, player);
        cam?.Shake(0.6f);
    }

    // ---------- motion ----------

    private void Face(Vector3 dir, float dt)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return;
        var want = Quaternion.LookRotation(dir.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * dt);
    }

    private void Step(Vector3 delta)
    {
        if (cc == null || !cc.enabled) { transform.position += delta; return; }
        verticalSpeed = cc.isGrounded ? -2f : Mathf.Max(verticalSpeed + gravity * Time.deltaTime, -50f);
        cc.Move(delta + Vector3.up * verticalSpeed * Time.deltaTime);
    }

    private void ApplyGravity(float dt) => Step(Vector3.zero);

    private void SetSpeed(float v)
    {
        if (bossAnimator != null) bossAnimator.SetFloat(SpeedId, v);
    }
}
