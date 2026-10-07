using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Warden's swordsmanship — the backbone of every phase (melee rework):
///   • Strings, not single swings: the Big Sword pack's 7-cut, two 4-cut and the
///     3-cut chains, the wolf chain (G1 → G2 → Grave Rend) and a dozen skills,
///     each cut rolling its own follow-up (keep pressing / heavy finisher /
///     delayed overhead / stop) — so no two exchanges play the same.
///   • He comes to you: runs when you're far, strafes while he waits for an
///     opening, and every cut tracks and closes the distance (motion warping on
///     top of the authored travel) so sprinting in circles is not a defence.
///     Keep away too long and he answers with a gap-closer: the dash draw, the
///     leaping Mooncleaver, Skyfall, a code-driven leap slam, or (Phase 2+) a
///     volley of thrown weapons led to where you're running.
///   • Reads you: you behind him → turning cuts; you airborne → the upper cut;
///     you drinking → a punish; you pressing him → the guard counter (Revenge
///     Guard) or a backstep into the dash.
///   • The signature reads (Crown, Worldsplitter, …) share a "special" budget so
///     they punctuate the swordwork instead of replacing it.
/// Phase tempo: P1 a precise swordsman, P2 faster and longer strings plus the
/// magic, P3 slower, enormous, every finisher shaking the floor.
/// </summary>
public sealed partial class BossLord
{
    [Header("Pursuit & pressure (melee rework)")]
    [Tooltip("Run speed when you're out of reach (Phase 2 ×1.1, Phase 3 heavy ×0.85).")]
    [SerializeField, Min(1f)] private float runSpeed = 5.4f;
    [Tooltip("Beyond this distance he runs at you instead of walking.")]
    [SerializeField, Min(2f)] private float runFrom = 6.5f;
    [Tooltip("Seconds you can stay out of his reach before he forces a gap-closer.")]
    [SerializeField, Min(0.3f)] private float kiteTimeout = 1.4f;
    [Tooltip("Chance he circles you (strafe) between strings instead of walking straight in.")]
    [SerializeField, Range(0f, 1f)] private float strafeChance = 0.6f;
    [Tooltip("Top speed of the extra closing travel added to a cut (m/s).")]
    [SerializeField, Min(1f)] private float maxWarpSpeed = 11f;

    private static readonly int MoveXId = Animator.StringToHash("MoveX");
    private static readonly int MoveYId = Animator.StringToHash("MoveY");

    private float nextAttackAt, nextSpecialAt, outOfReach, strafeUntil, warpLeft, pressure, moveX, moveY;
    private int strafeSign = 1, chainDepth;
    private bool hasMoveXY, hasSpeedParam, meleeParamsRead;
    private Move queued, dashCut;
    private readonly List<string> recent = new List<string>();
    private Move guardMove, rushP1, rushP2, rushP3;
    private Coroutine ghostChain;
    private Transform ghostSword;
    private MeshFilter ghostSwordMesh;

    /// <summary>The player's flat dust chip (.4,.36,.34 at a .55) — pre-divided by
    /// WardenFx.Chips' ×1.28 glow so the chips land on the player's colour.</summary>
    private static readonly Color DustChip = new Color(0.31f, 0.28f, 0.27f, 0.55f);

    // ================================================================== tempo per phase

    private float Aggression => phase == 0 ? 0.8f : phase == 1 ? 1.05f : 1f;
    private int MaxChain => phase == 0 ? 3 : phase == 1 ? 5 : 4;
    private float EarlyTurn => phase == 0 ? 420f : phase == 1 ? 540f : 280f;
    private float LateTurn => phase == 0 ? 75f : phase == 1 ? 110f : 50f;

    private float RhythmGap()
    {
        var gap = phase == 0 ? Random.Range(0.45f, 1.1f) : phase == 1 ? Random.Range(0.2f, 0.75f) : Random.Range(0.45f, 1.05f);
        // Low on health he stops giving you room.
        var hpK = health != null ? health.Current / Mathf.Max(1f, health.Max) : 1f;
        return gap * Mathf.Lerp(0.6f, 1f, hpK);
    }

    private float SpecialGap() => phase == 1 ? Random.Range(7f, 11f) : Random.Range(6f, 9.5f);

    /// <summary>Which locomotion parameters the controller has. Idempotent — Start and
    /// Engage both call it: the Animator sits on a child, so during the parent's Awake
    /// it may not be initialized yet, and an empty read would leave him sliding in the
    /// idle pose with no strafe or run. Reads once from an initialized Animator.</summary>
    private void InitMelee()
    {
        if (meleeParamsRead || bossAnimator == null || bossAnimator.runtimeAnimatorController == null
            || !bossAnimator.isInitialized) return;
        hasMoveXY = hasSpeedParam = false;
        var ps = bossAnimator.parameters;
        foreach (var p in ps)
        {
            if (p.nameHash == MoveXId) hasMoveXY = true;
            if (p.nameHash == SpeedId) hasSpeedParam = true;
        }
        meleeParamsRead = ps.Length > 0;
    }

    private void ResetMelee()
    {
        nextAttackAt = nextSpecialAt = 0f;
        outOfReach = strafeUntil = warpLeft = pressure = 0f;
        chainDepth = 0;
        queued = dashCut = null;
        recent.Clear();
        ZeroMove();
    }

    // ================================================================== the move tables

    private Move Cut(int id, string name, string tag, float damage, float speed, params Vector2[] windows)
        => new Move { id = id, name = name, tag = tag, damage = damage, speed = speed, windows = windows, range = 4.2f, arc = 150f };

    /// <summary>All three phases' swordwork. Strings are linked cuts; singles are
    /// skills; utilities are scripted reads (guard, backstep, leap, volley).</summary>
    private void BuildMelee(out List<Move> p1, out List<Move> p2, out List<Move> p3)
    {
        p1 = BuildPhase(0);
        p2 = BuildPhase(1);
        p3 = BuildPhase(2);
    }

