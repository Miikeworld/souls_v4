using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Tools > Project Restart > Level > Build Sunken Vault.
/// Generates 03_SunkenVault.unity — the story level that replaces Foundry Depths
/// (design: LEVEL_SUNKEN_VAULT.md). Titanfall-scale halls built from the
/// PolygonDungeonRealms Dungeon_01 dwarf kit (25–30 m stacked walls, galleries,
/// a chasm crossed by a chained wall-run course or a guarded bridge), three
/// sealed crowd arenas fought in waves, and the throne hall of the giant
/// Swollen King (BossColossus + BossCinematics camera work).
///
/// Clones the player / camera / HUD wiring and one dressed skeleton from
/// 00_TestBlockout. Collision is invisible boxes on the design grid; vendor art
/// never collides and goes through the Mechanical Environment pass; glows use
/// project-owned Unlit materials; vendor prefabs/materials/GUIDs are never
/// modified. Also extends EnemyBase.controller with the crowd states
/// (Attack2/Heavy/Lunge/Knockdown/GetUp) and builds ColossusBase.controller.
/// Manual, outside Play Mode. A rebuild replaces the scene (timestamped backup).
/// </summary>
public static class ProjectRestartSunkenVault
{
    public const string ScenePath = "Assets/_Project/Scenes/03_SunkenVault.unity";
    private const string OldScenePath = "Assets/_Project/Scenes/03_FoundryDepths.unity";
    private const string SourcePath = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const string KitRoot = "Assets/PolygonDungeonRealms/Prefabs";
    private const string DarkRoot = "Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs";
    private const string FxRoot = "Assets/ThirdParty/Synty/PolygonParticles";
    private const string MatFolder = "Assets/_Project/ArtDirection/Materials/SunkenVault";
    private const string AnimFolder = "Assets/_Project/Animations";
    private const string EnemyControllerPath = AnimFolder + "/EnemyBase.controller";
    private const string BossControllerPath = AnimFolder + "/ColossusBase.controller";
    private const string Grz = "Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_Big_Sword/";
    private const string Mk = "Assets/ThirdParty/MagicalKnightSet/Animation/Humanoid/";

    private static readonly Dictionary<string, string> prefabPaths = new();
    private static Transform root, art, col, lights, play, story;
    private static Material lava, crimson, violet, rune, runMark, fogWall;
    private static GameObject enemyTemplate;
    private static AnimatorController sitCtrl, standCtrl;
    private static int seed;
    private static readonly List<string> report = new();

    // =====================================================================
    [MenuItem("Tools/Project Restart/Level/Build Sunken Vault")]
    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("[SunkenVault] Exit Play Mode first."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Selection.objects = System.Array.Empty<Object>();
        report.Clear();
        ProjectRestartUrpFix.FixAll();
        EnsureFolder(MatFolder);
        ExtendEnemyController();
        var bossCtrl = BuildBossController();
        RetireFoundryDepths();

        var source = EditorSceneManager.OpenScene(SourcePath, OpenSceneMode.Single);
        if (System.IO.File.Exists(ScenePath))
            AssetDatabase.CopyAsset(ScenePath, "Assets/_Project/ArtDirection/SunkenVault_BeforeRebuild_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".unity");
        if (!EditorSceneManager.SaveScene(source, ScenePath, true)) throw new System.InvalidOperationException("Could not clone " + SourcePath);
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var player = Object.FindFirstObjectByType<PlayerLocomotion>();
        var enemy = Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None).FirstOrDefault(e => e.GetComponent<IBossEngage>() == null);
        Require(player != null, "Player (PlayerLocomotion) missing in " + SourcePath);
        Require(enemy != null, "No dressed skeleton enemy in " + SourcePath + " — run Setup Skeleton Enemies first.");
        enemyTemplate = Object.Instantiate(enemy.gameObject); enemyTemplate.name = "VaultEnemyTemplate"; enemyTemplate.SetActive(false);
        foreach (var go in scene.GetRootGameObjects())
        {
            var keep = go == enemyTemplate ||
                       go.GetComponentInChildren<PlayerLocomotion>(true) || go.GetComponentInChildren<Camera>(true) ||
                       go.GetComponentInChildren<Unity.Cinemachine.CinemachineCamera>(true) ||
                       go.GetComponentInChildren<Canvas>(true) || go.GetComponentInChildren<UnityEngine.EventSystems.EventSystem>(true) ||
                       (go.GetComponent<Light>() && go.GetComponent<Light>().type == LightType.Directional);
            if (!keep) Object.DestroyImmediate(go);
        }

        Prepare();
        seed = 7;
        root = new GameObject("SunkenVault").transform;
        art = Group(root, "Art"); col = Group(root, "Collision"); lights = Group(root, "Lights");
        play = Group(root, "Gameplay"); story = Group(root, "Story");

        var cc = player.GetComponent<CharacterController>();
        if (cc) cc.enabled = false;
        player.transform.SetPositionAndRotation(new Vector3(0f, Ledge + 0.08f, 4f), Quaternion.identity);
        if (cc) cc.enabled = true;

        BuildArrival();
        BuildPlaza();
        BuildChasm();
        BuildCathedral();
        BuildTreasury();
        var boss = BuildThroneHall(bossCtrl);
        BuildAtmosphere(scene);

        Object.DestroyImmediate(enemyTemplate);
        ProjectRestartMechanicalEnvironment.Apply();
        Validate(boss);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        var build = EditorBuildSettings.scenes.Where(s => s.path != OldScenePath).ToList();
        if (!build.Any(s => s.path == ScenePath)) build.Add(new EditorBuildSettingsScene(ScenePath, true));
        else foreach (var s in build.Where(s => s.path == ScenePath)) s.enabled = true;
        EditorBuildSettings.scenes = build.ToArray();
        SceneView.lastActiveSceneView?.LookAt(new Vector3(0f, 6f, 40f), Quaternion.Euler(30f, 0f, 0f), 60f);
        Debug.Log("[SunkenVault] 03_SunkenVault saved — 6 zones, 2 sealed crowd arenas + bridge guard post, chasm wall-run course, " +
                  (boss ? "Swollen King boss" : "NO BOSS") + ". Main menu NEW GAME/CONTINUE now load it. " +
                  "Static build only: walk the route in Play Mode, then Level > Capture Sunken Vault Views.\n" + string.Join("\n", report));
    }

    // Heights (floors): the arrival ledge overlooks the plaza; the throne sits above the treasury stair.
    private const float Ledge = 12.5f, Landing = -4f, Throne = 12.5f;

    // =====================================================================
    // ARRIVAL LEDGE (z 0..20, y 12.5) — overlook + checkpoint + grand stair down.
    private static void BuildArrival()
    {
        var z = Group(art, "0_ArrivalLedge");
        Floor(z, -8f, 8f, 0f, 20f, Ledge);
        Solid("Ledge", new Vector3(0f, Ledge * 0.5f, 10f), new Vector3(16f, Ledge, 20f));
        TallWall(z, new Vector3(-8.5f, Ledge, 0f), new Vector3(-8.5f, Ledge, 20f), 10f);
        TallWall(z, new Vector3(8.5f, Ledge, 0f), new Vector3(8.5f, Ledge, 20f), 10f);
        TallWall(z, new Vector3(-9f, Ledge, -0.5f), new Vector3(9f, Ledge, -0.5f), 10f);
        // The ledge face toward the plaza, below the balustrade.
        Facade(z, new Vector3(-8f, 0f, 20f), new Vector3(-5f, 0f, 20f), Ledge);
        Facade(z, new Vector3(5f, 0f, 20f), new Vector3(8f, 0f, 20f), Ledge);
        Rail(z, new Vector3(-8f, Ledge, 19.7f), new Vector3(-5f, Ledge, 19.7f));
        Rail(z, new Vector3(5f, Ledge, 19.7f), new Vector3(8f, Ledge, 19.7f));
        PlaceCheckpoint(new Vector3(-4.5f, Ledge, 8f), 90f, "Vault Gate", true);
        Torch(z, new Vector3(-7.2f, Ledge, 14f)); Torch(z, new Vector3(7.2f, Ledge, 14f)); Torch(z, new Vector3(7.2f, Ledge, 3f));
        Banner(z, new Vector3(0f, Ledge + 4f, -0.1f), 0f);
        Inscription(new Vector3(4.6f, Ledge, 9f), -90f, "SM_Env_Dwarf_Obelisk_01", new[]
        {
            "THE VAULT OF THE HOLD — Cut Corestone enters here. Crimson stock goes below the king's hall.",
            "By the king's word: the vault does not close.",
        });
        // Grand stair: 12.5 m down over 25 m (five native 2.5 m kit runs).
        StairRun(z, new Vector3(0f, Ledge, 20f), new Vector3(0f, 0f, 45f), 10f);
    }

