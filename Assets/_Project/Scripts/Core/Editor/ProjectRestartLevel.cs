using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Build Ruin Level.
/// Generates the ruin gauntlet between the test blockout and the boss arena,
/// as one `RuinLevel` root (deleted + rebuilt in place — always idempotent):
///
///   S1 Entry hall  x36..54  — tutorial prompts, pillars, torchlight.
///   S2 Gap gauntlet x54..70 — floating rock platforms over a KillZone pit;
///                              one wide gap demands a sprint-jump.
///   S3 Lever hall  x70..86  — sealed portcullis; lever hidden in a south
///                              alcove opens it (puzzle: find the side room).
///   S4 Passage     x86..100 — quiet storeroom corridor (deco crates, bones).
///   S5 Approach    x96..106 z64..104 — colonnade ramp to the fog gate/boss.
///
/// Collision comes from the floor/wall slabs (TestGround-styled cubes) and
/// collider-guaranteed platforms — Synty deco prefabs dress over them.
/// </summary>
public static class ProjectRestartLevel
{
    private const string ScenePath = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const string RootName = "RuinLevel";
    private const string Env = "Assets/ThirdParty/Synty/PolygonDungeon/Prefabs/Environments/";
    private const string Prop = "Assets/ThirdParty/Synty/PolygonDungeon/Prefabs/Props/";
    private const string MatPath = "Assets/_Project/Materials/TestGround.mat";

    [MenuItem("Tools/Project Restart/Build Ruin Level")]
    public static void Setup()
    {
        ProjectRestartUrpFix.FixAll(); // Synty deco mats are legacy shaders — fix before placing
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var old = GameObject.Find(RootName);
        if (old != null) Object.DestroyImmediate(old);
        var root = new GameObject(RootName);

        var floorMat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        BuildEntryHall(root.transform, floorMat);
        BuildGapRun(root.transform, floorMat);
        BuildLeverHall(root.transform, floorMat);
        BuildPassage(root.transform, floorMat);
        BuildApproach(root.transform, floorMat);

        Require(EditorSceneManager.SaveScene(scene), "Could not save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectRestart] Ruin level built — entry hall → gap gauntlet → " +
                  "lever gate → storeroom → boss approach. Play from the blockout " +
                  "and head east along the torches.");
    }

    // ---------- S1: entry hall (x36..54, z56..64) ----------

    private static void BuildEntryHall(Transform root, Material mat)
    {
        // Approach apron from the blockout + hall floor.
        Slab(root, mat, "Floor_Approach", new Vector3(33f, -0.5f, 60f), new Vector3(6f, 1f, 8f));
        Slab(root, mat, "Floor_Hall", new Vector3(45f, -0.5f, 60f), new Vector3(18f, 1f, 9f));
        // North wall + south parapet — corridor read. The tall north wall is
        // an assigned wall-run surface; the low parapet is not.
        Wall(root, mat, new Vector3(45f, 2f, 64.4f), new Vector3(18f, 4f, 1f), wallRun: true);
        Wall(root, mat, new Vector3(45f, 1.2f, 55.6f), new Vector3(18f, 2.4f, 1f));
        Deco(root, Env + "Misc/SM_Env_Entrance_Large_01.prefab", new Vector3(36.2f, 0f, 60f),
             Quaternion.Euler(0f, 90f, 0f));
        for (var x = 40f; x <= 52f; x += 6f)
        {
            Deco(root, Env + "Pillars/SM_Env_Pillar_Square_01.prefab", new Vector3(x, 0f, 63.2f), Quaternion.identity);
            Deco(root, Env + "Pillars/SM_Env_Pillar_Square_01.prefab", new Vector3(x, 0f, 56.8f), Quaternion.identity);
        }
        Brazier(root, new Vector3(42f, 0f, 62.6f));
        Brazier(root, new Vector3(50f, 0f, 62.6f));
        Deco(root, Env + "Bones/SM_Env_BonePile_01.prefab", new Vector3(44f, 0f, 57.3f), Quaternion.Euler(0f, 40f, 0f));

        Trigger(root, "Tut_Move", new Vector3(39f, 1.5f, 60f), new Vector3(5f, 3f, 8f),
                "MOVE — WASD · SPRINT — HOLD CTRL", true);
        Trigger(root, "Tut_Actions", new Vector3(48f, 1.5f, 60f), new Vector3(5f, 3f, 8f),
                "SHIFT — ROLL · SPACE — JUMP", true);
    }

