using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Setup Dark Lord Boss.
/// Builds Assets/_Project/Animations/BossLordBase.controller — a Locomotion
/// blend (Big Sword Idle→Walk by Speed) plus every named state BossLord.CrossFades
/// to: greatsword strings and skills (Big Sword attack-root takes), ninja
/// teleport/dodge (NinjaAnimset inplace — own avatar), MagicalKnight crown/cast
/// poses (own avatar), plus Roar/Stagger/Die and the Warden P2/P3 states. Then dresses arena 2 deeper along
/// the ruin corridor: unpacked DarkLord visual (mask+cape attachments,
/// ornate blade socketed to Hand_R, 1.15x), Health+Targetable+CharacterController
/// +BossLord, RootMotionRelay+FootGrounding on the ANIMATOR object, FogGate2.
/// Re-runnable in place.
/// </summary>
public static class ProjectRestartBossLord
{
    private const string ScenePath = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const string ControllerPath = "Assets/_Project/Animations/BossLordBase.controller";
    private const string LordPrefabPath =
        "Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs/Characters/SM_Chr_DarkLord_Male_01.prefab";
    private const string AttachDir =
        "Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs/Characters/Attachments";
    private const string MaskPrefabPath = AttachDir + "/SM_Chr_Attach_DarkLord_Mask_01.prefab";
    private const string CapePrefabPath = AttachDir + "/SM_Chr_Attach_DarkLord_Cape_01.prefab";
    private const string WeaponPrefabPath =
        "Assets/ThirdParty/Synty/PolygonDungeon/Prefabs/Weapons/SM_Wep_Ornate_Sword_01.prefab";
    private const string FxDir = "Assets/ThirdParty/Synty/PolygonFantasyRivals/Prefabs/FX";

    private const string NRoot = "Assets/ThirdParty/NinjaAnimset/Animation/Humanoid/";
    private const string MkRoot = "Assets/ThirdParty/MagicalKnightSet/Animation/Humanoid/";

    private const string DieFallbackPath = MkRoot + "dead_02.fbx";

    private static readonly Vector3 ArenaCenter = new Vector3(101f, 0f, 150f);