    private List<Move> BuildPhase(int ph)
    {
        // Tempo / weight per phase: P2 quicker and longer, P3 slow and enormous.
        var tempo = ph == 0 ? 1f : ph == 1 ? 1.1f : 0.82f;
        var dmg = ph == 0 ? 1f : ph == 1 ? 1.1f : 1.3f;
        var warpK = ph == 2 ? 1.2f : 1f;
        var heavy = ph == 2;
        var list = new List<Move>();

        Move C(int id, string name, string tag, float d, float sp, params Vector2[] w)
        {
            var m = Cut(id, name, tag, d * dmg, sp * tempo, w);
            if (heavy) { m.reach = 0.7f; m.force = 1.5f; m.shake = 0.1f; }
            return m;
        }
        void Heavy(Move m, float impact, float shake, float hitstop, float ring = 0f, float ringDamage = 0f)
        {
            m.impact = impact; m.shake = shake; m.hitstop = hitstop; m.force = Mathf.Max(m.force, 1.4f + impact * 0.4f);
            if (heavy && ring > 0f) { m.ringRadius = ring; m.ringDamage = ringDamage; }
        }

        // --- The seven cuts (Attack_7Combo 1-2-3-5-6-7) ---------------------------
        var s1 = C(DrawSlashId, "Seven I", "seven", 22f, 1f, new Vector2(0.28f, 0.62f));
        var s2 = C(TwinCutId, "Seven II", "seven", 22f, 1f, new Vector2(0.24f, 0.6f));
        var s3 = C(TwinCut2Id, "Seven III", "seven", 24f, 1f, new Vector2(0.24f, 0.62f));
        var s5 = C(SevenCut5Id, "Seven V", "seven", 24f, 1f, new Vector2(0.1f, 0.45f), new Vector2(0.55f, 0.8f));
        var s6 = C(SevenCut6Id, "Seven VI", "seven", 26f, 1f, new Vector2(0.4f, 0.62f), new Vector2(0.72f, 0.88f));
        var s7 = C(SevenCut7Id, "Seven VII", "seven", 36f, 0.95f, new Vector2(0.18f, 0.4f));
        Heavy(s7, 1.4f, 0.24f, 0.06f, 9f, 14f);
        s7.delayChance = 0.45f;
        s1.pickMax = 5.6f; s1.cooldown = 2f; s1.weight = 1.3f; s1.warp = 2.8f * warpK;
        s2.warp = s3.warp = 1.8f * warpK; s5.warp = s6.warp = 1.6f * warpK; s7.warp = 2.4f * warpK;
        s1.linkAt = 0.66f; s2.linkAt = 0.62f; s3.linkAt = 0.66f; s5.linkAt = 0.84f; s6.linkAt = 0.84f;

        // --- Four cuts A / B (Attack_4Combo 1A|1B-2-3-4) ---------------------------
        var f1a = C(FourCut1AId, "Four IA", "fourA", 20f, 1f, new Vector2(0.18f, 0.5f));
        var f1b = C(FourCut1BId, "Four IB", "fourB", 24f, 1f, new Vector2(0.34f, 0.58f));
        var f2 = C(FourCut2Id, "Four II", "four", 24f, heavy ? 0.98f : 1f, new Vector2(0.3f, 0.66f));
        var f3 = C(FourCut3Id, "Four III", "four", 24f, 1f, new Vector2(0.4f, 0.68f));
        var f4 = C(FourCut4Id, "Four IV", "four", 34f, heavy ? 0.92f : 1f, new Vector2(0.5f, 0.72f));
        Heavy(f4, 1.4f, 0.24f, 0.06f, 9.5f, 16f);
        f4.delayChance = 0.4f;
        f1a.pickMax = 5.4f; f1a.cooldown = 2.6f; f1a.warp = 2.6f * warpK;
        f1b.pickMin = 2.5f; f1b.pickMax = 7.5f; f1b.cooldown = 4.5f; f1b.warp = 3.8f * warpK; f1b.gapCloser = true; f1b.warpStop = 1.3f;
        f2.turner = true; f2.arc = 200f; f2.warp = 1.6f * warpK; f3.warp = 1.6f * warpK; f4.warp = 2.2f * warpK;
        f1a.linkAt = 0.7f; f1b.linkAt = 0.72f; f2.linkAt = 0.74f; f3.linkAt = 0.76f;

        // --- Three cuts (Attack_3Combo 1-2-3) --------------------------------------
        var t1 = C(ThreeCut1Id, "Three I", "three", 20f, 1.05f, new Vector2(0.2f, 0.46f));
        var t2 = C(ThreeCut2Id, "Three II", "three", 20f, 1.05f, new Vector2(0.14f, 0.42f));
        var t3 = C(ThreeCut3Id, "Three III", "three", 28f, 1f, new Vector2(0.55f, 0.82f));
        Heavy(t3, 1.1f, 0.16f, 0.04f, 8f, 12f);
        t1.pickMax = 5.2f; t1.cooldown = 2.4f; t1.warp = 2.4f * warpK; t2.warp = 1.6f * warpK; t3.warp = 2f * warpK;
        t3.delayChance = 0.3f;
        t1.linkAt = 0.62f; t2.linkAt = 0.66f;

        // --- The wolf chain (Skill_G_1 → G_2 → Grave Rend) ---------------------------
        var w1 = C(WolfFang1Id, "Wolf I", "wolf", 22f, 1.05f, new Vector2(0.22f, 0.38f));
        var w2 = C(WolfFang2Id, "Wolf II", "wolf", 24f, 1.05f, new Vector2(0.27f, 0.41f));
        var rend = C(GraveRendId, "Grave Rend", "wolf", 38f, 0.95f, new Vector2(0.35f, 0.48f));
        Heavy(rend, 1.5f, 0.26f, 0.07f, 10f, 16f);
        rend.delayChance = 0.35f;
        w1.pickMax = 5.2f; w1.cooldown = 4f; w1.warp = 2.4f * warpK; w2.warp = 1.8f * warpK; rend.warp = 2.2f * warpK;
        w1.linkAt = 0.5f; w2.linkAt = 0.5f;

        // --- Singles -------------------------------------------------------------------
        var heaven = C(HeavenCutId, "Heaven Cut", "heaven", 36f, 0.95f, new Vector2(0.4f, 0.52f));
        Heavy(heaven, 1.3f, 0.22f, 0.05f, 9f, 14f);
        heaven.holdAtBase = 0.36f; heaven.hold = new Vector2(0.3f, 0.9f); heaven.release = 1.25f; heaven.trackAt = 0.34f;
        heaven.pickMin = 1.2f; heaven.pickMax = 5.4f; heaven.cooldown = 6f; heaven.warp = 2f * warpK;

        var bone = C(BonesunderId, "Bonesunder", "bone", 26f, 1.05f, new Vector2(0.16f, 0.28f));
        bone.pickMax = 5.2f; bone.cooldown = 4.5f; bone.warp = 2.6f * warpK; bone.weight = 0.9f;

        var gale = C(CounterCleaveId, "Iron Gale", "gale", 26f, 1.05f, new Vector2(0.26f, 0.4f));
        gale.arc = 210f; gale.turner = true; gale.pickMax = 4.6f; gale.cooldown = 5f; gale.warp = 1.6f * warpK;

        var ashen = C(AshenCleaveId, "Ashen Cleave", "ashen", 26f, 1.05f, new Vector2(0.18f, 0.36f));
        ashen.arc = 210f; ashen.turner = true; ashen.pickMax = 4.8f; ashen.cooldown = 4.5f; ashen.warp = 1.8f * warpK;

        var wolfAll = C(WolfFangId, "Grave Wolf", "wolfAll", 24f, 0.98f, new Vector2(0.14f, 0.27f), new Vector2(0.47f, 0.58f));
        Heavy(wolfAll, 1.1f, 0.16f, 0.04f, 8f, 12f);
        wolfAll.turner = true; wolfAll.arc = 200f; wolfAll.pickMax = 4.8f; wolfAll.cooldown = 7f; wolfAll.warp = 2f * warpK;

        var upper = C(UpperCutId, "Upper Cut", "upper", 26f, 1.05f, new Vector2(0.3f, 0.5f));
        upper.antiAir = true; upper.maxHeight = 5f; upper.pickMax = 5f; upper.cooldown = 5f; upper.warp = 2.2f * warpK; upper.weight = 0.5f;

        var rush = C(RushDrawId, "Rush Draw", "rush", 28f, 1.05f, new Vector2(0.3f, 0.62f));
        rush.gapCloser = true; rush.pickMin = 4.2f; rush.pickMax = 13f; rush.cooldown = 3.8f; rush.warp = 7.5f * warpK; rush.warpStop = 1.3f;

        // Skill_J / Skill_I are authored leaps: rootY keeps their arc (the rest stay grounded).
        var moon = C(MoonRushId, "Mooncleaver", "moon", 34f, 1f, new Vector2(0.38f, 0.49f));
        Heavy(moon, 1.2f, 0.2f, 0.05f, 9f, 14f);
        moon.gapCloser = true; moon.pickMin = 4.5f; moon.pickMax = 13f; moon.cooldown = 6.5f; moon.warp = 6.5f * warpK; moon.warpStop = 1.4f;
        moon.rootY = true;

        var sky = C(SkyfallId, "Skyfall", "skyfall", 32f, 1f, new Vector2(0.42f, 0.57f));
        Heavy(sky, 1.3f, 0.22f, 0.05f, 9f, 14f);
        sky.gapCloser = true; sky.pickMin = 3.5f; sky.pickMax = 10f; sky.cooldown = 6.5f; sky.warp = 5f * warpK; sky.warpStop = 1.3f;
        sky.rootY = true;

        var counterCut = C(GuardAttackId, "Revenge", "counter", 30f, 1.1f, new Vector2(0.1f, 0.56f));
        counterCut.arc = 200f; counterCut.warp = 3f; counterCut.warpStop = 1.3f;

        // --- Strings ---------------------------------------------------------------------
        s1.links = ph == 0
            ? new[] { new Link(s2, 1f, 5.5f), new Link(bone, 0.35f, 5.2f), new Link(rush, 0.5f, 14f, 5f) }
            : new[] { new Link(s2, 1f, 5.5f), new Link(bone, 0.35f, 5.2f), new Link(ashen, 0.3f, 4.8f), new Link(rush, 0.6f, 14f, 5f) };
        s2.links = new[] { new Link(s3, 1f, 5.5f), new Link(heaven, ph == 0 ? 0.4f : 0.55f, 5.4f), new Link(moon, 0.5f, 14f, 5.5f) };
        s3.links = new[] { new Link(s5, 1f, 5.5f), new Link(wolfAll, 0.35f, 4.8f), new Link(rush, 0.5f, 14f, 5f) };
        s5.links = new[] { new Link(s6, 1f, 5.5f), new Link(gale, 0.4f, 4.6f) };
        s6.links = new[] { new Link(s7, 1f, 6f), new Link(sky, 0.4f, 11f, 4f) };
        f1a.links = new[] { new Link(f2, 1f, 5.5f), new Link(bone, 0.3f, 5.2f) };
        f1b.links = new[] { new Link(f2, 1f, 5.5f), new Link(f3, 0.4f, 5.5f) };
        f2.links = new[] { new Link(f3, 1f, 5.5f), new Link(rend, 0.3f, 5.2f), new Link(rush, 0.4f, 14f, 5f) };
        f3.links = new[] { new Link(f4, 1f, 6f), new Link(heaven, 0.3f, 5.4f) };
        t1.links = new[] { new Link(t2, 1f, 5.2f), new Link(f2, 0.3f, 5.2f) };
        t2.links = new[] { new Link(t3, 1f, 5.5f), new Link(sky, 0.35f, 11f, 4f) };
        w1.links = new[] { new Link(w2, 1f, 5.2f) };
        w2.links = new[] { new Link(rend, 1f, 5.6f), new Link(moon, 0.5f, 14f, 5.5f) };
        bone.links = ph == 0 ? System.Array.Empty<Link>() : new[] { new Link(s3, 0.6f, 5.2f) };
        gale.links = new[] { new Link(t2, 0.6f, 5f) };
        ashen.links = new[] { new Link(f3, 0.5f, 5.2f) };
        rush.links = new[] { new Link(t2, 0.7f, 5f), new Link(s2, 0.5f, 5f) };
        rush.linkAt = 0.7f;
        moon.links = new[] { new Link(f2, 0.5f, 5f) };
        moon.linkAt = 0.72f;
        foreach (var m in new[] { rush, moon }) m.linkChance = 0.55f;

        // --- Utilities (scripted reads) ----------------------------------------------
        // Only with its own takes (Setup Warden Fight): a guard without the pose would read as standing idle.
        var guard = new Move { name = "Revenge Guard", tag = "guard", seq = GuardCounter, utility = true, cooldown = 9f, pickMax = 4.6f, weight = 0.6f, requiresState = GuardLoopId };
        var backstep = new Move { name = "Backstep", tag = "step", seq = Backstep, utility = true, cooldown = 6f, pickMax = 2.6f, weight = 0.5f, requiresState = BackStepId };
        var leap = new Move { name = "Leap Slam", tag = "leap", seq = LeapSlam, utility = true, gapCloser = true, cooldown = ph == 2 ? 8f : 10f, pickMin = 6f, pickMax = 18f, weight = 0.9f };
        var volley = new Move { name = "Blade Volley", tag = "volley", seq = BladeVolley, utility = true, gapCloser = true, cooldown = 9f, pickMin = 7f, pickMax = 26f, weight = 0.9f };

        if (ph == 0)
        {
            counter = counterCut;
            guardMove = guard;
            rushP1 = rush;
            list.AddRange(new[] { s1, f1a, f1b, t1, w1, heaven, bone, gale, wolfAll, upper, rush, moon, guard, backstep });
        }
        else if (ph == 1)
        {
            rushP2 = rush;
            // The late seven cuts also open a string on their own, so the finisher shows up.
            s6.pickMax = 5.4f; s6.cooldown = 6f; s6.weight = 0.6f;
            list.AddRange(new[] { s1, s6, f1a, f1b, t1, w1, heaven, bone, gale, ashen, wolfAll, upper, rush, moon, sky, guard, backstep, leap, volley });
        }
        else
        {
            // The Core-driven body: the old heavy table becomes a string of its own.
            var heavySweep = C(HeavySweepId, "Heavy Sweep", "heavy", 30f, 0.98f, new Vector2(0.4f, 0.64f));
            heavySweep.arc = 200f; heavySweep.turner = true; heavySweep.pickMax = 5.6f; heavySweep.cooldown = 3f; heavySweep.warp = 3f;
            heavySweep.force = 1.6f; heavySweep.shake = 0.12f;
            var heavyCombo = C(HeavyComboId, "Heavy Combo", "heavy", 26f, 0.98f, new Vector2(0.28f, 0.45f), new Vector2(0.58f, 0.78f));
            heavyCombo.warp = 2.4f;
            var heavySmash = C(HeavySmashId, "Heavy Smash", "heavy", 44f, 0.92f, new Vector2(0.5f, 0.72f));
            Heavy(heavySmash, 1.6f, 0.32f, 0.07f, 10f, 18f);
            heavySmash.holdAtBase = 0.44f; heavySmash.hold = new Vector2(0.2f, 0.6f); heavySmash.release = 1.1f; heavySmash.trackAt = 0.42f;
            heavySmash.pickMin = 1.5f; heavySmash.pickMax = 6f; heavySmash.cooldown = 5f; heavySmash.warp = 3f;
            heavySweep.links = new[] { new Link(heavyCombo, 1f, 6f), new Link(heavySmash, 0.6f, 6.5f) };
            heavyCombo.links = new[] { new Link(heavySmash, 1f, 6.5f), new Link(rend, 0.4f, 6f) };
            heavySweep.linkAt = 0.72f; heavyCombo.linkAt = 0.82f;
            rushP3 = rush;
            s6.pickMax = 5.6f; s6.cooldown = 6f; s6.weight = 0.7f;
            list.AddRange(new[] { heavySweep, heavySmash, s1, s6, f1a, f1b, w1, heaven, bone, wolfAll, upper, rush, moon, sky, leap, volley });
            leap.weight = 1.2f;
            volley.weight = 0.7f;
        }
        return list;
    }

