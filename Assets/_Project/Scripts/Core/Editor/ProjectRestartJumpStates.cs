using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Non-destructive post-build step. Adds Jump, JumpForward and Crouch state machines
/// to the existing single-layer PlayerBase.controller if they are missing.
/// </summary>
public static class ProjectRestartJumpStates
{
    public const string JumpStartClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Base/CLazy@Jmp_Base_A_Start.FBX";
    public const string JumpKeepClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Base/CLazy@Jmp_Base_A_Keep.FBX";
    public const string JumpLandClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Land/CLazy@Land_Base_Wait.FBX";

    public const string JumpForwardStartClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Front_Air/CLazy@Jmp_FrontAir_A_Start.FBX";
    public const string JumpForwardKeepClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Front_Air/CLazy@Jmp_FrontAir_A_Keep.FBX";
    public const string JumpForwardLandClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Land/CLazy@Land_Base_Move.FBX";
    public const string LandingRollClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Land/CLazy@Land_Roll_Move_Root.FBX";

    public const string JumpDoubleTurnLeftClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Turn_Air/CLazy@Jmp_TurnAir_Left.FBX";
    public const string JumpDoubleTurnRightClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Turn_Air/CLazy@Jmp_TurnAir_Right.FBX";
    public const string JumpDoubleFallClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Air_Loop/CLazy@Jump_Down_A_Loop.FBX";

    // Real crouch, authored on the protagonist skeleton in Blender
    // (Tools/PlayerAnim/crouch_anim.py), one in-place take per file:
    // Player_Crouch_Down / _Idle (loop) / _Up.fbx. Preferred over the CLazy
    // slide clips below, which the crouch states used to borrow — their
    // root-in-pose travel slid the body away and culled the renderer.
    public const string PlayerCrouchFolder = "Assets/_Project/Animations/Crouch/";
    public const string PlayerModelPath = "Assets/Character_Unity.fbx";
    private static readonly string[] PlayerCrouchTakes = { "Crouch_Down", "Crouch_Idle", "Crouch_Up" };
    private static string PlayerCrouchPath(string take) => PlayerCrouchFolder + "Player_" + take + ".fbx";

    public const string CrouchStartClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P2_CLazyEscape/Esc_Slide_Fwd/CLazy@Esc_Slide_Start_Root.FBX";
    public const string CrouchLoopClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P2_CLazyEscape/Esc_Slide_Fwd/CLazy@Esc_Slide_Keep_Long_Root.FBX";
    public const string CrouchEndClipPath = "Assets/ThirdParty/CLazyRunner/Animations/P2_CLazyEscape/Esc_Slide_Fwd/CLazy@Esc_Slide_End_Root.FBX";

    private const float JumpEnterDuration = 0.1f;
    private const float JumpStartToAirDuration = 0.05f;
    private const float JumpAirToLandDuration = 0.03f;
    private const float JumpLandToLocoDuration = 0.05f;
    private const float JumpLandToLocoExitTime = 0.95f;
    private const float JumpLandToJumpDuration = 0.05f;

    private const float CrouchEnterDuration = 0.1f;
    private const float CrouchStartToLoopDuration = 0.15f;
    private const float CrouchLoopToEndDuration = 0.1f;
    private const float CrouchEndToLocoDuration = 0.15f;

