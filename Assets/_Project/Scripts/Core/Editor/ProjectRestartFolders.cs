using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ProjectRestartFolders
{
    private static readonly string[] ProjectFolders =
    {
        "Animators", "Audio", "Materials",
        "Prefabs/Characters", "Prefabs/Enemies", "Prefabs/Props",
        "Scenes/00_TestBlockout", "Scenes/01_Dungeon_Boss",
        "Scripts/Combat", "Scripts/Traversal", "Scripts/UI", "Scripts/Enemies", "Scripts/Core",
        "ScriptableObjects/Weapons", "ScriptableObjects/Abilities", "ScriptableObjects/Enemies",
        "Shaders"
    };

    private static readonly (string Guid, string Source, string Destination)[] Packages =
    {
        ("5cf54006bb14e1b499495a99812c9ebd", "CLazyRunnerActionAnimPack", "CLazyRunner"),
        ("6988616bd6db15241965bfc030a0d029", "Grruzam Powerful Sword Animation(Great Sword, Katana)", "GrruzamPowerfulSword"),
        ("f91e037fb7c72e0418ef96dd1c02d175", "Magical-Knight_Set", "MagicalKnightSet"),
        ("f505599066f1bd6479dacdf081c92e6b", "Souls-like Essentials", "SoulslikeEssential"),
        ("84d203015959d2946a742c1abbb8d5f9", "Ninja_AnimSet", "NinjaAnimset"),
        ("2d83f4d1637062b4aa065f837ae56efe", "PolygonPackage/PolygonDarkFantasy", "Synty/PolygonDarkFantasy"),
        ("1f23ffa6e9c645948965941c7d0d00a3", "PolygonPackage/PolygonDungeon", "Synty/PolygonDungeon"),
        ("cedeafafdc6c55349b975086e48f21e4", "PolygonPackage/PolygonFantasyRivals", "Synty/PolygonFantasyRivals"),
        ("7d346e97aed69db40b97fafa64d43b9e", "PolygonPackage/PolygonFantasyHeroCharacters", "Synty/PolygonFantasyHeroCharacters"),
        ("23f8a37460aab604bb03d8cb357a2efb", "PolygonPackage/PolygonParticles", "Synty/PolygonParticles")
    };

    [MenuItem("Tools/Project Restart/Step 1 - Create Folders and Organize Assets")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            Debug.LogWarning("[ProjectRestart] Step 1 deferred: exit Play Mode and wait for compilation/import to finish, then run again.");
            return;
        }

        try
        {
            Organize();
        }
        catch (Exception exception)
        {
            Debug.LogError("[ProjectRestart] Step 1 FAIL: " + exception.Message + " No assets are deleted or overwritten by this tool. If a move failed midway, successful moves remain in place; resolve the failure before rerunning.");
            Debug.LogException(exception);
        }
    }

    private static void Organize()
    {
        var expectedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in Packages)
        {
            var source = "Assets/online assets/" + package.Source;
            var destination = "Assets/ThirdParty/" + package.Destination;
            var current = AssetDatabase.GUIDToAssetPath(package.Guid);
            if (!AssetDatabase.IsValidFolder(current) || (current != source && current != destination))
                throw new InvalidOperationException("Missing or unexpectedly relocated package: " + source);
            if (current != destination && (Directory.Exists(destination) || File.Exists(destination) || File.Exists(destination + ".meta")))
                throw new InvalidOperationException("Destination already exists; refusing to merge or overwrite: " + destination);

            expectedPaths.Add(package.Guid, destination);
            var assetCount = 0;
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { current }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == current)
                    continue;
                if (!path.StartsWith(current + "/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unexpected asset path: " + path);
                expectedPaths.Add(guid, destination + path.Substring(current.Length));
                if (!AssetDatabase.IsValidFolder(path))
                    assetCount++;
            }
            if (assetCount == 0)
                throw new InvalidOperationException("Package has no imported assets: " + current);
        }

        var moved = 0;
        EditorApplication.LockReloadAssemblies();
        try
        {
            foreach (var folder in ProjectFolders)
                EnsureFolder("Assets/_Project/" + folder);
            EnsureFolder("Assets/ThirdParty/Synty");

            foreach (var package in Packages)
            {
                var source = AssetDatabase.GUIDToAssetPath(package.Guid);
                var destination = "Assets/ThirdParty/" + package.Destination;
                if (source == destination)
                    continue;
                var error = AssetDatabase.ValidateMoveAsset(source, destination);
                if (!string.IsNullOrEmpty(error))
                    throw new InvalidOperationException(source + ": " + error);
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var package in Packages)
                {
                    var source = AssetDatabase.GUIDToAssetPath(package.Guid);
                    var destination = "Assets/ThirdParty/" + package.Destination;
                    if (source == destination)
                        continue;
                    var error = AssetDatabase.MoveAsset(source, destination);
                    if (!string.IsNullOrEmpty(error))
                        throw new InvalidOperationException(source + ": " + error);
                    moved++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var entry in expectedPaths)
            {
                if (AssetDatabase.GUIDToAssetPath(entry.Key) != entry.Value || AssetDatabase.AssetPathToGUID(entry.Value) != entry.Key)
                    throw new InvalidOperationException("GUID/path verification failed for " + entry.Value);
            }
            foreach (var folder in ProjectFolders)
            {
                if (!AssetDatabase.IsValidFolder("Assets/_Project/" + folder))
                    throw new InvalidOperationException("Missing project folder: " + folder);
            }

            GargoyleImportVerification.Verify();
            Debug.Log("[ProjectRestart] Step 1 PASS: all requested _Project folders exist; all 10 owned packages are under Assets/ThirdParty; " + moved + " packages moved this run; " + expectedPaths.Count + " asset/folder GUIDs preserved. Existing imported packages were relocated, not downloaded or reimported from source archives. No rig, material, animation controller, scene, or gameplay changes were made. Stopped before step 2.");
        }
        finally
        {
            EditorApplication.UnlockReloadAssemblies();
        }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;
        if (Directory.Exists(path) || File.Exists(path) || File.Exists(path + ".meta"))
            throw new InvalidOperationException("Unimported or conflicting path; refresh Unity before retrying: " + path);
        var separator = path.LastIndexOf('/');
        var parent = path.Substring(0, separator);
        EnsureFolder(parent);
        if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, path.Substring(separator + 1))))
            throw new InvalidOperationException("Could not create folder: " + path);
    }
}