    // ================================================================== picking

    private void NoteMove(Move m)
    {
        var tag = string.IsNullOrEmpty(m.tag) ? m.name : m.tag;
        recent.Add(tag);
        if (recent.Count > 5) recent.RemoveAt(0);
    }

    /// <summary>Penalty for repeating a family: the last pick is nearly off the table,
    /// older ones fade back in.</summary>
    private float Recency(Move m)
    {
        var tag = string.IsNullOrEmpty(m.tag) ? m.name : m.tag;
        var age = 0;
        for (var i = recent.Count - 1; i >= 0; i--, age++)
            if (recent[i] == tag) return age switch { 0 => 0.08f, 1 => 0.3f, 2 => 0.55f, 3 => 0.8f, _ => 0.9f };
        return 1f;
    }

    private bool PlayerAirborne => player != null && !WardenHazard.Grounded && !WardenHazard.WallRunning && player.position.y - floorY > 0.9f;
    private bool PlayerHealing => playerState != null && playerState.IsDrinking;
    private bool PlayerPressing => playerAttack != null && playerAttack.IsAttacking && PlayerDistance < 4.5f;

    private Move PickMove(float dist)
    {
        var moves = PhaseMoves();
        var to = player.position - transform.position;
        to.y = 0f;
        var behind = Vector3.Angle(transform.forward, to) > 110f && dist < 5.5f;
        var air = PlayerAirborne && dist < 6f;
        var kiting = outOfReach >= kiteTimeout;
        var healing = PlayerHealing && dist < 16f;
        var specialReady = phase > 0 && Time.time >= nextSpecialAt;
        var hpK = health.Current / Mathf.Max(1f, health.Max);

        var total = 0f;
        var weights = new float[moves.Length];
        for (var i = 0; i < moves.Length; i++)
        {
            var m = moves[i];
            if (!ReadyInBand(m, dist)) continue;
            if (m.special && !specialReady) continue;
            var w = m.weight * Recency(m);
            if (m.special) w *= 1.4f;
            if (behind) w *= m.turner ? 3.5f : 0.3f;
            if (air) w *= m.antiAir ? 6f : 0.5f;
            if (kiting || healing) w *= m.gapCloser ? 4f : 0.35f;
            else if (m.gapCloser && dist < 6f) w *= 0.4f;
            if (m.tag == "guard") w *= PlayerPressing || pressure > 1.2f ? 3f : 0.15f;
            if (m.tag == "step") w *= dist < 2f || pressure > 1.5f ? 2.2f : 0.2f;
            if (m.tag == "upper" && !air) w *= 0.25f;
            // Low on health: the heavy finishers and the specials come out more.
            if (m.impact > 0f || m.special) w *= Mathf.Lerp(1.4f, 1f, hpK);
            weights[i] = w;
            total += w;
        }
        if (total <= 0f) return null;
        var roll = Random.value * total;
        for (var i = 0; i < moves.Length; i++)
        {
            if (weights[i] <= 0f) continue;
            roll -= weights[i];
            if (roll <= 0f) return moves[i];
        }
        return null;
    }