    [MenuItem("Tools/Project Restart/Add Jump and Crouch States")]
    public static void BuildAndVerify()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Add Jump and Crouch States deferred: exit Play Mode and wait for compilation/import.");
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
            Debug.Log("[ProjectRestart] Add Jump and Crouch States PASS: " + controller.name);
        }
        catch (Exception exception)
        {
            Debug.LogError("[ProjectRestart] Add Jump and Crouch States FAIL: " + exception.Message);
            Debug.LogException(exception);
        }
    }

    public static void Ensure(AnimatorController controller)
    {
        if (controller == null)
            throw new ArgumentNullException(nameof(controller));

        var machine = controller.layers[0].stateMachine;
        var locomotion = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "Locomotion");
        Require(locomotion != null, "PlayerBase.controller must contain a Locomotion state.");

        // Parameters
        AddParameterIfMissing(controller, "Jump", AnimatorControllerParameterType.Trigger);
        AddParameterIfMissing(controller, "JumpForward", AnimatorControllerParameterType.Trigger);
        AddParameterIfMissing(controller, "InAir", AnimatorControllerParameterType.Bool);
        AddParameterIfMissing(controller, "VerticalSpeed", AnimatorControllerParameterType.Float);
        RemoveParameterIfPresent(controller, "LandingRoll");
        AddParameterIfMissing(controller, "Crouch", AnimatorControllerParameterType.Trigger);
        AddParameterIfMissing(controller, "Crouching", AnimatorControllerParameterType.Bool);
        AddParameterIfMissing(controller, "DoubleJump", AnimatorControllerParameterType.Trigger);
        AddParameterIfMissing(controller, "JumpVariant", AnimatorControllerParameterType.Float);

        // Idle jump states
        var jumpClips = (start: LoadClip(JumpStartClipPath, "Jmp_Base_A_Start"),
                         keep: LoadClip(JumpKeepClipPath, "Jmp_Base_A_Keep"),
                         land: LoadClip(JumpLandClipPath, "Land_Base_Wait"));

        var jumpStart = AddOrGetState(machine, "JumpStart", new Vector3(250, 250), out var jumpStartNew);
        var jumpAir = AddOrGetState(machine, "JumpAir", new Vector3(500, 250), out var jumpAirNew);
        var jumpLand = AddOrGetState(machine, "JumpLand", new Vector3(500, 400), out var jumpLandNew);

        if (jumpStartNew) ConfigureState(jumpStart, jumpClips.start);
        if (jumpAirNew) ConfigureState(jumpAir, jumpClips.keep);
        if (jumpLandNew) ConfigureState(jumpLand, jumpClips.land);

        AddTransitionIfMissing(locomotion, jumpStart, "Jump", AnimatorConditionMode.If, 0f,
            JumpEnterDuration, 0.9f, false);
        AddTransitionIfMissing(jumpStart, jumpAir, null, AnimatorConditionMode.Greater, 0f,
            JumpStartToAirDuration, 1f, true);
        AddTransitionIfMissing(jumpAir, jumpLand, "InAir", AnimatorConditionMode.IfNot, 0f,
            JumpAirToLandDuration, 0f, false);
        AddTransitionIfMissing(jumpLand, jumpStart, "Jump", AnimatorConditionMode.If, 0f,
            JumpLandToJumpDuration, 0f, false);
        AddTransitionIfMissing(jumpLand, locomotion, null, AnimatorConditionMode.Greater, 0f,
            JumpLandToLocoDuration, JumpLandToLocoExitTime, true);

        // Moving jump (forward) states
        var jumpForwardClips = (start: LoadClip(JumpForwardStartClipPath, "Jmp_FrontAir_A_Start"),
                                keep: LoadClip(JumpForwardKeepClipPath, "Jmp_FrontAir_A_Keep"),
                                land: LoadClip(JumpForwardLandClipPath, "Land_Base_Move"));

        var jumpForwardStart = AddOrGetState(machine, "JumpForwardStart", new Vector3(700, 100), out var jfStartNew);
        var jumpForwardAir = AddOrGetState(machine, "JumpForwardAir", new Vector3(700, 250), out var jfAirNew);
        var jumpForwardLand = AddOrGetState(machine, "JumpForwardLand", new Vector3(700, 400), out var jfLandNew);

        if (jfStartNew) ConfigureState(jumpForwardStart, jumpForwardClips.start);
        if (jfAirNew) ConfigureState(jumpForwardAir, jumpForwardClips.keep);
        if (jfLandNew || jumpForwardLand.motion != jumpForwardClips.land)
            ConfigureState(jumpForwardLand, jumpForwardClips.land);

        AddTransitionIfMissing(locomotion, jumpForwardStart, "JumpForward", AnimatorConditionMode.If, 0f,
            JumpEnterDuration, 0.9f, false);
        AddTransitionIfMissing(jumpForwardStart, jumpForwardAir, null, AnimatorConditionMode.Greater, 0f,
            JumpStartToAirDuration, 1f, true);
        AddTransitionIfMissing(jumpForwardAir, jumpForwardLand, "InAir", AnimatorConditionMode.IfNot, 0f,
            JumpAirToLandDuration, 0f, false);
        AddTransitionIfMissing(jumpForwardLand, jumpForwardStart, "JumpForward", AnimatorConditionMode.If, 0f,
            JumpLandToJumpDuration, 0f, false);
        // Split on Speed at touchdown: moving landings take the stepping
        // Land_Base_Move, idle touchdowns the planted JumpLand absorb. A player
        // who stops mid-step bails to locomotion instead of walking-in-place.
        // Manage both exits explicitly — earlier runs promoted a conditional
        // exit to index 0, so AddTransitionIfMissing could clobber it.
        SplitForwardLand(jumpForwardAir, jumpForwardLand, jumpLand, locomotion);
        var landExit = jumpForwardLand.transitions.FirstOrDefault(t =>
            t.destinationState == locomotion && t.conditions.All(c => c.parameter != "Speed"));
        if (landExit == null)
            landExit = jumpForwardLand.AddTransition(locomotion);
        landExit.conditions = new AnimatorCondition[0];
        landExit.hasExitTime = true;
        landExit.exitTime = JumpLandToLocoExitTime;
        landExit.hasFixedDuration = true;
        landExit.duration = JumpLandToLocoDuration;
        landExit.canTransitionToSelf = false;
        var landBail = jumpForwardLand.transitions.FirstOrDefault(t =>
            t.destinationState == locomotion && t.conditions.Any(c => c.parameter == "Speed"));
        if (landBail == null)
            landBail = jumpForwardLand.AddTransition(locomotion);
        landBail.conditions = new[]
        {
            new AnimatorCondition { mode = AnimatorConditionMode.Less, threshold = ForwardLandSpeedGate, parameter = "Speed" }
        };
        landBail.hasExitTime = false;
        landBail.hasFixedDuration = true;
        landBail.duration = 0.12f;
        landBail.canTransitionToSelf = false;

        // Wallrun exits and drifting falls also land through the vertical-jump
        // air state — split that touchdown the same way: holding a direction
        // takes the stepping land, no input takes the planted absorb.
        var airToIdle = jumpAir.transitions.FirstOrDefault(t => t.destinationState == jumpLand);
        if (airToIdle != null && airToIdle.conditions.All(c => c.parameter != "Speed"))
        {
            var conds = airToIdle.conditions.ToList();
            conds.Add(new AnimatorCondition
            {
                mode = AnimatorConditionMode.Less,
                threshold = ForwardLandSpeedGate,
                parameter = "Speed"
            });
            airToIdle.conditions = conds.ToArray();
        }
        var airToStep = jumpAir.transitions.FirstOrDefault(t => t.destinationState == jumpForwardLand);
        if (airToStep == null)
            airToStep = jumpAir.AddTransition(jumpForwardLand);
        airToStep.conditions = new[]
        {
            new AnimatorCondition { mode = AnimatorConditionMode.IfNot, parameter = "InAir" },
            new AnimatorCondition { mode = AnimatorConditionMode.Greater, threshold = ForwardLandSpeedGate, parameter = "Speed" }
        };
        airToStep.hasExitTime = false;
        airToStep.hasFixedDuration = true;
        airToStep.duration = JumpAirToLandDuration;
        airToStep.canTransitionToSelf = false;

        var landingRoll = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "LandingRoll");
        if (landingRoll != null)
        {
            foreach (var state in machine.states.Select(item => item.state).ToArray())
                RemoveTransitionsTo(state, landingRoll);
            machine.RemoveState(landingRoll);
        }

        // Double jump state (mid-air second jump with a random left/right turn)
        const float DoubleJumpEnterDuration = 0.1f;
        const float DoubleJumpLandDuration = 0.1f;

        var doubleJumpClips = (turnLeft: LoadClip(JumpDoubleTurnLeftClipPath, "Jmp_TurnAir_Left"),
                               turnRight: LoadClip(JumpDoubleTurnRightClipPath, "Jmp_TurnAir_Right"));

        var jumpDoubleAir = AddOrGetState(machine, "JumpDoubleAir", new Vector3(450, 100), out var jumpDoubleAirNew);
        var needsTree = jumpDoubleAirNew || jumpDoubleAir.motion is not BlendTree;
        if (!needsTree && jumpDoubleAir.motion is BlendTree existingTree)
        {
            var expected = new[] { doubleJumpClips.turnLeft, doubleJumpClips.turnRight };
            needsTree = existingTree.children.Length != expected.Length ||
                        !expected.All(clip => existingTree.children.Any(child => child.motion == clip));
        }

        if (needsTree)
        {
            var tree = new BlendTree
            {
                name = "Jump Double Air",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "JumpVariant",
                useAutomaticThresholds = false,
                minThreshold = 0f,
                maxThreshold = 1f
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(doubleJumpClips.turnLeft, 0f);
            tree.AddChild(doubleJumpClips.turnRight, 1f);
            jumpDoubleAir.motion = tree;
            jumpDoubleAir.writeDefaultValues = false;
            jumpDoubleAir.iKOnFeet = true;
        }
        jumpDoubleAir.speed = 0.7f;

        AddTransitionIfMissing(jumpAir, jumpDoubleAir, "DoubleJump", AnimatorConditionMode.If, 0f,
            DoubleJumpEnterDuration, 0f, false);
        AddTransitionIfMissing(jumpForwardAir, jumpDoubleAir, "DoubleJump", AnimatorConditionMode.If, 0f,
            DoubleJumpEnterDuration, 0f, false);

        const float DoubleJumpFallDuration = 0.2f;
        var doubleJumpFallClip = LoadClip(JumpDoubleFallClipPath, "Jump_Down_A_Loop");
        var jumpDoubleFall = AddOrGetState(machine, "JumpDoubleFall", new Vector3(450, 225), out var jumpDoubleFallNew);
        if (jumpDoubleFallNew || jumpDoubleFall.motion != doubleJumpFallClip)
            ConfigureState(jumpDoubleFall, doubleJumpFallClip);
        jumpDoubleFall.speed = 0.8f;

        RemoveTransitionsTo(jumpDoubleAir, jumpAir, jumpForwardAir, jumpLand, jumpForwardLand, jumpDoubleFall);
        RemoveTransitionsTo(jumpDoubleFall, jumpLand, jumpForwardLand);
        AddTransitionIfMissing(jumpDoubleAir, jumpDoubleFall, "VerticalSpeed", AnimatorConditionMode.Less, 2.5f,
            DoubleJumpFallDuration, 0.58f, true);
        AddTransitionIfMissing(jumpDoubleAir, jumpForwardLand, "InAir", AnimatorConditionMode.IfNot, 0f,
            DoubleJumpLandDuration, 0f, false);
        AddTransitionIfMissing(jumpDoubleFall, jumpForwardLand, "InAir", AnimatorConditionMode.IfNot, 0f,
            DoubleJumpLandDuration, 0f, false);
        // Same split for the double-jump landing paths.
        SplitForwardLand(jumpDoubleAir, jumpForwardLand, jumpLand, locomotion);
        SplitForwardLand(jumpDoubleFall, jumpForwardLand, jumpLand, locomotion);

        // Crouch states
        var crouchClips = TryLoadPlayerCrouch(out var player) ? player :
                          (start: LoadClip(CrouchStartClipPath, "Esc_Slide_Start_Root"),
                           loop: LoadClip(CrouchLoopClipPath, "Esc_Slide_Keep_Long_Root"),
                           end: LoadClip(CrouchEndClipPath, "Esc_Slide_End_Root"));

        var crouchStart = AddOrGetState(machine, "CrouchStart", new Vector3(250, 500), out var crouchStartNew);
        var crouchLoop = AddOrGetState(machine, "CrouchLoop", new Vector3(500, 500), out var crouchLoopNew);
        var crouchEnd = AddOrGetState(machine, "CrouchEnd", new Vector3(500, 650), out var crouchEndNew);

        if (crouchStartNew) ConfigureState(crouchStart, crouchClips.start);
        if (crouchLoopNew) ConfigureState(crouchLoop, crouchClips.loop);
        if (crouchEndNew) ConfigureState(crouchEnd, crouchClips.end);

        AddTransitionIfMissing(locomotion, crouchStart, "Crouch", AnimatorConditionMode.If, 0f,
            CrouchEnterDuration, 0.9f, false);
        AddTransitionIfMissing(crouchStart, crouchLoop, "Crouching", AnimatorConditionMode.If, 0f,
            CrouchStartToLoopDuration, 1f, true);
        AddTransitionIfMissing(crouchLoop, crouchEnd, "Crouching", AnimatorConditionMode.IfNot, 0f,
            CrouchLoopToEndDuration, 0f, false);
        AddTransitionIfMissing(crouchEnd, locomotion, null, AnimatorConditionMode.Greater, 0f,
            CrouchEndToLocoDuration, 1f, true);

        // Safety transition for early crouch cancel before reaching the loop.
        AddTransitionIfMissing(crouchStart, crouchEnd, "Crouching", AnimatorConditionMode.IfNot, 0f,
            CrouchLoopToEndDuration, 0.1f, false);

        // Cancel windows: jump <-> slide.
        var dash = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == "Dash");
        if (dash != null)
        {
            const float CancelDuration = 0.1f;
            AddTransitionIfMissing(dash, jumpStart, "Jump", AnimatorConditionMode.If, 0f,
                CancelDuration, 0.9f, false);
            AddTransitionIfMissing(dash, jumpForwardStart, "JumpForward", AnimatorConditionMode.If, 0f,
                CancelDuration, 0.9f, false);
            AddTransitionIfMissing(jumpLand, dash, "Dash", AnimatorConditionMode.If, 0f,
                CancelDuration, 0f, false);
            AddTransitionIfMissing(jumpForwardLand, dash, "Dash", AnimatorConditionMode.If, 0f,
                CancelDuration, 0f, false);
        }

        EditorUtility.SetDirty(controller);
    }

    /// <summary>Imports Player_Crouch.fbx as Humanoid on the protagonist's avatar
    /// and puts its takes into CrouchStart/CrouchLoop/CrouchEnd (existing states
    /// are overwritten — Ensure only fills new ones).</summary>
    [MenuItem("Tools/Project Restart/Player Model/Install Crouch Animations")]
    public static void InstallCrouch()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Install Crouch Animations deferred: exit Play Mode and wait for compilation/import.");
            return;
        }
        try
        {
            foreach (var take in PlayerCrouchTakes) ConfigureCrouchImporter(take);
            Require(TryLoadPlayerCrouch(out var clips), "Player_Crouch_*.fbx imported but their Crouch_Down/Crouch_Idle/Crouch_Up humanoid clips were not found.");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
            Require(controller != null, "PlayerBase.controller not found. Run Step 5 first.");
            Ensure(controller); // creates the states/transitions if this controller never had them
            var machine = controller.layers[0].stateMachine;
            foreach (var (stateName, clip) in new[] { ("CrouchStart", clips.start), ("CrouchLoop", clips.loop), ("CrouchEnd", clips.end) })
            {
                var state = machine.states.Select(item => item.state).SingleOrDefault(item => item.name == stateName);
                Require(state != null, stateName + " state missing after Ensure.");
                ConfigureState(state, clip);
            }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ProjectRestart] Install Crouch Animations PASS: CrouchStart={clips.start.length:0.00}s, " +
                      $"CrouchLoop={clips.loop.length:0.00}s (loop={clips.loop.isLooping}), CrouchEnd={clips.end.length:0.00}s. " +
                      "Play Mode check still needed: out of combat, idle, press the slide/dodge key.");
        }
        catch (Exception exception)
        {
            Debug.LogError("[ProjectRestart] Install Crouch Animations FAIL: " + exception.Message);
            Debug.LogException(exception);
        }
    }

    private static void ConfigureCrouchImporter(string clipName)
    {
        var path = PlayerCrouchPath(clipName);
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        Require(importer != null, path + " missing — run Tools/PlayerAnim/crouch_anim.py in Blender first.");
        var avatar = AssetDatabase.LoadAllAssetsAtPath(PlayerModelPath).OfType<Avatar>().FirstOrDefault();
        Require(avatar != null && avatar.isHuman, "No humanoid avatar on " + PlayerModelPath);
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
        importer.sourceAvatar = avatar;
        importer.importAnimation = true;
        importer.SaveAndReimport();

        // In place and exactly as authored: rotation, height and XZ all baked
        // into the pose relative to the ORIGINAL root (the feet were planted on
        // the rest floor in Blender), so the soles stay where they stood.
        var takes = importer.defaultClipAnimations;
        Require(takes.Length == 1, path + " should hold exactly one take (has " + takes.Length + ").");
        foreach (var take in takes)
        {
            take.name = clipName; // Blender names the take "<armature>|Scene"
            var loop = clipName == "Crouch_Idle";
            take.loopTime = loop;
            take.loopPose = loop;
            take.lockRootRotation = true;
            take.keepOriginalOrientation = true;
            take.lockRootHeightY = true;
            take.keepOriginalPositionY = true;
            take.heightFromFeet = false;
            take.lockRootPositionXZ = true;
            take.keepOriginalPositionXZ = true;
        }
        importer.clipAnimations = takes;
        importer.SaveAndReimport();
    }

    private static bool TryLoadPlayerCrouch(out (AnimationClip start, AnimationClip loop, AnimationClip end) clips)
    {
        AnimationClip Find(string n) => AssetDatabase.LoadAllAssetsAtPath(PlayerCrouchPath(n)).OfType<AnimationClip>()
            .FirstOrDefault(c => c.isHumanMotion && c.name == n);
        clips = (Find("Crouch_Down"), Find("Crouch_Idle"), Find("Crouch_Up"));
        return clips.start != null && clips.loop != null && clips.end != null;
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

    private static void RemoveParameterIfPresent(AnimatorController controller, string name)
    {
        var parameter = controller.parameters.FirstOrDefault(item => item.name == name);
        if (parameter != null)
            controller.RemoveParameter(parameter);
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

    private const float ForwardLandSpeedGate = 0.15f;

    /// <summary>Splits an air state's touchdown on Speed: moving landings go to
    /// the stepping forward-land, idle touchdowns to the planted idle-land.
    /// Also removes the idle-skip→Locomotion transition earlier runs wrote.</summary>
    private static void SplitForwardLand(AnimatorState air, AnimatorState land, AnimatorState idleLand, AnimatorState locomotion)
    {
        var toLand = air.transitions.FirstOrDefault(t => t.destinationState == land);
        if (toLand != null && toLand.conditions.All(c => c.parameter != "Speed"))
        {
            var conds = toLand.conditions.ToList();
            conds.Add(new AnimatorCondition
            {
                mode = AnimatorConditionMode.Greater,
                threshold = ForwardLandSpeedGate,
                parameter = "Speed"
            });
            toLand.conditions = conds.ToArray();
        }

        var toIdle = air.transitions.FirstOrDefault(t => t.destinationState == idleLand);
        if (toIdle == null)
            toIdle = air.AddTransition(idleLand);
        toIdle.conditions = new[]
        {
            new AnimatorCondition { mode = AnimatorConditionMode.IfNot, parameter = "InAir" },
            new AnimatorCondition { mode = AnimatorConditionMode.Less, threshold = ForwardLandSpeedGate, parameter = "Speed" }
        };
        toIdle.hasExitTime = false;
        toIdle.hasFixedDuration = true;
        toIdle.duration = JumpAirToLandDuration;
        toIdle.canTransitionToSelf = false;

        var skip = air.transitions.FirstOrDefault(t => t.destinationState == locomotion &&
            t.conditions.Any(c => c.parameter == "Speed"));
        if (skip != null)
            air.RemoveTransition(skip);
    }

    private static void AddTransitionIfMissing(AnimatorState from, AnimatorState to, string parameter, AnimatorConditionMode mode, float threshold, float duration, float exitTime, bool hasExitTime)
    {
        AddTransitionIfMissing(from, to, parameter, mode, threshold, duration, exitTime, hasExitTime, null);
    }

    private static void AddTransitionIfMissing(AnimatorState from, AnimatorState to, string parameter, AnimatorConditionMode mode, float threshold, float duration, float exitTime, bool hasExitTime, (string name, AnimatorConditionMode mode, float threshold)[] extraConditions)
    {
        var existing = from.transitions.FirstOrDefault(item => item.destinationState == to);
        if (existing != null)
        {
            // Keep the transition updated with the latest builder settings.
            existing.hasExitTime = hasExitTime;
            existing.exitTime = exitTime;
            existing.hasFixedDuration = true;
            existing.duration = duration;
            existing.canTransitionToSelf = false;
            return;
        }

        var transition = from.AddTransition(to);
        if (!string.IsNullOrEmpty(parameter))
            transition.AddCondition(mode, threshold, parameter);
        if (extraConditions != null)
        {
            foreach (var c in extraConditions)
                transition.AddCondition(c.mode, c.threshold, c.name);
        }
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
