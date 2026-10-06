using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Explicit, reversible visual-only migration. Never runs on import.</summary>
public static class ProjectRestartProtagonist
{
    private const string ModelPath = "Assets/Character_Unity.fbx";
    private const string PrefabPath = "Assets/_Project/Prefabs/ProtagonistVisual.prefab";
    private const string VisualName = "ProtagonistVisual";

    [MenuItem("Tools/Project Restart/Player Model/Use New Protagonist in Gameplay Scenes")]
    public static void Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before replacing the player visual.");
        var active = SceneManager.GetActiveScene();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null) throw new InvalidOperationException("Missing " + ModelPath);
        var source = active.GetRootGameObjects().FirstOrDefault(g =>
            PrefabUtility.GetCorrespondingObjectFromSource(g) == model && g.GetComponentInParent<PlayerLocomotion>() == null);
        // Use the placed model's material overrides, without changing the imported FBX.
        var template = UnityEngine.Object.Instantiate(source != null ? source : model);
        template.name = VisualName;
        template.SetActive(true);
        template.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        template.transform.localScale = Vector3.one;
        try
        {
            var anim = template.GetComponentInChildren<Animator>(true);
            Require(anim != null && anim.avatar != null && anim.avatar.isValid && anim.avatar.isHuman,
                "New model must have a valid native Humanoid Avatar.");
            Require(anim.gameObject == template, "Expected imported Animator on model root.");
            Require(!template.GetComponentsInChildren<Collider>(true).Any() &&
                    !template.GetComponentsInChildren<Rigidbody>(true).Any(), "Visual contains physics components.");
            foreach (var renderer in template.GetComponentsInChildren<Renderer>(true))
                foreach (var mat in renderer.sharedMaterials)
                    Require(mat != null && mat.shader != null && mat.shader.isSupported,
                        "Missing/unsupported material on " + renderer.name);
            anim.applyRootMotion = false;
            anim.runtimeAnimatorController = null;
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Prefabs"))
                AssetDatabase.CreateFolder("Assets/_Project", "Prefabs");
            PrefabUtility.SaveAsPrefabAsset(template, PrefabPath);
        }
        finally { ClearSelectionOf(template); UnityEngine.Object.DestroyImmediate(template); }

        var paths = new[] { active.path, "Assets/_Project/Scenes/00_TestBlockout.unity",
            "Assets/_Project/Scenes/01_Courtyard.unity" }.Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        foreach (var path in paths)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null) continue;
            var scene = SceneManager.GetSceneByPath(path);
            var opened = !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                var players = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PlayerLocomotion>(true)).ToArray();
                foreach (var player in players)
                {
                    Replace(player, prefab);
                    CalibrateGrounding(player);
                }
                if (scene == active && source != null) source.SetActive(false);
                if (players.Length > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                }
            }
            finally { if (opened) { ClearSceneSelection(scene); EditorSceneManager.CloseScene(scene, true); } }
        }
        SceneManager.SetActiveScene(active);
        AssetDatabase.SaveAssets();
        Debug.Log("[Protagonist] Gameplay visual replacement saved. Old rigs and the placed reference are disabled, not deleted. Character creation/save data retained. Run Play Mode validation.");
    }

    private static void Replace(PlayerLocomotion player, GameObject prefab)
    {
        if (player.transform.Find(VisualName) != null)
        { Debug.Log("[Protagonist] Already installed: " + player.gameObject.scene.path); return; }
        var old = player.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.enabled && a.avatar != null && a.avatar.isHuman);
        var cc = player.GetComponent<CharacterController>();
        Require(old != null && old.runtimeAnimatorController != null && cc != null, "Player rig/controller/capsule missing.");
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab, player.transform);
        visual.name = VisualName;
        visual.transform.SetAsFirstSibling();
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = Vector3.one;
        var anim = visual.GetComponent<Animator>();
        anim.runtimeAnimatorController = old.runtimeAnimatorController;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        anim.updateMode = AnimatorUpdateMode.Normal;
        var bounds = BoundsOf(visual);
        Require(bounds.size.y > 0.01f, "New model has no usable mesh bounds.");
        var targetHeight = cc.height * Mathf.Abs(player.transform.lossyScale.y);
        visual.transform.localScale *= targetHeight / bounds.size.y;
        bounds = BoundsOf(visual);
        var floor = player.transform.TransformPoint(cc.center - Vector3.up * cc.height * 0.5f).y;
        visual.transform.position += Vector3.up * (floor - bounds.min.y);
        foreach (var t in visual.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = old.gameObject.layer;

        var relay = visual.AddComponent<RootMotionRelay>();
        var oldRelay = old.GetComponent<RootMotionRelay>();
        if (oldRelay != null) EditorUtility.CopySerialized(oldRelay, relay);
        var grounding = visual.AddComponent<FootGrounding>();
        var oldGrounding = old.GetComponent<FootGrounding>();
        if (oldGrounding != null) EditorUtility.CopySerialized(oldGrounding, grounding);
        var groundData = new SerializedObject(grounding);
        groundData.FindProperty("visualRoot").objectReferenceValue = visual.transform;
        groundData.FindProperty("character").objectReferenceValue = cc;
        groundData.ApplyModifiedPropertiesWithoutUndo();

        var map = new Dictionary<UnityEngine.Object, UnityEngine.Object>
        {
            [old] = anim, [old.transform] = visual.transform, [old.gameObject] = visual
        };
        if (oldRelay != null) map[oldRelay] = relay;
        if (oldGrounding != null) map[oldGrounding] = grounding;
        for (var i = 0; i < (int)HumanBodyBones.LastBone; ++i)
        {
            var from = old.GetBoneTransform((HumanBodyBones)i);
            var to = anim.GetBoneTransform((HumanBodyBones)i);
            if (from != null && to != null) { map[from] = to; map[from.gameObject] = to.gameObject; }
        }
        var renderer = visual.GetComponentInChildren<SkinnedMeshRenderer>(true);
        foreach (var r in old.GetComponentsInChildren<Renderer>(true)) map[r] = renderer;
        foreach (var component in player.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (component == null || component.transform.IsChildOf(old.transform)) continue;
            var serialized = new SerializedObject(component);
            var p = serialized.GetIterator();
            while (p.Next(true))
                if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue != null &&
                    map.TryGetValue(p.objectReferenceValue, out var replacement)) p.objectReferenceValue = replacement;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        var customizer = player.GetComponent<PlayerCustomizer>();
        if (customizer != null)
        {
            var so = new SerializedObject(customizer);
            so.FindProperty("applySavedAppearance").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        // Keep the old avatar out of all GetComponentsInChildren(true) runtime searches.
        var backup = new GameObject("Previous Player Visual (disabled backup)");
        SceneManager.MoveGameObjectToScene(backup, player.gameObject.scene);
        backup.SetActive(false);
        old.transform.SetParent(backup.transform, true);
        old.enabled = false;
        // Never select the visual here: Replace also runs inside additively
        // opened scenes that CloseScene destroys in memory — a selection pointing
        // into a closed scene is the null SerializedObject the Inspector then
        // throws on (AnimatorInspector/TransformInspector/GameObjectInspector).
        Debug.Log($"[Protagonist] {player.gameObject.scene.path}: native humanoid={anim.avatar.isValid}, height={BoundsOf(visual).size.y:F3}m, scale={visual.transform.localScale}, capsule unchanged; grip={anim.GetBoneTransform(HumanBodyBones.RightHand).name}; saved stats preserved.");
    }

    private static Bounds BoundsOf(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
        Require(renderers.Length > 0, "No skinned mesh on protagonist.");
        var result = renderers[0].bounds;
        foreach (var r in renderers.Skip(1)) result.Encapsulate(r.bounds);
        return result;
    }

    [MenuItem("Tools/Project Restart/Player Model/Repair Protagonist Grounding")]
    public static void RepairGrounding()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Exit Play Mode first.");
        var active = SceneManager.GetActiveScene();
        foreach (var path in new[] { active.path, "Assets/_Project/Scenes/00_TestBlockout.unity",
                     "Assets/_Project/Scenes/01_Courtyard.unity" }.Where(p => !string.IsNullOrEmpty(p)).Distinct())
        {
            var scene = SceneManager.GetSceneByPath(path);
            var opened = !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                foreach (var player in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PlayerLocomotion>(true)))
                    if (player.transform.Find(VisualName) != null) CalibrateGrounding(player);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            finally { if (opened) { ClearSceneSelection(scene); EditorSceneManager.CloseScene(scene, true); } }
        }
        SceneManager.SetActiveScene(active);
    }

    [MenuItem("Tools/Project Restart/Player Model/Align Active Player Visual")]
    public static void AlignActiveVisual()
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Exit Play Mode first.");
        Selection.objects = Array.Empty<UnityEngine.Object>();
        var scene = SceneManager.GetActiveScene();
        foreach(var player in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<PlayerLocomotion>(true)))
        {
            var visual = player.transform.Find(VisualName);
            if(!visual) continue;
            Undo.RecordObject(visual, "Align player visual with controller");
            var before = visual.localPosition;
            visual.localPosition = new Vector3(0, before.y, 0);
            CalibrateGrounding(player);
            PrefabUtility.RecordPrefabInstancePropertyModifications(visual);
            Debug.Log($"[PlayerAlignment] {before} -> {visual.localPosition}; capsule and scale preserved.");
        }
        EditorSceneManager.MarkSceneDirty(scene);
    }

    private static void CalibrateGrounding(PlayerLocomotion player)
    {
        var visual = player.transform.Find(VisualName);
        var cc = player.GetComponent<CharacterController>();
        var idle = AssetDatabase.LoadAllAssetsAtPath("Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_Katana_Blade/1_Movements/1__Idle/M_katana_Blade@Idle_ver_B.FBX")
            .OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        Require(visual != null && cc != null && idle != null, "Grounding calibration requires visual, capsule and katana idle.");
        Require(!AnimationMode.InAnimationMode(), "Exit animation preview before calibration.");
        var sample = UnityEngine.Object.Instantiate(visual.gameObject, visual.parent);
        float sole;
        try
        {
            sample.hideFlags = HideFlags.HideAndDontSave;
            AnimationMode.StartAnimationMode();
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(sample, idle, 0f);
            AnimationMode.EndSampling();
            var animator = sample.GetComponent<Animator>();
            var meshes = sample.GetComponentsInChildren<SkinnedMeshRenderer>();
            sole = Mathf.Min(
                GroundingSolver.MeasureFootLowest(meshes, animator.GetBoneTransform(HumanBodyBones.LeftFoot), 0.20f),
                GroundingSolver.MeasureFootLowest(meshes, animator.GetBoneTransform(HumanBodyBones.RightFoot), 0.20f));
            Require(sole < float.MaxValue && !float.IsNaN(sole), "Could not measure boot sole in idle pose.");
        }
        finally
        {
            AnimationMode.StopAnimationMode();
            ClearSelectionOf(sample);
            UnityEngine.Object.DestroyImmediate(sample);
        }
        var floor = player.transform.TransformPoint(cc.center - Vector3.up * cc.height * 0.5f).y;
        var before = visual.localPosition.y;
        visual.position += Vector3.up * (floor - sole);
        PrefabUtility.RecordPrefabInstancePropertyModifications(visual);
        var grounding = new SerializedObject(visual.GetComponent<FootGrounding>());
        grounding.FindProperty("useMeshBoundsGuard").boolValue = false;
        grounding.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log($"[ProtagonistGrounding] {player.gameObject.scene.name}: sampled boot sole={sole:F3}, capsule floor={floor:F3}, visual Y {before:F3} -> {visual.localPosition.y:F3}; padded-bounds guard disabled.");
    }

    /// <summary>Inspector targets that outlive their object surface as
    /// SerializedObjectNotCreatableException / MissingReferenceException on the
    /// next repaint — drop doomed objects from the selection before destroying.</summary>
    private static void ClearSelectionOf(UnityEngine.Object doomed)
    {
        if (doomed != null && Selection.objects.Contains(doomed))
            Selection.objects = Selection.objects.Where(o => o != null && o != doomed).ToArray();
    }

    private static void ClearSceneSelection(Scene scene)
    {
        Selection.objects = Selection.objects.Where(o =>
        {
            if (o is GameObject go) return go.scene != scene;
            return !(o is Component c) || c.gameObject.scene != scene;
        }).ToArray();
    }

    private static void Require(bool ok, string message)
    { if (!ok) throw new InvalidOperationException("[Protagonist] " + message); }
}