    // ---------- S2: gap gauntlet (x54..70) ----------

    private static void BuildGapRun(Transform root, Material mat)
    {
        // Floor slabs end at x=54 and resume x=70 — the pit is between.
        Slab(root, mat, "Floor_Landing", new Vector3(75f, -0.5f, 60f), new Vector3(10f, 1f, 9f));
        // Stepping platforms — rising line, last hop is the long one.
        var rocks = new[]
        {
            "Rocks/SM_Env_Rock_Flat_Platform_02.prefab",
            "Rocks/SM_Env_Rock_Flat_Platform_03.prefab",
            "Rocks/SM_Env_Rock_Flat_Platform_04.prefab",
            "Rocks/SM_Env_Rock_Flat_Platform_05.prefab",
        };
        var spots = new[]
        {
            new Vector3(57.0f, -0.1f, 60f),
            new Vector3(60.5f, 0.15f, 62f),
            new Vector3(64.0f, 0.40f, 58.5f),
            new Vector3(68.4f, 0.65f, 60f), // 4.4m hop — sprint + jump
        };
        for (var i = 0; i < spots.Length; i++)
        {
            var p = Deco(root, Env + rocks[i % rocks.Length], spots[i],
                         Quaternion.Euler(0f, i * 73f, 0f));
            // Platforms are footing — guarantee a collider whatever the prefab ships.
            if (p != null && p.GetComponentInChildren<Collider>() == null)
                p.AddComponent<BoxCollider>();
        }
        // Pit floor far below so the kill volume reads as a fall, not a wall.
        Slab(root, mat, "Pit_Bed", new Vector3(62f, -8.5f, 60f), new Vector3(18f, 1f, 16f));
        var kz = Trigger(root, "Pit_KillZone", new Vector3(62f, -4f, 60f), new Vector3(20f, 5f, 16f), null, false);
        kz.AddComponent<KillZone>();
        Trigger(root, "Tut_Gap", new Vector3(52.5f, 1.5f, 60f), new Vector3(4f, 3f, 8f),
                "THE FLOOR IS GONE — SPRINT TO JUMP FAR", true);
    }

    // ---------- S3: lever hall (x70..86 + south alcove z48..56) ----------

