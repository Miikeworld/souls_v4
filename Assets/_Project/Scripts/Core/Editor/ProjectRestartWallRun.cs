using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Adds WallRunLeft/WallRunRight states and the transitions needed to enter them from
/// the air and return to locomotion or jump air. Run after Step 5 and Add Jump/Crouch States.
/// </summary>
public static class ProjectRestartWallRun
{
    public const string WallRunLeftClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P6_CLazyWallRun/WallRun_Low_Dash/CLazy@LoWall_LDash_Loop.FBX";
    public const string WallRunRightClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P6_CLazyWallRun/WallRun_Low_Dash/CLazy@LoWall_RDash_Loop.FBX";
    public const string WallRunLeftStartClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P6_CLazyWallRun/WallRun_Low_Dash/CLazy@LoWall_LDash_Start_Root.FBX";
    public const string WallRunRightStartClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P6_CLazyWallRun/WallRun_Low_Dash/CLazy@LoWall_RDash_Start_Root.FBX";

    private const float WallRunEnterDuration = 0.05f;

    [MenuItem("Tools/Project Restart/Add Wall Run States")]
    public static void BuildAndVerify()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Add Wall Run States deferred: exit Play Mode and wait for compilation/import.");
            return;
        }

        try
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
            Require(controller != null, "PlayerBase.controller not found. Run Step 5 first.");
            Ensure(controller);
            AssetDatabase.SaveAssets();
            Selection.activeObject = controller;
            EditorGUIUtility.PingObject(controller);
            Debug.Log("[ProjectRestart] Add Wall Run States PASS: " + controller.name);
        }
        catch (Exception exception)
        {
            Debug.LogError("[ProjectRestart] Add Wall Run States FAIL: " + exception.Message);
            Debug.LogException(exception);
        }
    }

    public static void Ensure(AnimatorController controller)
    {
        if (controller == null)
            throw new ArgumentNullException(nameof(controller));

        var machine = controller.layers[0].stateMachine;
        var locomotion = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "Locomotion");
        var jumpAir = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "JumpAir");
        var jumpForwardAir = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "JumpForwardAir");
        var jumpDoubleAir = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "JumpDoubleAir");
        var jumpDoubleFall = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "JumpDoubleFall");

        Require(locomotion != null, "PlayerBase.controller must contain a Locomotion state.");
        Require(jumpAir != null, "PlayerBase.controller must contain a JumpAir state.");

        AddParameterIfMissing(controller, "WallRunLeft", AnimatorControllerParameterType.Trigger);
        AddParameterIfMissing(controller, "WallRunRight", AnimatorControllerParameterType.Trigger);

        var leftClip = LoadClip(WallRunLeftClipPath, "LoWall_LDash_Loop");
        var rightClip = LoadClip(WallRunRightClipPath, "LoWall_RDash_Loop");
        var leftStartClip = LoadClip(WallRunLeftStartClipPath, "LoWall_LDash_Start_Root");
        var rightStartClip = LoadClip(WallRunRightStartClipPath, "LoWall_RDash_Start_Root");

        var wallRunLeft = AddOrGetState(machine, "WallRunLeft", new Vector3(1000, 100), out var _);
        var wallRunRight = AddOrGetState(machine, "WallRunRight", new Vector3(1200, 100), out var _);
        var wallRunStartLeft = AddOrGetState(machine, "WallRunStartLeft", new Vector3(1000, -100), out var _);
        var wallRunStartRight = AddOrGetState(machine, "WallRunStartRight", new Vector3(1200, -100), out var _);

        // Always re-apply the clips so re-running the tool repairs states created
        // against the older "_All" one-shot dash clips.
        ConfigureState(wallRunLeft, leftClip);
        ConfigureState(wallRunRight, rightClip);
        ConfigureState(wallRunStartLeft, leftStartClip);
        ConfigureState(wallRunStartRight, rightStartClip);

        // The authored start clips flow into the loop near their end, so entries from
        // flips or falls get a real transition animation instead of a hard cut.
        AddTransitionIfMissing(wallRunStartLeft, wallRunLeft, null, AnimatorConditionMode.If, 0f,
            0.15f, 0.9f, true);
        AddTransitionIfMissing(wallRunStartRight, wallRunRight, null, AnimatorConditionMode.If, 0f,
            0.15f, 0.9f, true);

        // Entries target the start states; repoint any older air->loop transitions so a
        // rebuilt controller does not keep duplicate trigger transitions.
        var airSources = new[] { jumpAir, jumpForwardAir, jumpDoubleAir, jumpDoubleFall };
        foreach (var source in airSources)
        {
            if (source == null) continue;
            RemoveTransitionsTo(source, wallRunLeft, wallRunRight);
            AddTransitionIfMissing(source, wallRunStartLeft, "WallRunLeft", AnimatorConditionMode.If, 0f,
                WallRunEnterDuration, 0f, false);
            AddTransitionIfMissing(source, wallRunStartRight, "WallRunRight", AnimatorConditionMode.If, 0f,
                WallRunEnterDuration, 0f, false);
        }

        RemoveTransitionsTo(wallRunLeft, locomotion, jumpAir);
        RemoveTransitionsTo(wallRunRight, locomotion, jumpAir);

        EditorUtility.SetDirty(controller);
    }

    private static void ConfigureState(AnimatorState state, AnimationClip clip)
    {
        if (state == null || clip == null) return;
        state.motion = clip;
        state.writeDefaultValues = false;
        state.iKOnFeet = true;
    }

    private static AnimationClip LoadClip(string path, string clipName)
    {
        var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().ToArray();
        var clip = clips.SingleOrDefault(item => item.name == clipName);
        Require(clip != null && clip.isHumanMotion, $"Could not load humanoid clip '{clipName}' from {path}");
        return clip;
    }

    private static void AddParameterIfMissing(AnimatorController controller, string name, AnimatorControllerParameterType type)
    {
        if (controller.parameters.Any(item => item.name == name && item.type == type))
            return;
        controller.AddParameter(name, type);
    }

    private static AnimatorState AddOrGetState(AnimatorStateMachine machine, string name, Vector3 position, out bool isNew)
    {
        var existing = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == name);
        if (existing != null)
        {
            isNew = false;
            return existing;
        }
        isNew = true;
        return machine.AddState(name, position);
    }

    private static void AddTransitionIfMissing(AnimatorState from, AnimatorState to, string parameter, AnimatorConditionMode mode, float threshold, float duration, float exitTime, bool hasExitTime)
    {
        if (from == null || to == null) return;
        if (from.transitions.Any(item => item.destinationState == to))
            return;

        var transition = from.AddTransition(to);
        if (!string.IsNullOrEmpty(parameter))
            transition.AddCondition(mode, threshold, parameter);
        transition.hasExitTime = hasExitTime;
        transition.exitTime = exitTime;
        transition.hasFixedDuration = true;
        transition.duration = duration;
        transition.canTransitionToSelf = false;
    }

    private static void RemoveTransitionsTo(AnimatorState from, params AnimatorState[] destinations)
    {
        foreach (var transition in from.transitions.Where(item => destinations.Contains(item.destinationState)).ToArray())
            from.RemoveTransition(transition);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