    // =====================================================================
    // GATE PLAZA (z 20..96, x -30..30): 25 m walls, side galleries, wall-run fins.
    // Crowd arena #1 (2 waves) — seals at the stair foot and the north exit.
    private static void BuildPlaza()
    {
        var z = Group(art, "1_GatePlaza");
        Floor(z, -30f, 30f, 20f, 96f);
        Solid("Floor_Plaza", new Vector3(0f, -0.5f, 58f), new Vector3(60f, 1f, 76f));
        TallWall(z, new Vector3(-30.5f, 0f, 20f), new Vector3(-30.5f, 0f, 96f), 25f);
        TallWall(z, new Vector3(30.5f, 0f, 20f), new Vector3(30.5f, 0f, 96f), 25f);
        TallWall(z, new Vector3(-31f, 0f, 19.5f), new Vector3(-8.5f, 0f, 19.5f), 25f);
        TallWall(z, new Vector3(8.5f, 0f, 19.5f), new Vector3(31f, 0f, 19.5f), 25f);
        // North edge = the chasm lip: low wall either side of the takeoff ledge and bridge.
        foreach (var (a, b) in new[] { (-30f, -8f), (14f, 30f) })
        {
            Solid("PlazaLip", new Vector3((a + b) * 0.5f, 0.6f, 96.2f), new Vector3(b - a, 1.2f, 0.4f));
            Rail(z, new Vector3(a, 0f, 96.2f), new Vector3(b, 0f, 96.2f));
        }
        // Takeoff ledge and bridgehead pierce the lip.
        Floor(z, -8f, 8f, 96f, 98f);
        Solid("Takeoff", new Vector3(0f, -0.5f, 97f), new Vector3(16f, 1f, 2f));

        // Pillars + statues — cover for crowds, plunge perches.
        foreach (var x in new[] { -18f, 18f })
        foreach (var pz in new[] { 36f, 58f, 80f })
        {
            Fit(z, "SM_Env_Dwarf_Pillar_06", new Vector3(x, 0f, pz), 0f, new Vector3(2.2f, 18f, 2.2f));
            Solid("Pillar", new Vector3(x, 9f, pz), new Vector3(2f, 18f, 2f));
        }
        // Vendor statues are authored huge — fit to a plaza-sized 5 m.
        Fit(z, "SM_Env_Statue_01", new Vector3(0f, 0f, 58f), 180f, new Vector3(float.NaN, 5f, float.NaN));
        Solid("Statue", new Vector3(0f, 2f, 58f), new Vector3(3f, 4f, 3f));
        Lamp(new Vector3(0f, 5f, 58f), Warm(), 14f, 2.2f);

        // Side galleries (y 4) with ramps at their south ends — plunge onto the crowd.
        foreach (var side in new[] { -1f, 1f })
        {
            var x0 = side < 0 ? -30f : 24f; var x1 = side < 0 ? -24f : 30f; var cx = (x0 + x1) * 0.5f;
            Solid("Gallery", new Vector3(cx, 2f, 64f), new Vector3(6f, 4f, 52f));
            Floor(z, x0, x1, 38f, 90f, 4f);
            Facade(z, new Vector3(side < 0 ? x1 : x0, 0f, 38f), new Vector3(side < 0 ? x1 : x0, 0f, 90f), 4f);
            Ramp("GalleryRamp", new Vector3(cx, 0f, 28f), new Vector3(cx, 4f, 38f), 6f);
            Rail(z, new Vector3(side < 0 ? x1 : x0, 4f, 44f), new Vector3(side < 0 ? x1 : x0, 4f, 56f));
            Rail(z, new Vector3(side < 0 ? x1 : x0, 4f, 70f), new Vector3(side < 0 ? x1 : x0, 4f, 90f));
            Solid("GalleryRail", new Vector3(side < 0 ? x1 : x0, 4.6f, 50f), new Vector3(0.3f, 1.2f, 12f));
            Solid("GalleryRail", new Vector3(side < 0 ? x1 : x0, 4.6f, 80f), new Vector3(0.3f, 1.2f, 20f));
            for (var gz = 42f; gz <= 88f; gz += 15f) Torch(z, new Vector3(cx + side * 2f, 4f, gz));
        }

        // Wall-run fins: two pairs 5.4 m apart (wall-dash corridors) to break the crowd's ring.
        Fin(z, new Vector3(-10.2f, 0f, 44f), new Vector3(-10.2f, 0f, 58f), 10f);
        Fin(z, new Vector3(-4.8f, 0f, 47f), new Vector3(-4.8f, 0f, 61f), 10f);
        Fin(z, new Vector3(4.8f, 0f, 63f), new Vector3(4.8f, 0f, 77f), 10f);
        Fin(z, new Vector3(10.2f, 0f, 66f), new Vector3(10.2f, 0f, 80f), 10f);

        Runes(z, new Vector3(-30f, 6f, 30f), new Vector3(-30f, 6f, 90f), violet, true);
        Runes(z, new Vector3(30f, 6f, 30f), new Vector3(30f, 6f, 90f), violet, false);
        foreach (var cz in new[] { 40f, 76f }) Chandelier(z, new Vector3(0f, 16f, cz));

        // Crowd arena #1 — sealed at the stair foot and the takeoff/bridge mouths.
        var seals = new[]
        {
            Seal(new Vector3(0f, 0f, 45.5f), 10.5f, 0f),
            Seal(new Vector3(0f, 0f, 96.6f), 16f, 0f),
            Seal(new Vector3(11.5f, 0f, 96.6f), 5f, 0f),
        };
        var enc = Encounter("Arena_GatePlaza", new Vector3(0f, 3f, 70f), new Vector3(58f, 6f, 46f), seals, "THE HOLD STIRS");
        var w1 = Wave(enc, "Wave1");
        // Open floor between pillars, fins and statue (explicit — rings clipped the fins).
        foreach (var p in new[] { new Vector3(-22f, 0f, 50f), new Vector3(-24f, 0f, 64f), new Vector3(-21f, 0f, 74f), new Vector3(-14f, 0f, 87f),
                                  new Vector3(22f, 0f, 50f), new Vector3(24f, 0f, 64f), new Vector3(21f, 0f, 74f), new Vector3(14f, 0f, 87f) })
            Enemy(w1, p, 180f);
        var w2 = Wave(enc, "Wave2");
        foreach (var p in new[] { new Vector3(-27f, 4f, 70f), new Vector3(27f, 4f, 72f), new Vector3(-12f, 0f, 92f), new Vector3(12f, 0f, 92f), new Vector3(0f, 0f, 92f) })
            Enemy(w2, p, 180f);
        Enemy(w2, new Vector3(0f, 0f, 86f), 180f, elite: "Vault Captain");
        SetWaves(enc, w1, w2);
    }

    // =====================================================================
    // THE CHASM (z 96..164): the wall-run course (four alternating marked walls,
    // 5.4 m apart — wall-dash width) or the guarded bridge on the east (x 9..14).
    private static void BuildChasm()
    {
        var z = Group(art, "2_Chasm");
        // Depth: lava far below, cliff faces, a safety net that returns you to the takeoff.
        Glow("LavaFloor", new Vector3(-3f, -30f, 130f), new Vector3(28f, 0.2f, 72f), lava);
        for (var cz = 100f; cz < 164f; cz += 12f) Lamp(new Vector3(-3f, -26f, cz), Lava(), 22f, 3f);
        foreach (var x in new[] { -15.5f, 15.5f })
            for (var cz = 96f; cz < 164f; cz += 10f)
                Fit(z, "SM_Env_Rock_Cliff_01", new Vector3(x, -30f, cz + 5f), x < 0 ? 90f : -90f, new Vector3(10f, 30f, 4f));
        TallWall(z, new Vector3(-15.5f, 0f, 96f), new Vector3(-15.5f, 0f, 164f), 25f);
        TallWall(z, new Vector3(15.5f, 0f, 96f), new Vector3(15.5f, 0f, 164f), 25f);
        var back = new GameObject("TakeoffReturn").transform; back.SetParent(play);
        back.SetPositionAndRotation(new Vector3(0f, 0.1f, 93f), Quaternion.identity);
        var net = new GameObject("ChasmSafetyNet"); net.transform.SetParent(play); net.transform.position = new Vector3(-3f, -14f, 130f);
        net.AddComponent<BoxCollider>().size = new Vector3(26f, 4f, 70f);
        Set(net.AddComponent<FallReturn>(), "returnPoint", back);

        // Wall-run course: faces at x = ±2.7 (5.4 m apart), each 18 m, descending ~1.3 m per wall.
        var walls = new[]
        {
            (x: -3.05f, z0: 98f, z1: 116f, top: 9f),
            (x: 3.05f, z0: 113f, z1: 131f, top: 8f),
            (x: -3.05f, z0: 128f, z1: 146f, top: 7f),
            (x: 3.05f, z0: 143f, z1: 161f, top: 6f),
        };
        foreach (var w in walls)
        {
            var c = Solid("ChasmRunWall", new Vector3(w.x, (w.top - 12f) * 0.5f, (w.z0 + w.z1) * 0.5f), new Vector3(0.7f, w.top + 12f, w.z1 - w.z0));
            c.AddComponent<WallRunSurface>();
            for (var y = -12f; y < w.top; y += 5f)
                Facade(z, new Vector3(w.x, y, w.z0), new Vector3(w.x, y, w.z1), Mathf.Min(5f, w.top - y));
            var face = w.x < 0 ? w.x + 0.37f : w.x - 0.37f;
            Glow("WallRunMark", new Vector3(face, 1.4f, (w.z0 + w.z1) * 0.5f), new Vector3(0.05f, 0.25f, w.z1 - w.z0 - 1f), runMark);
            Lamp(new Vector3(face + (w.x < 0 ? 1.2f : -1.2f), 3f, (w.z0 + w.z1) * 0.5f), Violet(0.9f), 9f, 1.4f);
        }
        Inscription(new Vector3(-6.5f, 0f, 94f), 0f, null, new[]
        {
            "The haulers ran the cut-stone walls when the bridge was jammed. Keep moving and the wall carries you.",
        }, "READ MARKINGS");

        // Landing platform (y -4) and the climb into the cathedral.
        Floor(z, -9f, 9f, 162f, 174f, Landing);
        Solid("Landing", new Vector3(0f, Landing - 0.5f, 168f), new Vector3(18f, 1f, 12f));
        Ramp("LandingClimb", new Vector3(0f, Landing, 174f), new Vector3(0f, 0f, 182f), 10f);
        Facade(z, new Vector3(-9f, Landing - 6f, 162f), new Vector3(9f, Landing - 6f, 162f), 6f);
        // Landing walls and the strip either side of the climb (no open drop beside it).
        TallWall(z, new Vector3(-9.5f, Landing, 162f), new Vector3(-9.5f, Landing, 182f), 29f);
        TallWall(z, new Vector3(9.5f, Landing, 166f), new Vector3(9.5f, Landing, 182f), 29f);
        Floor(z, -9f, -5f, 174f, 182f, Landing); Floor(z, 5f, 9f, 174f, 182f, Landing);
        Solid("LandingStrip", new Vector3(0f, Landing - 0.5f, 178f), new Vector3(19f, 1f, 8f));
        Solid("ClimbFace", new Vector3(0f, -2f, 182.3f), new Vector3(70f, 4f, 0.6f));
        PlaceCheckpoint(new Vector3(-6f, Landing, 170f), 90f, "Chasm Landing", false);
        Torch(z, new Vector3(7.5f, Landing, 165f)); Torch(z, new Vector3(-7.5f, Landing, 165f));

        // The bridge (x 9..14): floor tiles over pillars, balustrades, a guard post mid-span.
        Floor(z, 9f, 14f, 96f, 118f);
        Floor(z, 9f, 14f, 138f, 150f);
        Solid("Bridge", new Vector3(11.5f, -0.5f, 123f), new Vector3(5f, 1f, 54f));
        Floor(z, 7f, 17f, 118f, 138f);
        Solid("BridgePost", new Vector3(12f, -0.5f, 128f), new Vector3(10f, 1f, 20f));
        StairRun(z, new Vector3(11.5f, 0f, 150f), new Vector3(6.5f, Landing, 166f), 5f);
        foreach (var x in new[] { 9.1f, 13.9f })
        {
            Rail(z, new Vector3(x, 0f, 98f), new Vector3(x, 0f, 118f));
            Rail(z, new Vector3(x, 0f, 138f), new Vector3(x, 0f, 150f));
            Solid("BridgeRail", new Vector3(x, 0.6f, 108f), new Vector3(0.3f, 1.2f, 20f));
            Solid("BridgeRail", new Vector3(x, 0.6f, 144f), new Vector3(0.3f, 1.2f, 12f));
        }
        for (var bz = 100f; bz <= 150f; bz += 10f)
            Fit(z, "SM_Env_Dwarf_Stairs_Pillar_04", new Vector3(11.5f, -24f, bz), 0f, new Vector3(3f, 24f, 3f));
        Torch(z, new Vector3(16f, 0f, 121f)); Torch(z, new Vector3(16f, 0f, 135f));
        var guard = Group(play, "BridgeGuard");
        foreach (var p in new[] { new Vector3(10f, 0f, 124f), new Vector3(14f, 0f, 126f), new Vector3(9.5f, 0f, 131f), new Vector3(14.5f, 0f, 133f), new Vector3(12f, 0f, 120f), new Vector3(12f, 0f, 136f) })
            Enemy(guard, p, 180f);
        Enemy(guard, new Vector3(12f, 0f, 129f), 180f, elite: "Bridge Warden");
    }

