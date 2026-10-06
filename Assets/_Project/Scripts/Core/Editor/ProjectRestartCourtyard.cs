using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Tools > Project Restart > Build Courtyard Scene.
/// Creates `Assets/_Project/Scenes/01_Courtyard.unity` — a separate
/// project-owned encounter yard (detail §250), built by cloning the test
/// blockout so the Player/camera/HUD wiring comes along intact, stripping the
/// level roots, then laying out a ~24m × 20m walled yard:
///
///   - two fighting pockets — 3 light enemies west, 4 light + 1 elite east;
///   - an optional 8-enemy stress ring, placed inactive;
///   - two marked wall-run surfaces (crimson stripe = the mark);
///   - one low plunge platform (2.2m) with a 3-riser stair;
///   - a slope ramp and restrained perimeter ruins;
///   - a lit checkpoint — resting resets every encounter.
///
/// Bare Targetable capsules are promoted + dressed by SetupIn — the same
/// skeleton/nav pipeline the blockout uses — so a rerun rebuilds everything.
/// </summary>
public static class ProjectRestartCourtyard
{
    private const string SourcePath = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const string ScenePath = "Assets/_Project/Scenes/01_Courtyard.unity";
    private const string RootName = "Courtyard";
    private const string MatPath = "Assets/_Project/Materials/TestGround.mat";

    // Level roots the clone must not keep — matched by ROOT GameObject name
    // only, so nothing under Player/HUD can be hit accidentally.
    private static readonly string[] StripRoots =
    {
        "RuinLevel", "Test Ground", "NavGrid",
        "BossArenaFloor", "BossArenaFloor2", "FogGate", "FogGate2",
        "FogGateBlocker", "FogGateBlocker2", "DarkLordBoss", "FortGolemBoss",
        "MerchantStall", "WeaponPickup", "Checkpoint A", "Checkpoint B",
        "Wall Run Test Wall", "Wall Run Test Wall (1)",
        "Lock-On Test Dummy", "Lock-On Test Dummy (1)", "Lock-On Test Dummy (2)",
        "Lock-On Test Dummy (3)", "Lock-On Test Dummy (4)", "Lock-On Test Dummy (5)",
        "Lock-On Test Dummy (6)", "Lock-On Test Dummy (7)", "Lock-On Test Dummy (8)",
        "Lock-On Test Dummy (9)",
    };

    [MenuItem("Tools/Project Restart/Build Courtyard Scene")]
    public static void Setup()
    {
        ProjectRestartUrpFix.FixAll();
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            Require(AssetDatabase.CopyAsset(SourcePath, ScenePath),
                    "Could not clone " + SourcePath + " — run the test-scene setup first.");
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        foreach (var go in scene.GetRootGameObjects().ToArray())
            if (StripRoots.Contains(go.name)) Object.DestroyImmediate(go);
        var old = GameObject.Find(RootName);
        if (old != null) Object.DestroyImmediate(old);

        var root = new GameObject(RootName);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);

        BuildFloor(root.transform, mat);
        BuildPerimeter(root.transform, mat);
        BuildPlungePlatform(root.transform, mat);
        BuildSlope(root.transform, mat);
        BuildRuins(root.transform);
        var check = Checkpoint.EnsureNear(new Vector3(0f, 0f, -8f));
        check.transform.SetParent(root.transform, true);
        if (!check.Lit) check.LightSilently();

        BuildEncounters(root.transform);

        // Player + camera start at the south edge looking into the yard.
        var player = Object.FindFirstObjectByType<PlayerLocomotion>();
        if (player != null)
            player.transform.SetPositionAndRotation(new Vector3(0f, 0.2f, -7.5f), Quaternion.identity);
        var cam = Object.FindFirstObjectByType<Camera>();
        if (cam != null)
            cam.transform.SetPositionAndRotation(new Vector3(0f, 2.6f, -11.5f),
                Quaternion.Euler(8f, 0f, 0f));

        // NavGrid sized to the yard — PathGrid drives enemy A* around walls.
        var gridGo = GameObject.Find("NavGrid") ?? new GameObject("NavGrid");
        gridGo.transform.SetParent(root.transform, false);
        var grid = gridGo.GetComponent<PathGrid>() ?? gridGo.AddComponent<PathGrid>();
        var gso = new SerializedObject(grid);
        gso.FindProperty("center").vector3Value = Vector3.zero;
        gso.FindProperty("size").vector2Value = new Vector2(24f, 20f);
        gso.ApplyModifiedPropertiesWithoutUndo();