    /// <summary>At the cut's link point: keep the string going? Which branch?</summary>
    private Move RollLink(Move m)
    {
        if (m.linkRolled) return null;
        m.linkRolled = true;
        if (m.links.Length == 0 || chainDepth >= MaxChain || player == null || !WardenHazard.Alive) return null;
        var hpK = health.Current / Mathf.Max(1f, health.Max);
        var chance = m.linkChance * Aggression * Mathf.Lerp(1.15f, 1f, hpK);
        if (Random.value > chance) return null;
        var dist = PlayerDistance;
        var total = 0f;
        foreach (var l in m.links)
            if (LinkOpen(m, l, dist)) total += l.weight;
        if (total <= 0f) return null;
        var roll = Random.value * total;
        foreach (var l in m.links)
        {
            if (!LinkOpen(m, l, dist)) continue;
            roll -= l.weight;
            if (roll <= 0f) return l.to;
        }
        return null;
    }

    /// <summary>A branch is open while you're in its distance band — and only when it
    /// plays a DIFFERENT take than the one running: a follow-up whose state (fallbacks
    /// included) resolves to the state already playing would restart that clip, and
    /// TickAttack would read the outgoing instance's time (windows spent, string ended).
    /// Branches with no playable state at all are closed too.</summary>
    private bool LinkOpen(Move m, Link l, float dist)
    {
        if (l.to == null || l.to.special || dist > l.maxDist || dist < l.minDist) return false;
        var state = ResolvedState(l.to.id);
        return state != 0 && state != m.played;
    }