    private static void BuildLeverHall(Transform root, Material mat)
    {
        Slab(root, mat, "Floor_GateHall", new Vector3(80f, -0.5f, 60f), new Vector3(12f, 1f, 9f));
        Slab(root, mat, "Floor_Alcove", new Vector3(74f, -0.5f, 51f), new Vector3(8f, 1f, 9f));
        // North wall; south wall with an opening over the alcove (x72..76).
        Wall(root, mat, new Vector3(80f, 2f, 64.4f), new Vector3(12f, 4f, 1f), wallRun: true);
        Wall(root, mat, new Vector3(71f, 1.2f, 55.6f), new Vector3(4f, 2.4f, 1f));
        Wall(root, mat, new Vector3(80f, 1.2f, 55.6f), new Vector3(8f, 2.4f, 1f));
        // Alcove walls.
        Wall(root, mat, new Vector3(70f, 1.2f, 51f), new Vector3(1f, 2.4f, 9f));
        Wall(root, mat, new Vector3(78f, 1.2f, 51f), new Vector3(1f, 2.4f, 9f));
        Wall(root, mat, new Vector3(74f, 1.2f, 46.6f), new Vector3(9f, 2.4f, 1f));
        // Gate wall across the corridor at x=82 — side pillars + top lintel
        // leave a z57.5..62.5 doorway; the portcullis leaf is the blocker.
        Wall(root, mat, new Vector3(82f, 2f, 56.5f), new Vector3(1f, 4f, 2f));
        Wall(root, mat, new Vector3(82f, 2f, 63.5f), new Vector3(1f, 4f, 2f));
        Wall(root, mat, new Vector3(82f, 4f, 60f), new Vector3(1f, 2f, 9f));

        var gateGo = new GameObject("Gate_Lever");
        gateGo.transform.SetParent(root, false);
        gateGo.transform.position = new Vector3(82f, 0f, 60f);
        // Door leaf: the portcullis mesh + an invisible blocker slab lifting together.
        var leaf = new GameObject("Door");
        leaf.transform.SetParent(gateGo.transform, false);
        leaf.transform.localPosition = Vector3.zero;
        var mesh = Deco(leaf.transform, Env + "Misc/SM_Env_Portcullis_01.prefab",
                        Vector3.zero, Quaternion.Euler(0f, 90f, 0f));
        if (mesh == null) Slab(leaf.transform, mat, "DoorSlab", new Vector3(0f, 1.5f, 0f), new Vector3(0.5f, 3f, 4f));
        var blocker = leaf.AddComponent<BoxCollider>();
        blocker.center = new Vector3(0f, 1.5f, 0f);
        blocker.size = new Vector3(0.8f, 3f, 5.2f);
        var door = gateGo.AddComponent<GateDoor>();
        var dso = new SerializedObject(door);
        SetRef(dso, "door", leaf.transform);
        dso.ApplyModifiedPropertiesWithoutUndo();

        // Lever in the alcove — off the main sightline so the room must be found.
        var lever = Deco(root, Prop + "SM_Prop_Tech_Lever_01.prefab", new Vector3(74f, 0f, 49f),
                         Quaternion.Euler(0f, 180f, 0f));
        var leverGo = lever != null ? lever : new GameObject("Lever");
        leverGo.name = "Lever_AlCove";
        var lg = leverGo.AddComponent<LeverGate>();
        var lso = new SerializedObject(lg);
        SetRef(lso, "gate", door);
        var arm = leverGo.transform.Find("Arm");
        if (arm != null) SetRef(lso, "arm", arm);
        lso.ApplyModifiedPropertiesWithoutUndo();
        // Interact needs a collider for readability of the spot (distance is what gates it).
        var lc = leverGo.GetComponentInChildren<Collider>();
        if (lc == null) { var b = leverGo.AddComponent<BoxCollider>(); b.center = Vector3.up; }

        Brazier(root, new Vector3(73f, 0f, 50.5f));
        Brazier(root, new Vector3(80f, 0f, 62.6f));
        Deco(root, Env + "Bones/SM_Env_Bone_Skull_01.prefab", new Vector3(78f, 0f, 57.2f), Quaternion.identity);
        Trigger(root, "Tut_Gate", new Vector3(76f, 1.5f, 60f), new Vector3(6f, 3f, 8f),
                "SEALED — A MECHANISM MUST OPEN IT", true);
    }

    // ---------- S4: storeroom passage (x86..100) ----------

    private static void BuildPassage(Transform root, Material mat)
    {
        Slab(root, mat, "Floor_Passage", new Vector3(92f, -0.5f, 60f), new Vector3(12f, 1f, 9f));
        Wall(root, mat, new Vector3(92f, 2f, 64.4f), new Vector3(12f, 4f, 1f), wallRun: true);
        Wall(root, mat, new Vector3(92f, 1.2f, 55.6f), new Vector3(12f, 2.4f, 1f));

        // Storeroom dressing — crates and bones set the "old ruin" read without
        // gating the corridor (the push/plate puzzle was cut).
        Deco(root, Prop + "SM_Prop_Crate_Metal_02.prefab", new Vector3(88.5f, 0f, 58f), Quaternion.identity);
        Deco(root, Prop + "SM_Prop_Crate_Metal_01.prefab", new Vector3(88.9f, 0f, 57.2f), Quaternion.Euler(0f, 40f, 0f));
        Brazier(root, new Vector3(90f, 0f, 62.6f));
        Deco(root, Env + "Bones/SM_Env_BonePile_Small_01.prefab", new Vector3(95f, 0f, 57f), Quaternion.Euler(0f, 130f, 0f));
    }

    // ---------- S5: boss approach (x96..106, z64..104) ----------

