using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Setup Skeleton Enemies + Nav Grid.
/// Rebuilds Assets/_Project/Animations/EnemyBase.controller (Grruzam humanoid
/// clips — they retarget onto the Synty skeleton rig), swaps the capsule
/// dummies' visuals for PolygonDungeon skeletons (capsule collider stays — it
/// is the physics body), adds a PathGrid sized to the arena, and drops
/// FootGrounding on the player hero. Re-runnable: the visual child and
/// controller are rebuilt in place.
/// </summary>
public static class ProjectRestartEnemies
{
    private const string ScenePath = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const string ControllerPath = "Assets/_Project/Animations/EnemyBase.controller";
    private const string SkeletonPrefabPath =
        "Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs/Characters/SM_Chr_Skeleton_HeavyArmor_01.prefab";
    private const string WeaponPrefabPath =
        "Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs/Weapons/SM_Wep_Sword_01.prefab";
    private const string ShieldPrefabPath =
        "Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs/Weapons/SM_Wep_Shield_01.prefab";
    private const string AnimRoot = "Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_Big_Sword/";
    private const string IdleClipPath = AnimRoot + "1_Movements/1__Idle/M_Big_Sword@Idle.FBX";
    private const string AttackClipPath = AnimRoot + "2_Attacks/1__4Combos/M_Big_Sword@Attack_4Combo_1A_Inplace.FBX";
    private const string JogRoot = AnimRoot + "1_Movements/3__Jogging/A/M_Big_Sword@Jogging_8Way_verA_";
    private const string HitClipPath = AnimRoot + "4_Damages/1__Front/M_Big_Sword@Damage_Front_Small_ver_A.FBX";
    private const string DieClipPath = AnimRoot + "4_Damages/6__Die/M_Big_Sword@Damage_Die.FBX";
    private const string HitBackClipPath = AnimRoot + "4_Damages/2__Back/M_Big_Sword@Damage_Back_Small_ver_A.FBX";
    // SoulslikeEssential backstab pair — Synty humanoid, own avatar.
    private const string StabbedClipPath = "Assets/ThirdParty/SoulslikeEssential/Backstab_Stabbed.FBX";
    private const string StabbedDeathClipPath = "Assets/ThirdParty/SoulslikeEssential/Backstab_Death.FBX";
    private const float MaxGridExtent = 50f;

    [MenuItem("Tools/Project Restart/Setup Skeleton Enemies + Nav Grid")]
    public static void Setup() => SetupIn(ScenePath);

    /// <summary>Same pipeline pointed at another scene (the courtyard reuses
    /// dressing + nav grid wholesale).</summary>
    internal static void SetupIn(string scenePath)
    {
        var controller = BuildController();
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != scenePath)
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