    // =====================================================================
    // FORGE CATHEDRAL (z 182..262, x -35..35, walls 30 m): central dais, crimson
    // core, wall-run fins, side galleries. Crowd arena #2 — three waves.
    private static void BuildCathedral()
    {
        var z = Group(art, "3_Cathedral");
        Floor(z, -35f, 35f, 182f, 262f);
        Solid("Floor_Cathedral", new Vector3(0f, -0.5f, 222f), new Vector3(70f, 1f, 80f));
        TallWall(z, new Vector3(-35.5f, 0f, 182f), new Vector3(-35.5f, 0f, 262f), 30f);
        TallWall(z, new Vector3(35.5f, 0f, 182f), new Vector3(35.5f, 0f, 262f), 30f);
        CrossWall(z, 182f, -35f, 35f, 5f, 30f);
        CrossWall(z, 262f, -35f, 35f, 5f, 30f);

        // Central dais with a Crimson Corestone growth — the hold's poisoned heart.
        Floor(z, -9f, 9f, 213f, 231f, 1.5f);
        Solid("Dais", new Vector3(0f, 0.75f, 222f), new Vector3(18f, 1.5f, 18f));
        foreach (var (a, b) in new[] { (new Vector3(0f, 0f, 211f), new Vector3(0f, 1.5f, 213f)), (new Vector3(0f, 0f, 233f), new Vector3(0f, 1.5f, 231f)) })
            Ramp("DaisStep", a, b, 6f);
        Crystal(z, new Vector3(0f, 1.5f, 222f), crimson, 2.6f);
        Lamp(new Vector3(0f, 6f, 222f), new Color(1f, 0.12f, 0.1f), 24f, 4f);
        Fx("FX_Electricity_01", new Vector3(0f, 5f, 222f), new Color(1f, 0.2f, 0.2f));
        Fx("FX_Embers_01", new Vector3(0f, 2f, 222f));

        // Fins around the dais — wall-dash pairs on each flank.
        Fin(z, new Vector3(-20.7f, 0f, 205f), new Vector3(-20.7f, 0f, 221f), 12f);
        Fin(z, new Vector3(-15.3f, 0f, 223f), new Vector3(-15.3f, 0f, 239f), 12f);
        Fin(z, new Vector3(15.3f, 0f, 205f), new Vector3(15.3f, 0f, 221f), 12f);
        Fin(z, new Vector3(20.7f, 0f, 223f), new Vector3(20.7f, 0f, 239f), 12f);

        // Side galleries (y 6) with ramps.
        foreach (var side in new[] { -1f, 1f })
        {
            var x0 = side < 0 ? -35f : 28f; var x1 = side < 0 ? -28f : 35f; var cx = (x0 + x1) * 0.5f;
            Solid("Gallery", new Vector3(cx, 3f, 230f), new Vector3(7f, 6f, 52f));
            Floor(z, x0, x1, 204f, 256f, 6f);
            Facade(z, new Vector3(side < 0 ? x1 : x0, 0f, 204f), new Vector3(side < 0 ? x1 : x0, 0f, 256f), 6f);
            Ramp("GalleryRamp", new Vector3(cx, 0f, 190f), new Vector3(cx, 6f, 204f), 7f);
            Rail(z, new Vector3(side < 0 ? x1 : x0, 6f, 204f), new Vector3(side < 0 ? x1 : x0, 6f, 220f));
            Rail(z, new Vector3(side < 0 ? x1 : x0, 6f, 236f), new Vector3(side < 0 ? x1 : x0, 6f, 256f));
            Solid("GalleryRail", new Vector3(side < 0 ? x1 : x0, 6.6f, 212f), new Vector3(0.3f, 1.2f, 16f));
            Solid("GalleryRail", new Vector3(side < 0 ? x1 : x0, 6.6f, 246f), new Vector3(0.3f, 1.2f, 20f));
            for (var gz = 210f; gz <= 254f; gz += 14f) Torch(z, new Vector3(cx + side * 2.4f, 6f, gz));
            Runes(z, new Vector3(side * 35f, 10f, 190f), new Vector3(side * 35f, 10f, 258f), crimson, side < 0);
        }
        foreach (var x in new[] { -24f, 24f })
        foreach (var pz in new[] { 192f, 252f })
        {
            Fit(z, "SM_Env_Dwarf_Pillar_06", new Vector3(x, 0f, pz), 0f, new Vector3(2.6f, 24f, 2.6f));
            Solid("Pillar", new Vector3(x, 12f, pz), new Vector3(2.4f, 24f, 2.4f));
        }
        foreach (var cz in new[] { 196f, 248f }) Chandelier(z, new Vector3(0f, 20f, cz));
        Place(z, "SM_Prop_Dwarf_Forge_Golem_Chest_01", new Vector3(-30f, 0f, 186f), 40f);
        Place(z, "SM_Prop_Tech_Container_Pile_01", new Vector3(30f, 0f, 187f), -30f);
        Place(z, "SM_Env_Ore_Pile_01", new Vector3(-31f, 0f, 258f), 0f);

        var seals = new[] { Seal(new Vector3(0f, 0f, 182f), 10f, 0f), Seal(new Vector3(0f, 0f, 262f), 10f, 0f) };
        var enc = Encounter("Arena_Cathedral", new Vector3(0f, 3f, 226f), new Vector3(66f, 6f, 70f), seals, "THE FORGE HOLDS ITS DEAD");
        var w1 = Wave(enc, "Wave1");
        foreach (var p in Ring(new Vector3(0f, 0f, 222f), 13f, 8)) Enemy(w1, p, 180f);
        var w2 = Wave(enc, "Wave2");
        foreach (var p in new[] { new Vector3(-28f, 0f, 198f), new Vector3(28f, 0f, 198f), new Vector3(-27f, 0f, 252f), new Vector3(27f, 0f, 252f), new Vector3(-6f, 0f, 194f), new Vector3(6f, 0f, 194f) })
            Enemy(w2, p, 180f);
        Enemy(w2, new Vector3(-31f, 6f, 240f), 90f); Enemy(w2, new Vector3(31f, 6f, 240f), -90f);
        var w3 = Wave(enc, "Wave3");
        foreach (var p in Ring(new Vector3(0f, 0f, 240f), 8f, 6)) Enemy(w3, p, 180f);
        Enemy(w3, new Vector3(-6f, 0f, 250f), 180f, elite: "Forge Overseer");
        Enemy(w3, new Vector3(6f, 0f, 250f), 180f, elite: "Forge Overseer");
        SetWaves(enc, w1, w2, w3);
    }

    // =====================================================================
    // TREASURY (z 262..312): gold, statues, the keeper, the last checkpoint.
    private static void BuildTreasury()
    {
        var z = Group(art, "4_Treasury");
        Floor(z, -15f, 15f, 262f, 312f);
        Solid("Floor_Treasury", new Vector3(0f, -0.5f, 287f), new Vector3(30f, 1f, 50f));
        TallWall(z, new Vector3(-15.5f, 0f, 262f), new Vector3(-15.5f, 0f, 312f), 20f);
        TallWall(z, new Vector3(15.5f, 0f, 262f), new Vector3(15.5f, 0f, 312f), 20f);
        foreach (var x in new[] { -12f, 12f })
        foreach (var pz in new[] { 270f, 282f, 294f, 306f })
        {
            Place(z, Hash() % 2 == 0 ? "SM_Prop_Gold_Pile_Large_01" : "SM_Prop_Gold_Pile_02", new Vector3(x, 0f, pz), Hash() % 360);
            Fit(z, "SM_Env_Statue_02", new Vector3(x * 1.15f, 0f, pz + 6f), x < 0 ? 90f : -90f, new Vector3(float.NaN, 3.2f, float.NaN));
        }
        Place(z, "SM_Prop_Dwarf_TreasureChest_01", new Vector3(-9f, 0f, 300f), 70f);
        Cache(new Vector3(-9f, 0f, 300f), "item_soulcask", 2, "OPEN");
        Cache(new Vector3(9f, 0f, 276f), "item_vial", 3, "SEARCH");
        Place(z, "SM_Prop_Barrel_Shelf_Large_01", new Vector3(10.5f, 0f, 276f), -90f);
        foreach (var cz in new[] { 274f, 298f }) Chandelier(z, new Vector3(0f, 14f, cz));
        PlaceCheckpoint(new Vector3(-5f, 0f, 304f), 90f, "Treasury", false);
        Npc("Vault Keeper", "PolygonDarkFantasy", "Characters/SM_Chr_Gravedigger_Male_01", new Vector3(6f, 0f, 296f), -110f, false, "VAULT KEEPER",
            new[]
            {
                "You came through the forge alive. The king's men stopped coming that way a long time ago.",
                "The king kept the Crimson stock under his own hall. He said no one else could be trusted with it.",
                "He was right, in a way. It never left. It went into him instead.",
                "He is very large now. Stand under him and he will crush you. Stand far and the floor itself will reach you.",
                "If you mean to go up, jump the shockwaves. Don't try to outrun them.",
            },
            new[] { "Jump the shockwaves.", "He still thinks the hold is his." });
        // Grand stair to the throne hall: 12.5 m over 25 m.
        StairRun(z, new Vector3(0f, 0f, 312f), new Vector3(0f, Throne, 337f), 10f);
        Facade(z, new Vector3(-5.5f, 0f, 312f), new Vector3(-5.5f, 0f, 337f), Throne + 4f);
        Facade(z, new Vector3(5.5f, 0f, 312f), new Vector3(5.5f, 0f, 337f), Throne + 4f);
        Solid("StairWallW", new Vector3(-5.8f, (Throne + 4f) * 0.5f, 324.5f), new Vector3(0.6f, Throne + 4f, 25f));
        Solid("StairWallE", new Vector3(5.8f, (Throne + 4f) * 0.5f, 324.5f), new Vector3(0.6f, Throne + 4f, 25f));
        CrossWall(z, 312f, -15f, 15f, 5f, 20f);
    }