        // The skeleton-dress pipeline reads the OPEN scene — same promotion,
        // visuals and stats as the blockout enemies.
        Require(EditorSceneManager.SaveScene(scene), "Could not save " + ScenePath);
        AssetDatabase.SaveAssets();
        ProjectRestartEnemies.SetupIn(ScenePath);
        Debug.Log("[ProjectRestart] Courtyard built at 01_Courtyard.unity — 24×20m, two pockets " +
                  "(3 light / 4 light + elite), stress ring inactive, wall-run north+south, " +
                  "2.2m plunge platform, lit checkpoint. Play via the build settings or drag the scene in.");
    }

    // ---------- layout ----------

    /// <summary>One continuous fighting floor — no gaps inside the yard.</summary>
    private static void BuildFloor(Transform root, Material mat)
    {
        Slab(root, mat, "Floor_Main", new Vector3(0f, -0.5f, 0f), new Vector3(24f, 1f, 20f));
    }

    /// <summary>Low perimeter walls; the north and south runs are the marked
    /// wall-run surfaces — a crimson stripe slab on the face is the tell.</summary>
    private static void BuildPerimeter(Transform root, Material mat)
    {
        Wall(root, mat, new Vector3(0f, 1.6f, 10.4f), new Vector3(24f, 3.2f, 1f), wallRun: true, marked: true);
        Wall(root, mat, new Vector3(0f, 1.6f, -10.4f), new Vector3(24f, 3.2f, 1f), wallRun: true, marked: true);
        Wall(root, mat, new Vector3(12.4f, 1.0f, 0f), new Vector3(1f, 2.0f, 20f));
        Wall(root, mat, new Vector3(-12.4f, 1.0f, 0f), new Vector3(1f, 2.0f, 20f));
    }

    /// <summary>The low plunge platform — 2.2m ledge near the north wall with a
    /// three-riser stair up its west face. Jump off the edge + LMB = the slam.</summary>
    private static void BuildPlungePlatform(Transform root, Material mat)
    {
        Slab(root, mat, "PlungePlatform", new Vector3(5.5f, 1.1f, 7.5f), new Vector3(5f, 2.2f, 4f));
        for (var i = 0; i < 3; i++)
            Slab(root, mat, $"Stair_{i}", new Vector3(1.6f - i * 0.55f, 0.2f + i * 0.55f, 7.5f),
                 new Vector3(1.1f, 0.4f, 3f));
    }

    /// <summary>A walkable ramp on the west edge — slope traversal + attack
    /// checks on tilted ground.</summary>
    private static void BuildSlope(Transform root, Material mat)
    {
        var go = Slab(root, mat, "SlopeRamp", new Vector3(-9f, 0.55f, 5.5f), new Vector3(4f, 0.35f, 6f));
        go.transform.rotation = Quaternion.Euler(-12f, 0f, 0f);
    }

    /// <summary>Restrained perimeter ruins — broken pillars + braziers at the
    /// corners; the centre stays clear for fights.</summary>
    private static void BuildRuins(Transform root)
    {
        const string env = "Assets/ThirdParty/Synty/PolygonDungeon/Prefabs/Environments/";
        const string prop = "Assets/ThirdParty/Synty/PolygonDungeon/Prefabs/Props/";
        var spots = new[]
        {
            new Vector3(-10.5f, 0f, 9f), new Vector3(-10.5f, 0f, -9f),
            new Vector3(10.5f, 0f, 9f), new Vector3(10.5f, 0f, -9f),
            new Vector3(-10.5f, 0f, 0f), new Vector3(10.5f, 0f, -4f),
        };
        for (var i = 0; i < spots.Length; i++)
            Deco(root, env + "Pillars/SM_Env_Pillar_Square_02.prefab", spots[i],
                 Quaternion.Euler(0f, i * 47f, 0f));
        foreach (var p in new[] { new Vector3(-11f, 0f, 8f), new Vector3(11f, 0f, -8f) })
        {
            Deco(root, prop + "SM_Prop_Brazier_01.prefab", p, Quaternion.identity);
            var light = new GameObject("TorchLight");
            light.transform.SetParent(root, false);
            light.transform.position = p + Vector3.up * 1.6f;
            var l = light.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.62f, 0.3f);
            l.intensity = 1.6f;
            l.range = 9f;
        }
    }

    /// <summary>Resettable encounters (detail §252): pocket A — three light;
    /// pocket B — four light + one named elite; the stress ring is placed
    /// inactive so it can be enabled for a crowd test without rebuilding.</summary>
    private static void BuildEncounters(Transform root)
    {
        var pocketA = Group(root, "Encounter_A_3Light");
        Spawn(pocketA, new Vector3(-6.5f, 0f, -2.5f));
        Spawn(pocketA, new Vector3(-8f, 0f, 0.5f));
        Spawn(pocketA, new Vector3(-5f, 0f, 1.5f));

        var pocketB = Group(root, "Encounter_B_4Light_Elite");
        Spawn(pocketB, new Vector3(6f, 0f, 1f));
        Spawn(pocketB, new Vector3(8f, 0f, 3f));
        Spawn(pocketB, new Vector3(4.5f, 0f, 4f));
        Spawn(pocketB, new Vector3(7f, 0f, 5.5f));
        Spawn(pocketB, new Vector3(6.5f, 0f, 3.2f), eliteName: "Courtyard Warden");

        var stress = Group(root, "Encounter_C_Stress8_INACTIVE");
        for (var i = 0; i < 8; i++)
        {
            var a = i / 8f * Mathf.PI * 2f;
            Spawn(stress, new Vector3(Mathf.Cos(a) * 4.5f, 0f, Mathf.Sin(a) * 4.5f + 2f));
        }
        stress.gameObject.SetActive(false);
    }

    private static Transform Group(Transform root, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        return go.transform;
    }

    /// <summary>Bare capsule + Targetable — SetupIn adds Health/EnemyAI and the
    /// skeleton dressing on the same pass, so this works from a fresh scene.</summary>
    private static void Spawn(Transform group, Vector3 pos, string eliteName = null)
    {
        var dummy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        dummy.name = eliteName != null ? "Elite " + eliteName : "Courtyard Skeleton";
        dummy.transform.SetParent(group, false);
        dummy.transform.position = pos;
        if (dummy.GetComponent<Targetable>() == null) dummy.AddComponent<Targetable>();
        if (eliteName != null)
        {
            var ai = dummy.GetComponent<EnemyAI>() ?? dummy.AddComponent<EnemyAI>();
            var so = new SerializedObject(ai);
            var p = so.FindProperty("eliteName");
            if (p != null) p.stringValue = eliteName;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    // ---------- primitives ----------

    private static GameObject Slab(Transform root, Material mat, string name, Vector3 pos, Vector3 size)
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

    private static void Wall(Transform root, Material mat, Vector3 pos, Vector3 size,
                             bool wallRun = false, bool marked = false)
    {
        var go = Slab(root, mat, "Wall", pos, size);
        if (wallRun && go.GetComponent<WallRunSurface>() == null) go.AddComponent<WallRunSurface>();
        if (!marked) return;
        // The mark: a narrow crimson stripe down the face — readable at speed.
        var mark = Slab(root, null, "WallRunMark", pos + Vector3.forward * (size.z < size.x ? -0.51f * Mathf.Sign(pos.z) : 0f)
                        + Vector3.right * (size.z < size.x ? 0f : -0.51f * Mathf.Sign(pos.x)),
                        new Vector3(size.z < size.x ? 2.2f : 0.12f, 1.4f, size.z < size.x ? 0.12f : 2.2f));
        var r = mark.GetComponent<MeshRenderer>();
        if (r != null)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor", new Color(0.55f, 0.05f, 0.1f));
            r.sharedMaterial = m;
        }
    }

    private static void Deco(Transform root, string path, Vector3 pos, Quaternion rot)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) { Debug.LogWarning("[ProjectRestart] Deco prefab missing: " + path); return; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.transform.SetParent(root, false);
        go.transform.SetPositionAndRotation(pos, rot);
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new System.Exception("[ProjectRestart] " + message);
    }
}

