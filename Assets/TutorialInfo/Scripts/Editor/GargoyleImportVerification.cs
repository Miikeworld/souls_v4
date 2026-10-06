using System;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GargoyleImportVerification
{
    private const string SessionKey = "ProjectRestart.GargoyleImportVerification.v2";
    private static string PrefabPath => AssetDatabase.GUIDToAssetPath("2e019375636fea1468f45103b4ad73b4");

    static GargoyleImportVerification()
    {
        if (!SessionState.GetBool(SessionKey, false))
            EditorApplication.delayCall += Verify;
    }

    [MenuItem("Tools/Project Restart/Verify Gargoyle Import")]
    public static void Verify()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        SessionState.SetBool(SessionKey, true);
        AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
            throw new InvalidOperationException("Gargoyle verification failed: prefab could not be imported.");

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (PrefabUtility.IsPrefabAssetMissing(transform.gameObject))
                    throw new InvalidOperationException("Gargoyle verification failed: missing nested prefab at " + transform.name);
            }

            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0)
                throw new InvalidOperationException("Gargoyle verification failed: no skinned meshes.");

            foreach (var renderer in renderers)
            {
                if (renderer.sharedMesh == null || renderer.rootBone == null || Array.Exists(renderer.bones, bone => bone == null))
                    throw new InvalidOperationException("Gargoyle verification failed: missing mesh or bone at " + renderer.name);
            }

            var animator = root.GetComponent<Animator>();
            if (animator == null || animator.avatar == null)
                throw new InvalidOperationException("Gargoyle verification failed: missing animator or avatar.");

            var result = "[ProjectRestart] Gargoyle import PASS: " + PrefabPath + "; no missing nested prefabs; " + renderers.Length + " skinned meshes with resolved bones; avatar present. Missing Tail_Rig_01 was removed by authorization, not restored.";
            System.IO.File.WriteAllText("Library/GargoyleImportVerification.txt", DateTime.UtcNow.ToString("O") + "\n" + result);
            Debug.Log(result);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