    [MenuItem("Tools/Project Restart/Setup Dark Lord Boss")]
    public static void Setup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Setup Dark Lord deferred: exit Play Mode and wait for compilation/import to finish, then run again.");
            return;
        }
        ProjectRestartUrpFix.FixAll();
        var controller = BuildController();
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var boss = EnsureLord(controller);
        EnsureArena();
        EnsureGate(boss);

        Require(EditorSceneManager.SaveScene(scene), "Could not save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectRestart] Dark Lord boss ready - BossLordBase.controller (incl. Warden P2/P3 states), " +
                  "FogGate2 seals the approach. Play: samurai reads; at half health Phase 2 (still in control); " +
                  "the first kill hands the body to the Core (Phase 3). Run Tools > Project Restart > " +
                  "Setup Warden Fight to build the Core Sanctum arena around him.");
    }

    // ---------- controller ----------

    internal static AnimatorController BuildController()
    {
        const string folder = "Assets/_Project/Animations";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/_Project", "Animations");
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)
                         ?? AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.parameters = System.Array.Empty<AnimatorControllerParameter>();
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

        var sm = controller.layers[0].stateMachine;
        var loco = EnsureState(sm, "Locomotion", BuildLocomotionTree(controller), new Vector3(-300, 0));
        sm.defaultState = loco;

        // P1 the Warden — he carries a greatsword, so every cut is an authored Big Sword
        // take (attack-root, the same config the player's big-sword setup applies to
        // these clips): the relay consumes the authored travel, his blade does the work.
        EnsureState(sm, "DrawSlash", Root(LoadClip(BigRoot + "2_Attacks/2__7Combos/M_Big_Sword@Attack_7Combo_1.FBX", "Attack_7Combo_1")), new Vector3(0, -140), 1.0f);
        EnsureState(sm, "TwinCut", Root(LoadClip(BigRoot + "2_Attacks/2__7Combos/M_Big_Sword@Attack_7Combo_2.FBX", "Attack_7Combo_2")), new Vector3(0, 0), 1.0f);
        EnsureState(sm, "TwinCut2", Root(LoadClip(BigRoot + "2_Attacks/2__7Combos/M_Big_Sword@Attack_7Combo_3.FBX", "Attack_7Combo_3")), new Vector3(0, 70), 1.0f);
        EnsureState(sm, "HeavenCut", Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_C.FBX", "Skill_C")), new Vector3(0, 140), 0.9f);
        EnsureState(sm, "RushDraw", Root(LoadClip(BigRoot + "2_Attacks/3__Dash_Attack/M_Big_Sword@Dash_Attack_ver_A.FBX", "Dash_Attack_ver_A")), new Vector3(0, 280), 1.05f);
        EnsureState(sm, "WolfFang", Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_G_ALL.FBX", "Skill_G_ALL")), new Vector3(0, 420), 0.95f);
        EnsureState(sm, "Bonesunder", Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_K.FBX", "Skill_K")), new Vector3(0, 560), 1.0f);
        EnsureState(sm, "CounterCleave", Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_F.FBX", "Skill_F")), new Vector3(0, 700), 1.05f);

        // P2 ninja �X _inplace takes: travel is scripted (teleport/dodge/steps),
        // clips keep their own avatar. Faster playback = the ninja phase read.
        EnsureState(sm, "ShadowJab1", LoadClip(NRoot + "combo_01_1_inplace.fbx", "combo_01_1"), new Vector3(300, -280), 1.15f);
        EnsureState(sm, "ShadowJab2", LoadClip(NRoot + "combo_01_2_inplace.fbx", "combo_01_2"), new Vector3(300, -140), 1.15f);
        EnsureState(sm, "ShadowJab3", LoadClip(NRoot + "combo_01_3_inplace.fbx", "combo_01_3"), new Vector3(300, 0), 1.15f);
        EnsureState(sm, "KunaiThrow", LoadClip(NRoot + "daggerthrow_01_inplace.fbx", "daggerthrow_01"), new Vector3(300, 140), 1.1f);
        EnsureState(sm, "TeleportOut", LoadClip(NRoot + "teleport_start_inplace.fbx", "teleport_start"), new Vector3(600, -140), 1.2f);
        EnsureState(sm, "TeleportIn", LoadClip(NRoot + "teleport_end_inplace.fbx", "teleport_end"), new Vector3(600, 0), 1.2f);
        EnsureState(sm, "DodgeL", LoadClip(NRoot + "avoid_L_inplace.fbx", "avoid_L"), new Vector3(600, 140), 1.2f);
        EnsureState(sm, "DodgeR", LoadClip(NRoot + "avoid_R_inplace.fbx", "avoid_R"), new Vector3(600, 280), 1.2f);

        // P3 magic �X MagicalKnight keeps its own avatar.
        EnsureState(sm, "PyreCast", LoadClip(MkRoot + "atk_energy02.fbx", "atk_energy02"), new Vector3(900, -140), 0.95f);
        EnsureState(sm, "AshSlam", LoadClip(MkRoot + "atk_energy05.fbx", "atk_energy05"), new Vector3(900, 0), 0.85f);
        EnsureState(sm, "EmberEdge", LoadClip(MkRoot + "atk_sword02.fbx", "atk_sword02"), new Vector3(900, 140), 1f);
        EnsureState(sm, "Revive", LoadClip(MkRoot + "rise_02.fbx", "rise_02"), new Vector3(900, 280), 0.9f);

        EnsureState(sm, "Roar", LoadClip(MkRoot + "buff01.fbx", "buff01"), new Vector3(-600, -140), 0.95f);
        // Big Sword reactions (same feet-baked config as StaggerHeavy / the player's Death).
        EnsureState(sm, "Stagger", Bake(LoadClip(BigRoot + "4_Damages/1__Front/M_Big_Sword@Damage_Front_Big_ver_A.FBX", "Damage_Front_Big_ver_A")), new Vector3(-600, 0), 1f);
        var dieClip = LoadClip(BigRoot + "4_Damages/6__Die/M_Big_Sword@Damage_Die.FBX", "Damage_Die");
        EnsureState(sm, "Die", Bake(dieClip != null ? dieClip : LoadClip(DieFallbackPath, "dead_02")), new Vector3(-600, 140), 1f);
        EnsureWardenStates(controller, sm);

        foreach (var s in sm.states) s.state.transitions = System.Array.Empty<AnimatorStateTransition>();
        sm.anyStateTransitions = System.Array.Empty<AnimatorStateTransition>();

        ProjectRestartCombat.SetIkPass(controller); // FootGrounding's OnAnimatorIK
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static BlendTree BuildLocomotionTree(AnimatorController controller)
    {
        var old = AssetDatabase.LoadAllAssetsAtPath(ControllerPath);
        foreach (var a in old)
            if (a is BlendTree bt && bt.name == "BossLordMove") AssetDatabase.RemoveObjectFromAsset(bt);
        var tree = new BlendTree
        {
            name = "BossLordMove",
            blendType = BlendTreeType.Simple1D,
            blendParameter = "Speed",
            useAutomaticThresholds = false
        };
        // The greatsword carry from the first step (Phase 3 slows the same walk further).
        tree.AddChild(Bake(LoadClip(BigRoot + "1_Movements/1__Idle/M_Big_Sword@Idle.FBX", "Idle")), 0f);
        tree.AddChild(Bake(LoadClip(BigRoot + "1_Movements/2__Walk/A/M_Big_Sword@Walk_ver_A_Front.FBX", "Walk_ver_A_Front")), 1f);
        var kids = tree.children;
        kids[1].timeScale = 0.85f;
        tree.children = kids;
        AssetDatabase.AddObjectToAsset(tree, controller);
        return tree;
    }

    private const string BigRoot = "Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_Big_Sword/";

    /// <summary>The Warden's Phase 2 / Phase 3 states (BossLord CrossFades by name).
    /// Clips shared with the player keep the SAME root config the player's setup
    /// applies (Big Sword skills = attack-root; Intro / Jump_Attack_Combo_3 /
    /// MagicalKnight takes untouched) so neither tool re-imports them differently.
    /// Boss-only Grruzam takes get the feet-Y bake like the Colossus' clips.</summary>
    private static void EnsureWardenStates(AnimatorController controller, AnimatorStateMachine sm)
    {
        // P3 locomotion — the greatsword carry (same clips/config as ColossusBase).
        EnsureState(sm, "LocomotionHeavy", BuildHeavyTree(controller), new Vector3(-300, 280));

        // Phase 2 — still in control.
        EnsureState(sm, "CrimsonSweep", Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_H.FBX", "Skill_H")), new Vector3(1200, -280), 0.85f);
        // King's Spear is a lunge, not a cast: the Big Sword dash take (WardenPose holds the thrust stance).
        EnsureState(sm, "KingsSpear", Root(LoadClip(BigRoot + "2_Attacks/3__Dash_Attack/M_Big_Sword@Dash_Attack_ver_B.FBX", "Dash_Attack_ver_B")), new Vector3(1200, -140), 0.8f);
        EnsureState(sm, "Cyclone", Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_E.FBX", "Skill_E")), new Vector3(1200, -420), 0.9f);
        EnsureState(sm, "Executioner", LoadClip(MkRoot + "atk_overhandslash.fbx", "atk_overhandslash"), new Vector3(1200, 0), 1f);
        EnsureState(sm, "CrownRaise", LoadClip(MkRoot + "atk_energy09_start.fbx", "atk_energy09_start"), new Vector3(1200, 140), 1f);
        EnsureState(sm, "CrownHold", LoadClip(MkRoot + "atk_energy09_loop.fbx", "atk_energy09_loop"), new Vector3(1200, 280), 1f);
        EnsureState(sm, "CrownLand", LoadClip(MkRoot + "atk_energy09_end.fbx", "atk_energy09_end"), new Vector3(1200, 420), 1f);

        // Phase 3 — the Core has taken control. Heavy and slow.
        EnsureState(sm, "HeavySweep", Root(LoadClip(BigRoot + "2_Attacks/1__4Combos/M_Big_Sword@Attack_4Combo_2.FBX", "Attack_4Combo_2")), new Vector3(1500, -420), 0.8f);
        EnsureState(sm, "HeavySmash", Root(LoadClip(BigRoot + "2_Attacks/1__4Combos/M_Big_Sword@Attack_4Combo_4.FBX", "Attack_4Combo_4")), new Vector3(1500, -280), 0.75f);
        EnsureState(sm, "HeavyCombo", Root(LoadClip(BigRoot + "2_Attacks/2__7Combos/M_Big_Sword@Attack_7Combo_5.FBX", "Attack_7Combo_5")), new Vector3(1500, -140), 0.8f);
        var plant = Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_A.FBX", "Skill_A"));
        EnsureState(sm, "TwinRupture", plant, new Vector3(1500, 0), 0.85f);
        EnsureState(sm, "CorePlant", plant, new Vector3(1500, 140), 0.8f);
        EnsureState(sm, "RuinousSweep", Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_L.FBX", "Skill_L")), new Vector3(1500, 280), 1f);
        EnsureState(sm, "Worldsplitter", Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_D.FBX", "Skill_D")), new Vector3(1500, 420), 1f);
        // Crimson Guillotine: the rising moon-arc into a slam WITH its authored leap travel.
        EnsureState(sm, "Guillotine", Root(LoadClip(BigRoot + "3_Skills/M_Big_Sword@Skill_J.FBX", "Skill_J")), new Vector3(1500, 560), 0.9f);
        EnsureState(sm, "KingsFallCrouch", Bake(LoadClip(BigRoot + "1_Movements/7__Double_Jump/M_Big_Sword@Double_Jump_Start_ZeroHeight.FBX", "Double_Jump_Start_ZeroHeight")), new Vector3(1800, -280), 0.5f);
        EnsureState(sm, "KingsFallHang", Bake(LoadClip(BigRoot + "4_Damages/1__Front/M_Big_Sword@Damage_Front_Flying_ver_A_ZeroHeight.FBX", "Damage_Front_Flying_ver_A_ZeroHeight")), new Vector3(1800, -140), 1f);
        EnsureState(sm, "KingsFallDrop", LoadClip(BigRoot + "2_Attacks/4__Jump_Attack/M_Big_Sword@Jump_Attack_Combo_3_ZeroHeight.FBX", "Jump_Attack_Combo_3_ZeroHeight"), new Vector3(1800, 0), 1.6f);
        EnsureState(sm, "KingsFallLand", Bake(LoadClip(BigRoot + "1_Movements/6__Jump/M_Big_Sword@Jump_End_ZeroHeight.FBX", "Jump_End_ZeroHeight")), new Vector3(1800, 140), 0.7f);
        EnsureState(sm, "StaggerHeavy", Bake(LoadClip(BigRoot + "4_Damages/1__Front/M_Big_Sword@Damage_Front_Big_ver_A.FBX", "Damage_Front_Big_ver_A")), new Vector3(1800, 280), 0.8f);

        // The Phase 2 → 3 transition and the finale.
        EnsureState(sm, "Collapse", Bake(LoadClip(BigRoot + "4_Damages/1__Front/M_Big_Sword@Damage_Front_High_KnockDown_ZeroHeight.FBX", "Damage_Front_High_KnockDown_ZeroHeight")), new Vector3(2100, -140), 1f);
        EnsureState(sm, "StandStruggle", Bake(LoadClip(BigRoot + "4_Damages/1__Front/M_Big_Sword@Damage_Front_Down_StandUp.FBX", "Damage_Front_Down_StandUp")), new Vector3(2100, 0), 0.55f);
        EnsureState(sm, "GreatswordPull", LoadClip(BigRoot + "1_Movements/0__Intro/M_Big_Sword@Intro.FBX", "Intro"), new Vector3(2100, 140), 1f);
        EnsureState(sm, "FinalFall", Bake(LoadClip(BigRoot + "4_Damages/1__Front/M_Big_Sword@Damage_Front_Flying_ver_B_ZeroHeight.FBX", "Damage_Front_Flying_ver_B_ZeroHeight")), new Vector3(2100, 280), 1f);
    }

    private static BlendTree BuildHeavyTree(AnimatorController controller)
    {
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(ControllerPath))
            if (a is BlendTree bt && bt.name == "BossLordHeavyMove") AssetDatabase.RemoveObjectFromAsset(bt);
        var tree = new BlendTree
        {
            name = "BossLordHeavyMove",
            blendType = BlendTreeType.Simple1D,
            blendParameter = "Speed",
            useAutomaticThresholds = false
        };
        tree.AddChild(Bake(LoadClip(BigRoot + "1_Movements/1__Idle/M_Big_Sword@Idle.FBX", "Idle")), 0f);
        tree.AddChild(Bake(LoadClip(BigRoot + "1_Movements/2__Walk/A/M_Big_Sword@Walk_ver_A_Front.FBX", "Walk_ver_A_Front")), 1f);
        var kids = tree.children;
        kids[1].timeScale = 0.72f;
        tree.children = kids;
        AssetDatabase.AddObjectToAsset(tree, controller);
        return tree;
    }

    // ---------- scene ----------

    private static BossLord EnsureLord(AnimatorController controller)
    {
        var boss = Object.FindFirstObjectByType<BossLord>();
        if (boss == null)
        {
            var go = new GameObject("DarkLordBoss");
            go.transform.SetPositionAndRotation(ArenaCenter, Quaternion.Euler(0f, 180f, 0f));
            boss = go.AddComponent<BossLord>();
        }
        var health = boss.GetComponent<Health>();
        Require(health != null, "BossLord requires Health (RequireComponent failed).");
        var hso = new SerializedObject(health);
        SetIfFound(hso, "maxHealth", 1400f);
        SetIfFound(hso, "soulsReward", 12000);
        var bf = hso.FindProperty("bloodFx");
        if (bf != null) bf.objectReferenceValue = ProjectRestartCombat.FindFx("FX_BloodSplat_01");
        hso.ApplyModifiedPropertiesWithoutUndo();

        if (boss.GetComponent<Targetable>() == null) boss.gameObject.AddComponent<Targetable>();
        var cc = boss.GetComponent<CharacterController>();
        if (cc == null) cc = boss.gameObject.AddComponent<CharacterController>();

        // Boss Polish installed the custom rig: keep it (and its fitted sword) —
        // rebuilding the Synty visual here used to put both bodies on screen.
        var custom = boss.transform.Find("BossVisual");
        if (custom != null)
        {
            var customAnim = custom.GetComponent<Animator>();
            if (customAnim != null)
            {
                customAnim.runtimeAnimatorController = controller;
                customAnim.applyRootMotion = true;
                customAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (customAnim.GetComponent<RootMotionRelay>() == null) customAnim.gameObject.AddComponent<RootMotionRelay>();
                if (customAnim.GetComponent<FootGrounding>() == null) customAnim.gameObject.AddComponent<FootGrounding>();
                var cso = new SerializedObject(boss);
                var cp = cso.FindProperty("bossAnimator");
                if (cp != null) cp.objectReferenceValue = customAnim;
                WriteCue(cso.FindProperty("roarFx"), "Fire_Circle_FX", "root", new Vector3(0f, 0.5f, 0f), 1.6f, 5, 3f);
                WriteCue(cso.FindProperty("slamFx"), "Fire_Circle_FX", "root", Vector3.zero, 2f, 1, 2.5f);
                WriteCue(cso.FindProperty("reviveFx"), "EnergyPull_FX", "body", new Vector3(0f, 1.2f, 0f), 2f, 5, 3.5f);
                WriteCue(cso.FindProperty("teleportFx"), "EnergyPush_FX", "root", Vector3.zero, 1.4f, 1, 2f);
                cso.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(boss.gameObject);
                return boss;
            }
        }

        var old = boss.transform.Find("DarkLordVisual");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LordPrefabPath);
        Require(prefab != null, "DarkLord prefab missing: " + LordPrefabPath);
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        visual.name = "DarkLordVisual";
        visual.transform.SetParent(boss.transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = Vector3.one * 1.15f; // a lord reads bigger

        var animator = visual.GetComponentInChildren<Animator>(true);
        Require(animator != null && animator.avatar != null && animator.avatar.isHuman,
                "DarkLord needs a humanoid-avatar Animator.");
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = true;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var bounds = CalcBounds(visual);
        cc.height = Mathf.Clamp(bounds.size.y, 1.8f, 3f);
        cc.radius = Mathf.Clamp(Mathf.Max(bounds.size.x, bounds.size.z) * 0.26f, 0.35f, 0.8f);
        cc.center = new Vector3(0f, cc.height * 0.5f + 0.05f, 0f);
        cc.slopeLimit = 50f;
        cc.stepOffset = 0.45f;

        var handR = FindDeep(visual.transform, "Hand_R") ?? animator.GetBoneTransform(HumanBodyBones.RightHand);
        ArmWeapon(handR, animator);
        // Attachments socket onto humanoid bones with identity local pose �X
        // Synty attach pieces are authored for the shared rig.
        AttachTo(MaskPrefabPath, animator.GetBoneTransform(HumanBodyBones.Head));
        AttachTo(CapePrefabPath, animator.GetBoneTransform(HumanBodyBones.Chest));

        if (animator.GetComponent<RootMotionRelay>() == null)
            animator.gameObject.AddComponent<RootMotionRelay>();
        var fg = animator.GetComponent<FootGrounding>();
        if (fg == null) fg = animator.gameObject.AddComponent<FootGrounding>();
        var fso = new SerializedObject(fg);
        SetIfFound(fso, "pelvisOffsetMax", 0.5f);
        SetIfFound(fso, "attackPelvisOffsetMax", 0.7f);
        fso.ApplyModifiedPropertiesWithoutUndo();

        var so = new SerializedObject(boss);
        var animProp = so.FindProperty("bossAnimator");
        if (animProp != null) animProp.objectReferenceValue = animator;
        WriteCue(so.FindProperty("roarFx"), "Fire_Circle_FX", "root", new Vector3(0f, 0.5f, 0f), 1.6f, 5, 3f);
        WriteCue(so.FindProperty("slamFx"), "Fire_Circle_FX", "root", Vector3.zero, 2f, 1, 2.5f);
        WriteCue(so.FindProperty("reviveFx"), "EnergyPull_FX", "body", new Vector3(0f, 1.2f, 0f), 2f, 5, 3.5f);
        WriteCue(so.FindProperty("teleportFx"), "EnergyPush_FX", "root", Vector3.zero, 1.4f, 1, 2f);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(boss.gameObject);
        return boss;
    }

    /// <summary>Arena 2 �X deeper along the same ruin corridor (south approach).</summary>
    private static void EnsureArena()
    {
        if (GameObject.Find("BossArenaFloor2") != null) return;
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "BossArenaFloor2";
        floor.transform.position = ArenaCenter + Vector3.down * 0.55f;
        floor.transform.localScale = new Vector3(26f, 1f, 26f);
        var r = floor.GetComponent<MeshRenderer>();
        if (r != null)
            r.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/_Project/Materials/TestGround.mat");
        EditorUtility.SetDirty(floor);
    }

    private static void EnsureGate(BossLord boss)
    {
        var gate = GameObject.Find("FogGate2");
        if (gate == null)
        {
            gate = new GameObject("FogGate2");
            gate.transform.position = new Vector3(101f, 1.6f, 143f);
        }
        var trig = gate.GetComponent<BoxCollider>();
        if (trig == null) trig = gate.AddComponent<BoxCollider>();
        trig.isTrigger = true;
        trig.size = new Vector3(10f, 4f, 3f);

        var blocker = GameObject.Find("FogGateBlocker2");
        if (blocker == null)
        {
            blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blocker.name = "FogGateBlocker2";
            blocker.transform.SetParent(gate.transform, false);
            blocker.transform.localPosition = Vector3.zero;
            blocker.transform.localScale = new Vector3(10f, 5f, 0.8f);
            var r = blocker.GetComponent<MeshRenderer>();
            if (r != null) r.enabled = false;
            blocker.SetActive(false);
        }

        var fg = gate.GetComponent<FogGate>();
        if (fg == null) fg = gate.AddComponent<FogGate>();
        var so = new SerializedObject(fg);
        var bp = so.FindProperty("boss");
        if (bp != null) bp.objectReferenceValue = boss;
        var bl = so.FindProperty("blocker");
        if (bl != null) bl.objectReferenceValue = blocker;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(gate);
    }

    // ---------- helpers ----------

    private static void ArmWeapon(Transform hand, Animator animator)
    {
        if (hand == null)
        {
            Debug.LogWarning("[ProjectRestart] DarkLord hand bone not found �X weapon skipped.");
            return;
        }
        var old = FindDeep(hand.root, "LordWeapon");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WeaponPrefabPath);
        if (prefab == null) { Debug.LogWarning("[ProjectRestart] Weapon prefab missing: " + WeaponPrefabPath); return; }
        var item = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        item.name = "LordWeapon";
        item.transform.SetParent(hand, false);
        // Palm grip, not wrist pivot: the weapon's pivot is its handle, so it
        // sits on the wrist��middle-knuckle line like WeaponSocket.palmGrip.
        var knuckle = animator != null ? animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal) : null;
        item.transform.position = knuckle != null
            ? Vector3.Lerp(hand.position, knuckle.position, 0.55f)
            : hand.position;
        item.transform.localRotation = Quaternion.identity;
        item.transform.localScale = Vector3.one;
        Debug.Log($"[ProjectRestart] LordWeapon grip -> {hand.name} " +
                  $"localPos={item.transform.localPosition} localRot={item.transform.localEulerAngles} " +
                  "(tune by editing LordWeapon's local transform in the scene)", item);
    }

    private static void AttachTo(string prefabPath, Transform bone)
    {
        if (bone == null)
        {
            Debug.LogWarning("[ProjectRestart] Attachment bone missing for " + prefabPath);
            return;
        }
        var name = System.IO.Path.GetFileNameWithoutExtension(prefabPath);
        var old = FindDeep(bone.root, name);
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) { Debug.LogWarning("[ProjectRestart] Attach prefab missing: " + prefabPath); return; }
        var item = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        item.name = name;
        item.transform.SetParent(bone, false);
        item.transform.localPosition = Vector3.zero;
        item.transform.localRotation = Quaternion.identity;
        item.transform.localScale = Vector3.one;
    }

    private static void WriteCue(SerializedProperty cue, string prefabName, string attach,
                                 Vector3 offset, float scale, int palette, float life)
    {
        if (cue == null) return;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{FxDir}/{prefabName}.prefab");
        if (prefab == null)
        {
            Debug.LogWarning("[ProjectRestart] FX prefab missing: " + prefabName + " �X cue left empty.");
            return;
        }
        cue.FindPropertyRelative("prefab").objectReferenceValue = prefab;
        cue.FindPropertyRelative("attach").stringValue = attach;
        cue.FindPropertyRelative("offset").vector3Value = offset;
        cue.FindPropertyRelative("scale").floatValue = scale;
        cue.FindPropertyRelative("palette").intValue = palette;
        cue.FindPropertyRelative("life").floatValue = life;
    }

    private static Bounds CalcBounds(GameObject visual)
    {
        Bounds? b = null;
        foreach (var r in visual.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            b = b.HasValue ? Encapsulate(b.Value, r.bounds) : r.bounds;
        }
        return b ?? new Bounds(Vector3.up * 1.5f, new Vector3(1.6f, 3f, 1.6f));
    }

    private static Bounds Encapsulate(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

    private static void SetIfFound(SerializedObject so, string name, float v)
    {
        var p = so.FindProperty(name);
        if (p != null && p.propertyType == SerializedPropertyType.Float) p.floatValue = v;
        else if (p != null && p.propertyType == SerializedPropertyType.Integer) p.intValue = (int)v;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (var i = 0; i < root.childCount; i++)
        {
            var f = FindDeep(root.GetChild(i), name);
            if (f != null) return f;
        }
        return null;
    }

    private static AnimatorState EnsureState(AnimatorStateMachine sm, string name, Motion clip, Vector3 pos, float speed = 1f)
    {
        foreach (var s in sm.states)
            if (s.state.name == name) { s.state.motion = clip; s.state.speed = speed; return s.state; }
        var st = sm.AddState(name, pos);
        st.motion = clip;
        st.speed = speed;
        st.writeDefaultValues = false;
        return st;
    }

    /// <summary>Stationary clips: feet-baked root Y + shared avatar.</summary>
    private static AnimationClip Bake(AnimationClip clip) => ProjectRestartCombat.EnsureClipYBake(clip);

    /// <summary>Strikes: authored XZ stays live for the RootMotionRelay.</summary>
    private static AnimationClip Root(AnimationClip clip) => ProjectRestartCombat.EnsureAttackRootClip(clip);

    private static AnimationClip LoadClip(string path, string clipName)
    {
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
            if (a is AnimationClip c && c.name == clipName) return c;
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
            if (a is AnimationClip c && !c.name.StartsWith("__")) return c;
        Debug.LogWarning("[ProjectRestart] Clip not found in " + path);
        return null;
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new System.Exception("[ProjectRestart] " + message);
    }
}


// Manual, project-owned visual replacement. Source enemy gameplay and vendor assets stay intact.
public static class BossVisualSetup
{
 const string F="Assets/_Project/BossPolish/";
 const string O=@"C:\Users\milkw\Documents\Codex\2026-10-04\open-my-blender-character-project-and\outputs\Boss";
 [System.Serializable] class Topology { public string[] edges=System.Array.Empty<string>(),points=System.Array.Empty<string>(); }
 static string Key(Vector2 u)=>Mathf.RoundToInt(u.x*10000)+","+Mathf.RoundToInt(u.y*10000);
 static string Edge(string a,string b)=>string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;
 [MenuItem("Tools/Boss Polish/1 Build rigged boss")]
 public static void Build()
 {
  if(EditorApplication.isPlaying)throw new System.InvalidOperationException("Exit Play Mode first.");
  var importer=(ModelImporter)AssetImporter.GetAtPath(F+"Boss_Unity.fbx");
  importer.isReadable=true;importer.importAnimation=false;importer.importCameras=false;importer.importLights=false;
  importer.materialImportMode=ModelImporterMaterialImportMode.None;importer.meshCompression=ModelImporterMeshCompression.Off;
  importer.animationType=ModelImporterAnimationType.Human;importer.avatarSetup=ModelImporterAvatarSetup.CreateFromThisModel;importer.globalScale=2.4f;
  importer.SaveAndReimport();
  var model=AssetDatabase.LoadAssetAtPath<GameObject>(F+"Boss_Unity.fbx");
  var transforms=model.GetComponentsInChildren<Transform>(true);
  var human=new System.Collections.Generic.List<HumanBone>();
  foreach(var name in HumanTrait.BoneName)
  {
   var t=System.Array.Find(transforms,t=>t.name==name.Replace(" ",""));
   if(t)human.Add(new HumanBone{boneName=t.name,humanName=name,limit=new HumanLimit{useDefaultValues=true}});
  }
  var desc=importer.humanDescription;desc.human=human.ToArray();
  desc.skeleton=System.Array.ConvertAll(transforms,t=>new SkeletonBone{name=t.name,position=t.localPosition,rotation=t.localRotation,scale=t.localScale});
  desc.upperArmTwist=.5f;desc.lowerArmTwist=.5f;desc.upperLegTwist=.5f;desc.lowerLegTwist=.5f;desc.armStretch=.05f;desc.legStretch=.05f;
  importer.humanDescription=desc;importer.SaveAndReimport();
  model=AssetDatabase.LoadAssetAtPath<GameObject>(F+"Boss_Unity.fbx");
  var avatar=model.GetComponent<Animator>()?.avatar;
  if(!avatar||!avatar.isValid||!avatar.isHuman)throw new System.InvalidOperationException("Boss humanoid mapping is invalid.");
  foreach(string suffix in new[]{"_BaseColor.png","_EmissionMask.png"})
  {
   var ti=(TextureImporter)AssetImporter.GetAtPath(F+"Boss"+suffix);ti.sRGBTexture=suffix.StartsWith("_BaseColor");
   ti.textureType=TextureImporterType.Default;ti.mipmapEnabled=true;ti.wrapMode=TextureWrapMode.Clamp;ti.maxTextureSize=2048;ti.textureCompression=TextureImporterCompression.CompressedHQ;ti.SaveAndReimport();
  }
  var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/WeaponPolish/Shaders/WeaponStylizedWire.shader");
  if(!shader||ShaderUtil.ShaderHasError(shader))throw new System.InvalidOperationException("Wire shader unavailable.");
  var mat=AssetDatabase.LoadAssetAtPath<Material>(F+"M_Boss_StylizedWire.mat");
  if(!mat){mat=new Material(shader);AssetDatabase.CreateAsset(mat,F+"M_Boss_StylizedWire.mat");}
  mat.SetTexture("_BaseMap",AssetDatabase.LoadAssetAtPath<Texture2D>(F+"Boss_BaseColor.png"));mat.SetTexture("_EmissionMask",AssetDatabase.LoadAssetAtPath<Texture2D>(F+"Boss_EmissionMask.png"));
  mat.SetColor("_BaseColor",Color.white);mat.SetColor("_WireTint",Color.black);mat.SetColor("_EmissionColor",new Color(1,.015f,.006f));
  mat.SetFloat("_WireStrength",1);mat.SetFloat("_WireThickness",.5f);mat.SetFloat("_FaceWireStrength",1);mat.SetFloat("_EmissionStrength",1.25f);mat.SetFloat("_ShadowStrength",.25f);EditorUtility.SetDirty(mat);
  var instance=Object.Instantiate(model);instance.name="BossVisual";
  var smr=instance.GetComponentInChildren<SkinnedMeshRenderer>();var src=smr.sharedMesh;var uv=src.uv;var verts=src.vertices;var normals=src.normals;var weights=src.boneWeights;var tris=src.triangles;
  var topology=JsonUtility.FromJson<Topology>(AssetDatabase.LoadAssetAtPath<TextAsset>(F+"Boss_Topology.json").text);
  var points=new System.Collections.Generic.HashSet<string>(topology.points);var edges=new System.Collections.Generic.HashSet<string>(topology.edges);
  int matches=0;foreach(var u in uv)if(points.Contains(Key(u)))matches++;
  if(matches<uv.Length*.997f)throw new System.InvalidOperationException("UV metadata mismatch "+matches+"/"+uv.Length);
  var vp=new Vector3[tris.Length];var np=new Vector3[tris.Length];var up=new Vector2[tris.Length];var wp=new BoneWeight[tris.Length];var col=new Color[tris.Length];var ids=new int[tris.Length];int hidden=0;
  for(int i=0;i<tris.Length;i+=3)
  {
   var keys=new[]{Key(uv[tris[i]]),Key(uv[tris[i+1]]),Key(uv[tris[i+2]])};int bits=0;
   if(edges.Contains(Edge(keys[1],keys[2])))bits|=1;else hidden++;
   if(edges.Contains(Edge(keys[2],keys[0])))bits|=2;else hidden++;
   if(edges.Contains(Edge(keys[0],keys[1])))bits|=4;else hidden++;
   for(int c=0;c<3;c++){int id=tris[i+c];vp[i+c]=verts[id];np[i+c]=normals[id];up[i+c]=uv[id];wp[i+c]=weights[id];col[i+c]=new Color(c==0?1:0,c==1?1:0,bits,1);ids[i+c]=i+c;}
  }
  var wire=new Mesh{name="Boss original polygon wire (derived)",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
  wire.vertices=vp;wire.normals=np;wire.uv=up;wire.boneWeights=wp;wire.bindposes=src.bindposes;wire.colors=col;wire.triangles=ids;wire.bounds=src.bounds;
  wire=MeshAssetWriter.Write(wire,F+"Boss_Wire.asset");
  smr.sharedMesh=wire;smr.sharedMaterial=mat;smr.updateWhenOffscreen=false;smr.localBounds=src.bounds;
  var animator=instance.GetComponent<Animator>();animator.runtimeAnimatorController=AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/_Project/Animations/BossLordBase.controller");animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
  var fg=instance.AddComponent<FootGrounding>();var fso=new SerializedObject(fg);fso.FindProperty("pelvisOffsetMax").floatValue=.8f;fso.FindProperty("attackPelvisOffsetMax").floatValue=.9f;fso.ApplyModifiedPropertiesWithoutUndo();
  InstallSword(animator);PrefabUtility.SaveAsPrefabAsset(instance,F+"BossVisual.prefab");Object.DestroyImmediate(instance);AssetDatabase.SaveAssets();
  System.IO.File.WriteAllText(O+"/Unity_Import.txt","Avatar valid="+avatar.isValid+" human="+avatar.isHuman+"\nUV points matched="+matches+"/"+uv.Length+"\nTriangles="+tris.Length/3+" derived vertices="+wire.vertexCount+" hidden triangulation sides="+hidden+"\nSource mesh and UV unchanged. Existing BossLord controller retained.");
  Debug.Log("Boss prefab ready: valid humanoid, original polygon wire, red emission.");
 }

 [MenuItem("Tools/Boss Polish/2 Replace current Dark Lord visual")]
 public static void Replace()
 {
  if(EditorApplication.isPlaying)throw new System.InvalidOperationException("Exit Play Mode first.");
  var current=UnityEngine.SceneManagement.SceneManager.GetActiveScene();if(current.isDirty)throw new System.InvalidOperationException("Save the scene first.");
  Selection.activeObject=null;System.IO.Directory.CreateDirectory(O+"/BeforeInstall");
  var report=new System.Collections.Generic.List<string>();
  foreach(string name in new[]{"00_TestBlockout","01_Courtyard","02_FoundryTutorial","03_SunkenVault"})
  {
   string path="Assets/_Project/Scenes/"+name+".unity";if(!System.IO.File.Exists(path))continue;
   string backup=O+"/BeforeInstall/"+name+".unity";if(!System.IO.File.Exists(backup))System.IO.File.Copy(path,backup);
   var scene=path==current.path?current:EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);int count=0;
   foreach(var root in scene.GetRootGameObjects())foreach(var boss in root.GetComponentsInChildren<BossLord>(true))
   {
    var old=boss.transform.Find("DarkLordVisual");var existing=boss.transform.Find("BossVisual");
    if(!existing)
    {
     var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(F+"BossVisual.prefab");if(!prefab)throw new System.InvalidOperationException("Build the boss first.");
     var visual=(GameObject)PrefabUtility.InstantiatePrefab(prefab,boss.transform);visual.name="BossVisual";
     var cc=boss.GetComponent<CharacterController>();visual.transform.localScale=Vector3.one*(cc?cc.height/2.4f:1);
     visual.transform.localPosition=new Vector3(0,cc?cc.center.y-cc.height*.5f:0,0);visual.transform.localRotation=Quaternion.identity;
     var anim=visual.GetComponent<Animator>();var oldAnim=old?old.GetComponentInChildren<Animator>(true):null;
     if(oldAnim)anim.runtimeAnimatorController=oldAnim.runtimeAnimatorController;
     anim.applyRootMotion=true;if(!anim.GetComponent<RootMotionRelay>())anim.gameObject.AddComponent<RootMotionRelay>();
     var so=new SerializedObject(boss);so.FindProperty("bossAnimator").objectReferenceValue=anim;so.ApplyModifiedPropertiesWithoutUndo();
     if(old)old.gameObject.SetActive(false);existing=visual.transform;
    }
    InstallSword(existing.GetComponent<Animator>());EditorUtility.SetDirty(boss);count++;
   }
   if(count>0){EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);}
   report.Add(name+": "+count+" Dark Lord bosses replaced; original visual retained disabled.");
   if(scene.path!=current.path)EditorSceneManager.CloseScene(scene,true);
  }
  AssetDatabase.SaveAssets();System.IO.File.WriteAllLines(O+"/Boss_Installation.txt",report);Debug.Log("Boss replacement complete; gameplay brain, phases and colliders preserved.");
 }
 static Texture2D LoadReadable(string p){var t=new Texture2D(2,2);t.LoadImage(System.IO.File.ReadAllBytes(p));return t;}
 static void InstallSword(Animator anim)
 {
  var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(F+"BossSword.prefab");if(!prefab)return;
  var hand=anim.GetBoneTransform(HumanBodyBones.RightHand);if(!hand)return;
  var found=hand.Find("BossSword");var sword=found?found.gameObject:(GameObject)PrefabUtility.InstantiatePrefab(prefab,hand);sword.name="BossSword";
  for(int i=hand.childCount-1;i>=0;i--){var child=hand.GetChild(i);if(child.name=="BossSword"&&child.gameObject!=sword)Object.DestroyImmediate(child.gameObject);}
  float unitScale=anim.transform.lossyScale.x/hand.lossyScale.x;
  sword.transform.localScale=Vector3.one*unitScale;Debug.Log("[BossPolish] "+BossRigRepair.FitSword(anim,sword.transform)); // measured fist grip, not a guessed offset
 }
 [MenuItem("Tools/Boss Polish/3 Inspect boss gameplay")]
 static void Inspect()
 {
  if(!EditorApplication.isPlaying)throw new System.InvalidOperationException("Enter Play Mode first.");
  var boss=Object.FindFirstObjectByType<BossLord>();if(!boss)throw new System.InvalidOperationException("No Dark Lord in scene.");
  var player=Object.FindFirstObjectByType<PlayerLocomotion>();var cc=player.GetComponent<CharacterController>();cc.enabled=false;player.transform.position=boss.transform.position+boss.transform.forward*5;cc.enabled=true;
  var cam=Object.FindFirstObjectByType<PlayerCameraController>();var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
  if(cam){typeof(PlayerCameraController).GetField("yaw",flags)?.SetValue(cam,boss.transform.eulerAngles.y+180);typeof(PlayerCameraController).GetField("pitch",flags)?.SetValue(cam,18f);}
  boss.Engage();Selection.activeGameObject=boss.gameObject;
  var a=boss.transform.Find("BossVisual").GetComponent<Animator>();System.IO.File.WriteAllText(O+"/Boss_Live.txt","Humanoid valid="+a.avatar.isValid+" controller="+AssetDatabase.GetAssetPath(a.runtimeAnimatorController));
 }
}


public static class BossSwordSetup
{
 const string F="Assets/_Project/BossPolish/";
 [System.Serializable] class Topology { public string[] edges=System.Array.Empty<string>(),points=System.Array.Empty<string>(); }
 static string Key(Vector2 u)=>Mathf.RoundToInt(u.x*10000)+","+Mathf.RoundToInt(u.y*10000);
 static string Edge(string a,string b)=>string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;
 [MenuItem("Tools/Boss Polish/4 Build crimson greatsword")]
 public static void Build()
 {
  if(EditorApplication.isPlaying)throw new System.InvalidOperationException("Exit Play Mode first.");
  var imp=(ModelImporter)AssetImporter.GetAtPath(F+"Sword_Painted.fbx");imp.isReadable=true;imp.globalScale=1.9f;imp.importAnimation=false;imp.materialImportMode=ModelImporterMaterialImportMode.None;imp.meshCompression=ModelImporterMeshCompression.Off;imp.SaveAndReimport();
  foreach(string suffix in new[]{"_BaseColor.png","_EmissionMask.png"}){var ti=(TextureImporter)AssetImporter.GetAtPath(F+"Sword"+suffix);ti.sRGBTexture=suffix.StartsWith("_BaseColor");ti.mipmapEnabled=true;ti.wrapMode=TextureWrapMode.Clamp;ti.maxTextureSize=2048;ti.textureCompression=TextureImporterCompression.CompressedHQ;ti.SaveAndReimport();}
  var model=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(F+"Sword_Painted.fbx"));var mf=model.GetComponentInChildren<MeshFilter>();var src=mf.sharedMesh;var uv=src.uv;var v=src.vertices;var n=src.normals;var tris=src.triangles;
  var topo=JsonUtility.FromJson<Topology>(AssetDatabase.LoadAssetAtPath<TextAsset>(F+"Sword_Topology.json").text);var edges=new System.Collections.Generic.HashSet<string>(topo.edges);var points=new System.Collections.Generic.HashSet<string>(topo.points);int matches=0;foreach(var q in uv)if(points.Contains(Key(q)))matches++;
  if(matches<uv.Length*.997f)throw new System.InvalidOperationException("Sword UV mismatch");
  var bounds=new Bounds(mf.transform.TransformPoint(v[0]),Vector3.zero);foreach(var q in v)bounds.Encapsulate(mf.transform.TransformPoint(q));var grip=new Vector3(bounds.center.x,bounds.min.y+bounds.size.y*.215f,bounds.center.z);
  var vp=new Vector3[tris.Length];var np=new Vector3[tris.Length];var up=new Vector2[tris.Length];var col=new Color[tris.Length];var ids=new int[tris.Length];int hidden=0;
  for(int i=0;i<tris.Length;i+=3){var keys=new[]{Key(uv[tris[i]]),Key(uv[tris[i+1]]),Key(uv[tris[i+2]])};int bits=0;
   if(edges.Contains(Edge(keys[1],keys[2])))bits|=1;else hidden++;if(edges.Contains(Edge(keys[2],keys[0])))bits|=2;else hidden++;if(edges.Contains(Edge(keys[0],keys[1])))bits|=4;else hidden++;
   for(int c=0;c<3;c++){int id=tris[i+c];vp[i+c]=mf.transform.TransformPoint(v[id])-grip;np[i+c]=mf.transform.TransformDirection(n[id]).normalized;up[i+c]=uv[id];col[i+c]=new Color(c==0?1:0,c==1?1:0,bits,1);ids[i+c]=i+c;}}
  var mesh=new Mesh{name="Crimson greatsword polygon wire (derived)",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};mesh.vertices=vp;mesh.normals=np;mesh.uv=up;mesh.colors=col;mesh.triangles=ids;mesh.RecalculateBounds();mesh=MeshAssetWriter.Write(mesh,F+"Sword_Wire.asset");
  var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/WeaponPolish/Shaders/WeaponStylizedWire.shader");var mat=AssetDatabase.LoadAssetAtPath<Material>(F+"M_BossSword_Wire.mat");if(!mat){mat=new Material(shader);AssetDatabase.CreateAsset(mat,F+"M_BossSword_Wire.mat");}
  mat.SetTexture("_BaseMap",AssetDatabase.LoadAssetAtPath<Texture2D>(F+"Sword_BaseColor.png"));mat.SetTexture("_EmissionMask",AssetDatabase.LoadAssetAtPath<Texture2D>(F+"Sword_EmissionMask.png"));mat.SetColor("_BaseColor",Color.white);mat.SetColor("_WireTint",Color.black);mat.SetColor("_EmissionColor",new Color(1,.006f,.012f));mat.SetFloat("_WireStrength",1);mat.SetFloat("_WireThickness",.5f);mat.SetFloat("_FaceWireStrength",1);mat.SetFloat("_EmissionStrength",1.1f);mat.SetFloat("_ShadowStrength",.25f);EditorUtility.SetDirty(mat);
  var root=new GameObject("BossSword");root.AddComponent<MeshFilter>().sharedMesh=mesh;root.AddComponent<MeshRenderer>().sharedMaterial=mat;PrefabUtility.SaveAsPrefabAsset(root,F+"BossSword.prefab");Object.DestroyImmediate(root);Object.DestroyImmediate(model);AssetDatabase.SaveAssets();
  System.IO.File.WriteAllText(@"C:/Users/milkw/Documents/Codex/2026-10-04/open-my-blender-character-project-and/outputs/BossSword/Unity_Import.txt","Original source and UV preserved; grip pivot on derived mesh.\nUV matches="+matches+"/"+uv.Length+" triangles="+tris.Length/3+" hidden diagonal sides="+hidden+"\nHeight="+bounds.size.y+" grip="+grip);
 }
}
/// <summary>
/// Repairs the custom boss rig in Unity (Blender sources untouched):
/// 1. Skirt weights — the long crimson skirt was skinned 100% to the shin bones,
///    so every stride dragged skirt panels with the lower legs and tore them into
///    spikes (Animation_Audit max edge stretch 30–65×). Skirt vertices (below the
///    hips, well outside the leg bone's own radius, or painted red) are rebuilt
///    pelvis-led: upper skirt follows Hips, lower skirt takes up to 55% from the
///    thighs split left/right — never the shins.
/// 2. Sword grip — seated from the actual hand instead of a guessed offset: the
///    rig has no finger bones, so the palm plane comes from the hand's own
///    vertices (thinnest axis), the forearm gives knuckle-forward, the fist is the
///    hand-vertex centroid; blade along the fist's width (thumb side up), edge forward.
/// Axes are derived from the rig (feet→head, right hip→left hip), never assumed.
/// </summary>
/// <summary>Writes a generated mesh into an existing .asset by rebuilding it through the
/// Mesh API (Clear + every channel), keeping the asset's GUID. EditorUtility.CopySerialized
/// was used before: a raw field copy that leaves the vertex layout out of sync when the
/// vertex count changes — a SkinnedMeshRenderer then refuses to draw ("mesh data size and
/// vertex stride" mismatch).</summary>
public static class MeshAssetWriter
{
 public static Mesh Write(Mesh src,string path)
 {
  var dst=AssetDatabase.LoadAssetAtPath<Mesh>(path);
  if(!dst){AssetDatabase.CreateAsset(src,path);return src;}
  dst.Clear();
  dst.indexFormat=src.indexFormat;
  dst.name=src.name;
  dst.vertices=src.vertices;
  if(src.normals.Length==src.vertexCount)dst.normals=src.normals;
  if(src.uv.Length==src.vertexCount)dst.uv=src.uv;
  // Extra UV channels carry shader data (Mechanical Environment: UV3 barycentrics, UV4 crease mask).
  var extra=new System.Collections.Generic.List<Vector4>();
  for(int ch=1;ch<8;ch++){extra.Clear();src.GetUVs(ch,extra);if(extra.Count==src.vertexCount)dst.SetUVs(ch,extra);}
  if(src.colors.Length==src.vertexCount)dst.colors=src.colors;
  if(src.boneWeights.Length==src.vertexCount)dst.boneWeights=src.boneWeights;
  if(src.bindposes.Length>0)dst.bindposes=src.bindposes;
  dst.subMeshCount=src.subMeshCount;
  for(int i=0;i<src.subMeshCount;i++)dst.SetTriangles(src.GetTriangles(i),i,false);
  dst.bounds=src.bounds;
  EditorUtility.SetDirty(dst);
  if(src!=dst)Object.DestroyImmediate(src);
  return dst;
 }
}

public static class BossRigRepair
{
 const string F="Assets/_Project/BossPolish/";

 [MenuItem("Tools/Boss Polish/6 Refit boss sword")]
 public static void Repair()
 {
  if(EditorApplication.isPlaying)throw new System.InvalidOperationException("Exit Play Mode first.");
  if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
  Selection.activeObject=null;
  var prefabPath=F+"BossVisual.prefab";
  var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(F+"Boss_Wire.asset");
  var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
  if(!mesh||!prefab)throw new System.InvalidOperationException("Run Boss Polish 1 first (Boss_Wire.asset / BossVisual.prefab missing).");
  var smr=prefab.GetComponentInChildren<SkinnedMeshRenderer>();
  var names=System.Array.ConvertAll(smr.bones,b=>b?b.name:"");
  var log=new System.Collections.Generic.List<string>();
  // Skin weights are now fixed at the source (Tools/BossRerig/rerig_boss.py) — no runtime reweight.
  EditorUtility.SetDirty(mesh);AssetDatabase.SaveAssets();

  // Prefab asset: refit the sword in its rest pose.
  var contents=PrefabUtility.LoadPrefabContents(prefabPath);
  try{var a=contents.GetComponentInChildren<Animator>();var s=a?a.GetBoneTransform(HumanBodyBones.RightHand)?.Find("BossSword"):null;if(s)log.Add("prefab: "+FitSword(a,s));PrefabUtility.SaveAsPrefabAsset(contents,prefabPath);}
  finally{PrefabUtility.UnloadPrefabContents(contents);}

  // Scene instances (overrides) — same refit, scenes saved.
  var current=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
  foreach(string name in new[]{"00_TestBlockout","01_Courtyard","02_FoundryTutorial","03_SunkenVault"})
  {
   string path="Assets/_Project/Scenes/"+name+".unity";if(!System.IO.File.Exists(path))continue;
   var scene=path==current.path?current:EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);int count=0;
   foreach(var root in scene.GetRootGameObjects())foreach(var boss in root.GetComponentsInChildren<BossLord>(true))
   {
    var vis=boss.transform.Find("BossVisual");var a=vis?vis.GetComponent<Animator>():null;
    var s=a?a.GetBoneTransform(HumanBodyBones.RightHand)?.Find("BossSword"):null;
    if(s){log.Add(name+": "+FitSword(a,s));count++;}
   }
   if(count>0){EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);}
   if(scene.path!=current.path)EditorSceneManager.CloseScene(scene,true);
  }
  Debug.Log("[BossRigRepair]\n"+string.Join("\n",log)+"\nRun Boss Polish 5 (Audit) to re-measure edge stretch.");
 }

 static Texture2D LoadBaseColor()
 {
  var p=F+"Boss_BaseColor.png";if(!System.IO.File.Exists(p))return null;
  var t=new Texture2D(2,2);t.LoadImage(System.IO.File.ReadAllBytes(p));return t; // readable copy; importer untouched
 }

 /// <summary>Rebuilds pelvis-led weights for skirt vertices. Returns a log line.</summary>
 internal static string ReweightSkirt(Mesh mesh,string[] bones,Texture2D baseColor)
 {
  int I(string n)=>System.Array.IndexOf(bones,n);
  int hips=I("Hips"),ulL=I("LeftUpperLeg"),ulR=I("RightUpperLeg"),llL=I("LeftLowerLeg"),llR=I("RightLowerLeg"),ftL=I("LeftFoot"),ftR=I("RightFoot"),head=I("Head");
  if(hips<0||ulL<0||ulR<0||llL<0||llR<0||ftL<0||ftR<0||head<0)return "skirt reweight skipped: humanoid leg bones not found";
  var bind=mesh.bindposes;Vector3 O(int b)=>bind[b].inverse.MultiplyPoint3x4(Vector3.zero);
  Vector3 hipsP=O(hips),hipL=O(ulL),hipR=O(ulR),kneeL=O(llL),kneeR=O(llR),ankL=O(ftL),ankR=O(ftR),headP=O(head);
  var up=(headP-(ankL+ankR)*.5f).normalized; var lat=(hipL-hipR).normalized; // rig-derived axes (mesh may be Z-up)
  float Hgt(Vector3 p)=>Vector3.Dot(p,up);
  float hipsH=Hgt(hipsP),kneeH=(Hgt(kneeL)+Hgt(kneeR))*.5f,height=Hgt(headP)-Hgt((ankL+ankR)*.5f);
  float SegDist(Vector3 p,Vector3 a,Vector3 b){var ab=b-a;var t=Mathf.Clamp01(Vector3.Dot(p-a,ab)/Mathf.Max(1e-6f,ab.sqrMagnitude));return Vector3.Distance(p,a+ab*t);}
  float Dist(int bone,Vector3 p)=>bone==ulL?SegDist(p,hipL,kneeL):bone==ulR?SegDist(p,hipR,kneeR):bone==llL?SegDist(p,kneeL,ankL):bone==llR?SegDist(p,kneeR,ankR):bone==ftL?Vector3.Distance(p,ankL):Vector3.Distance(p,ankR);
  var legs=new System.Collections.Generic.HashSet<int>{ulL,ulR,llL,llR,ftL,ftR};
  var v=mesh.vertices;var w=mesh.boneWeights;var uv=mesh.uv;
  int Dom(BoneWeight b){var bi=b.boneIndex0;var bw=b.weight0;if(b.weight1>bw){bi=b.boneIndex1;bw=b.weight1;}if(b.weight2>bw){bi=b.boneIndex2;bw=b.weight2;}if(b.weight3>bw)bi=b.boneIndex3;return bi;}
  // Per-bone median distance = that limb's own radius; skirt sits well outside it.
  var per=new System.Collections.Generic.Dictionary<int,System.Collections.Generic.List<float>>();
  for(int i=0;i<v.Length;i++){var d=Dom(w[i]);if(!legs.Contains(d))continue;if(!per.TryGetValue(d,out var l))per[d]=l=new System.Collections.Generic.List<float>();l.Add(Dist(d,v[i]));}
  var med=new System.Collections.Generic.Dictionary<int,float>();foreach(var kv in per){kv.Value.Sort();med[kv.Key]=Mathf.Max(.01f*height,kv.Value[kv.Value.Count/2]);}
  bool Red(Vector2 t){if(!baseColor||uv.Length!=v.Length)return false;var c=baseColor.GetPixelBilinear(t.x,t.y);return c.r>.35f&&c.r>c.g*2f&&c.r>c.b*1.6f;}
  float latL=Vector3.Dot(hipL,lat),latR=Vector3.Dot(hipR,lat);
  int changed=0;
  for(int i=0;i<v.Length;i++)
  {
   var d=Dom(w[i]);if(!legs.Contains(d)||!med.ContainsKey(d))continue;
   if(Hgt(v[i])>hipsH+.02f*height)continue;
   var dist=Dist(d,v[i]);var m=med[d];
   bool skirt=dist>2.2f*m||(dist>1.25f*m&&Red(uv.Length==v.Length?uv[i]:Vector2.zero));
   if(!skirt)continue;
   float t=Mathf.Clamp01((hipsH-Hgt(v[i]))/Mathf.Max(1e-4f,hipsH-kneeH));
   float sL=Mathf.Clamp01(Mathf.InverseLerp(latR,latL,Vector3.Dot(v[i],lat)));
   float thigh=.55f*t;
   var entries=new[]{(hips,1f-thigh),(ulL,thigh*sL),(ulR,thigh*(1f-sL))};
   System.Array.Sort(entries,(a,b)=>b.Item2.CompareTo(a.Item2));
   float sum=entries[0].Item2+entries[1].Item2+entries[2].Item2;
   w[i]=new BoneWeight{boneIndex0=entries[0].Item1,weight0=entries[0].Item2/sum,boneIndex1=entries[1].Item1,weight1=entries[1].Item2/sum,boneIndex2=entries[2].Item1,weight2=entries[2].Item2/sum};
   changed++;
  }
  mesh.boneWeights=w;
  return $"skirt reweight: {changed}/{v.Length} vertices moved to Hips/thighs (up axis {up:F2}, leg radii {string.Join(",",System.Linq.Enumerable.Select(med,kv=>bones[kv.Key]+"="+kv.Value.ToString("F3")))})";
 }

 /// <summary>Seats the sword (pivot = grip) in the right fist from the hand's geometry.</summary>
 internal static string FitSword(Animator anim,Transform sword)
 {
  var hand=anim.GetBoneTransform(HumanBodyBones.RightHand);var fore=anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
  var smr=anim.GetComponentInChildren<SkinnedMeshRenderer>();
  if(!hand||!fore||!smr)return "sword fit skipped (hand/forearm/skin missing)";
  var forward=(hand.position-fore.position).normalized;
  // Hand vertices in world space at the rest pose.
  var baked=new Mesh();smr.BakeMesh(baked);var bv=baked.vertices;var bw=smr.sharedMesh.boneWeights;Object.DestroyImmediate(baked);
  int hi=System.Array.IndexOf(smr.bones,hand);
  var pts=new System.Collections.Generic.List<Vector3>();
  if(hi>=0)for(int i=0;i<bv.Length&&i<bw.Length;i++){var b=bw[i];float wt=b.boneIndex0==hi?b.weight0:b.boneIndex1==hi?b.weight1:b.boneIndex2==hi?b.weight2:b.boneIndex3==hi?b.weight3:0f;if(wt>=.5f)pts.Add(smr.transform.TransformPoint(bv[i]));}
  var fist=hand.position+forward*.06f*anim.transform.lossyScale.x;
  var across=Vector3.Cross(forward,Vector3.up);
  if(pts.Count>=8)
  {
   var c=Vector3.zero;foreach(var p in pts)c+=p;c/=pts.Count;fist=c;
   // Plane ⊥ forward: 2×2 covariance → thinnest axis = palm normal, the other = fist width.
   var e1=Vector3.Cross(forward,Mathf.Abs(Vector3.Dot(forward,Vector3.up))<.9f?Vector3.up:Vector3.right).normalized;var e2=Vector3.Cross(forward,e1);
   float a=0,bxy=0,cc=0;foreach(var p in pts){var q=p-c;float x=Vector3.Dot(q,e1),y=Vector3.Dot(q,e2);a+=x*x;bxy+=x*y;cc+=y*y;}
   float ang=.5f*Mathf.Atan2(2f*bxy,a-cc); // major axis angle in (e1,e2)
   across=(Mathf.Cos(ang)*e1+Mathf.Sin(ang)*e2).normalized;
  }
  // Thumb side faces the character's forward in a rest/T pose — blade tip goes that way.
  if(Vector3.Dot(across,anim.transform.forward)<0f)across=-across;
  var edge=Vector3.ProjectOnPlane(forward,across).normalized;
  // Sword mesh: long axis +Y (pivot at grip, pommel at -Y), flat width = wider of X/Z.
  var mf=sword.GetComponent<MeshFilter>();var sb=mf&&mf.sharedMesh?mf.sharedMesh.bounds:new Bounds(Vector3.up,new Vector3(.1f,2,.3f));
  var widthLocal=sb.size.x>=sb.size.z?Vector3.right:Vector3.forward;
  sword.rotation=Quaternion.LookRotation(across,edge)*Quaternion.Inverse(Quaternion.LookRotation(Vector3.up,widthLocal));
  sword.position=fist;
  return $"sword seated in {hand.name}: {pts.Count} hand verts, local pos {sword.localPosition:F3}, rot {sword.localEulerAngles:F0}";
 }
}