        // Bare Targetable capsules (freshly spawned test dummies) carry no
        // combat components yet — promote them here so the dress pass isn't
        // gated on Setup Fast Crowd Combat having run first. Bosses keep
        // their own brain — never EnemyAI.
        foreach (var t in Object.FindObjectsByType<Targetable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t.GetComponent<IBossEngage>() != null) continue;
            if (t.GetComponent<Health>() == null) t.gameObject.AddComponent<Health>();
            if (t.GetComponent<EnemyAI>() == null) t.gameObject.AddComponent<EnemyAI>();
        }
        // Include inactive: dead-or-disabled dummies are still setup targets —
        // wake them so the dressed skeletons actually fight in Play.
        var enemies = Object.FindObjectsByType<EnemyAI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Require(enemies.Length > 0, "No EnemyAI objects in " + scenePath);
        foreach (var e in enemies)
            if (!e.gameObject.activeSelf)
            {
                e.gameObject.SetActive(true);
                Debug.Log($"[ProjectRestart] Reactivated inactive enemy '{e.name}'.", e);
            }
        var variants = new[] { "SM_Chr_Skeleton_HeavyArmor_01", "SM_Chr_Skeleton_LightArmor_01", "SM_Chr_Skeleton_Ranger_01" };
        var skeletonPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SkeletonPrefabPath);
        Require(skeletonPrefab != null, "Skeleton prefab missing: " + SkeletonPrefabPath);

        for (var i = 0; i < enemies.Length; i++)
            // Enemy 0 is the named elite (world-space nameplate + HP bar);
            // the rest stay trash mobs — the three-tier UI convention.
            DressEnemy(enemies[i], skeletonPrefab, controller, variants[i % variants.Length],
                       i == 0 ? "Grave Warden" : null);

        EnsureNavGrid(scene);
        EnsureFootGrounding();
        Require(EditorSceneManager.SaveScene(scene), "Could not save " + scenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[ProjectRestart] Skeleton enemies done — {enemies.Length} enemies dressed, " +
                  "EnemyBase.controller + NavGrid ready. Play: enemies aggro only inside their " +
                  "frontal sight cone, path around walls via A*, swing/stagger/die with clips.");
    }

    private static AnimatorController BuildController()
    {
        const string folder = "Assets/_Project/Animations";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/_Project", "Animations");
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)
                         ?? AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.parameters = System.Array.Empty<AnimatorControllerParameter>();
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("MoveX", AnimatorControllerParameterType.Float);
        controller.AddParameter("MoveY", AnimatorControllerParameterType.Float);
        controller.AddParameter("Attack", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Hit", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("Dead", AnimatorControllerParameterType.Bool);
        controller.AddParameter("StabDeath", AnimatorControllerParameterType.Bool);

        var sm = controller.layers[0].stateMachine;
        // Grruzam clips get the root-Y bake — without it their vertical motion
        // (the death fall!) is eaten as root motion and corpses float at hip
        // height. The Synty SoulslikeEssential clips keep their own avatar and
        // are left untouched — copying the Grruzam avatar onto them breaks them.
        var idle = EnsureState(sm, "Idle", Bake(LoadClip(IdleClipPath, "Idle")), new Vector3(-300, 0));
        var move = EnsureState(sm, "Move", BuildMoveTree(controller), new Vector3(-300, 140));
        var attack = EnsureState(sm, "Attack", Bake(LoadClip(AttackClipPath, "Attack_3Combo_1_Inplace")), new Vector3(0, 0));
        var hit = EnsureState(sm, "Hit", Bake(LoadClip(HitClipPath, "Damage_Front_Small_ver_A")), new Vector3(0, 140));
        var hitBack = EnsureState(sm, "HitBack", Bake(LoadClip(HitBackClipPath, "Damage_Back_Small_ver_A")), new Vector3(0, 280));
        var stabbed = EnsureState(sm, "Stabbed", LoadClip(StabbedClipPath, "Backstab_Stabbed"), new Vector3(300, 220));
        var die = EnsureState(sm, "Die", Bake(LoadClip(DieClipPath, "Damage_Die")), new Vector3(300, 60));
        var stabDeath = EnsureState(sm, "StabbedDeath", LoadClip(StabbedDeathClipPath, "Backstab_Death"), new Vector3(560, 140));
        sm.defaultState = idle;

        foreach (var st in new[] { idle, move, attack, hit, hitBack, stabbed, die, stabDeath })
            st.transitions = System.Array.Empty<AnimatorStateTransition>();
        sm.anyStateTransitions = System.Array.Empty<AnimatorStateTransition>();

        Trans(idle, move, AnimatorConditionMode.Greater, 0.1f, "Speed", 0.15f);
        Trans(move, idle, AnimatorConditionMode.Less, 0.1f, "Speed", 0.25f);
        Trans(idle, attack, AnimatorConditionMode.If, 0f, "Attack", 0.14f);
        Trans(move, attack, AnimatorConditionMode.If, 0f, "Attack", 0.14f);
        ExitTrans(attack, move, 0.88f, 0.22f);
        Trans(idle, hit, AnimatorConditionMode.If, 0f, "Hit", 0.08f);
        Trans(move, hit, AnimatorConditionMode.If, 0f, "Hit", 0.08f);
        Trans(attack, hit, AnimatorConditionMode.If, 0f, "Hit", 0.08f);
        ExitTrans(hit, move, 0.82f, 0.22f);
        // HitBack/Stabbed are CrossFade-entered by EnemyAI (every hit restarts
        // the flinch); they only need exits.
        ExitTrans(hitBack, move, 0.82f, 0.22f);
        ExitTrans(stabbed, move, 0.95f, 0.2f);
        // Death from anywhere; the corpse pose IS the loop. A killing backstab
        // (StabDeath) routes to the paired stab death instead of the generic fall.
        var anyToDie = sm.AddAnyStateTransition(die);
        anyToDie.AddCondition(AnimatorConditionMode.If, 0f, "Dead");
        anyToDie.AddCondition(AnimatorConditionMode.IfNot, 0f, "StabDeath");
        anyToDie.duration = 0.1f;
        anyToDie.canTransitionToSelf = false;
        var anyToStab = sm.AddAnyStateTransition(stabDeath);
        anyToStab.AddCondition(AnimatorConditionMode.If, 0f, "Dead");
        anyToStab.AddCondition(AnimatorConditionMode.If, 0f, "StabDeath");
        anyToStab.duration = 0.08f;
        anyToStab.canTransitionToSelf = false;

        // FootGrounding's OnAnimatorIK needs the layer's IK pass.
        ProjectRestartCombat.SetIkPass(controller);

        EditorUtility.SetDirty(controller);
        return controller;
    }

    /// <summary>8-way directional jog — MoveX/MoveY pick the clip matching the
    /// enemy-local travel direction so strafing plays a side jog, not a
    /// forward jog sliding sideways.</summary>
    private static BlendTree BuildMoveTree(AnimatorController controller)
    {
        var old = AssetDatabase.LoadAllAssetsAtPath(ControllerPath);
        foreach (var a in old)
            if (a is BlendTree bt && bt.name == "Move") AssetDatabase.RemoveObjectFromAsset(bt);

        var tree = new BlendTree
        {
            name = "Move",
            blendType = BlendTreeType.FreeformDirectional2D,
            blendParameter = "MoveX",
            blendParameterY = "MoveY",
            useAutomaticThresholds = false
        };
        tree.AddChild(Bake(LoadClip(JogRoot + "F.FBX", "Jogging_8Way_verA_F")), new Vector2(0f, 1f));
        tree.AddChild(Bake(LoadClip(JogRoot + "FR45.FBX", "Jogging_8Way_verA_FR45")), new Vector2(0.707f, 0.707f));
        tree.AddChild(Bake(LoadClip(JogRoot + "R90.FBX", "Jogging_8Way_verA_R90")), new Vector2(1f, 0f));
        tree.AddChild(Bake(LoadClip(JogRoot + "BR45.FBX", "Jogging_8Way_verA_BR45")), new Vector2(0.707f, -0.707f));
        tree.AddChild(Bake(LoadClip(JogRoot + "B.FBX", "Jogging_8Way_verA_B")), new Vector2(0f, -1f));
        tree.AddChild(Bake(LoadClip(JogRoot + "BL45.FBX", "Jogging_8Way_verA_BL45")), new Vector2(-0.707f, -0.707f));
        tree.AddChild(Bake(LoadClip(JogRoot + "L90.FBX", "Jogging_8Way_verA_L90")), new Vector2(-1f, 0f));
        tree.AddChild(Bake(LoadClip(JogRoot + "FL45.FBX", "Jogging_8Way_verA_FL45")), new Vector2(-0.707f, 0.707f));
        AssetDatabase.AddObjectToAsset(tree, controller);
        return tree;
    }

    private static void DressEnemy(EnemyAI enemy, GameObject prefab, AnimatorController controller, string variant, string eliteName = null)
    {
        if (HuskVisualSetup.Dress(enemy, controller)) return;
        // The capsule renderer is the placeholder body — hide it; the collider stays.
        var capsuleRend = enemy.GetComponent<MeshRenderer>();
        if (capsuleRend != null) capsuleRend.enabled = false;

        var old = enemy.transform.Find("SkeletonVisual");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        // Reparenting the weapon items onto hand bones needs plain GameObjects —
        // unpack so the instance hierarchy is freely editable.
        PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        visual.name = "SkeletonVisual";
        visual.transform.SetParent(enemy.transform, false);
        // The dummies are capsule primitives — pivot at the capsule CENTER, not
        // the feet. The skeleton's pivot is its soles, so drop it to the
        // capsule bottom or it floats half a capsule (1m) above the floor.
        visual.transform.localPosition = new Vector3(0f, CapsuleBottomLocalY(enemy), 0f);
        visual.transform.localRotation = Quaternion.identity;

        // Bundle prefab: keep only the chosen character mesh; drop the rest.
        var kept = false;
        foreach (var smr in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var isVariant = smr.gameObject.name == variant;
            smr.gameObject.SetActive(isVariant);
            kept |= isVariant;
        }
        if (!kept)
            Debug.LogWarning($"[ProjectRestart] '{variant}' mesh not in prefab bundle — first SMR left active.", enemy);

        // A missing or legacy-shader material renders MAGENTA under URP — a
        // purple skeleton is a broken material slot, not a palette pick. Repair
        // any broken slot on the kept variant with a working sibling material.
        Material fallback = null;
        foreach (var smr in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!smr.gameObject.activeSelf) continue;
            foreach (var m in smr.sharedMaterials)
                if (m != null && m.shader != null && m.shader.isSupported) { fallback = m; break; }
            if (fallback != null) break;
        }
        if (fallback == null) fallback = EnemyWeaponMat();
        foreach (var smr in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!smr.gameObject.activeSelf) continue;
            var mats = smr.sharedMaterials;
            var repaired = false;
            for (var i = 0; i < mats.Length; i++)
                if (mats[i] == null || mats[i].shader == null || !mats[i].shader.isSupported)
                { mats[i] = fallback; repaired = true; }
            if (repaired)
            {
                smr.sharedMaterials = mats;
                Debug.LogWarning($"[ProjectRestart] {enemy.name}: repaired a missing/unsupported material on '{smr.name}' (it would render purple).", enemy);
            }
        }

        var animator = visual.GetComponentInChildren<Animator>(true);
        Require(animator != null, "Skeleton prefab has no Animator.");
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        // Arm it: standalone weapon prefabs socketed to the hand bones. Look the
        // bones up BY NAME — the bundle's humanoid avatar maps RightHand to the
        // wrong bone, which put the sword in the left hand.
        var handR = FindDeep(visual.transform, "Hand_R") ?? animator.GetBoneTransform(HumanBodyBones.RightHand);
        var handL = FindDeep(visual.transform, "Hand_L") ?? animator.GetBoneTransform(HumanBodyBones.LeftHand);
        ArmItem(visual.transform, WeaponPrefabPath, handR, "EnemyWeapon");
        ArmItem(visual.transform, ShieldPrefabPath, handL, "EnemyShield");

        // Grruzam clips put hips at Grruzam-rig height — on the shorter Synty
        // skeleton the feet dangle above the floor. FootGrounding goes on the
        // ANIMATOR's GameObject so its OnAnimatorIK fires; it self-resolves the
        // enemy's CC on the parent.
        var fg = animator.gameObject.AddComponent<FootGrounding>();
        var fso = new SerializedObject(fg);
        var mc = fso.FindProperty("pelvisOffsetMax");
        if (mc != null) mc.floatValue = 0.8f; // measured retarget gap is ~0.5
        var ac = fso.FindProperty("attackPelvisOffsetMax");
        if (ac != null) ac.floatValue = 0.9f; // attack/death poses displace further
        fso.ApplyModifiedPropertiesWithoutUndo();

        var so = new SerializedObject(enemy);
        var prop = so.FindProperty("enemyAnimator");
        if (prop != null) prop.objectReferenceValue = animator;
        var eliteProp = so.FindProperty("eliteName");
        // An explicitly named elite keeps its nameplate — callers that place a
        // named elite (the courtyard's Warden) must not have it stripped by the
        // index-0 designation above.
        if (eliteProp != null) eliteProp.stringValue = eliteName ?? eliteProp.stringValue ?? "";
        so.ApplyModifiedPropertiesWithoutUndo();
        // Elites stand out on stats too — deeper pool, bigger payout. A
        // scene-placed elite (non-empty eliteName) counts even when the caller
        // designation is null.
        var isElite = eliteName != null
                      || !string.IsNullOrEmpty(so.FindProperty("eliteName")?.stringValue);
        var health = enemy.GetComponent<Health>();
        if (health != null)
        {
            var hso = new SerializedObject(health);
            var hp = hso.FindProperty("maxHealth");
            if (hp != null) hp.floatValue = isElite ? 440f : 200f;
            var souls = hso.FindProperty("soulsReward");
            if (souls != null) souls.intValue = isElite ? 120 : 25;
            var mana = hso.FindProperty("manaReward");
            if (mana != null) mana.intValue = isElite ? 12 : 6;
            hso.ApplyModifiedPropertiesWithoutUndo();
        }
        EditorUtility.SetDirty(enemy.gameObject);
    }

    private static float CapsuleBottomLocalY(EnemyAI enemy)
    {
        var cc = enemy.GetComponent<CharacterController>();
        if (cc != null) return cc.center.y - cc.height * 0.5f;
        var cap = enemy.GetComponent<CapsuleCollider>();
        return cap != null ? cap.center.y - cap.height * 0.5f : 0f;
    }

    /// <summary>Grruzam clip → shared-avatar + feet-baked root Y (reimports the
    /// FBX once, no-ops when already correct).</summary>
    private static AnimationClip Bake(AnimationClip clip) => ProjectRestartCombat.EnsureClipYBake(clip);

    private static void ArmItem(Transform root, string prefabPath, Transform hand, string name)
    {
        if (hand == null) return;
        var old = FindDeep(root, name);
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) { Debug.LogWarning("[ProjectRestart] Weapon prefab missing: " + prefabPath); return; }
        var item = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        item.name = name;
        item.transform.SetParent(hand, false);
        item.transform.localPosition = Vector3.zero;
        item.transform.localRotation = Quaternion.identity;
        // Enemy sword+shield read as plain grey mesh — no vendor texture;
        // also covers a vendor material that fails under URP (magenta).
        if (name == "EnemyWeapon" || name == "EnemyShield")
            foreach (var r in item.GetComponentsInChildren<Renderer>(true))
                r.sharedMaterial = EnemyWeaponMat();
    }

    /// <summary>Shared flat-grey URP Lit for enemy swords — created once so all
    /// enemies reference one asset instead of per-instance materials.</summary>
    private static Material EnemyWeaponMat()
    {
        const string path = "Assets/_Project/Materials/EnemyWeaponGrey.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Materials"))
                AssetDatabase.CreateFolder("Assets/_Project", "Materials");
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor", new Color(0.5f, 0.5f, 0.53f));
            AssetDatabase.CreateAsset(m, path);
        }
        return m;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (var i = 0; i < root.childCount; i++)
        {
            var f = FindDeep(root.GetChild(i), name);
            if (f != null) return f;
        }
        return null;
    }

    private static void EnsureNavGrid(UnityEngine.SceneManagement.Scene scene)
    {
        var grid = Object.FindFirstObjectByType<PathGrid>();
        if (grid == null)
        {
            var go = new GameObject("NavGrid");
            UnityEditor.SceneManagement.EditorSceneManager.MoveGameObjectToScene(go, scene);
            grid = go.AddComponent<PathGrid>();
        }
        var ground = GameObject.Find("Test Ground");
        var center = Vector3.zero;
        var size = new Vector2(MaxGridExtent, MaxGridExtent);
        if (ground != null)
        {
            var col = ground.GetComponent<Collider>();
            if (col != null)
            {
                center = col.bounds.center;
                size = new Vector2(Mathf.Min(col.bounds.size.x, MaxGridExtent),
                                   Mathf.Min(col.bounds.size.z, MaxGridExtent));
            }
        }
        var so = new SerializedObject(grid);
        so.FindProperty("center").vector3Value = new Vector3(center.x, 0f, center.z);
        so.FindProperty("size").vector2Value = size;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(grid.gameObject);
    }

    private static void EnsureFootGrounding()
    {
        var loco = Object.FindFirstObjectByType<PlayerLocomotion>();
        if (loco == null) { Debug.LogWarning("[ProjectRestart] No player found — FootGrounding skipped."); return; }
        Animator anim = null;
        foreach (var a in loco.GetComponentsInChildren<Animator>(true))
            if (a.avatar != null && a.isHuman) { anim = a; break; }
        if (anim == null) { Debug.LogWarning("[ProjectRestart] Player has no humanoid Animator — FootGrounding skipped."); return; }
        // Search the whole player hierarchy — an existing FootGrounding may sit on a
        // different hero child (added by an earlier buggy run); only checking the
        // Animator's own GameObject would add a second one that fights the first.
        if (loco.GetComponentInChildren<FootGrounding>(true) == null)
        {
            anim.gameObject.AddComponent<FootGrounding>();
            EditorUtility.SetDirty(anim.gameObject);
        }
    }

    private static AnimatorState EnsureState(AnimatorStateMachine sm, string name, Motion clip, Vector3 pos)
    {
        foreach (var s in sm.states)
            if (s.state.name == name) { s.state.motion = clip; return s.state; }
        var st = sm.AddState(name, pos);
        st.motion = clip;
        st.writeDefaultValues = false;
        return st;
    }

    private static void Trans(AnimatorState from, AnimatorState to, AnimatorConditionMode mode,
                              float threshold, string param, float duration)
    {
        var t = from.AddTransition(to);
        t.AddCondition(mode, threshold, param);
        t.hasExitTime = false;
        t.duration = duration;
    }

    private static void ExitTrans(AnimatorState from, AnimatorState to, float exitTime, float duration)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = true;
        t.exitTime = exitTime;
        t.duration = duration;
    }

    private static AnimationClip LoadClip(string path, string clipName)
    {
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
            if (a is AnimationClip c && c.name == clipName) return c;
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
            if (a is AnimationClip c && !c.name.StartsWith("__")) return c;
        Debug.LogWarning("[ProjectRestart] Clip not found in " + path);
        return null;
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new System.Exception("[ProjectRestart] " + message);
    }
}


