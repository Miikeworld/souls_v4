using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Run Souls Self-Checks — edit-mode asserts for the
/// souls-loop logic that can be checked without Play: stat point-buy math,
/// the checkpoint travel list, and ability-slot attune bounds. Logs PASS/FAIL
/// per check; creates only temporary objects and destroys them.
/// </summary>
public static class SoulsSelfChecks
{
    private static int fails;

    [MenuItem("Tools/Project Restart/Run Souls Self-Checks")]
    public static void Run()
    {
        fails = 0;
        Stats();
        Travel();
        Attune();
        SprintClock();
        ArtAssets();
        BossSetup();
        LevelSetup();
        Alignment();
        HudArtKit();
        ShopInventory();
        CrowdCombat();
        BladeAndPlunge();
        KillMana();
        UltMeter();
        AerialSetup();
        LevelUp();
        CrimsonCore();
        OverdriveEdgeBand();
        if (fails == 0) Debug.Log("[SelfCheck] ALL PASS");
        else Debug.LogError($"[SelfCheck] {fails} FAILED");
    }

    private static void Check(bool ok, string what)
    {
        if (ok) Debug.Log("[SelfCheck] PASS " + what);
        else { fails++; Debug.LogError("[SelfCheck] FAIL " + what); }
    }

    [MenuItem("Tools/Project Restart/Run Fast Crowd Combat Checks")]
    public static void RunCrowdCombatChecks()
    {
        fails = 0;
        CrowdCombat();
        BladeAndPlunge();
        UltMeter();
        AerialSetup();
        if (fails == 0) Debug.Log("[SelfCheck] Action input/data PASS. Play Mode pose, grip, camera collision and device checks remain required.");
        else Debug.LogError($"[SelfCheck] Crowd combat {fails} FAILED");
    }

    /// <summary>Crimson Instability / Overdrive state machine with default tuning:
    /// gain, decay delay, auto-ignition at full, fast Core Energy regen inside
    /// Overdrive, clean expiry, zero-energy discharge.</summary>
    private static void CrimsonCore()
    {
        var host = new GameObject("Crimson check");
        try
        {
            var mana = host.AddComponent<PlayerMana>(); mana.ConfigureMax(50f);
            var c = host.AddComponent<CrimsonInstability>();
            Check(!c.GaugeVisible, "crimson: hidden without the greatsword");
            c.testEquipped = true;
            Check(!c.TryActivate(), "crimson: Overdrive refused below full");
            c.Add(8f); c.Tick(3.9f);
            Check(Mathf.Approximately(c.Instability, 8f), "crimson: no decay inside the delay");
            c.Tick(1f);
            Check(Mathf.Approximately(c.Instability, 2f), "crimson: decays after the delay");
            c.Add(500f);
            Check(c.Ready && Mathf.Approximately(c.Instability, 100f), "crimson: clamps at max, ready");
            c.Tick(0.1f);
            Check(c.Overdrive && c.DamageScale > 1f && c.PoiseScale > 1f, "crimson: full gauge auto-fires Overdrive, scaling damage/poise");
            mana.Drain(30f); c.Tick(1f);
            Check(Mathf.Approximately(mana.Current, 40f) && c.Overdrive, "crimson: Overdrive regenerates Core Energy");
            c.Add(50f);
            Check(c.Gauge01 < 1f, "crimson: no build-up during Overdrive");
            c.Tick(11.5f);
            Check(!c.Overdrive && !c.Discharging && c.Instability == 0f && Mathf.Approximately(c.DamageScale, 1f), "crimson: clean expiry resets");
            c.Add(500f); mana.Drain(mana.Current - 2f); c.Tick(0.1f);
            Check(c.Overdrive, "crimson: a near-empty core still ignites");
            c.Tick(1f);
            Check(c.Overdrive && mana.Current > 2f, "crimson: the pool refills inside Overdrive");
            mana.Drain(mana.Current); c.Tick(0.1f);
            Check(!c.Overdrive && c.Discharging && mana.Current == 0f && c.Instability == 0f, "crimson: zero Core Energy discharges");
            c.Add(50f);
            Check(c.Instability == 0f, "crimson: vented blade can't build during lockout");
            c.Tick(3.1f); c.Add(8f);
            Check(!c.Discharging && Mathf.Approximately(c.Instability, 8f), "crimson: builds again after lockout");
            mana.ConfigureMax(50f); c.Add(500f); c.Tick(0.1f); c.testEquipped = false; c.Tick(0.1f);
            Check(c.Overdrive && c.GaugeVisible && Mathf.Approximately(c.DamageScale, 1f),
                "crimson: Overdrive survives a katana swap but only empowers crimson contacts");
            c.Tick(20f); c.Tick(4f); // runs dry → discharge → lockout clears
            Check(!c.Overdrive && c.Instability == 0f && !c.GaugeVisible, "crimson: gauge hides once the katana core is empty");
        }
        finally { Object.DestroyImmediate(host); }
    }

