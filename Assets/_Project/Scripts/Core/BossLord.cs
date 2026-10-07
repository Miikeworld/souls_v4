using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Vess, the Warden — the final boss, three phases behind a fog gate.
///
///   P1 the Warden       A greatsword swordsman: DrawSlash / TwinCut→TwinCut2 /
///        HeavenCut (a held, delayed overhead) / RushDraw / Wolf Fang / Bonesunder,
///        all authored Big Sword takes — his own blade does the work.
///   P2 "still in control" (HP ≤ phaseAt): the pillars crack, stones lift, and
///        controlled Crimson magic joins the swordwork — Crimson Sweep (jump),
///        King's Spear (side-step), Executioner's Delay (wait, then dodge),
///        Crimson Cyclone (a travelling spin), Crown of Blades (six weapons,
///        later TEN), Reaper's Wheel (axes rolling down marked lanes),
///        Impaler's Ring (spears around you, thrusting in pairs), Shattered Step,
///        and once, Thousand-Blade Judgment (3 m up, a rain of weapons).
///   P3 "the Core has taken control" (on the first death): he collapses, his
///        sword breaks, the Core beats three times, the SAME sword rises out of
///        the floor reforged ×1.55 and the sanctum tears into floating wall-run
///        slabs. Heavy greatsword strings plus Twin Rupture, Crimson Flood,
///        Ruinous Sweep, King's Fall, Worldsplitter, Crimson Guillotine, the
///        Armory (weapons ripped out of the arena itself) and Grave of Kings —
///        and at finaleAt, End of the Warden: the movement exam ending in an
///        aerial strike on the exposed Core.
///
/// Every dangerous attack reads body pose (WardenPose silhouettes over the clip)
/// → colour/VFX tell (blade heat, glint, crimson marks) → sound → attack; a red
/// floor marker is never the only warning. Crimson = damage here, purple = the
/// suit can use this, pale red = a two-frame impact peak. His hits come from his
/// blade (WardenBlade sweeps the real sword against your body); his misses hit
/// the room — grooves in the floor, chips and toppled pillars. The giant moves
/// get a held breath of silence (WardenAudio.Duck), a slight widening of the
/// frame (PlayerCameraController.Frame) and a short impact pause — and the big
/// misses get stuck in the stone: punish windows.
/// Simple swings are Move-table entries with normalized windows (Attack mode,
/// authored XZ via the RootMotionRelay); signature attacks are coroutines
/// (Sequence mode) that own motion, levitation and hazards. ResetAll() rides
/// GameLoop's death/rest resets; a felled Warden stays felled.
/// </summary>
[RequireComponent(typeof(Health))]
public sealed partial class BossLord : MonoBehaviour, IRootMotionOwner, IBossEngage
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
        public bool effectPlayed, struck, glinted, impacted, held;
        public float sampledTime, holdUntil;
        // Warden additions
        public System.Func<IEnumerator> seq;      // signature attack — runs as a coroutine
        public float maxHeight = 3f;               // melee only connects below this (low sweeps are jumpable)
        public float impact, shake, hitstop;       // layered ground impact where the blade meets the floor
        public float ringRadius, ringDamage;       // a jumpable Core ring leaves the impact
        public float speed = 1f;                   // animator speed while it plays (P3 weight)
        public bool needsSanctum;
        public float reach = 0.55f;                // blade-sweep slack around the player's body
        public float force = 1f;                   // pillar strike weight of the swing
        public float holdAt = -1f;                 // anticipation hold: freeze here…
        public Vector2 hold;                       // …for a random time in this range (s)
        public float release = 1.2f;               // animator speed after the hold
        public string name = "";
        // Melee rework: strings, tracking, closing the gap.
        public Link[] links = System.Array.Empty<Link>();  // branches out of this cut (one is rolled at linkAt)
        public float linkAt = 0.7f;                // normalized time the follow-up decision is made
        public float linkChance = 0.75f;           // base chance to keep the string going
        public float warp;                         // metres he may add on top of the clip to reach you
        public float warpStop = 1.7f;              // where the warp wants him to stop, from your body
        public float lateTrack = -1f;              // deg/s he still turns once the cut is live (<0 = phase default)
        public float delayChance;                  // chance this cut gets a held, delayed release
        public string tag = "";                    // recency family (strings share one)
        public bool gapCloser, antiAir, turner, special, utility;
        public float holdAtBase = -1f;
        public int played;                         // the state actually playing (fallbacks included)
        public int requiresState;                  // scripted read that only makes sense with this state present
        public bool linkRolled;
        public bool rootY;                         // authored leap arc: the clip's root Y reaches the capsule while it plays
    }

    /// <summary>One branch out of a cut: where the string can go next, and when.</summary>
    private sealed class Link
    {
        public Move to;
        public float weight = 1f;
        public float maxDist = 5f;                 // only while you're still this close
        public float minDist;                      // …or only once you've backed off this far (gap-closing follow-ups)
        public Link(Move to, float weight = 1f, float maxDist = 5f, float minDist = 0f)
        {
            this.to = to; this.weight = weight; this.maxDist = maxDist; this.minDist = minDist;
        }
    }

    [Header("Identity")]
    [SerializeField] private string displayName = "Vess, Hollow Blade";
    [Tooltip("HP fraction where Phase 2 ('still in control') begins.")]
    [SerializeField, Range(0.1f, 0.9f)] private float phaseAt = 0.5f;
    [Tooltip("HP fraction restored when the Core takes control (Phase 3).")]
    [SerializeField, Range(0.1f, 0.9f)] private float reviveAt = 0.4f;
    [Tooltip("Phase 2: HP fraction of max where Thousand-Blade Judgment fires, once.")]
    [SerializeField, Range(0.02f, 0.45f)] private float judgmentAt = 0.22f;
    [Tooltip("Phase 2: below this HP fraction of max the crown grows from six weapons to ten.")]
    [SerializeField, Range(0.05f, 0.5f)] private float crownTenAt = 0.34f;
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
    [Tooltip("Poise damage multiplier while he is stuck/exposed after a big miss (the punish window).")]
    [SerializeField, Min(1f)] private float exposedPoiseScale = 2.5f;

    [Header("Presentation")]
    [SerializeField] private Animator bossAnimator;

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
    private static readonly int TwinCut2Id = Animator.StringToHash("TwinCut2");
    private static readonly int HeavenCutId = Animator.StringToHash("HeavenCut");
    private static readonly int RushDrawId = Animator.StringToHash("RushDraw");
    private static readonly int WolfFangId = Animator.StringToHash("WolfFang");
    private static readonly int BonesunderId = Animator.StringToHash("Bonesunder");
    private static readonly int CounterCleaveId = Animator.StringToHash("CounterCleave");
    private static readonly int CycloneId = Animator.StringToHash("Cyclone");
    private static readonly int GuillotineId = Animator.StringToHash("Guillotine");
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
    // Melee rework — the rest of the Big Sword set (BossLordBase.controller, Setup Warden Fight).
    private static readonly int SevenCut5Id = Animator.StringToHash("SevenCut5");
    private static readonly int SevenCut6Id = Animator.StringToHash("SevenCut6");
    private static readonly int SevenCut7Id = Animator.StringToHash("SevenCut7");
    private static readonly int FourCut1AId = Animator.StringToHash("FourCut1A");
    private static readonly int FourCut1BId = Animator.StringToHash("FourCut1B");
    private static readonly int FourCut2Id = Animator.StringToHash("FourCut2");
    private static readonly int FourCut3Id = Animator.StringToHash("FourCut3");
    private static readonly int FourCut4Id = Animator.StringToHash("FourCut4");
    private static readonly int ThreeCut1Id = Animator.StringToHash("ThreeCut1");
    private static readonly int ThreeCut2Id = Animator.StringToHash("ThreeCut2");
    private static readonly int ThreeCut3Id = Animator.StringToHash("ThreeCut3");
    private static readonly int AshenCleaveId = Animator.StringToHash("AshenCleave");
    private static readonly int WolfFang1Id = Animator.StringToHash("WolfFang1");
    private static readonly int WolfFang2Id = Animator.StringToHash("WolfFang2");
    private static readonly int GraveRendId = Animator.StringToHash("GraveRend");
    private static readonly int SkyfallId = Animator.StringToHash("Skyfall");
    private static readonly int UpperCutId = Animator.StringToHash("UpperCut");
    private static readonly int MoonRushId = Animator.StringToHash("MoonRush");
    private static readonly int GuardStartId = Animator.StringToHash("GuardStart");
    private static readonly int GuardLoopId = Animator.StringToHash("GuardLoop");
    private static readonly int GuardAcceptId = Animator.StringToHash("GuardAccept");
    private static readonly int GuardAttackId = Animator.StringToHash("GuardAttack");
    private static readonly int GuardEndId = Animator.StringToHash("GuardEnd");
    private static readonly int BackStepId = Animator.StringToHash("BackStep");
    private static readonly int SideStepLId = Animator.StringToHash("SideStepL");
    private static readonly int SideStepRId = Animator.StringToHash("SideStepR");
    private static readonly int LeapRiseId = Animator.StringToHash("LeapRise");
    private static readonly int LeapAirId = Animator.StringToHash("LeapAir");
    private static readonly int LeapSlamId = Animator.StringToHash("LeapSlam");
    private static readonly int LeapLandId = Animator.StringToHash("LeapLand");
    private static readonly int DrawIntroId = Animator.StringToHash("DrawIntro");
    private static readonly int CastFlickId = Animator.StringToHash("CastFlick");

    /// <summary>When a Warden state is missing (setup not re-run) the nearest
    /// older state plays instead — timing falls back to each beat's timeout.</summary>
    private static int Fallback(int id)
    {
        if (id == SevenCut5Id) return HeavyComboId;
        if (id == SevenCut6Id || id == ThreeCut2Id || id == AshenCleaveId) return TwinCutId;
        if (id == SevenCut7Id || id == GraveRendId || id == SkyfallId) return HeavenCutId;
        if (id == FourCut1AId || id == FourCut1BId || id == ThreeCut1Id || id == UpperCutId) return DrawSlashId;
        if (id == FourCut2Id) return HeavySweepId;
        if (id == FourCut3Id || id == ThreeCut3Id) return TwinCut2Id;
        if (id == FourCut4Id) return HeavySmashId;
        if (id == WolfFang1Id || id == WolfFang2Id) return WolfFangId;
        if (id == MoonRushId) return RushDrawId;
        if (id == GuardAttackId) return CounterCleaveId;
        if (id == SideStepLId || id == BackStepId) return DodgeLId;
        if (id == SideStepRId) return DodgeRId;
        if (id == LeapRiseId) return KingsFallCrouchId;
        if (id == LeapAirId) return KingsFallHangId;
        if (id == LeapSlamId) return KingsFallDropId;
        if (id == LeapLandId) return KingsFallLandId;
        if (id == DrawIntroId) return GreatswordPullId;
        if (id == CastFlickId) return CrownRaiseId;
        if (id == CrimsonSweepId || id == TwinCut2Id || id == WolfFangId) return TwinCutId;
        if (id == KingsSpearId || id == GuillotineId) return RushDrawId;
        if (id == BonesunderId || id == CounterCleaveId) return DrawSlashId;
        if (id == ExecutionerId || id == HeavySmashId || id == TwinRuptureId || id == CorePlantId || id == WorldsplitterId) return HeavenCutId;
        if (id == HeavySweepId || id == HeavyComboId || id == RuinousSweepId || id == CycloneId) return TwinCutId;
        if (id == CrownRaiseId || id == CrownHoldId || id == CrownLandId || id == GreatswordPullId) return RoarId;
        if (id == LocomotionHeavyId) return LocomotionId;
        if (id == StaggerHeavyId || id == CollapseId || id == StandStruggleId) return StaggerId;
        if (id == KingsFallCrouchId || id == KingsFallHangId || id == KingsFallDropId || id == KingsFallLandId) return StaggerId;
        if (id == FinalFallId) return DieId;
        return 0;
    }

    /// <summary>The state hash <see cref="Play"/> would actually play for <paramref name="id"/>
    /// (the fallback chain walked, up to four hops); 0 when nothing resolves.</summary>
    internal int ResolvedState(int id)
    {
        if (bossAnimator == null || bossAnimator.runtimeAnimatorController == null) return 0;
        for (var hop = 0; hop < 4 && id != 0 && !bossAnimator.HasState(0, id); hop++) id = Fallback(id);
        return id != 0 && bossAnimator.HasState(0, id) ? id : 0;
    }

    private static readonly HashSet<int> missingStates = new HashSet<int>();
    private static bool staleControllerWarned;

    /// <summary>A state hash back to its name (the static "…Id" fields) — only for the one-time warnings.</summary>
    private static string StateName(int id)
    {
        foreach (var f in typeof(BossLord).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
            if (f.IsInitOnly && f.FieldType == typeof(int) && f.Name.EndsWith("Id") && (int)f.GetValue(null) == id)
                return f.Name.Substring(0, f.Name.Length - 2);
        return id.ToString();
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
    // Only an authored leap arc (a rootY table cut) lifts him, and only from its own live instance.
    public bool AllowRootY => mode == Mode.Attack && current != null && current.rootY && current.played != 0
                              && bossAnimator != null && FreshInstance(current.played, out _);

    // ---------- runtime ----------

    private Health health;
    private CharacterController cc;
    private Targetable targetable;
    private FootGrounding grounding;
    private WardenBody body;
    private WardenBlade blade;
    private WardenPose pose;
    private Transform player;
    private PlayerHealth playerHealth;
    private PlayerState playerState;
    private PlayerLocomotion playerLoco;
    private AttackController playerAttack;
    private PlayerCameraController cam;
    private readonly List<Renderer> visuals = new List<Renderer>();

    private Mode mode = Mode.Dormant;
    private int phase;                 // 0 the Warden, 1 in control, 2 Core
    private bool revived, roared, judgmentDone, judgmentPending, finaleDone, finalePending, defeated, finishing;
    private Move current;
    private int lastMoveId;
    private float modeT, poise, poiseWait, verticalSpeed, stepT, exposedUntil;
    private int dodgeSide;
    private bool levitating, seqRootMotion, seqMotion, counterReady;
    private float floorY;
    private Move[] p1Moves, p2Moves, p3Moves;
    private Move crown, step, spear, sweep, executioner, cyclone, wheel, impaler;
    private Move flood, ruinous, kingsFall, splitter, grave, twin, guillotine, armory;
    private Move counter;
    private AudioSource drone;
    private Light sun;
    private Color fog0;
    private float sun0 = -1f;

    private CoreSanctum Sanctum => sanctum != null ? sanctum : (sanctum = CoreSanctum.Active);
    private float BodyHeight => cc != null ? cc.height : 2.4f;
    private Vector3 Chest => body != null ? body.ChestPosition : transform.position + Vector3.up * 1.4f;
    private Vector3 ArenaCenter => Sanctum != null ? Sanctum.Center : home;
    private Vector3 home;
    private bool Exposed => Time.time < exposedUntil;
    private bool staggerPunish, corePrompt;
    private float punishT, tetherT, edgeChipT;
    // A state (re)started this frame reads time 0 until the animator has run; a crossfade
    // onto the state already playing is verified the next frame (hard restart if ignored).
    private int restartState, restartFrame = -1, verifyState, verifyFrame;
    private SkinnedMeshRenderer bodySkin;

    /// <summary>The visible body's skinned renderer (the largest under his Animator) — his afterimages bake it.</summary>
    public SkinnedMeshRenderer BodySkin
    {
        get
        {
            if (bodySkin != null || bossAnimator == null) return bodySkin;
            var best = 0;
            foreach (var s in bossAnimator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var n = s.sharedMesh != null ? s.sharedMesh.vertexCount : 0;
                if (n > best) { best = n; bodySkin = s; }
            }
            return bodySkin;
        }
    }

    private void OnEnable() => all.Add(this);

    private void OnDisable()
    {
        all.Remove(this);
        if (corePrompt) { corePrompt = false; GameHud.HidePrompt(); }
    }

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
        blade = gameObject.AddComponent<WardenBlade>();
        blade.Init(transform);
        if (bossAnimator != null)
        {
            pose = bossAnimator.gameObject.AddComponent<WardenPose>();
            pose.Init(bossAnimator, transform);
        }
        SyncSword();
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

    private void Start()
    {
        floorY = Sanctum != null ? Sanctum.Center.y : transform.position.y;
        blade.FloorY = floorY;
        // The child Animator is initialised by now (Awake order across objects isn't guaranteed).
        InitMelee();
    }

    private void OnDestroy()
    {
        if (health != null) { health.Damaged -= OnDamaged; health.Died -= OnDied; }
        WardenAudio.StopLoop(drone);
        WardenAudio.Bed(false);
        Mood(0f);
    }

    /// <summary>Point the blade tracker and the pose solver at whatever is in his hand now.</summary>
    private void SyncSword()
    {
        var s = body != null ? body.HandSword : null;
        if (blade != null) blade.SetSword(s);
        if (pose != null) pose.SetSword(s);
    }

    // ================================================================== move tables

    private void BuildMoves()
    {
        // The signature reads (Phase 2 / 3). Long cooldowns, and a shared "special"
        // budget (BossLordMelee) keeps them as spice between sword strings — the
        // swordwork is the backbone of every phase.
        sweep = new Move { name = "CrimsonSweep", seq = CrimsonSweep, cooldown = 14f, pickMin = 0f, pickMax = 6f, weight = 1f, special = true };
        spear = new Move { name = "KingsSpear", seq = KingsSpear, cooldown = 13f, pickMin = 4f, pickMax = 15f, weight = 1.1f, special = true, gapCloser = true };
        executioner = new Move { name = "Executioner", seq = ExecutionersDelay, cooldown = 16f, pickMin = 0f, pickMax = 5.2f, weight = 0.9f, special = true };
        crown = new Move { name = "Crown", seq = CrownOfBlades, cooldown = 26f, pickMin = 3f, pickMax = 22f, weight = 1f, special = true };
        step = new Move { name = "ShatteredStep", seq = ShatteredStep, cooldown = 11f, pickMin = 5f, pickMax = 20f, weight = 1f, special = true, gapCloser = true };
        cyclone = new Move { name = "Cyclone", seq = CrimsonCyclone, cooldown = 18f, pickMin = 0f, pickMax = 7f, weight = 0.9f, special = true, turner = true };
        wheel = new Move { name = "ReapersWheel", seq = ReapersWheel, cooldown = 24f, pickMin = 6f, pickMax = 24f, weight = 0.8f, special = true };
        impaler = new Move { name = "Impaler", seq = ImpalersRing, cooldown = 24f, pickMin = 3f, pickMax = 20f, weight = 0.8f, special = true };

        twin = new Move { name = "TwinRupture", seq = TwinRupture, cooldown = 15f, pickMin = 0f, pickMax = 7f, weight = 1f, special = true, turner = true };
        flood = new Move { name = "CrimsonFlood", seq = CrimsonFlood, cooldown = 34f, pickMin = 0f, pickMax = 30f, weight = 0.9f, needsSanctum = true, special = true };
        ruinous = new Move { name = "RuinousSweep", seq = RuinousSweep, cooldown = 26f, pickMin = 0f, pickMax = 12f, weight = 0.8f, needsSanctum = true, special = true };
        kingsFall = new Move { name = "KingsFall", seq = KingsFall, cooldown = 20f, pickMin = 6f, pickMax = 28f, weight = 1.1f, special = true, gapCloser = true };
        splitter = new Move { name = "Worldsplitter", seq = Worldsplitter, cooldown = 18f, pickMin = 3f, pickMax = 22f, weight = 1f, special = true };
        grave = new Move { name = "GraveOfKings", seq = GraveOfKings, cooldown = 30f, pickMin = 0f, pickMax = 22f, weight = 0.7f, special = true };
        guillotine = new Move { name = "Guillotine", seq = CrimsonGuillotine, cooldown = 20f, pickMin = 4f, pickMax = 14f, weight = 1f, special = true, gapCloser = true };
        armory = new Move { name = "Armory", seq = ArmoryOfTheFallen, cooldown = 26f, pickMin = 4f, pickMax = 24f, weight = 0.85f, special = true };

        BuildMelee(out var p1, out var p2, out var p3);
        var p2Special = new[] { sweep, spear, executioner, cyclone, crown, wheel, impaler, step };
        var p3Special = new[] { twin, flood, ruinous, kingsFall, splitter, guillotine, armory, grave };
        p1Moves = p1.ToArray();
        p2Moves = Concat(p2, p2Special);
        p3Moves = Concat(p3, p3Special);
    }

    private static Move[] Concat(List<Move> a, Move[] b)
    {
        var r = new Move[a.Count + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Count);
        return r;
    }

    private Move[] PhaseMoves() => phase == 0 ? p1Moves : phase == 1 ? p2Moves : p3Moves;

    // ================================================================== engage / reset

    /// <summary>Fog gate / trigger calls this — the fight starts.</summary>
    public void Engage()
    {
        if (defeated || health.IsDead || mode != Mode.Dormant) return;
        mode = Mode.Chase;
        floorY = Sanctum != null ? Sanctum.Center.y : home.y;
        blade.FloorY = floorY;
        InitMelee();
        if (!staleControllerWarned && bossAnimator != null && bossAnimator.runtimeAnimatorController != null
            && bossAnimator.isInitialized && (!bossAnimator.HasState(0, SevenCut5Id) || !hasMoveXY))
        {
            staleControllerWarned = true;
            Debug.LogWarning("[Warden] BossLordBase.controller is out of date — run Tools > Project Restart > Setup Warden Fight (Core Sanctum); cuts fall back to the old katana takes", this);
        }
        GameHud.Boss(health, displayName, phaseAt);
        WardenAudio.Bed(true);
        ResetMelee();
        // His first act is to draw the blade — the fight's "this is the weapon" beat.
        StartSequence(DrawIntro());
    }

    /// <summary>Souls reset: back to spawn, full HP, dormant, arena whole again.</summary>
    public void Respawn()
    {
        if (defeated) return;
        StopAllCoroutines();
        WardenHazard.ClearAll();
        WardenAudio.StopLoop(drone);
        WardenAudio.Bed(false);
        WardenAudio.Unduck();
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
        exposedUntil = 0f;
        counterReady = staggerPunish = false;
        foreach (var list in new[] { p1Moves, p2Moves, p3Moves }) foreach (var m in list) m.nextAllowed = 0f;
        ResetMelee();
        body.ResetAll();
        SetBodySize(1f);
        SyncSword();
        pose?.Kill();
        Sanctum?.ResetArena();
        if (bossAnimator != null) { AnimSpeed(1f); bossAnimator.Rebind(); }
        restartState = verifyState = 0;
        GameHud.BossClear();
        // No finale prompt survives a death.
        corePrompt = false;
        GameHud.HidePrompt();
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
            WardenAudio.Bed(false);
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
        WardenAudio.Bed(false);
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
        // Stuck in the stone after a big miss: every hit bites deep into his posture
        // (purple chips — the suit is making him pay).
        if (Exposed)
        {
            poiseDamage *= exposedPoiseScale;
            WardenFx.Chips(Chest, 4, 2.2f, Vector3.zero, 0.4f, WardenFx.PurpleBright, 1f);
        }
        if (mode == Mode.Chase || mode == Mode.Attack) pressure = Mathf.Min(3f, pressure + 0.6f);
        if (mode == Mode.Sequence && !Exposed) return;
        if (mode == Mode.Dodge) return;
        // In control: sometimes he simply isn't there for the follow-up — and answers.
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
        if (mode == Mode.Sequence)
        {
            // A posture break mid-punish ends the sequence where he stands.
            StopAllCoroutines();
            WardenHazard.ClearAll();
            EndSequenceState();
        }
        // The break reads on his body, never as text: the impact peak, a faceted ring and
        // crimson chips off the chest, the blade going cold with its tip dropping, his
        // heart lurching — and purple on the chest (PunishRead) when the suit can punish
        // it (P3 / exposed).
        staggerPunish = phase == 2 || Exposed;
        mode = Mode.Staggered;
        modeT = 0f;
        current = null;
        Play(phase == 2 ? StaggerHeavyId : StaggerId, 0.12f);
        var chest = Chest;
        WardenFx.Peak(chest, 0.5f);
        WardenFx.Pulse(chest, ToCam(chest), 0.2f, 1.1f, 0.3f, WardenFx.Crimson, 1.2f, WardenFx.CoreSides);
        WardenFx.Chips(chest, 14, 3.4f, Vector3.zero, 0.55f, WardenFx.Crimson, 1.3f);
        blade.Swinging = false;
        blade.Heat = 0f;
        if (blade.HasBlade) WardenFx.Chips(blade.Tip, 6, 1.4f, Vector3.down * 0.6f, 0.45f, WardenFx.CrimsonDeep, 1f);
        body.Beat(1.3f);
        WardenAudio.Play("shatter", chest, 0.8f, 0.8f);
        WardenFx.HitStop(0.06f);
    }

    // ================================================================== tick

    private void Update()
    {
        VerifyRestart();
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

        if (mode != Mode.Attack && mode != Mode.Sequence) { blade.Swinging = false; blade.Heat = Mathf.MoveTowards(blade.Heat, 0f, dt * 3f); }
        if (Exposed || (mode == Mode.Staggered && staggerPunish)) PunishRead(dt);
        else punishT = 0f;

        switch (mode)
        {
            case Mode.Chase:
                if (!roared && phase == 0 && health.Current <= health.Max * phaseAt) { EnterRoar(); break; }
                if (counterReady && dist <= 4.6f)
                {
                    counterReady = false;
                    EnterMove(counter);
                    break;
                }
                TickChase(dt, toPlayer, dist);
                break;

            case Mode.Attack:
                TickAttack(dt);
                break;

            case Mode.Dodge:
                if (DodgeDone()) { counterReady = true; ToChase(); break; }
                Step(transform.right * dodgeSide * dodgeSpeed * Mathf.Lerp(1.6f, 0.4f, Mathf.Clamp01(modeT / 0.55f)) * dt);
                Face(ToPlayerFlat, dt, 360f);
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
        body.SetCore(WardenBody.CoreMode.Glimmer);
        body.Beat(1f);
        WardenFx.Shake(0.4f);
        WardenAudio.Play("thump", Chest, 0.8f, 1f);
        // The phase reads in the player's language, no banner: a giant Core sigil stamped
        // under him, a white facet ring snapping open, the crimson ground ring, chips, an
        // impact star at the chest — and his blade flaring.
        var floor = FloorPoint(transform.position) + Vector3.up * 0.04f;
        WardenFx.Stamp(floor, Vector3.up, 2.6f, WardenFx.Crimson, 1.6f);
        WardenFx.Pulse(floor, Vector3.up, 0.3f, 2.1f, 0.26f, Color.white, 1.4f);
        WardenFx.Ring(floor + Vector3.up * 0.01f, Vector3.up, 0.5f, 11f, 0.6f, WardenFx.Crimson, 0.14f, 24, 0.15f);
        WardenFx.Chips(floor, 18, 4f, Vector3.up * 0.3f, 0.5f, WardenFx.Crimson, 1.3f);
        WardenFx.Dust(floor, 10, 2.2f, 1.2f);
        WardenFx.Star(Chest, WardenFx.Crimson, Vector3.up, 2.2f, 1.8f);
        blade.Heat = 1f;
        blade.Glint(1.3f);
        // The room answers first: every pillar cracks, the stones start to lift.
        Sanctum?.CrackPillars();
        Sanctum?.Instability(0.3f);
        cam?.Frame(4f, 0.12f, 0.3f, 1.6f);
        // The new reads open gradually, never all at once.
        var now = Time.time;
        spear.nextAllowed = now + 3f;
        sweep.nextAllowed = now + 4f;
        cyclone.nextAllowed = now + 5f;
        step.nextAllowed = now + 6f;
        executioner.nextAllowed = now + 8f;
        crown.nextAllowed = now + 10f;
        wheel.nextAllowed = now + 13f;
        impaler.nextAllowed = now + 16f;
    }

    /// <summary>P3 footfalls: every step lands — the player's light-landing tap (a small
    /// crimson facet ring) and a dull thud, no shake.</summary>
    private void HeavyStep(float dt)
    {
        stepT -= dt;
        if (stepT > 0f) return;
        stepT = 0.62f;
        WardenFx.Pulse(FloorPoint(transform.position) + Vector3.up * 0.03f, Vector3.up, 0.12f * bodySize, 0.38f * bodySize, 0.2f, WardenFx.Crimson, 0.8f);
        WardenAudio.Play("thud", transform.position, 0.22f, Random.Range(0.55f, 0.65f));
    }

    /// <summary>The punish read — purple on his chest every half second while the suit can
    /// make him pay (exposed after a big miss, or a Phase 3 posture break).</summary>
    private void PunishRead(float dt)
    {
        punishT -= dt;
        if (punishT > 0f) return;
        punishT = 0.5f;
        var c = Chest;
        WardenFx.Pulse(c, ToCam(c), 0.18f, 0.75f, 0.32f, WardenFx.PurpleBright, 1f, WardenFx.CoreSides);
        WardenFx.Converge(c, 6, 0.9f, 0.28f, WardenFx.Purple);
    }

    private static Vector3 ToCam(Vector3 p)
    {
        var c = Camera.main;
        return c != null ? c.transform.position - p : Vector3.up;
    }

    private bool ReadyInBand(Move m, float dist)
        => Time.time >= m.nextAllowed && dist >= m.pickMin && dist <= PickReach(m) * 1.15f
           && (!m.needsSanctum || Sanctum != null)
           && (m.requiresState == 0 || (bossAnimator != null && bossAnimator.HasState(0, m.requiresState)))
           && (m.seq != null || ResolvedState(m.id) != 0);

    private static float PickReach(Move m) => m.pickMax > 0f ? m.pickMax : m.range;

    private static int Key(Move m) => m.seq != null ? m.seq.Method.Name.GetHashCode() : m.id;

    private void EnterMove(Move m, bool chained = false)
    {
        current = m;
        lastMoveId = Key(m);
        m.nextAllowed = Time.time + m.cooldown;
        if (!chained)
        {
            chainDepth = 0;
            NoteMove(m);
        }
        SetSpeed(0f);
        ZeroMove();
        if (m.seq != null)
        {
            if (m.special) nextSpecialAt = Time.time + SpecialGap();
            StartSequence(m.seq());
            return;
        }
        m.win = 0;
        m.struck = m.glinted = m.impacted = m.held = false;
        m.effectPlayed = false;
        m.sampledTime = 0f;
        m.linkRolled = false;
        // Souls rhythm-breaker: some finishers hang a beat before they come down.
        m.holdAt = m.holdAtBase;
        if (m.holdAt < 0f && m.delayChance > 0f && m.windows.Length > 0 && Random.value < m.delayChance * Aggression)
        {
            m.holdAt = Mathf.Max(0.05f, m.windows[0].x - 0.07f);
            m.hold = new Vector2(0.25f, 0.7f);
            m.release = 1.25f;
        }
        warpLeft = m.warp;
        mode = Mode.Attack;
        modeT = 0f;
        blade.Heat = 0f;
        m.played = Play(m.id, chained ? 0.1f : 0.15f, m.speed);
    }

    private void EnterDodge(Vector3 from)
    {
        mode = Mode.Dodge;
        modeT = 0f;
        current = null;
        dodgeSide = transform.InverseTransformDirection(from - transform.position).x < 0f ? 1 : -1;
        dodgePlayed = Play(dodgeSide > 0 ? SideStepRId : SideStepLId, 0.08f, 1.25f);
        // The player's dodge: an afterimage left at push-off, a few chips flung off.
        WardenFx.Ghost(BodySkin, WardenFx.Crimson, 0.34f);
        WardenFx.Shards(Chest, 6, 2f, WardenFx.Crimson, false, 0.9f, 0.4f, -transform.right * dodgeSide);
        WardenFx.Dust(transform.position, 2, 0.8f, 0.6f);
        WardenAudio.Play("swish", Chest, 0.5f, 1.3f);
    }

    private int dodgePlayed;

    private bool DodgeDone()
    {
        if (bossAnimator == null || dodgePlayed == 0) return modeT >= 0.6f;
        var info = bossAnimator.GetCurrentAnimatorStateInfo(0);
        if (info.shortNameHash != dodgePlayed) return modeT >= 1.2f;
        return info.normalizedTime >= 0.82f || modeT >= 1.2f;
    }

    private void TickAttack(float dt)
    {
        var m = current;
        if (m == null || bossAnimator == null) { EndSwing(); ToChase(); return; }
        // No take and no fallback: the move is over at once — he never stands frozen.
        if (m.played == 0) { EndSwing(); ToChase(); return; }
        if (!FreshInstance(m.played, out var info))
        {
            // Still crossfading in (or restarting the state that was already playing):
            // keep closing and turning — never time windows off the outgoing instance.
            if (modeT < 0.6f) { TrackAndWarp(m, 0f, 0.5f, false, dt); return; }
            if (modeT > 4f) { EndSwing(); ToChase(); }
            return;
        }
        var nt = info.normalizedTime;
        var len = Mathf.Max(0.25f, info.length);
        var strikeAt = m.windows.Length > 0 ? m.windows[Mathf.Min(m.win, m.windows.Length - 1)].x : 0.5f;

        // Anticipation hold: the pose freezes, the blade heats, he keeps a slow bead on you.
        if (m.holdAt >= 0f && !m.held && nt >= m.holdAt)
        {
            m.held = true;
            m.holdUntil = Time.time + Random.Range(m.hold.x, Mathf.Max(m.hold.x, m.hold.y));
            AnimSpeed(0.03f);
            WardenAudio.Play("armour", Chest, 0.4f, Random.Range(0.85f, 1f));
        }
        var holding = m.held && Time.time < m.holdUntil;
        if (m.held && !holding && bossAnimator.speed < 0.1f) AnimSpeed(m.release * m.speed);

        // Tracking + closing the distance (the clip's own travel still plays on top).
        var secondsToStrike = Mathf.Max(0f, (strikeAt - nt) * len / Mathf.Max(0.05f, bossAnimator.speed));
        TrackAndWarp(m, nt, holding ? 0.4f : secondsToStrike, holding, dt);

        if (m.win < m.windows.Length)
        {
            var w = m.windows[m.win];
            var lead = 0.35f / len;
            blade.Heat = holding ? Mathf.MoveTowards(blade.Heat, 1f, dt * 2f)
                                 : Mathf.Max(blade.Heat * (nt > w.y ? 0f : 1f), Mathf.Clamp01((nt - (w.x - lead)) / lead));
            if (!m.glinted && !holding && nt >= w.x - 0.12f / len) { m.glinted = true; blade.Glint(m.damage >= 34f ? 1.25f : 0.9f); }
            blade.Swinging = nt >= w.x - 0.08f / len && nt <= w.y + 0.06f / len;
        }
        else
        {
            blade.Heat = Mathf.MoveTowards(blade.Heat, 0f, dt * 4f);
            blade.Swinging = false;
        }

        while (m.win < m.windows.Length && nt >= m.windows[m.win].x)
        {
            var w = m.windows[m.win];
            if (!m.effectPlayed)
            {
                m.effectPlayed = true;
                blade.NewSwing(m.force);
                WardenAudio.Play(phase == 2 ? "swish" : "slash", transform.position, phase == 2 ? 0.6f : 0.75f, phase == 2 ? 0.7f : Random.Range(0.95f, 1.08f));
                if (phase == 2 && m.impact <= 0f && m.shake > 0f) WardenFx.Shake(m.shake);
            }
            if (nt <= w.y)
            {
                if (!m.struck && BladeContact(m)) m.struck = true;
                if (m.impact > 0f && !m.impacted && blade.FloorContactThisSwing)
                    StrikeImpact(m, blade.LastFloorContact);
                break;
            }
            // Window over: a heavy that never found the floor still lands its weight in front of him.
            if (m.impact > 0f && !m.impacted) StrikeImpact(m, FrontPoint(Mathf.Min(m.range * 0.6f, 3.2f)));
            m.win++;
            m.struck = m.glinted = m.impacted = false;
            m.effectPlayed = false;
        }
        m.sampledTime = nt;

        // Strings: the next cut comes while you're still inside it — or chases you if you backed off.
        if (nt >= m.linkAt && !holding)
        {
            var next = RollLink(m);
            if (next != null)
            {
                chainDepth++;
                EnterMove(next, chained: true);
                return;
            }
        }
        if (nt >= 0.94f)
        {
            EndSwing();
            ToChase();
            current = null;
        }
    }

    private void EndSwing()
    {
        blade.Swinging = false;
        blade.Heat = 0f;
    }

    private void ToChase()
    {
        mode = Mode.Chase;
        modeT = 0f;
        current = null;
        chainDepth = 0;
        nextAttackAt = Time.time + RhythmGap();
        EndSwing();
        if (bossAnimator == null) return;
        AnimSpeed(1f);
        var loco = phase == 2 && bossAnimator.HasState(0, LocomotionHeavyId) ? LocomotionHeavyId : LocomotionId;
        if (bossAnimator.GetCurrentAnimatorStateInfo(0).shortNameHash != loco)
            bossAnimator.CrossFadeInFixedTime(loco, 0.3f, 0);
    }

    /// <summary>Did his blade connect this frame? The real sword's sweep against your
    /// body, a point-blank body check for hugging, and a flat "where is the blade
    /// pointing right now" read that forgives a mis-measured blade axis (the sweep
    /// is exact only when the fitted sword's axis is). The old arc test only when no
    /// sword is rigged. Lands the hit feedback (and the heavy shove) when it does.</summary>
    private bool BladeContact(Move m)
    {
        if (playerHealth == null || player == null) return false;
        var to = player.position - transform.position;
        var h = player.position.y - floorY;
        to.y = 0f;
        var body = cc != null ? cc.radius * transform.lossyScale.x : 0.5f;
        bool hit;
        if (blade.HasBlade)
        {
            var fast = blade.TipSpeed >= blade.MinSpeed;
            var hugging = fast && to.magnitude <= 1.25f + body && Vector3.Angle(transform.forward, to) <= 70f && h > -1.5f && h < m.maxHeight;
            var tipFlat = Vector3.ProjectOnPlane(blade.Tip - transform.position, Vector3.up);
            // Never further than an arm plus the blade, whatever the (mis)measured tip says.
            var arm = 0.9f * bodySize * transform.lossyScale.x;
            var reach = Mathf.Min(Mathf.Max(tipFlat.magnitude, blade.Length * 0.85f), arm + blade.Length) + 0.55f + m.reach * 0.5f;
            var pointing = fast && tipFlat.sqrMagnitude > 0.04f && Vector3.Angle(tipFlat, to) <= 32f
                           && to.magnitude <= reach && h > -1.5f && h < m.maxHeight;
            hit = blade.SweepHits(m.reach) || hugging || pointing;
        }
        else hit = to.magnitude <= m.range && Vector3.Angle(transform.forward, to) <= m.arc * 0.5f && h > -2f && h < m.maxHeight;
        if (!hit || !WardenHazard.Damage(m.damage, transform.position)) return false;
        Connect(m.damage);
        if (m.damage >= 30f) WardenHazard.Shove(transform.position, m.damage >= 40f ? 2.4f : 1.5f);
        return true;
    }

    /// <summary>The feel of his blade landing: a short pause, a sharp kick, a bass hit
    /// on the heavy ones, and the player's own impact star + sparks at the wound, in
    /// crimson, thrown along the blade's travel.</summary>
    private void Connect(float damage)
    {
        var at = WardenHazard.Chest;
        var heavy = damage >= 34f;
        var swing = blade.HasBlade ? blade.TipVelocity : Vector3.zero;
        if (swing.sqrMagnitude < 0.25f) swing = at - (blade.HasBlade ? blade.Tip : Chest);
        WardenFx.Star(at, WardenFx.Crimson, swing, heavy ? 2f : 1.4f);
        HitFx.Spawn(at, swing, heavy ? 1.6f : 1f, WardenFx.Crimson);
        if (heavy) WardenFx.Peak(at, 0.45f);
        WardenFx.HitStop(heavy ? 0.08f : 0.045f);
        WardenFx.Shake(heavy ? 0.3f : 0.16f);
        WardenAudio.Play("metal", at, heavy ? 0.9f : 0.6f, Random.Range(0.9f, 1.05f));
        if (heavy) WardenAudio.Play("sub", at, 0.9f, 1f);
    }

    /// <summary>A heavy's weight landing where his blade met the floor: the layered
    /// impact, the jumpable ring, and the room taking the shock.</summary>
    private void StrikeImpact(Move m, Vector3 point)
    {
        m.impacted = true;
        point = FloorPoint(point);
        WardenFx.Impact(point, m.impact, m.shake, m.hitstop);
        WardenAudio.Play("slam", point, 0.8f, 0.8f);
        if (m.impact >= 1.5f) WardenAudio.Play("sub", point, 0.8f, 1f);
        Sanctum?.StrikeRadius(point, m.impact * 1.4f, m.force * 0.6f);
        if (m.ringRadius > 0f)
        {
            GroundWave.Spawn(point, transform.forward, 360f, m.ringRadius, 10f, 0.9f, m.ringDamage);
            WardenAudio.Play("pulse", point, 0.8f, 1f);
        }
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
        IgnorePlayer(false);
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
        blade.Swinging = false;
        blade.Heat = 0f;
        pose?.Clear(0.2f);
        foreach (var src in seqLoops) WardenAudio.StopLoop(src);
        seqLoops.Clear();
        // A sequence cut short (posture break, set piece) never leaves him empty-handed.
        if (!health.IsDead && !finishing && !defeated)
        {
            if (phase == 2 && body.HasGreatsword) body.ShowSword(2);
            else if (phase < 2) body.ShowSword(1);
            SyncSword();
        }
    }

    private readonly List<AudioSource> seqLoops = new List<AudioSource>();

    /// <summary>A loop owned by the running sequence — stopped with it even when it is cut short.</summary>
    private AudioSource SeqLoop(string id, Vector3 pos, float volume, float pitch)
    {
        var src = WardenAudio.Loop(id, pos, volume, pitch);
        if (src != null) seqLoops.Add(src);
        return src;
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
        // Missing states (setup not re-run) walk down the fallback chain.
        var state = ResolvedState(id);
        if (state == 0)
        {
            if (id != 0 && missingStates.Add(id))
                Debug.LogWarning($"[Warden] Animator state '{StateName(id)}' is missing from {bossAnimator.name}'s controller and has no fallback — that beat runs on its timeout.", this);
            return 0;
        }
        bossAnimator.speed = speed;
        // Re-entering the state already playing (a string link, or a cut right after the
        // same take): crossfade into a NEW instance from its start — verified next frame,
        // with a hard restart if the animator ignored it.
        var again = StateActive(state);
        if (again) bossAnimator.CrossFadeInFixedTime(state, fade, 0, 0f);
        else bossAnimator.CrossFadeInFixedTime(state, fade, 0);
        verifyState = again ? state : 0;
        verifyFrame = Time.frameCount;
        restartState = state;
        restartFrame = Time.frameCount;
        return state;
    }

    /// <summary>Is <paramref name="state"/> the current state, or the one being faded into?</summary>
    private bool StateActive(int state)
    {
        if (bossAnimator.GetCurrentAnimatorStateInfo(0).shortNameHash == state) return true;
        return bossAnimator.IsInTransition(0) && bossAnimator.GetNextAnimatorStateInfo(0).shortNameHash == state;
    }

    /// <summary>The frame after a crossfade onto the playing state: if the animator didn't
    /// start a transition into a new instance, restart the state outright.</summary>
    private void VerifyRestart()
    {
        if (verifyState == 0 || bossAnimator == null || Time.frameCount <= verifyFrame) return;
        var s = verifyState;
        verifyState = 0;
        if (bossAnimator.IsInTransition(0) && bossAnimator.GetNextAnimatorStateInfo(0).shortNameHash == s) return;
        if (bossAnimator.GetCurrentAnimatorStateInfo(0).shortNameHash != s) return;
        bossAnimator.Play(s, 0, 0f);
        restartState = s;
        restartFrame = Time.frameCount;
    }

    /// <summary>True once the move's own instance of <paramref name="state"/> is current and
    /// fully faded in. False on the frame it was (re)started, while its restart is unverified
    /// and while it is still fading in — so a cut entered on the state that was already
    /// playing never reads the outgoing instance's time.</summary>
    private bool FreshInstance(int state, out AnimatorStateInfo info)
    {
        info = bossAnimator.GetCurrentAnimatorStateInfo(0);
        if (state == restartState && Time.frameCount == restartFrame) return false;
        if (state == verifyState) return false;
        if (bossAnimator.IsInTransition(0) && bossAnimator.GetNextAnimatorStateInfo(0).shortNameHash == state) return false;
        return info.shortNameHash == state;
    }

    /// <summary>Normalized time of the LIVE instance of <paramref name="id"/> (resolved through
    /// the fallbacks): the incoming side of a crossfade, 0 on the frame it was (re)started,
    /// -1 when it isn't playing.</summary>
    private float StateTime(int id)
    {
        if (bossAnimator == null || id == 0) return -1f;
        id = ResolvedState(id);
        if (id == 0) return -1f;
        if (id == restartState && Time.frameCount == restartFrame) return 0f;
        if (bossAnimator.IsInTransition(0))
        {
            var next = bossAnimator.GetNextAnimatorStateInfo(0);
            if (next.shortNameHash == id) return next.normalizedTime;
        }
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
            WardenFx.Pulse(FloorPoint(transform.position) + Vector3.up * 0.03f, Vector3.up, 0.15f, 0.6f, 0.24f, WardenFx.Crimson, 1f);
            WardenFx.Dust(transform.position, 3, 1f, 0.8f);
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
        if (!WardenHazard.Damage(damage, o)) return false;
        Connect(damage);
        if (damage >= 30f) WardenHazard.Shove(o, damage >= 40f ? 2.4f : 1.5f);
        return true;
    }

    /// <summary>Where a planted / swung blade meets the floor in front of him.</summary>
    private Vector3 FrontPoint(float forward) => new Vector3(transform.position.x, floorY, transform.position.z) + FlatDir(transform.forward) * forward;

    private Vector3 FloorPoint(Vector3 p) => new Vector3(p.x, floorY, p.z);

    /// <summary>Teleport: shatter into shards, gone, reform at <paramref name="pos"/>.</summary>
    private IEnumerator BlinkTo(Vector3 pos, Vector3 face)
    {
        Play(TeleportOutId, 0.1f, 1.3f);
        yield return Wait(0.18f);
        WardenFx.Shards(Chest, 16, 4.5f, WardenFx.Crimson, false, 1.3f, 0.6f, null, 0.7f);
        WardenFx.Shards(Chest, 8, 1.5f, WardenFx.Crimson, true, 1f, 0.9f, null, 0.5f);
        WardenAudio.Play("shatter", Chest, 0.7f, 1.2f);
        BlinkOut(pos);
        SetVisible(false);
        SetTangible(false);
        yield return Wait(0.3f);
        if (Sanctum != null) pos = Sanctum.ClampToPlatform(pos, 1.2f);
        transform.SetPositionAndRotation(FloorPoint(pos), Quaternion.LookRotation(FlatDir(face), Vector3.up));
        WardenFx.Converge(Chest, 22, 1.3f, 0.16f, WardenFx.Crimson);
        yield return Wait(0.16f);
        SetTangible(true);
        SetVisible(true);
        BlinkIn();
        WardenFx.Shards(Chest, 8, 2.5f, WardenFx.Crimson, false, 1f, 0.4f);
        Play(TeleportInId, 0.06f, 1.3f);
    }

    /// <summary>Leaving, in the player's dodge language: afterimages of his own body
    /// peeling off along the exit toward <paramref name="toward"/>, a crimson facet
    /// ring at his feet and chips. (Call while he is still visible.)</summary>
    private void BlinkOut(Vector3 toward)
    {
        var feet = FloorPoint(transform.position) + Vector3.up * 0.04f;
        var skin = BodySkin;
        if (skin != null)
        {
            // Each ghost bakes the current pose; the root is nudged along the exit for the
            // bake and put straight back (same frame — the capsule never sees it).
            var dir = FlatDir(toward - transform.position);
            var root = transform.position;
            for (var i = 0; i < 3; i++)
            {
                transform.position = root + dir * (0.45f * i);
                WardenFx.Ghost(skin, WardenFx.Crimson, 0.3f + 0.08f * i);
            }
            transform.position = root;
        }
        WardenFx.Pulse(feet, Vector3.up, 0.25f, 1.3f, 0.3f, WardenFx.Crimson, 1.1f);
        WardenFx.Chips(feet, 8, 2.6f, Vector3.up * 0.3f, 0.4f, WardenFx.Crimson, 1.1f);
    }

    /// <summary>Arriving: a white then a crimson 8-gon snapping open at his feet.</summary>
    private void BlinkIn()
    {
        var feet = FloorPoint(transform.position) + Vector3.up * 0.04f;
        WardenFx.Pulse(feet, Vector3.up, 0.1f, 0.8f, 0.14f, Color.white, 1f, WardenFx.CoreSides);
        WardenFx.Pulse(feet, Vector3.up, 0.2f, 1.5f, 0.3f, WardenFx.Crimson, 1.1f, WardenFx.CoreSides);
    }

    // ================================================================== sequence swing helpers

    private bool lastSwingHit;

    /// <summary>A blade swing inside a sequence: heat climbs, the glint flashes just
    /// before <paramref name="from"/>, then trail + blade contact are live until
    /// <paramref name="to"/> (normalized time of state <paramref name="id"/>). A heavy
    /// lands its impact where the blade meets the floor. Sets <see cref="lastSwingHit"/>.
    /// <paramref name="edge"/> = the big sweeps' emphasis, tied to the real blade: chips
    /// flung off the tip along its travel, a Core sigil where the edge bites the floor.</summary>
    private IEnumerator Swing(int id, float from, float to, float damage, float reach = 0.6f, float force = 1f,
                              float impact = 0f, float shake = 0f, float hitstop = 0f, float maxHeight = 3f, float timeout = 2.5f,
                              bool edge = false)
    {
        var m = new Move { damage = damage, reach = reach, force = force, impact = impact, shake = shake, hitstop = hitstop,
                           maxHeight = maxHeight, range = 4.2f, arc = 140f };
        var t = 0f;
        var glinted = false;
        var stamped = false;
        while (t < timeout)
        {
            // A missing/never-current state falls back to a short timed beat.
            var nt = StateTime(id);
            if (nt < 0f ? t >= 0.3f : nt >= from) break;
            t += Time.deltaTime;
            if (nt >= 0f && !glinted && nt >= from - 0.08f) { glinted = true; blade.Glint(); }
            blade.Heat = Mathf.MoveTowards(blade.Heat, 1f, Time.deltaTime * 3f);
            yield return null;
        }
        if (!glinted) blade.Glint();
        blade.NewSwing(force);
        blade.Swinging = true;
        var guard = 0f;
        while (guard < timeout)
        {
            var nt = StateTime(id);
            if (nt < 0f ? guard >= 0.35f : nt > to) break;
            guard += Time.deltaTime;
            if (!m.struck && BladeContact(m)) m.struck = true;
            if (impact > 0f && !m.impacted && blade.FloorContactThisSwing) StrikeImpact(m, blade.LastFloorContact);
            if (edge)
            {
                EdgeChips(Time.deltaTime);
                if (impact <= 0f && !stamped && blade.FloorContactThisSwing)
                {
                    stamped = true;
                    WardenFx.Stamp(FloorPoint(blade.LastFloorContact) + Vector3.up * 0.03f, Vector3.up, 0.8f, WardenFx.Crimson, 0.9f);
                }
            }
            yield return null;
        }
        if (impact > 0f && !m.impacted) StrikeImpact(m, FrontPoint(2.2f));
        blade.Swinging = false;
        blade.Heat = 0f;
        lastSwingHit = m.struck;
    }

    /// <summary>Crimson chips flung off the real blade's tip along its travel while it moves
    /// at swing speed — the big cuts' weight, drawn from the sword itself (never a fake arc).</summary>
    private void EdgeChips(float dt)
    {
        edgeChipT -= dt;
        if (edgeChipT > 0f || !blade.HasBlade || blade.TipSpeed < blade.MinSpeed) return;
        edgeChipT = 0.05f;
        var v = blade.TipVelocity;
        WardenFx.Chips(blade.Tip, 2, Mathf.Min(v.magnitude * 0.25f, 4f), v.normalized * 0.8f, 0.3f, WardenFx.Crimson, 0.9f);
    }

    /// <summary>The punish window after a big miss: the blade is wedged in the stone.
    /// He strains (sparks, grit, armour), every hit bites ×exposedPoiseScale into his
    /// posture, then he rips it free in a spray of stone.</summary>
    private IEnumerator Stuck(float seconds, Vector3 at)
    {
        at = FloorPoint(at);
        exposedUntil = Time.time + seconds;
        AnimSpeed(0.03f);
        WardenAudio.Play("metal", at, 0.7f, 0.6f);
        var scrape = SeqLoop("scrape", at, 0.45f, 0.6f);
        var t = 0f;
        var next = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            if (t >= next)
            {
                next = t + Random.Range(0.18f, 0.3f);
                WardenFx.Sparks(at + Vector3.up * 0.05f, Vector3.up, 4, 3f);
                WardenFx.Debris(at, 1, 2.5f, 0.6f);
                body.Beat(0.4f);
                if (Random.value < 0.35f) WardenAudio.Play("armour", Chest, 0.35f, Random.Range(0.8f, 1.05f));
                // A strain: the frozen pose jerks a frame forward and back.
                AnimSpeed(Random.value < 0.5f ? 0.25f : 0.03f);
            }
            else if (bossAnimator != null && bossAnimator.speed > 0.1f && t > next - 0.12f) AnimSpeed(0.03f);
            yield return null;
        }
        WardenAudio.StopLoop(scrape);
        exposedUntil = 0f;
        // Ripped free.
        WardenFx.Debris(at, 8, 6f, 1.2f, Vector3.up * 0.6f);
        WardenFx.Dust(at, 4, 1.4f, 1f);
        WardenFx.Sparks(at, Vector3.up, 10, 6f);
        WardenAudio.Play("stone", at, 0.9f, 0.7f);
        WardenFx.Shake(0.12f);
        AnimSpeed(1f);
    }

    // ================================================================== Phase 2 — "still in control"

    /// <summary>Sword low, body twisting; the edge heats and glints; then a low sweep —
    /// the blade's own trail carries the cut — and a red floor wave. Jump it.</summary>
    private IEnumerator CrimsonSweep()
    {
        var id = Play(CrimsonSweepId, 0.15f, 0.85f);
        yield return Until(id, 0.24f, 0.9f, 260f);
        AnimSpeed(0.04f);
        WardenAudio.Play("scrape", transform.position, 0.35f, 1.4f);
        WardenFx.Shards(FrontPoint(1.4f) + Vector3.up * 0.2f, 6, 1.2f, WardenFx.Crimson, false, 0.8f, 0.4f);
        var t = 0f;
        while (t < 0.38f) { t += Time.deltaTime; blade.Heat = t / 0.38f; yield return null; }
        AnimSpeed(1.15f);
        WardenAudio.Play("slash", transform.position, 0.9f, 0.85f);
        GroundWave.Spawn(FloorPoint(transform.position), transform.forward, 240f, 10.5f, 13f, 0.85f, 16f, false, 1.2f);
        WardenFx.Shards(FrontPoint(2f) + Vector3.up * 0.15f, 14, 4f, WardenFx.Crimson, false, 0.9f, 0.45f, transform.right);
        WardenFx.Shake(0.12f);
        yield return Swing(id, 0.33f, 0.43f, 24f, 0.6f, 1.2f, maxHeight: 1.1f, edge: true);
        yield return Until(id, 0.95f, 1.6f);
    }

    /// <summary>Blade drawn back to the hip, level, aimed (the Thrust silhouette);
    /// a narrow strip marks the line; the blade heats; the lunge sends a thin
    /// blade-wave down it. Side-step.</summary>
    private IEnumerator KingsSpear()
    {
        var id = Play(KingsSpearId, 0.15f, 0.75f);
        pose?.Set(WardenPose.Kind.Thrust, 0.3f);
        var mark = WardenMark.Line(transform.position, transform.position + FlatDir(transform.forward) * 15f, 0.38f, WardenFx.Crimson, 0.05f, 0.14f);
        var t = 0f;
        while (t < 1.2f && (StateTime(id) < 0.3f || t < 0.5f))
        {
            t += Time.deltaTime;
            if (StateTime(id) >= 0.26f) AnimSpeed(0.04f);
            Face(ToPlayerFlat, Time.deltaTime, 200f);
            var o = FloorPoint(transform.position);
            mark.SetLine(o, o + FlatDir(transform.forward) * 15f, 0.38f);
            mark.SetPulse(t / 1.2f);
            blade.Heat = t / 1.2f;
            yield return null;
        }
        // Aim locked.
        mark.SetPulse(1f);
        WardenAudio.Play("chime", Chest, 0.4f, 0.6f);
        blade.Glint(1.1f);
        AnimSpeed(0.04f);
        yield return Wait(0.22f);
        pose?.Clear(0.06f);
        AnimSpeed(1.2f);
        mark.Release(0.25f);
        var origin = FloorPoint(transform.position);
        var dir = FlatDir(transform.forward);
        BladeWave.Spawn(origin + dir * 0.8f, dir, 18f, 26f, 0.7f, 2.2f, 20f);
        WardenAudio.Play("slash", transform.position, 1f, 1.2f);
        seqRootMotion = true;
        yield return Swing(id, StateTime(id) + 0.02f, 0.68f, 26f, 0.6f, 1.2f, timeout: 1.5f);
        seqRootMotion = false;
        yield return Until(id, 0.95f, 1.5f);
    }

    /// <summary>Sword straight overhead and HELD (the Overhead silhouette) while the
    /// Core brightens, the blade heats and the hum rises — variable hold, the room
    /// goes quiet. Wait for it, then dodge the release. A miss buries the blade.</summary>
    private IEnumerator ExecutionersDelay()
    {
        var id = Play(ExecutionerId, 0.15f, 1f);
        yield return Until(id, 0.3f, 1.2f, 280f);
        pose?.Set(WardenPose.Kind.Overhead, 0.25f);
        yield return Until(id, 0.38f, 0.6f, 280f);
        AnimSpeed(0.02f);
        var hold = Random.Range(0.65f, 1.55f);
        WardenAudio.Duck(hold + 0.2f);
        WardenAudio.Play("hum", Chest, 0.8f, 1.6f / hold);
        WardenAudio.Play("armour", Chest, 0.5f, 0.9f);
        var reach = WardenMark.Line(FloorPoint(transform.position), FrontPoint(4.8f), 0.9f, WardenFx.Crimson, 0.04f, 0.08f);
        var t = 0f;
        while (t < hold)
        {
            t += Time.deltaTime;
            var k = t / hold;
            body.Charge(k);
            blade.Heat = k;
            reach.SetAlpha(0.4f + 0.6f * k);
            Face(ToPlayerFlat, Time.deltaTime, 50f);
            reach.SetLine(FloorPoint(transform.position), FrontPoint(4.8f), 0.9f);
            yield return null;
        }
        reach.Release(0.1f);
        pose?.Clear(0.05f);
        AnimSpeed(1.8f);
        body.Charge(0f);
        WardenAudio.Unduck();
        yield return Swing(id, 0.42f, 0.56f, 38f, 0.65f, 1.8f, 1.3f, 0.28f, 0.06f, timeout: 0.8f);
        body.Beat(1f);
        if (!lastSwingHit)
            yield return Stuck(0.85f, blade.FloorContactThisSwing ? blade.LastFloorContact : FrontPoint(2.2f));
        AnimSpeed(1f);
        yield return Until(id, 0.95f, 1.8f);
    }

    /// <summary>His body fractures into shards; a red line marks where he'll come
    /// back through you. He reforms at its end, blade already moving, and dashes
    /// the line — time the dodge.</summary>
    private IEnumerator ShatteredStep()
    {
        Play(TeleportOutId, 0.1f, 1.3f);
        yield return Wait(0.16f);

        // Reappear on a flank, a dash-length away, aimed through where you stand now.
        var target = FloorPoint(player.position);
        var ang = Random.value < 0.5f ? -flankAngle : flankAngle;
        var away = Quaternion.AngleAxis(ang, Vector3.up) * -FlatDir(player.forward);
        var start = target + away * (teleportRange + 3.5f);
        if (Sanctum != null) start = Sanctum.ClampToPlatform(start, 1.2f);

        WardenFx.Shards(Chest, 16, 4.5f, WardenFx.Crimson, false, 1.3f, 0.6f, null, 0.7f);
        WardenAudio.Play("shatter", Chest, 0.7f, 1.2f);
        BlinkOut(start);
        SetVisible(false);
        SetTangible(false);
        var dir = FlatDir(target - start);
        var len = Vector3.Distance(FloorPoint(start), target) + 4f;
        var mark = WardenMark.Line(start, start + dir * len, 0.95f, WardenFx.Crimson, 0.07f, 0.18f);
        WardenAudio.Play("crack", start, 0.6f, 1.2f);
        var t = 0f;
        while (t < 0.65f) { t += Time.deltaTime; mark.SetPulse(t / 0.65f); yield return null; }

        transform.SetPositionAndRotation(FloorPoint(start), Quaternion.LookRotation(dir, Vector3.up));
        SetTangible(true);
        SetVisible(true);
        BlinkIn();
        WardenFx.Shards(Chest, 10, 2.5f, WardenFx.Crimson, false, 1f, 0.4f);
        mark.Release(0.3f);
        var id = Play(RushDrawId, 0.05f, 1.35f);
        blade.Glint(1.2f);
        blade.NewSwing(1.2f);
        blade.Swinging = true;
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
            if (!hit && (blade.SweepHits(0.6f) || WardenHazard.SegmentToBody(before + Vector3.up * 1f, transform.position + Vector3.up * 1f) < 1.2f))
            {
                hit = true;
                if (WardenHazard.Damage(28f, transform.position)) Connect(28f);
            }
            if (Random.value < 0.5f) WardenFx.Shards(Chest, 1, 0.5f, WardenFx.Crimson, false, 0.8f, 0.35f);
            ApplyGravity();
            yield return null;
        }
        seqMotion = false;
        blade.Swinging = false;
        yield return Until(id != 0 ? id : RushDrawId, 0.9f, 1.2f);
    }

    /// <summary>The Phase 2 ultimate: his empty hand raised, three metres up, his
    /// sword hanging below him; a halo of every kind of weapon overhead; each strike
    /// forms in the sky, THEN its circle shows, THEN it falls. Small strikes stay
    /// small — only the last giant blade gets the full impact, after the silence.</summary>
    private IEnumerator ThousandBladeJudgment()
    {
        judgmentDone = true;
        var centre = ArenaCenter;
        if (Vector3.ProjectOnPlane(transform.position - centre, Vector3.up).magnitude > 2f)
            yield return BlinkTo(centre, ToPlayerFlat);
        Play(CrownHoldId, 0.3f, 1f);
        pose?.Set(WardenPose.Kind.RaiseHand, 0.4f);
        body.FloatSword(true);
        SyncSword();
        body.Charge(0.5f);
        WardenAudio.Play("hum", Chest, 0.9f, 1f);
        StartCoroutine(MoodTo(1f, 1.2f));
        cam?.Frame(6f, 0.22f, 0.8f, 9.5f, 1.2f, 1.4f);
        Sanctum?.Instability(1f);
        yield return LiftTo(3f, 1.6f);

        // The host: a slow halo of weapons overhead — swords, axes, spears, greatswords.
        var halo = new List<SpectralBlade>();
        for (var i = 0; i < 24; i++)
        {
            var a = i * Mathf.PI * 2f / 24f;
            var r = 3f + (i % 3) * 3.5f;
            var p = centre + new Vector3(Mathf.Cos(a) * r, 11f + (i % 4) * 0.8f, Mathf.Sin(a) * r);
            var kind = WardenArsenal.Mixed(i);
            halo.Add(SpectralBlade.Spawn(kind, -1, WardenArsenal.NaturalLength(kind, 0.75f), p,
                                         Quaternion.FromToRotation(Vector3.up, Vector3.down), transform, 0.25f, i % 4 == 0, 0.7f + (i % 5) * 0.05f));
            if (i % 2 == 0) yield return Wait(0.05f);
        }

        var rain = 0f;
        var next = 0f;
        var n = 0;
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
                    RainStrike.Spawn(floor, 0.75f, 1.25f, 14f, 1f, transform, false, WardenArsenal.Mixed(n++));
            }
            yield return null;
        }

        // The final, giant sword — it gets the big read, the silence, and the big impact.
        var giantAt = WardenHazard.FloorAt(WardenHazard.Feet + Vector3.up * 2f, 6f, out var gf) ? gf : FloorPoint(WardenHazard.Feet);
        WardenAudio.Duck(2.2f, 0.02f);
        WardenAudio.Play("hum", giantAt, 1f, 0.9f);
        WardenAudio.Play("armour", Chest, 0.6f, 0.8f);
        // The last blade is his own, colossal.
        RainStrike.Spawn(giantAt, 1.5f, 4.2f, 42f, 4f, transform, big: true, kind: WardenArsenal.HasPrefab(ArsenalKind.Own) ? ArsenalKind.Own : ArsenalKind.Greatsword);
        var w = 0f;
        while (w < 2.4f) { w += Time.deltaTime; SpinHalo(halo, centre); yield return null; }
        WardenAudio.Unduck();
        foreach (var b in halo) if (b != null) b.Dissolve(0.35f);
        yield return Wait(0.5f);
        StartCoroutine(MoodTo(0f, 1f));
        body.Charge(0f);
        pose?.Clear(0.4f);
        yield return Descend(0.8f);
        body.FloatSword(false);
        SyncSword();
        Play(CrownLandId, 0.2f, 1f);
        Sanctum?.StrikeRadius(FloorPoint(transform.position), 4f, 0.5f);
        // Punish window: he settles from the hover, spent.
        exposedUntil = Time.time + 1.6f;
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
        SyncSword();
        pose?.Kill();
        WardenAudio.Bed(false);                                  // the room goes silent
        WardenFx.Dust(feet, 4, 1.4f, 1.1f);
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
        SyncSword();
        blade.Glint(1.8f);
        // The pull tears the arena apart.
        Sanctum?.Shatter(feet);
        WardenFx.Impact(feet + fwd * 1.2f, 2.6f, 0.7f, 0.1f, 16, 2f, 2f);
        WardenAudio.Play("boom", feet, 1f, 0.7f);
        WardenAudio.Play("sub", feet, 1f, 0.9f);
        WardenAudio.Bed(true, 0.38f);
        PostPulse.AberrationPulse(0.5f, 0.2f);
        StartCoroutine(GrowTo(1.15f, 1.4f));
        phase = 2;
        poise = corePoiseMax;
        yield return Wait(2.2f);

        GameHud.Boss(health, displayName, finaleAt);
        health.Invulnerable = false;
        inTransition = false;
        var now = Time.time;
        twin.nextAllowed = now + 3f;
        flood.nextAllowed = now + 6f;
        splitter.nextAllowed = now + 9f;
        kingsFall.nextAllowed = now + 13f;
        guillotine.nextAllowed = now + 11f;
        ruinous.nextAllowed = now + 16f;
        armory.nextAllowed = now + 18f;
        grave.nextAllowed = now + 22f;
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
        var inner = WardenMark.Circle(c, 2.4f, WardenFx.Crimson, 0.08f, 0.06f, WardenFx.RingSides);
        var outer = WardenMark.Circle(c, 3.6f, WardenFx.Crimson, 0.08f, 0.04f, WardenFx.RingSides);
        var t = 0f;
        while (StateTime(id) < 0.413f && t < 2.2f)
        {
            t += Time.deltaTime;
            if (StateTime(id) < 0.3f) Face(ToPlayerFlat, Time.deltaTime, 140f);
            c = FloorPoint(transform.position);
            // The two rings trade a stepped beat (never a smooth flicker): inner, outer, inner…
            var innerBeat = Mathf.FloorToInt(t * 3.8f) % 2 == 0;
            inner.SetCircle(c, 2.4f); inner.SetPulse(innerBeat ? 1f : 0.3f);
            outer.SetCircle(c, 3.6f); outer.SetPulse(innerBeat ? 0.3f : 1f);
            yield return null;
        }
        var impact = FrontPoint(1.4f);
        // The silhouette holds: blade driven point-down, both hands on the hilt.
        pose?.Plant(impact, 0.08f);
        WardenFx.Impact(impact, 1.6f, 0.3f, 0.06f, 9, 1.2f, 1.2f);
        WardenAudio.Play("slam", impact, 1f, 0.75f);
        WardenAudio.Play("sub", impact, 0.7f, 1.1f);
        MeleeHit(2.6f, 360f, 30f, 2.5f, impact);
        Sanctum?.StrikeRadius(impact, 3f, 0.8f);
        inner.Release(0.1f);
        GroundWave.Spawn(c, transform.forward, 360f, OuterReach, 9f, 0.9f, 22f);
        WardenAudio.Play("pulse", c, 1f, 1f);
        AnimSpeed(0.05f);
        var beat = 0f;
        while (beat < 1.1f) { beat += Time.deltaTime; body.Charge(beat / 1.1f); yield return null; }
        body.Charge(0f);
        body.Beat(1f);
        outer.Release(0.1f);
        GroundWave.Spawn(c, transform.forward, 360f, OuterReach, 9f, 0.9f, 22f);
        WardenAudio.Play("pulse", c, 1f, 0.82f);
        pose?.Clear(0.35f);
        AnimSpeed(0.85f);
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
        // The read before any VFX: sword planted, he crouches over it.
        pose?.Plant(swordAt, 0.1f);
        WardenFx.Impact(swordAt, 1.3f, 0.2f, 0.05f, 8, 1f, 1.4f);
        WardenAudio.Play("slam", swordAt, 1f, 0.7f);
        WardenAudio.Play("sub", swordAt, 0.8f, 1f);
        AnimSpeed(0.02f);
        body.SetCore(WardenBody.CoreMode.Open);
        body.Charge(0.6f);
        var hum = SeqLoop("drone", Chest, 0.4f, 0.9f);
        const float warn = 1.8f, burn = 1.8f;
        // The room holds its breath while the floor turns; the walls get the frame.
        WardenAudio.Duck(warn, 0.04f);
        WardenAudio.Play("armour", Chest, 0.5f, 0.85f);
        cam?.Frame(5f, 0.25f, 0.9f, warn + burn + 0.6f, 0.5f, 0.8f);
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
        // Still prying the blade out of the floor — the long punish window, and the
        // jump off the wall is the way in: every hit now breaks his posture fast. The
        // purple pulse on his chest (PunishRead) says so — no text.
        pose?.Clear(0.6f);
        yield return Stuck(2.2f, swordAt);
        AnimSpeed(0.6f);
        yield return Until(id, 0.95f, 2.2f);
    }

    /// <summary>Greatsword dragged behind him, sparks off the stone; the walls light
    /// purple — then one huge sweep sends a tall wave across the platform. Get on
    /// a wall above it.</summary>
    private IEnumerator RuinousSweep()
    {
        Sanctum?.ChargeWalls(1f);
        var id = Play(RuinousSweepId, 0.25f, 0.3f);
        pose?.Set(WardenPose.Kind.Drag, 0.35f);
        cam?.Frame(4f, 0.2f, 0.7f, 3.6f, 0.5f, 0.8f);
        var scrape = SeqLoop("scrape", transform.position, 0.6f, 0.8f);
        var t = 0f;
        var spark = 0f;
        while (t < 2.4f && StateTime(id) < 0.36f)
        {
            t += Time.deltaTime;
            Face(ToPlayerFlat, Time.deltaTime, 70f);
            spark -= Time.deltaTime;
            blade.Heat = Mathf.Clamp01(t / 2.4f);
            if (spark <= 0f)
            {
                spark = 0.06f;
                // The tip drags on the stone: sparks and a gouge behind him.
                var behind = blade.HasBlade ? FloorPoint(blade.Tip) + Vector3.up * 0.05f
                                            : FloorPoint(transform.position) - FlatDir(transform.forward) * 1.6f + Vector3.up * 0.05f;
                WardenFx.Sparks(behind, Vector3.up, 2, 3f, -FlatDir(transform.forward) * 0.5f);
                WardenFx.Shards(behind, 1, 3f, Random.value < 0.3f ? WardenFx.PaleRed : WardenFx.Crimson, false, 0.7f, 0.3f, Vector3.up * 0.5f);
            }
            if (scrape != null) scrape.transform.position = transform.position;
            yield return null;
        }
        WardenAudio.StopLoop(scrape);
        pose?.Clear(0.08f);
        AnimSpeed(1.15f);
        yield return Until(id, 0.37f, 0.6f);
        var c = FloorPoint(transform.position);
        WardenAudio.Play("slash", c, 1f, 0.6f);
        WardenAudio.Play("boom", c, 0.7f, 1.2f);
        WardenFx.Shake(0.22f);
        MeleeHit(5f, 240f, 34f, 2.4f);
        GroundWave.Spawn(c, transform.forward, 360f, OuterReach, 12f, 2.4f, 30f, true, 1.2f, 0.8f);
        // The radial cut above is the damage; the blade's own pass (its trail, the chips off
        // its tip, the sigil where it bites the stone) carves the room.
        yield return Swing(id, StateTime(id) + 0.01f, 0.52f, 0f, 0.75f, 2.2f, timeout: 1f, edge: true);
        yield return Wait(0.8f);
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
        // Take-off, the player's way: a facet ring under him and a few chips — no puffs.
        WardenFx.Pulse(FloorPoint(transform.position) + Vector3.up * 0.04f, Vector3.up, 0.3f, 1.6f, 0.3f, WardenFx.Crimson, 1.2f);
        WardenFx.Dust(transform.position, 4, 1.6f, 1f);
        if (bossAnimator != null) bossAnimator.transform.localRotation = Quaternion.Euler(24f, 0f, 0f);
        cam?.Frame(5f, 0.18f, 1.2f, 3.6f, 0.3f, 0.9f);
        yield return LiftTo(8f, 0.42f, violent: true);
        if (hangId != 0) AnimSpeed(0.05f);

        const float inner = 3.6f, outerStart = 9.5f, window = 2.2f, lockAt = 1.35f;
        var target = LandingTarget(FloorPoint(WardenHazard.Feet));
        var innerMark = WardenMark.Circle(target, inner, WardenFx.Crimson, 0.1f, 0.12f, WardenFx.RingSides);
        var outerMark = WardenMark.Circle(target, outerStart, WardenFx.Crimson, 0.09f, 0f, WardenFx.RingSides);
        var t = 0f;
        var hushed = false;
        while (t < window)
        {
            t += Time.deltaTime;
            if (!hushed && t >= window - 0.7f) { hushed = true; WardenAudio.Duck(0.8f, 0.03f); WardenAudio.Play("armour", Chest, 0.5f, 0.75f); }
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
        WardenAudio.Unduck();
        WardenFx.Impact(land, 2.4f, 0.5f, 0.09f, 14, 1.8f, 1.6f);
        WardenAudio.Play("boom", land, 1f, 0.8f);
        WardenAudio.Play("metal", land, 0.9f, 0.7f);
        WardenAudio.Play("sub", land, 1f, 0.9f);
        if (blade.HasBlade)
        {
            // Sword-first: the blade buries itself at his feet, gouging the stone.
            var g = new List<Vector3>();
            for (var d = -0.6f; d <= 2.6f; d += 0.4f) g.Add(land + FlatDir(transform.forward) * d + Vector3.up * 0.04f);
            WardenFx.Groove(g, 0.3f, 1.8f);
            WardenFx.Sparks(land + FlatDir(transform.forward) * 1.2f, Vector3.up, 12, 6f);
        }
        Sanctum?.StrikeRadius(land, inner + 1.5f, 1.6f);
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

    /// <summary>The move to fear: the greatsword straight overhead (the Overhead
    /// silhouette) while red light climbs it segment by segment — handle, guard,
    /// lower, middle, tip — and the room goes silent but for the hum and his armour.
    /// Tip lit = swing. The slam is only the first hit: a thin crack shoots across
    /// the arena (splitting any pillar in its path), sits 0.6s, then erupts along its
    /// whole length. Side-step the line. A miss wedges the blade: pull-out punish.</summary>
    private IEnumerator Worldsplitter() => WorldsplitterRoutine(0.32f, true);

    private IEnumerator WorldsplitterRoutine(float segmentTime, bool track)
    {
        var id = Play(WorldsplitterId, 0.2f, 1f);
        yield return Until(id, 0.22f, 1.2f, 160f);
        pose?.Set(WardenPose.Kind.Overhead, 0.3f);
        yield return Until(id, 0.3f, 0.6f, 160f);
        AnimSpeed(0.02f);
        WardenAudio.Duck(segmentTime * 5f + 0.35f, 0.02f);
        WardenAudio.Play("hum", Chest, 0.9f, 1.6f / (segmentTime * 5f));
        WardenAudio.Play("armour", Chest, 0.55f, 0.8f);
        cam?.Frame(4f, 0.15f, 0.9f, segmentTime * 5f + 1.6f, 0.5f, 0.9f);
        for (var s = 1; s <= 5; s++)
        {
            body.Countdown(s);
            body.Charge(s / 5f);
            blade.Heat = s / 5f;
            var t = 0f;
            while (t < segmentTime)
            {
                t += Time.deltaTime;
                if (track && s < 5) Face(ToPlayerFlat, Time.deltaTime, 120f);
                yield return null;
            }
            if (s == 3) WardenAudio.Play("armour", Chest, 0.4f, 0.95f);
        }
        pose?.Clear(0.05f);
        AnimSpeed(1.5f);
        blade.Glint(1.6f);
        blade.NewSwing(3f);
        blade.Swinging = true;
        yield return Until(id, 0.476f, 0.5f);
        WardenAudio.Unduck();
        body.ClearCountdown();
        body.Charge(0f);
        body.Beat(1.5f);
        var impact = blade.FloorContactThisSwing ? FloorPoint(blade.LastFloorContact) : FrontPoint(2.2f);
        WardenFx.Impact(impact, 2.2f, 0.6f, 0.1f, 14, 1.6f, 1.2f);
        WardenAudio.Play("slam", impact, 1f, 0.7f);
        WardenAudio.Play("sub", impact, 1f, 0.85f);
        var hit = MeleeHit(3.6f, 60f, 44f, 3f);
        var dir = FlatDir(transform.forward);
        var length = CrackLength(impact, dir);
        CrackEruption.Spawn(impact, dir, length, 0.6f, 1.1f, 36f);
        Sanctum?.StrikeLine(impact, dir, length, 1.6f, 3f);
        blade.Swinging = false;
        if (!hit)
        {
            // Wedged in the stone: the eruption is the cover, the pull-out is the opening.
            yield return Stuck(1.6f, impact);
        }
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
        SyncSword();
        Play(CrownRaiseId, 0.2f, 1f);
        pose?.Set(WardenPose.Kind.RaiseHand, 0.3f);
        yield return Wait(0.35f);
        Play(CrownHoldId, 0.25f, 1f);
        body.Charge(0.4f);
        cam?.Frame(4f, 0.2f, 0.8f, 6.5f, 0.6f, 1f);
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
            // Colossal copies of his own blade and the old kings' greatswords, alternating.
            swords.Add(GraveBlade.Spawn(p, 4.2f, 3.3f, 26f, transform, i % 2 == 0 && WardenArsenal.HasPrefab(ArsenalKind.Own) ? ArsenalKind.Own : ArsenalKind.Greatsword));
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
        pose?.Clear(0.3f);
        Play(CrownLandId, 0.2f, 1f);
        yield return Wait(0.4f);
        body.TakeGreatsword();
        SyncSword();
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
        if (Vector3.ProjectOnPlane(transform.position - centre, Vector3.up).magnitude > 1.5f)
            yield return BlinkTo(centre, ToPlayerFlat);
        WardenAudio.Bed(false);
        drone = WardenAudio.MusicLoop("drone", centre + Vector3.up * 3f, 0.6f, 1f);
        StartCoroutine(MoodTo(1f, 2f));
        cam?.Frame(6f, 0.3f, 1.2f, 8f, 1.2f, 1.5f);
        pose?.Set(WardenPose.Kind.RaiseHand, 0.6f);
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
                    RainStrike.Spawn(floor, 0.8f, 1.25f, 14f, 1f, transform, false, WardenArsenal.Mixed(Random.Range(0, 5)));
            }
            yield return null;
        }
        yield return Wait(0.8f);

        // Part 2 — ground rings (jump → delayed double jump).
        var raise = Play(WorldsplitterId, 0.25f, 1f);
        pose?.Set(WardenPose.Kind.Overhead, 0.3f);
        yield return Until(raise, 0.3f, 1f);
        AnimSpeed(0.02f);
        var r1 = WardenMark.Circle(floorCentre, 2.6f, WardenFx.Crimson, 0.1f, 0.06f, WardenFx.RingSides);
        var r2 = WardenMark.Circle(floorCentre, 4f, WardenFx.Crimson, 0.1f, 0.04f, WardenFx.RingSides);
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

            // Final approach: the chest opens, the last Worldsplitter charges — blade
            // straight overhead, the room silent but for his heart.
            pose?.Set(WardenPose.Kind.Overhead, 0.4f);
            WardenAudio.Duck(7f, 0.15f);
            cam?.Frame(7f, 0.3f, 1.4f, 7.5f, 0.8f, 1f);
            body.SetCore(WardenBody.CoreMode.Open);
            Sanctum?.ChargeWalls(1f);
            var countdown = 6.5f;
            t = 0f;
            tetherT = 0f;
            var lit = 0;
            var struck = false;
            while (t < countdown)
            {
                t += Time.deltaTime;
                Heartbeat(ref beat, Mathf.Lerp(0.8f, 0.35f, t / countdown));
                var want = Mathf.Min(5, Mathf.FloorToInt(t / countdown * 5f) + 1);
                if (want != lit) { lit = want; body.Countdown(lit); body.Charge(lit / 5f); }
                // The suit's purple tether to the Core; the prompt only while a jump press WOULD leap.
                CoreTether(Time.deltaTime);
                CorePrompt(CoreLeapArmed());
                if (CoreLeapRequested())
                {
                    CorePrompt(false);
                    var leap = CoreLeap();
                    while (leap.MoveNext()) yield return leap.Current;
                    if (coreStruck) { struck = true; break; }
                }
                if (CoreStrikeInReach()) { struck = true; break; }
                yield return null;
            }
            CorePrompt(false);
            WardenAudio.Unduck();
            if (struck || coreStruck)
            {
                pose?.Kill();
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
            WardenAudio.Play("sub", floorCentre, 1f, 0.8f);
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
    private bool CoreLeapRequested() => CoreLeapArmed() && playerLoco.JumpAction.WasPressedThisFrame();

    /// <summary>Would a jump press leap right now? Airborne or on a wall, within reach of the Core.</summary>
    private bool CoreLeapArmed()
    {
        if (playerLoco == null || playerLoco.JumpAction == null || !WardenHazard.Alive) return false;
        if (WardenHazard.Grounded && !WardenHazard.WallRunning) return false;
        return CoreInRange();
    }

    private bool CoreInRange() => Vector3.ProjectOnPlane(body.CorePosition - WardenHazard.Feet, Vector3.up).magnitude <= 17f;

    /// <summary>The leap prompt — shown only while the leap is armed, hidden otherwise.</summary>
    private void CorePrompt(bool on)
    {
        if (on == corePrompt) return;
        corePrompt = on;
        if (on) GameHud.ShowPrompt("SPC", "LEAP TO THE CORE");
        else GameHud.HidePrompt();
    }

    /// <summary>The diegetic read of the leap: a purple tether drawn from your chest to the
    /// Core while you're within reach — brighter, with purple chips at your boots, while a
    /// jump press would carry you there (purple = the suit can use this).</summary>
    private void CoreTether(float dt)
    {
        tetherT -= dt;
        if (tetherT > 0f || !WardenHazard.Alive || !CoreInRange()) return;
        tetherT = 0.15f;
        var armed = CoreLeapArmed();
        var col = WardenFx.PurpleBright;
        col.a = armed ? 1f : 0.45f;
        WardenFx.Stroke(WardenHazard.Chest, body.CorePosition, col, armed ? 0.07f : 0.035f, 0.22f, 0.4f);
        if (armed) WardenFx.Chips(WardenHazard.Feet + Vector3.up * 0.1f, 3, 1.2f, Vector3.up * 0.4f, 0.35f, WardenFx.Purple, 1f);
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
    /// you hang there a breath — an attack or jump press strikes at once, and the end
    /// of the hang strikes anyway: the leap is the committed choice.</summary>
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
            // Attack presses buffer through the flight (the jump that launched it doesn't count).
            pressed |= playerAttack != null && playerAttack.AttackAction != null && playerAttack.AttackAction.WasPressedThisFrame();
            yield return null;
        }
        // The hang: the Core flaring is the read — no prompt. Any press strikes now.
        var hang = 0f;
        while (hang < 0.75f && !pressed && WardenHazard.Alive)
        {
            hang += Time.deltaTime;
            pressed = StrikePressed();
            WardenFx.Shards(pt.position, 1, 0.4f, WardenFx.Purple, true, 0.9f, 0.4f);
            yield return null;
        }
        if (WardenHazard.Alive)
        {
            coreStruck = true;
            yield break;
        }
        st.IsRooted = false;
        playerLoco?.ResumeVerticalMotion(0f);
    }

    private bool StrikePressed()
        => (playerAttack != null && playerAttack.AttackAction != null && playerAttack.AttackAction.WasPressedThisFrame())
           || (playerLoco != null && playerLoco.JumpAction != null && playerLoco.JumpAction.WasPressedThisFrame());

    /// <summary>The final strike: everything stops for a breath; impact frames; the
    /// Core bursts; he drops; the floating arena holds… then gravity returns.</summary>
    private IEnumerator CoreStrike()
    {
        WardenHazard.ClearAll();
        CorePrompt(false);
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
                    SyncSword();
                    SetBodySize(1.15f);
                    Sanctum?.Shatter(FloorPoint(transform.position));
                    GameHud.Boss(health, displayName, finaleAt);
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
        if (bossAnimator != null && hasSpeedParam) bossAnimator.SetFloat(SpeedId, v);
    }
}