/// <summary>Separate playable teaching blockout; never replaces the ruin level.</summary>
public static class ProjectRestartFoundryTutorial
{
    const string Path = "Assets/_Project/Scenes/02_FoundryTutorial.unity";
    [MenuItem("Tools/Project Restart/Build Foundry Tutorial %#t")]
    public static void Build()
    {
        if(EditorApplication.isPlayingOrWillChangePlaymode) return;
        var source=SceneManager.GetActiveScene();
        if(source.path==Path) {
            EditorSceneManager.SaveScene(source);
            source=EditorSceneManager.OpenScene("Assets/_Project/Scenes/00_TestBlockout.unity",OpenSceneMode.Single);
        }
        if(source.path!="Assets/_Project/Scenes/00_TestBlockout.unity") { Debug.LogError("[Foundry] Open 00_TestBlockout first; tutorial copies its player/camera wiring."); return; }
        Selection.objects=System.Array.Empty<Object>();
        ProjectRestartProtagonist.AlignActiveVisual();
        EditorSceneManager.SaveScene(source);
        if(System.IO.File.Exists(Path)) {
            var backup="Assets/_Project/ArtDirection/Foundry_BeforeRebuild_"+System.DateTime.Now.ToString("yyyyMMdd_HHmmss")+".unity";
            AssetDatabase.CopyAsset(Path,backup);
        }
        if(!EditorSceneManager.SaveScene(source,Path,true)) throw new System.InvalidOperationException("Could not clone tutorial scene");
        var scene=EditorSceneManager.OpenScene(Path,OpenSceneMode.Single);
        var player=Object.FindFirstObjectByType<PlayerLocomotion>();
        var enemy=Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None).FirstOrDefault(e=>e.GetComponent<IBossEngage>() == null);
        if(!player || !enemy) throw new System.InvalidOperationException("Player and dressed skeleton required in source scene");
        var template=Object.Instantiate(enemy.gameObject);template.name="FoundryEnemyTemplate";template.SetActive(false);
        foreach(var go in scene.GetRootGameObjects())
        {
            bool keep=go==template || go.GetComponentInChildren<PlayerLocomotion>(true) ||
                go.GetComponentInChildren<Camera>(true) || go.GetComponentInChildren<Unity.Cinemachine.CinemachineCamera>(true) ||
                go.GetComponentInChildren<Canvas>(true) || go.GetComponentInChildren<UnityEngine.EventSystems.EventSystem>(true) ||
                (go.GetComponent<Light>() && go.GetComponent<Light>().type==LightType.Directional);
            if(!keep)Object.DestroyImmediate(go);
        }
        var root=new GameObject("FoundryWalk");
        var stone=Material("FoundryStone",new Color(0.30f,0.31f,0.35f));
        var dark=Material("FoundryIron",new Color(0.12f,0.10f,0.15f));
        var copper=Material("FoundryCopper",new Color(0.46f,0.27f,0.13f));
        var violet=Material("FoundryWallMark",new Color(0.37f,0.17f,0.53f));
        for(int i=0;i<10;i++)
        {
            float z=i*20+10;
            Slab(root.transform,"Floor_"+i,new Vector3(0,-0.5f,z),new Vector3(14,1,20),stone);
            for(int col=0;col<4;col++) for(int row=0;row<5;row++)
                Slab(root.transform,"FloorPanel",new Vector3(-5.25f+col*3.5f,0.012f,i*20+2+row*4),new Vector3(3.46f,0.024f,3.96f),stone,false);
            Slab(root.transform,"West_"+i,new Vector3(-7.5f,1.4f,z),new Vector3(1,2.8f,20),dark);
            Slab(root.transform,"East_"+i,new Vector3(7.5f,1.4f,z),new Vector3(1,2.8f,20),dark);
            // Repeated portal kit, open 6m sightline; no random piles of props.
            foreach(float x in new[]{-4f,4f}) Slab(root.transform,"PortalPier",new Vector3(x,3.5f,z+9),new Vector3(1,7,1),stone);
            Slab(root.transform,"PortalHeader",new Vector3(0,7,z+9),new Vector3(9,0.6f,1),copper);
        }
        Slab(root.transform,"EntryFloor",new Vector3(0,-0.5f,-5),new Vector3(14,1,10),stone);
        Slab(root.transform,"StartWall",new Vector3(0,1.5f,-10.5f),new Vector3(15,3,1),dark);
        var cc=player.GetComponent<CharacterController>();cc.enabled=false;
        player.transform.SetPositionAndRotation(new Vector3(0,0.08f,2),Quaternion.identity);cc.enabled=true;
        var cpGo=new GameObject("Foundry Checkpoint");cpGo.transform.SetParent(root.transform);cpGo.transform.position=new Vector3(-4,0,3);
        var cp=cpGo.AddComponent<Checkpoint>(); Set(cp,"litAtStart",true);Set(cp,"displayName","Foundry Walk");
        Slab(root.transform,"JumpSill",new Vector3(0,0.4f,9),new Vector3(12,0.8f,0.8f),copper);
        var a=Lesson(root.transform,0,TutorialTrigger.Lesson.Jump,"01 / Crossing","WASD moves. {SPRINT} sprints. {JUMP} jumps.\nJump the sill and reach the copper pad.",null,copper,new Vector3(0,0,16));
        Slab(root.transform,"SlideBeam",new Vector3(0,4f,26),new Vector3(6,0.5f,1),dark);
        Slab(root.transform,"MomentumSideL",new Vector3(-5,1.5f,26),new Vector3(4,3,1),stone);
        Slab(root.transform,"MomentumSideR",new Vector3(5,1.5f,26),new Vector3(4,3,1),stone);
        var b=Lesson(root.transform,1,TutorialTrigger.Lesson.Momentum,"02 / Momentum","Sprint and slide along the copper lane.\nDouble jump toward the ember.",a,copper,new Vector3(0,0,36));
        for(int side=0;side<2;side++) {
            float x=side==0?-3.5f:3.5f;
            var wall=Slab(root.transform,"MarkedRunWall",new Vector3(x,3,side==0?46:53),new Vector3(0.7f,6,10),stone);
            wall.AddComponent<WallRunSurface>();
            Slab(root.transform,"WallRunMarker",new Vector3(x+(side==0?0.36f:-0.36f),1.6f,side==0?46:53),new Vector3(0.04f,0.25f,9),violet,false);
        }
        var c=Lesson(root.transform,2,TutorialTrigger.Lesson.WallRun,"03 / Wall route","Jump beside a violet-marked wall while moving.\nAttach automatically; {JUMP} pushes off. Reach the pad.",b,copper,new Vector3(0,0,57));
        var basic=Spawn(template,root.transform,new Vector3(0,1,70),Quaternion.Euler(0,180,0),100,12);
        var d=Lesson(root.transform,3,TutorialTrigger.Lesson.Combat,"04 / Blade basics","LMB attacks. Middle click locks on. {SLIDE} dodges.\nPractise a dodge, then defeat the guard.",c,copper,null,basic);
        var rear=Spawn(template,root.transform,new Vector3(0,1,89),Quaternion.identity,200,8);
        var e=Lesson(root.transform,4,TutorialTrigger.Lesson.Backstab,"05 / Rear critical","Approach behind the guard. LMB close to its back.\nLand a rear critical; ordinary damage does not count.",d,copper,null,rear); Set(e,"practiceResources",true);
        var tech=Spawn(template,root.transform,new Vector3(0,1,109),Quaternion.Euler(0,180,0),1500,5);
        var f=Lesson(root.transform,5,TutorialTrigger.Lesson.Technique,"06 / Summoned blade","RMB summons a big-sword technique. Land a hit.\nTry RMB during normal 1, then chain RMB. Core Energy refills here.",e,copper,null,tech); Set(f,"practiceResources",true);
        var air=Spawn(template,root.transform,new Vector3(0,1,129),Quaternion.Euler(0,180,0),1500,5);
        var g=Lesson(root.transform,6,TutorialTrigger.Lesson.Aerial,"07 / Launcher","Two committed LMB normals, then RMB launches.\nConnect an air strike with either attack. Core Energy refills here.",f,copper,null,air); Set(g,"practiceResources",true);
        // A broad low ledge with ordinary risers, not an inaccessible floating asset.
        Slab(root.transform,"PlungeDeck",new Vector3(0,1.1f,144),new Vector3(6,2.2f,4),stone);
        for(int i=0;i<7;i++) Slab(root.transform,"Stair_"+i,new Vector3(-4.5f,0.15f*(i+1),139+i*0.55f),new Vector3(3,0.3f*(i+1),0.6f),stone);
        Slab(root.transform,"DeckLink",new Vector3(-3.5f,1.1f,144),new Vector3(2,2.2f,4),stone);
        var plunge=Spawn(template,root.transform,new Vector3(0,1,149),Quaternion.identity,1000,5);
        var h=Lesson(root.transform,7,TutorialTrigger.Lesson.Plunge,"08 / Plunge","Step off the ledge; airborne LMB plunges.\nLand the impact on the guard below.",g,copper,null,plunge);Set(h,"practiceResources",true);
        var restGo=new GameObject("Practice Checkpoint");restGo.transform.SetParent(root.transform);restGo.transform.position=new Vector3(-3,0,169);
        var rest=restGo.AddComponent<Checkpoint>();Set(rest,"displayName","Foundry Rest");
        var resources=Lesson(root.transform,8,TutorialTrigger.Lesson.Checkpoint,"09 / Recovery","R drinks a flask. E lights this Checkpoint; E again rests.\nRest refills pools and resets guards. Leave, then reach the pad.",h,copper,new Vector3(0,0,177));
        var final=new[]{Spawn(template,root.transform,new Vector3(-3,1,190),Quaternion.identity,80,10),Spawn(template,root.transform,new Vector3(3,1,194),Quaternion.Euler(0,180,0),100,10),Spawn(template,root.transform,new Vector3(0,1,197),Quaternion.Euler(0,180,0),100,10)};
        var flank=Slab(root.transform,"FinalMarkedWall",new Vector3(-5.5f,2.5f,191),new Vector3(0.7f,5,10),stone);flank.AddComponent<WallRunSurface>();
        Slab(root.transform,"WallRunMarker",new Vector3(-5.14f,1.6f,191),new Vector3(0.04f,0.25f,9),violet,false);
        Slab(root.transform,"FinalPlungeDeck",new Vector3(4.8f,0.8f,190),new Vector3(3,1.6f,4),stone);
        Lesson(root.transform,9,TutorialTrigger.Lesson.Assessment,"10 / The proving yard","Clear three guards. Choose wall flank, rear attack or blade.\nReach the exit pad to complete training.",resources,copper,new Vector3(0,0,198),final);
        Object.DestroyImmediate(template);
        ProjectRestartMechanicalEnvironment.Apply();
        EditorSceneManager.SaveScene(scene);AssetDatabase.SaveAssets();
        var build=EditorBuildSettings.scenes.ToList();if(!build.Any(s=>s.path==Path))build.Add(new EditorBuildSettingsScene(Path,true));else foreach(var s in build.Where(s=>s.path==Path))s.enabled=true;EditorBuildSettings.scenes=build.ToArray();
        ProjectRestartFoundryDress.Dress();
        SceneView.lastActiveSceneView?.LookAt(new Vector3(0,1,8),Quaternion.Euler(18,0,0),12);
        Debug.Log("[Foundry] Separate 10-lesson teaching blockout saved; action/contact gates, renewable practice targets/mana, source player/camera/HUD retained. Live route acceptance still required.");
    }
    static Material Material(string name,Color color)
    {
        var path="Assets/_Project/ArtDirection/Materials/"+name+".mat";var m=AssetDatabase.LoadAssetAtPath<Material>(path);
        if(!m){m=new Material(Shader.Find("Universal Render Pipeline/Lit"));m.SetColor("_BaseColor",color);m.SetFloat("_Smoothness",0);AssetDatabase.CreateAsset(m,path);}return m;
    }
    static GameObject Slab(Transform root,string name,Vector3 pos,Vector3 size,Material mat,bool solid=true)
    {
        var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(root);go.transform.position=pos;go.transform.localScale=size;go.GetComponent<Renderer>().sharedMaterial=mat;if(!solid)go.GetComponent<Collider>().enabled=false;return go;
    }
    static Health Spawn(GameObject template,Transform root,Vector3 position,Quaternion rotation,float hp,float damage)
    {
        var go=Object.Instantiate(template,root);go.name="Foundry Guard";go.transform.SetPositionAndRotation(position,rotation);go.SetActive(true);
        var body=go.GetComponent<CharacterController>();
        if(body)go.transform.position=new Vector3(position.x,body.height*0.5f-body.center.y,position.z);
        var health=go.GetComponent<Health>();Set(health,"maxHealth",hp);Set(health,"soulsReward",0);Set(health,"manaReward",0);
        var ai=go.GetComponent<EnemyAI>();Set(ai,"damage",damage);Set(ai,"sightRange",7f);Set(ai,"deaggroRange",10f);Set(ai,"poiseMax",0f);Set(ai,"peripheralRange",0.5f);return health;
    }
    static TutorialTrigger Lesson(Transform root,int index,TutorialTrigger.Lesson kind,string title,string message,TutorialTrigger previous,Material mat,Vector3? pad,params Health[] targets)
    {
        float z=index*20+10;
        var gate=Slab(root,"LessonGate",new Vector3(0,3.5f,z+9.5f),new Vector3(7,7,0.3f),mat);
        var go=new GameObject(title);go.transform.SetParent(root);go.transform.position=new Vector3(0,3,z);var col=go.AddComponent<BoxCollider>();col.isTrigger=true;col.size=new Vector3(14,8,19.8f);
        var lesson=go.AddComponent<TutorialTrigger>();var so=new SerializedObject(lesson);so.FindProperty("lesson").enumValueIndex=(int)kind;so.FindProperty("lessonTitle").stringValue=title;so.FindProperty("message").stringValue=message;so.FindProperty("prerequisite").objectReferenceValue=previous;so.FindProperty("exitGate").objectReferenceValue=gate;
        var ts=so.FindProperty("targets");ts.arraySize=targets.Length;for(int i=0;i<targets.Length;i++)ts.GetArrayElementAtIndex(i).objectReferenceValue=targets[i];
        if(pad.HasValue){var landing=new GameObject("LandingTarget");landing.transform.SetParent(root);landing.transform.position=pad.Value;so.FindProperty("landing").objectReferenceValue=landing.transform;Slab(root,"CopperLanding",pad.Value+Vector3.up*0.025f,new Vector3(3,0.05f,2),mat);}
        so.ApplyModifiedPropertiesWithoutUndo();return lesson;
    }
    static void Set(Object obj,string field,object value)
    {
        var so=new SerializedObject(obj);var p=so.FindProperty(field);if(p==null)throw new System.InvalidOperationException(obj.name+" missing "+field);
        if(value is bool b)p.boolValue=b;else if(value is int i)p.intValue=i;else if(value is float f)p.floatValue=f;else if(value is string s)p.stringValue=s;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}