    // ================================================================== moving between strings

    /// <summary>Chase mode: attack when the rhythm allows, otherwise run you down,
    /// walk in, or circle you while he picks his moment.</summary>
    private void TickChase(float dt, Vector3 toPlayer, float dist)
    {
        pressure = Mathf.MoveTowards(pressure, 0f, dt * 0.5f);
        outOfReach = dist > 5.4f ? outOfReach + dt : Mathf.Max(0f, outOfReach - dt * 2f);

        if (queued != null)
        {
            var q = queued;
            queued = null;
            EnterMove(q);
            return;
        }
        var punish = PlayerHealing || outOfReach >= kiteTimeout;
        if (Time.time >= nextAttackAt || (punish && Time.time >= nextAttackAt - 0.6f))
        {
            var move = PickMove(dist);
            if (move != null) { EnterMove(move); return; }
        }

        var heavy = phase == 2;
        var walk = walkSpeed * (phase == 1 ? phase2Haste : heavy ? phase3Haste * heavyWalk : 1f);
        var run = (hasMoveXY ? runSpeed : walkSpeed * 1.6f) * (phase == 1 ? 1.1f : heavy ? 0.85f : 1f);
        var aim = FlatDir(PredictPlayer(0.3f) - transform.position);
        var right = Vector3.Cross(Vector3.up, aim);
        Vector3 vel;
        float animX, animY;
        if (dist > runFrom || (punish && dist > 3.2f))
        {
            vel = aim * run;
            animX = 0f;
            animY = 2f;
            strafeUntil = 0f;
        }
        else if (Time.time < nextAttackAt && hasMoveXY && dist > 2.2f)
        {
            // Waiting for his moment: circle, closing a little, never backing off.
            if (Time.time >= strafeUntil)
            {
                strafeUntil = Time.time + Random.Range(0.7f, 1.5f);
                strafeSign = Random.value < 0.5f ? -1 : 1;
                if (Random.value > strafeChance) strafeSign = 0;
            }
            var close = dist > 4f ? 0.55f : 0.15f;
            vel = (right * strafeSign + aim * close).normalized * walk * (strafeSign == 0 ? 0.7f : 0.85f);
            animX = strafeSign;
            animY = strafeSign == 0 ? 0.7f : close;
        }
        else if (dist > 2.2f)
        {
            vel = aim * walk * 1.15f;
            animX = 0f;
            animY = 1f;
        }
        else
        {
            // Right on top of you and still waiting: a slow sidestep, never a moonwalk.
            vel = hasMoveXY ? right * strafeSign * walk * 0.45f : Vector3.zero;
            animX = hasMoveXY ? strafeSign * 0.5f : 0f;
            animY = 0f;
        }
        Face(aim, dt, heavy ? turnSpeed * 0.6f : turnSpeed * 1.2f);
        SetMove(animX, animY);
        Step(vel * dt);
        if (heavy && vel.sqrMagnitude > 0.5f) HeavyStep(dt);
    }

    /// <summary>Locomotion blend: MoveX/MoveY when the controller has them (strafe,
    /// walk 1, run 2), else the old 1D Speed (walk only).</summary>
    private void SetMove(float x, float y)
    {
        if (bossAnimator == null) return;
        var dt = Time.deltaTime;
        moveX = Mathf.MoveTowards(moveX, x, dt * 5f);
        moveY = Mathf.MoveTowards(moveY, y, dt * 5f);
        if (hasMoveXY)
        {
            bossAnimator.SetFloat(MoveXId, moveX);
            bossAnimator.SetFloat(MoveYId, moveY);
        }
        if (hasSpeedParam) bossAnimator.SetFloat(SpeedId, Mathf.Clamp01(new Vector2(moveX, moveY).magnitude));
    }

    private void ZeroMove()
    {
        moveX = moveY = 0f;
        if (bossAnimator == null) return;
        if (hasMoveXY) { bossAnimator.SetFloat(MoveXId, 0f); bossAnimator.SetFloat(MoveYId, 0f); }
        if (hasSpeedParam) bossAnimator.SetFloat(SpeedId, 0f);
    }

    private Vector3 PredictPlayer(float seconds)
    {
        if (player == null) return transform.position + transform.forward;
        var v = playerLoco != null ? Vector3.ProjectOnPlane(playerLoco.ActualPlanarVelocity, Vector3.up) : Vector3.zero;
        if (v.magnitude > 9f) v = v.normalized * 9f;
        return player.position + v * Mathf.Clamp(seconds, 0f, 0.8f);
    }

    /// <summary>During a cut: full tracking through the windup (toward where you'll
    /// be), a slow drift once it's live — and extra travel toward you on top of
    /// the clip so the blade arrives where you are, not where you were.</summary>
    private void TrackAndWarp(Move m, float nt, float secondsToStrike, bool holding, float dt)
    {
        if (player == null) return;
        var strikeAt = m.windows.Length > 0 ? m.windows[0].x : 0.5f;
        var tracking = holding || nt < (m.trackAt > 0f ? m.trackAt : strikeAt - 0.03f);
        var aim = PredictPlayer(Mathf.Min(secondsToStrike, 0.45f) * 0.7f);
        var dir = FlatDir(aim - transform.position);
        var rate = tracking ? EarlyTurn * (holding ? 0.4f : 1f) : (m.lateTrack >= 0f ? m.lateTrack : LateTurn);
        Face(dir, dt, rate);

        if (warpLeft <= 0f || holding || nt > strikeAt + 0.04f) return;
        var flat = Vector3.ProjectOnPlane(aim - transform.position, Vector3.up).magnitude;
        var body = cc != null ? cc.radius * transform.lossyScale.x : 0.5f;
        var need = flat - (m.warpStop + body);
        if (need <= 0.05f || Vector3.Angle(transform.forward, dir) > 65f) return;
        var v = Mathf.Min(need / Mathf.Max(0.1f, secondsToStrike), maxWarpSpeed);
        var stepLen = Mathf.Min(v * dt, warpLeft, need);
        // The dash read, once per cut and only when the closing travel really is a dash:
        // the player's sprint-start streak off both boots + an afterimage chain (one baked
        // pose every ~1.2 m he covers, at most 4) — never a per-frame puff.
        if (warpLeft >= m.warp - 1e-4f) dashCut = null;   // first closing step of this cut
        if (dashCut != m && v >= 4f && Mathf.Min(need, warpLeft) >= 1.2f)
        {
            dashCut = m;
            KickBoots(-dir, 5, 2.4f, 0.16f, WardenFx.CrimsonDeep, 1f);
            GhostChain(4, 1.2f, Mathf.Clamp(secondsToStrike + 0.15f, 0.3f, 0.8f));
        }
        MoveFlat(dir * stepLen);
        warpLeft -= stepLen;
    }

