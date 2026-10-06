using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class ProjectRestartTestScene
{
    public const string ScenePath = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const string GroundMaterialPath = "Assets/_Project/Materials/TestGround.mat";
    private const string GroundTexturePath = "Assets/_Project/Materials/TestGroundChecker.asset";

    [MenuItem("Tools/Project Restart/Step 6 - Build and Verify Test Scene")]
    public static void BuildAndVerify()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Step 6 deferred: exit Play Mode and wait for compilation/import to finish, then run again.");
            return;
        }

        try
        {
            VerifyPipeline();
            var existing = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            if (existing != null)
            {
                var opened = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (opened.path != ScenePath)
                {
                    if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                        return;
                    opened = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                }
                RecalibrateAndApply(opened);
                EditorSceneManager.SaveScene(opened);
                VerifyScene(opened);
                ReportReady();
                return;
            }

            Require(!File.Exists(ScenePath) && !Directory.Exists(ScenePath) && !File.Exists(ScenePath + ".meta"), "Scene path is occupied; refusing to overwrite it.");
            Require(AssetDatabase.IsValidFolder("Assets/_Project/Scenes") && AssetDatabase.IsValidFolder("Assets/_Project/Materials"), "Run the folder setup first.");
            var hero = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath("071b4da7cfff18347a371c677585b749"));
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
            var inputs = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetDatabase.GUIDToAssetPath("052faaac586de48259a63d0c4782560b"));
            Require(hero != null && controller != null && inputs != null, "Hero, PlayerBase.controller or InputSystem_Actions.inputactions is missing.");
            Require(inputs.FindAction("Player/Move") != null && inputs.FindAction("Player/Sprint") != null && inputs.FindAction("Player/Jump") != null, "Input asset needs Player/Move, Player/Sprint and Player/Jump.");
            ProjectRestartJumpStates.Ensure(controller);
            ProjectRestartWallRun.Ensure(controller);
            AssetDatabase.SaveAssets();
            var calibration = ProjectRestartStrideCalibration.Measure(hero, controller);
            VerifyBlendMath(calibration.SlowWalkSpeed, calibration.WalkSpeed, calibration.RunSpeed, calibration.CycleDurations);
            Require(calibration.RestBounds.size.y > 0.5f && calibration.RestBounds.size.y < 5f, $"Hero mesh height is unexpected: {calibration.RestBounds.size.y:F4} m (expected 0.5–5 m), min={calibration.RestBounds.min.ToString("F4")}, max={calibration.RestBounds.max.ToString("F4")}. Share the Hero idle bounds and scaled-bounds regression logs; do not resize the prefab to bypass this check.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.65f);
            RenderSettings.fog = false;

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Test Ground";
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            ground.GetComponent<Renderer>().sharedMaterial = GetGroundMaterial();
            EnsureWallRunTestGeometry(scene);

            var lightObject = new GameObject("Directional Light", typeof(Light));
            lightObject.transform.rotation = Quaternion.Euler(50f, 150f, 0f);
            var light = lightObject.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            light.shadows = LightShadows.Soft;
            light.shadowBias = 0.02f;
            light.shadowNormalBias = 0.2f;
            light.shadowNearPlane = 0.05f;
            light.useViewFrustumForShadowCasterCull = false;
            lightObject.AddComponent<UniversalAdditionalLightData>();
            RenderSettings.sun = light;

            var player = new GameObject("Player", typeof(CharacterController));
            player.transform.position = new Vector3(0f, 0f, 0f);
            var visual = PrefabUtility.InstantiatePrefab(hero, scene) as GameObject;
            Require(visual != null, "Could not instantiate Hero.");
            visual.transform.SetParent(player.transform, false);
            visual.transform.localPosition = new Vector3(0f, -calibration.RestBounds.min.y, 0f);
            visual.transform.localRotation = Quaternion.identity;
            Require(visual.GetComponentsInChildren<Collider>(true).Length == 0 && visual.GetComponentsInChildren<Rigidbody>(true).Length == 0, "Hero has unexpected physics components; refusing to combine them with a CharacterController.");
            var animator = visual.GetComponent<Animator>();
            Require(animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman, "Hero Avatar is not a valid Humanoid.");
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.updateMode = AnimatorUpdateMode.Normal;
            PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
            PrefabUtility.RecordPrefabInstancePropertyModifications(visual.transform);

            var height = calibration.RestBounds.size.y;
            var character = player.GetComponent<CharacterController>();
            character.height = height;
            character.radius = Mathf.Clamp(height * 0.15f, 0.2f, 0.4f);
            character.center = Vector3.up * (height * 0.5f);
            character.skinWidth = 0.02f;
            character.stepOffset = 0.2f;
            character.slopeLimit = 45f;
            character.minMoveDistance = 0f;

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener), typeof(UniversalAdditionalCameraData), typeof(CinemachineBrain));
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.GetComponent<Camera>();
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 250f;
            camera.fieldOfView = 55f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.18f, 0.23f, 0.3f);
            var offset = new Vector3(0f, height * 2f, -height * 3f);

            var vcamObject = new GameObject("Player VCam", typeof(CinemachineCamera), typeof(CinemachineFollow), typeof(CinemachineHardLookAt));
            vcamObject.transform.SetParent(cameraObject.transform, false);
            var vcam = vcamObject.GetComponent<CinemachineCamera>();
            var follow = vcamObject.GetComponent<CinemachineFollow>();
            var hardLook = vcamObject.GetComponent<CinemachineHardLookAt>();

            var target = new CameraTarget
            {
                TrackingTarget = player.transform,
                CustomLookAtTarget = false,
                LookAtTarget = player.transform
            };
            vcam.Target = target;
            vcam.Priority = 1;

            var lens = vcam.Lens;
            lens.NearClipPlane = 0.05f;
            lens.FarClipPlane = 250f;
            lens.FieldOfView = 55f;
            vcam.Lens = lens;

            follow.FollowOffset = offset;
            var tracker = follow.TrackerSettings;
            tracker.PositionDamping = Vector3.zero;
            follow.TrackerSettings = tracker;
            hardLook.LookAtOffset = new Vector3(0f, height * 0.5f, 0f);

            var movement = player.AddComponent<PlayerLocomotion>();
            var tilt = player.AddComponent<CameraTiltController>();
            var slide = player.AddComponent<SlideController>();
            var landingRoll = player.AddComponent<LandingRollController>();
            landingRoll.enabled = false;
            var wallRun = player.AddComponent<WallRunController>();
            var cameraController = player.AddComponent<PlayerCameraController>();
            player.AddComponent<PlayerInputSettings>();
            tilt.ExplicitCamera = vcam;

            var cameraData = new SerializedObject(cameraController);
            cameraData.FindProperty("cinemachineCamera").objectReferenceValue = vcam;
            cameraData.FindProperty("cinemachineFollow").objectReferenceValue = follow;
            cameraData.FindProperty("cinemachineLookAt").objectReferenceValue = hardLook;
            cameraData.FindProperty("target").objectReferenceValue = player.transform;
            cameraData.FindProperty("inputActions").objectReferenceValue = inputs;
            cameraData.ApplyModifiedPropertiesWithoutUndo();

            var slideData = new SerializedObject(slide);
            slideData.FindProperty("inputActions").objectReferenceValue = inputs;
            slideData.ApplyModifiedPropertiesWithoutUndo();

            ApplyCalibration(movement, inputs, cameraObject.transform, animator, calibration);

            VerifyScene(scene);
            Require(EditorSceneManager.SaveScene(scene, ScenePath), "Test scene could not be saved.");
            Selection.activeGameObject = player;
            SceneView.lastActiveSceneView?.Frame(new Bounds(player.transform.position + Vector3.up, Vector3.one * 5f), false);
            ReportReady();
        }
        catch (Exception exception)
        {
            Debug.LogError("[ProjectRestart] Step 6 setup FAIL: " + exception.Message + " No existing scene or vendor assets are automatically overwritten or deleted; inspect any newly created unsaved scene before retrying.");
            Debug.LogException(exception);
        }
    }

    private static void RecalibrateAndApply(Scene scene)
    {
        var hero = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath("071b4da7cfff18347a371c677585b749"));
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
        var inputs = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetDatabase.GUIDToAssetPath("052faaac586de48259a63d0c4782560b"));
        Require(hero != null && controller != null && inputs != null, "Hero, PlayerBase.controller or InputSystem_Actions.inputactions is missing.");
        ProjectRestartJumpStates.Ensure(controller);
        ProjectRestartWallRun.Ensure(controller);
        AssetDatabase.SaveAssets();

        var movement = scene.GetRootGameObjects().SelectMany(item => item.GetComponentsInChildren<PlayerLocomotion>()).SingleOrDefault();
        var animator = scene.GetRootGameObjects().SelectMany(item => item.GetComponentsInChildren<Animator>()).SingleOrDefault(item => item.avatar != null && item.avatar.isValid && item.avatar.isHuman);
        Require(movement != null && animator != null, "Existing scene does not contain a PlayerLocomotion + Humanoid Hero.");
        EnsureWallRunTestGeometry(scene);

        var animatorData = new SerializedObject(animator);
        animatorData.FindProperty("m_Controller").objectReferenceValue = controller;
        animatorData.ApplyModifiedPropertiesWithoutUndo();

        var view = scene.GetRootGameObjects().SelectMany(item => item.GetComponentsInChildren<Camera>()).SingleOrDefault()?.transform;
        if (view == null)
            view = animator.transform;

        var data = new SerializedObject(movement);
        var existingSlow = data.FindProperty("slowWalkSpeed").floatValue;
        var existingWalk = data.FindProperty("walkSpeed").floatValue;
        var existingRun = data.FindProperty("runSpeed").floatValue;
        var existingDurations = data.FindProperty("cycleDurations").vector4Value;
        var existingView = data.FindProperty("view").objectReferenceValue as Transform;
        var existingInputs = data.FindProperty("inputActions").objectReferenceValue as InputActionAsset;
        var existingAnimator = data.FindProperty("animator").objectReferenceValue as Animator;
        var speedsValid = existingSlow > 0f && existingWalk > existingSlow && existingRun > existingWalk &&
                          existingDurations.x > 0f && existingDurations.y > 0f && existingDurations.z > 0f && existingDurations.w > 0f;

        ProjectRestartStrideCalibration.Result calibration;
        if (speedsValid)
        {
            Debug.Log("[ProjectRestart] Step 6: existing PlayerLocomotion speed/duration values are valid; skipping re-calibration. Set any speed to 0 or delete the scene to force a full re-calibration.");
            ApplyReferences(movement, inputs, existingView ?? view, existingAnimator == null || existingAnimator != animator ? animator : existingAnimator);
        }
        else
        {
            calibration = ProjectRestartStrideCalibration.Measure(hero, controller);
            VerifyBlendMath(calibration.SlowWalkSpeed, calibration.WalkSpeed, calibration.RunSpeed, calibration.CycleDurations);
            Require(calibration.RestBounds.size.y > 0.5f && calibration.RestBounds.size.y < 5f, $"Hero mesh height is unexpected: {calibration.RestBounds.size.y:F4} m (expected 0.5–5 m).");
            ApplyCalibration(movement, inputs, existingView ?? view, existingAnimator == null || existingAnimator != animator ? animator : existingAnimator, calibration);
        }
        if (!movement.enabled)
            movement.enabled = true;

        EnsurePlayerModules(movement.gameObject, inputs);
        EnsureCinemachineRig(movement.gameObject, view);
    }

    private static void EnsurePlayerModules(GameObject player, InputActionAsset inputs)
    {
        if (player.GetComponent<PlayerState>() == null)
            player.AddComponent<PlayerState>();
        if (player.GetComponent<CameraTiltController>() == null)
            player.AddComponent<CameraTiltController>();
        if (player.GetComponent<PlayerInputSettings>() == null)
            player.AddComponent<PlayerInputSettings>();

        var cameraController = player.GetComponent<PlayerCameraController>();
        if (cameraController == null)
        {
            cameraController = player.AddComponent<PlayerCameraController>();
            var data = new SerializedObject(cameraController);
            data.FindProperty("inputActions").objectReferenceValue = inputs;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        else
        {
            var data = new SerializedObject(cameraController);
            if (data.FindProperty("inputActions").objectReferenceValue == null)
            {
                data.FindProperty("inputActions").objectReferenceValue = inputs;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        ApplyActionFraming(player);
        var slide = player.GetComponent<SlideController>();
        if (slide == null)
        {
            slide = player.AddComponent<SlideController>();
            var data = new SerializedObject(slide);
            data.FindProperty("inputActions").objectReferenceValue = inputs;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        else
        {
            var data = new SerializedObject(slide);
            if (data.FindProperty("inputActions").objectReferenceValue == null)
            {
                data.FindProperty("inputActions").objectReferenceValue = inputs;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        var landingRoll = player.GetComponent<LandingRollController>();
        if (landingRoll == null)
            landingRoll = player.AddComponent<LandingRollController>();
        landingRoll.enabled = false;

        if (player.GetComponent<WallRunController>() == null)
            player.AddComponent<WallRunController>();
    }

    public static void ApplyActionFraming(GameObject player)
    {
        var camera = player.GetComponent<PlayerCameraController>();
        if (camera == null) return;
        var data = new SerializedObject(camera);
        data.FindProperty("combatFOV").floatValue = 55f;
        data.FindProperty("combatDistance").floatValue = 4.8f;
        data.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void EnsureCinemachineRig(GameObject player, Transform cameraTransform)
    {
        var camera = cameraTransform.GetComponent<Camera>();
        Require(camera != null, "Expected the main camera on the view transform.");

        var brain = camera.GetComponent<CinemachineBrain>();
        if (brain == null)
            brain = camera.gameObject.AddComponent<CinemachineBrain>();

        var vcam = UnityEngine.Object.FindFirstObjectByType<CinemachineCamera>();
        if (vcam == null)
        {
            var vcamObject = new GameObject("Player VCam", typeof(CinemachineCamera), typeof(CinemachineFollow), typeof(CinemachineHardLookAt));
            vcamObject.transform.SetParent(camera.transform, false);
            vcam = vcamObject.GetComponent<CinemachineCamera>();

            var follow = vcamObject.GetComponent<CinemachineFollow>();
            var hardLook = vcamObject.GetComponent<CinemachineHardLookAt>();

            var controller = player.GetComponent<CharacterController>();
            var height = controller != null ? controller.height : 1.6f;
            var offset = new Vector3(0f, height * 2f, -height * 3f);

            var target = new CameraTarget
            {
                TrackingTarget = player.transform,
                CustomLookAtTarget = false,
                LookAtTarget = player.transform
            };
            vcam.Target = target;
            vcam.Priority = 1;

            var lens = vcam.Lens;
            lens.NearClipPlane = 0.05f;
            lens.FarClipPlane = 250f;
            lens.FieldOfView = 55f;
            vcam.Lens = lens;

            follow.FollowOffset = offset;
            var tracker = follow.TrackerSettings;
            tracker.PositionDamping = Vector3.zero;
            follow.TrackerSettings = tracker;
            hardLook.LookAtOffset = new Vector3(0f, height * 0.5f, 0f);

            var tilt = player.GetComponent<CameraTiltController>();
            if (tilt != null)
                tilt.ExplicitCamera = vcam;

            var cameraController = player.GetComponent<PlayerCameraController>();
            if (cameraController != null)
            {
                var data = new SerializedObject(cameraController);
                if (data.FindProperty("cinemachineCamera").objectReferenceValue == null)
                    data.FindProperty("cinemachineCamera").objectReferenceValue = vcam;
                if (data.FindProperty("cinemachineFollow").objectReferenceValue == null)
                    data.FindProperty("cinemachineFollow").objectReferenceValue = vcam.GetComponent<CinemachineFollow>();
                if (data.FindProperty("cinemachineLookAt").objectReferenceValue == null)
                    data.FindProperty("cinemachineLookAt").objectReferenceValue = vcam.GetComponent<CinemachineHardLookAt>();
                if (data.FindProperty("target").objectReferenceValue == null)
                    data.FindProperty("target").objectReferenceValue = player.transform;
                data.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }

    private static void ApplyReferences(PlayerLocomotion movement, InputActionAsset inputs, Transform view, Animator animator)
    {
        var data = new SerializedObject(movement);
        data.FindProperty("inputActions").objectReferenceValue = inputs;
        data.FindProperty("animator").objectReferenceValue = animator;
        data.FindProperty("view").objectReferenceValue = view;
        data.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ApplyCalibration(PlayerLocomotion movement, InputActionAsset inputs, Transform view, Animator animator, ProjectRestartStrideCalibration.Result calibration)
    {
        ApplyReferences(movement, inputs, view, animator);
        var data = new SerializedObject(movement);
        data.FindProperty("slowWalkSpeed").floatValue = calibration.SlowWalkSpeed;
        data.FindProperty("walkSpeed").floatValue = calibration.WalkSpeed;
        data.FindProperty("runSpeed").floatValue = calibration.RunSpeed;
        data.FindProperty("cycleDurations").vector4Value = calibration.CycleDurations;
        data.FindProperty("maxJumps").intValue = 2;
        data.FindProperty("doubleJumpVariantCount").intValue = 2;
        data.ApplyModifiedPropertiesWithoutUndo();
    }

    [MenuItem("Tools/Project Restart/Step 6 - Check Live Player")]
    public static void CheckLivePlayer()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[ProjectRestart] Enter Play Mode in 00_TestBlockout before checking live movement.");
            return;
        }
        try
        {
            var scene = SceneManager.GetActiveScene();
            Require(scene.path == ScenePath, "Open 00_TestBlockout before checking live movement.");
            VerifyScene(scene);
            var movement = scene.GetRootGameObjects().SelectMany(item => item.GetComponentsInChildren<PlayerLocomotion>()).Single();
            var animator = movement.GetComponentInChildren<Animator>();
            Debug.Log("[ProjectRestart] Step 6 live snapshot: grounded=" + movement.GetComponent<CharacterController>().isGrounded + "; actualSpeed=" + movement.ActualPlanarSpeed.ToString("F3") + " m/s; Speed=" + animator.GetFloat("Speed").ToString("F3") + "; dashing=" + movement.IsDashing + "; Apply Root Motion=" + animator.applyRootMotion + ". This snapshot is not proof of input coverage or zero foot sliding; test slow-walking, walking, sprinting and dashing visually in the Game view.");
        }
        catch (Exception exception)
        {
            Debug.LogError("[ProjectRestart] Step 6 live check FAIL: " + exception.Message);
        }
    }

    [MenuItem("Tools/Project Restart/Dump Shadow Diagnostics")]
    public static void DumpShadowDiagnostics()
    {
        var scene = SceneManager.GetActiveScene();
        var light = scene.GetRootGameObjects()
            .SelectMany(item => item.GetComponentsInChildren<Light>(true))
            .FirstOrDefault(item => item.type == LightType.Directional);
        if (light == null)
        {
            Debug.LogWarning("[ShadowDiag] No directional light in scene.");
            return;
        }

        var rp = (QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
        var camera = scene.GetRootGameObjects().SelectMany(item => item.GetComponentsInChildren<Camera>()).FirstOrDefault();
        var additional = camera != null ? camera.GetComponent<UniversalAdditionalCameraData>() : null;
        var wall = scene.GetRootGameObjects().Where(item => item.name.StartsWith("Wall Run Test Wall"))
            .Select(item => item.GetComponent<Renderer>()).Where(item => item != null).ToArray();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ShadowDiag] ---");
        sb.AppendLine($"  quality level: {QualitySettings.names[QualitySettings.GetQualityLevel()]} | renderPipeline asset: {(rp != null ? rp.name : "NULL")}");
        sb.AppendLine($"  quality shadows: {QualitySettings.shadows} | quality shadowDistance: {QualitySettings.shadowDistance}");
        if (rp != null)
            sb.AppendLine($"  URP: mainLightShadows={rp.supportsMainLightShadows} softShadows={rp.supportsSoftShadows} shadowDistance={rp.shadowDistance} cascadeCount={rp.shadowCascadeCount}");
        sb.AppendLine($"  light '{light.name}': enabled={light.enabled} shadows={light.shadows} strength={light.shadowStrength} bias={light.shadowBias} normalBias={light.shadowNormalBias} nearPlane={light.shadowNearPlane} useViewFrustumForShadowCasterCull={light.useViewFrustumForShadowCasterCull} cullingMask={light.cullingMask} euler={light.transform.eulerAngles}");
        if (camera != null)
            sb.AppendLine($"  camera '{camera.name}': enabled={camera.enabled} renderShadows={(additional != null ? additional.renderShadows.ToString() : "no additional data")} far={camera.farClipPlane} pos={camera.transform.position}");
        foreach (var renderer in wall)
            sb.AppendLine($"  wall '{renderer.name}': enabled={renderer.enabled} isVisible={renderer.isVisible} castShadows={renderer.shadowCastingMode} receiveShadows={renderer.receiveShadows} layer={renderer.gameObject.layer} bounds={renderer.bounds} mat={(renderer.sharedMaterial != null ? renderer.sharedMaterial.name + "/" + renderer.sharedMaterial.shader.name : "NULL")}");
        Debug.Log(sb.ToString());
    }

    private static Material GetGroundMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(GroundMaterialPath);
        if (material != null)
            return material;
        Require(!File.Exists(GroundMaterialPath) && !File.Exists(GroundMaterialPath + ".meta"), "Ground material path is occupied.");
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        Require(shader != null && shader.isSupported, "URP Lit is unavailable.");
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(GroundTexturePath);
        if (texture == null)
        {
            Require(!File.Exists(GroundTexturePath) && !File.Exists(GroundTexturePath + ".meta"), "Ground texture path is occupied.");
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "TestGroundChecker",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            var dark = new Color(0.25f, 0.28f, 0.3f);
            var light = new Color(0.4f, 0.43f, 0.45f);
            texture.SetPixels(new[] { dark, light, light, dark });
            texture.Apply();
            AssetDatabase.CreateAsset(texture, GroundTexturePath);
        }
        material = new Material(shader) { name = "TestGround" };
        material.SetTexture("_BaseMap", texture);
        material.SetTextureScale("_BaseMap", Vector2.one * 100f);
        material.SetFloat("_Smoothness", 0f);
        AssetDatabase.CreateAsset(material, GroundMaterialPath);
        AssetDatabase.SaveAssetIfDirty(texture);
        AssetDatabase.SaveAssetIfDirty(material);
        return material;
    }

    private static void EnsureWallRunTestGeometry(Scene scene)
    {
        var wall = scene.GetRootGameObjects().SingleOrDefault(item => item.name == "Wall Run Test Wall");
        if (wall == null)
        {
            wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall Run Test Wall";
            SceneManager.MoveGameObjectToScene(wall, scene);
        }

        wall.transform.SetPositionAndRotation(new Vector3(0f, 4.24f, 10f), Quaternion.identity);
        wall.transform.localScale = new Vector3(40f, 10f, 0.5f);
        var renderer = wall.GetComponent<Renderer>();
        renderer.sharedMaterial = GetGroundMaterial();
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
        renderer.allowOcclusionWhenDynamic = false;

        var directionalLight = scene.GetRootGameObjects()
            .SelectMany(item => item.GetComponentsInChildren<Light>(true))
            .FirstOrDefault(item => item.type == LightType.Directional);
        if (directionalLight != null)
        {
            directionalLight.transform.rotation = Quaternion.Euler(50f, 150f, 0f);
            directionalLight.shadows = LightShadows.Soft;
            directionalLight.shadowBias = 0.02f;
            directionalLight.shadowNormalBias = 0.2f;
            directionalLight.shadowNearPlane = 0.05f;
            directionalLight.useViewFrustumForShadowCasterCull = false;
        }
    }

    private static void VerifyScene(Scene scene)
    {
        VerifyPipeline();
        var roots = scene.GetRootGameObjects();
        var movements = roots.SelectMany(item => item.GetComponentsInChildren<PlayerLocomotion>()).ToArray();
        Require(movements.Length == 1 && movements[0].enabled, "Expected one enabled PlayerLocomotion.");
        var movement = movements[0];
        var character = movement.GetComponent<CharacterController>();
        Require(character != null && character.enabled && character.height > character.radius * 2f && character.minMoveDistance == 0f, "CharacterController setup is invalid.");
        Require(movement.GetComponentsInChildren<Rigidbody>(true).Length == 0, "Player must use CharacterController, not Rigidbody movement.");
        var data = new SerializedObject(movement);
        var animator = data.FindProperty("animator").objectReferenceValue as Animator;
        var inputs = data.FindProperty("inputActions").objectReferenceValue as InputActionAsset;
        var view = data.FindProperty("view").objectReferenceValue as Transform;
        Require(inputs != null && inputs.FindAction("Player/Move") != null && inputs.FindAction("Player/Sprint") != null && inputs.FindAction("Player/Jump") != null && view != null, "Movement input/camera wiring is missing.");
        Require(movement.GetComponent<PlayerInputSettings>() != null, "Player needs PlayerInputSettings for control-scheme overlay.");
        Require(animator != null && animator.enabled && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman && !animator.applyRootMotion, "Hero must have a valid Humanoid Avatar and Apply Root Motion disabled.");
        var controller = animator.runtimeAnimatorController as AnimatorController;
        Require(controller != null && AssetDatabase.GetAssetPath(controller) == ProjectRestartLocomotion.ControllerPath && controller.layers.Length == 1, "Hero must use the single-layer PlayerBase.controller.");
        var hasDoubleJump = controller.parameters.Any(p => p.name == "DoubleJump" && p.type == AnimatorControllerParameterType.Trigger) &&
                            controller.parameters.Any(p => p.name == "JumpVariant" && p.type == AnimatorControllerParameterType.Float) &&
                            controller.parameters.Any(p => p.name == "VerticalSpeed" && p.type == AnimatorControllerParameterType.Float);
        Require(hasDoubleJump, "PlayerBase.controller is missing DoubleJump, JumpVariant, or VerticalSpeed. Run Tools > Add Jump and Crouch States.");
        var machine = controller.layers[0].stateMachine;
        Require(machine.states.Any(item => item.state.name == "JumpDoubleAir"), "PlayerBase.controller is missing the JumpDoubleAir state. Run Tools > Add Jump and Crouch States.");
        Require(machine.states.Any(item => item.state.name == "JumpDoubleFall"), "PlayerBase.controller is missing the JumpDoubleFall state. Run Tools > Add Jump and Crouch States.");
        var slowWalk = data.FindProperty("slowWalkSpeed").floatValue;
        var walk = data.FindProperty("walkSpeed").floatValue;
        var run = data.FindProperty("runSpeed").floatValue;
        Require(slowWalk > 0f && walk > slowWalk && run > walk, "Locomotion speeds must be ordered: slowWalk < walk < run.");
        VerifyBlendMath(slowWalk, walk, run, data.FindProperty("cycleDurations").vector4Value);
        var cameras = roots.SelectMany(item => item.GetComponentsInChildren<Camera>()).ToArray();
        Require(cameras.Length == 1 && cameras[0].enabled && cameras[0].transform == view && cameras[0].GetComponent<UniversalAdditionalCameraData>() != null, "Expected one wired URP camera.");
        var brain = cameras[0].GetComponent<CinemachineBrain>();
        Require(brain != null && brain.enabled, "Main Camera must have a CinemachineBrain for the camera-tilt module.");
        var vcam = UnityEngine.Object.FindFirstObjectByType<CinemachineCamera>();
        Require(vcam != null && vcam.enabled, "Scene must contain an active CinemachineCamera.");
        Require(movement.GetComponent<PlayerCameraController>() != null, "Player must have a PlayerCameraController for orbit and motion.");
        Require(movement.GetComponent<PlayerState>() != null, "Player must have a PlayerState module for shared state flags.");
        Require(movement.GetComponent<CameraTiltController>() != null, "Player must have a CameraTiltController for displacement roll.");
        Require(movement.GetComponent<SlideController>() != null, "Player must have a SlideController for the ground-lunge state.");
        Require(movement.GetComponent<LandingRollController>() != null, "Player must have a LandingRollController for landing rolls.");
        Require(movement.GetComponent<WallRunController>() != null, "Player must have a WallRunController for wall runs.");
        var ground = roots.SingleOrDefault(item => item.name == "Test Ground");
        Require(ground != null && ground.GetComponent<MeshCollider>() != null && ground.GetComponent<MeshCollider>().enabled, "Flat ground collider is missing.");
        var wall = roots.SingleOrDefault(item => item.name == "Wall Run Test Wall");
        Require(wall != null && wall.GetComponent<BoxCollider>() != null && wall.GetComponent<BoxCollider>().enabled, "Wall-run test collider is missing.");
        foreach (var renderer in roots.SelectMany(item => item.GetComponentsInChildren<Renderer>()).Where(item => item.enabled && !item.forceRenderingOff))
        {
            if (renderer is SkinnedMeshRenderer skinned && !ProjectRestartStrideCalibration.HasGeometry(skinned))
                continue;
            foreach (var material in renderer.sharedMaterials)
                Require(material != null && material.shader != null && material.shader.isSupported && material.GetTag("RenderPipeline", false) == "UniversalPipeline", "Missing or non-URP material on " + renderer.name + "; resolve step 3 before confirming the visual test.");
        }
    }

    private static void VerifyBlendMath(float slowWalk, float walk, float run, Vector4 durations)
    {
        Require(slowWalk > 0f && walk > slowWalk && run > walk && durations.x > 0f && durations.y > 0f && durations.z > 0f && durations.w > 0f, "Invalid stride calibration.");
        Require(Mathf.Approximately(PlayerLocomotion.SpeedFromBlend(0f, slowWalk, walk, run, durations), 0f) &&
                Mathf.Approximately(PlayerLocomotion.SpeedFromBlend(1f / 3f, slowWalk, walk, run, durations), slowWalk) &&
                Mathf.Approximately(PlayerLocomotion.SpeedFromBlend(2f / 3f, slowWalk, walk, run, durations), walk) &&
                Mathf.Approximately(PlayerLocomotion.SpeedFromBlend(1f, slowWalk, walk, run, durations), run), "Blend speed endpoints failed.");
        var previous = -1f;
        for (var index = 0; index <= 20; index++)
        {
            var parameter = index / 20f;
            var speed = PlayerLocomotion.SpeedFromBlend(parameter, slowWalk, walk, run, durations);
            Require(speed >= previous && Mathf.Abs(PlayerLocomotion.BlendFromSpeed(speed, slowWalk, walk, run, durations) - parameter) < 0.0001f, "Blend speed monotonicity/round-trip test failed.");
            previous = speed;
        }
    }

    private static void VerifyPipeline()
    {
        Require((QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline) is UniversalRenderPipelineAsset, "Active quality level must use URP.");
        var settings = GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>();
        Require(settings != null && !settings.enableRenderCompatibilityMode, "Render Graph must be enabled; project pipeline settings were not changed by this tool.");
#if !ENABLE_INPUT_SYSTEM
        throw new InvalidOperationException("Enable the Input System in Player Settings; this tool does not change project configuration.");
#endif
    }

    private static void ReportReady()
    {
        var missing = SceneManager.GetActiveScene().GetRootGameObjects()
            .SelectMany(ProjectRestartStrideCalibration.FindMissingGeometry).ToArray();
        var status = missing.Length == 0 ? "PASS" : "READY WITH MISSING MESHES";
        var message = "[ProjectRestart] Step 6 setup " + status + ": " + ScenePath + "; flat checker plane, Fantasy Hero + CharacterController, PlayerBase.controller with Apply Root Motion disabled, Input System movement, Cinemachine follow camera and CameraTiltController. Play Mode controls: WASD/left stick=walk; small stick input=slow walk; hold Left Shift/left-stick press=sprint/run; Space/gamepad east button=slide. Click the Game view for keyboard focus. Setup and blend-math checks passed; actual keyboard/gamepad movement and no visible foot sliding still require your Play Mode confirmation. Camera roll is triggered only by displacement states (Slide, WallDash).";
        if (missing.Length == 0)
            Debug.Log(message);
        else
            Debug.LogWarning(message + " " + missing.Length + " active renderers have missing/empty meshes: " + string.Join(", ", missing.Take(6).Select(renderer => renderer.name)) + (missing.Length > 6 ? " (first 6 shown)" : "") + ". Bounds calculation excluded them; asset references were NOT repaired. Full visual verification remains blocked by these missing meshes.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
