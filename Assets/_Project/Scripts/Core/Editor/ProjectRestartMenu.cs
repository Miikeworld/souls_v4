using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the Souls-style front end: a generated 00_MainMenu scene (title →
/// main menu → character creation over a slowly rotating Fantasy Hero), the
/// PlayerCustomizer wiring on the gameplay player, and Build Settings order.
/// Re-running is safe — the menu scene is generated content and is rebuilt
/// from scratch each time; the test scene only gains the customizer component.
/// </summary>
public static class ProjectRestartMenu
{
    private const string MenuScenePath = "Assets/_Project/Scenes/00_MainMenu.unity";
    private const string TestScenePath = ProjectRestartTestScene.ScenePath;
    private const string StageMaterialPath = "Assets/_Project/Materials/MenuStage.mat";
    private const string HeroPrefabGuid = "071b4da7cfff18347a371c677585b749";
    private const string InputsGuid = "052faaac586de48259a63d0c4782560b";
    private const string KatanaSetPath = "Assets/_Project/Combat/Katana.asset";
    private const string BigSwordSetPath = "Assets/_Project/Combat/BigSword.asset";

    [MenuItem("Tools/Project Restart/Build Main Menu + Character Creation")]
    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Menu build deferred: exit Play Mode and wait for compilation/import to finish, then run again.");
            return;
        }

        try
        {
            var hero = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(HeroPrefabGuid));
            var inputs = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetDatabase.GUIDToAssetPath(InputsGuid));
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
            var katana = AssetDatabase.LoadAssetAtPath<WeaponSet>(KatanaSetPath);
            var bigSword = AssetDatabase.LoadAssetAtPath<WeaponSet>(BigSwordSetPath);
            Require(hero != null && inputs != null && controller != null, "Hero prefab, input actions or PlayerBase.controller is missing.");
            Require(AssetDatabase.LoadAssetAtPath<SceneAsset>(TestScenePath) != null,
                $"Test scene is missing at {TestScenePath} — run Step 6 first.");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            BuildMenuScene(hero, inputs, controller);
            WireTestScene(katana, bigSword);
            EnsureBuildSettings();

            // Leave the user looking at the finished menu scene.
            var menu = EditorSceneManager.OpenScene(MenuScenePath, OpenSceneMode.Single);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(MenuScenePath);
            Debug.Log("[ProjectRestart] Main menu + character creation built. " +
                      "Play the menu scene: title → Begin a New Game → creation → Confirm loads the test scene with your character.");
        }
        catch (Exception exception)
        {
            Debug.LogError("[ProjectRestart] Menu build FAIL: " + exception.Message);
            Debug.LogException(exception);
        }
    }

    // ---------- menu scene ----------

    private static void BuildMenuScene(GameObject heroPrefab, InputActionAsset inputs, AnimatorController controller)
    {
        Scene scene;
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(MenuScenePath) != null)
        {
            scene = EditorSceneManager.OpenScene(MenuScenePath, OpenSceneMode.Single);
            foreach (var root in scene.GetRootGameObjects())
                UnityEngine.Object.DestroyImmediate(root);
        }
        else
        {
            Require(AssetDatabase.IsValidFolder("Assets/_Project/Scenes"), "Assets/_Project/Scenes is missing.");
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        // Mood: dark violet stage (matches the UI plum/violet palette, still
        // readable against black hair), light fog, one warm key + one cold rim.
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.08f, 0.06f, 0.1f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = 0.055f;
        RenderSettings.fogColor = new Color(0.15f, 0.105f, 0.185f);

        var stageMat = GetStageMaterial();

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Stage Floor";
        floor.transform.localScale = new Vector3(14f, 1f, 14f);
        floor.GetComponent<Renderer>().sharedMaterial = stageMat;

        var pedestal = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pedestal.name = "Pedestal";
        pedestal.transform.position = new Vector3(0.85f, 0.045f, 0f);
        pedestal.transform.localScale = new Vector3(1.1f, 0.045f, 1.1f);
        pedestal.GetComponent<Renderer>().sharedMaterial = stageMat;

        var keyGo = new GameObject("Key Light", typeof(Light), typeof(UniversalAdditionalLightData));
        keyGo.transform.position = new Vector3(-0.4f, 2.6f, 2.2f);
        keyGo.transform.rotation = Quaternion.Euler(38f, -12f, 0f);
        var key = keyGo.GetComponent<Light>();
        key.type = LightType.Spot;
        key.color = new Color(1f, 0.82f, 0.62f);
        key.intensity = 60f;
        key.range = 14f;
        key.spotAngle = 55f;
        key.shadows = LightShadows.Soft;

        var rimGo = new GameObject("Rim Light", typeof(Light), typeof(UniversalAdditionalLightData));
        rimGo.transform.position = new Vector3(2.4f, 2.4f, -1.8f);
        rimGo.transform.rotation = Quaternion.Euler(30f, 150f, 0f);
        var rim = rimGo.GetComponent<Light>();
        rim.type = LightType.Spot;
        rim.color = new Color(0.45f, 0.55f, 0.9f);
        rim.intensity = 45f;
        rim.range = 14f;
        rim.spotAngle = 60f;

        var emberGo = new GameObject("Ember Light", typeof(Light), typeof(UniversalAdditionalLightData));
        emberGo.transform.position = new Vector3(0.85f, 0.35f, 0.9f);
        var ember = emberGo.GetComponent<Light>();
        ember.type = LightType.Point;
        ember.color = new Color(1f, 0.45f, 0.18f);
        ember.intensity = 4f;
        ember.range = 4f;

        // The hero on the pedestal — right-of-frame so the left column is UI.
        var hero = PrefabUtility.InstantiatePrefab(heroPrefab, scene) as GameObject;
        Require(hero != null, "Could not instantiate the Fantasy Hero preset.");
        hero.name = "Preview Hero";
        hero.transform.position = new Vector3(0.85f, 0.09f, 0f);
        hero.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
        var animator = hero.GetComponent<Animator>();
        if (animator != null)
        {
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
        var preview = hero.AddComponent<MenuCharacterPreview>();

        var camGo = new GameObject("Menu Camera", typeof(Camera), typeof(AudioListener), typeof(UniversalAdditionalCameraData));
        camGo.tag = "MainCamera";
        var cam = camGo.GetComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.15f, 0.105f, 0.185f);
        cam.fieldOfView = 32f;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 60f;
        camGo.transform.position = new Vector3(0.15f, 1.42f, 3.35f);
        camGo.transform.LookAt(new Vector3(0.85f, 1.05f, 0f));

        var esGo = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        var module = esGo.GetComponent<InputSystemUIInputModule>();
        module.actionsAsset = inputs;

        var bootGo = new GameObject("MenuBootstrap", typeof(MainMenuController), typeof(CharacterCreatorController));
        var mbc = bootGo.GetComponent<MainMenuController>();
        var ccc = bootGo.GetComponent<CharacterCreatorController>();

        // Serialized wiring — preview/rig/controller references.
        var pData = new SerializedObject(preview);
        pData.FindProperty("rig").objectReferenceValue = hero.transform;
        pData.FindProperty("idleController").objectReferenceValue = controller;
        pData.ApplyModifiedPropertiesWithoutUndo();

        var mData = new SerializedObject(mbc);
        mData.FindProperty("preview").objectReferenceValue = preview;
        mData.FindProperty("creator").objectReferenceValue = ccc;
        mData.FindProperty("gameSceneName").stringValue =
            System.IO.Path.GetFileNameWithoutExtension(TestScenePath);
        mData.ApplyModifiedPropertiesWithoutUndo();

        var cData = new SerializedObject(ccc);
        cData.FindProperty("preview").objectReferenceValue = preview;
        cData.ApplyModifiedPropertiesWithoutUndo();

        Require(EditorSceneManager.SaveScene(scene, MenuScenePath), "Menu scene could not be saved.");
    }

    // ---------- gameplay scene wiring ----------

    private static void WireTestScene(WeaponSet katana, WeaponSet bigSword)
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != TestScenePath)
            scene = EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
        var player = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Player");
        Require(player != null, "No 'Player' root object in the test scene.");

        var customizer = player.GetComponent<PlayerCustomizer>();
        if (customizer == null) customizer = player.AddComponent<PlayerCustomizer>();

        var data = new SerializedObject(customizer);
        if (katana != null) data.FindProperty("katanaSet").objectReferenceValue = katana;
        data.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        Require(EditorSceneManager.SaveScene(scene), "Test scene could not be saved.");
    }

    private static void EnsureBuildSettings()
    {
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        list.RemoveAll(s => s.path == MenuScenePath);
        list.Insert(0, new EditorBuildSettingsScene(MenuScenePath, true));
        if (!list.Any(s => s.path == TestScenePath))
            list.Insert(1, new EditorBuildSettingsScene(TestScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
    }

    // ---------- shared ----------

    private static Material GetStageMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(StageMaterialPath);
        if (mat != null) return mat;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        Require(shader != null, "URP Lit shader not found — is URP active?");
        mat = new Material(shader) { name = "MenuStage" };
        mat.SetColor("_BaseColor", new Color(0.035f, 0.035f, 0.045f));
        mat.SetFloat("_Smoothness", 0.12f);
        AssetDatabase.CreateAsset(mat, StageMaterialPath);
        return mat;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
