using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ProjectRestartHeroRig
{
    private static readonly string[] ModelGuids =
    {
        "038320a534bd8b444b43734860d1a211",
        "98a56ff2c8ee98645a4d3f79fa1506c5"
    };

    [MenuItem("Tools/Project Restart/Step 2 - Verify Fantasy Hero Humanoid Rigs")]
    public static void Verify()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Step 2 deferred: exit Play Mode and wait for compilation/import to finish, then run again.");
            return;
        }

        var failures = 0;
        foreach (var guid in ModelGuids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            try
            {
                VerifyModel(path);
            }
            catch (Exception exception)
            {
                failures++;
                Debug.LogError("[ProjectRestart] Step 2 model FAIL: " + path + "; " + exception.Message);
            }
        }

        var primaryModel = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(ModelGuids[0]));
        if (primaryModel != null)
        {
            Selection.activeObject = primaryModel;
            EditorGUIUtility.PingObject(primaryModel);
        }

        if (failures == 0)
            Debug.Log("[ProjectRestart] Step 2 API checks PASS: both Fantasy Hero models have valid Humanoid Avatars and all required bones resolve. No rig settings or bone mappings were changed. Inspect Rig > Configure for each model to confirm the mapping UI has no errors; this API check does not replace that visual confirmation. Stopped before step 3.");
        else
            Debug.LogError("[ProjectRestart] Step 2 FAIL: " + failures + " of 2 models failed. No rig settings or bone mappings were changed; share the model failures before proceeding.");
    }

    [MenuItem("Tools/Project Restart/Step 4 - Select CLazy Walk")]
    public static void SelectWalk()
    {
        SelectRetargetClip("6e950ce0deb6c22419d4f264e8441f3b", "Mvm_Walk");
    }

    [MenuItem("Tools/Project Restart/Step 4 - Select Grruzam Attack")]
    public static void SelectAttack()
    {
        SelectRetargetClip("9b7d649f82b34544e92cb4f402d0f424", "Attack_3Combo_1_Inplace");
    }

    private static void SelectRetargetClip(string guid, string clipName)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Step 4 deferred: exit Play Mode and wait for compilation/import to finish, then run again.");
            return;
        }

        var path = AssetDatabase.GUIDToAssetPath(guid);
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        var heroPath = AssetDatabase.GUIDToAssetPath("071b4da7cfff18347a371c677585b749");
        var hero = AssetDatabase.LoadAssetAtPath<GameObject>(heroPath);
        var animator = hero != null ? hero.GetComponent<Animator>() : null;
        if (importer == null || importer.animationType != ModelImporterAnimationType.Human ||
            animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
        {
            Debug.LogError("[ProjectRestart] Step 4 preflight FAIL: source must be Humanoid and Chr_FantasyHero_Preset_1 must have a valid Humanoid Avatar.");
            return;
        }

        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (!(asset is AnimationClip clip) || clip.name != clipName)
                continue;
            if (clip.legacy || !clip.isHumanMotion || clip.length <= 0)
            {
                Debug.LogError("[ProjectRestart] Step 4 preflight FAIL: " + clipName + " is not a nonempty Mecanim Humanoid clip.", clip);
                return;
            }

            Selection.activeObject = clip;
            EditorGUIUtility.PingObject(hero);
            Debug.Log("[ProjectRestart] Step 4 preview READY, not a retarget PASS: " + clipName + " from " + path + "; target=" + heroPath + "; Avatar=" + animator.avatar.name + ". In the Inspector clip preview, drag the highlighted Hero prefab into the preview area and play/scrub the entire clip. Verify the preview shows the Hero, not the source character. Check feet, knees, shoulders, hands and spine for distortion. Source metadata records copied-avatar bone-length and discarded-translation warnings. Report visual success/failure for this clip; no assets or rig settings were changed.", clip);
            return;
        }

        Debug.LogError("[ProjectRestart] Step 4 preflight FAIL: clip " + clipName + " was not found in " + path);
    }

    private static void VerifyModel(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null)
            throw new InvalidOperationException("ModelImporter is missing.");
        if (importer.animationType != ModelImporterAnimationType.Human)
            throw new InvalidOperationException("Animation Type is " + importer.animationType + ", not Humanoid.");

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (model == null)
            throw new InvalidOperationException("Imported model could not be loaded.");
        var previewScene = EditorSceneManager.NewPreviewScene();
        try
        {
            var instance = PrefabUtility.InstantiatePrefab(model, previewScene) as GameObject;
            if (instance == null)
                throw new InvalidOperationException("Could not instantiate the model in a temporary preview scene.");
            var animator = instance.GetComponent<Animator>();
            if (animator == null || animator.avatar == null)
                throw new InvalidOperationException("Root Animator or Avatar is missing.");
            var avatar = animator.avatar;
            if (!avatar.isValid || !avatar.isHuman)
                throw new InvalidOperationException("Avatar " + avatar.name + ": isValid=" + avatar.isValid + ", isHuman=" + avatar.isHuman);

            var requiredBones = 0;
            for (var index = 0; index < (int)HumanBodyBones.LastBone; index++)
            {
                if (!HumanTrait.RequiredBone(index))
                    continue;
                if (animator.GetBoneTransform((HumanBodyBones)index) == null)
                    throw new InvalidOperationException("Required Humanoid bone is missing: " + (HumanBodyBones)index);
                requiredBones++;
            }

            if (instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0)
                throw new InvalidOperationException("No skinned character meshes were found.");

            Debug.Log("[ProjectRestart] Step 2 model PASS: " + path + "; Animation Type=Humanoid; Avatar Definition=" + importer.avatarSetup + "; Avatar=" + avatar.name + "; isValid=True; isHuman=True; required bones=" + requiredBones + "; no remapping performed.", model);
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }
}
