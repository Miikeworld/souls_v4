using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Combat locomotion setup (Phase 2): adds Locked/MoveX/MoveY parameters, builds the
/// CombatMove 2D strafe blend tree (Big Sword idle + Jogging 8-way), wires
/// Locomotion?�CombatMove transitions plus mirrored Jump/JumpForward exits, creates the
/// BigSword/Katana WeaponSet assets with the katana AnimatorOverrideController, and
/// equips the player with WeaponSocket + BigSword. Idempotent ??safe to re-run.
/// </summary>
public static class ProjectRestartCombat
{
    private const string CombatFolder = "Assets/_Project/Combat";
    private const string BigSwordAssetPath = CombatFolder + "/BigSword.asset";
    private const string KatanaAssetPath = CombatFolder + "/Katana.asset";
    private const string KatanaOverridePath = CombatFolder + "/KatanaCombat.overrideController";
    private const string RunFastLoopPath = CombatFolder + "/Clips/BS_Run_Fast_ver_A_Loop.anim";

    // The pack's canonical avatar: every working clip (453 files) copies
    // M_Big_Sword@Intro.FBX's avatar ??built on the rig's real T-pose. Clips
    // on own-avatar imports retarget through a degenerate auto T-pose.
    private const string GrruzamAvatarFbx =
        "Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_Big_Sword/1_Movements/0__Intro/M_Big_Sword@Intro.FBX";
    // A clip that already references the shared avatar ??reading its importer
    // hands us the exact Avatar object (sub-asset loading can't see it).
    private const string GrruzamAvatarRefFbx =
        "Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_katana_Blade/1_Movements/0__Intro/M_katana_Blade@Intro.FBX";

    // CombatMove blend children: (clip suffix, tree position). Big Sword base
    // clips ??the walk set so locked-on combat is walk-first (run only on the
    // explicit hold-to-sprint input).
    private static readonly (string suffix, Vector2 pos)[] MoveChildren =
    {
        ("Idle", Vector2.zero),
        ("Walk_ver_A_Front", new Vector2(0f, 1f)),
        ("Walk_ver_A_Front_R45", new Vector2(0.7071f, 0.7071f)),
        ("Walk_ver_A_Front_R90", new Vector2(1f, 0f)),
        ("Walk_ver_A_Back_R45", new Vector2(0.7071f, -0.7071f)),
        ("Walk_ver_A_Back", new Vector2(0f, -1f)),
        ("Walk_ver_A_Back_L45", new Vector2(-0.7071f, -0.7071f)),
        ("Walk_ver_A_Front_L90", new Vector2(-1f, 0f)),
        ("Walk_ver_A_Front_L45", new Vector2(-0.7071f, 0.7071f)),
    };

    // Locked dodge states: (state name, clip suffix, clip-pack prefix). All-CLazy
    // quickstep set ??BoostDash darts for front/back, standstill sidesteps for
    // left/right (the Grruzam weapon dodges read as rolls). Entered via
    // CrossFade from DodgeController ??no transitions needed.
    // _Root takes carry authored root travel ??the relay uses their direction
    // while code keeps the tuned dodge speed/distance.
    private static readonly (string state, string suffix, string prefix)[] DodgeStates =
    {
        ("DodgeFront", "Esc_BoostDash_Front_Root", "CLazy@"),
        ("DodgeBack", "Esc_BoostDash_Back_Root", "CLazy@"),
        ("DodgeLeft", "Esc_StandDodge_Left_Root", "CLazy@"),
        ("DodgeRight", "Esc_StandDodge_Right_Root", "CLazy@"),
    };

    // Combo attack states: (state name, Grruzam clip suffix, playback speed).
    // Non-inplace takes ??the RootMotionRelay drives the capsule with their
    // authored travel (stepDistance/advanceDistance stay as fallback). Speeds
    // stay near authored ??faster playback reads as flailing, not responsive.
    private static readonly (string state, string suffix, float speed)[] AttackStates =
    {
        ("Attack1", "Attack_7Combo_1", 1.4f),
        ("Attack2", "Attack_7Combo_2", 1.4f),
        ("Attack3", "Attack_7Combo_3", 1.35f),
        ("Attack4", "Attack_7Combo_4", 1.35f),
        ("Attack5", "Attack_7Combo_5", 1.3f),
        ("Attack6", "Attack_7Combo_6", 1.3f),
        ("Attack7", "Attack_7Combo_7", 1.4f),
    };

    // Big Sword 7Combo base clip ??katana override clip. Katana ships no
    // 7Combo ??the 4Combo set covers the armed attack slots (user picked the
    // 4-hit katana string); the WeaponSet's 4-entry attack list means states
    // 5-7 are never reached. Non-inplace takes: the relay consumes their root
    // travel (the authored root lift that once floated the player mid-combo is
    // a delta the relay drops for Y and the pelvis solve covers at pose level).
    private static readonly (string baseSuffix, string katanaSuffix)[] KatanaAttackMap =
    {
        ("Attack_7Combo_1", "Attack_4Combo_1"),
        ("Attack_7Combo_2", "Attack_4Combo_2"),
        ("Attack_7Combo_3", "Attack_4Combo_3"),
        // Straight 4-combo: hit 4 is the set's own finisher (authored travel,
        // non-inplace). 5Combo_5 was tried and rejected by the user.
        ("Attack_7Combo_4", "Attack_4Combo_4"),
        ("Attack_7Combo_5", "Attack_4Combo_2"),
        ("Attack_7Combo_6", "Attack_4Combo_3"),
        ("Attack_7Combo_7", "Attack_4Combo_4"),
    };

    // Base CombatMove blend children ??katana ver_B _Root set. The _Root
    // files are the unmodified authored takes ??the in-place conversions
    // carried residual hip drift (lean/sink/pop). Root motion is discarded
    // anyway (applyRootMotion off), so _Root clips still play in place but
    // with the clean authored body curves. Idle is ver_B too ??mixing a ver_A
    // idle with ver_B movement snaps the stance on every transition.
    private static readonly (string baseSuffix, string katanaSuffix)[] KatanaMoveMap =
    {
        ("Idle", "Idle_ver_B"),
        ("Walk_ver_A_Front", "Jogging_8Way_verB_F_Root"),
        ("Walk_ver_A_Front_R45", "Jogging_8Way_verB_FR45_Root"),
        ("Walk_ver_A_Front_R90", "Jogging_8Way_verB_R90_Root"),
        ("Walk_ver_A_Back_R45", "Jogging_8Way_verB_BR45_Root"),
        ("Walk_ver_A_Back", "Jogging_8Way_verB_B_Root"),
        ("Walk_ver_A_Back_L45", "Jogging_8Way_verB_BL45_Root"),
        ("Walk_ver_A_Front_L90", "Jogging_8Way_verB_L90_Root"),
        ("Walk_ver_A_Front_L45", "Jogging_8Way_verB_FL45_Root"),
    };

    // Unarmed attack states (CLazy kick chain) ??used whenever the weapon is
    // stowed so out-of-combat attacks need no sword. Shared across weapons.
    private static readonly (string state, string suffix, float speed)[] UnarmedStates =
    {
        ("UnarmedAttack1", "Attack_Kick_A", 1.2f),
        ("UnarmedAttack2", "Attack_Kick_B", 1.2f),
        ("UnarmedAttack3", "Attack_Kick_C", 1.15f),
    };

    [MenuItem("Tools/Project Restart/Setup Combat Locomotion (Weapon + Strafe)")]
    public static void Setup()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
        if (controller == null)
        {
            Debug.LogError("[ProjectRestart] PlayerBase.controller not found ??run Step 5 first.");
            return;
        }

        SetIkPass(controller);
        AddParameterIfMissing(controller, "Locked", AnimatorControllerParameterType.Bool);
        AddParameterIfMissing(controller, "Sprinting", AnimatorControllerParameterType.Bool);
        AddParameterIfMissing(controller, "MoveX", AnimatorControllerParameterType.Float);
        AddParameterIfMissing(controller, "MoveY", AnimatorControllerParameterType.Float);
        AddParameterIfMissing(controller, "WeaponDraw", AnimatorControllerParameterType.Trigger);
        AddParameterIfMissing(controller, "MoveSpeedScale", AnimatorControllerParameterType.Float);
        AddParameterIfMissing(controller, "SprintSpeedScale", AnimatorControllerParameterType.Float);