public static class BossRigAudit
{
 const string F="Assets/_Project/BossPolish/";
 const string O=@"C:/Users/milkw/Documents/Codex/2026-10-04/open-my-blender-character-project-and/outputs/Boss/";
 [MenuItem("Tools/Boss Polish/5 Audit animated rig")]
 public static void Audit()
 {
  if(EditorApplication.isPlaying)throw new System.InvalidOperationException("Exit Play Mode first.");
  var go=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(F+"BossVisual.prefab"));go.hideFlags=HideFlags.HideAndDontSave;go.transform.position=Vector3.zero;
  foreach(var fg in go.GetComponentsInChildren<FootGrounding>())fg.enabled=false;
  var anim=go.GetComponent<Animator>();anim.applyRootMotion=false;var hand=anim.GetBoneTransform(HumanBodyBones.RightHand);var existingSword=hand.Find("BossSword");var sword=existingSword?existingSword.gameObject:Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(F+"BossSword.prefab"),hand);float unitScale=go.transform.lossyScale.x/hand.lossyScale.x;sword.transform.localScale=Vector3.one*unitScale;BossRigRepair.FitSword(anim,sword.transform);
  var smr=go.GetComponentInChildren<SkinnedMeshRenderer>();var mesh=new Mesh();smr.BakeMesh(mesh);var original=mesh.vertices;var tris=smr.sharedMesh.triangles;var report=new System.Collections.Generic.List<string>();
  var clips=anim.runtimeAnimatorController.animationClips;AnimationMode.StartAnimationMode();
  try
  {
   foreach(var clip in clips)
   {
    float worst=0;string detail="";bool finite=true;Bounds bounds=new Bounds();
    foreach(float t in new[]{.15f,.5f,.85f})
    {
     AnimationMode.BeginSampling();AnimationMode.SampleAnimationClip(go,clip,clip.length*t);AnimationMode.EndSampling();if(t==.5f && (clip.name=="Idle_ver_A"||clip.name=="Attack_3Combo_1"||clip.name=="combo_01_2"||clip.name=="atk_energy02"))Capture(go,clip.name);smr.BakeMesh(mesh);var v=mesh.vertices;bounds=mesh.bounds;
     foreach(var q in v)finite &= !(float.IsNaN(q.x)||float.IsInfinity(q.x)||float.IsNaN(q.y)||float.IsInfinity(q.y)||float.IsNaN(q.z)||float.IsInfinity(q.z));
     for(int i=0;i<tris.Length;i+=3)for(int e=0;e<3;e++){int a=tris[i+e],b=tris[i+(e+1)%3];float len=Vector3.Distance(original[a],original[b]);if(len>.001f){float ratio=Vector3.Distance(v[a],v[b])/len;if(ratio>worst){worst=ratio;var wa=smr.sharedMesh.boneWeights[a];var wb=smr.sharedMesh.boneWeights[b];detail=" rest "+original[a]+" to "+original[b]+" posed "+v[a]+" to "+v[b]+" weights "+smr.bones[wa.boneIndex0].name+" "+wa.weight0+" / "+smr.bones[wb.boneIndex0].name+" "+wb.weight0;}}}
    }
    report.Add(clip.name+": finite="+finite+" maximum edge ratio="+worst.ToString("F2")+" baked bounds="+bounds.size+detail);
    if(!finite)throw new System.InvalidOperationException("Invalid deformation in "+clip.name);
   }
  }
  finally {AnimationMode.StopAnimationMode();Object.DestroyImmediate(mesh);Object.DestroyImmediate(go);}
  System.IO.File.WriteAllLines(O+"Animation_Audit.txt",report);Debug.Log("Boss rig sampled across all controller clips; audit saved.");
 }
 static void Capture(GameObject go,string label)
 {
  var skin=go.GetComponentInChildren<SkinnedMeshRenderer>();var baked=new Mesh();skin.BakeMesh(baked);var bg=new GameObject("Baked pose preview");bg.hideFlags=HideFlags.HideAndDontSave;bg.layer=31;bg.transform.position=skin.transform.position;bg.transform.rotation=skin.transform.rotation;bg.AddComponent<MeshFilter>().sharedMesh=baked;bg.AddComponent<MeshRenderer>().sharedMaterial=skin.sharedMaterial;skin.enabled=false;
  foreach(var tr in go.GetComponentsInChildren<Transform>())tr.gameObject.layer=31;
  var cg=new GameObject("Temporary boss inspection camera");cg.hideFlags=HideFlags.HideAndDontSave;var cam=cg.AddComponent<Camera>();cam.cullingMask=1<<31;cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=new Color(.12f,.13f,.15f);cam.orthographic=true;cam.orthographicSize=2;var target=go.transform.position+Vector3.up*1.25f;cg.transform.position=target+new Vector3(2,.7f,5);cg.transform.LookAt(target);
  var lg=new GameObject("Temporary inspection light");lg.hideFlags=HideFlags.HideAndDontSave;var light=lg.AddComponent<Light>();light.type=LightType.Directional;light.cullingMask=1<<31;light.intensity=2;lg.transform.rotation=Quaternion.Euler(40,-30,0);
  var rt=RenderTexture.GetTemporary(1000,1100,24);var old=RenderTexture.active;cam.targetTexture=rt;
  try {cam.Render();RenderTexture.active=rt;var tex=new Texture2D(1000,1100,TextureFormat.RGB24,false);tex.ReadPixels(new Rect(0,0,1000,1100),0,0);tex.Apply();System.IO.File.WriteAllBytes(O+"Unity_"+label+".png",tex.EncodeToPNG());Object.DestroyImmediate(tex);}
  finally {RenderTexture.active=old;cam.targetTexture=null;RenderTexture.ReleaseTemporary(rt);Object.DestroyImmediate(cg);Object.DestroyImmediate(lg);skin.enabled=true;Object.DestroyImmediate(bg);Object.DestroyImmediate(baked);}
 }

}

