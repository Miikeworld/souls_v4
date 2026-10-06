using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Lock-on setup: adds a LockOnController to the player and spawns capsule test
/// dummies carrying Targetable. Works on the currently open scene (expects
/// 00_TestBlockout). Safe to re-run — existing components/dummies are skipped.
/// </summary>
public static class ProjectRestartLockOn
{
    private const string DummyName = "Lock-On Test Dummy";
    private const string DummyMaterialPath = "Assets/_Project/Materials/TestEnemy.mat";

    [MenuItem("Tools/Project Restart/Setup Lock-On (Player + Test Dummies)")]
    public static void SetupLockOn()
    {
        var scene = SceneManager.GetActiveScene();
        var player = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<PlayerLocomotion>(true))
            .FirstOrDefault();
        if (player == null)
        {
            Debug.LogError("[ProjectRestart] Setup Lock-On: no PlayerLocomotion found in the open scene. Open 00_TestBlockout first.");
            return;
        }

        var lockOn = player.GetComponent<LockOnController>();
        if (lockOn == null)
        {
            lockOn = player.gameObject.AddComponent<LockOnController>();
            Debug.Log("[ProjectRestart] Added LockOnController to the player.");
        }

        var material = LoadOrCreateDummyMaterial();
        var spawned = 0;
        spawned += EnsureDummy(new Vector3(4f, 1f, 6f), material, 0) ? 1 : 0;
        spawned += EnsureDummy(new Vector3(-4f, 1f, 9f), material, 1) ? 1 : 0;
        // Eight more in a loose ring — crowd-combo practice needs bodies that
        // surround the player, not a pair standing in a queue.
        spawned += EnsureDummy(new Vector3(9f, 1f, 3f), material, 2) ? 1 : 0;
        spawned += EnsureDummy(new Vector3(-9f, 1f, 4f), material, 3) ? 1 : 0;
        spawned += EnsureDummy(new Vector3(10f, 1f, 12f), material, 4) ? 1 : 0;
        spawned += EnsureDummy(new Vector3(-10f, 1f, 13f), material, 5) ? 1 : 0;
        spawned += EnsureDummy(new Vector3(3f, 1f, 17f), material, 6) ? 1 : 0;
        spawned += EnsureDummy(new Vector3(-2f, 1f, 19f), material, 7) ? 1 : 0;
        spawned += EnsureDummy(new Vector3(14f, 1f, 7f), material, 8) ? 1 : 0;
        spawned += EnsureDummy(new Vector3(-14f, 1f, 8f), material, 9) ? 1 : 0;

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[ProjectRestart] Setup Lock-On done. Player '{player.name}' has LockOnController; spawned {spawned} new dumm{(spawned == 1 ? "y" : "ies")} (existing kept). Middle mouse / right-stick press toggles lock; flick look input sideways to switch targets.");
    }

    private static bool EnsureDummy(Vector3 position, Material material, int index)
    {
        var name = index == 0 ? DummyName : $"{DummyName} ({index})";
        var existing = GameObject.Find(name);
        if (existing != null)
        {
            if (existing.GetComponent<Targetable>() == null)
                existing.AddComponent<Targetable>();
            return false;
        }

        var dummy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        dummy.name = name;
        dummy.transform.position = position;
        if (material != null)
            dummy.GetComponent<MeshRenderer>().sharedMaterial = material;
        dummy.AddComponent<Targetable>();
        Undo.RegisterCreatedObjectUndo(dummy, "Spawn Lock-On Test Dummy");
        return true;
    }

    private static Material LoadOrCreateDummyMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(DummyMaterialPath);
        if (material != null) return material;

        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) return null;

        material = new Material(shader) { color = new Color(0.65f, 0.15f, 0.15f) };
        AssetDatabase.CreateAsset(material, DummyMaterialPath);
        return material;
    }
}