        var sm = controller.layers[0].stateMachine;
        var combatMove = EnsureCombatMoveState(controller, sm);
        var runFastLoop = EnsureRunFastLoopClip();
        var combatSprint = EnsureCombatSprintState(sm, runFastLoop);
        // Direction-blend trees play at a fixed rate unless told otherwise ??tie
        // playback to the velocity-driven scale params so cadence tracks accel.
        combatMove.speedParameterActive = true;
        combatMove.speedParameter = "MoveSpeedScale";
        combatSprint.speedParameterActive = true;
        combatSprint.speedParameter = "SprintSpeedScale";
        EnsureTransitions(sm, combatMove, combatSprint);
        EnsureCombatLandState(sm, sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion"), combatMove);
        EnsureDodgeStates(sm);
        EnsureAttackStates(sm);
        EnsureDrawState(sm);
        EnsureUtilityStates(sm);
        EnsureAnticipationStates(sm);

        var bigSword = EnsureWeaponSet(BigSwordAssetPath, "Big Sword", "Modeling_Weapon_Big_Sword", null, "M_Big_Sword@");
        if (!bigSword.crimsonCore) { bigSword.crimsonCore = true; EditorUtility.SetDirty(bigSword); }
        var katanaOverride = EnsureKatanaOverride(controller, runFastLoop);
        // 4-hit chain: 4Combo 1-4 straight (the set's own authored finisher).
        EnsureWeaponSet(KatanaAssetPath, "Katana", "Modeling_Weapon_katana_Blade", katanaOverride, "M_katana_Blade@", chainLength: 4);

        EnsurePlayerWiring(bigSword);
        ProjectRestartPlunge.CalibrateBladeSet(bigSword);
        TuneWeaponFeel(bigSword);
        ProjectRestartPlunge.Configure(controller);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectRestart] Combat locomotion setup PASS: CombatMove 2D walk blend, CombatSprint, CLazy quickstep dodges, Attack1-7 combo states, Locked/Sprinting/MoveX/MoveY params, WeaponSets + WeaponSocket/DodgeController/AttackController on player, Health on Targetables. Verify strafe + dodge + combo in Play Mode.");
    }

    [MenuItem("Tools/Project Restart/Setup Fast Crowd Combat")]
    public static void SetupActionPrototype()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Exit Play Mode and wait for import/compilation before action setup.");
            return;
        }
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
        if (controller == null) { Debug.LogError("[ProjectRestart] Missing PlayerBase controller."); return; }
        EnsureAnticipationStates(controller.layers[0].stateMachine);
        EnsureArtStates(controller.layers[0].stateMachine);
        EnsureAerialStates(controller.layers[0].stateMachine);
        EnsureUpperBodyLayer(controller);
        var updatedArts = EnsureArtAssets();
        var plungeReady = ProjectRestartPlunge.Configure(controller);
        foreach (var caster in UnityEngine.Object.FindObjectsByType<WeaponArtCaster>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var so = new SerializedObject(caster);
            SyncLibrary(so, updatedArts);
            // Fixed technique routes (plan §2): these slots aren't
            // checkpoint-attunable, so the table is authoritative — always
            // write the default, repairing stale values from older mappings
            // (e.g. techniqueLauncher holding Mooncleaver from the sprint-
            // launcher era) that an empty-only fill would never fix.
            foreach (var (prop, assetName) in TechniqueDefaults)
            {
                var p = so.FindProperty(prop);
                var art = updatedArts.FirstOrDefault(a => a != null && a.name == assetName);
                if (p != null && art != null && p.objectReferenceValue != art)
                    p.objectReferenceValue = art;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        var katana = AssetDatabase.LoadAssetAtPath<WeaponSet>(CombatFolder + "/Katana.asset");
        if (katana != null) { katana.attackRange = 2.6f; katana.attackArc = 180f; EditorUtility.SetDirty(katana); }
        var bigSword = AssetDatabase.LoadAssetAtPath<WeaponSet>(BigSwordAssetPath);
        ProjectRestartPlunge.CalibrateBladeSet(bigSword);
        TuneWeaponFeel(bigSword);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        var scene = SceneManager.GetActiveScene();
        foreach (var player in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PlayerLocomotion>(true)))
            ProjectRestartTestScene.ApplyActionFraming(player.gameObject);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!plungeReady) Debug.LogWarning("[ProjectRestart] Plunge phases were not installed; see the PlungeSetup diagnostic before playtesting.");
        Debug.Log("[ProjectRestart] Fast crowd combat configured in place; save the scene. The Hero/source animation viewer is optional. Group/device playtests remain required; unreviewed contact gates remain provisional.");
    }

    [MenuItem("Tools/Project Restart/Audit Action Materials + Grip")]
    public static void AuditActionMaterialsAndGrip()
    {
        // Read-only diagnosis: never blanket-convert Fantasy Hero/custom shaders.
        var scene = SceneManager.GetActiveScene();
        var bad = 0;
        foreach (var renderer in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Renderer>(true)))
        {
            var hierarchy = renderer.name;
            for (var parent = renderer.transform.parent; parent != null; parent = parent.parent)
                hierarchy = parent.name + "/" + hierarchy;
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat != null && mat.shader != null && mat.shader.isSupported
                    && mat.shader.name != "Hidden/InternalErrorShader") continue;
                bad++;
                Debug.LogWarning($"[ActionAudit] Renderer {hierarchy}: material={AssetDatabase.GetAssetPath(mat)}; shader={(mat != null && mat.shader != null ? mat.shader.name : "MISSING")}. Diagnose this asset before conversion.", renderer);
            }
        }
        foreach (var socket in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<WeaponSocket>(true)))
        {
            var data = new SerializedObject(socket);
            var set = socket.Set;
            Debug.Log($"[ActionAudit] {socket.name}: handBone={data.FindProperty("handBone").intValue} (expected RightHand={(int)HumanBodyBones.RightHand}); palm={data.FindProperty("palmGrip").floatValue}; manual={data.FindProperty("manualWeapon").objectReferenceValue}; motion={data.FindProperty("motionSet").objectReferenceValue}; shadow={data.FindProperty("shadowRig").objectReferenceValue}; set={set}; bladeAxis={(set != null ? set.bladeAxis : Vector3.zero)}. Preview actual hand/blade placement in Play Mode.", socket);
        }
        foreach (var suffix in new[] { "Attack_4Combo_1", "UpperAttack_ZeroHeight", "Jump_Attack_Combo_1_ZeroHeight" })
        {
            var clip = LoadClip("M_katana_Blade@" + suffix, suffix);
            var importer = clip != null ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip)) as ModelImporter : null;
            Debug.Log($"[ActionAudit] Candidate {suffix}: path={AssetDatabase.GetAssetPath(clip)}; length={(clip != null ? clip.length : 0f):F2}s; sharedAvatar={(importer != null ? importer.sourceAvatar : null)}. Availability does not certify pose/contact suitability.");
        }
        Debug.Log($"[ActionAudit] {bad} unsupported/missing material assignments. No assets changed; shader support alone does not certify appearance.");
    }

    private static AnimatorState EnsureCombatMoveState(AnimatorController controller, AnimatorStateMachine sm)
    {
        var existing = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "CombatMove");
        if (existing != null && existing.motion is BlendTree existingTree &&
            existingTree.blendType == BlendTreeType.FreeformDirectional2D &&
            existingTree.blendParameter == "MoveX" && existingTree.blendParameterY == "MoveY")
        {
            // Rebuild the children if they went stale (e.g. jog ??walk swap) ??            // a missing/renamed clip anywhere means the set no longer matches.
            if (ChildrenMatch(existingTree))
                return existing;
            while (existingTree.children.Length > 0)
                existingTree.RemoveChild(0);
            foreach (var (suffix, pos) in MoveChildren)
            {
                var clip = LoadClip("M_Big_Sword@" + suffix, suffix);
                if (clip == null)
                {
                    Debug.LogError($"[ProjectRestart] Missing Big Sword clip for '{suffix}' ??CombatMove not rebuilt.");
                    return existing;
                }
                existingTree.AddChild(EnsureClipYBake(clip), pos);
            }
            return existing;
        }

        var tree = new BlendTree
        {
            name = "Combat Move",
            blendType = BlendTreeType.FreeformDirectional2D,
            blendParameter = "MoveX",
            blendParameterY = "MoveY",
            useAutomaticThresholds = false
        };

        foreach (var (suffix, pos) in MoveChildren)
        {
            var clip = LoadClip("M_Big_Sword@" + suffix, suffix);
            if (clip == null)
            {
                Debug.LogError($"[ProjectRestart] Missing Big Sword clip for '{suffix}' ??CombatMove not built.");
                return existing;
            }
            tree.AddChild(EnsureClipYBake(clip), pos);
        }

        if (existing == null)
        {
            var state = sm.AddState("CombatMove");
            state.motion = tree;
            existing = state;
        }
        else
        {
            existing.motion = tree;
        }

        AssetDatabase.AddObjectToAsset(tree, controller);
        return existing;
    }

    /// <summary>True when the blend tree's children are exactly the expected clips in order.</summary>
    private static bool ChildrenMatch(BlendTree tree)
    {
        var children = tree.children;
        if (children.Length != MoveChildren.Length) return false;
        for (int i = 0; i < children.Length; i++)
        {
            var clip = children[i].motion as AnimationClip;
            if (clip == null || clip.name != MoveChildren[i].suffix) return false;
        }
        return true;
    }

    /// <summary>
    /// Big Sword's Run_Fast_ver_A ships with loopTime off (it's a transition piece in
    /// the pack's own controller). Rather than edit the vendor .meta, we duplicate the
    /// clip into _Project with looping enabled ??the original asset stays untouched.
    /// </summary>
    private static AnimationClip EnsureRunFastLoopClip()
    {
        var copy = AssetDatabase.LoadAssetAtPath<AnimationClip>(RunFastLoopPath);
        if (copy != null) return copy;

        var source = LoadClip("M_Big_Sword@Run_Fast_ver_A", "Run_Fast_ver_A");
        if (source == null)
        {
            Debug.LogError("[ProjectRestart] Missing Big Sword Run_Fast_ver_A clip.");
            return null;
        }
        source = EnsureClipYBake(source);

        var clone = UnityEngine.Object.Instantiate(source);
        var settings = AnimationUtility.GetAnimationClipSettings(clone);
        settings.loopTime = true;
        AnimationUtility.SetAnimationClipSettings(clone, settings);
        Directory.CreateDirectory(CombatFolder + "/Clips");
        AssetDatabase.CreateAsset(clone, RunFastLoopPath);
        Debug.Log("[ProjectRestart] Created looping Run_Fast_ver_A copy at " + RunFastLoopPath);
        return clone;
    }

    /// <summary>
    /// Weapon-held fast run for sprinting while locked on. Single clip ??sprinting
    /// faces the movement direction, so only the forward run is needed.
    /// </summary>
    private static AnimatorState EnsureCombatSprintState(AnimatorStateMachine sm, AnimationClip clip)
    {
        if (clip == null)
            return sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "CombatSprint");

        var existing = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "CombatSprint");
        if (existing == null)
            existing = sm.AddState("CombatSprint");
        if (existing.motion != clip)
            existing.motion = clip;
        return existing;
    }

    private static void EnsureTransitions(AnimatorStateMachine sm, AnimatorState combatMove, AnimatorState combatSprint)
    {
        var locomotion = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion");
        var jumpStart = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "JumpStart");
        var jumpForwardStart = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "JumpForwardStart");
        var crouchStart = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "CrouchStart");
        if (locomotion == null || combatMove == null)
        {
            Debug.LogError("[ProjectRestart] Locomotion or CombatMove state missing ??transitions not wired.");
            return;
        }

        // Unarmed?�armed stance swaps are whole-body pose changes ??a longer
        // crossfade reads as the body settling into guard instead of a snap.
        AddTransitionIfMissing(locomotion, combatMove, "Locked", AnimatorConditionMode.If, 0f, 0.22f);
        AddTransitionIfMissing(combatMove, locomotion, "Locked", AnimatorConditionMode.IfNot, 0f, 0.22f);
        EnsureLockedLandExit(sm, "JumpLand", locomotion, combatMove);
        EnsureLockedLandExit(sm, "JumpForwardLand", locomotion, combatMove);
        if (jumpStart != null)
            AddTransitionIfMissing(combatMove, jumpStart, "Jump", AnimatorConditionMode.If, 0f, 0.05f);
        if (jumpForwardStart != null)
            AddTransitionIfMissing(combatMove, jumpForwardStart, "JumpForward", AnimatorConditionMode.If, 0f, 0.05f);
        if (crouchStart != null)
            AddTransitionIfMissing(combatMove, crouchStart, "Crouch", AnimatorConditionMode.If, 0f, 0.08f);

        // Sprint-while-locked keys off the Sprinting bool (input intent) ??the damped
        // Speed float waits for the physical speed ramp, which made armed-sprint entry
        // feel late. Strip any old Speed-threshold variants, then add the bool ones.
        if (combatSprint != null)
        {
            RemoveTransition(combatMove, combatSprint, "Speed");
            RemoveTransition(combatSprint, combatMove, "Speed");
            AddTransitionIfMissing(combatMove, combatSprint, "Sprinting", AnimatorConditionMode.If, 0f, 0.16f);
            AddTransitionIfMissing(combatSprint, combatMove, "Sprinting", AnimatorConditionMode.IfNot, 0f, 0.2f);
            AddTransitionIfMissing(combatSprint, locomotion, "Locked", AnimatorConditionMode.IfNot, 0f, 0.22f);
            // Locking on while already sprinting skips the CombatMove hop entirely.
            AddLockedSprintTransition(locomotion, combatSprint);
            if (jumpStart != null)
                AddTransitionIfMissing(combatSprint, jumpStart, "Jump", AnimatorConditionMode.If, 0f, 0.05f);
            if (jumpForwardStart != null)
                AddTransitionIfMissing(combatSprint, jumpForwardStart, "JumpForward", AnimatorConditionMode.If, 0f, 0.05f);
        }
    }

    /// <summary>
    /// Land states exited to Locomotion unconditionally ??locked-on landing
    /// blended into the unarmed clip set for a beat, then Locked snapped the
    /// pose to CombatMove a frame later (the visible pop). Split the exit:
    /// Locomotion only when NOT locked, CombatMove when locked ??the
    /// conditions are exclusive so transition order can't matter.
    /// </summary>
    private static void EnsureLockedLandExit(AnimatorStateMachine sm, string landName, AnimatorState locomotion, AnimatorState combatMove)
    {
        var land = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == landName);
        if (land == null || locomotion == null || combatMove == null) return;

        var toLoco = land.transitions.FirstOrDefault(t => t.destinationState == locomotion &&
            t.conditions.All(c => c.parameter != "Locked"));
        if (toLoco != null)
        {
            var conds = toLoco.conditions.ToList();
            conds.Add(new AnimatorCondition { mode = AnimatorConditionMode.IfNot, parameter = "Locked", threshold = 0f });
            toLoco.conditions = conds.ToArray();
        }

        var toCombat = land.transitions.FirstOrDefault(t => t.destinationState == combatMove &&
            t.conditions.Any(c => c.parameter == "Locked" && c.mode == AnimatorConditionMode.If));
        if (toCombat == null)
        {
            toCombat = land.AddTransition(combatMove);
            toCombat.hasExitTime = true;
            toCombat.exitTime = 0.95f;
            toCombat.duration = 0.15f;
            toCombat.AddCondition(AnimatorConditionMode.If, 0f, "Locked");
        }
    }

    /// <summary>
    /// CombatLand: the armed landing (Grruzam Jump_End ??weapon in guard) played
    /// when touching down while locked on, instead of the unarmed CLazy land.
    /// Its InAir-false entries are moved to the FRONT of each air state's
    /// transition list so they win over the unarmed land exits. Leaves into
    /// CombatMove (or Locomotion if lock dropped mid-air); moving cuts the
    /// recovery short so feet don't slide under a planted pose.
    /// </summary>
    private static AnimatorState EnsureCombatLandState(AnimatorStateMachine sm, AnimatorState locomotion, AnimatorState combatMove)
    {
        var clip = LoadClip("M_Big_Sword@Jump_End_ZeroHeight", "Jump_End_ZeroHeight");
        if (clip == null || locomotion == null || combatMove == null)
        {
            Debug.LogError("[ProjectRestart] CombatLand needs M_Big_Sword@Jump_End_ZeroHeight + Locomotion/CombatMove ??skipped.");
            return null;
        }
        clip = EnsureClipYBake(clip);
        var land = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "CombatLand")
                   ?? sm.AddState("CombatLand", new Vector3(900, 420));
        land.motion = clip;
        land.speed = 1.3f;

        foreach (var airName in new[] { "JumpAir", "JumpForwardAir", "JumpDoubleAir", "JumpDoubleFall" })
        {
            var air = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == airName);
            if (air == null) continue;
            var t = air.transitions.FirstOrDefault(x => x.destinationState == land);
            if (t == null)
            {
                t = air.AddTransition(land);
                t.AddCondition(AnimatorConditionMode.IfNot, 0f, "InAir");
                t.AddCondition(AnimatorConditionMode.If, 0f, "Locked");
            }
            t.hasExitTime = false;
            t.duration = 0.12f;
            // Priority = array order: put the locked land first.
            air.transitions = new[] { t }.Concat(air.transitions.Where(x => x != t)).ToArray();
        }

        land.transitions = Array.Empty<AnimatorStateTransition>();
        void Exit(AnimatorState dest, AnimatorConditionMode lockMode, float exitTime, float duration, bool moving)
        {
            var e = land.AddTransition(dest);
            e.hasExitTime = true;
            e.exitTime = exitTime;
            e.duration = duration;
            e.AddCondition(lockMode, 0f, "Locked");
            if (moving) e.AddCondition(AnimatorConditionMode.Greater, 0.2f, "Speed");
        }
        Exit(combatMove, AnimatorConditionMode.If, 0.3f, 0.18f, true);   // moving: short recovery
        Exit(locomotion, AnimatorConditionMode.IfNot, 0.3f, 0.18f, true);
        Exit(combatMove, AnimatorConditionMode.If, 0.45f, 0.18f, false); // planted: brief land
        Exit(locomotion, AnimatorConditionMode.IfNot, 0.45f, 0.18f, false);
        foreach (var (param, stateName) in new[] { ("Jump", "JumpStart"), ("JumpForward", "JumpForwardStart") })
        {
            var js = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == stateName);
            if (js == null) continue;
            var j = land.AddTransition(js);
            j.hasExitTime = false;
            j.duration = 0.1f;
            j.AddCondition(AnimatorConditionMode.If, 0f, param);
        }
        return land;
    }

    /// <summary>Locomotion ??CombatSprint: Locked AND Sprinting ??the instant armed-run entry.</summary>
    private static void AddLockedSprintTransition(AnimatorState locomotion, AnimatorState combatSprint)
    {
        // Strip the old Speed-threshold variant ??it double-fires alongside the bool one.
        RemoveTransition(locomotion, combatSprint, "Speed");

        foreach (var t in locomotion.transitions)
        {
            if (t.destinationState == combatSprint &&
                t.conditions.Any(c => c.parameter == "Locked" && c.mode == AnimatorConditionMode.If) &&
                t.conditions.Any(c => c.parameter == "Sprinting" && c.mode == AnimatorConditionMode.If))
            {
                t.duration = 0.16f;
                return;
            }
        }
        var transition = locomotion.AddTransition(combatSprint);
        transition.hasExitTime = false;
        transition.duration = 0.16f;
        transition.AddCondition(AnimatorConditionMode.If, 0f, "Locked");
        transition.AddCondition(AnimatorConditionMode.If, 0f, "Sprinting");
    }

    /// <summary>Removes transitions from?�to whose conditions reference the parameter.</summary>
    private static void RemoveTransition(AnimatorState from, AnimatorState to, string parameter)
    {
        var stale = from.transitions.Where(t => t.destinationState == to &&
            t.conditions.Any(c => c.parameter == parameter)).ToList();
        foreach (var t in stale) from.RemoveTransition(t);
    }

    private static void AddTransitionIfMissing(AnimatorState from, AnimatorState to, string parameter, AnimatorConditionMode mode, float threshold, float duration)
    {
        var existing = from.transitions.FirstOrDefault(t => t.destinationState == to &&
            t.conditions.Any(c => c.parameter == parameter && c.mode == mode));
        if (existing != null)
        {
            // Update tuning on re-run: duration + threshold may have changed.
            existing.duration = duration;
            var cond = existing.conditions.First(c => c.parameter == parameter && c.mode == mode);
            if (!Mathf.Approximately(cond.threshold, threshold))
            {
                var conds = existing.conditions.ToList();
                var idx = conds.FindIndex(c => c.parameter == parameter && c.mode == mode);
                conds[idx] = new AnimatorCondition { mode = mode, parameter = parameter, threshold = threshold };
                existing.conditions = conds.ToArray();
            }
            return;
        }
        var transition = from.AddTransition(to);
        transition.hasExitTime = false;
        transition.duration = duration;
        transition.AddCondition(mode, threshold, parameter);
    }

    /// <summary>FootGrounding drives OnAnimatorIK ??every controller that uses
    /// it needs the layer's IK pass or the callback never fires.</summary>
    public static void SetIkPass(AnimatorController controller)
    {
        var layers = controller.layers;
        if (layers.Length == 0 || layers[0].iKPass) return;
        layers[0].iKPass = true;
        controller.layers = layers;
    }

    private static void AddParameterIfMissing(AnimatorController controller, string name, AnimatorControllerParameterType type)
    {
        if (controller.parameters.Any(p => p.name == name && p.type == type)) return;
        controller.AddParameter(name, type);
    }

    /// <summary>Adds the four dodge states with CLazy quickstep clips (entered via
    /// CrossFade ??no transitions). Re-points states that already exist, and scales
    /// playback speed so the clip fills its dodge window exactly.</summary>
    private static void EnsureDodgeStates(AnimatorStateMachine sm)
    {
        foreach (var (stateName, suffix, prefix) in DodgeStates)
        {
            var clip = LoadClip(prefix + suffix, suffix);
            if (clip == null)
            {
                Debug.LogError($"[ProjectRestart] Missing clip {prefix}{suffix} ??{stateName} not built.");
                continue;
            }
            clip = EnsureDodgeRootClip(clip);
            var existing = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == stateName);
            if (existing == null) existing = sm.AddState(stateName);
            if (existing.motion != clip) existing.motion = clip;
            // The dodge window is shorter than the authored clip ??scale playback so the
            // motion completes inside it. Back dodges are always the shorter backstep now.
            var window = stateName == "DodgeBack" ? 0.32f : 0.5f;
            var speed = Mathf.Clamp(clip.length / window, 0.8f, 2.2f);
            if (!Mathf.Approximately(existing.speed, speed)) existing.speed = speed;
            Debug.Log($"[ProjectRestart] {stateName}: clip={clip.name} len={clip.length:F2}s ??speed={speed:F2}");
        }
    }

    /// <summary>
    /// WeaponDraw: a one-shot flourish played when the weapon materializes on
    /// lock-on ??the Grruzam Intro is the pack's draw-the-blade entrance. Base
    /// clip is Big Sword's Intro; the katana override swaps in its own. Entered
    /// from Locomotion/CombatMove on the WeaponDraw trigger (WeaponSocket only
    /// sets it while planted); exits to whichever blend Locked routes to, and
    /// moving breaks it early so the feet never slide under a stance flourish.
    /// </summary>
    private static void EnsureDrawState(AnimatorStateMachine sm)
    {
        var locomotion = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Locomotion");
        var combatMove = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "CombatMove");
        var clip = LoadClip("M_Big_Sword@Intro", "Intro");
        if (locomotion == null || combatMove == null || clip == null)
        {
            Debug.LogError("[ProjectRestart] WeaponDraw needs Locomotion, CombatMove and the M_Big_Sword@Intro clip ??state not built.");
            return;
        }

        var draw = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "WeaponDraw");
        if (draw == null) draw = sm.AddState("WeaponDraw");
        if (draw.motion != clip) draw.motion = clip;

        AddTransitionIfMissing(locomotion, draw, "WeaponDraw", AnimatorConditionMode.If, 0f, 0.14f);
        AddTransitionIfMissing(combatMove, draw, "WeaponDraw", AnimatorConditionMode.If, 0f, 0.14f);
        EnsureDrawExit(draw, combatMove, true);
        EnsureDrawExit(draw, locomotion, false);
    }

    /// <summary>Two exits per destination: a timed one at the flourish's end,
    /// and an early one when the player starts moving (Speed &gt; 0.15).</summary>
    private static void EnsureDrawExit(AnimatorState draw, AnimatorState dest, bool locked)
    {
        var mode = locked ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot;

        var timed = draw.transitions.FirstOrDefault(t => t.destinationState == dest &&
            t.hasExitTime && t.conditions.Any(c => c.parameter == "Locked" && c.mode == mode));
        if (timed == null)
        {
            timed = draw.AddTransition(dest);
            timed.AddCondition(mode, 0f, "Locked");
        }
        timed.hasExitTime = true;
        timed.exitTime = 0.9f;
        timed.duration = 0.15f;

        var interrupt = draw.transitions.FirstOrDefault(t => t.destinationState == dest &&
            !t.hasExitTime && t.conditions.Any(c => c.parameter == "Speed" && c.mode == AnimatorConditionMode.Greater));
        if (interrupt == null)
        {
            interrupt = draw.AddTransition(dest);
            interrupt.AddCondition(AnimatorConditionMode.Greater, 0.15f, "Speed");
            interrupt.AddCondition(mode, 0f, "Locked");
        }
        interrupt.duration = 0.1f;
    }

    private static WeaponSet EnsureWeaponSet(string path, string displayName, string prefabName, AnimatorOverrideController overrideController, string clipPrefix, int chainLength = 0)
    {
        var set = AssetDatabase.LoadAssetAtPath<WeaponSet>(path);
        var created = false;
        if (set == null)
        {
            Directory.CreateDirectory(CombatFolder);
            set = ScriptableObject.CreateInstance<WeaponSet>();
            set.displayName = displayName;
            set.moveSpeed = 4f;
            AssetDatabase.CreateAsset(set, path);
            created = true;
        }

        if (set.weaponPrefab == null)
            set.weaponPrefab = FindPrefab(f => Path.GetFileNameWithoutExtension(f).Equals(prefabName, StringComparison.OrdinalIgnoreCase));
        if (set.overrideController == null && overrideController != null)
            set.overrideController = overrideController;
        // Dodge clip fields mirror the state mapping: all-CLazy quickstep set,
        // shared across weapons. _Root takes carry the travel the relay reads.
        set.dodgeFront = LoadClip("CLazy@Esc_BoostDash_Front_Root", "Esc_BoostDash_Front_Root");
        set.dodgeBack = LoadClip("CLazy@Esc_BoostDash_Back_Root", "Esc_BoostDash_Back_Root");
        set.dodgeLeft = LoadClip("CLazy@Esc_StandDodge_Left_Root", "Esc_StandDodge_Left_Root");
        set.dodgeRight = LoadClip("CLazy@Esc_StandDodge_Right_Root", "Esc_StandDodge_Right_Root");
        set.backstep = LoadClip("CLazy@Esc_BoostDash_Back_Root", "Esc_BoostDash_Back_Root");

        // Sync tuning defaults while they're still the shipped values ??once you
        // hand-tune a field on the asset this stops touching it.
        if (Mathf.Approximately(set.dodgeDistance, 6.5f))
        {
            set.dodgeDistance = 3.4f; set.backstepDistance = 1.6f;
            set.dodgeDuration = 0.5f; set.backstepDuration = 0.32f;
            set.iFrameStart = 0.06f; set.iFrameEnd = 0.23f;
            set.dodgeCooldown = 0.18f;
            set.dodgeEntrySpeed = 1.4f; set.dodgeExitSpeed = 0.7f;
        }
        // Move speed must match the clips that actually play. The base blend
        // runs Walk_ver_A_* (~2.8 m/s cadence); the katana override swaps them
        // for Jogging_8Way_verB_* (jog cadence ~4 m/s) ??at 2.8 the feet outrun
        // the body and it skates. Sets without an override keep the walk pace.
        var targetMoveSpeed = set.overrideController != null ? 4f : 2.8f;
        if (set.overrideController != null ? set.moveSpeed < 3.5f : set.moveSpeed > 3f)
            set.moveSpeed = targetMoveSpeed;

        // Rebuild the combo chain when the length doesn't match the weapon's
        // chain or the durations are stale sub-second windows (migration off
        // the 2?-speed defaults). Hand-tuned values are respected otherwise.
        // chainLength 0 = the full 7-state table; katana runs a 4-hit chain
        // (Attack_4Combo_4 = the set's own authored finisher).
        var chainLen = chainLength > 0 ? chainLength : AttackStates.Length;
        if (set.attacks == null || set.attacks.Length != chainLen
            || set.attacks[0].clip == null || set.attacks[0].advanceDistance <= 0f)
        {
            var damages = new[] { 18f, 20f, 22f, 24f, 26f, 30f, 45f };
            var steps = new[] { 1.2f, 1f, 0.9f, 0.8f, 0.8f, 0.7f, 1f };
            var advances = new[] { 0.9f, 0.9f, 1f, 1f, 1.1f, 1.2f, 1.6f };
            // Short chains still end on a heavy finisher ??bump the last hit.
            if (chainLen < damages.Length) damages[chainLen - 1] = Mathf.Max(damages[chainLen - 1], 40f);
            var a = new AttackStep[chainLen];
            for (var i = 0; i < chainLen; i++)
            {
                var (_, suffix, speed) = AttackStates[i];
                var clip = LoadClip(clipPrefix + suffix, suffix);
                // Weapons without a same-named clip (katana has no 7Combo) fall
                // back to their mapped equivalent for the timing.
                if (clip == null)
                    clip = LoadClip(clipPrefix + KatanaAttackMap[i].katanaSuffix, KatanaAttackMap[i].katanaSuffix);
                var d = clip != null ? clip.length / speed : 1.2f;
                a[i] = new AttackStep
                {
                    clip = clip,
                    duration = d,
                    hitStart = d * 0.38f,
                    hitEnd = d * 0.62f,
                    damage = damages[i],
                    stepDistance = steps[i],
                    advanceDistance = advances[i],
                };
            }
            set.attacks = a;
        }

        // Root-motion policy lives here, not in the rebuild block ??hand-tuned
        // steps survive rebuilds but must still flag for the relay and point at
        // the non-inplace take the state now plays.
        if (set.attacks != null)
        {
            for (var i = 0; i < set.attacks.Length && i < AttackStates.Length; i++)
            {
                var (_, suffix, _) = AttackStates[i];
                var clip = LoadClip(clipPrefix + suffix, suffix)
                    ?? LoadClip(clipPrefix + KatanaAttackMap[i].katanaSuffix, KatanaAttackMap[i].katanaSuffix);
                if (clip != null) set.attacks[i].clip = clip;
                set.attacks[i].useRootMotion = true;
                // The chain finisher is a leap-slam — its authored root Y is
                // the descent. Dropping it left the body hanging mid-pose
                // ("should hit the ground but floats"). Mid-chain steps stay
                // floored; a no-Y clip under verbatim delta is a no-op anyway.
                set.attacks[i].rootMotionY = i == set.attacks.Length - 1;
            }
        }

        if (created) Debug.Log($"[ProjectRestart] Created WeaponSet {path} ({displayName}).");
        if (displayName == "Katana") { set.attackRange = 2.6f; set.attackArc = 180f; }
        EditorUtility.SetDirty(set);
        return set;
    }

    /// <summary>One-off states the souls loop enters via CrossFade (no
    /// transitions): Death, Backstab, Potion, Art1-9, Checkpoint*. Non-Grruzam clips
    /// keep their own humanoid avatar ??the shared-avatar bake is a Grruzam
    /// workaround only, and copying it onto other packs breaks their retarget.</summary>
    private static void EnsureUtilityStates(AnimatorStateMachine sm)
    {
        EnsureNamedState(sm, "Death", LoadClip("M_Big_Sword@Damage_Die", "Damage_Die"), 1f, bake: true);
        // Backstab: SoulslikeEssential's paired stab (enemy side = Backstab_Stabbed),
        // speed-fit so it fills AttackController.BackstabStep's 1.5s window.
        var stab = LoadClip("Backstab_Stab", "Backstab_Stab", "SoulslikeEssential");
        EnsureNamedState(sm, "Backstab", stab, stab != null ? Mathf.Clamp(stab.length / 1.5f, 0.5f, 2f) : 1f);
        // Spells are gone ??weapon arts replace them.
        RemoveStateIfPresent(sm, "Cast");
        EnsureArtStates(sm);

        // Plunge phases are installed together by ProjectRestartPlunge after
        // scene wiring, with native avatars and code-owned landing recovery.

        // SoulslikeEssential is Synty-tailored humanoid with its own avatar
        // (ADC.fbx) ??never Grruzam-baked.
        const string se = "SoulslikeEssential";
        // Flask: the real drink + the empty-flask shake (replaces buff01).
        RemoveStateIfPresent(sm, "Flask");
        EnsureNamedState(sm, "Potion", LoadClip("Potion_Drink", "Potion_Drink", se), 1f);
        EnsureNamedState(sm, "PotionEmpty", LoadClip("Potion_Empty", "Potion_Empty", se), 1f);

        // Checkpoint: ignite, sit ??idle loop, stand.
        EnsureNamedState(sm, "CheckpointIgnite", LoadClip("Bonfire_Ignite", "Bonfire_Ignite", se), 1f);
        var sit = EnsureNamedState(sm, "CheckpointSit", LoadClip("Bonfire_Start", "Bonfire_Start", se), 1f);
        var idle = EnsureNamedState(sm, "CheckpointIdle", LoadClip("Bonfire_Idle", "Bonfire_Idle", se), 1f);
        EnsureNamedState(sm, "CheckpointStand", LoadClip("Bonfire_End", "Bonfire_End", se), 1f);
        if (sit != null && idle != null && !sit.transitions.Any(t => t.destinationState == idle))
        {
            var t = sit.AddTransition(idle);
            t.hasExitTime = true;
            t.exitTime = 0.95f;
            t.duration = 0.1f;
        }
    }

    private static void RemoveStateIfPresent(AnimatorStateMachine sm, string name)
    {
        var st = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == name);
        if (st == null) return;
        sm.RemoveState(st);
        Debug.Log($"[ProjectRestart] Removed stale state {name}.");
    }

    private static AnimatorState EnsureNamedState(AnimatorStateMachine sm, string name, AnimationClip clip, float speed, bool bake = false)
    {
        if (clip == null)
        {
            Debug.LogError($"[ProjectRestart] Missing clip for {name} ??state not built.");
            return null;
        }
        if (bake) clip = EnsureClipYBake(clip);
        var st = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == name) ?? sm.AddState(name);
        if (st.motion != clip) st.motion = clip;
        if (!Mathf.Approximately(st.speed, speed)) st.speed = speed;
        Debug.Log($"[ProjectRestart] {name}: clip={clip.name} len={clip.length:F2}s ??speed={speed:F2}");
        return st;
    }

    /// <summary>Adds the armed (Grruzam) and unarmed (CLazy kick) combo states ??    /// CrossFade-driven, no transitions. Playback speed is synced so each clip
    /// fills its swing window.</summary>
    private static void EnsureAttackStates(AnimatorStateMachine sm)
    {
        // Armed swings use non-inplace takes ??the RootMotionRelay consumes
        // their authored travel. Unarmed CLazy kicks stay baked (scripted step).
        EnsureSwingStates(sm, "M_Big_Sword@", AttackStates, rootMotion: true);
        EnsureSwingStates(sm, "CLazy@", UnarmedStates, rootMotion: false);
    }

    // Reuse the exact attack motion keys so weapon overrides and the shadow
    // rig sample the same pose. Runtime selects normalized windup time; root
    // deltas are discarded because anticipation never owns root motion.
    private static void EnsureAnticipationStates(AnimatorStateMachine sm)
    {
        foreach (var entry in sm.states.Where(x => x.state.name == "ChargeGround" || x.state.name == "ChargeAir").ToArray())
            sm.RemoveState(entry.state);
    }

    private static void EnsureSwingStates(AnimatorStateMachine sm, string prefix, (string state, string suffix, float speed)[] table, bool rootMotion)
    {
        foreach (var (stateName, suffix, speed) in table)
        {
            var clip = LoadClip(prefix + suffix, suffix);
            if (clip == null)
            {
                Debug.LogError($"[ProjectRestart] Missing clip {prefix}{suffix} ??{stateName} not built.");
                continue;
            }
            clip = rootMotion ? EnsureAttackRootClip(clip) : EnsureClipYBake(clip);
            var existing = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == stateName);
            if (existing == null) existing = sm.AddState(stateName);
            if (existing.motion != clip) existing.motion = clip;
            if (!Mathf.Approximately(existing.speed, speed)) existing.speed = speed;
            Debug.Log($"[ProjectRestart] {stateName}: clip={clip.name} len={clip.length:F2}s ??speed={speed:F2} (window {clip.length / speed:F2}s)");
        }
    }

    /// <summary>
    /// Bakes root-transform Y into the pose relative to feet on the clip's source
    /// FBX ??same convention the locomotion clips already follow. Without it the
    /// authored Y dips in low swings replay verbatim and the mesh sinks through
    /// the floor. Returns a fresh clip reference (the import invalidates it).
    /// </summary>
    internal static AnimationClip EnsureClipYBake(AnimationClip clip)
        => EnsureClipRootConfig(clip, bakeY: true, bakeXZ: true, authoredY: false);

    /// <summary>Attack take with real root travel: XZ AND root Y stay root
    /// motion ??the RootMotionRelay consumes XZ and drops Y unless the step
    /// allows it. Authoring the authored-Y path keeps the delta verbatim.</summary>
    internal static AnimationClip EnsureAttackRootClip(AnimationClip clip)
        => EnsureClipRootConfig(clip, bakeY: false, bakeXZ: false, authoredY: true);

    /// <summary>Dodge _Root take: authored XZ becomes the relay's travel
    /// direction (code still owns speed); root Y stays baked via feet so the
    /// crouch/hop pose reads like the old in-place dodges.</summary>
    internal static AnimationClip EnsureDodgeRootClip(AnimationClip clip)
        => EnsureClipRootConfig(clip, bakeY: true, bakeXZ: false, authoredY: false);

    private static AnimationClip EnsureClipRootConfig(AnimationClip clip, bool bakeY, bool bakeXZ, bool authoredY)
    {
        if (clip == null) return clip;
        var path = AssetDatabase.GetAssetPath(clip);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            return clip;
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null) return clip;

        // Every working clip in the pack copies the shared avatar from
        // M_Big_Sword@Intro.FBX ??built on the rig's real T-pose. Stripping it
        // (an earlier feet-sink "fix") left each clip on an auto-generated
        // avatar whose degenerate T-pose warped EVERY retargeted bone ??the
        // lean-back idle / floor-sinking run / constant popping. Restore the
        // shared avatar; the ~2cm leg-length error it warned about is covered
        // by heightFromFeet + the 0.02 height offset below.
        var canonicalAvatar = path.StartsWith("Assets/ThirdParty/GrruzamPowerfulSword/", StringComparison.OrdinalIgnoreCase)
            ? CanonicalGrruzamAvatar() : null;
        var changed = false;
        if (canonicalAvatar != null &&
            (importer.avatarSetup != ModelImporterAvatarSetup.CopyFromOther
             || importer.sourceAvatar != canonicalAvatar))
        {
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = canonicalAvatar;
            changed = true;
        }

        // "Bake Into Pose" flags aren't exposed on ModelImporterClipAnimation ??        // patch the .meta directly and reimport BEFORE reading clipAnimations.
        // Y baked (loopBlendPositionY:1) means root Y replays in the pose ??        // unbaked means it becomes a delta the relay can apply or drop.
        if (SetMetaFlag(path, "loopBlendPositionY", bakeY ? 1 : 0)
            | SetMetaFlag(path, "loopBlendPositionXZ", bakeXZ ? 1 : 0))
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) return clip;
        }

        var infos = importer.clipAnimations;
        if (infos == null || infos.Length == 0) infos = importer.defaultClipAnimations;
        if (infos != null && infos.Length > 0)
        {
            // Katana override clips keep the AUTHORED root Y (the _Root and
            // non-Inplace takes are the clean originals) ??foot-derived Y
            // re-measures the lowest foot every frame, and any noise in that
            // read becomes whole-body sink/snap jitter (and SINKING on attack
            // clips whose authored feet dip below the plane). Other clips keep
            // the feet-aligned bake.
            // Katana override clips keep authored Y for the reasons above. Die
            // clips (4_Damages/6__Die) need it for the opposite reason: the
            // authored fall-to-floor drop must replay verbatim ??feet-derived
            // Y leaves the corpse hanging at wherever the lying pose's feet
            // end up, which is the floating-deadbody bug.
            var keepAuthoredY = authoredY || path.Contains("M_katana_Blade") || path.Contains("6__Die");
            var ybakeChanged = false;
            foreach (var info in infos)
            {
                if (keepAuthoredY)
                {
                    if (info.keepOriginalPositionY && !info.heightFromFeet) continue;
                    info.keepOriginalPositionY = true;
                    info.heightFromFeet = false;
                    ybakeChanged = true;
                }
                else
                {
                    if (info.heightFromFeet && !info.keepOriginalPositionY && Mathf.Approximately(info.heightOffset, 0.02f)) continue;
                    info.heightFromFeet = true;      // feet stay aligned to the root position
                    info.keepOriginalPositionY = false; // derive root Y from feet instead of replaying authored dips
                    info.heightOffset = 0.02f;       // retarget leg-length error (~2cm) sinks toes through the floor ??lift slightly
                    ybakeChanged = true;
                }
            }
            if (ybakeChanged) importer.clipAnimations = infos;
            changed |= ybakeChanged;
        }
        if (!changed) return clip;

        var clipName = clip.name; // capture before reimport ??the clip object is destroyed
        importer.SaveAndReimport();
        Debug.Log($"[ProjectRestart] Root config (bakeY={bakeY}, bakeXZ={bakeXZ}) on {Path.GetFileName(path)}");

        // The reimport destroyed the old clip object ??reload it.
        return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
            .FirstOrDefault(c => c.name == clipName);
    }

    /// <summary>Writes a loopBlendPosition* flag in the FBX .meta to the target
    /// value. These packs are one animation take per file ??a flat patch is
    /// safe. Returns true only when the file actually changed.</summary>
    private static bool SetMetaFlag(string assetPath, string flag, int value)
    {
        var metaPath = assetPath + ".meta";
        if (!File.Exists(metaPath)) return false;
        var meta = File.ReadAllText(metaPath);
        var next = flag + ": " + value;
        if (meta.Contains(next)) return false;
        var cur = flag + ": " + (1 - value);
        if (!meta.Contains(cur)) return false;
        File.WriteAllText(metaPath, meta.Replace(cur, next));
        return true;
    }

    /// <summary>The pack's canonical avatar, read straight off a clip importer
    /// that already references it ??the exact object the working clips copy.
    /// (LoadAllAssetsAtPath doesn't surface Avatar sub-assets reliably.)</summary>
    private static Avatar CanonicalGrruzamAvatar()
    {
        var refImporter = AssetImporter.GetAtPath(GrruzamAvatarRefFbx) as ModelImporter;
        var avatar = refImporter != null ? refImporter.sourceAvatar : null;
        if (avatar == null)
        {
            refImporter = AssetImporter.GetAtPath(GrruzamAvatarFbx) as ModelImporter;
            avatar = refImporter != null ? refImporter.sourceAvatar : null;
        }
        if (avatar == null)
            avatar = AssetDatabase.LoadAssetAtPath<Avatar>(GrruzamAvatarFbx);
        if (avatar == null)
            Debug.LogWarning("[ProjectRestart] Canonical Grruzam avatar not found ??checked " + GrruzamAvatarRefFbx);
        return avatar;
    }

    /// <summary>
    /// Katana override: swaps every CombatMove blend clip for the katana equivalent.
    /// Base clips stay for anything not overridden (jump, wall run, etc.).
    /// </summary>
    private static AnimatorOverrideController EnsureKatanaOverride(AnimatorController controller, AnimationClip runFastLoop)
    {
        var oc = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(KatanaOverridePath);
        var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
        foreach (var (baseSuffix, katanaSuffix) in KatanaMoveMap)
        {
            var baseClip = LoadClip("M_Big_Sword@" + baseSuffix, baseSuffix);
            var katanaClip = LoadClip("M_katana_Blade@" + katanaSuffix, katanaSuffix);
            if (katanaClip != null) katanaClip = EnsureClipYBake(katanaClip);
            if (baseClip != null && katanaClip != null)
                pairs.Add(new KeyValuePair<AnimationClip, AnimationClip>(baseClip, katanaClip));
        }
        // Use the authored travelling katana sprint, with the low sword-carry stance.
        var katanaRunFast = LoadClip("M_katana_Blade@Run_Fast_Root_ver_A", "Run_Fast_Root_ver_A");
        if (runFastLoop != null && katanaRunFast != null)
            pairs.Add(new KeyValuePair<AnimationClip, AnimationClip>(runFastLoop, katanaRunFast));
        // Attack states ??katana's 5-combo equivalents (katana ships no 7Combo ??        // its last two heavy hits cover Big Sword's 6th/7th swings). Non-inplace
        // takes: the relay consumes their authored root travel.
        foreach (var (baseSuffix, katanaSuffix) in KatanaAttackMap)
        {
            var baseClip = LoadClip("M_Big_Sword@" + baseSuffix, baseSuffix);
            var katanaClip = LoadClip("M_katana_Blade@" + katanaSuffix, katanaSuffix);
            if (katanaClip != null) katanaClip = EnsureAttackRootClip(katanaClip);
            if (baseClip != null && katanaClip != null)
                pairs.Add(new KeyValuePair<AnimationClip, AnimationClip>(baseClip, katanaClip));
        }
        // One-off variant states (DashAttack/JumpAttack). The dash attack moves
        // to ver_B with the rest of the stance set; the jump attack has one clip.
        // rootMotion: authored travel arcs feed the relay; the rest keep baked.
        foreach (var (baseSuffix, katanaSuffix, rootMotion) in new[]
        {
            ("Dash_Attack_ver_A", "Dash_Attack_ver_B", true),
            ("Jump_Attack_Combo_1_ZeroHeight", "Jump_Attack_Combo_1_ZeroHeight", true),
            ("Intro", "Intro", false), // WeaponDraw flourish ??each weapon's own Intro take
            ("Jump_End_ZeroHeight", "Jump_End_ZeroHeight", false), // CombatLand ??katana guard landing
            ("Damage_Die", "Damage_Die", false),       // Death ??the armed fall, per weapon
        })
        {
            var baseClip = LoadClip("M_Big_Sword@" + baseSuffix, baseSuffix);
            var katanaClip = LoadClip("M_katana_Blade@" + katanaSuffix, katanaSuffix);
            if (katanaClip != null) katanaClip = rootMotion ? EnsureAttackRootClip(katanaClip) : EnsureClipYBake(katanaClip);
            if (baseClip != null && katanaClip != null)
                pairs.Add(new KeyValuePair<AnimationClip, AnimationClip>(baseClip, katanaClip));
        }
        // Drop stale katana pairs from earlier runs: Grruzam dodge clips
        // (dodges are shared CLazy now), the jog clips the walk-first blend
        // replaced, the old 3Combo/4Combo bases, and the _Inplace 7Combo bases
        // the states moved off when attacks switched to root-motion takes.
        if (oc != null)
        {
            // Inverted pairs from an old write (a katana clip as the ORIGINAL)
            // can never match a base state — strip any pair keyed by a katana
            // clip before merging the correct base→katana ones.
            var existing = new List<KeyValuePair<AnimationClip, AnimationClip>>(oc.overridesCount);
            oc.GetOverrides(existing);
            foreach (var kv in existing)
            {
                var p = kv.Key != null ? AssetDatabase.GetAssetPath(kv.Key) : null;
                if (p != null && p.Contains("M_katana_Blade")) oc[kv.Key] = null;
            }
            foreach (var suffix in new[] { "Dodge_Front", "Dodge_Back",
                "Jogging_8Way_verA_F", "Jogging_8Way_verA_B", "Jogging_8Way_verA_L90", "Jogging_8Way_verA_R90",
                "Jogging_8Way_verA_FL45", "Jogging_8Way_verA_FR45", "Jogging_8Way_verA_BL45", "Jogging_8Way_verA_BR45",
                "Attack_3Combo_1_Inplace", "Attack_3Combo_2_Inplace", "Attack_3Combo_3_Inplace",
                "Attack_7Combo_1_Inplace", "Attack_7Combo_2_Inplace", "Attack_7Combo_3_Inplace",
                "Attack_7Combo_4_Inplace", "Attack_7Combo_5_Inplace", "Attack_7Combo_6_Inplace", "Attack_7Combo_7_Inplace",
                "UpperAttack_ZeroHeight" /* old Backstab pair ??the stab is weapon-agnostic now */ })
            {
                var stale = LoadClip("M_Big_Sword@" + suffix, suffix);
                if (stale != null) oc[stale] = null;
            }
        }

        // Regression guard: every override clip must copy the shared Grruzam
        // avatar ??an own-avatar import produces a degenerate T-pose that
        // warps the whole retarget (the lean/sink/pop locked-on bug).
        foreach (var p in pairs)
        {
            if (p.Value == null) continue;
            var clipPath = AssetDatabase.GetAssetPath(p.Value);
            if (string.IsNullOrEmpty(clipPath) || !clipPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
            var mi = AssetImporter.GetAtPath(clipPath) as ModelImporter;
            if (mi != null && mi.sourceAvatar == null)
                Debug.LogWarning($"[ProjectRestart] Override clip '{p.Value.name}' has no shared avatar ??its retarget will distort.", p.Value);
        }

        if (oc != null)
        {
            // Merge ??and overwrite: pairs whose override clip changed (ver_A ??            // ver_B moves, 5Combo ??4Combo attacks) must update in place, not
            // be skipped because the base key already exists.
            var changed = 0;
            foreach (var p in pairs)
            {
                if (p.Key == null) continue;
                if (oc[p.Key] == p.Value) continue;
                oc[p.Key] = p.Value;
                changed++;
            }
            if (changed > 0)
            {
                EditorUtility.SetDirty(oc);
                Debug.Log($"[ProjectRestart] Katana override updated {changed} clip swap(s).");
            }
            return oc;
        }

        oc = new AnimatorOverrideController(controller);
        oc.ApplyOverrides(pairs);
        Directory.CreateDirectory(CombatFolder);
        AssetDatabase.CreateAsset(oc, KatanaOverridePath);
        Debug.Log($"[ProjectRestart] Created katana override with {pairs.Count} clip swaps.");
        return oc;
    }

    private static void EnsurePlayerWiring(WeaponSet bigSword)
    {
        var scene = SceneManager.GetActiveScene();
        var player = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<PlayerLocomotion>(true))
            .FirstOrDefault();
        if (player == null)
        {
            Debug.LogWarning("[ProjectRestart] No PlayerLocomotion in the open scene ??WeaponSocket not wired. Open 00_TestBlockout and re-run.");
            return;
        }

        var socket = player.GetComponent<WeaponSocket>();
        if (socket == null)
        {
            socket = player.gameObject.AddComponent<WeaponSocket>();
            Debug.Log("[ProjectRestart] Added WeaponSocket to the player.");
        }
        if (player.GetComponent<DodgeController>() == null)
        {
            player.gameObject.AddComponent<DodgeController>();
            Debug.Log("[ProjectRestart] Added DodgeController to the player.");
        }
        if (player.GetComponent<AttackController>() == null)
        {
            player.gameObject.AddComponent<AttackController>();
            Debug.Log("[ProjectRestart] Added AttackController to the player.");
        }

        var inputs = AssetDatabase.LoadAssetAtPath<UnityEngine.InputSystem.InputActionAsset>(
            AssetDatabase.GUIDToAssetPath("052faaac586de48259a63d0c4782560b"));
        var attack = player.GetComponent<AttackController>();
        if (inputs != null && attack != null)
        {
            var attackData = new SerializedObject(attack);
            var inputProp = attackData.FindProperty("inputActions");
            if (inputProp != null && inputProp.objectReferenceValue == null)
            {
                inputProp.objectReferenceValue = inputs;
                attackData.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // Walk-first combat: sync the locomotion fallback pace while it's still
        // the old jog default ??the WeaponSet's moveSpeed is the real source.
        var locoData = new SerializedObject(player);
        var speedProp = locoData.FindProperty("lockedMoveSpeed");
        if (speedProp != null && speedProp.floatValue > 3f)
        {
            speedProp.floatValue = 2.8f;
        }
        // Transition feel — the values serialized into the scene are snappy
        // (0.05–0.1s). Push longer blends so idle↔walk↔run, swings and dodges
        // read as motion, not pose swaps. Re-running re-applies.
        void Feel(SerializedObject so, string name, float v)
        {
            var p = so.FindProperty(name);
            if (p != null && p.propertyType == SerializedPropertyType.Float) p.floatValue = v;
        }
        Feel(locoData, "speedParamDampTime", 0.18f);
        Feel(locoData, "moveParamDampTime", 0.12f);
        locoData.ApplyModifiedPropertiesWithoutUndo();
        if (attack != null)
        {
            var feelData = new SerializedObject(attack);
            Feel(feelData, "fadeOut", 0.06f);
            var streakProp = feelData.FindProperty("dashStreakFx");
            if (streakProp != null) streakProp.objectReferenceValue = FindFx("FX_Wind_Streaks_01");
            var diveProp = feelData.FindProperty("diveImpactFx");
            if (diveProp != null) diveProp.objectReferenceValue = FindFx("FX_Impact_Dirt_01");
            var artSwordProp = feelData.FindProperty("artSwordSet");
            if (artSwordProp != null) artSwordProp.objectReferenceValue = bigSword;
            var rageProp = feelData.FindProperty("rageWeaponSet");
            if (rageProp != null) rageProp.objectReferenceValue = bigSword;
            feelData.ApplyModifiedPropertiesWithoutUndo();
        }
        var dodge = player.GetComponent<DodgeController>();
        if (dodge != null)
        {
            var dodgeData = new SerializedObject(dodge);
            Feel(dodgeData, "fadeDuration", 0.12f);
            Feel(dodgeData, "endFadeDuration", 0.26f);
            dodgeData.ApplyModifiedPropertiesWithoutUndo();
        }

        // Health + AI on every scene Targetable so swings have something to
        // hit ??and it hits back (chase ??telegraph ??strike ??strafe loop).
        foreach (var t in scene.GetRootGameObjects()
                     .SelectMany(root => root.GetComponentsInChildren<Targetable>(true)))
        {
            // Bosses run their own brain (IBossEngage), not EnemyAI — a stray
            // EnemyAI drives params the boss controllers don't have (per-frame
            // SetFloat "parameter does not exist" spam).
            if (t.GetComponent<IBossEngage>() != null)
            {
                var stray = t.GetComponent<EnemyAI>();
                if (stray != null) UnityEngine.Object.DestroyImmediate(stray);
                continue;
            }
            if (t.GetComponent<Health>() == null)
            {
                t.gameObject.AddComponent<Health>();
                Debug.Log($"[ProjectRestart] Added Health to {t.name}.");
            }
            if (t.GetComponent<EnemyAI>() == null)
            {
                t.gameObject.AddComponent<EnemyAI>();
                Debug.Log($"[ProjectRestart] Added EnemyAI to {t.name}.");
            }
            // PolygonParticles hit feedback — blood splat on the victim,
            // slash burst at the player when the enemy connects.
            var hso = new SerializedObject(t.GetComponent<Health>());
            var bp = hso.FindProperty("bloodFx");
            if (bp != null) bp.objectReferenceValue = FindFx("FX_BloodSplat_Small_01");
            hso.ApplyModifiedPropertiesWithoutUndo();

        }
        if (player.GetComponent<PlayerHealth>() == null)
        {
            player.gameObject.AddComponent<PlayerHealth>();
            Debug.Log("[ProjectRestart] Added PlayerHealth to the player.");
        }
        // No bloodFx on the player — taking damage shakes the camera + cracks the
        // heart frame; a gore splat on the hero read wrong (user call).
        if (player.GetComponent<PlayerStamina>() == null)
        {
            player.gameObject.AddComponent<PlayerStamina>();
            Debug.Log("[ProjectRestart] Added PlayerStamina to the player.");
        }
        if (player.GetComponent<PlayerMana>() == null)
        {
            player.gameObject.AddComponent<PlayerMana>();
            Debug.Log("[ProjectRestart] Added PlayerMana to the player.");
        }
        if (player.GetComponent<PlayerHud>() == null)
        {
            player.gameObject.AddComponent<PlayerHud>();
            Debug.Log("[ProjectRestart] Added PlayerHud to the player.");
        }

        // Katana is the starting (and only) weapon.
        var katanaSet = AssetDatabase.LoadAssetAtPath<WeaponSet>(KatanaAssetPath);
        var so = new SerializedObject(socket);
        var prop = so.FindProperty("weaponSet");
        if (katanaSet != null && prop.objectReferenceValue != katanaSet)
        {
            prop.objectReferenceValue = katanaSet;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Souls loop: flask + spell caster on the player (runtime GameLoop
        // auto-adds them too ??wiring here makes them inspector-tunable), and
        // a bonfire beside the spawn point.
        var flask = player.GetComponent<EstusFlask>();
        if (flask == null)
        {
            flask = player.gameObject.AddComponent<EstusFlask>();
            Debug.Log("[ProjectRestart] Added EstusFlask to the player.");
        }
        var flaskData = new SerializedObject(flask);
        var bottleProp = flaskData.FindProperty("bottlePrefab");
        if (bottleProp != null && bottleProp.objectReferenceValue == null)
        {
            bottleProp.objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/ThirdParty/Synty/PolygonDungeon/Prefabs/Items/SM_Item_Potion_01.prefab");
            flaskData.ApplyModifiedPropertiesWithoutUndo();
        }
        // Weapon arts (the scene's old SpellCaster component is this same
        // script GUID, renamed ??its stale "spells" data is simply ignored).
        var caster = player.GetComponent<WeaponArtCaster>();
        if (caster == null)
        {
            caster = player.gameObject.AddComponent<WeaponArtCaster>();
            Debug.Log("[ProjectRestart] Added WeaponArtCaster to the player.");
        }
        // Audio hook bank on the player — entries stay empty until clips ship;
        // SfxBank.Ensure() would self-create an empty one anyway.
        if (player.GetComponent<SfxBank>() == null)
        {
            player.gameObject.AddComponent<SfxBank>();
            Debug.Log("[ProjectRestart] Added SfxBank to the player.");
        }
        EnsureToxicFx(); // must exist before EnsureArtAssets resolves its FX cue
        var arts = EnsureArtAssets();
        var casterData = new SerializedObject(caster);
        SyncLibrary(casterData, arts);
        // Technique loadout — the fixed-route archetype map (plan §2). These
        // slots aren't attunable, so the table is authoritative: always write
        // the default and repair stale values from older mappings rather than
        // only filling empties.
        foreach (var (slotProp, assetName) in TechniqueDefaults)
        {
            var p = casterData.FindProperty(slotProp);
            var art = arts.FirstOrDefault(a => a != null && a.name == assetName);
            if (p != null && art != null && p.objectReferenceValue != art)
                p.objectReferenceValue = art;
        }
        casterData.ApplyModifiedPropertiesWithoutUndo();
        EnsureCheckpoints(player.transform);
        EnsureWeaponPickup(player.transform);

        EditorSceneManager.MarkSceneDirty(scene);
    }

    /// <summary>Checkpoint A (lit, beside spawn ??renames the old "Bonfire")
    /// and Checkpoint B "Far Ruins" in the arena's far corner, so TRAVEL has
    /// somewhere to go.</summary>
    private static void EnsureCheckpoints(Transform player)
    {
        var all = UnityEngine.Object.FindObjectsByType<Checkpoint>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var a = all.FirstOrDefault(c => c.name == "Checkpoint A" || c.name == "Bonfire");
        if (a == null)
        {
            var go = new GameObject("Checkpoint A");
            go.transform.position = player.position + Vector3.right * 3.5f;
            a = go.AddComponent<Checkpoint>();
        }
        a.name = "Checkpoint A";
        SetCheckpointFields(a, "Arena Gate", true);

        if (all.All(c => c.name != "Checkpoint B"))
        {
            var ground = GameObject.Find("Test Ground");
            var col = ground != null ? ground.GetComponent<Collider>() : null;
            var pos = col != null
                ? new Vector3(col.bounds.max.x - 4f, col.bounds.max.y, col.bounds.max.z - 4f)
                : player.position + new Vector3(12f, 0f, 12f);
            var go = new GameObject("Checkpoint B");
            go.transform.position = pos;
            // Face back toward the arena so the rest spot is on the open side.
            var toCentre = (col != null ? col.bounds.center : player.position) - pos;
            toCentre.y = 0f;
            if (toCentre.sqrMagnitude > 0.01f) go.transform.rotation = Quaternion.LookRotation(toCentre.normalized);
            SetCheckpointFields(go.AddComponent<Checkpoint>(), "Far Ruins", false);
            Debug.Log($"[ProjectRestart] Placed Checkpoint B (Far Ruins) at {pos}.");
        }
    }

    private static void SetCheckpointFields(Checkpoint c, string displayName, bool lit)
    {
        var so = new SerializedObject(c);
        so.FindProperty("displayName").stringValue = displayName;
        so.FindProperty("litAtStart").boolValue = lit;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(c.gameObject);
    }

    // Weapon arts: Art1..8 states ??MagicKnight + Ninja humanoid takes (real
    // authored travel ??the relay consumes it; the clips keep their own avatar,
    // never the Grruzam bake). Assets in Combat/Arts/ are created once; hand
    // edits are kept, and files for arts no longer in the table are deleted.
    private const float ArtSpeed = 1.1f;
    private const string MkHuman = "MagicalKnightSet/Animation/Humanoid";
    private const string NjHuman = "NinjaAnimset/Animation/Humanoid";

    private sealed class ArtRow
    {
        public string clip;                       // fbx file base name (clip name matches)
        public string pack;                       // LoadClip pathMustContain filter
        public string name, shortName, icon = "Shard";
        public float cost, dmg = 28f, poise = 25f, range = 3f, arc = 160f, speed = ArtSpeed;
        public float stam;                      // stamina paid at commit (detail §118: launcher pays 14, no mana)
        public float shake = 0.25f, hitstop;
        public float rehit;                     // seconds between blade passes (continuous-contact moves; 0 = once per window)
        public bool burst;                      // Q+RMB ult: world-freeze + banner + flash + i-frames, then rage swap
        public string follow;                   // technique press while this art runs → the named art (chain links)
        public bool launch;                     // hits pop poise-vulnerable enemies airborne (the dedicated launcher)
        public int launchMax;                   // cap on enemies a single wave can launch (0 = uncapped)
        public bool spike;                      // hits slam an airborne enemy back down
        public bool airChase;                   // may start airborne; rises to meet a launched enemy before the smash
        public Vector2[] windows = new[] { new Vector2(0.35f, 0.58f) };
        public Vector2[] trails;                 // source-motion trail gates may include a non-damaging rise
        public float[] impacts;                 // explicit contact accents, not each window's arbitrary end
        public string loopClip, loopEndClip;      // channelled arts / flourish exits
        public float loopMax = 1.6f, manaPerSec = 12f, loopInterval = 0.35f;
        // time | prefab | attach | palette | life | shake | hitstop
        public (float t, string fx, string attach, int palette, float life, float shake, float hitstop)[] fx;
        public (float t, string id)[] sfx;
        public Proj proj;
        public bool bake;                       // Grruzam clips need the feet-Y bake; MK/Ninja keep own avatar
        public DamageKind kind = DamageKind.Normal; // numeral colour (Poison = green)
        public float bloom;                     // PostPulse intensity at the first hit window
        // detail §5 — explicit gameplay identity, not theme-derived:
        public ActionFamily family;             // default Art; M_Big_Sword pack → Technique; launcher/aerial/ult set per row
        public AimPolicy aim;                   // default MovementLed; set TargetLed/LaunchSession per row
        public ReactionProfile reaction = ReactionProfile.Sweep;
        public sealed class Proj                 // null = no projectile
        {
            public string fx; public float t = 0.45f, speed = 20f, dmg = 20f, life = 2f, spread = 12f;
            public int count = 1; public bool homing;
        }
    }

    private static readonly ArtRow[] ArtTable =
    {
        new ArtRow { // wide energy arc ??the bread-and-butter slash
            clip = "atk_energy01", pack = MkHuman, name = "Arc Blade", shortName = "ARC", icon = "ArcBlade", cost = 15f, aim = AimPolicy.TargetLed,
            dmg = 30f, hitstop = 0.05f, windows = new[] { new Vector2(0.32f, 0.55f) },
            fx = new[]
            {
                (0.05f, "FX_Wind_Streaks_01", "root", 0, 0f, 0f, 0f),
                (0.30f, "EnergyPush_FX", "handR", 2, 0f, 0.2f, 0f),
            },
            sfx = new[] { (0.28f, "art.slash") },
        },
        new ArtRow { // lunging pierce ??authored travel carries the rush
            clip = "atk_energy03", pack = MkHuman, name = "Radiant Rush", shortName = "RUSH", icon = "RadiantRush", cost = 20f,
            dmg = 34f, range = 3.5f, hitstop = 0.05f, windows = new[] { new Vector2(0.40f, 0.65f) },
            fx = new[]
            {
                (0.02f, "FX_Wind_Streaks_01", "root", 4, 0f, 0f, 0f),
                (0.38f, "EnergyPush_FX", "handR", 4, 0f, 0.25f, 0f),
            },
            sfx = new[] { (0.10f, "art.rush"), (0.38f, "art.slash") },
        },
        new ArtRow { // heavy overhead ??the punish button
            clip = "atk_overhandslash", pack = MkHuman, name = "Starfall Slam", shortName = "SLAM", icon = "StarfallSlam", cost = 28f,
            dmg = 46f, poise = 45f, arc = 120f, shake = 0.45f, hitstop = 0.1f,
            windows = new[] { new Vector2(0.45f, 0.68f) },
            fx = new[]
            {
                (0.42f, "FX_Lightning_Background_01", "root", 3, 0f, 0.4f, 0.1f),
                (0.44f, "Sparks_FX", "handR", 0, 0f, 0f, 0f),
            },
            sfx = new[] { (0.42f, "art.slam") },
        },
        new ArtRow { // channelled fire aura ??the pack ships start/loop/end
            clip = "atk_energy09_start", pack = MkHuman, name = "Solar Sigil", shortName = "SIGIL", icon = "SolarSigil", cost = 25f,
            dmg = 14f, poise = 8f, range = 3.2f, arc = 360f, speed = 1f, hitstop = 0.04f,
            loopClip = "atk_energy09_loop", loopEndClip = "atk_energy09_end",
            loopMax = 2.2f, manaPerSec = 14f, loopInterval = 0.4f,
            fx = new[]
            {
                (0.15f, "FX_Runes", "root", 3, 0f, 0f, 0f),
                (0.90f, "Fire_Circle_FX", "root", 0, 3f, 0f, 0f), // outlives into the loop
            },
            sfx = new[] { (0.10f, "art.channel") },
        },
        new ArtRow { // dagger fan ??the ranged poke
            clip = "daggerthrow_01", pack = NjHuman, name = "Phantom Daggers", shortName = "DAGR", icon = "PhantomDaggers", cost = 18f,
            dmg = 8f, poise = 5f, range = 2f, hitstop = 0.03f, windows = new[] { new Vector2(0.30f, 0.42f) },
            proj = new ArtRow.Proj { fx = "Magic_Missile_FX", t = 0.42f, count = 3, spread = 14f, dmg = 16f, homing = true },
            fx = new[] { (0.40f, "EnergyPull_FX", "handL", 5, 0f, 0f, 0f) },
            sfx = new[] { (0.40f, "art.throw") },
        },
        new ArtRow { // blink strike ??authored XZ is the teleport step
            clip = "teleport_start", pack = NjHuman, name = "Shadowstep Cut", shortName = "SHDW", icon = "ShadowstepCut", cost = 24f,
            dmg = 36f, poise = 30f, range = 2.5f, arc = 120f, speed = 1f, hitstop = 0.06f,
            windows = new[] { new Vector2(0.50f, 0.80f) },
            loopEndClip = "teleport_end",
            fx = new[]
            {
                (0.02f, "Smoke_Large_FX", "root", 5, 0f, 0f, 0f),
                (0.55f, "EnergyPull_FX", "handR", 5, 0f, 0.2f, 0f),
            },
            sfx = new[] { (0.02f, "art.teleport"), (0.50f, "art.slash") },
        },
        new ArtRow { // spinning multi-kick ??crowd control
            clip = "atk_02", pack = NjHuman, name = "Cyclone Kick", shortName = "CYCL", icon = "CycloneKick", cost = 22f,
            dmg = 18f, arc = 300f, hitstop = 0.05f, windows = new[] { new Vector2(0.22f, 0.42f), new Vector2(0.50f, 0.72f) },
            fx = new[] { (0.20f, "Magic_Swirl_FX", "root", 2, 0f, 0.15f, 0f) },
            sfx = new[] { (0.20f, "art.spin") },
        },
        new ArtRow { // palm-thrust burst ??fast stagger
            clip = "atk_04", pack = NjHuman, name = "Dragon Palm", shortName = "PALM", icon = "DragonPalm", cost = 26f,
            dmg = 40f, poise = 50f, arc = 90f, shake = 0.35f, hitstop = 0.08f,
            windows = new[] { new Vector2(0.38f, 0.60f) },
            fx = new[]
            {
                (0.36f, "EnergyPush_FX", "handR", 1, 0f, 0.3f, 0.06f),
                (0.38f, "Sparks_FX", "handR", 0, 0f, 0f, 0f),
            },
            sfx = new[] { (0.36f, "art.palm") },
        },
        new ArtRow { // toxic skull bloom — the reference's green AoE cast: full-circle
            // sweep, skull cluster pops outward, poison-green numerals, bloom spike.
            clip = "M_katana_Blade@Skill_I", name = "Toxic Chorus", shortName = "TOX", icon = "ToxicSkull", cost = 30f,
            dmg = 24f, poise = 20f, range = 4.2f, arc = 360f, speed = 1f, hitstop = 0.05f,
            windows = new[] { new Vector2(0.38f, 0.62f) },
            bake = true, kind = DamageKind.Poison, bloom = 1.6f,
            fx = new[]
            {
                (0.34f, "FX_Poison_Green_01", "root", 0, 2.2f, 0.15f, 0f),  // ground miasma outlives the burst
                (0.38f, "FX_ToxicSkulls", "root", 0, 1.5f, 0.3f, 0.08f),   // skull ring on the hit window
            },
            sfx = new[] { (0.36f, "art.channel") },
        },
        // ---- Big Sword skill arts (Art10+) — the summoned spectral blade IS a
        // big sword, so these use the pack's authored M_Big_Sword@Skill_* takes;
        // the clip's own arcs match the manifested weapon exactly. bake = feet-Y
        // like the katana art; useRootMotion keeps any authored travel.
        new ArtRow { // pommel-down overhead execution — the punish icon
            clip = "M_Big_Sword@Skill_A", pack = "M_Big_Sword", name = "Grave Rend", shortName = "REND", icon = "GraveRend", cost = 16f,
            dmg = 48f, poise = 50f, arc = 120f, range = 3.2f, shake = 0.45f, hitstop = 0.1f, bloom = 0.8f,
            windows = new[] { new Vector2(0.370f, 0.450f) }, trails = new[] { new Vector2(0.348f, 0.450f) }, impacts = new[] { 0.413f }, bake = true,
            fx = new[]
            {
                (0.40f, "FX_SwordSlash_01", "handR", 0, 0f, 0f, 0f),
                (0.50f, "FX_Impact_Dirt_01", "root", 0, 0f, 0.35f, 0.06f),
            },
            sfx = new[] { (0.413f, "art.slam") },
        },
        new ArtRow { // wide authored acrobatic cleave — the dodge-counter; links into Molten Arc
            clip = "M_Big_Sword@Skill_F", pack = "M_Big_Sword", name = "Iron Gale", shortName = "GALE", icon = "IronGale", cost = 14f, follow = "Molten Arc",
            dmg = 36f, poise = 32f, arc = 200f, range = 3.4f, shake = 0.3f,
            windows = new[] { new Vector2(0.300f, 0.370f) }, trails = new[] { new Vector2(0.273f, 0.370f) }, impacts = System.Array.Empty<float>(), bake = true,
            fx = new[]
            {
                (0.25f, "FX_Wind_Streaks_01", "root", 0, 0f, 0f, 0f),
                (0.38f, "FX_SwordSlash_01", "handR", 0, 0f, 0.15f, 0f),
            },
            sfx = new[] { (0.300f, "art.slash") },
        },
        new ArtRow { // rising moon-arc into a slam — THE LAUNCHER (in-place
            // variant so the leap can't carry the capsule off a ledge; the
            // rising arc pops poise-vulnerable enemies into the air-chase window)
            clip = "M_Big_Sword@Skill_J_Inplace", pack = "M_Big_Sword", name = "Mooncleaver", shortName = "MOON", icon = "Mooncleaver", cost = 15f,
            dmg = 44f, poise = 42f, arc = 160f, range = 3f, shake = 0.4f, hitstop = 0.08f, bloom = 0.8f,
            windows = new[] { new Vector2(0.400f, 0.447f) }, trails = new[] { new Vector2(0.154f, 0.231f), new Vector2(0.385f, 0.447f) }, impacts = new[] { 0.431f }, bake = true,
            fx = new[]
            {
                (0.40f, "FX_Lightning_Background_01", "root", 0, 0f, 0.3f, 0.06f),
                (0.58f, "FX_Impact_Dirt_01", "root", 0, 0f, 0.3f, 0.06f),
            },
            sfx = new[] { (0.431f, "art.slash") },
        },
        // ---- the rest of the M_Big_Sword skill set (Art13+) — icons reuse the
        // three sword silhouettes by motion family; durations come from the clips.
        new ArtRow { // broad cleave — links out of Tide Splitter
            clip = "M_Big_Sword@Skill_B", pack = "M_Big_Sword", name = "Ashen Cleave", shortName = "CLV", icon = "IronGale", cost = 13f, follow = "Stonebreaker",
            dmg = 38f, poise = 34f, arc = 200f, range = 3.4f, shake = 0.3f,
            windows = new[] { new Vector2(0.205f, 0.325f) }, trails = new[] { new Vector2(0.182f, 0.325f) }, impacts = System.Array.Empty<float>(), bake = true,
            fx = new[] { (0.34f, "FX_SwordSlash_01", "handR", 0, 0f, 0.15f, 0f) },
            sfx = new[] { (0.205f, "art.slash") },
        },
        new ArtRow { // red-edge heavy cut — finisher; links into Bonesunder
            clip = "M_Big_Sword@Skill_C", pack = "M_Big_Sword", name = "Red Reaver", shortName = "REAV", icon = "GraveRend", cost = 16f, follow = "Bonesunder",
            dmg = 46f, poise = 48f, arc = 130f, range = 3.1f, shake = 0.4f, hitstop = 0.08f,
            windows = new[] { new Vector2(0.429f, 0.472f) }, trails = new[] { new Vector2(0.400f, 0.472f) }, impacts = new[] { 0.457f }, bake = true,
            fx = new[] { (0.40f, "FX_SwordSlash_01", "handR", 0, 0f, 0.2f, 0f), (0.50f, "FX_Impact_Dirt_01", "root", 0, 0f, 0.3f, 0.05f) },
            sfx = new[] { (0.457f, "art.slam") },
        },
        new ArtRow { // ground-splitting slam
            clip = "M_Big_Sword@Skill_D", pack = "M_Big_Sword", name = "Stonebreaker", shortName = "STON", icon = "GraveRend", cost = 17f,
            dmg = 50f, poise = 55f, arc = 110f, range = 3f, shake = 0.5f, hitstop = 0.12f, bloom = 0.8f,
            windows = new[] { new Vector2(0.405f, 0.487f) }, trails = new[] { new Vector2(0.381f, 0.487f) }, impacts = new[] { 0.476f }, bake = true,
            fx = new[] { (0.52f, "FX_Impact_Dirt_01", "root", 0, 0f, 0.4f, 0.08f) },
            sfx = new[] { (0.476f, "art.slam") },
        },
        new ArtRow { // continuous authored crowd spin — hits once per blade pass,
            // damage/poise split across the passes so a full connection ≈ one old hit
            clip = "M_Big_Sword@Skill_E", pack = "M_Big_Sword", name = "Tide Splitter", shortName = "TIDE", icon = "IronGale", cost = 13f, follow = "Ashen Cleave",
            dmg = 10f, poise = 9f, arc = 240f, range = 3.6f, shake = 0.25f, rehit = 0.15f,
            windows = new[] { new Vector2(0.145f, 0.655f) }, trails = new[] { new Vector2(0.145f, 0.673f) }, impacts = System.Array.Empty<float>(), bake = true,
            fx = new[] { (0.28f, "FX_Wind_Streaks_01", "root", 0, 0f, 0f, 0f), (0.38f, "FX_SwordSlash_01", "handR", 0, 0f, 0.15f, 0f) },
            sfx = new[] { (0.145f, "art.slash") },
        },
        new ArtRow { // wolf-combo opening cut — links G_1 → G_2 → G_ALL
            clip = "M_Big_Sword@Skill_G_1", pack = "M_Big_Sword", name = "Grave Wolf I", shortName = "WLF1", icon = "Mooncleaver", cost = 11f, follow = "Grave Wolf II",
            dmg = 26f, poise = 24f, arc = 160f, range = 3f, shake = 0.2f,
            windows = new[] { new Vector2(0.250f, 0.334f) }, trails = new[] { new Vector2(0.229f, 0.354f) }, impacts = System.Array.Empty<float>(), bake = true,
            fx = new[] { (0.28f, "FX_SwordSlash_01", "handR", 0, 0f, 0f, 0f) },
            sfx = new[] { (0.250f, "art.slash") },
        },
        new ArtRow { // wolf-combo follow-up
            clip = "M_Big_Sword@Skill_G_2", pack = "M_Big_Sword", name = "Grave Wolf II", shortName = "WLF2", icon = "Mooncleaver", cost = 12f, follow = "Grave Rend",
            dmg = 28f, poise = 26f, arc = 160f, range = 3f, shake = 0.25f,
            windows = new[] { new Vector2(0.300f, 0.365f) }, trails = new[] { new Vector2(0.280f, 0.365f) }, impacts = System.Array.Empty<float>(), bake = true,
            fx = new[] { (0.30f, "FX_SwordSlash_01", "handR", 0, 0f, 0.15f, 0f) },
            sfx = new[] { (0.300f, "art.slash") },
        },
        new ArtRow { // the full wolf chain — longest commit, biggest payout
            clip = "M_Big_Sword@Skill_G_ALL", pack = "M_Big_Sword", name = "Grave Wolf", shortName = "WLF", icon = "Mooncleaver", cost = 22f,
            dmg = 55f, poise = 60f, arc = 200f, range = 3.2f, shake = 0.5f, hitstop = 0.12f, bloom = 0.9f,
            windows = new[] { new Vector2(0.167f, 0.225f), new Vector2(0.500f, 0.542f) }, trails = new[] { new Vector2(0.153f, 0.236f), new Vector2(0.486f, 0.542f) }, impacts = new[] { 0.528f }, bake = true,
            fx = new[] { (0.30f, "FX_SwordSlash_01", "handR", 0, 0f, 0.15f, 0f), (0.60f, "FX_SwordSlash_01", "handR", 0, 0f, 0.3f, 0.08f) },
            sfx = new[] { (0.167f, "art.slash"), (0.528f, "art.slam") },
        },
        new ArtRow { // molten sweeping arc
            clip = "M_Big_Sword@Skill_H", pack = "M_Big_Sword", name = "Molten Arc", shortName = "MOLT", icon = "IronGale", cost = 14f,
            dmg = 40f, poise = 36f, arc = 180f, range = 3.4f, shake = 0.3f,
            windows = new[] { new Vector2(0.340f, 0.405f) }, trails = new[] { new Vector2(0.319f, 0.426f) }, impacts = System.Array.Empty<float>(), bake = true,
            fx = new[] { (0.36f, "FX_SwordSlash_01", "handR", 0, 0f, 0.2f, 0f), (0.40f, "FX_Wind_Streaks_01", "root", 0, 0f, 0f, 0f) },
            sfx = new[] { (0.340f, "art.slash") },
        },
        new ArtRow { // falling-edge smash — the air chase: rises to a launched
            // enemy, then the hit spikes it back down into a hard landing
            clip = "M_Big_Sword@Skill_I", pack = "M_Big_Sword", name = "Skyfall Edge", shortName = "SKYF", icon = "GraveRend", cost = 15f, airChase = true, spike = true,
            family = ActionFamily.Aerial, aim = AimPolicy.LaunchSession,
            dmg = 45f, poise = 44f, arc = 140f, range = 3f, shake = 0.4f, hitstop = 0.08f,
            windows = new[] { new Vector2(0.457f, 0.544f) }, trails = new[] { new Vector2(0.435f, 0.544f) }, impacts = new[] { 0.522f }, bake = true,
            fx = new[] { (0.46f, "FX_Impact_Dirt_01", "root", 0, 0f, 0.35f, 0.06f) },
            sfx = new[] { (0.522f, "art.slam") },
        },
        new ArtRow { // mooncleaver WITH authored travel — the rush version carries
            // the leap forward (J_Inplace above is the stationary twin)
            clip = "M_Big_Sword@Skill_J", pack = "M_Big_Sword", name = "Mooncleaver Rush", shortName = "MRSH", icon = "Mooncleaver", cost = 15f,
            dmg = 44f, poise = 40f, arc = 160f, range = 3.4f, shake = 0.4f, hitstop = 0.08f, bloom = 0.8f,
            windows = new[] { new Vector2(0.400f, 0.447f) }, trails = new[] { new Vector2(0.154f, 0.231f), new Vector2(0.385f, 0.447f) }, impacts = new[] { 0.431f }, bake = true,
            fx = new[] { (0.40f, "FX_Lightning_Background_01", "root", 0, 0f, 0.3f, 0.06f), (0.58f, "FX_Impact_Dirt_01", "root", 0, 0f, 0.3f, 0.06f) },
            sfx = new[] { (0.431f, "art.slash") },
        },
        new ArtRow { // focused horizontal strike (verified source trajectory; no floor impact)
            clip = "M_Big_Sword@Skill_K", pack = "M_Big_Sword", name = "Bonesunder", shortName = "BONE", icon = "GraveRend", cost = 16f,
            dmg = 47f, poise = 52f, arc = 120f, range = 3f, shake = 0.45f, hitstop = 0.1f,
            windows = new[] { new Vector2(0.193f, 0.242f) }, trails = new[] { new Vector2(0.183f, 0.250f) }, impacts = System.Array.Empty<float>(), bake = true,
            fx = new[] { (0.44f, "FX_SwordSlash_01", "handR", 0, 0f, 0.2f, 0f), (0.48f, "FX_Impact_Dirt_01", "root", 0, 0f, 0.3f, 0.05f) },
            sfx = new[] { (0.217f, "art.slam") },
        },
        new ArtRow { // the finale — widest arc, brightest bloom; the Q+RMB ult,
            // so it's priced like a burst (~a full mana bar on cast)
            clip = "M_Big_Sword@Skill_L", pack = "M_Big_Sword", name = "Last Eclipse", shortName = "ECLP", icon = "Mooncleaver", cost = 56f,
            family = ActionFamily.Ultimate, aim = AimPolicy.TargetLed, reaction = ReactionProfile.Finisher,
            dmg = 52f, poise = 50f, arc = 220f, range = 3.4f, shake = 0.5f, hitstop = 0.1f, bloom = 1.0f,
            windows = new[] { new Vector2(0.385f, 0.505f) }, trails = new[] { new Vector2(0.370f, 0.523f) }, impacts = new[] { 0.492f }, bake = true, burst = true,
            // The violet crackle at 0.02 plays inside the world freeze — energy
            // suspended mid-spawn until time snaps back with the blade.
            // The compact dark-crimson eruption at 0.44 sits inside the real
            // contact window (0.385–0.505) — detail §203.
            fx = new[] { (0.02f, "FX_Lightning_Background_01", "root", 5, 0.7f, 0.1f, 0f), (0.38f, "FX_Lightning_Background_01", "root", 0, 0f, 0.3f, 0.06f), (0.44f, "FX_Explosion_Large_Dark_01", "root", 0, 0.8f, 0.15f, 0f), (0.42f, "FX_Wind_Streaks_01", "root", 0, 0f, 0f, 0f) },
            sfx = new[] { (0.492f, "art.slam") },
        },
        new ArtRow { // THE LAUNCHER — the ZeroHeight take on purpose (detail
            // §116): the aerial session owns capsule Y outright, so the clip
            // must not also carry authored rise. Two normals → RMB fires it;
            // ≤3 poise-vulnerable launch.
            clip = "M_Big_Sword@UpperAttack_ZeroHeight", pack = "M_Big_Sword", name = "Upper Attack", shortName = "UPPR", icon = "Mooncleaver", cost = 0f, stam = 14f,
            family = ActionFamily.Launcher, aim = AimPolicy.TargetLed,
            dmg = 24f, poise = 30f, arc = 120f, range = 2.6f, shake = 0.4f, hitstop = 0.07f, bloom = 0.6f,
            launch = true, launchMax = 3, follow = "Skyfall Edge",
            windows = new[] { new Vector2(0.340f, 0.480f) }, trails = new[] { new Vector2(0.300f, 0.500f) }, impacts = new[] { 0.42f }, bake = true,
            fx = new[] { (0.30f, "FX_SwordSlash_01", "handR", 0, 0.35f, 0f, 0f), (0.42f, "FX_Impact_Dirt_01", "root", 0, 0.6f, 0.2f, 0.05f) },
            sfx = new[] { (0.40f, "art.slash") },
        },
    };

    private static void EnsureArtStates(AnimatorStateMachine sm)
    {
        var wanted = new HashSet<string>();
        for (var i = 0; i < ArtTable.Length; i++)
        {
            var row = ArtTable[i];
            var state = "Art" + (i + 1);
            wanted.Add(state);
            // MK/Ninja clips are already XZ-unbaked humanoid takes on their own
            // avatar ??no root-config pass (EnsureAttackRootClip would graft the
            // Grruzam avatar onto them).
            var clip = LoadClip(row.clip, row.clip, row.pack);
            EnsureNamedState(sm, state, row.bake ? EnsureAttackRootClip(clip) : clip, CrowdSpeed(row));
            if (row.loopClip != null)
            {
                wanted.Add(state + "Loop");
                EnsureNamedState(sm, state + "Loop", EnsureLoopClip(LoadClip(row.loopClip, row.loopClip, row.pack)), 1f);
            }
            if (row.loopEndClip != null)
            {
                wanted.Add(state + "End");
                EnsureNamedState(sm, state + "End", LoadClip(row.loopEndClip, row.loopEndClip, row.pack), 1f);
            }
        }

    }

    /// <summary>loopTime=1 so a channelled loop take actually loops.</summary>
    private static AnimationClip EnsureLoopClip(AnimationClip clip)
    {
        if (clip == null) return null;
        var path = AssetDatabase.GetAssetPath(clip);
        if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || !SetMetaFlag(path, "loopTime", 1))
            return clip;
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
            .FirstOrDefault(c => c.name == clip.name);
    }

    /// <summary>The three deliberate aerial strikes (detail §134-138):
    /// Jump_Attack_Combo_1/2/3 ZeroHeight takes — the session owns capsule Y,
    /// so no authored rise is wanted as root motion. Speed is fit to the
    /// session's per-step duration so each strike fills its window.</summary>
    private static void EnsureAerialStates(AnimatorStateMachine sm)
    {
        var durations = new[] { 0.42f, 0.42f, 0.44f }; // AttackController.AirSteps
        for (var i = 0; i < 3; i++)
        {
            var suffix = $"Jump_Attack_Combo_{i + 1}_ZeroHeight";
            var clip = LoadClip("M_Big_Sword@" + suffix, suffix, "M_Big_Sword");
            if (clip == null)
            {
                Debug.LogError($"[ProjectRestart] Missing clip M_Big_Sword@{suffix} — AirStrike{i + 1} not built.");
                continue;
            }
            clip = EnsureAttackRootClip(clip);
            EnsureNamedState(sm, $"AirStrike{i + 1}", clip, clip.length / durations[i]);
        }
    }

    /// <summary>The moving-drink layer (detail §drinks): an upper-body masked
    /// Override layer at default weight 0 with its own Potion/PotionEmpty
    /// states — the flask raises the weight during a sip while the base layer
    /// keeps driving the legs (half-speed walk stays visible). Idempotent.</summary>
    private static void EnsureUpperBodyLayer(AnimatorController controller)
    {
        var mask = EnsureUpperBodyMask();
        var layers = controller.layers;
        var idx = -1;
        for (var i = 0; i < layers.Length; i++)
            if (layers[i].name == "UpperBody") { idx = i; break; }
        if (idx < 0)
        {
            controller.AddLayer(new AnimatorControllerLayer
            {
                name = "UpperBody",
                avatarMask = mask,
                defaultWeight = 0f,
                blendingMode = AnimatorLayerBlendingMode.Override,
                stateMachine = new AnimatorStateMachine { name = "UpperBody" },
            });
            idx = controller.layers.Length - 1;
            layers = controller.layers;
        }
        else if (layers[idx].avatarMask != mask || layers[idx].defaultWeight != 0f
                 || layers[idx].blendingMode != AnimatorLayerBlendingMode.Override)
        {
            layers[idx].avatarMask = mask;
            layers[idx].defaultWeight = 0f;
            layers[idx].blendingMode = AnimatorLayerBlendingMode.Override;
            controller.layers = layers;
        }
        var sm = layers[idx].stateMachine;
        const string se = "SoulslikeEssential";
        EnsureNamedState(sm, "Potion", LoadClip("Potion_Drink", "Potion_Drink", se), 1f);
        EnsureNamedState(sm, "PotionEmpty", LoadClip("Potion_Empty", "Potion_Empty", se), 1f);
        // Default state: an empty "None" so the layer contributes nothing while
        // its weight sits at zero between sips.
        if (sm.defaultState == null || sm.defaultState.name != "None")
        {
            var none = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "None") ?? sm.AddState("None");
            none.motion = null;
            sm.defaultState = none;
        }
    }

    /// <summary>Upper-body humanoid mask asset — body/head/arms/fingers in,
    /// root + legs + foot IK out — so locomotion keeps owning the lower half.</summary>
    private static AvatarMask EnsureUpperBodyMask()
    {
        var path = CombatFolder + "/UpperBody.mask";
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
        if (mask == null)
        {
            mask = new AvatarMask();
            AssetDatabase.CreateAsset(mask, path);
        }
        var on = new[]
        {
            AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head,
            AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm,
            AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers,
        };
        var off = new[]
        {
            AvatarMaskBodyPart.Root, AvatarMaskBodyPart.LeftLeg, AvatarMaskBodyPart.RightLeg,
            AvatarMaskBodyPart.LeftFootIK, AvatarMaskBodyPart.RightFootIK,
        };
        foreach (var p in on) mask.SetHumanoidBodyPartActive(p, true);
        foreach (var p in off) mask.SetHumanoidBodyPartActive(p, false);
        mask.transformCount = 0;
        EditorUtility.SetDirty(mask);
        return mask;
    }

    /// <summary>FX prefab by base name ??searches the whole ThirdParty tree.</summary>
    internal static GameObject FindFx(string name)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab " + name))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == name)
                return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        Debug.LogWarning($"[ProjectRestart] FX prefab '{name}' not found ??cue will be empty.");
        return null;
    }

    private static WeaponArt[] EnsureArtAssets()
    {
        var folder = CombatFolder + "/Arts";
        Directory.CreateDirectory(folder);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<WeaponArt>();
        for (var i = 0; i < ArtTable.Length; i++)
        {
            var row = ArtTable[i];
            var state = "Art" + (i + 1);
            var path = $"{folder}/{row.name.Replace(" ", "")}.asset";
            keep.Add(path);
            var art = AssetDatabase.LoadAssetAtPath<WeaponArt>(path);
            if (art == null)
            {
                var clip = LoadClip(row.clip, row.clip, row.pack);
                art = ScriptableObject.CreateInstance<WeaponArt>();
                art.artName = row.name;
                art.shortName = row.shortName;
                art.icon = row.icon;
                art.stateName = state;
                art.manaCost = row.cost;
                art.duration = clip != null ? clip.length / row.speed : 1.4f;
                art.hitWindows = row.windows;
                art.damagePerHit = row.dmg;
                art.poiseDamage = row.poise;
                art.range = row.range;
                art.arc = row.arc;
                art.useRootMotion = true;
                art.shake = row.shake;
                art.hitstop = row.hitstop;
                if (row.loopClip != null) art.loopState = state + "Loop";
                if (row.loopEndClip != null)
                {
                    art.loopEndState = state + "End";
                    var endClip = LoadClip(row.loopEndClip, row.loopEndClip, row.pack);
                    if (endClip != null) art.loopEndDuration = Mathf.Max(0.3f, endClip.length);
                }
                art.loopMaxTime = row.loopMax;
                art.manaPerSecond = row.manaPerSec;
                art.loopHitInterval = row.loopInterval;
                art.damageKind = row.kind;
                art.bloomPunch = row.bloom;
                if (row.fx != null)
                    art.fxCues = row.fx.Select(f => new FxCue
                    {
                        time = f.t, prefab = FindFx(f.fx), attach = f.attach,
                        palette = f.palette, life = f.life, shake = f.shake, hitstop = f.hitstop,
                    }).ToArray();
                if (row.sfx != null)
                    art.sfxCues = row.sfx.Select(s => new SfxCue { time = s.t, id = s.id }).ToArray();
                if (row.proj != null)
                    art.projectile = new ProjectileSpec
                    {
                        prefab = FindFx(row.proj.fx), spawnTime = row.proj.t, speed = row.proj.speed,
                        damage = row.proj.dmg, life = row.proj.life, count = row.proj.count,
                        spreadDeg = row.proj.spread, homing = row.proj.homing,
                    };
                AssetDatabase.CreateAsset(art, path);
                Debug.Log($"[ProjectRestart] Created weapon art {path} ({row.clip}, {art.duration:F2}s).");
            }
            TuneCrowdArt(art, row);
            // stateName is positional (Art = index+1): a stale or hand-dragged
            // asset pointing at another index would play the wrong clip —
            // re-pin it to the table on every run.
            if (art.stateName != state) { art.stateName = state; EditorUtility.SetDirty(art); }
            list.Add(art);
        }
        // Technique chains: row.follow names the art fired when the technique
        // input lands while this one runs (wolf combo, spin → cleave → slam).
        for (var i = 0; i < ArtTable.Length && i < list.Count; i++)
        {
            var next = ArtTable[i].follow != null
                ? list.FirstOrDefault(a => a != null && a.artName == ArtTable[i].follow) : null;
            if (list[i].followUp != next) { list[i].followUp = next; EditorUtility.SetDirty(list[i]); }
        }
        foreach (var guid in AssetDatabase.FindAssets("t:WeaponArt", new[] { folder }))
        {
            var extra = AssetDatabase.LoadAssetAtPath<WeaponArt>(AssetDatabase.GUIDToAssetPath(guid));
            if (extra != null && !list.Contains(extra)) list.Add(extra);
        }
        return list.ToArray();
    }

    /// <summary>The right-click archetype map — every A–L big-sword skill lives
    /// here, not in the Q library. Context order is the caster's pick ladder.
    /// Values match the art's FILE name (folder asset names have no spaces).</summary>
    private static readonly (string prop, string asset)[] TechniqueDefaults =
    {
        ("techniqueNeutral", "TideSplitter"),     // E — crowd spin → Ashen Cleave → Stonebreaker
        ("techniqueEarly", "GraveWolfI"),         // G_1 → G_2 → Grave Rend (1st normal → RMB)
        ("techniqueFinisher", "RedReaver"),       // C → Bonesunder (3rd+ normal → RMB)
        ("techniqueAir", "SkyfallEdge"),          // I — session modifier+RMB spike
        ("techniqueDodge", "IronGale"),           // F — dodge counter → Molten Arc
        ("techniqueDodgeMod", "Mooncleaver"),     // J_Inplace — dodge + modifier + RMB
        ("techniqueMod", "GraveWolf"),            // G_ALL — modifier + RMB full chain
        ("techniqueLauncher", "UpperAttack"),     // UpperAttack — the pop-up (2 normals → RMB)
        ("techniqueSprint", "MooncleaverRush"),   // J — sprint ≥0.6s + attack: the dash strike
        ("ultimate", "LastEclipse"),              // L — dedicated T / D-pad up → rage
    };

    /// <summary>The equipped big sword carries its own feel layer on plain
    /// swings — dark ribbon, fire edge, crimson impact and a whoosh — so the
    /// weapon never reads dry between arts. FX prefabs come from the crimson
    /// crowd variants; missing prefabs just leave the slot empty.</summary>
    private static void TuneWeaponFeel(WeaponSet set)
    {
        if (set == null) return;
        set.swingRibbon = true;
        var edge = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/FX/Crowd/FX_CrimsonFireEdge.prefab");
        var impact = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/FX/Crowd/FX_CrimsonImpact.prefab");
        if (edge != null) set.edgeFx = edge;
        if (impact != null) set.contactFx = impact;
        set.swingSfx = "art.slash";
        EditorUtility.SetDirty(set);
    }

    /// <summary>Syncs the Q library with the generated arts: big-sword skills
    /// are technique-only (removed if a previous run added them), the rest
    /// append without reordering existing entries.</summary>
    private static void SyncLibrary(SerializedObject casterSo, WeaponArt[] arts)
    {
        var lib = casterSo.FindProperty("library");
        if (lib == null) return;
        var slots = casterSo.FindProperty("slots");
        var slotArts = casterSo.FindProperty("slotArts");
        // Snapshot attunement identity before the reorder — removing a library
        // entry shifts every index after it, so resolve slots by asset, not
        // by position (detail §256 save/attunement migration).
        var held = new WeaponArt[slots != null ? slots.arraySize : 0];
        for (var i = 0; i < held.Length; i++)
        {
            var fromRef = slotArts != null && i < slotArts.arraySize
                ? slotArts.GetArrayElementAtIndex(i).objectReferenceValue as WeaponArt : null;
            var idx = slots.GetArrayElementAtIndex(i).intValue;
            held[i] = fromRef != null ? fromRef
                : idx >= 0 && idx < lib.arraySize ? lib.GetArrayElementAtIndex(idx).objectReferenceValue as WeaponArt
                : null;
        }
        for (var i = lib.arraySize - 1; i >= 0; i--)
            if (lib.GetArrayElementAtIndex(i).objectReferenceValue is WeaponArt a && a != null && a.BigSwordSkill)
            {
                // Object-ref elements need two deletes: first call nulls, second shrinks.
                lib.DeleteArrayElementAtIndex(i);
                lib.DeleteArrayElementAtIndex(i);
            }
        var present = new HashSet<WeaponArt>();
        for (var i = 0; i < lib.arraySize; i++) present.Add(lib.GetArrayElementAtIndex(i).objectReferenceValue as WeaponArt);
        foreach (var art in arts)
            if (art != null && !art.BigSwordSkill && !present.Contains(art))
            { lib.InsertArrayElementAtIndex(lib.arraySize); lib.GetArrayElementAtIndex(lib.arraySize - 1).objectReferenceValue = art; }
        if (slots == null) return;
        if (slotArts != null && slotArts.arraySize != slots.arraySize) slotArts.arraySize = slots.arraySize;
        for (var i = 0; i < held.Length; i++)
        {
            var idx = -1;
            for (var j = 0; j < lib.arraySize; j++)
                if (lib.GetArrayElementAtIndex(j).objectReferenceValue == held[i]) { idx = j; break; }
            if (idx < 0) idx = Mathf.Min(i, lib.arraySize - 1); // art left the library — clamp, keep a valid slot
            slots.GetArrayElementAtIndex(i).intValue = idx;
            if (slotArts != null)
                slotArts.GetArrayElementAtIndex(i).objectReferenceValue =
                    idx >= 0 ? lib.GetArrayElementAtIndex(idx).objectReferenceValue : null;
        }
    }
    // "Skill_" catches every A–L clip AND katana-kit crowd arts like Toxic
    // Chorus (M_katana_Blade@Skill_I); the pack check exists for big-sword
    // takes with no Skill_ in the filename — M_Big_Sword@UpperAttack took the
    // legacy early-return and stayed a stale, never-retuned asset.
    private static bool IsCrowd(ArtRow row) => row.bake && (row.clip.Contains("Skill_") || row.pack == "M_Big_Sword");

    private static float CrowdMultiplier(ArtRow row)
    {
        if (!IsCrowd(row)) return 1f;
        var clip = row.clip;
        if (clip.EndsWith("G_ALL") || clip.EndsWith("L")) return 1.25f;
        if (clip.EndsWith("G_1") || clip.EndsWith("G_2")) return 1.5f;
        if (clip.EndsWith("B") || clip.EndsWith("E") || clip.EndsWith("F") || clip.EndsWith("H")) return 1.6f;
        return 1.35f;
    }
    private static float CrowdSpeed(ArtRow row) => row.speed * CrowdMultiplier(row);

    private static void TuneCrowdArt(WeaponArt art, ArtRow row)
    {
        if (!IsCrowd(row) && row.name != "Arc Blade")
        {
            // Legacy arts keep their authored look — the table only owns the
            // feel numbers. They no longer summon the big sword (BigSwordSkill
            // is theme-derived), so the cast rides the equipped blade now.
            if (art.hitstop != row.hitstop) { art.hitstop = row.hitstop; EditorUtility.SetDirty(art); }
            return;
        }
        var big = row.pack == "M_Big_Sword";
        art.recoveryTransitionDelay = 0.06f;
        // DarkCrimson is the big-sword skill language — katana-kit arts riding
        // this tune path (Arc Blade keeps its red wave; Toxic Chorus's miasma)
        // stay dark only where authored so, else BigSwordSkill would summon the
        // spectral sword for an equipped-weapon art.
        art.visualTheme = big || row.name == "Arc Blade" ? ArtVisualTheme.DarkCrimson : ArtVisualTheme.Original;
        // detail §5 — the row's explicit family/aim/reaction; the M_Big_Sword
        // pack defaults to Technique, spins always take the Spin profile.
        art.family = row.family != ActionFamily.Art ? row.family
            : big ? ActionFamily.Technique : ActionFamily.Art;
        art.aim = row.aim;
        art.reaction = row.clip.EndsWith("E") ? ReactionProfile.Spin : row.reaction;
        if (row.name == "Arc Blade")
        {
            art.manaCost = 15f; art.cooldown = 1.6f; art.damagePerHit = 30f; art.poiseDamage = 25f;
            art.contactMode = ArtContactMode.ProjectileOnly;
            art.projectile = new ProjectileSpec { wave = true, waveSize = new Vector2(2.4f, 1.6f), maxTravel = 12f,
                speed = 18f, life = 12f / 18f + 0.1f, damage = 30f, pierce = true, spawnTime = 0.42f };
            art.fxCues = System.Array.Empty<FxCue>();
        }
        else
        {
            var mult = CrowdMultiplier(row);
            art.manaCost = Mathf.Ceil(row.cost * 0.75f);
            // Spec-owned gameplay numbers retune every run — the table is the
            // authority (detail §118), unlike the hand-reviewed windows above.
            art.damagePerHit = row.dmg;
            art.poiseDamage = row.poise;
            art.range = row.range;
            art.arc = row.arc;
            art.staminaCost = row.stam;
            art.cooldown = mult == 1.25f ? 2.2f : mult == 1.35f ? 1.8f : 1.2f;
            var clip = LoadClip(row.clip, row.clip, row.pack);
            if (clip != null) art.duration = clip.length / CrowdSpeed(row);
            art.contactMode = row.clip.EndsWith("E") ? ArtContactMode.Spin : ArtContactMode.Frontal;
            art.rehitInterval = row.rehit;
            // Enabled flag is table-driven; the spec's pose/timing numbers are
            // hand-tuned per asset — don't stomp them on a rerun.
            if (art.burst == null) art.burst = new BurstSpec();
            art.burst.enabled = row.burst;
            // Hitstop bands (detail §208): quick 0.04 / heavy 0.07 / final 0.10 —
            // the row's authored value picks its band, bounded by the table.
            art.hitstop = row.hitstop > 0.08f ? 0.10f : row.hitstop > 0.05f ? 0.07f : 0.04f;
            art.shake = row.shake;
            // Motion-derived defaults replace the old uniform table fractions;
            // explicit visual-review edits remain authoritative on a setup rerun.
            if (!art.contactMotionReviewed)
            {
                art.hitWindows = row.windows.ToArray();
                art.trailWindows = (row.trails ?? row.windows).ToArray();
            }
            else if (art.trailWindows == null || art.trailWindows.Length == 0)
                art.trailWindows = art.hitWindows.ToArray();
            if (row.sfx != null)
                art.sfxCues = row.sfx.Select(s => new SfxCue { time = s.t, id = s.id }).ToArray();
            // Clips that carry real authored root-Y arcs: the aerial movers
            // (I/J/J_Inplace/UpperAttack) and the chain-ender slams
            // (D=Stonebreaker, K=Bonesunder) — a slam whose descent is dropped
            // finishes floating mid-pose.
            art.rootMotionY = row.clip.EndsWith("I") || row.clip.EndsWith("J") || row.clip.EndsWith("J_Inplace")
                || row.clip.EndsWith("UpperAttack") || row.clip.EndsWith("D") || row.clip.EndsWith("K");
            art.ribbonLife = row.clip.EndsWith("L") ? 0.14f : 0.10f;
            // Big-sword skills read huge: the spectral blade spawns oversized.
            // Launch/spike/air-chase flags route through the sweep in combat.
            art.summonScale = big ? 1.45f : 1f;
            art.launch = row.launch;
            art.launchMax = row.launchMax;
            art.spike = row.spike;
            art.airChase = row.airChase;
            art.bladeEdgeFx = EnsureCrimsonVariant(row.clip.EndsWith("H") ? "FireEdge" : "Edge", "FX_Trail_Fire_01", row.clip.EndsWith("H") ? 0.35f : 0.18f);
            art.contactFx = EnsureCrimsonVariant("Impact", "FX_Sparks_01", 0.5f);
            var smoke = EnsureCrimsonVariant("Smoke", "FX_Smoke_Black_Small_01", 0.18f);
            var impactTimes = !art.contactMotionReviewed && row.impacts != null
                ? row.impacts
                : mult == 1.35f || mult == 1.25f ? art.hitWindows.Select(w => w.y).ToArray() : System.Array.Empty<float>();
            art.fxCues = impactTimes.Select(t => new FxCue { time = t, attach = "floor",
                prefab = EnsureCrimsonVariant("Scar", "FX_Impact_Dirt_01", 0.65f), life = 0.6f, scale = big ? 0.9f : 0.65f }).ToArray();
            art.fxCues = art.fxCues.Concat(art.trailWindows.Select(w => new FxCue { time = w.x, prefab = smoke,
                attach = "handR", follow = true, life = Mathf.Max(0.1f, (w.y - w.x) * art.duration), scale = big ? 0.7f : 0.5f })).ToArray();
            // Authored row.fx cues ride along — the wholesale rebuild above used
            // to stomp them (Last Eclipse's lightning/eruption never survived a
            // rerun). Resolve the vendor prefab fresh so they stay real cues.
            if (row.fx != null)
                art.fxCues = art.fxCues.Concat(row.fx.Select(f => new FxCue
                {
                    time = f.t, prefab = FindFx(f.fx), attach = f.attach,
                    palette = f.palette, life = f.life, shake = f.shake, hitstop = f.hitstop,
                })).ToArray();
        }
        EditorUtility.SetDirty(art);
    }

    private static GameObject EnsureCrimsonVariant(string suffix, string vendor, float scale)
    {
        const string folder = "Assets/_Project/FX/Crowd";
        if (!AssetDatabase.IsValidFolder("Assets/_Project/FX")) AssetDatabase.CreateFolder("Assets/_Project", "FX");
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/_Project/FX", "Crowd");
        var path = folder + "/FX_Crimson" + suffix + ".prefab";
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var source = FindFx(vendor);
        if (existing == null && source == null) return null;
        var instance = existing != null ? PrefabUtility.LoadPrefabContents(path) : UnityEngine.Object.Instantiate(source);
        try
        {
            instance.name = "FX_Crimson" + suffix;
            if (existing == null) instance.transform.localScale *= scale;
            if (existing == null) foreach (var ps in instance.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main; main.startColor = suffix == "Smoke" ? new Color(0.025f, 0.008f, 0.012f, 0.25f) : suffix == "Impact" ? new Color(1f, 0.8f, 0.72f, 0.9f) : new Color(0.8f, 0.025f, 0.055f, 0.8f);
                main.loop = false;
                var emission = ps.emission; emission.rateOverTimeMultiplier *= 0.3f;
                var bursts = new ParticleSystem.Burst[emission.burstCount];
                emission.GetBursts(bursts);
                for (var i = 0; i < bursts.Length; i++)
                    bursts[i].count = new ParticleSystem.MinMaxCurve(Mathf.Clamp(Mathf.Ceil(bursts[i].count.constantMax * 0.25f), 1f, 12f));
                emission.SetBursts(bursts);
            }
            // Re-run repair on EXISTING variants too. Vendor fire prefabs carry
            // missing material references; null main/trail slots are not valid FX.
            ProjectRestartPlunge.RepairOwnedParticles(instance, suffix, source);
            return PrefabUtility.SaveAsPrefabAsset(instance, path);
        }
        finally
        {
            if (existing != null) PrefabUtility.UnloadPrefabContents(instance);
            else UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    /// <summary>FX_ToxicSkulls.prefab — the toxic art's skull-cluster burst: a ring
    /// of Synty skull props animated by SkullBurst, over FX_Poison_Green_01 ground
    /// miasma. Built once; the art's fx cue references it by name via FindFx.</summary>
    private static void EnsureToxicFx()
    {
        const string path = "Assets/_Project/FX/FX_ToxicSkulls.prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return;
        Directory.CreateDirectory("Assets/_Project/FX");

        var root = new GameObject("FX_ToxicSkulls");
        try
        {
            root.AddComponent<SkullBurst>();
            var skulls = new GameObject("Skulls");
            skulls.transform.SetParent(root.transform, false);
            for (var i = 0; i < 7; i++)
            {
                var prop = FindFx("SM_Prop_Skull_0" + (i % 4 + 1));
                if (prop == null) continue;
                var s = (GameObject)PrefabUtility.InstantiatePrefab(prop);
                PrefabUtility.UnpackPrefabInstance(s, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                s.name = "Skull" + i;
                s.transform.SetParent(skulls.transform, false);
            }
            var poison = FindFx("FX_Poison_Green_01");
            if (poison != null)
            {
                var p = (GameObject)PrefabUtility.InstantiatePrefab(poison);
                p.name = "Poison";
                p.transform.SetParent(root.transform, false);
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log($"[ProjectRestart] Built {path} — {skulls.transform.childCount} skulls + poison miasma.");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    /// <summary>The weapon-swap stand near the spawn — starts holding Big
    /// Sword; E takes it and leaves your katana on the stand. Re-runnable.</summary>
    private static void EnsureWeaponPickup(Transform player)
    {
        var existing = UnityEngine.Object.FindFirstObjectByType<WeaponPickup>();
        if (existing == null)
        {
            var go = new GameObject("WeaponPickup");
            go.transform.position = player.position + new Vector3(-2.2f, 0f, 1.4f);
            existing = go.AddComponent<WeaponPickup>();
        }
        var bigSword = AssetDatabase.LoadAssetAtPath<WeaponSet>(BigSwordAssetPath);
        var so = new SerializedObject(existing);
        var setProp = so.FindProperty("set");
        if (setProp != null && setProp.objectReferenceValue == null && bigSword != null)
            setProp.objectReferenceValue = bigSword;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(existing.gameObject);
    }

    // ---- Live weapon-socket tuning --------------------------------------
    // In Play Mode (locked on so the sword is drawn), click "Cycle Grip
    // Rotation" ??each click steps the sword to the next candidate grip
    // orientation and logs it. Stop when it looks right; the value is already
    // saved on BigSword.asset. Then use the axis nudges to slide the grip.

    private static readonly Vector3[] GripCandidates =
    {
        Vector3.zero,
        new Vector3(90f, 0f, 0f), new Vector3(-90f, 0f, 0f), new Vector3(180f, 0f, 0f),
        new Vector3(0f, 90f, 0f), new Vector3(0f, -90f, 0f), new Vector3(0f, 180f, 0f),
        new Vector3(0f, 0f, 90f), new Vector3(0f, 0f, -90f), new Vector3(0f, 0f, 180f),
        new Vector3(0f, 90f, -90f), new Vector3(0f, -90f, 90f),
        new Vector3(90f, 0f, 90f), new Vector3(-90f, 0f, -90f),
        new Vector3(90f, 90f, 0f), new Vector3(-90f, -90f, 0f),
        new Vector3(0f, 180f, 90f), new Vector3(0f, 180f, -90f),
        new Vector3(90f, -90f, 0f), new Vector3(-90f, 90f, 0f),
        new Vector3(0f, -90f, -90f), new Vector3(0f, 90f, 90f),
        new Vector3(180f, 90f, 0f), new Vector3(180f, -90f, 0f),
        new Vector3(90f, 180f, 0f), new Vector3(-90f, 180f, 0f),
    };
    private static int gripCycleIndex;

    [MenuItem("Tools/Project Restart/Weapon Socket/Cycle Grip Rotation")]
    private static void CycleGrip()
    {
        var set = AssetDatabase.LoadAssetAtPath<WeaponSet>(BigSwordAssetPath);
        if (set == null)
        {
            Debug.LogWarning("[ProjectRestart] BigSword.asset not found ??run combat setup first.");
            return;
        }
        var i = gripCycleIndex % GripCandidates.Length;
        set.handLocalEuler = GripCandidates[i];
        EditorUtility.SetDirty(set);
        Debug.Log($"[WeaponSocket] grip candidate {i + 1}/{GripCandidates.Length}: euler {set.handLocalEuler} ??click again if wrong, STOP when it looks right.");
        gripCycleIndex++;
    }

    [MenuItem("Tools/Project Restart/Weapon Socket/Blade Roll +15°")]
    private static void RollP() => NudgeSocket(roll: 15f);
    [MenuItem("Tools/Project Restart/Weapon Socket/Blade Roll -15°")]
    private static void RollN() => NudgeSocket(roll: -15f);
    [MenuItem("Tools/Project Restart/Weapon Socket/Slide Along Grip +2cm")]
    private static void GripP() => SlideGrip(0.02f);
    [MenuItem("Tools/Project Restart/Weapon Socket/Slide Along Grip -2cm")]
    private static void GripN() => SlideGrip(-0.02f);

    /// <summary>Slides the sword along its own blade direction ??in/out of the palm.</summary>
    private static void SlideGrip(float amount)
    {
        var set = AssetDatabase.LoadAssetAtPath<WeaponSet>(BigSwordAssetPath);
        if (set == null) return;
        var axis = set.bladeAxis.sqrMagnitude > 0.001f ? set.bladeAxis.normalized : Vector3.forward;
        var bladeDir = Quaternion.Euler(set.handLocalEuler) * axis;
        set.handLocalPosition += bladeDir * amount;
        EditorUtility.SetDirty(set);
    }

    private static readonly Vector3[] BladeAxes = { Vector3.forward, Vector3.up, Vector3.right };
    private static int bladeAxisIndex;

    /// <summary>Steps bladeAxis through Z?�Y?�X until rolling spins around the tip?�handle line.</summary>
    [MenuItem("Tools/Project Restart/Weapon Socket/Cycle Blade Axis")]
    private static void CycleBladeAxis()
    {
        var set = AssetDatabase.LoadAssetAtPath<WeaponSet>(BigSwordAssetPath);
        if (set == null) return;
        set.bladeAxis = BladeAxes[bladeAxisIndex % BladeAxes.Length];
        bladeAxisIndex++;
        EditorUtility.SetDirty(set);
        Debug.Log($"[WeaponSocket] bladeAxis = {set.bladeAxis} ??click again if roll still spins the wrong way.");
    }

    [MenuItem("Tools/Project Restart/Weapon Socket/Nudge X +2cm")]
    private static void NudgeXp() => NudgeSocket(pos: new Vector3(0.02f, 0f, 0f));
    [MenuItem("Tools/Project Restart/Weapon Socket/Nudge X -2cm")]
    private static void NudgeXn() => NudgeSocket(pos: new Vector3(-0.02f, 0f, 0f));
    [MenuItem("Tools/Project Restart/Weapon Socket/Nudge Y +2cm")]
    private static void NudgeYp() => NudgeSocket(pos: new Vector3(0f, 0.02f, 0f));
    [MenuItem("Tools/Project Restart/Weapon Socket/Nudge Y -2cm")]
    private static void NudgeYn() => NudgeSocket(pos: new Vector3(0f, -0.02f, 0f));
    [MenuItem("Tools/Project Restart/Weapon Socket/Nudge Z +2cm")]
    private static void NudgeZp() => NudgeSocket(pos: new Vector3(0f, 0f, 0.02f));
    [MenuItem("Tools/Project Restart/Weapon Socket/Nudge Z -2cm")]
    private static void NudgeZn() => NudgeSocket(pos: new Vector3(0f, 0f, -0.02f));
    [MenuItem("Tools/Project Restart/Weapon Socket/Reset Socket")]
    private static void ResetSocket()
    {
        var set = AssetDatabase.LoadAssetAtPath<WeaponSet>(BigSwordAssetPath);
        if (set == null) return;
        set.handLocalPosition = Vector3.zero;
        set.handLocalEuler = Vector3.zero;
        set.bladeRoll = 0f;
        gripCycleIndex = 0;
        EditorUtility.SetDirty(set);
    }

    private static void NudgeSocket(Vector3 pos = default, Vector3 euler = default, float roll = 0f)
    {
        var set = AssetDatabase.LoadAssetAtPath<WeaponSet>(BigSwordAssetPath);
        if (set == null)
        {
            Debug.LogWarning("[ProjectRestart] BigSword.asset not found ??run combat setup first.");
            return;
        }
        set.handLocalPosition += pos;
        set.handLocalEuler += euler;
        set.bladeRoll += roll;
        EditorUtility.SetDirty(set);
    }

    /// <summary>
    /// Loads the named clip from the FBX whose file name (no extension) equals
    /// fbxFileName ??the same suffix exists in multiple packs, so exact file match
    /// keeps Big Sword and Katana clips from colliding.
    /// </summary>
    private static AnimationClip LoadClip(string fbxFileName, string clipName, string pathMustContain = null)
    {
        foreach (var guid in AssetDatabase.FindAssets(fbxFileName))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
            // Packs that ship both Generic and Humanoid takes of one filename ??            // the hint pins the humanoid one (generic clips can't retarget).
            if (pathMustContain != null && !path.Replace('\\', '/').Contains(pathMustContain)) continue;
            if (!Path.GetFileNameWithoutExtension(path).Equals(fbxFileName, StringComparison.OrdinalIgnoreCase)) continue;
            var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().ToArray();
            var clip = clips.FirstOrDefault(c => c.name == clipName) ?? clips.FirstOrDefault();
            if (clip != null) return clip;
        }
        return null;
    }

    private static GameObject FindPrefab(Func<string, bool> pathFilter)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:GameObject"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (pathFilter(path))
                return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        return null;
    }
}