    // =====================================================================
    // THRONE HALL (z 337..407, x -30..30, floor y 12.5, walls 30 m): the Swollen King.
    private static Health BuildThroneHall(AnimatorController bossCtrl)
    {
        var z = Group(art, "5_ThroneHall");
        var y = Throne;
        Floor(z, -30f, 30f, 337f, 407f, y);
        Solid("Floor_Throne", new Vector3(0f, y - 0.5f, 372f), new Vector3(60f, 1f, 70f));
        Solid("ThroneMass", new Vector3(0f, (y - 1f) * 0.5f, 372f), new Vector3(60f, y - 1f, 70f));
        TallWall(z, new Vector3(-30.5f, y, 337f), new Vector3(-30.5f, y, 407f), 30f);
        TallWall(z, new Vector3(30.5f, y, 337f), new Vector3(30.5f, y, 407f), 30f);
        TallWall(z, new Vector3(-31f, y, 407.5f), new Vector3(31f, y, 407.5f), 30f);
        WallRun(z, new Vector3(-31f, y, 336.5f), new Vector3(-5.5f, y, 336.5f), 30f);
        WallRun(z, new Vector3(5.5f, y, 336.5f), new Vector3(31f, y, 336.5f), 30f);
        Solid("ThroneLintel", new Vector3(0f, y + 18f, 336.5f), new Vector3(11f, 24f, 1f));
        Fit(z, "SM_Env_Dwarf_Wall_Archway_03", new Vector3(0f, y, 336.5f), 0f, new Vector3(13f, 9f, float.NaN));

        // Pillar ring (cover from the rings is NOT given — the waves pass through).
        for (var i = 0; i < 8; i++)
        {
            var a = (i + 0.5f) * Mathf.PI * 2f / 8f;
            var p = new Vector3(Mathf.Cos(a) * 22f, y, 372f + Mathf.Sin(a) * 22f);
            Fit(z, "SM_Env_Dwarf_Pillar_06", p, 0f, new Vector3(2.8f, 26f, 2.8f));
            Solid("ThronePillar", p + Vector3.up * 13f, new Vector3(2.5f, 26f, 2.5f));
            Lamp(p + new Vector3(0f, 4f, 0f) - (p - new Vector3(0f, y, 372f)).normalized * 2f, Warm(), 9f, 1.6f);
        }
        // Throne on its dais.
        Floor(z, -7f, 7f, 393f, 405f, y + 1.5f);
        Solid("ThroneDais", new Vector3(0f, y + 0.75f, 399f), new Vector3(14f, 1.5f, 12f));
        Place(z, "SM_Prop_Dwarf_Throne_01", new Vector3(0f, y + 1.5f, 401f), 180f, 2.6f);
        foreach (var x in new[] { -9f, 9f })
        {
            Place(z, "SM_Prop_Gold_Pile_Large_01", new Vector3(x, y, 398f), Hash() % 360, 1.4f);
            Crystal(z, new Vector3(x * 1.6f, y, 402f), crimson, 1.4f);
            Lamp(new Vector3(x * 1.6f, y + 2f, 402f), new Color(1f, 0.12f, 0.1f), 12f, 3f);
        }
        Runes(z, new Vector3(-30f, y + 8f, 342f), new Vector3(-30f, y + 8f, 402f), crimson, true);
        Runes(z, new Vector3(30f, y + 8f, 342f), new Vector3(30f, y + 8f, 402f), crimson, false);
        foreach (var cz in new[] { 352f, 388f }) Chandelier(z, new Vector3(0f, y + 22f, cz));
        Banner(z, new Vector3(0f, y + 12f, 407.2f), 180f);

        // Fog seal at the arena mouth.
        var boss = BuildBoss(bossCtrl, new Vector3(0f, y, 380f));
        var gate = new GameObject("FogGate_Throne"); gate.transform.SetParent(play); gate.transform.position = new Vector3(0f, y + 1.6f, 339f);
        var trig = gate.AddComponent<BoxCollider>(); trig.isTrigger = true; trig.size = new Vector3(10f, 4f, 3f);
        var blocker = new GameObject("FogGateBlocker"); blocker.transform.SetParent(gate.transform, false);
        blocker.transform.position = new Vector3(0f, y + 3f, 336.3f);
        blocker.AddComponent<BoxCollider>().size = new Vector3(11f, 6f, 0.6f);
        blocker.SetActive(false);
        var fog = GameObject.CreatePrimitive(PrimitiveType.Quad); fog.name = "FogWall";
        Object.DestroyImmediate(fog.GetComponent<Collider>());
        fog.transform.SetParent(gate.transform, false);
        fog.transform.position = new Vector3(0f, y + 3.2f, 336f);
        fog.transform.localScale = new Vector3(11f, 6.4f, 1f);
        fog.GetComponent<MeshRenderer>().sharedMaterial = fogWall;
        var fg = gate.AddComponent<FogGate>();
        Health bossHealth = null;
        if (boss != null)
        {
            Set(fg, "boss", boss);
            bossHealth = boss.GetComponent<Health>();
        }
        Set(fg, "blocker", blocker); Set(fg, "fog", fog);

        var exitGo = new GameObject("ThroneExit"); exitGo.transform.SetParent(play); exitGo.transform.position = new Vector3(0f, y + 1.5f, 404f);
        var exit = exitGo.AddComponent<LevelExit>();
        if (bossHealth != null)
        {
            var so = new SerializedObject(exit);
            var req = so.FindProperty("requires"); req.arraySize = 1; req.GetArrayElementAtIndex(0).objectReferenceValue = bossHealth;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        Set(exit, "promptText", "CLAIM THE THRONE ROOM");
        Set(exit, "banner", "THE VAULT FALLS SILENT");
        Lamp(new Vector3(0f, y + 3f, 404f), Violet(0.9f), 6f, 1.2f);
        return bossHealth;
    }

    /// <summary>The Swollen King: the DungeonRealms dwarf king scaled to a giant,
    /// a war hammer in the right hand, ColossusBase.controller, root-motion relay,
    /// foot grounding, chest aim point, and the BossColossus brain.</summary>
    private static BossColossus BuildBoss(AnimatorController ctrl, Vector3 pos)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(KitRoot + "/Characters/Chr_BR_Dwarf_King_01.prefab");
        if (prefab == null || ctrl == null) { report.Add("Boss skipped: dwarf king prefab or controller missing."); return null; }
        const float targetHeight = 7.5f; // a giant against 26 m pillars, not a big man
        var go = new GameObject("SwollenKing");
        go.transform.SetParent(play);
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, 180f, 0f));
        var health = go.AddComponent<Health>();
        Set(health, "maxHealth", 2600f); Set(health, "soulsReward", 9000); Set(health, "manaReward", 0);
        var blood = ProjectRestartCombat.FindFx("FX_BloodSplat_01");
        if (blood != null) Set(health, "bloodFx", blood);

        var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab, go.transform);
        PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        visual.name = "SwollenKingVisual";
        visual.transform.localPosition = Vector3.zero; visual.transform.localRotation = Quaternion.identity; visual.transform.localScale = Vector3.one;
        var b0 = RenderBounds(visual);
        var s = targetHeight / Mathf.Max(0.5f, b0.size.y);
        visual.transform.localScale = Vector3.one * s;
        var b = RenderBounds(visual);
        visual.transform.position += new Vector3(0f, pos.y - b.min.y, 0f);
        foreach (var c in visual.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        ProjectOwnedUrp(visual, "King");

        var anim = visual.GetComponentInChildren<Animator>(true);
        if (anim == null || anim.avatar == null || !anim.avatar.isHuman) { report.Add("Boss: dwarf king has no humanoid Animator."); return null; }
        anim.runtimeAnimatorController = ctrl;
        anim.applyRootMotion = true;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        if (anim.GetComponent<RootMotionRelay>() == null) anim.gameObject.AddComponent<RootMotionRelay>();
        var fgr = anim.GetComponent<FootGrounding>() ?? anim.gameObject.AddComponent<FootGrounding>();
        Set(fgr, "pelvisOffsetMax", 1.2f); Set(fgr, "attackPelvisOffsetMax", 1.8f);

        var hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
        var hammer = Resolve(KitRoot, "SM_Wep_WarHammer_Large_01");
        if (hand != null && hammer != null)
        {
            var w = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(hammer), hand);
            w.name = "KingHammer";
            w.transform.localPosition = Vector3.zero; w.transform.localRotation = Quaternion.identity; w.transform.localScale = Vector3.one;
            foreach (var c in w.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            ProjectOwnedUrp(w, "King");
            FitGrip(anim, w.transform);
        }

        var cc = go.AddComponent<CharacterController>();
        cc.height = targetHeight * 0.92f; cc.radius = 1.7f; cc.center = new Vector3(0f, cc.height * 0.5f + 0.05f, 0f);
        cc.slopeLimit = 45f; cc.stepOffset = 0.5f;
        var tgt = go.AddComponent<Targetable>();
        var chest = anim.GetBoneTransform(HumanBodyBones.UpperChest) ?? anim.GetBoneTransform(HumanBodyBones.Chest);
        if (chest != null) Set(tgt, "aimPoint", chest);

        var brain = go.AddComponent<BossColossus>();
        Set(brain, "bossAnimator", anim);
        Set(brain, "height", targetHeight);
        var so = new SerializedObject(brain);
        WriteCue(so.FindProperty("roarFx"), "FX_Fire_Big_01", "root", new Vector3(0f, 0.5f, 0f), 2.4f, 5, 2.5f);
        WriteCue(so.FindProperty("slamFx"), "FX_GroundCrack_Blast_01", "root", Vector3.zero, 3f, 0, 2.5f);
        so.ApplyModifiedPropertiesWithoutUndo();
        return brain;
    }

    // =====================================================================
    private static void BuildAtmosphere(Scene scene)
    {
        RenderSettings.skybox = null;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = new Color(0.05f, 0.05f, 0.075f);
        RenderSettings.fogDensity = 0.014f;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.19f, 0.19f, 0.26f);
        RenderSettings.ambientEquatorColor = new Color(0.11f, 0.1f, 0.13f);
        RenderSettings.ambientGroundColor = new Color(0.05f, 0.04f, 0.05f);
        foreach (var light in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Light>(true)).Where(l => l.type == LightType.Directional))
        {
            light.intensity = 0.55f;
            light.color = new Color(0.78f, 0.8f, 1f);
            light.transform.rotation = Quaternion.Euler(62f, -28f, 0f);
        }
        foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Where(c => c.CompareTag("MainCamera")))
        {
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = RenderSettings.fogColor;
            camera.farClipPlane = Mathf.Max(camera.farClipPlane, 260f);
        }
        Kill(new Vector3(0f, -45f, 200f), new Vector3(200f, 6f, 460f));
    }

    // =====================================================================
    // Encounter helpers
    private static GameObject Encounter(string name, Vector3 center, Vector3 size, GameObject[] seals, string toast)
    {
        var go = new GameObject(name); go.transform.SetParent(play); go.transform.position = center;
        go.AddComponent<BoxCollider>().size = size;
        var enc = go.AddComponent<CrowdEncounter>();
        var so = new SerializedObject(enc);
        var sp = so.FindProperty("seals"); sp.arraySize = seals.Length;
        for (var i = 0; i < seals.Length; i++) sp.GetArrayElementAtIndex(i).objectReferenceValue = seals[i];
        so.FindProperty("startToast").stringValue = toast;
        so.ApplyModifiedPropertiesWithoutUndo();
        return go;
    }

    private static Transform Wave(GameObject encounter, string name)
    {
        var t = Group(encounter.transform, name);
        return t;
    }

    private static void SetWaves(GameObject encounter, params Transform[] waves)
    {
        var so = new SerializedObject(encounter.GetComponent<CrowdEncounter>());
        var wp = so.FindProperty("waves"); wp.arraySize = waves.Length;
        for (var i = 0; i < waves.Length; i++)
        {
            wp.GetArrayElementAtIndex(i).objectReferenceValue = waves[i].gameObject;
            waves[i].gameObject.SetActive(false);
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>Arena seal: a portcullis skin over a blocker, inactive until the fight starts.</summary>
    private static GameObject Seal(Vector3 bottom, float width, float yaw)
    {
        var go = new GameObject("ArenaSeal"); go.transform.SetParent(play);
        go.transform.SetPositionAndRotation(bottom, Quaternion.Euler(0f, yaw, 0f));
        var blocker = new GameObject("Blocker"); blocker.transform.SetParent(go.transform, false);
        blocker.transform.localPosition = Vector3.up * 3f;
        blocker.AddComponent<BoxCollider>().size = new Vector3(width, 6f, 0.6f);
        var skin = Fit(go.transform, "SM_Env_Dwarf_Gate_01", bottom, yaw, new Vector3(width, 6f, float.NaN));
        skin.isStatic = false;
        foreach (var t in skin.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = false;
        var glow = new GameObject("SealLight"); glow.transform.SetParent(go.transform, false); glow.transform.localPosition = Vector3.up * 2f;
        var l = glow.AddComponent<Light>(); l.type = LightType.Point; l.color = new Color(1f, 0.15f, 0.12f); l.range = width; l.intensity = 2f; l.shadows = LightShadows.None;
        go.SetActive(false);
        return go;
    }

    private static IEnumerable<Vector3> Ring(Vector3 c, float r, int n)
    {
        for (var i = 0; i < n; i++)
        {
            var a = i * Mathf.PI * 2f / n + 0.3f;
            yield return c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
        }
    }

    private static void Enemy(Transform parent, Vector3 pos, float yaw, string elite = null)
    {
        var go = Object.Instantiate(enemyTemplate, parent);
        go.name = elite != null ? "Elite " + elite : "Hollowed Miner";
        go.SetActive(true);
        // Template capsules have no CharacterController in edit mode (EnemyAI adds it in
        // Awake) — place the capsule's bottom on the floor from whichever body exists.
        // (Explicit TryGetComponent: the editor's fake-null GetComponent result defeats ??.)
        var bottom = 0f;
        if (go.TryGetComponent<CharacterController>(out var ccb)) bottom = ccb.center.y - ccb.height * 0.5f;
        else if (go.TryGetComponent<CapsuleCollider>(out var cap)) bottom = cap.center.y - cap.height * 0.5f;
        go.transform.SetPositionAndRotation(new Vector3(pos.x, pos.y - bottom * go.transform.lossyScale.y + 0.02f, pos.z), Quaternion.Euler(0f, yaw, 0f));
        var health = go.GetComponent<Health>();
        var ai = go.GetComponent<EnemyAI>();
        if (elite == null)
        {
            Set(health, "maxHealth", 90f); Set(health, "soulsReward", 45); Set(health, "manaReward", 6);
            Set(ai, "damage", 13f); Set(ai, "poiseMax", 0f);
        }
        else
        {
            Set(health, "maxHealth", 380f); Set(health, "soulsReward", 500); Set(health, "manaReward", 12);
            Set(ai, "damage", 24f); Set(ai, "poiseMax", 50f);
            Set(ai, "eliteName", elite);
        }
        Set(ai, "sightRange", 14f);
        Set(ai, "deaggroRange", 40f);
    }

    private static void PlaceCheckpoint(Vector3 pos, float yaw, string name, bool lit)
    {
        var go = new GameObject("Checkpoint " + name); go.transform.SetParent(play);
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        var cp = go.AddComponent<Checkpoint>();
        Set(cp, "displayName", name); Set(cp, "litAtStart", lit);
    }

    private static void Npc(string name, string pack, string prefab, Vector3 pos, float yaw, bool sitting,
                            string speaker, string[] lines, string[] repeat)
    {
        var path = (pack == "PolygonDarkFantasy" ? DarkRoot : KitRoot) + "/" + prefab + ".prefab";
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var go = new GameObject("NPC " + name); go.transform.SetParent(story);
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        GameObject model = null;
        if (src != null)
        {
            model = (GameObject)PrefabUtility.InstantiatePrefab(src, go.transform);
            model.transform.localPosition = Vector3.zero; model.transform.localRotation = Quaternion.identity;
            var anim = model.GetComponentInChildren<Animator>(true);
            if (anim != null)
            {
                anim.runtimeAnimatorController = sitting ? sitCtrl : standCtrl;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            }
        }
        else report.Add("NPC model missing: " + path);
        var capsule = go.AddComponent<CapsuleCollider>();
        capsule.center = new Vector3(0f, sitting ? 0.6f : 0.9f, 0f); capsule.height = sitting ? 1.2f : 1.8f; capsule.radius = 0.35f;
        var talk = go.AddComponent<NpcTalk>();
        var so = new SerializedObject(talk);
        so.FindProperty("speaker").stringValue = speaker;
        so.FindProperty("facePlayer").boolValue = !sitting;
        so.FindProperty("model").objectReferenceValue = model;
        WriteLines(so.FindProperty("lines"), lines);
        WriteLines(so.FindProperty("repeatLines"), repeat);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void Inscription(Vector3 pos, float yaw, string stone, string[] lines, string prompt = "READ")
    {
        var go = new GameObject("Inscription"); go.transform.SetParent(story);
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
        if (stone != null) Fit(art, stone, pos, yaw, new Vector3(1.1f, 2.2f, float.NaN));
        var talk = go.AddComponent<NpcTalk>();
        var so = new SerializedObject(talk);
        so.FindProperty("speaker").stringValue = "INSCRIPTION";
        so.FindProperty("promptText").stringValue = prompt;
        so.FindProperty("facePlayer").boolValue = false;
        so.FindProperty("radius").floatValue = 2.2f;
        WriteLines(so.FindProperty("lines"), lines);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void Cache(Vector3 pos, string itemFile, int count, string prompt)
    {
        var item = AssetDatabase.LoadAssetAtPath<ItemDef>("Assets/_Project/Items/" + itemFile + ".asset");
        if (item == null) { report.Add("Item missing: " + itemFile); return; }
        var go = new GameObject("Cache " + itemFile); go.transform.SetParent(play); go.transform.position = pos;
        var glow = new GameObject("Glint"); glow.transform.SetParent(go.transform, false); glow.transform.localPosition = Vector3.up * 0.6f;
        var l = glow.AddComponent<Light>(); l.type = LightType.Point; l.color = new Color(1f, 0.75f, 0.45f); l.range = 2.4f; l.intensity = 1.6f; l.shadows = LightShadows.None;
        var cache = go.AddComponent<ItemCache>();
        Set(cache, "item", item); Set(cache, "count", count); Set(cache, "promptText", prompt); Set(cache, "marker", glow);
    }

    private static void WriteLines(SerializedProperty p, string[] lines)
    {
        lines ??= System.Array.Empty<string>();
        p.arraySize = lines.Length;
        for (var i = 0; i < lines.Length; i++) p.GetArrayElementAtIndex(i).stringValue = lines[i];
    }

    // =====================================================================
    // Geometry helpers — invisible collision; art never collides.
    private static GameObject Solid(string name, Vector3 center, Vector3 size)
    {
        var go = new GameObject(name); go.transform.SetParent(col); go.transform.position = center;
        go.AddComponent<BoxCollider>().size = size;
        go.isStatic = true;
        return go;
    }

    /// <summary>Walkable ramp collider from a (bottom-centre, low end) to b (high end).</summary>
    private static void Ramp(string name, Vector3 a, Vector3 b, float width)
    {
        var d = b - a; var flat = new Vector3(d.x, 0f, d.z); var len = d.magnitude;
        var go = new GameObject(name); go.transform.SetParent(col);
        _ = flat;
        var rot = Quaternion.LookRotation(d.normalized, Vector3.up); // pitched forward, up as close to world-up as possible
        go.transform.rotation = rot;
        go.transform.position = (a + b) * 0.5f - (rot * Vector3.up) * 0.25f;
        go.AddComponent<BoxCollider>().size = new Vector3(width, 0.5f, len);
        go.isStatic = true;
    }

    private static void Kill(Vector3 center, Vector3 size)
    {
        var go = new GameObject("KillZone"); go.transform.SetParent(play); go.transform.position = center;
        var b = go.AddComponent<BoxCollider>(); b.isTrigger = true; b.size = size;
        go.AddComponent<KillZone>();
    }

    private static void Glow(string name, Vector3 center, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(art); go.transform.position = center; go.transform.localScale = size;
        var r = go.GetComponent<MeshRenderer>(); r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.isStatic = true;
    }

    /// <summary>Wall collision a→b plus a stacked kit facade in 5 m tiers (Dungeon_01 rhythm:
    /// heavy base, plain middles, trim band at the top) and pillars every 10 m.</summary>
    private static GameObject TallWall(Transform parent, Vector3 a, Vector3 b, float h)
    {
        var c = WallCollider(a, b, h);
        for (var y = 0f; y < h - 0.01f; y += 5f)
        {
            var tierH = Mathf.Min(5f, h - y);
            var piece = y == 0f ? "SM_Env_Dwarf_Wall_05" : y + 5f >= h ? "SM_Env_Dwarf_Wall_02" : "SM_Env_Dwarf_Wall_03";
            Facade(parent, a + Vector3.up * y, b + Vector3.up * y, tierH, piece);
        }
        var len = (b - a).magnitude;
        for (var t = 10f; t < len - 1f; t += 10f)
            Fit(parent, "SM_Env_Dwarf_Pillar_02", Vector3.Lerp(a, b, t / len), 0f, new Vector3(1.4f, h + 0.5f, 1.4f));
        return c;
    }

    private static GameObject WallRun(Transform parent, Vector3 a, Vector3 b, float h) => TallWall(parent, a, b, h);

    private static GameObject WallCollider(Vector3 a, Vector3 b, float h)
    {
        var mid = (a + b) * 0.5f; var d = b - a; var len = d.magnitude;
        var alongX = Mathf.Abs(d.x) > Mathf.Abs(d.z);
        return Solid("Wall", new Vector3(mid.x, a.y + h * 0.5f, mid.z), alongX ? new Vector3(len, h, 1f) : new Vector3(1f, h, len));
    }

    /// <summary>Cross wall at z from x0..x1 with a centred opening (no lintel above 8 m — open sightline).</summary>
    private static void CrossWall(Transform parent, float z, float x0, float x1, float halfOpen, float h)
    {
        TallWall(parent, new Vector3(x0, 0f, z), new Vector3(-halfOpen, 0f, z), h);
        TallWall(parent, new Vector3(halfOpen, 0f, z), new Vector3(x1, 0f, z), h);
        Solid("Lintel", new Vector3(0f, 8f + (h - 8f) * 0.5f, z), new Vector3(halfOpen * 2f, h - 8f, 1f));
        Facade(parent, new Vector3(-halfOpen, 8f, z), new Vector3(halfOpen, 8f, z), h - 8f, "SM_Env_Dwarf_Wall_03");
        Fit(parent, "SM_Env_Dwarf_Wall_Archway_03", new Vector3(0f, 0f, z), 0f, new Vector3(halfOpen * 2f + 2f, 8f, float.NaN));
    }

    /// <summary>Free-standing marked wall-run fin (violet strip) — crowd escape / flank route.</summary>
    private static void Fin(Transform parent, Vector3 a, Vector3 b, float h)
    {
        var c = WallCollider(a, b, h);
        c.transform.localScale = new Vector3(0.8f, 1f, 1f);
        c.AddComponent<WallRunSurface>();
        for (var y = 0f; y < h - 0.01f; y += 5f)
            Facade(parent, a + Vector3.up * y, b + Vector3.up * y, Mathf.Min(5f, h - y), y == 0f ? "SM_Env_Dwarf_Wall_05" : "SM_Env_Dwarf_Wall_03", 0.8f);
        var mid = (a + b) * 0.5f; var len = (b - a).magnitude;
        foreach (var side in new[] { -1f, 1f })
            Glow("WallRunMark", new Vector3(mid.x + side * 0.42f, 1.5f, mid.z), new Vector3(0.05f, 0.25f, len - 1f), runMark);
        Lamp(mid + Vector3.up * 4f, Violet(0.7f), 8f, 1.1f);
    }

    /// <summary>Kit wall modules (≤5 m each) from a to b — visual only.</summary>
    private static void Facade(Transform parent, Vector3 a, Vector3 b, float h, string piece = null, float depth = float.NaN)
    {
        var n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / 5f - 0.01f));
        for (var i = 0; i < n; i++)
        {
            var p0 = Vector3.Lerp(a, b, (float)i / n); var p1 = Vector3.Lerp(a, b, (float)(i + 1) / n);
            Span(parent, piece ?? (Hash() % 7 == 0 ? "SM_Env_Dwarf_Wall_01" : "SM_Env_Dwarf_Wall_05"), p0, p1, h, depth);
        }
    }

    private static void Rail(Transform parent, Vector3 a, Vector3 b)
    {
        var n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / 5f - 0.01f));
        for (var i = 0; i < n; i++)
            Span(parent, "SM_Env_Dwarf_Balustrade_03", Vector3.Lerp(a, b, (float)i / n), Vector3.Lerp(a, b, (float)(i + 1) / n), 1.1f);
    }

    /// <summary>Kit floor tiles over a rectangle, top flush with y (Dungeon_01 floors).</summary>
    private static void Floor(Transform parent, float x0, float x1, float z0, float z1, float y = 0f)
    {
        int nx = Mathf.Max(1, Mathf.RoundToInt((x1 - x0) / 5f)), nz = Mathf.Max(1, Mathf.RoundToInt((z1 - z0) / 5f));
        float cx = (x1 - x0) / nx, cz = (z1 - z0) / nz;
        var square = Mathf.Abs(cx - cz) < 0.01f;
        for (var i = 0; i < nx; i++)
        for (var j = 0; j < nz; j++)
        {
            var h = Hash();
            var piece = h % 11 == 0 ? "SM_Env_Dwarf_Floor_26" : h % 5 == 0 ? "SM_Env_Dwarf_Floor_02" : "SM_Env_Dwarf_Floor_21";
            var yaw = square ? (h % 4) * 90f : (h % 2) * 180f;
            var size = (yaw % 180f) == 0f ? new Vector3(cx, float.NaN, cz) : new Vector3(cz, float.NaN, cx);
            Fit(parent, piece, new Vector3(x0 + (i + 0.5f) * cx, y, z0 + (j + 0.5f) * cz), yaw, size, true);
        }
    }

    /// <summary>A straight stair from a (top or bottom edge centre) to b: native 2.5 m kit
    /// runs oriented by measuring which way the mesh rises, plus a smooth ramp collider.</summary>
    private static void StairRun(Transform parent, Vector3 a, Vector3 b, float width)
    {
        var low = a.y <= b.y ? a : b; var high = a.y <= b.y ? b : a;
        var flat = new Vector3(high.x - low.x, 0f, high.z - low.z);
        var steps = Mathf.Max(1, Mathf.RoundToInt((high.y - low.y) / 2.5f));
        var rise = (high.y - low.y) / steps; var run = flat.magnitude / steps;
        var dirYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        var ascend = AscendYaw("SM_Env_Dwarf_Stairs_01");
        for (var i = 0; i < steps; i++)
        {
            var c = low + flat.normalized * run * (i + 0.5f) + Vector3.up * rise * i;
            var go = Spawn(parent, "SM_Env_Dwarf_Stairs_01");
            var s0 = Bounds(go).size;
            // Local axes: the rising axis (ascend) maps to the run; scale to the step box.
            go.transform.rotation = Quaternion.Euler(0f, dirYaw - ascend, 0f);
            var alongZ = Mathf.Abs(Mathf.DeltaAngle(ascend, 0f)) < 45f || Mathf.Abs(Mathf.DeltaAngle(ascend, 180f)) < 45f;
            go.transform.localScale = alongZ
                ? new Vector3(width / Mathf.Max(0.1f, s0.x), rise / Mathf.Max(0.1f, s0.y), run / Mathf.Max(0.1f, s0.z))
                : new Vector3(run / Mathf.Max(0.1f, s0.x), rise / Mathf.Max(0.1f, s0.y), width / Mathf.Max(0.1f, s0.z));
            Ground(go, c, false);
        }
        Ramp("StairRamp", low, high, width);
    }

    /// <summary>Yaw (deg) of the direction a stair mesh rises, in its local frame:
    /// compares mean vertex height on each side of the bounds centre.</summary>
    private static float AscendYaw(string name)
    {
        var go = Spawn(art, name);
        try
        {
            var pts = new List<Vector3>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue; // editor keeps a CPU copy even for non-readable imports
                foreach (var v in mf.sharedMesh.vertices) pts.Add(go.transform.InverseTransformPoint(mf.transform.TransformPoint(v)));
            }
            if (pts.Count == 0) { report.Add("Stair rise direction unknown (mesh not readable) — assumed +Z."); return 0f; }
            var c = pts.Aggregate(Vector3.zero, (s, p) => s + p) / pts.Count;
            float Rise(System.Func<Vector3, float> axis)
            {
                var hi = pts.Where(p => axis(p) > axis(c)).Select(p => p.y).DefaultIfEmpty(0f).Average();
                var lo = pts.Where(p => axis(p) <= axis(c)).Select(p => p.y).DefaultIfEmpty(0f).Average();
                return hi - lo;
            }
            var rz = Rise(p => p.z); var rx = Rise(p => p.x);
            var yaw = Mathf.Abs(rz) >= Mathf.Abs(rx) ? (rz >= 0f ? 0f : 180f) : (rx >= 0f ? 90f : -90f);
            report.Add($"Stair rises along local yaw {yaw} (Δz {rz:F2}, Δx {rx:F2}).");
            return yaw;
        }
        finally { Object.DestroyImmediate(go); }
    }

    private static void Torch(Transform parent, Vector3 pos)
    {
        Place(parent, "SM_Prop_Dwarf_Brazier_01", pos, 0f);
        Fx("FX_Fire_Small_01", pos + Vector3.up * 1.05f);
        Lamp(pos + Vector3.up * 1.6f, Warm(), 8f, 2.2f);
    }

    private static void Chandelier(Transform parent, Vector3 pos)
    {
        Place(parent, "SM_Prop_Dwarf_Chandelier_01", pos, Hash() % 360, 2f);
        Lamp(pos + Vector3.down * 1.5f, Warm(), 18f, 2.4f);
    }

    private static void Banner(Transform parent, Vector3 pos, float yaw) => Place(parent, "SM_Prop_Banner_04", pos, yaw, 1.6f);

    /// <summary>A line of glowing rune symbols along a wall (Dungeon_01's rune bands).</summary>
    private static void Runes(Transform parent, Vector3 a, Vector3 b, Material mat, bool faceEast)
    {
        var len = (b - a).magnitude;
        for (var t = 0f; t <= len; t += 6f)
        {
            var p = Vector3.Lerp(a, b, t / Mathf.Max(0.01f, len)) + new Vector3(faceEast ? 0.6f : -0.6f, 0f, 0f);
            var name = "SM_Rune_Symbol_" + (new[] { 16, 49, 20, 29, 51 })[Hash() % 5].ToString("00");
            if (Resolve(KitRoot, name) == null) continue;
            var go = Place(parent, name, p, faceEast ? 90f : -90f, 1.6f);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                r.sharedMaterials = Enumerable.Repeat(mat, r.sharedMaterials.Length).ToArray();
        }
    }

    private static void Crystal(Transform parent, Vector3 pos, Material mat, float scale)
    {
        var go = Place(parent, Hash() % 2 == 0 ? "SM_Env_Crystals_Cluster_Large_01" : "SM_Env_Crystals_Cluster_Large_02", pos, Hash() % 360, scale);
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            r.sharedMaterials = Enumerable.Repeat(mat, r.sharedMaterials.Length).ToArray();
    }

    private static void Lamp(Vector3 pos, Color c, float range, float intensity)
    {
        var go = new GameObject("Light"); go.transform.SetParent(lights); go.transform.position = pos;
        var l = go.AddComponent<Light>(); l.type = LightType.Point; l.color = c; l.range = range; l.intensity = intensity; l.shadows = LightShadows.None;
    }

    private static void Fx(string name, Vector3 pos, Color? tint = null)
    {
        var path = Resolve(FxRoot, name);
        if (path == null) { report.Add("FX missing: " + name); return; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), art);
        go.transform.position = pos;
        if (tint.HasValue)
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main; // instance override only
                var a = main.startColor.color.a;
                main.startColor = new Color(tint.Value.r, tint.Value.g, tint.Value.b, a <= 0f ? 1f : a);
            }
    }

    private static GameObject Place(Transform parent, string name, Vector3 bottom, float yaw, float scale = 1f)
    {
        var go = Spawn(parent, name);
        go.transform.localScale = Vector3.one * scale;
        go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        Ground(go, bottom, false);
        return go;
    }

    private static GameObject Fit(Transform parent, string name, Vector3 point, float yaw, Vector3 size, bool topAligned = false)
    {
        var go = Spawn(parent, name);
        var s0 = Bounds(go).size;
        float sx = float.IsNaN(size.x) ? float.NaN : size.x / Mathf.Max(s0.x, 0.01f);
        float sy = float.IsNaN(size.y) ? float.NaN : size.y / Mathf.Max(s0.y, 0.01f);
        float sz = float.IsNaN(size.z) ? float.NaN : size.z / Mathf.Max(s0.z, 0.01f);
        var known = new[] { sx, sy, sz }.Where(v => !float.IsNaN(v)).ToArray();
        var mean = known.Length > 0 ? known.Average() : 1f;
        go.transform.localScale = new Vector3(float.IsNaN(sx) ? mean : sx, float.IsNaN(sy) ? mean : sy, float.IsNaN(sz) ? mean : sz);
        go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        Ground(go, point, topAligned);
        return go;
    }

    private static GameObject Span(Transform parent, string name, Vector3 a, Vector3 b, float h, float depth = float.NaN)
    {
        var go = Spawn(parent, name);
        var s0 = Bounds(go).size;
        var d = b - a; d.y = 0f;
        var len = d.magnitude;
        var longX = s0.x >= s0.z;
        var yaw = longX ? Mathf.Atan2(-d.z, d.x) * Mathf.Rad2Deg : Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        var sl = len / Mathf.Max(longX ? s0.x : s0.z, 0.01f);
        var sy = h / Mathf.Max(s0.y, 0.01f);
        var sd = float.IsNaN(depth) ? Mathf.Clamp(sy, 0.5f, 2f) : depth / Mathf.Max(longX ? s0.z : s0.x, 0.01f);
        go.transform.localScale = longX ? new Vector3(sl, sy, sd) : new Vector3(sd, sy, sl);
        go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        Ground(go, (a + b) * 0.5f, false);
        return go;
    }

    private static GameObject Spawn(Transform parent, string name)
    {
        var path = Resolve(KitRoot, name);
        Require(path != null, "Kit prefab missing: " + name);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), parent);
        go.transform.localPosition = Vector3.zero; go.transform.rotation = Quaternion.identity; go.transform.localScale = Vector3.one;
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        foreach (var l in go.GetComponentsInChildren<Light>(true)) l.enabled = false;
        foreach (var p in go.GetComponentsInChildren<ParticleSystem>(true)) p.gameObject.SetActive(false);
        foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;
        return go;
    }

    private static void Ground(GameObject go, Vector3 point, bool topAligned)
    {
        var b = Bounds(go);
        go.transform.position += point - new Vector3(b.center.x, topAligned ? b.max.y : b.min.y, b.center.z);
    }

    private static Bounds Bounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToArray();
        Require(rs.Length > 0, "No geometry: " + go.name);
        var b = rs[0].bounds; foreach (var r in rs.Skip(1)) b.Encapsulate(r.bounds);
        return b;
    }

    private static Bounds RenderBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer)).ToArray();
        if (rs.Length == 0) return new Bounds(go.transform.position + Vector3.up, Vector3.one * 2f);
        var b = rs[0].bounds; foreach (var r in rs.Skip(1)) b.Encapsulate(r.bounds);
        return b;
    }

    private static string Resolve(string folder, string name)
    {
        var key = folder + "|" + name;
        if (prefabPaths.TryGetValue(key, out var p)) return p;
        p = AssetDatabase.FindAssets(name + " t:Prefab", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(x => System.IO.Path.GetFileNameWithoutExtension(x) == name);
        prefabPaths[key] = p;
        return p;
    }

    // =====================================================================
    // Controllers
    private static void Prepare()
    {
        prefabPaths.Clear();
        lava = UnlitMat("Lava", new Color(2.2f, 0.7f, 0.15f));
        crimson = UnlitMat("CrimsonCorestone", new Color(1.9f, 0.08f, 0.12f));
        violet = UnlitMat("VioletRunes", new Color(0.75f, 0.32f, 1.9f));
        rune = violet;
        runMark = UnlitMat("WallRunMark", new Color(0.62f, 0.3f, 1.2f));
        fogWall = UnlitMat("FogWall", new Color(0.9f, 0.12f, 0.16f, 0.32f), transparent: true);
        sitCtrl = IdleController("NpcIdle_Sit", "Assets/ThirdParty/SoulslikeEssential/Bonfire_Idle.FBX");
        standCtrl = IdleController("NpcIdle_Stand", "Assets/ThirdParty/CLazyRunner/Animations/P1_CLazyMovement/Mvm_Idle/CLazy@Idle_Wait_B.FBX");
    }

    /// <summary>Adds the crowd states EnemyAI looks for to EnemyBase.controller —
    /// HasState-guarded, existing states untouched. In-place takes only (enemy
    /// root motion is off except the knockdown fall).</summary>
    private static void ExtendEnemyController()
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(EnemyControllerPath);
        if (ctrl == null) { report.Add("EnemyBase.controller missing — run Setup Skeleton Enemies first; crowd states skipped."); return; }
        var sm = ctrl.layers[0].stateMachine;
        var move = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Move");
        AnimatorState Add(string name, AnimationClip clip, Vector3 pos, float speed, bool exitToMove)
        {
            if (clip == null) { report.Add("Enemy clip missing for " + name); return null; }
            var st = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == name) ?? sm.AddState(name, pos);
            st.motion = clip; st.speed = speed; st.writeDefaultValues = false;
            st.transitions = System.Array.Empty<AnimatorStateTransition>();
            if (exitToMove && move != null)
            {
                var t = st.AddTransition(move);
                t.hasExitTime = true; t.exitTime = 0.9f; t.duration = 0.2f; t.hasFixedDuration = true;
            }
            return st;
        }
        Add("Attack2", ProjectRestartCombat.EnsureClipYBake(Clip(Grz + "2_Attacks/0__3Combos/M_Big_Sword@Attack_3Combo_2_Inplace.FBX")), new Vector3(0, -140), 1.05f, true);
        Add("Heavy", ProjectRestartCombat.EnsureClipYBake(Clip(Grz + "2_Attacks/1__4Combos/M_Big_Sword@Attack_4Combo_4_Inplace.FBX")), new Vector3(0, -280), 0.85f, true);
        Add("Lunge", ProjectRestartCombat.EnsureClipYBake(Clip(Grz + "2_Attacks/1__4Combos/M_Big_Sword@Attack_4Combo_1B_Inplace.FBX")), new Vector3(0, -420), 1.1f, true);
        Add("Knockdown", ProjectRestartCombat.EnsureDodgeRootClip(Clip(Grz + "4_Damages/1__Front/M_Big_Sword@Damage_Front_High_KnockDown.FBX")), new Vector3(300, -140), 1f, false);
        Add("GetUp", ProjectRestartCombat.EnsureClipYBake(Clip(Grz + "4_Damages/1__Front/M_Big_Sword@Damage_Front_Down_StandUp.FBX")), new Vector3(300, -280), 1.15f, true);
        EditorUtility.SetDirty(ctrl);
        report.Add("EnemyBase.controller: Attack2/Heavy/Lunge/Knockdown/GetUp ensured.");
    }

    /// <summary>ColossusBase.controller — the king's own clip player (golem-style:
    /// code owns every transition). Giant cadence: slowed strikes.</summary>
    private static AnimatorController BuildBossController()
    {
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(BossControllerPath) ?? AnimatorController.CreateAnimatorControllerAtPath(BossControllerPath);
        ctrl.parameters = System.Array.Empty<AnimatorControllerParameter>();
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        var sm = ctrl.layers[0].stateMachine;
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(BossControllerPath))
            if (a is BlendTree bt && bt.name == "KingMove") AssetDatabase.RemoveObjectFromAsset(bt);
        var tree = new BlendTree { name = "KingMove", blendType = BlendTreeType.Simple1D, blendParameter = "Speed", useAutomaticThresholds = false };
        tree.AddChild(ProjectRestartCombat.EnsureClipYBake(Clip(Grz + "1_Movements/1__Idle/M_Big_Sword@Idle.FBX")), 0f);
        tree.AddChild(ProjectRestartCombat.EnsureClipYBake(Clip(Grz + "1_Movements/2__Walk/A/M_Big_Sword@Walk_ver_A_Front.FBX")), 1f);
        var kids = tree.children; kids[1].timeScale = 0.75f; tree.children = kids;
        AssetDatabase.AddObjectToAsset(tree, ctrl);
        AnimatorState State(string name, Motion m, Vector3 pos, float speed)
        {
            var st = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == name) ?? sm.AddState(name, pos);
            st.motion = m; st.speed = speed; st.writeDefaultValues = false;
            return st;
        }
        var loco = State("Locomotion", tree, new Vector3(-300, 0), 1f);
        sm.defaultState = loco;
        State("Sweep", ProjectRestartCombat.EnsureAttackRootClip(Clip(Grz + "2_Attacks/1__4Combos/M_Big_Sword@Attack_4Combo_2.FBX")), new Vector3(0, -140), 0.7f);
        State("Smash", ProjectRestartCombat.EnsureAttackRootClip(Clip(Grz + "2_Attacks/1__4Combos/M_Big_Sword@Attack_4Combo_4.FBX")), new Vector3(0, 0), 0.6f);
        State("Combo", ProjectRestartCombat.EnsureAttackRootClip(Clip(Grz + "2_Attacks/2__7Combos/M_Big_Sword@Attack_7Combo_5.FBX")), new Vector3(0, 140), 0.7f);
        State("Leap", ProjectRestartCombat.EnsureAttackRootClip(Clip(Grz + "2_Attacks/3__Dash_Attack/M_Big_Sword@Dash_Attack_ver_A.FBX")), new Vector3(300, -140), 0.85f);
        State("Stomp", Clip(Mk + "atk_energy05.fbx"), new Vector3(300, 0), 0.65f);
        State("Roar", Clip(Mk + "buff01.fbx"), new Vector3(300, 140), 0.85f);
        State("Stagger", ProjectRestartCombat.EnsureClipYBake(Clip(Grz + "4_Damages/1__Front/M_Big_Sword@Damage_Front_High_KnockDown_ZeroHeight.FBX")), new Vector3(600, -140), 0.8f);
        State("GetUp", ProjectRestartCombat.EnsureClipYBake(Clip(Grz + "4_Damages/1__Front/M_Big_Sword@Damage_Front_Down_StandUp.FBX")), new Vector3(600, 0), 0.8f);
        State("Die", ProjectRestartCombat.EnsureClipYBake(Clip(Grz + "4_Damages/6__Die/M_Big_Sword@Damage_Die.FBX")), new Vector3(600, 140), 0.8f);
        foreach (var s in sm.states) s.state.transitions = System.Array.Empty<AnimatorStateTransition>();
        sm.anyStateTransitions = System.Array.Empty<AnimatorStateTransition>();
        ProjectRestartCombat.SetIkPass(ctrl);
        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    /// <summary>Legacy Standard vendor materials render magenta under URP. Swap each
    /// renderer onto a project-owned URP Lit copy (texture/colour/emission carried over)
    /// in the level's material folder — the vendor .mat stays untouched.</summary>
    private static void ProjectOwnedUrp(GameObject go, string prefix)
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null) return;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            var changed = false;
            for (var i = 0; i < mats.Length; i++)
            {
                var src = mats[i];
                if (src == null || src.shader == null || src.shader.name.StartsWith("Universal Render Pipeline")) continue;
                var path = MatFolder + "/" + prefix + "_" + src.name.Replace("/", "_") + ".mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(lit) { name = prefix + " " + src.name };
                    if (src.HasProperty("_MainTex") && src.GetTexture("_MainTex") != null) m.SetTexture("_BaseMap", src.GetTexture("_MainTex"));
                    if (src.HasProperty("_Color")) m.SetColor("_BaseColor", src.GetColor("_Color"));
                    if (src.HasProperty("_EmissionMap") && src.GetTexture("_EmissionMap") != null && src.IsKeywordEnabled("_EMISSION"))
                    {
                        m.SetTexture("_EmissionMap", src.GetTexture("_EmissionMap"));
                        m.SetColor("_EmissionColor", src.HasProperty("_EmissionColor") ? src.GetColor("_EmissionColor") : Color.white);
                        m.EnableKeyword("_EMISSION");
                    }
                    m.SetFloat("_Smoothness", 0.15f);
                    AssetDatabase.CreateAsset(m, path);
                }
                mats[i] = m;
                changed = true;
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    private static AnimationClip Clip(string fbx)
    {
        var all = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__")).ToArray();
        if (all.Length == 0) report.Add("Clip missing: " + fbx);
        return all.FirstOrDefault();
    }

    private static Material UnlitMat(string name, Color c, bool transparent = false)
    {
        var path = MatFolder + "/" + name + ".mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Unlit")); AssetDatabase.CreateAsset(m, path); }
        m.SetColor("_BaseColor", c);
        if (transparent)
        {
            m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        EditorUtility.SetDirty(m);
        return m;
    }

    private static AnimatorController IdleController(string name, string fbx)
    {
        var path = AnimFolder + "/" + name + ".controller";
        var clip = Clip(fbx);
        if (clip == null) return null;
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(path) ?? AnimatorController.CreateAnimatorControllerAtPath(path);
        var sm = ctrl.layers[0].stateMachine;
        var state = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == "Idle") ?? sm.AddState("Idle");
        state.motion = clip;
        sm.defaultState = state;
        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    private static void WriteCue(SerializedProperty cue, string prefabName, string attach, Vector3 offset, float scale, int palette, float life)
    {
        if (cue == null) return;
        var prefab = ProjectRestartCombat.FindFx(prefabName);
        if (prefab == null) { report.Add("FX cue prefab missing: " + prefabName); return; }
        cue.FindPropertyRelative("prefab").objectReferenceValue = prefab;
        cue.FindPropertyRelative("attach").stringValue = attach;
        cue.FindPropertyRelative("offset").vector3Value = offset;
        cue.FindPropertyRelative("scale").floatValue = scale;
        cue.FindPropertyRelative("palette").intValue = palette;
        cue.FindPropertyRelative("life").floatValue = life;
    }

    /// <summary>Foundry Depths is replaced: its scene moves to an archive folder
    /// (not deleted) and leaves Build Settings.</summary>
    private static void RetireFoundryDepths()
    {
        if (!System.IO.File.Exists(OldScenePath)) return;
        const string archive = "Assets/_Project/ArtDirection/Archive";
        EnsureFolder(archive);
        var target = archive + "/03_FoundryDepths_Retired.unity";
        if (System.IO.File.Exists(target)) AssetDatabase.DeleteAsset(target);
        var err = AssetDatabase.MoveAsset(OldScenePath, target);
        report.Add(string.IsNullOrEmpty(err) ? "Foundry Depths archived to " + target : "Foundry Depths archive failed: " + err);
    }

    private static void Validate(Health boss)
    {
        var problems = new List<string>();
        var enemies = Object.FindObjectsByType<EnemyAI>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        var encounters = Object.FindObjectsByType<CrowdEncounter>(FindObjectsSortMode.None).Length;
        var runWalls = Object.FindObjectsByType<WallRunSurface>(FindObjectsSortMode.None).Length;
        if (boss == null) problems.Add("no boss");
        if (encounters < 2) problems.Add("encounters " + encounters);
        if (runWalls < 8) problems.Add("wall-run surfaces " + runWalls);
        if (enemies < 40) problems.Add("enemies " + enemies);
        if (problems.Count == 0) Debug.Log($"[SunkenVaultCheck] PASS: {enemies} enemies ({encounters} sealed arenas), {runWalls} wall-run surfaces, boss wired. Static wiring only.");
        else Debug.LogWarning("[SunkenVaultCheck] " + string.Join("; ", problems));
    }

    // =====================================================================
    // King weapon grip + pose review

    /// <summary>Seat a hafted weapon in a closed fist, measured from the rig instead of
    /// trusting the hand bone's axes: the haft runs through the fist from the little-finger
    /// knuckle toward the index knuckle (head on the thumb side), the head's striking face
    /// points the way the knuckles face, and the haft end sits just below the fist.
    /// The weapon's long axis / head end come from its own mesh. Run with the rig in its
    /// bind pose (edit mode) — the result is stored hand-local, so it rides every clip.</summary>
    private static void FitGrip(Animator anim, Transform weapon)
    {
        var hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
        if (hand == null || weapon == null) return;
        var idx = anim.GetBoneTransform(HumanBodyBones.RightIndexProximal);
        var lit = anim.GetBoneTransform(HumanBodyBones.RightLittleProximal) ?? anim.GetBoneTransform(HumanBodyBones.RightRingProximal);
        var mid = anim.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        var thumb = anim.GetBoneTransform(HumanBodyBones.RightThumbProximal);

        // Hand frame (world): knuckle-forward, haft axis across the fist.
        var forward = ((mid != null ? mid.position : idx != null ? idx.position : hand.position + hand.forward * .1f) - hand.position).normalized;
        Vector3 across;
        if (idx != null && lit != null) across = (idx.position - lit.position).normalized;
        else if (thumb != null) across = Vector3.ProjectOnPlane(thumb.position - hand.position, forward).normalized;
        else across = Vector3.Cross(forward, Vector3.up).normalized;
        across = Vector3.ProjectOnPlane(across, forward).normalized;
        var palm = idx != null && lit != null ? Vector3.Lerp(idx.position, lit.position, .5f) : Vector3.Lerp(hand.position, mid != null ? mid.position : hand.position, .6f);
        var fist = Vector3.Lerp(hand.position, palm, .55f); // the fist closes between wrist and knuckles

        // Weapon frame (local): long axis, head end, head face axis — from vertices.
        var pts = new List<Vector3>();
        foreach (var mf in weapon.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null)
                foreach (var v in mf.sharedMesh.vertices) pts.Add(weapon.InverseTransformPoint(mf.transform.TransformPoint(v)));
        if (pts.Count < 4) { report.Add("Grip fit skipped: weapon mesh unreadable."); return; }
        var min = pts[0]; var max = pts[0];
        foreach (var p in pts) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var size = max - min;
        var axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        Vector3 Unit(int a) => a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward;
        var mean = pts.Aggregate(Vector3.zero, (s, p) => s + p) / pts.Count;
        var centre = (min + max) * .5f;
        // The head carries most of the vertices: the mean leans toward it.
        var headSign = mean[axis] >= centre[axis] ? 1f : -1f;
        var longLocal = Unit(axis) * headSign;
        var headEnd = headSign > 0f ? max[axis] : min[axis];
        var buttEnd = headSign > 0f ? min[axis] : max[axis];
        var len = Mathf.Abs(headEnd - buttEnd);
        // Head face axis: the wider of the two remaining extents, measured in the head third.
        var head = pts.Where(p => Mathf.Abs(p[axis] - headEnd) < len * .3f).ToList();
        var o1 = (axis + 1) % 3; var o2 = (axis + 2) % 3;
        float Ext(int a) { if (head.Count == 0) return size[a]; var lo = head.Min(p => p[a]); var hi = head.Max(p => p[a]); return hi - lo; }
        var faceLocal = Unit(Ext(o1) >= Ext(o2) ? o1 : o2);

        // Rotation: weapon long axis → across (haft through the fist), face → knuckle-forward.
        var faceWorldWant = Vector3.ProjectOnPlane(forward, across).normalized;
        var rot = Quaternion.LookRotation(across, faceWorldWant) * Quaternion.Inverse(Quaternion.LookRotation(longLocal, faceLocal));
        weapon.rotation = rot;
        // Position: the grip point (a sixth up from the haft end) lands in the fist.
        var gripLocal = centre;
        gripLocal[axis] = buttEnd + (headEnd - buttEnd) * .17f;
        weapon.position += fist - weapon.TransformPoint(gripLocal);
        report.Add($"King weapon grip fitted on {hand.name}: long axis {"XYZ"[axis]}{(headSign > 0 ? "+" : "-")}, local pos {weapon.localPosition:F3}, rot {weapon.localEulerAngles:F0}.");
    }

    [MenuItem("Tools/Project Restart/Level/Refit King Weapon Grip")]
    public static void RefitKingGrip()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        report.Clear();
        var boss = Object.FindFirstObjectByType<BossColossus>();
        var anim = boss != null ? boss.GetComponentInChildren<Animator>(true) : null;
        var hammer = anim != null ? anim.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "KingHammer") : null;
        if (hammer == null) { Debug.LogWarning("[SunkenVault] Open 03_SunkenVault — no BossColossus/KingHammer found."); return; }
        hammer.localPosition = Vector3.zero; hammer.localRotation = Quaternion.identity;
        FitGrip(anim, hammer);
        EditorSceneManager.MarkSceneDirty(boss.gameObject.scene);
        EditorSceneManager.SaveScene(boss.gameObject.scene);
        Debug.Log("[SunkenVault] " + string.Join("\n", report));
    }

    /// <summary>Samples the king's controller clips in edit mode (AnimationMode) and renders
    /// a front and a side view of each pose — rig, retarget and weapon-grip review.</summary>
    [MenuItem("Tools/Project Restart/Level/Capture King Poses")]
    public static void CaptureKingPoses()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var boss = Object.FindFirstObjectByType<BossColossus>();
        var anim = boss != null ? boss.GetComponentInChildren<Animator>(true) : null;
        var ctrl = anim != null ? anim.runtimeAnimatorController as AnimatorController : null;
        if (ctrl == null) { Debug.LogWarning("[SunkenVault] Open 03_SunkenVault — the king or its controller is missing."); return; }
        var poses = new (string state, float t)[] { ("Locomotion", .3f), ("Sweep", .5f), ("Smash", .6f), ("Stomp", .55f), ("Leap", .5f), ("Combo", .35f), ("Roar", .5f), ("Stagger", .7f) };
        const string dir = "Tools/LevelReview/King";
        System.IO.Directory.CreateDirectory(dir);
        var host = new GameObject("Temporary King Review Camera") { hideFlags = HideFlags.HideAndDontSave };
        var cam = host.AddComponent<Camera>(); cam.enabled = false; cam.fieldOfView = 40f; cam.farClipPlane = 200f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.18f, .18f, .22f); cam.cullingMask = ~(1 << 5);
        var rt = new RenderTexture(640, 640, 24); cam.targetTexture = rt;
        var img = new Texture2D(640, 640, TextureFormat.RGB24, false);
        var sheet = new Texture2D(640 * 4, 640 * 4, TextureFormat.RGB24, false);
        var prior = RenderTexture.active;
        var root = boss.transform;
        var h = 7.5f;
        AnimationMode.StartAnimationMode();
        try
        {
            for (var i = 0; i < poses.Length; i++)
            {
                var st = ctrl.layers[0].stateMachine.states.Select(s => s.state).FirstOrDefault(s => s.name == poses[i].state);
                var clip = st?.motion as AnimationClip ?? (st?.motion as BlendTree)?.children.FirstOrDefault().motion as AnimationClip;
                if (clip == null) continue;
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(anim.gameObject, clip, clip.length * poses[i].t);
                AnimationMode.EndSampling();
                for (var v = 0; v < 2; v++)
                {
                    var dirV = v == 0 ? root.forward : root.right;
                    cam.transform.position = root.position + dirV * h * 1.6f + Vector3.up * h * .55f;
                    cam.transform.LookAt(root.position + Vector3.up * h * .5f);
                    cam.Render();
                    RenderTexture.active = rt; img.ReadPixels(new Rect(0, 0, 640, 640), 0, 0); img.Apply();
                    System.IO.File.WriteAllBytes($"{dir}/{i:00}_{poses[i].state}_{(v == 0 ? "front" : "side")}.png", img.EncodeToPNG());
                    var cell = i * 2 + v;
                    sheet.SetPixels((cell % 4) * 640, (3 - cell / 4) * 640, 640, 640, img.GetPixels());
                }
            }
            sheet.Apply();
            System.IO.File.WriteAllBytes(dir + "/KingPoses.png", sheet.EncodeToPNG());
        }
        finally
        {
            AnimationMode.StopAnimationMode();
            RenderTexture.active = prior; cam.targetTexture = null;
            Object.DestroyImmediate(rt); Object.DestroyImmediate(img); Object.DestroyImmediate(sheet); Object.DestroyImmediate(host);
        }
        Debug.Log("[SunkenVault] King pose sheet written to " + dir + "/KingPoses.png (edit-mode samples — root motion and grounding are not applied).");
    }

    // =====================================================================
    [MenuItem("Tools/Project Restart/Level/Capture Sunken Vault Views")]
    public static void CaptureViews()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (SceneManager.GetActiveScene().path != ScenePath) { Debug.LogWarning("[SunkenVault] Open " + ScenePath + " first."); return; }
        var shots = new (string name, Vector3 pos, Vector3 look)[]
        {
            ("01_ArrivalOverlook", new Vector3(0f, Ledge + 2.2f, 3f), new Vector3(0f, 2f, 60f)),
            ("02_Plaza", new Vector3(22f, 9f, 28f), new Vector3(-4f, 2f, 70f)),
            ("03_PlazaFins", new Vector3(-2f, 2.2f, 38f), new Vector3(0f, 3f, 70f)),
            ("04_ChasmCourse", new Vector3(0f, 3f, 92f), new Vector3(0f, -1f, 140f)),
            ("05_ChasmSide", new Vector3(13f, 6f, 100f), new Vector3(-3f, -6f, 135f)),
            ("06_Cathedral", new Vector3(25f, 12f, 186f), new Vector3(0f, 2f, 225f)),
            ("07_Treasury", new Vector3(0f, 3f, 264f), new Vector3(0f, 2f, 300f)),
            ("08_ThroneHall", new Vector3(0f, Throne + 4f, 339f), new Vector3(0f, Throne + 3f, 380f)),
            ("09_KingCloseup", new Vector3(5f, Throne + 2f, 372f), new Vector3(0f, Throne + 3.5f, 380f)),
            ("10_Overview", new Vector3(110f, 140f, 120f), new Vector3(0f, 0f, 210f)),
        };
        const string dir = "Tools/LevelReview/SunkenVault";
        System.IO.Directory.CreateDirectory(dir);
        var host = new GameObject("Temporary Level Review Camera") { hideFlags = HideFlags.HideAndDontSave };
        var cam = host.AddComponent<Camera>(); cam.enabled = false; cam.fieldOfView = 60f; cam.farClipPlane = 500f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = RenderSettings.fogColor; cam.cullingMask = ~(1 << 5);
        var rt = new RenderTexture(960, 540, 24);
        cam.targetTexture = rt;
        var img = new Texture2D(960, 540, TextureFormat.RGB24, false);
        var prior = RenderTexture.active;
        try
        {
            foreach (var s in shots)
            {
                cam.transform.position = s.pos; cam.transform.LookAt(s.look);
                cam.Render();
                RenderTexture.active = rt; img.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); img.Apply();
                System.IO.File.WriteAllBytes(dir + "/" + s.name + ".png", img.EncodeToPNG());
            }
        }
        finally
        {
            RenderTexture.active = prior; cam.targetTexture = null;
            Object.DestroyImmediate(rt); Object.DestroyImmediate(img); Object.DestroyImmediate(host);
        }
        Debug.Log("[SunkenVault] " + shots.Length + " review shots written to " + dir + " (composition only — not a playtest).");
    }

    // =====================================================================
    private static Transform Group(Transform parent, string name)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        return go.transform;
    }

    private static int Hash() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed >> 4; }
    private static Color Warm() => new Color(1f, 0.55f, 0.25f);
    private static Color Lava() => new Color(1f, 0.42f, 0.12f);
    private static Color Violet(float k) => new Color(0.55f * k, 0.25f * k, 1f * k);

    private static void Set(Object obj, string field, object value)
    {
        if (obj == null) return;
        var so = new SerializedObject(obj);
        var p = so.FindProperty(field);
        if (p == null) { report.Add($"{obj.GetType().Name} has no field '{field}'"); return; }
        switch (value)
        {
            case bool b: p.boolValue = b; break;
            case int i: if (p.propertyType == SerializedPropertyType.Float) p.floatValue = i; else p.intValue = i; break;
            case float f: if (p.propertyType == SerializedPropertyType.Integer) p.intValue = Mathf.RoundToInt(f); else p.floatValue = f; break;
            case string s: p.stringValue = s; break;
            case Object o: p.objectReferenceValue = o; break;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new System.InvalidOperationException("[SunkenVault] " + message);
    }
}

/// <summary>Tools > Project Restart > FX > Play Effect Gallery — Play Mode only.</summary>
public static class FxGalleryMenu
{
    [MenuItem("Tools/Project Restart/FX/Play Effect Gallery (Play Mode)")]
    public static void Play()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[FX] Enter Play Mode first, then run the gallery."); return; }
        TraversalEffects.PlayGallery();
    }
}