// Manual, project-owned visual replacement. Source enemy gameplay and vendor assets stay intact.
public static class HuskVisualSetup
{
 const string F="Assets/_Project/EnemyPolish/";
 const string O=@"C:\Users\milkw\Documents\Codex\2026-10-04\open-my-blender-character-project-and\outputs\Enemies";
 [System.Serializable] class Topology { public string[] edges=System.Array.Empty<string>(),points=System.Array.Empty<string>(); }
 static string Key(Vector2 u)=>Mathf.RoundToInt(u.x*10000)+","+Mathf.RoundToInt(u.y*10000);
 static string Edge(string a,string b)=>string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;
 [MenuItem("Tools/Enemy Polish/1 Build painted humanoid husk")]
 public static void Build()
 {
  if(EditorApplication.isPlaying)throw new System.InvalidOperationException("Exit Play Mode first.");
  var importer=(ModelImporter)AssetImporter.GetAtPath(F+"Husk_Unity.fbx");
  importer.isReadable=true;importer.importAnimation=false;importer.importCameras=false;importer.importLights=false;
  importer.materialImportMode=ModelImporterMaterialImportMode.None;importer.meshCompression=ModelImporterMeshCompression.Off;
  importer.animationType=ModelImporterAnimationType.Human;importer.avatarSetup=ModelImporterAvatarSetup.CreateFromThisModel;importer.globalScale=2;
  importer.SaveAndReimport();
  var model=AssetDatabase.LoadAssetAtPath<GameObject>(F+"Husk_Unity.fbx");
  var transforms=model.GetComponentsInChildren<Transform>(true);
  var human=new System.Collections.Generic.List<HumanBone>();
  foreach(var name in HumanTrait.BoneName)
  {
   var t=System.Array.Find(transforms,t=>t.name==name.Replace(" ",""));
   if(t)human.Add(new HumanBone{boneName=t.name,humanName=name,limit=new HumanLimit{useDefaultValues=true}});
  }
  var desc=importer.humanDescription;desc.human=human.ToArray();
  desc.skeleton=System.Array.ConvertAll(transforms,t=>new SkeletonBone{name=t.name,position=t.localPosition,rotation=t.localRotation,scale=t.localScale});
  desc.upperArmTwist=.5f;desc.lowerArmTwist=.5f;desc.upperLegTwist=.5f;desc.lowerLegTwist=.5f;desc.armStretch=.05f;desc.legStretch=.05f;
  importer.humanDescription=desc;importer.SaveAndReimport();
  model=AssetDatabase.LoadAssetAtPath<GameObject>(F+"Husk_Unity.fbx");
  var avatar=model.GetComponent<Animator>()?.avatar;
  if(!avatar||!avatar.isValid||!avatar.isHuman)throw new System.InvalidOperationException("Husk humanoid mapping is invalid.");
  foreach(string suffix in new[]{"_BaseColor.png","_EmissionMask.png"})
  {
   var ti=(TextureImporter)AssetImporter.GetAtPath(F+"Husk"+suffix);ti.sRGBTexture=suffix.StartsWith("_BaseColor");
   ti.textureType=TextureImporterType.Default;ti.mipmapEnabled=true;ti.wrapMode=TextureWrapMode.Clamp;ti.maxTextureSize=2048;ti.textureCompression=TextureImporterCompression.CompressedHQ;ti.SaveAndReimport();
  }
  var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/WeaponPolish/Shaders/WeaponStylizedWire.shader");
  if(!shader||ShaderUtil.ShaderHasError(shader))throw new System.InvalidOperationException("Wire shader unavailable.");
  var mat=AssetDatabase.LoadAssetAtPath<Material>(F+"M_Husk_StylizedWire.mat");
  if(!mat){mat=new Material(shader);AssetDatabase.CreateAsset(mat,F+"M_Husk_StylizedWire.mat");}
  mat.SetTexture("_BaseMap",AssetDatabase.LoadAssetAtPath<Texture2D>(F+"Husk_BaseColor.png"));mat.SetTexture("_EmissionMask",AssetDatabase.LoadAssetAtPath<Texture2D>(F+"Husk_EmissionMask.png"));
  mat.SetColor("_BaseColor",Color.white);mat.SetColor("_WireTint",Color.black);mat.SetColor("_EmissionColor",new Color(1,.015f,.006f));
  mat.SetFloat("_WireStrength",1);mat.SetFloat("_WireThickness",.5f);mat.SetFloat("_FaceWireStrength",1);mat.SetFloat("_EmissionStrength",1.25f);mat.SetFloat("_ShadowStrength",.25f);EditorUtility.SetDirty(mat);
  var instance=Object.Instantiate(model);instance.name="HuskVisual";
  var smr=instance.GetComponentInChildren<SkinnedMeshRenderer>();var src=smr.sharedMesh;var uv=src.uv;var verts=src.vertices;var normals=src.normals;var weights=src.boneWeights;var tris=src.triangles;
  var topology=JsonUtility.FromJson<Topology>(AssetDatabase.LoadAssetAtPath<TextAsset>(F+"Husk_Topology.json").text);
  var points=new System.Collections.Generic.HashSet<string>(topology.points);var edges=new System.Collections.Generic.HashSet<string>(topology.edges);
  int matches=0;foreach(var u in uv)if(points.Contains(Key(u)))matches++;
  if(matches<uv.Length*.997f)throw new System.InvalidOperationException("UV metadata mismatch "+matches+"/"+uv.Length);
  var vp=new Vector3[tris.Length];var np=new Vector3[tris.Length];var up=new Vector2[tris.Length];var wp=new BoneWeight[tris.Length];var col=new Color[tris.Length];var ids=new int[tris.Length];int hidden=0;
  for(int i=0;i<tris.Length;i+=3)
  {
   var keys=new[]{Key(uv[tris[i]]),Key(uv[tris[i+1]]),Key(uv[tris[i+2]])};int bits=0;
   if(edges.Contains(Edge(keys[1],keys[2])))bits|=1;else hidden++;
   if(edges.Contains(Edge(keys[2],keys[0])))bits|=2;else hidden++;
   if(edges.Contains(Edge(keys[0],keys[1])))bits|=4;else hidden++;
   for(int c=0;c<3;c++){int id=tris[i+c];vp[i+c]=verts[id];np[i+c]=normals[id];up[i+c]=uv[id];wp[i+c]=weights[id];col[i+c]=new Color(c==0?1:0,c==1?1:0,bits,1);ids[i+c]=i+c;}
  }
  var wire=new Mesh{name="Husk original polygon wire (derived)",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
  wire.vertices=vp;wire.normals=np;wire.uv=up;wire.boneWeights=wp;wire.bindposes=src.bindposes;wire.colors=col;wire.triangles=ids;wire.bounds=src.bounds;
  var old=AssetDatabase.LoadAssetAtPath<Mesh>(F+"Husk_Wire.asset");
  wire=MeshAssetWriter.Write(wire,F+"Husk_Wire.asset");
  smr.sharedMesh=wire;smr.sharedMaterial=mat;smr.updateWhenOffscreen=false;smr.localBounds=src.bounds;
  var animator=instance.GetComponent<Animator>();animator.runtimeAnimatorController=AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/_Project/Animations/EnemyBase.controller");animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
  var fg=instance.AddComponent<FootGrounding>();var fso=new SerializedObject(fg);fso.FindProperty("pelvisOffsetMax").floatValue=.8f;fso.FindProperty("attackPelvisOffsetMax").floatValue=.9f;fso.ApplyModifiedPropertiesWithoutUndo();
  PrefabUtility.SaveAsPrefabAsset(instance,F+"HuskVisual.prefab");Object.DestroyImmediate(instance);AssetDatabase.SaveAssets();
  System.IO.File.WriteAllText(O+"/Unity_Import.txt","Avatar valid="+avatar.isValid+" human="+avatar.isHuman+"\nUV points matched="+matches+"/"+uv.Length+"\nTriangles="+tris.Length/3+" derived vertices="+wire.vertexCount+" hidden triangulation sides="+hidden+"\nSource mesh and UV unchanged. Existing EnemyBase controller retained.");
  Debug.Log("Husk prefab ready: valid humanoid, original polygon wire, red emission.");
 }
 public static bool Dress(EnemyAI enemy,RuntimeAnimatorController controller=null)
 {
  if(enemy.GetComponent<IBossEngage>()!=null)return false;
  var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(F+"HuskVisual.prefab");if(!prefab)return false;
  var existing=enemy.transform.Find("HuskVisual");if(existing)return true;
  var old=enemy.transform.Find("SkeletonVisual");var oldAnimator=old?old.GetComponentInChildren<Animator>(true):null;
  var visual=(GameObject)PrefabUtility.InstantiatePrefab(prefab,enemy.transform);visual.name="HuskVisual";
  var cc=enemy.GetComponent<CharacterController>();var cap=enemy.GetComponent<CapsuleCollider>();
  float bottom=cc?cc.center.y-cc.height*.5f:cap?cap.center.y-cap.height*.5f:0;
  visual.transform.localPosition=new Vector3(0,bottom,0);visual.transform.localRotation=Quaternion.identity;
  var anim=visual.GetComponent<Animator>();anim.runtimeAnimatorController=controller?controller:oldAnimator&&oldAnimator.runtimeAnimatorController?oldAnimator.runtimeAnimatorController:anim.runtimeAnimatorController;
  // Keep existing equipment as separate instances; originals remain in the disabled source visual.
  if(old)
  {
   foreach(string item in new[]{"EnemyWeapon","EnemyShield"})
   {
    Transform found=null;foreach(var t in old.GetComponentsInChildren<Transform>(true))if(t.name==item){found=t;break;}
    if(!found)continue;var hand=anim.GetBoneTransform(item=="EnemyWeapon"?HumanBodyBones.RightHand:HumanBodyBones.LeftHand);
    if(hand){var copy=Object.Instantiate(found.gameObject,hand,false);copy.name=item;copy.transform.localPosition=found.localPosition;copy.transform.localRotation=found.localRotation;copy.transform.localScale=found.localScale;}
   }
   old.gameObject.SetActive(false);
  }
  var capsuleRenderer=enemy.GetComponent<MeshRenderer>();if(capsuleRenderer)capsuleRenderer.enabled=false;
  var so=new SerializedObject(enemy);so.FindProperty("enemyAnimator").objectReferenceValue=anim;so.ApplyModifiedPropertiesWithoutUndo();
  PrefabUtility.RecordPrefabInstancePropertyModifications(enemy);EditorUtility.SetDirty(enemy);
  return true;
 }
 [MenuItem("Tools/Enemy Polish/2 Replace enemies in gameplay scenes")]
 static void ReplaceScenes()
 {
  if(EditorApplication.isPlaying)throw new System.InvalidOperationException("Exit Play Mode first.");
  var current=UnityEngine.SceneManagement.SceneManager.GetActiveScene();string currentPath=current.path;
  if(current.isDirty)throw new System.InvalidOperationException("Save current scene changes before replacing enemies.");
  System.IO.Directory.CreateDirectory(O+"/Before");var report=new System.Collections.Generic.List<string>();Selection.activeObject=null;
  foreach(string name in new[]{"00_TestBlockout","01_Courtyard","02_FoundryTutorial","03_SunkenVault"})
  {
   string path="Assets/_Project/Scenes/"+name+".unity";if(!System.IO.File.Exists(path))continue;
   string backup=O+"/Before/"+name+".unity";if(!System.IO.File.Exists(backup))System.IO.File.Copy(path,backup);
   var scene=path==currentPath?current:EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);int count=0;
   foreach(var root in scene.GetRootGameObjects())foreach(var enemy in root.GetComponentsInChildren<EnemyAI>(true))if(Dress(enemy))count++;
   EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);report.Add(name+": "+count+" husk enemies; original visuals disabled and retained.");
   if(path!=currentPath)EditorSceneManager.CloseScene(scene,true);
  }
  AssetDatabase.SaveAssets();System.IO.File.WriteAllLines(O+"/Unity_Replacement.txt",report);Debug.Log("Husk visuals installed; enemy AI, statistics, colliders, encounter references and bosses preserved.");
 }
 [MenuItem("Tools/Enemy Polish/3 Inspect husk gameplay animation")]
 static void Inspect()
 {
  if(!EditorApplication.isPlaying)throw new System.InvalidOperationException("Enter Play Mode first.");
  var enemies=Object.FindObjectsByType<EnemyAI>(FindObjectsInactive.Exclude,FindObjectsSortMode.None);EnemyAI enemy=null;
  foreach(var e in enemies)if(e.transform.Find("HuskVisual")){enemy=e;break;}
  if(!enemy)throw new System.InvalidOperationException("No active husk enemy.");
  var p=Object.FindFirstObjectByType<PlayerLocomotion>();var cc=p.GetComponent<CharacterController>();cc.enabled=false;p.transform.position=enemy.transform.position+enemy.transform.forward*3;cc.enabled=true;
  var cam=Object.FindFirstObjectByType<PlayerCameraController>();var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
  if(cam)typeof(PlayerCameraController).GetField("yaw",flags)?.SetValue(cam,enemy.transform.eulerAngles.y+180);
  Selection.activeGameObject=enemy.gameObject;
  var a=enemy.transform.Find("HuskVisual").GetComponent<Animator>();var report=new System.Collections.Generic.List<string>();
  report.Add("Live avatar valid="+a.avatar.isValid+" human="+a.isHuman+"; current clip selection and deformation inspected in Game view.");
  report.Add("Enemy controller="+AssetDatabase.GetAssetPath(a.runtimeAnimatorController));
  report.Add("Visible husk renderer="+a.GetComponentInChildren<SkinnedMeshRenderer>().enabled+"; source visual active="+enemy.transform.Find("SkeletonVisual").gameObject.activeSelf);
  System.IO.File.WriteAllLines(O+"/Gameplay_Inspection.txt",report);
 }
}