    /// <summary>Overdrive edge arcs: the deepest inset stays in the outer 10%
    /// (centre clear), and perimeter walks wrap corners exactly.</summary>
    private static void OverdriveEdgeBand()
    {
        var r = new Rect(-960f, -540f, 1920f, 1080f);
        var deepest = 0f;
        for (var i = 0; i < 400; i++)
        {
            var p = OverdriveEdge.Edge(r, i * 0.01f, OverdriveEdge.MaxInset);
            var dx = Mathf.Min(p.x - r.xMin, r.xMax - p.x) / r.width;
            var dy = Mathf.Min(p.y - r.yMin, r.yMax - p.y) / r.height;
            deepest = Mathf.Max(deepest, Mathf.Min(dx, dy));
        }
        Check(deepest <= 0.1f + 1e-4f, $"overdrive edge: arcs stay in the outer 10% (deepest {deepest:P1})");
        var fwd = OverdriveEdge.Advance(r, 0.95f, 200f); // 96px to the bottom-right corner, 104px up the right side
        Check(Mathf.Abs(fwd - (1f + 104f / 1080f)) < 1e-3f, "overdrive edge: forward walk wraps the corner");
        var back = OverdriveEdge.Advance(r, 0.02f, -100f); // 38.4px back to bottom-left, then 61.6px up the left side
        Check(Mathf.Abs(back - (4f - 61.6f / 1080f)) < 1e-3f, "overdrive edge: backward walk wraps through u=0");
    }

    private static void CrowdCombat()
    {
        var requests = new RecoveryBuffer<string>();
        requests.Set("art at press", 1f);
        Check(!requests.TryTake(1.1f, false, out _) && requests.HasRequest, "crowd: windup keeps captured request");
        Check(requests.TryTake(1.2f, true, out var captured) && captured == "art at press", "crowd: recovery consumes captured art");
        Check(!requests.TryTake(1.21f, true, out _), "crowd: one request, one commit");
        requests.Set("normal", 2f); requests.Set("dodge", 2.05f);
        Check(requests.TryTake(2.2f, true, out captured) && captured == "dodge", "crowd: latest request replaces");
        requests.Set("art", 3f);
        Check(!requests.TryTake(3.251f, true, out _), "crowd: expired request rejected");
        Check(CombatContactClock.Crosses(0.2f, 0.7f, 0.4f, 0.45f), "crowd: skipped narrow contact still detected");
        var manaHost = new GameObject("Crowd mana check");
        try
        {
            var pool = manaHost.AddComponent<PlayerMana>(); pool.ConfigureMax(50f);
            Check(pool.TrySpend(20f) && Mathf.Approximately(pool.Current, 30f), "crowd: mana pays exact cost");
            Check(!pool.TrySpend(31f) && Mathf.Approximately(pool.Current, 30f), "crowd: failed payment leaves mana intact");
        }
        finally { Object.DestroyImmediate(manaHost); }
        var arts = AssetDatabase.FindAssets("t:WeaponArt", new[] { "Assets/_Project/Combat/Arts" })
            .Select(g => AssetDatabase.LoadAssetAtPath<WeaponArt>(AssetDatabase.GUIDToAssetPath(g))).ToArray();
        var crowd = arts.Where(x => x.BigSwordSkill).ToArray();
        Check(crowd.Length == 15, "crowd: all 15 Big Sword entries retained");
        foreach (var art in crowd)
        {
            Check(art.manaCost >= 0f && art.cooldown >= 1.2f, "crowd: cost/cooldown " + art.artName);
            Check(Mathf.Approximately(art.recoveryTransitionDelay, 0.06f), "crowd: recovery " + art.artName);
            Check(art.hitstop >= 0.039f && art.hitstop <= 0.101f, "crowd: bounded freeze " + art.artName);
            Check(art.summonScale >= 1.3f, "crowd: oversized spectral blade " + art.artName);
            Check(art.hitWindows != null && art.hitWindows.Length > 0 && art.hitWindows.All(w => w.x >= 0f && w.y <= 1f && w.y > w.x),
                "crowd: contact windows " + art.artName);
            if (art.rehitInterval > 0f)
                Check(art.rehitInterval < art.hitWindows.Max(w => w.y - w.x) * art.duration,
                    "crowd: re-hit interval fits a window " + art.artName);
            if (art.burst != null && art.burst.enabled)
            {
                Check(art.burst.timeScale > 0f && art.burst.timeScale <= 0.3f && art.burst.rageSeconds > 0f,
                    "burst: spec sane + rage window " + art.artName);
                Check(art.hitWindows != null && art.hitWindows.Length > 0,
                    "burst: snap-back needs a hit window " + art.artName);
            }
            if (art.followUp != null)
                Check(art.followUp.BigSwordSkill, "chain: follow-up stays in the archetype " + art.artName);
            Check(art.trailWindows != null && art.trailWindows.Length > 0, "crowd: blade gates " + art.artName);
        }
        var launcher = arts.FirstOrDefault(x => x.launch);
        Check(launcher != null && launcher.artName == "Upper Attack", "air: Upper Attack is the launcher");
        Check(launcher != null && launcher.family == ActionFamily.Launcher && launcher.launchMax == 3
              && Mathf.Approximately(launcher.manaCost, 0f) && Mathf.Approximately(launcher.staminaCost, 14f)
              && Mathf.Approximately(launcher.damagePerHit, 24f) && Mathf.Approximately(launcher.poiseDamage, 30f)
              && Mathf.Approximately(launcher.range, 2.6f) && Mathf.Approximately(launcher.arc, 120f),
              "air: launcher tuning per detail §118 (24dmg/30poise/14stam/0mana/2.6m/120°)");
        var chase = arts.FirstOrDefault(x => x.airChase);
        Check(chase != null && chase.spike && chase.artName == "Skyfall Edge", "air: Skyfall Edge is the chase smash");
        var graveRend = arts.FirstOrDefault(x => x.artName == "Grave Rend");
        Check(graveRend != null && graveRend.followUp == null, "chain: Grave Rend ends its route (no -> full Grave Wolf)");
        Check(chase != null && chase.followUp == null, "chain: Skyfall Edge has no automatic follow-up");
        Check(crowd.Count(a => a.followUp != null) >= 5, "chain: at least five technique links");
        Check(arts.Count(a => !a.BigSwordSkill && a.contactMode == ArtContactMode.Frontal) >= 7,
            "arts: equipped-weapon Q set survives (no big-sword summon)");
        var wave = arts.FirstOrDefault(x => x.artName == "Arc Blade");
        Check(wave != null && wave.contactMode == ArtContactMode.ProjectileOnly && wave.projectile != null &&
            wave.projectile.wave && wave.projectile.pierce && Mathf.Approximately(wave.projectile.maxTravel, 12f), "crowd: projectile-only piercing wave");
        var burst = arts.FirstOrDefault(x => x.artName == "Last Eclipse");
        Check(burst != null && burst.family == ActionFamily.Ultimate && burst.burst != null && burst.burst.enabled
              && Mathf.Approximately(burst.burst.timeScale, 0.05f) && burst.burst.rageSeconds > 0f,
            "burst: Last Eclipse is the dedicated-input ultimate (0.05 slowdown, rage on completion)");
        Check(arts.Count(a => a.burst != null && a.burst.enabled) == 1, "burst: exactly one ult-flagged art");
        Debug.Log($"[SelfCheck] Reviewed contact motions: {crowd.Count(a => a.contactMotionReviewed)}/15. Unreviewed gates are provisional.");
        Debug.Log("[SelfCheck] Timing/grip/device/crowd physics require preview and Play Mode; these are data checks.");
    }