    // ================================================================== afterimages / boot kicks

    /// <summary>The player's dodge afterimage chain on his body (and the blade in his hand):
    /// one baked pose now, then one every <paramref name="spacing"/> metres he covers (3D,
    /// so a leap's rise counts; slow travel falls back to a 0.1 s cadence), at most
    /// <paramref name="count"/>, for at most <paramref name="maxAge"/> s. A new chain
    /// replaces a running one; StopAllCoroutines (resets, posture breaks) ends it.</summary>
    private void GhostChain(int count, float spacing, float maxAge)
    {
        if (count <= 0 || !isActiveAndEnabled) return;
        if (ghostChain != null) StopCoroutine(ghostChain);
        ghostChain = StartCoroutine(RunGhostChain(count, Mathf.Max(0.2f, spacing), maxAge));
    }

    private IEnumerator RunGhostChain(int count, float spacing, float maxAge)
    {
        DropGhost();
        var left = count - 1;
        var last = transform.position;
        var age = 0f;
        var clock = 0f;
        while (left > 0)
        {
            yield return null;
            age += Time.deltaTime;
            clock += Time.deltaTime;
            if (age > maxAge || health.IsDead) break;
            var d = Vector3.Distance(transform.position, last);
            if (d < spacing && (clock < 0.1f || d < spacing * 0.4f)) continue;
            DropGhost();
            left--;
            last = transform.position;
            clock = 0f;
        }
        ghostChain = null;
    }

    private void DropGhost()
    {
        const float life = 0.42f;
        WardenFx.Ghost(BodySkin, WardenFx.Crimson, life);
        // The blade rides along in the afterimage — only while it is really seated in his hand.
        if (blade == null || !blade.HasBlade) return;
        var s = body != null ? body.HandSword : null;
        if (s != ghostSword)
        {
            ghostSword = s;
            ghostSwordMesh = s != null ? s.GetComponentInChildren<MeshFilter>() : null;
        }
        WardenFx.Ghost(ghostSwordMesh, WardenFx.Crimson, life);
    }

    /// <summary>Chips kicked off both boots (the player's sprint-start streak / takeoff).</summary>
    private void KickBoots(Vector3 bias, int count, float speed, float life, Color col, float size = 1f)
    {
        for (var i = 0; i < 2; i++) WardenFx.Chips(Boot(i), count, speed, bias, life, col, size);
    }