    private static void BuildApproach(Transform root, Material mat)
    {
        // Connector from the push room + the north colonnade to the arena,
        // plus a pad under Checkpoint B at (96,96).
        Slab(root, mat, "Floor_Link", new Vector3(99f, -0.5f, 60f), new Vector3(6f, 1f, 9f));
        Slab(root, mat, "Floor_Approach", new Vector3(101f, -0.5f, 84f), new Vector3(10f, 1f, 44f));
        Slab(root, mat, "Floor_CheckpointPad", new Vector3(96f, -0.5f, 96f), new Vector3(9f, 1f, 9f));
        // Colonnade — pillars every 6m both sides, braziers between.
        for (var z = 70f; z <= 92f; z += 6f)
        {
            Deco(root, Env + "Pillars/SM_Env_Pillar_Square_02.prefab", new Vector3(97f, 0f, z), Quaternion.identity);
            Deco(root, Env + "Pillars/SM_Env_Pillar_Square_02.prefab", new Vector3(105f, 0f, z), Quaternion.identity);
        }
        Brazier(root, new Vector3(97.6f, 0f, 74f));
        Brazier(root, new Vector3(104.4f, 0f, 80f));
        Brazier(root, new Vector3(97.6f, 0f, 86f));
        Deco(root, Env + "Bones/SM_Env_BonePile_02.prefab", new Vector3(103f, 0f, 71f), Quaternion.Euler(0f, 200f, 0f));
        Deco(root, Env + "Bones/SM_Env_Bone_Ribs_01.prefab", new Vector3(98.5f, 0f, 90f), Quaternion.Euler(0f, 15f, 0f));
        // Broken stairs dressing at the approach mouth.
        Deco(root, Env + "Floors/SM_Env_Stairs_Broken_01.prefab", new Vector3(101f, 0f, 66f), Quaternion.identity);
        Trigger(root, "Tut_Approach", new Vector3(101f, 1.5f, 82f), new Vector3(8f, 3f, 8f),
                "THE DEEP RUIN — SOMETHING WAITS", true);
    }

    // ---------- builders ----------

    private static GameObject Slab(Transform root, Material mat, string name,
                                   Vector3 pos, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(root, false);
        go.transform.position = pos;
        go.transform.localScale = size;
        var r = go.GetComponent<MeshRenderer>();
        if (r != null && mat != null) r.sharedMaterial = mat;
        return go;
    }

    private static void Wall(Transform root, Material mat, Vector3 pos, Vector3 size, bool wallRun = false)
    {
        var go = Slab(root, mat, "Wall", pos, size);
        if (wallRun && go.GetComponent<WallRunSurface>() == null) go.AddComponent<WallRunSurface>();
    }

    /// <summary>Tutorial trigger — `text != null` adds a TutorialTrigger; returns the GO.</summary>
    private static GameObject Trigger(Transform root, string name, Vector3 pos, Vector3 size,
                                      string text, bool banner)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        go.transform.position = pos;
        var b = go.AddComponent<BoxCollider>();
        b.isTrigger = true;
        b.size = size;
        if (text != null)
        {
            var tt = go.AddComponent<TutorialTrigger>();
            var so = new SerializedObject(tt);
            var m = so.FindProperty("message");
            if (m != null) m.stringValue = text;
            var bn = so.FindProperty("banner");
            if (bn != null) bn.boolValue = banner;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        return go;
    }

    private static void Brazier(Transform root, Vector3 pos)
    {
        Deco(root, Prop + "SM_Prop_Brazier_01.prefab", pos, Quaternion.identity);
        var light = new GameObject("TorchLight");
        light.transform.SetParent(root, false);
        light.transform.position = pos + Vector3.up * 1.6f;
        var l = light.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = new Color(1f, 0.62f, 0.3f);
        l.intensity = 1.6f;
        l.range = 9f;
    }

    private static GameObject Deco(Transform root, string path, Vector3 pos, Quaternion rot)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {
            Debug.LogWarning("[ProjectRestart] Deco prefab missing: " + path);
            return null;
        }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.transform.SetParent(root, false);
        go.transform.SetPositionAndRotation(pos, rot);
        return go;
    }

    private static void SetRef(SerializedObject so, string name, Object value)
    {
        var p = so.FindProperty(name);
        if (p != null) p.objectReferenceValue = value;
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new System.Exception("[ProjectRestart] " + message);
    }
}