    /// <summary>Kill-mana credit: a player-attributed hit credits the death,
    /// including an environmental killing blow inside the window; respawn and
    /// revive clear it. Boss gating lives in EnemyAI.OnDied (brains never carry
    /// EnemyAI) so it isn't exercised here.</summary>
    private static void KillMana()
    {
        var host = new GameObject("SelfCheck_KillMana") { hideFlags = HideFlags.HideAndDontSave };
        var hero = new GameObject("SelfCheck_Hero") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var health = host.AddComponent<Health>();
            var pool = hero.AddComponent<PlayerMana>();
            health.ResetHealth(); // Awake doesn't run in edit mode — set the pool explicitly
            health.TakeDamage(10f, Vector3.zero, 0f, DamageKind.Normal, hero.transform);
            Check(health.TryKillCredit(out var p, out _) && p == pool, "mana: player-owned hit credits the kill");
            health.TakeDamage(9999f, Vector3.zero); // no attacker — a pit finish still counts
            Check(health.IsDead && health.TryKillCredit(out _, out _), "mana: recent player hit covers environmental kill");
            health.ResetHealth();
            Check(!health.TryKillCredit(out _, out _), "mana: respawn clears credit");
            health.TakeDamage(9999f, Vector3.zero);
            Check(!health.TryKillCredit(out _, out _), "mana: uncredited kill pays nothing");
        }
        finally
        {
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(hero);
        }
    }

    private static void BladeAndPlunge()
    {
        Check((int)HumanBodyBones.RightHand == 18, "blade: RightHand enum is 18");
        var set = ScriptableObject.CreateInstance<WeaponSet>();
        try
        {
            set.socketDeltaScale = 0f;
            set.weaponRotOffset = Vector3.zero;
            set.weaponPosOffset = Vector3.zero;
            set.bladeRoll = 0f;
            var sourceFrame = Quaternion.Euler(0f, 90f, 0f);
            var authored = Quaternion.Euler(20f, 35f, 40f);
            var source = BladePoseResolver.Capture(sourceFrame, Vector3.one,
                sourceFrame * authored, Vector3.zero);
            var palm = new Vector3(2f, 1f, -3f);
            var targetFrame = Quaternion.Euler(0f, -25f, 0f);
            var pose = BladePoseResolver.Resolve(source, targetFrame, palm, Quaternion.identity, set);
            Check((pose.position - palm).sqrMagnitude < 0.000001f, "blade: palm lock excludes source socket translation");
            Check(Quaternion.Angle(pose.rotation, targetFrame * authored) < 0.01f,
                "blade: source rotation maps through target actor frame");
            var outgoing = new BladePoseResolver.SourcePose(Quaternion.identity, Vector3.zero);
            var incoming = new BladePoseResolver.SourcePose(Quaternion.Euler(0f, 90f, 0f), Vector3.one);
            Check(Quaternion.Angle(BladePoseResolver.Blend(outgoing, incoming, 0f).rotation, outgoing.rotation) < 0.01f &&
                Quaternion.Angle(BladePoseResolver.Blend(outgoing, incoming, 1f).rotation, incoming.rotation) < 0.01f,
                "blade: crossfade endpoints retain their sampled poses");
            var scale = BladePoseResolver.LocalScale(Vector3.one, Vector3.one * 0.01f);
            Check((scale * 0.01f - Vector3.one).sqrMagnitude < 0.000001f,
                "blade: centimetre-scaled hand compensated once");
        }
        finally { Object.DestroyImmediate(set); }
        Check(Mathf.Approximately(AttackController.PlungePreparationVelocity(6f, 0.08f), 0f) &&
            Mathf.Approximately(AttackController.PlungePreparationVelocity(-12f, 0.08f), 0f),
            "plunge: rising and falling velocity brake to hang");
        Check(Mathf.Approximately(AttackController.PlungeDescentVelocity(0f), 0f) &&
            Mathf.Approximately(AttackController.PlungeDescentVelocity(0.18f), -18f) &&
            Mathf.Approximately(AttackController.PlungeDescentVelocity(30f), -18f),
            "plunge: descent ramps to bounded terminal velocity");
        Check(AttackController.PlungeSurfaceWalkable(Vector3.up, 45f) &&
            !AttackController.PlungeSurfaceWalkable(Vector3.right, 45f) &&
            !AttackController.PlungeSurfaceWalkable(Vector3.down, 45f), "plunge: floor accepted, wall and ceiling rejected");
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
        if (controller != null)
        {
            foreach (var name in new[] { "DiveStart", "DiveAttack", "DiveLand" })
            {
                var phase = controller.layers[0].stateMachine.states.Select(s => s.state).FirstOrDefault(s => s.name == name);
                Check(phase != null && phase.motion is AnimationClip && phase.transitions.Length == 0,
                    "plunge: owned phase with no automatic exits " + name + " (run manual setup first)");
                if (phase == null || !(phase.motion is AnimationClip clip)) continue;
                if (name == "DiveStart") Check(Mathf.Abs(clip.length / phase.speed - 0.35f) < 0.002f, "plunge: preparation fitted to 0.35 seconds");
                if (name == "DiveAttack") Check(clip.isLooping && Mathf.Abs(clip.length - 0.4f) < 0.002f,
                    "plunge: generated 0.4-second fall loop");
                if (name == "DiveLand") Check(Mathf.Approximately(phase.speed, 1.2f), "plunge: grounded recovery playback");
            }
        }
        else Check(false, "plunge: player controller available");
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/FX/Crowd" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            foreach (var renderer in prefab.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                var system = renderer.GetComponent<ParticleSystem>();
                Check(renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null &&
                    renderer.sharedMaterial.shader.name.StartsWith("Universal Render Pipeline/"),
                    "effects: valid URP particle material " + path + "/" + renderer.name);
                if (system != null && system.trails.enabled)
                    Check(renderer.trailMaterial != null && renderer.trailMaterial.shader != null,
                        "effects: valid trail material " + path + "/" + renderer.name);
            }
        }
    }

    /// <summary>The earned ultimate meter (detail §223): per-action cap 12,
    /// origin resets the cap, full-then-spend once.</summary>
    private static void UltMeter()
    {
        var go = new GameObject("SelfCheck_Ult") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var meter = go.AddComponent<UltCharge>();
            meter.Credit(1, 20f); // one action must not pay past the cap
            Check(Mathf.Approximately(meter.Current, UltCharge.PerActionCap),
                  "ult: one action caps at 12 charge");
            meter.Credit(1, 4f); // same origin again — still capped
            Check(Mathf.Approximately(meter.Current, UltCharge.PerActionCap),
                  "ult: repeat contacts from one action stay capped");
            meter.Credit(2, 3f); meter.Credit(3, 4f); meter.Credit(4, 8f);
            Check(Mathf.Approximately(meter.Current, 27f), "ult: +3 normal / +4 art / +8 elite accumulate");
            for (var i = 0; i < 30; i++) meter.Credit(100 + i, 8f);
            Check(meter.Full, "ult: meter reaches full");
            Check(meter.TrySpend() && Mathf.Approximately(meter.Current, 0f),
                  "ult: spend takes the full 100 exactly once");
            Check(!meter.TrySpend(), "ult: second spend denied on the empty meter");
        }
        finally { Object.DestroyImmediate(go); }
    }

    /// <summary>Checkpoint level-ups (PlayerStats): the souls gate, exact
    /// deduction, +1 level, the pool re-derived through StatRules, the cap,
    /// and the character.json round-trip — the file is backed up and restored
    /// because Persist writes the real save.</summary>
    private static void LevelUp()
    {
        var go = new GameObject("SelfCheck_LevelUp") { hideFlags = HideFlags.HideAndDontSave };
        var banked = SoulsWallet.Souls;
        var path = Path.Combine(Application.persistentDataPath, "character.json");
        var hadFile = File.Exists(path);
        var backup = hadFile ? File.ReadAllText(path) : null;
        try
        {
            SoulsWallet.Add(-SoulsWallet.Souls); // zero the purse
            var hp = go.AddComponent<PlayerHealth>();
            hp.ConfigureMax(CharacterCatalog.StatRules.Hp(10)); // vigor 10 -> 100 HP
            var stats = go.AddComponent<PlayerStats>();

            Check(stats.Level(PlayerStats.Stat.Vigor) == 10,
                  "levelup: vigor derives from the live pool (100 HP = 10)");
            Check(!stats.TryUpgrade(PlayerStats.Stat.Vigor) && SoulsWallet.Souls == 0
                  && stats.Level(PlayerStats.Stat.Vigor) == 10,
                  "levelup: empty purse refuses, nothing spent");

            SoulsWallet.Add(500);
            var cost = stats.Cost(PlayerStats.Stat.Vigor); // 50 + 8*10 = 130
            Check(stats.TryUpgrade(PlayerStats.Stat.Vigor), "levelup: affordable buy succeeds");
            Check(SoulsWallet.Souls == 500 - cost, "levelup: exact souls deducted once");
            Check(stats.Level(PlayerStats.Stat.Vigor) == 11
                  && Mathf.Approximately(hp.Max, CharacterCatalog.StatRules.Hp(11)),
                  "levelup: +1 vigor applied to the live pool");
            Check(Mathf.Approximately(hp.Current, hp.Max), "levelup: pool refills on upgrade");

            if (hadFile)
                Check(CharacterBuildData.Load().vigor == 11, "levelup: character.json round-trips the level");
            else
                Check(!File.Exists(path), "levelup: no character file — session-only, nothing invented");

            SoulsWallet.Add(99999);
            var guard = 0;
            while (stats.TryUpgrade(PlayerStats.Stat.Vigor) && guard++ < 40) { }
            Check(stats.Level(PlayerStats.Stat.Vigor) == CharacterCatalog.StatRules.Cap
                  && stats.Capped(PlayerStats.Stat.Vigor), "levelup: levels cap at StatRules.Cap (30)");
        }
        finally
        {
            SoulsWallet.Add(banked - SoulsWallet.Souls);
            if (hadFile) File.WriteAllText(path, backup);
            else if (File.Exists(path)) File.Delete(path);
            Object.DestroyImmediate(go);
        }
    }

    /// <summary>The aerial session's generated states (detail §4): the three
    /// discrete AirStrike states must exist on the base layer — they power the
    /// deliberate 1/2/3 chain.</summary>
    private static void AerialSetup()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
        if (controller == null) { Check(false, "aerial: player controller available"); return; }
        var names = new HashSet<string>(controller.layers[0].stateMachine.states.Select(s => s.state.name));
        foreach (var n in new[] { "AirStrike1", "AirStrike2", "AirStrike3" })
            Check(names.Contains(n), "aerial: state exists " + n);
        var hasUpper = controller.layers.Any(l => l.name == "UpperBody" && l.avatarMask != null
                        && l.blendingMode == AnimatorLayerBlendingMode.Override);
        Check(hasUpper, "drink: masked UpperBody layer present (moving sip)");
    }

    private static void HudArtKit()
    {
        var bad = new List<string>();
        foreach (var (name, rows) in HudArt.AllBitmaps())
        {
            if (rows.Any(r => r.Length != rows[0].Length)) bad.Add(name + ":ragged");
            if (rows.Any(r => r.Any(c => !HudArt.KnownChar(c)))) bad.Add(name + ":unknown char");
        }
        Check(bad.Count == 0, "hud art: bitmaps well-formed " + string.Join("; ", bad));
        Check(HudArt.DigitRows.Length == 10 && HudArt.DigitRows.All(d => d.Length == 5 && d.All(r => r.Length == 3)),
              "hud art: digits 0-9 are 3x5 glyphs");
        Check(HudArt.SkillIds.Count() == 12 && HudArt.SkillIds.All(id => HudArt.TryResolveSkill(id, null, out _)),
              "hud art: 9 skill icons resolve by id");

        var unresolved = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:WeaponArt", new[] { "Assets/_Project/Combat/Arts" }))
        {
            var art = AssetDatabase.LoadAssetAtPath<WeaponArt>(AssetDatabase.GUIDToAssetPath(guid));
            if (art != null && !HudArt.TryResolveSkill(art.icon, art.artName, out _)) unresolved.Add(art.name);
        }
        Check(unresolved.Count == 0, "hud art: every WeaponArt has a drawn skill icon " + string.Join(", ", unresolved));
        var noIcon = new List<string>();
        foreach (var guid in AssetDatabase.FindAssets("t:ItemDef", new[] { "Assets/_Project/Items" }))
        {
            var item = AssetDatabase.LoadAssetAtPath<ItemDef>(AssetDatabase.GUIDToAssetPath(guid));
            if (item != null && !HudArt.HasItemIcon(item.icon, item.itemName)) noIcon.Add(item.name);
        }
        foreach (var guid in AssetDatabase.FindAssets("t:WeaponSet", new[] { "Assets/_Project/Combat" }))
        {
            var set = AssetDatabase.LoadAssetAtPath<WeaponSet>(AssetDatabase.GUIDToAssetPath(guid));
            if (set != null && !string.IsNullOrEmpty(set.displayName) && !HudArt.HasItemIcon(null, set.displayName)) noIcon.Add(set.name);
        }
        Check(noIcon.Count == 0, "hud art: every item + weapon has a drawn icon " + string.Join(", ", noIcon));

        var frameOk = true;
        foreach (HudArt.Kind k in System.Enum.GetValues(typeof(HudArt.Kind)))
        {
            var sp = HudArt.Frame(k);
            var want = HudArt.BorderOf(k);
            frameOk &= sp != null && sp.rect.width == 24 && want >= 3 && want <= 6 &&
                       Mathf.Approximately(sp.border.x, want) && sp.border.x * 2 < 24;
        }
        Check(frameOk, "hud art: every frame kind is a 24px 9-slice with its declared border (3–6)");
        Check(HudArt.OutlineProbe() == 4, "hud art: outline wraps a lone texel in a 4-texel ink plus");

        var g = HudArt.Gourd();
        var l = HudArt.GourdLiquid();
        Check(g.rect.size == l.rect.size && g.rect.width == HudArt.GourdW && g.rect.height == HudArt.GourdH,
              "hud art: gourd and liquid sprites share one grid");
        // Window rows via the same teardrop test the liquid is drawn with
        // (the sprite textures are non-readable, geometry is checked directly).
        int low = int.MaxValue, high = -1;
        var ringInBody = true;
        for (var y = 0; y < HudArt.GourdH; y++)
        for (var x = 0; x < HudArt.GourdW; x++)
        {
            float fx = x + 0.5f, fy = y + 0.5f;
            if (HudArt.InWin(fx, fy)) { low = Mathf.Min(low, y); high = Mathf.Max(high, y); }
            // At least 2 texels of wood around the glass edge.
            if (HudArt.WinDist(fx, fy) <= 3.1f && !HudArt.InBody(fx, fy)) ringInBody = false;
        }
        Check(ringInBody, "hud art: glass window keeps a wood ring inside the gourd body");
        Check(HudArt.SnappedSurface(0f) <= low && HudArt.SnappedSurface(1f) >= high + 1 &&
              Mathf.Approximately(HudArt.LiquidFill(1f) * HudArt.GourdH, HudArt.SurfaceY(1f)),
              $"hud art: snapped liquid cut empties below row {low} and fills past row {high}");

        // The number tag overlaps the gourd's bottom-right rim: its inner
        // corner sits on wood, its outer corner hangs off the silhouette.
        Check(HudArt.InBody(HudArt.TagCx - 2f, HudArt.TagCy + 2f) &&
              !HudArt.InBody(HudArt.TagCx + 3f, HudArt.TagCy - 3f),
              "hud art: number tag overlaps the gourd's bottom-right rim");
        Check(HudArt.Dot() != null && HudArt.Knot(true).rect.height <= 15 && HudArt.Medallion().rect.width >= 22,
              "hud art: lock-on dot, knot caps and medallion build at the expected size");
    }

    private static void Stats()
    {
        const int b = CharacterCatalog.StatRules.Base;
        Check(CharacterCatalog.StatRules.Remaining(b, b, b) == 20, "stats: fresh build has 20 points");
        Check(CharacterCatalog.StatRules.Remaining(30, b, b) == 0, "stats: maxing one stat spends the pool");
        Check(!CharacterCatalog.StatRules.CanRaise(30, 30, b, b), "stats: cap blocks raise");
        Check(!CharacterCatalog.StatRules.CanRaise(b, 30, b, b), "stats: empty pool blocks raise");
        Check(CharacterCatalog.StatRules.CanRaise(b, 20, b, b), "stats: points left allows raise");
        Check(!CharacterCatalog.StatRules.CanLower(b), "stats: can't drop below base");
        Check(Mathf.Approximately(CharacterCatalog.StatRules.Hp(10), 100f) && Mathf.Approximately(CharacterCatalog.StatRules.Hp(30), 200f), "stats: HP 100→200");
        Check(Mathf.Approximately(CharacterCatalog.StatRules.Stamina(10), 100f), "stats: stamina 100 at base");
        Check(Mathf.Approximately(CharacterCatalog.StatRules.Mana(10), 60f) && Mathf.Approximately(CharacterCatalog.StatRules.Mana(30), 140f), "stats: mana 60→140");
    }

    private static void Travel()
    {
        var a = NewCheckpoint("A", true);
        var b = NewCheckpoint("B", true);
        var c = NewCheckpoint("C", false);
        try
        {
            var pool = new[] { a, b, c };
            var fromA = Checkpoint.TravelTargets(a, pool);
            Check(fromA.Count == 1 && fromA[0] == b, "travel: lists lit others only (not self, not unlit)");
            c.LightSilently();
            Check(Checkpoint.TravelTargets(a, pool).Count == 2, "travel: lighting adds a destination");
        }
        finally
        {
            Object.DestroyImmediate(a.gameObject);
            Object.DestroyImmediate(b.gameObject);
            Object.DestroyImmediate(c.gameObject);
        }
    }

    private static Checkpoint NewCheckpoint(string name, bool lit)
    {
        var go = new GameObject("SelfCheck_" + name) { hideFlags = HideFlags.HideAndDontSave };
        var cp = go.AddComponent<Checkpoint>();
        if (lit) cp.LightSilently();
        return cp;
    }

    private static void Attune()
    {
        var go = new GameObject("SelfCheck_Arts") { hideFlags = HideFlags.HideAndDontSave };
        var arts = new WeaponArt[3];
        try
        {
            var caster = go.AddComponent<WeaponArtCaster>();
            for (var i = 0; i < arts.Length; i++)
            {
                arts[i] = ScriptableObject.CreateInstance<WeaponArt>();
                arts[i].shortName = "A" + i;
            }
            var so = new SerializedObject(caster);
            var lib = so.FindProperty("library");
            lib.arraySize = arts.Length;
            for (var i = 0; i < arts.Length; i++) lib.GetArrayElementAtIndex(i).objectReferenceValue = arts[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            caster.Attune(1, 2);
            Check(caster.SlotName(1) == "A2", "arts: attune puts the library entry in the slot");
            caster.Attune(1, 9);
            caster.Attune(5, 0);
            Check(caster.SlotName(1) == "A2", "arts: out-of-range attune is ignored");
        }
        finally
        {
            Object.DestroyImmediate(go);
            foreach (var a in arts) if (a != null) Object.DestroyImmediate(a);
        }
    }

    /// <summary>Sprint-attack gate: the clock must grow while sprinting and
    /// hard-reset on any interruption — the dash attack keys off its age.</summary>
    private static void SprintClock()
    {
        var t = 0f;
        for (var i = 0; i < 60; i++) t = PlayerLocomotion.SprintClockStep(t, true, 1f / 60f);
        Check(Mathf.Approximately(t, 1f), "sprint: clock reaches 1.0s after a second of sprint");
        Check(PlayerLocomotion.SprintClockStep(t, false, 0f) == 0f, "sprint: clock resets on release");
        Check(PlayerLocomotion.SprintClockStep(0f, true, 0.5f) < 1f, "sprint: half-second sprint stays under the dash threshold");
    }

    /// <summary>Weapon-art assets match the animator: every state exists,
    /// channelled arts have loop+end states, windows are normalized, FX cues
    /// resolved. Skips cleanly before Setup Combat Locomotion has run.</summary>
    private static void ArtAssets()
    {
        var arts = AssetDatabase.FindAssets("t:WeaponArt", new[] { "Assets/_Project/Combat/Arts" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<WeaponArt>).ToArray();
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
            "Assets/_Project/Animators/PlayerBase.controller");
        if (arts.Length == 0 || controller == null)
        {
            Debug.Log("[SelfCheck] SKIP arts: no generated assets — run Setup Combat Locomotion first");
            return;
        }
        var states = new HashSet<string>(controller.layers[0].stateMachine.states.Select(s => s.state.name));
        var bad = new List<string>();
        foreach (var a in arts)
        {
            if (a == null) continue;
            if (!states.Contains(a.stateName)) bad.Add(a.name + ":missing " + a.stateName);
            // Empty string = "no channel" — only non-empty loop fields must resolve.
            if (!string.IsNullOrEmpty(a.loopState) && !states.Contains(a.loopState)) bad.Add(a.name + ":missing " + a.loopState);
            if (!string.IsNullOrEmpty(a.loopEndState) && !states.Contains(a.loopEndState)) bad.Add(a.name + ":missing " + a.loopEndState);
            if (!string.IsNullOrEmpty(a.loopState) && string.IsNullOrEmpty(a.loopEndState)) bad.Add(a.name + ":loop without end flourish");
            if (a.hitWindows == null || a.hitWindows.Length == 0
                || a.hitWindows.Any(w => w.x < 0f || w.y > 1f || w.x >= w.y)) bad.Add(a.name + ":bad windows");
            if (a.fxCues != null && a.fxCues.Any(c => c == null || c.prefab == null)) bad.Add(a.name + ":empty FX cue");
            // projectile is a serialized struct — never null; prefab==null = melee art (fine).
            if (a.projectile != null && a.projectile.prefab != null
                && (a.projectile.speed <= 0f || a.projectile.life <= 0f || a.projectile.damage <= 0f))
                bad.Add(a.name + ":degenerate projectile spec");
            if (a.duration <= 0.2f) bad.Add(a.name + ":degenerate duration");
        }
        var withProj = arts.Count(a => a != null && a.projectile != null && a.projectile.prefab != null);
        Check(withProj >= 1, $"arts: at least one projectile art ({withProj} found — generator should wire the missile art)");
        Check(bad.Count == 0, $"arts: {arts.Length} assets consistent" +
            (bad.Count == 0 ? "" : " — " + string.Join("; ", bad)));

        // The toxic AoE (Art9): poison-classed, 360° sweep, bloom punch, skull FX.
        var toxic = arts.FirstOrDefault(a => a != null && a.stateName == "Art9");
        Check(toxic != null && toxic.damageKind == DamageKind.Poison
              && Mathf.Approximately(toxic.arc, 360f) && toxic.bloomPunch > 0f,
              "arts: Art9 ToxicChorus is poison-classed, full-circle, bloom-punched");
        Check(toxic != null && toxic.fxCues != null
              && toxic.fxCues.Any(c => c != null && c.prefab != null && c.prefab.name == "FX_ToxicSkulls"),
              "arts: ToxicChorus carries the skull-burst FX cue");
    }

    /// <summary>The alignment-pass states: DiveAttack exists in PlayerBase for
    /// the falling-attack variant, and the swap stand is placed in the scene.</summary>
    private static void Alignment()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
            "Assets/_Project/Animators/PlayerBase.controller");
        if (controller != null)
        {
            var states = new HashSet<string>(controller.layers[0].stateMachine.states.Select(s => s.state.name));
            Check(states.Contains("DiveAttack"), "alignment: DiveAttack state in PlayerBase");
            Check(states.Contains("DiveLand"), "alignment: DiveLand flourish state in PlayerBase");
        }
        else Debug.Log("[SelfCheck] SKIP dive: no PlayerBase.controller — run Setup Combat Locomotion first");

        var pickups = Object.FindObjectsByType<WeaponPickup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var wired = pickups.Count(p => new SerializedObject(p).FindProperty("set").objectReferenceValue != null);
        Check(wired >= 1 || pickups.Length == 0,
              $"alignment: weapon pickup has a set assigned ({wired}/{pickups.Length}) — 0 pickups means Setup hasn't run here");
    }

    /// <summary>BossBase.controller must carry every state BossGolem drives by
    /// name — Locomotion blend, the four melee states, Slam/Roar, Stagger, Die —
    /// plus the Speed float. Skips cleanly before Setup FortGolem Boss runs.</summary>
    private static void BossSetup()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(
            "Assets/_Project/Animations/BossBase.controller");
        if (controller == null)
        {
            Debug.Log("[SelfCheck] SKIP boss: no BossBase.controller — run Setup FortGolem Boss first");
            return;
        }
        var states = new HashSet<string>(controller.layers[0].stateMachine.states.Select(s => s.state.name));
        var needed = new[] { "Locomotion", "Swing", "Crush", "Jab", "Barrage", "Slam", "Roar", "Stagger", "Die" };
        var missing = needed.Where(n => !states.Contains(n)).ToList();
        Check(missing.Count == 0, "boss: all states present" +
            (missing.Count == 0 ? "" : " — missing " + string.Join(", ", missing)));
        Check(controller.parameters.Any(p => p.name == "Speed"), "boss: Speed param present");
        Check(controller.layers[0].iKPass, "boss: IK pass on (FootGrounding)");
    }

    /// <summary>Inventory + save round-trip: stacks cap at stackMax, consume
    /// drops empty rows, Serialize/Restore preserves id:count, unknown ids are
    /// skipped, and weapon ownership registers. Edit-mode safe — Awake never
    /// runs, so only the pure stack ops are exercised.</summary>
    private static void ShopInventory()
    {
        var go = new GameObject("SelfCheck_Inv") { hideFlags = HideFlags.HideAndDontSave };
        var a = ScriptableObject.CreateInstance<ItemDef>();
        var b = ScriptableObject.CreateInstance<ItemDef>();
        var set = ScriptableObject.CreateInstance<WeaponSet>();
        try
        {
            a.itemId = "self_a"; a.stackMax = 3;
            b.itemId = "self_b"; b.stackMax = 99;
            var inv = go.AddComponent<Inventory>();
            inv.Add(a, 2);
            inv.Add(a, 5);
            Check(inv.Count(a) == 3, "inventory: stack caps at stackMax");
            Check(inv.TryConsume(a) && inv.Count(a) == 2, "inventory: consume decrements");
            for (var i = 0; i < 3; i++) inv.TryConsume(a);
            Check(inv.Count(a) == 0 && inv.EntryCount == 0, "inventory: empty rows drop out");

            inv.Add(a, 2);
            inv.Add(b, 4);
            var saved = inv.Serialize();
            Check(saved.Count == 2 && saved.Contains("self_a:2") && saved.Contains("self_b:4"),
                  "inventory: serialize emits id:count pairs");
            saved.Add("bogus_item:9");
            inv.Restore(saved, new List<ItemDef> { a, b });
            Check(inv.Count(a) == 2 && inv.Count(b) == 4 && inv.EntryCount == 2,
                  "inventory: restore round-trips and skips unknown ids");

            inv.Own(set);
            inv.Own(set);
            Check(inv.Owns(set) && inv.OwnedCount == 1, "inventory: weapon ownership dedupes");
        }
        finally
        {
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
            Object.DestroyImmediate(set);
        }
    }

    /// <summary>Ruin-level wiring on the open scene: the LeverGate drives a
    /// GateDoor whose door leaf is assigned, and the pit has a KillZone. The
    /// push/plate puzzle was cut — its absence is verified, not required.</summary>
    private static void LevelSetup()
    {
        var root = GameObject.Find("RuinLevel");
        if (root == null)
        {
            Debug.Log("[SelfCheck] SKIP level: no RuinLevel in this scene — run Build Ruin Level first");
            return;
        }
        var levers = root.GetComponentsInChildren<LeverGate>(true);
        var gates = root.GetComponentsInChildren<GateDoor>(true);
        var kills = root.GetComponentsInChildren<KillZone>(true);
        Check(levers.Length >= 1 && gates.Length >= 1,
              $"level: lever gate present (levers {levers.Length}, gates {gates.Length})");
        var wired = 0;
        foreach (var l in levers)
            if (new SerializedObject(l).FindProperty("gate").objectReferenceValue != null) wired++;
        Check(wired == levers.Length, $"level: all levers wired to gates ({wired}/{levers.Length})");
        Check(kills.Length >= 1, "level: kill zone under the gap");
    }
}