    private Vector3 Boot(int side)
    {
        if (bossAnimator != null && bossAnimator.isHuman)
        {
            var b = bossAnimator.GetBoneTransform(side == 0 ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
            if (b != null) return b.position;
        }
        return FloorPoint(transform.position) + FlatDir(transform.right) * (side == 0 ? -0.25f : 0.25f) + Vector3.up * 0.08f;
    }

    // ================================================================== scripted reads

    /// <summary>The fight's opening beat: he draws the greatsword with a flourish,
    /// the edge heats and glints — "this is the weapon".</summary>
    private IEnumerator DrawIntro()
    {
        var id = Play(DrawIntroId, 0.2f, 1.15f);
        WardenAudio.Play("armour", Chest, 0.5f, 0.9f);
        var t = 0f;
        bool forged = false, glinted = false;
        while (t < 1.25f)
        {
            t += Time.deltaTime;
            Face(ToPlayerFlat, Time.deltaTime, 220f);
            blade.Heat = Mathf.Clamp01(t / 0.9f);
            // The player's weapon forge: Core chips gathering onto the edge as it heats.
            if (!forged && t >= 0.25f && blade.HasBlade)
            {
                forged = true;
                WardenFx.Converge(Vector3.Lerp(blade.Base, blade.Tip, 0.55f), 12, 0.9f, 0.35f, WardenFx.Crimson);
            }
            if (!glinted && t >= 0.75f)
            {
                glinted = true;
                blade.Glint(1.4f);
                KickBoots(Vector3.up * 0.35f, 3, 1.4f, 0.4f, DustChip, 1.5f);
            }
            if (id != 0 && StateTime(id) >= 0.9f) break;
            yield return null;
        }
        blade.Heat = 0f;
    }

    /// <summary>Revenge Guard: he plants and raises the blade. Hits from the front
    /// ring off the steel — and the first swing you throw into it is answered by
    /// the counter cut. Wait it out, or go round him.</summary>
    private IEnumerator GuardCounter()
    {
        var id = Play(GuardStartId, 0.1f, 1.1f);
        WardenAudio.Play("armour", Chest, 0.55f, 1f);
        blade.Glint(0.8f);
        yield return Until(id, 0.9f, 0.45f, 360f);
        Play(GuardLoopId, 0.08f, 1f);
        var hold = Random.Range(1f, 1.8f);
        var t = 0f;
        var parried = false;
        while (t < hold)
        {
            t += Time.deltaTime;
            Face(ToPlayerFlat, Time.deltaTime, 260f);
            var front = PlayerInFront(100f);
            health.Invulnerable = front;
            blade.Heat = 0.35f + 0.15f * WardenFx.Stepped(Mathf.PingPong(t * 2.8f, 1f));   // stepped breath, never a sine flicker
            if (front && playerAttack != null && playerAttack.InHitWindow && PlayerDistance < 4.4f) { parried = true; break; }
            yield return null;
        }
        health.Invulnerable = false;
        if (parried)
        {
            var at = blade.HasBlade ? Vector3.Lerp(blade.Base, blade.Tip, 0.6f) : Chest + transform.forward * 0.6f;
            // Steel on steel: the player's impact star in the pale-red peak + the contact sparks.
            var deflect = FlatDir(transform.forward) + Vector3.up * 0.35f;
            WardenFx.Star(at, WardenFx.PaleRed, deflect, 1.6f);
            HitFx.Spawn(at, deflect, 1.2f, WardenFx.PaleRed);
            WardenAudio.Play("metal", at, 1f, 1.15f);
            WardenAudio.Play("shing", at, 0.7f, 0.9f);
            WardenFx.HitStop(0.07f);
            playerAttack?.Cancel();
            WardenHazard.Shove(transform.position, 1.2f, 0.18f);
            Play(GuardAcceptId, 0.04f, 1.2f);
            yield return Wait(0.14f, 400f);
            counterReady = true;
            yield break;
        }
        Play(GuardEndId, 0.15f, 1.1f);
        blade.Heat = 0f;
        yield return Wait(0.35f, 200f);
    }

    /// <summary>Where to throw from <paramref name="from"/> at <paramref name="speed"/> so it meets
    /// you if you keep running — scaled by <paramref name="bias"/> (1 = exact lead, 0 = where
    /// you are now, &gt;1 = ahead of you) so a volley brackets a straight-line sprint.</summary>
    private Vector3 LeadAim(Vector3 from, float speed, float bias = 1f)
    {
        var chest = WardenHazard.Chest;
        if (playerLoco == null || speed <= 0f) return chest;
        var v = Vector3.ProjectOnPlane(playerLoco.ActualPlanarVelocity, Vector3.up);
        if (v.magnitude > 9f) v = v.normalized * 9f;
        var t = Vector3.Distance(from, chest) / speed;
        // One refinement pass: the lead point moves the travel time.
        t = Vector3.Distance(from, chest + v * t) / speed;
        return chest + v * t * bias;
    }

    /// <summary>His capsule passes through yours while he's airborne (landings resolve by shove).</summary>
    private void IgnorePlayer(bool on)
    {
        var pc = WardenHazard.Capsule;
        if (cc != null && pc != null) Physics.IgnoreCollision(cc, pc, on);
    }

    private bool PlayerInFront(float coneDeg)
    {
        if (player == null) return false;
        var to = Vector3.ProjectOnPlane(player.position - transform.position, Vector3.up);
        return to.sqrMagnitude < 1e-4f || Vector3.Angle(transform.forward, to) <= coneDeg * 0.5f;
    }

    /// <summary>A hop back out of your string — and usually straight back in with the dash.</summary>
    private IEnumerator Backstep()
    {
        Play(BackStepId, 0.08f, 1.2f);
        // The player's dodge push-off: a ring tap + chips thrown against the travel, then the afterimages.
        var forward = FlatDir(transform.forward);
        var p = FloorPoint(transform.position) + Vector3.up * 0.04f;
        WardenFx.Pulse(p, Vector3.up, 0.2f, 0.9f, 0.28f, WardenFx.Crimson, 1f);
        WardenFx.Chips(p, 6, 2.2f, forward * 0.8f + Vector3.up * 0.2f, 0.3f, WardenFx.Crimson, 0.9f);
        WardenFx.Chips(p, 3, 1.5f, forward * 0.6f, 0.3f, WardenFx.CrimsonDeep, 1f);
        GhostChain(3, 1.1f, 0.5f);
        WardenAudio.Play("swish", Chest, 0.5f, 1.2f);
        seqMotion = true;
        var t = 0f;
        const float T = 0.42f, Dist = 3.6f;
        while (t < T)
        {
            t += Time.deltaTime;
            var k = Mathf.Clamp01(t / T);
            MoveFlat(-FlatDir(transform.forward) * Dist * Time.deltaTime / T * (1.6f - 1.2f * k));
            Face(ToPlayerFlat, Time.deltaTime, 360f);
            ApplyGravity();
            yield return null;
        }
        seqMotion = false;
        WardenFx.Pulse(FloorPoint(transform.position) + Vector3.up * 0.04f, Vector3.up, 0.15f, 0.55f, 0.22f, WardenFx.Crimson, 0.8f);   // brake skid
        yield return Wait(0.12f, 300f);
        var rush = phase == 0 ? rushP1 : phase == 1 ? rushP2 : rushP3;
        if (rush != null && Random.value < 0.65f) queued = rush;
        else if (Random.value < 0.5f && guardMove != null && phase < 2 && StateReady(guardMove)) queued = guardMove;
    }

    /// <summary>A queued read skips the picker's band checks — still never without its takes.</summary>
    private bool StateReady(Move m)
        => m.requiresState == 0 || (bossAnimator != null && bossAnimator.HasState(0, m.requiresState));

    /// <summary>The leap slam: a crouch while a crimson circle opens where you'll be,
    /// then a heavy arc through the air — he follows you for the first half — and
    /// a sword-first landing (a shockwave in Phase 3). Dodge the landing.</summary>
    private IEnumerator LeapSlam()
    {
        var target = LandingTarget(FloorPoint(PredictPlayer(0.9f)));
        Play(LeapRiseId, 0.12f, 1.15f);
        body.Beat(0.8f);
        blade.Glint(1.2f);
        WardenAudio.Play("armour", Chest, 0.5f, 0.85f);
        KickBoots(Vector3.up * 0.3f, 3, 1f, 0.3f, DustChip, 1.2f);
        var radius = phase == 2 ? 3.2f : 2.6f;
        var mark = WardenMark.Circle(target, radius, WardenFx.Crimson, 0.09f, 0.1f, WardenFx.RingSides);
        var t = 0f;
        while (t < 0.34f)
        {
            t += Time.deltaTime;
            target = LandingTarget(FloorPoint(PredictPlayer(0.9f)));
            mark.SetCircle(target, radius);
            mark.SetPulse(t / 0.34f * 0.4f);
            Face(FlatDir(target - transform.position), Time.deltaTime, 480f);
            yield return null;
        }
        // Launch.
        var from = transform.position;
        var flat = Vector3.ProjectOnPlane(target - from, Vector3.up);
        if (flat.magnitude > 15f) target = FloorPoint(from + flat.normalized * 15f);
        BeginLevitate();
        IgnorePlayer(true);
        Play(LeapAirId, 0.08f, 1f);
        WardenAudio.Play("whoom", Chest, 0.8f, 1.3f);
        var dist0 = Vector3.ProjectOnPlane(target - from, Vector3.up).magnitude;
        var T = 0.62f + dist0 * 0.022f;
        var apex = 3.2f + dist0 * 0.18f;
        // Takeoff, the player's way: a compressed ring + chips under each boot, then the
        // afterimages trailing the rise.
        for (var i = 0; i < 2; i++)
        {
            var at = FloorPoint(Boot(i)) + Vector3.up * 0.03f;
            WardenFx.Pulse(at, Vector3.up, 0.08f, 0.5f, 0.24f, WardenFx.Crimson, 0.9f);
            WardenFx.Chips(at, 5, 1.8f, Vector3.up * 0.3f, 0.3f, WardenFx.CrimsonDeep, 0.9f);
        }
        GhostChain(3, 1.8f, T * 0.5f);
        var slammed = false;
        t = 0f;
        while (t < T)
        {
            t += Time.deltaTime;
            var k = Mathf.Clamp01(t / T);
            if (k < 0.45f)
            {
                // He follows you for the first half of the flight.
                var want = LandingTarget(FloorPoint(PredictPlayer((1f - k) * T)));
                var shift = Vector3.ProjectOnPlane(want - target, Vector3.up);
                target += Vector3.ClampMagnitude(shift, 6f * Time.deltaTime);
            }
            mark.SetCircle(target, radius);
            mark.SetPulse(0.4f + 0.6f * k);
            var pos = Vector3.Lerp(from, target, k * (2f - k) * 0.5f + k * 0.5f) + Vector3.up * apex * 4f * k * (1f - k);
            pos.y = Mathf.Max(pos.y, floorY);
            var delta = pos - transform.position;
            if (cc != null && cc.enabled) cc.Move(delta); else transform.position = pos;
            Face(FlatDir(target - transform.position), Time.deltaTime, 360f);
            if (!slammed && k >= 0.6f) { slammed = true; Play(LeapSlamId, 0.06f, 1.5f); blade.NewSwing(2f); blade.Swinging = true; }
            yield return null;
        }
        SetY(floorY);
        EndLevitate();
        mark.Release(0.08f);
        blade.Swinging = false;
        var land = FloorPoint(transform.position);
        // Never left standing on your head: whatever the hit did, you end up beside him.
        if (PlayerDistance < (cc != null ? cc.radius * transform.lossyScale.x : 0.5f) + 0.9f) WardenHazard.Shove(land, 1.4f, 0.15f);
        var heavy = phase == 2;
        // The player's hard landing at his scale: pale-red 2-frame peak, a Core sigil stamped
        // under the blade, white + crimson 12-gon rings opening to the danger radius, chips
        // and dust chips, red Core cracks. Shake/hitstop land on contact.
        var s = heavy ? 1.6f : 1.3f;
        var g = land + Vector3.up * 0.04f;
        WardenFx.Peak(land + Vector3.up * 0.6f, 0.5f * s);
        WardenFx.Stamp(g, Vector3.up, 0.6f * s, WardenFx.Crimson, 1.3f);
        WardenFx.Pulse(g, Vector3.up, 0.1f, 1.1f * s, 0.28f, Color.white, 1.1f);
        WardenFx.Pulse(g, Vector3.up, 0.25f, radius, 0.5f, WardenFx.Crimson, 1.5f);
        WardenFx.Pulse(g + Vector3.up * 0.06f, Vector3.up, 0.15f, 1.3f * s, 0.38f, WardenFx.Crimson, 1f, WardenFx.CoreSides);
        WardenFx.Chips(g, Mathf.RoundToInt(18 * s), 4.2f, Vector3.up * 0.25f, 0.35f, WardenFx.Crimson, 1.1f);
        WardenFx.Chips(g, Mathf.RoundToInt(10 * s), 1.4f, Vector3.up * 0.35f, 0.85f, DustChip, 2.6f);
        WardenFx.Cracks(land, heavy ? 6 : 5, 1.6f * s, WardenFx.Crimson, 0.16f, 1.2f);
        WardenFx.Shake(heavy ? 0.4f : 0.26f);
        WardenFx.HitStop(0.06f);
        WardenAudio.Play("slam", land, 1f, 0.8f);
        if (heavy) WardenAudio.Play("sub", land, 0.9f, 1f);
        Sanctum?.StrikeRadius(land, radius + 1f, 1.2f);
        MeleeHit(radius + 0.4f, 360f, heavy ? 42f : 32f, 2.6f, land);
        if (heavy)
        {
            yield return Wait(0.3f);
            GroundWave.Spawn(land, transform.forward, 360f, OuterReach, 10f, 0.9f, 18f);
            WardenAudio.Play("pulse", land, 0.9f, 1f);
        }
        IgnorePlayer(false);
        Play(LeapLandId, 0.12f, 0.9f);
        // The landing is his opening.
        exposedUntil = Time.time + 0.5f;
        yield return Wait(0.6f, 120f);
    }

    /// <summary>Phase 2+ answer to keeping your distance: he flicks his hand and real
    /// weapons form over his shoulder, then fly one after another — each led to
    /// where you're running, its path flashed on the floor first.</summary>
    private IEnumerator BladeVolley()
    {
        Play(CastFlickId, 0.12f, 1.2f);
        pose?.Set(WardenPose.Kind.RaiseHand, 0.2f);
        WardenAudio.Play("armour", Chest, 0.45f, 1.05f);
        var count = phase == 2 ? 5 : 3;
        var fwd = FlatDir(transform.forward);
        var right = Vector3.Cross(Vector3.up, fwd);
        var weapons = new SpectralBlade[count];
        for (var i = 0; i < count; i++)
        {
            var kind = i % 3 == 1 ? ArsenalKind.Axe : i % 3 == 2 ? ArsenalKind.Spear : ArsenalKind.Sword;
            var side = (i - (count - 1) * 0.5f);
            var slot = Chest + Vector3.up * (0.9f + Mathf.Abs(side) * 0.15f) + right * side * 0.85f - fwd * 0.4f;
            weapons[i] = SpectralBlade.Spawn(kind, -1, WardenArsenal.NaturalLength(kind, 0.85f), slot,
                                             Quaternion.FromToRotation(Vector3.up, (Vector3.up + right * side * 0.3f).normalized),
                                             transform, 0.16f, true, 1f + i * 0.08f);
            yield return Wait(0.08f, 240f);
        }
        yield return Wait(0.2f, 200f);
        for (var i = 0; i < count; i++)
        {
            var w = weapons[i];
            if (w == null) continue;
            var tell = 0f;
            while (tell < 0.16f && w != null)
            {
                tell += Time.deltaTime;
                w.TurnToward(WardenHazard.Chest, 1100f);
                w.SetGlow(tell / 0.16f);
                Face(ToPlayerFlat, Time.deltaTime, 200f);
                yield return null;
            }
            if (w == null) continue;
            const float speed = 32f;
            var travel = Vector3.Distance(w.Tip, WardenHazard.Chest) / speed;
            var lead = playerLoco != null ? Vector3.ProjectOnPlane(playerLoco.ActualPlanarVelocity, Vector3.up) * travel * 0.85f : Vector3.zero;
            // Alternate: dead on / a step ahead / a step behind — strafing in one line never works twice.
            var bias = i % 3 == 1 ? 0.6f : i % 3 == 2 ? -0.4f : 0f;
            var aim = WardenHazard.Chest + lead * (1f + bias);
            var dir = (aim - w.Tip).normalized;
            WardenFx.Line(new[] { FloorPoint(w.Tip) + Vector3.up * 0.05f, FloorPoint(aim) + FlatDir(dir) * 3f + Vector3.up * 0.05f },
                          WardenFx.Crimson, 0.05f, 0.3f, 0.04f, 0.5f);
            w.Fire(dir, speed, phase == 2 ? 20f : 16f, 40f, w.Kind == ArsenalKind.Axe ? 900f : 0f);
            yield return Wait(phase == 2 ? 0.14f : 0.2f, 160f);
        }
        pose?.Clear(0.25f);
        yield return Wait(0.35f, 150f);
    }
}
