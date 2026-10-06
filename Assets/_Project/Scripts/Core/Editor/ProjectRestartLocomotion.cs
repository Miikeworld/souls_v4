using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ProjectRestartLocomotion
{
    public const string ControllerPath = "Assets/_Project/Animators/PlayerBase.controller";
    private const float EnterTransitionDuration = 0.15f;
    private const float ExitTransitionDuration = 0.25f;
    private static readonly string[] LocomotionClipGuids =
    {
        "f616a2e271f109447b49d45d058a4084",
        "6e950ce0deb6c22419d4f264e8441f3b",
        "2052e59abeb75e741848e7102f47a677",
        "82aaa36036bdcb246bdc018a9b15e3f0"
    };
    // Idle_ver_B pairs with the ver_B jog/walk overrides — the pack notes warn
    // against mixing a ver_A idle into ver_B movement (stance snaps), and its
    // meta ships baked unlike ver_A's.
    private static readonly string[] LocomotionClipNames = { "Idle_ver_B", "Mvm_Walk", "Mvm_Jog", "Mvm_Dash" };
    private static readonly float[] Thresholds = { 0f, 1f / 3f, 2f / 3f, 1f };
    public const float DashStateSpeed = 1f;
    private const float BoostThreshold = 1.5f;
    private const string DashGuid = "313b29954b78fba42866cb5b4ac69109";
    private const string DashName = "Esc_Slide_All_Long_Root";
    private const string BoostGuid = "827eef435d7173646b237af03413ca12";
    private const string BoostName = "Mvm_Boost";

    [MenuItem("Tools/Project Restart/Step 5 - Build and Verify PlayerBase")]
    public static void BuildAndVerify()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Step 5 deferred: exit Play Mode and wait for compilation/import to finish, then run again.");
            return;
        }

        try
        {
            var (locomotion, dash, boost) = LoadClips();
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Require(AssetDatabase.IsValidFolder("Assets/_Project/Animators"), "Run folder setup first.");
                Require(!File.Exists(ControllerPath) && !Directory.Exists(ControllerPath) && !File.Exists(ControllerPath + ".meta"), "Controller path is occupied; refusing to overwrite it.");
                controller = Build(locomotion, dash, boost);
            }
            else if (!ControllerMatches(controller, locomotion, dash, boost))
            {
                Debug.LogWarning("[ProjectRestart] Step 5: existing PlayerBase.controller does not match the expected structure. Step 5 does not overwrite existing controllers. Delete it manually and run again if you want a rebuild.");
                return;
            }

            ProjectRestartJumpStates.Ensure(controller);
            VerifyStructure(controller, locomotion, dash, boost);
            VerifyPlayback(controller, locomotion, dash, boost);
            AssetDatabase.SaveAssets();
            Selection.activeObject = controller;
            EditorGUIUtility.PingObject(controller);
            Debug.Log("[ProjectRestart] Step 5 PASS: " + ControllerPath + "; one Base Layer; Locomotion 1D blend tree uses Speed Float at 0=" + LocomotionClipNames[0] + ", 1/3=" + LocomotionClipNames[1] + " (slow walk), 2/3=" + LocomotionClipNames[2] + " (walk), 1=" + LocomotionClipNames[3] + " (run), " + BoostThreshold + "=" + BoostName + " (slide run boost); Dash Trigger enters " + DashName + " and returns after one cycle. Verified blend endpoints, Dash entry/exit, and no character-root displacement in a temporary Hero preview with Apply Root Motion disabled. Source clips have root rotation/Y/XZ baked into pose; no vendor assets changed. Speed is normalized, not metres/second. Step 6 must set Animator.applyRootMotion=false and calibrate movement to stride speed; no scene, movement script, or additional layers were created.", controller);
        }
        catch (Exception exception)
        {
            Debug.LogError("[ProjectRestart] Step 5 FAIL: " + exception.Message + " Existing controller assets are not automatically overwritten or deleted.");
            Debug.LogException(exception);
        }
    }

    private static (AnimationClip[] locomotion, AnimationClip dash, AnimationClip boost) LoadClips()
    {
        var locomotion = new AnimationClip[LocomotionClipGuids.Length];
        locomotion[0] = LoadBakedKatanaIdle();
        for (var index = 1; index < locomotion.Length; index++)
            locomotion[index] = LoadHumanoidClip(LocomotionClipGuids[index], LocomotionClipNames[index], true);
        var dash = LoadHumanoidClip(DashGuid, DashName, false);
        var boost = LoadHumanoidClip(BoostGuid, BoostName, true);
        return (locomotion, dash, boost);
    }

    /// <summary>The katana idle FBX ships with loopBlendPositionY/XZ unbaked
    /// and the per-file avatar — the strict loader would fail it, so run the
    /// shared root-config pass first (meta flags + canonical Grruzam avatar,
    /// same as every working katana clip), then re-validate. NEVER run this
    /// on the CLazy locomotion clips — they own their avatar.</summary>
    private static AnimationClip LoadBakedKatanaIdle()
    {
        var path = AssetDatabase.GUIDToAssetPath(LocomotionClipGuids[0]);
        Require(!string.IsNullOrEmpty(path), "Missing katana idle source: " + LocomotionClipNames[0]);
        var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
            .SingleOrDefault(item => item.name == LocomotionClipNames[0]);
        Require(clip != null, "Idle clip not found in " + path + ": " + LocomotionClipNames[0]);
        clip = ProjectRestartCombat.EnsureClipYBake(clip);
        return LoadHumanoidClip(LocomotionClipGuids[0], LocomotionClipNames[0], true);
    }

    [MenuItem("Tools/Project Restart/Swap Idle Clip")]
    public static void SwapIdleClip()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Swap Idle Clip deferred: exit Play Mode and wait for compilation/import to finish, then run again.");
            return;
        }
        try
        {
            var (locomotion, dash, boost) = LoadClips();
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            Require(controller != null, "PlayerBase.controller missing — run Step 5 first.");
            var machine = controller.layers[0].stateMachine;
            var locomotionState = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "Locomotion");
            var tree = locomotionState != null ? locomotionState.motion as BlendTree : null;
            Require(tree != null && tree.name == "Idle Walk Run" && tree.children.Length == 5, "Idle Walk Run blend tree not found under Locomotion.");
            var children = tree.children;
            children[0].motion = locomotion[0];
            tree.children = children;
            EditorUtility.SetDirty(tree);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            VerifyStructure(controller, locomotion, dash, boost);
            VerifyPlayback(controller, locomotion, dash, boost);
            Debug.Log("[ProjectRestart] Swap Idle Clip PASS: locomotion idle → " + LocomotionClipNames[0] +
                      " (katana stance, baked). Run Souls Self-Checks + Grounding Self-Test, then verify the idle pose in Play Mode.", controller);
        }
        catch (Exception exception)
        {
            Debug.LogError("[ProjectRestart] Swap Idle Clip FAIL: " + exception.Message);
            Debug.LogException(exception);
        }
    }

    private static AnimationClip LoadHumanoidClip(string guid, string name, bool mustLoop)
    {
        var path = AssetDatabase.GUIDToAssetPath(guid);
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        Require(importer != null && importer.animationType == ModelImporterAnimationType.Human, "Missing Humanoid source: " + name);
        var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().SingleOrDefault(item => item.name == name);
        Require(clip != null && clip.isHumanMotion && !clip.legacy && clip.length > 0, "Invalid Humanoid clip: " + name);
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        Require(settings.loopBlendOrientation && settings.loopBlendPositionY && settings.loopBlendPositionXZ, "Root motion is not fully baked into pose: " + clip.name);
        if (mustLoop)
            Require(settings.loopTime, "Locomotion clip must loop: " + clip.name);
        return clip;
    }

    private static AnimatorController Build(AnimationClip[] locomotion, AnimationClip dash, AnimationClip boost)
    {
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        Require(controller != null, "Could not create PlayerBase.controller.");
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("Dash", AnimatorControllerParameterType.Trigger);
        var layers = controller.layers;
        layers[0].defaultWeight = 1f;
        controller.layers = layers;
        var machine = layers[0].stateMachine;
        var locomotionState = machine.AddState("Locomotion", new Vector3(250, 100));
        var dashState = machine.AddState("Dash", new Vector3(500, 100));
        machine.defaultState = locomotionState;
        locomotionState.writeDefaultValues = false;
        dashState.writeDefaultValues = false;
        locomotionState.iKOnFeet = true;
        dashState.iKOnFeet = true;
        dashState.speed = DashStateSpeed;
        dashState.motion = dash;
        dashState.tag = "Dash";
        var tree = new BlendTree
        {
            name = "Idle Walk Run",
            blendType = BlendTreeType.Simple1D,
            blendParameter = "Speed",
            useAutomaticThresholds = false,
            minThreshold = 0f,
            maxThreshold = BoostThreshold
        };
        AssetDatabase.AddObjectToAsset(tree, controller);
        for (var index = 0; index < Thresholds.Length; index++)
            tree.AddChild(locomotion[index], Thresholds[index]);
        tree.AddChild(boost, BoostThreshold);
        locomotionState.motion = tree;

        var enterDash = locomotionState.AddTransition(dashState);
        enterDash.hasExitTime = false;
        enterDash.hasFixedDuration = true;
        enterDash.duration = EnterTransitionDuration;
        enterDash.canTransitionToSelf = false;
        enterDash.AddCondition(AnimatorConditionMode.If, 0f, "Dash");
        var exitDash = dashState.AddTransition(locomotionState);
        exitDash.hasExitTime = true;
        exitDash.exitTime = 1f;
        exitDash.hasFixedDuration = true;
        exitDash.duration = ExitTransitionDuration;
        exitDash.canTransitionToSelf = false;

        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(ControllerPath))
            EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(controller);
        return controller;
    }

    private static bool ControllerMatches(AnimatorController controller, AnimationClip[] locomotion, AnimationClip dash, AnimationClip boost)
    {
        try
        {
            VerifyStructure(controller, locomotion, dash, boost);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void VerifyStructure(AnimatorController controller, AnimationClip[] locomotion, AnimationClip dash, AnimationClip boost)
    {
        Require(controller.layers.Length >= 1 && controller.layers.Length <= 2 &&
                controller.layers[0].name == "Base Layer", "Expected Base Layer (+ optional masked UpperBody layer).");
        if (controller.layers.Length == 2)
        {
            var upper = controller.layers[1];
            Require(upper.name == "UpperBody" && upper.blendingMode == AnimatorLayerBlendingMode.Override &&
                    upper.avatarMask != null, "The second layer must be the masked UpperBody override layer (drink).");
        }
        var parameters = controller.parameters;
        Require(parameters.Any(item => item.name == "Speed" && item.type == AnimatorControllerParameterType.Float) &&
                parameters.Any(item => item.name == "Dash" && item.type == AnimatorControllerParameterType.Trigger),
                "Expected Speed Float and Dash Trigger parameters.");
        var machine = controller.layers[0].stateMachine;
        Require(machine.stateMachines.Length == 0 && machine.anyStateTransitions.Length == 0 && machine.entryTransitions.Length == 0, "Expected a flat state machine without submachines.");
        var locomotionState = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "Locomotion");
        var dashState = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "Dash");
        Require(locomotionState != null && dashState != null && machine.defaultState == locomotionState && dashState.motion == dash, "Incorrect default state or Dash motion.");
        var tree = locomotionState.motion as BlendTree;
        Require(tree != null && tree.blendType == BlendTreeType.Simple1D && tree.blendParameter == "Speed" && !tree.useAutomaticThresholds && tree.children.Length == 5, "Expected a five-child 1D Speed blend tree.");
        for (var index = 0; index < Thresholds.Length; index++)
            Require(tree.children[index].motion == locomotion[index] && Mathf.Approximately(tree.children[index].threshold, Thresholds[index]) && Mathf.Approximately(tree.children[index].timeScale, 1f), "Incorrect locomotion clip, threshold, or playback speed.");
        Require(tree.children[4].motion == boost && Mathf.Approximately(tree.children[4].threshold, BoostThreshold) && Mathf.Approximately(tree.children[4].timeScale, 1f), "Incorrect boost clip, threshold, or playback speed.");
        var enter = locomotionState.transitions.SingleOrDefault(item => item.destinationState == dashState);
        var exit = dashState.transitions.SingleOrDefault(item => item.destinationState == locomotionState);
        Require(enter != null && !enter.hasExitTime && enter.conditions.Length == 1 && enter.conditions[0].parameter == "Dash" && enter.conditions[0].mode == AnimatorConditionMode.If, "Incorrect Dash trigger transition.");
        Require(exit != null && exit.hasExitTime && Mathf.Approximately(exit.exitTime, 1f) && exit.conditions.Length == 0, "Dash must return to locomotion after one cycle without a condition.");
        Require(!locomotionState.writeDefaultValues && !dashState.writeDefaultValues && locomotionState.behaviours.Length == 0 && dashState.behaviours.Length == 0 && machine.behaviours.Length == 0, "Unexpected write-defaults or behaviours.");
        Require(Mathf.Approximately(dashState.speed, DashStateSpeed), "Dash state playback speed must match the configured multiplier.");
    }

    private static void VerifyPlayback(AnimatorController controller, AnimationClip[] locomotion, AnimationClip dash, AnimationClip boost)
    {
        var heroPath = AssetDatabase.GUIDToAssetPath("071b4da7cfff18347a371c677585b749");
        var hero = AssetDatabase.LoadAssetAtPath<GameObject>(heroPath);
        Require(hero != null, "Fantasy Hero preset could not be found.");
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var instance = PrefabUtility.InstantiatePrefab(hero, scene) as GameObject;
            Require(instance != null, "Could not instantiate the Hero preview.");
            var animator = instance.GetComponent<Animator>();
            Require(animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman, "Hero needs a valid Humanoid Avatar.");
            animator.applyRootMotion = false;
            animator.fireEvents = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = controller;
            animator.Rebind();
            animator.Update(0f);
            var position = instance.transform.position;
            var rotation = instance.transform.rotation;
            for (var index = 0; index < Thresholds.Length; index++)
            {
                animator.SetFloat("Speed", Thresholds[index]);
                animator.Update(1f / 60f);
                Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Locomotion"), "Expected Locomotion at Speed " + Thresholds[index]);
                Require(animator.GetCurrentAnimatorClipInfo(0).Any(info => info.clip == locomotion[index] && info.weight > 0.99f), "Incorrect blend result at Speed " + Thresholds[index]);
            }

            animator.SetFloat("Speed", BoostThreshold);
            animator.Update(1f / 60f);
            Require(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Locomotion"), "Expected Locomotion at Speed " + BoostThreshold + ".");
            Require(animator.GetCurrentAnimatorClipInfo(0).Any(info => info.clip == boost && info.weight > 0.99f), "Incorrect blend result at Speed " + BoostThreshold + ".");

            animator.SetTrigger("Dash");
            var enteredDash = false;
            var returnedToLocomotion = false;
            var frames = Mathf.CeilToInt((dash.length / DashStateSpeed + 1f) * 60f);
            for (var frame = 0; frame < frames; frame++)
            {
                animator.Update(1f / 60f);
                var state = animator.GetCurrentAnimatorStateInfo(0);
                enteredDash |= state.IsName("Base Layer.Dash");
                returnedToLocomotion |= enteredDash && state.IsName("Base Layer.Locomotion") && !animator.IsInTransition(0);
                Require(Vector3.Distance(position, instance.transform.position) < 0.001f && Quaternion.Angle(rotation, instance.transform.rotation) < 0.01f, "Animation displaced the character root.");
            }
            Require(enteredDash && returnedToLocomotion && animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Locomotion"), "Dash did not enter and return to locomotion.");
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