// Project-owned presentation over the tutorial's collision and lesson layout.
public static class ProjectRestartFoundryDress
{
    const string Vendor="Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs/";
    static readonly string[] Kit={"Base/SM_Bld_Base_Floor_Combined_01", "Base/SM_Bld_Base_Wall_01", "Building/SM_Bld_Pillar_02", "Building/SM_Bld_Wall_Archway_01", "Building/SM_Bld_Gates_Cemetary_02", "Building/SM_Bld_Trim_02", "Props/SM_Prop_Brazier_01", "Props/SM_Prop_Rack_Weapon_01", "Props/SM_Prop_Bookshelf_01", "Props/SM_Prop_Crate_01", "Props/SM_Prop_Flag_Dark_01"};
    [MenuItem("Tools/Project Restart/Tutorial Art/Audit Dark Fantasy Kit %#k")]
    public static void Audit()
    {
        if(EditorApplication.isPlayingOrWillChangePlaymode)return;
        foreach(var path in Kit) {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Vendor+path+".prefab");
            if(!prefab)throw new System.InvalidOperationException("Missing tutorial kit: "+path);
            var copy=Object.Instantiate(prefab);copy.hideFlags=HideFlags.HideAndDontSave;
            try{Debug.Log("[FoundryKit] "+path+" size="+Bounds(copy).size.ToString("F3"));}
            finally{Object.DestroyImmediate(copy);}
        }
    }
    [MenuItem("Tools/Project Restart/Tutorial Art/Dress Foundry with Dark Fantasy %#j")]
    public static void Dress()
    {
        if(EditorApplication.isPlayingOrWillChangePlaymode)return;
        var scene=SceneManager.GetActiveScene();
        if(scene.path!="Assets/_Project/Scenes/02_FoundryTutorial.unity")throw new System.InvalidOperationException("Open the Foundry tutorial first.");
        var root=scene.GetRootGameObjects().FirstOrDefault(g=>g.name=="FoundryWalk");
        if(!root)throw new System.InvalidOperationException("Foundry collision layout missing.");
        foreach(var path in Kit.Where(p=>!p.Contains("Statue")))
            if(!AssetDatabase.LoadAssetAtPath<GameObject>(Vendor+path+".prefab"))throw new System.InvalidOperationException("Missing kit: "+path);
        Selection.objects=System.Array.Empty<Object>();
        var backup="Assets/_Project/ArtDirection/Foundry_BeforeDungeonDress_"+System.DateTime.Now.ToString("yyyyMMdd_HHmmss")+".unity";
        if(!EditorSceneManager.SaveScene(scene,backup,true))throw new System.InvalidOperationException("Could not back up tutorial.");
        var old=root.transform.Find("DungeonPresentation");if(old)Object.DestroyImmediate(old.gameObject);
        foreach(var gate in root.GetComponentsInChildren<Transform>(true).Where(t=>t.name=="LessonGate").ToArray())
        {var skin=gate.Find("DungeonGate");if(skin)Object.DestroyImmediate(skin.gameObject);}
        var art=new GameObject("DungeonPresentation").transform;art.SetParent(root.transform);
        // Slide currently retains a standing capsule: teach momentum on an open runway.
        var beam=root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name=="SlideBeam");
        if(beam)beam.position=new Vector3(beam.position.x,4f,beam.position.z);
        foreach(var side in root.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("MomentumSide")))
        {side.position=new Vector3(side.position.x,2f,side.position.z);side.localScale=new Vector3(4,4,1);}
        var slideLesson=root.GetComponentsInChildren<TutorialTrigger>(true).FirstOrDefault(t=>t.name.StartsWith("02 /"));
        if(slideLesson){var so=new SerializedObject(slideLesson);so.FindProperty("message").stringValue="Sprint and slide along the copper lane. Double jump toward the ember.";so.ApplyModifiedPropertiesWithoutUndo();}
        var copper=AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/ArtDirection/Materials/FoundryCopper.mat");
        if(copper)for(int n=0;n<3;n++)foreach(float side in new[]{-1f,1f}) {
            var stroke=GameObject.CreatePrimitive(PrimitiveType.Cube);stroke.name="MomentumArrow";stroke.transform.SetParent(art);
            stroke.transform.SetPositionAndRotation(new Vector3(side*.38f,.025f,23+n*2),Quaternion.Euler(0,-side*45,0));
            stroke.transform.localScale=new Vector3(.12f,.025f,1.05f);stroke.GetComponent<Renderer>().sharedMaterial=copper;Object.DestroyImmediate(stroke.GetComponent<Collider>());
        }
        // Leave the functional colliders/markers and lesson references in place.
        foreach(var t in root.GetComponentsInChildren<Transform>(true).ToArray())
        {
            if(t.name=="FloorPanel" || t.name.StartsWith("Floor_") || t.name=="EntryFloor" ||
                t.name.StartsWith("West_") || t.name.StartsWith("East_") || t.name=="PortalPier" ||
                t.name=="PortalHeader" || t.name=="StartWall" || t.name=="JumpSill" || t.name=="SlideBeam" ||
                t.name.StartsWith("MomentumSide") || t.name=="LessonGate" || t.name=="PlungeDeck" ||
                t.name=="DeckLink" || t.name.StartsWith("Stair_") || t.name=="FinalPlungeDeck")
            {var renderer=t.GetComponent<MeshRenderer>();if(renderer)renderer.enabled=false;}
            if(t.name=="MarkedRunWall" || t.name=="FinalMarkedWall")
            {
                var renderer=t.GetComponent<MeshRenderer>();if(renderer)renderer.enabled=false;
                var b=t.GetComponent<Collider>().bounds;
                // Native masonry repeats along the wall; smooth collision remains at the painted face.
                for(int n=0;n<4;n++)Place("Base/SM_Bld_Base_Wall_01",art,new Vector3(b.center.x,b.min.y,b.min.z+1.25f+n*2.5f),Quaternion.Euler(0,90,0),b.size.y/3.006f,new Vector3(.65f,b.size.y,2.5f));
            }
        }
        // Square tiles at native proportions, flush to the unchanged walkable plane.
        for(int x=0;x<4;x++)for(int z=0;z<60;z++)
            Place("Base/SM_Bld_Base_Floor_Combined_01",art,new Vector3(-5.25f+x*3.5f,0.012f,-8.25f+z*3.5f),Quaternion.Euler(0,((x+z)%4)*90,0),1.4f,null,true);
        for(int room=0;room<10;room++)
        {
            float z=room*20+10;
            for(int tile=0;tile<5;tile++)foreach(float side in new[]{-1f,1f})
                Place("Base/SM_Bld_Base_Wall_01",art,new Vector3(side*7.2f,0,room*20+2+tile*4),Quaternion.Euler(0,side<0?90:-90,0),1.6f);
            // Large native archway, repeated as a coherent nave kit. No low ceiling over the camera.
            Place("Building/SM_Bld_Wall_Archway_01",art,new Vector3(0,0,z+9),Quaternion.identity,3f);
            foreach(float side in new[]{-1f,1f})
            {
                Place("Building/SM_Bld_Pillar_02",art,new Vector3(side*6.25f,0,z+8.1f),Quaternion.identity,1.65f);
                Place("Props/SM_Prop_Flag_Dark_01",art,new Vector3(side*6.6f,1.8f,z+5),Quaternion.Euler(0,side<0?90:-90,0),.7f);
                Place("Props/SM_Prop_Brazier_01",art,new Vector3(side*5.65f,0,z+6),Quaternion.identity,1.1f);
                Lamp(art,new Vector3(side*5.65f,1.2f,z+6));
            }
        }
        foreach(var gate in root.GetComponentsInChildren<Transform>(true).Where(t=>t.name=="LessonGate").ToArray())
        {
            var go=Place("Building/SM_Bld_Gates_Cemetary_02",art,new Vector3(gate.position.x,0,gate.position.z),Quaternion.identity,1.82f);
            go.name="DungeonGate";go.transform.SetParent(gate,true);
        }
        // Deliberate edge compositions: weapon practice, archives, then a sparse final courtyard.
        Place("Props/SM_Prop_Rack_Weapon_01",art,new Vector3(-5.6f,0,65),Quaternion.Euler(0,90,0),1.4f);
        Place("Props/SM_Prop_Rack_Weapon_01",art,new Vector3(5.6f,0,106),Quaternion.Euler(0,-90,0),1.4f);
        foreach(float z in new[]{83f,87f,163f})Place("Props/SM_Prop_Bookshelf_01",art,new Vector3(-6.1f,0,z),Quaternion.Euler(0,90,0),1f);
        foreach(float z in new[]{72f,113f,152f}) {
            Place("Props/SM_Prop_Crate_01",art,new Vector3(5.8f,0,z),Quaternion.Euler(0,-12,0),1.4f);
            Place("Props/SM_Prop_Crate_01",art,new Vector3(6.05f,0,z+1.7f),Quaternion.Euler(0,9,0),1f);
        }
        // Authored masonry skins for teaching obstacles; no visual piles over their usable volumes.
        foreach(var t in root.GetComponentsInChildren<Transform>(true).Where(t=>t.name=="JumpSill" || t.name=="SlideBeam" || t.name.StartsWith("MomentumSide") || t.name=="PlungeDeck" || t.name=="DeckLink" || t.name=="FinalPlungeDeck").ToArray())
        {
            var b=t.GetComponent<Collider>().bounds;
            Place("Base/SM_Bld_Base_Floor_Combined_01",art,new Vector3(b.center.x,b.min.y,b.center.z),Quaternion.identity,1,b.size);
        }
        foreach(var t in root.GetComponentsInChildren<Transform>(true).Where(t=>t.name.StartsWith("Stair_")).ToArray())
        {var b=t.GetComponent<Collider>().bounds;Place("Base/SM_Bld_Base_Floor_Combined_01",art,new Vector3(b.center.x,b.min.y,b.center.z),Quaternion.identity,1,b.size);}
        var fall=new GameObject("FallReset");fall.transform.SetParent(art);fall.transform.position=new Vector3(0,-7,95);
        var trigger=fall.AddComponent<BoxCollider>();trigger.isTrigger=true;trigger.size=new Vector3(200,4,260);fall.AddComponent<KillZone>();
        // Gallery mass outside the course gives depth without adding navigation clutter.
        for(int z=0;z<10;z++)foreach(float side in new[]{-1f,1f})
            Place("Building/SM_Bld_Pillar_02",art,new Vector3(side*10.5f,-.3f,z*20+7),Quaternion.identity,2.5f);
        RenderSettings.skybox=null;RenderSettings.fog=true;RenderSettings.fogMode=FogMode.Exponential;
        RenderSettings.fogColor=new Color(.085f,.09f,.13f);RenderSettings.fogDensity=.012f;
        RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor=new Color(.24f,.25f,.31f);RenderSettings.ambientEquatorColor=new Color(.13f,.12f,.16f);RenderSettings.ambientGroundColor=new Color(.06f,.05f,.065f);
        foreach(var light in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Light>(true)).Where(l=>l.type==LightType.Directional))
        {light.intensity=.85f;light.color=new Color(.82f,.86f,1f);}
        foreach(var camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Where(c=>c.CompareTag("MainCamera")))camera.backgroundColor=RenderSettings.fogColor;
        ProjectRestartMechanicalEnvironment.Apply();
        Validate(root);
        EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);AssetDatabase.SaveAssets();
        Debug.Log("[FoundryDress] Dark Fantasy nave, weapon/archives pockets and courtyard saved. Native-proportion kit; decorations have no colliders; original teaching collision retained. Backup: "+backup);
    }
    static GameObject Place(string path,Transform parent,Vector3 bottom,Quaternion rotation,float scale,Vector3? fit=null,bool topAligned=false)
    {
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Vendor+path+".prefab");
        var go=(GameObject)PrefabUtility.InstantiatePrefab(prefab,parent);go.transform.SetPositionAndRotation(Vector3.zero,rotation);go.transform.localScale=Vector3.one;
        var b=Bounds(go);
        if(b.size.sqrMagnitude<.001f)throw new System.InvalidOperationException("Empty kit geometry: "+path);
        if(fit.HasValue) {
            // Fitted instances use axis-aligned quarter turns; map world extents back into local axes.
            var wanted=Quaternion.Inverse(rotation)*fit.Value;var size=Quaternion.Inverse(rotation)*b.size;
            go.transform.localScale=new Vector3(Mathf.Abs(wanted.x)/Mathf.Max(Mathf.Abs(size.x),.01f),Mathf.Abs(wanted.y)/Mathf.Max(Mathf.Abs(size.y),.01f),Mathf.Abs(wanted.z)/Mathf.Max(Mathf.Abs(size.z),.01f));
        } else go.transform.localScale=Vector3.one*scale;
        b=Bounds(go);go.transform.position=bottom-new Vector3(b.center.x,topAligned?b.max.y:b.min.y,b.center.z);
        foreach(var c in go.GetComponentsInChildren<Collider>(true))c.enabled=false;
        foreach(var l in go.GetComponentsInChildren<Light>(true))l.enabled=false;
        foreach(var p in go.GetComponentsInChildren<ParticleSystem>(true)){p.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);p.gameObject.SetActive(false);}
        go.isStatic=true;
        return go;
    }
    static void Lamp(Transform parent,Vector3 position)
    {
        var go=new GameObject("WarmBrazierLight");go.transform.SetParent(parent);go.transform.position=position;
        var light=go.AddComponent<Light>();light.type=LightType.Point;light.color=new Color(1,.43f,.16f);light.range=6;light.intensity=2.2f;light.shadows=LightShadows.None;
    }
    static void Validate(GameObject root)
    {
        var lessons=root.GetComponentsInChildren<TutorialTrigger>(true);if(lessons.Length!=10)throw new System.InvalidOperationException("Expected ten lessons.");
        foreach(var lesson in lessons){var so=new SerializedObject(lesson);if(!so.FindProperty("exitGate").objectReferenceValue)throw new System.InvalidOperationException("Missing gate: "+lesson.name);}
        var count=root.GetComponentsInChildren<WallRunSurface>(true).Length;if(count<3)throw new System.InvalidOperationException("Wall-run markers lost.");
        Debug.Log("[FoundryDressCheck] PASS: ten lesson references and gates, three wall-run surfaces; decoration colliders disabled. Live traversal still requires review.");
    }
    [MenuItem("Tools/Project Restart/Tutorial Art/Capture Ten Room Views %#u")]
    public static void CaptureViews()
    {
        if(EditorApplication.isPlayingOrWillChangePlaymode)return;
        if(SceneManager.GetActiveScene().path!="Assets/_Project/Scenes/02_FoundryTutorial.unity")return;
        var dir="Tools/TutorialReview";System.IO.Directory.CreateDirectory(dir);
        var host=new GameObject("Temporary Foundry Review Camera");host.hideFlags=HideFlags.HideAndDontSave;
        var camera=host.AddComponent<Camera>();camera.enabled=false;camera.fieldOfView=60;
        camera.backgroundColor=RenderSettings.fogColor;camera.clearFlags=CameraClearFlags.SolidColor;
        camera.cullingMask=~(1<<5);camera.farClipPlane=90;
        var rt=new RenderTexture(640,360,24);camera.targetTexture=rt;
        var image=new Texture2D(640,360,TextureFormat.RGB24,false);
        var sheet=new Texture2D(1920,1440,TextureFormat.RGB24,false);
        var prior=RenderTexture.active;
        try {
            for(int i=0;i<10;i++) {
                float z=i*20+10;
                camera.transform.position=new Vector3(5.7f,6,z-7);
                camera.transform.LookAt(new Vector3(0,1.5f,z+3));camera.Render();
                RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,640,360),0,0);image.Apply();
                System.IO.File.WriteAllBytes(dir+"/Room_"+(i+1).ToString("00")+".png",image.EncodeToPNG());
                sheet.SetPixels((i%3)*640,(3-i/3)*360,640,360,image.GetPixels());
            }
            sheet.Apply();System.IO.File.WriteAllBytes(dir+"/CourseViews.png",sheet.EncodeToPNG());
        } finally {RenderTexture.active=prior;camera.targetTexture=null;Object.DestroyImmediate(rt);Object.DestroyImmediate(image);Object.DestroyImmediate(sheet);Object.DestroyImmediate(host);}
        Debug.Log("[FoundryReview] Ten world-only room views captured; course unchanged. These are visual checks, not completion tests.");
    }
    static Bounds Bounds(GameObject go)
    {
        var renderers=go.GetComponentsInChildren<Renderer>().Where(r=>r is MeshRenderer).ToArray();
        if(renderers.Length==0)throw new System.InvalidOperationException("No visible geometry: "+go.name);
        var b=renderers[0].bounds;foreach(var r in renderers.Skip(1))b.Encapsulate(r.bounds);return b;
    }
}
