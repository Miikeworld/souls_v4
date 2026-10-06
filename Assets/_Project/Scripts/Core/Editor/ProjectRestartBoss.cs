using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Setup FortGolem Boss.
/// Builds Assets/_Project/Animations/BossBase.controller — a Locomotion blend
/// (Speed: baked Idle→Walk) plus CrossFade-driven attack/roar/stagger/die
/// states that BossGolem drives by name. Then dresses the arena: the Rivals
/// FortGolem prefab is unpacked under a physics root (Health + Targetable +
/// CharacterController + BossGolem), its greatsword sockets onto the right
/// hand, and RootMotionRelay + FootGrounding land on the ANIMATOR object so
/// authored attack strides move the capsule and the feet stay planted.
/// A FogGate trigger + blocker seal the approach. Re-runnable in place.
/// </summary>
public static class ProjectRestartBoss
{
    private const string ScenePath = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const string ControllerPath = "Assets/_Project/Animations/BossBase.controller";
    private const string GolemPrefabPath =
        "Assets/ThirdParty/Synty/PolygonFantasyRivals/Prefabs/Characters/Character_BR_FortGolem_01.prefab";
    private const string WeaponPrefabPath =
        "Assets/ThirdParty/Synty/PolygonFantasyRivals/Prefabs/Weapons/SM_Wep_FortGolem_01.prefab";
    private const string FxDir = "Assets/ThirdParty/Synty/PolygonFantasyRivals/Prefabs/FX";

    private const string AnimRoot = "Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_Big_Sword/";
    private const string MkRoot = "Assets/ThirdParty/MagicalKnightSet/Animation/Humanoid/";
    private const string IdlePath = AnimRoot + "1_Movements/1__Idle/M_Big_Sword@Idle.FBX";
    private const string WalkPath = AnimRoot + "1_Movements/2__Walk/A/M_Big_Sword@Walk_ver_A_Front.FBX";
    private const string SwingPath = AnimRoot + "2_Attacks/1__4Combos/M_Big_Sword@Attack_4Combo_2.FBX";
    private const string CrushPath = AnimRoot + "2_Attacks/1__4Combos/M_Big_Sword@Attack_4Combo_4.FBX";
    private const string JabPath = AnimRoot + "2_Attacks/0__3Combos/M_Big_Sword@Attack_3Combo_1.FBX";
    private const string BarragePath = AnimRoot + "2_Attacks/2__7Combos/M_Big_Sword@Attack_7Combo_5.FBX";
    private const string ChargePath = AnimRoot + "2_Attacks/3__Dash_Attack/M_Big_Sword@Dash_Attack_ver_A.FBX";
    private const string StaggerPath = AnimRoot + "4_Damages/1__Front/M_Big_Sword@Damage_Front_Big_ver_A.FBX";
    private const string DiePath = AnimRoot + "4_Damages/6__Die/M_Big_Sword@Damage_Die.FBX";
    private const string SlamPath = MkRoot + "atk_energy05.fbx";
    private const string RoarPath = MkRoot + "buff01.fbx";

    private static readonly Vector3 ArenaCenter = new Vector3(104f, 0f, 104f);

    [MenuItem("Tools/Project Restart/Setup FortGolem Boss")]
    public static void Setup()
    {
        ProjectRestartUrpFix.FixAll(); // vendor mats are legacy Standard — convert or the golem is magenta
        var controller = BuildController();
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var boss = EnsureBoss(controller);
        EnsureArena();
        EnsureGate(boss);

        Require(EditorSceneManager.SaveScene(scene), "Could not save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectRestart] FortGolem boss ready — BossBase.controller, arena at " +
                  ArenaCenter + ", fog gate seals the approach. Play: walk past the gate, " +
                  "boss bar appears, phase 2 at half health with the Roar beat.");
    }

    // ---------- controller ----------

    private static AnimatorController BuildController()
    {
        const string folder = "Assets/_Project/Animations";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/_Project", "Animations");
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)
                         ?? AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.parameters = System.Array.Empty<AnimatorControllerParameter>();
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

        var sm = controller.layers[0].stateMachine;
        // Locomotion: baked idle↔walk blend driven by Speed — scripted cc.Move
        // supplies the travel. Attacks are CrossFade'd root-motion clips.
        var loco = EnsureState(sm, "Locomotion", BuildLocomotionTree(controller), new Vector3(-300, 0));
        sm.defaultState = loco;

        // Grruzam non-inplace strikes — EnsureAttackRootClip keeps authored Y /
        // live XZ and grafts the shared avatar. MK clips load raw: they keep
        // their own avatar (AGENTS.md — never graft the Grruzam avatar on).
        // Slower playback = mass. The golem is a 3m stone construct — human
        // cadence reads as a man in a suit; ~0.7x reads as weight. Windows are
        // normalized-time so timing scales with the clip, stays correct.
        EnsureState(sm, "Swing", Root(LoadClip(SwingPath, "Attack_4Combo_2")), new Vector3(0, -140), 0.7f);
        EnsureState(sm, "Crush", Root(LoadClip(CrushPath, "Attack_4Combo_4")), new Vector3(0, 0), 0.6f);
        EnsureState(sm, "Jab", Root(LoadClip(JabPath, "Attack_3Combo_1")), new Vector3(0, 140), 0.75f);
        EnsureState(sm, "Barrage", Root(LoadClip(BarragePath, "Attack_7Combo_5")), new Vector3(300, 0), 0.7f);
        // The gap-closer — one fast contrast against the lumbering strikes.
        EnsureState(sm, "Charge", Root(LoadClip(ChargePath, "Dash_Attack_ver_A")), new Vector3(600, 0), 0.9f);
        EnsureState(sm, "Slam", LoadClip(SlamPath, "atk_energy05"), new Vector3(300, -140), 0.65f);
        EnsureState(sm, "Roar", LoadClip(RoarPath, "buff01"), new Vector3(300, 140), 0.85f);
        EnsureState(sm, "Stagger", Bake(LoadClip(StaggerPath, "Damage_Front_Big_ver_A")), new Vector3(0, 280), 0.85f);
        EnsureState(sm, "Die", Bake(LoadClip(DiePath, "Damage_Die")), new Vector3(300, 280), 0.9f);

        // Code owns every transition — the states are pure clip players.
        foreach (var s in sm.states) s.state.transitions = System.Array.Empty<AnimatorStateTransition>();
        sm.anyStateTransitions = System.Array.Empty<AnimatorStateTransition>();

        ProjectRestartCombat.SetIkPass(controller); // FootGrounding's OnAnimatorIK
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static BlendTree BuildLocomotionTree(AnimatorController controller)
    {
        var old = AssetDatabase.LoadAllAssetsAtPath(ControllerPath);
        foreach (var a in old)
            if (a is BlendTree bt && bt.name == "BossMove") AssetDatabase.RemoveObjectFromAsset(bt);
        var tree = new BlendTree
        {
            name = "BossMove",
            blendType = BlendTreeType.Simple1D,
            blendParameter = "Speed",
            useAutomaticThresholds = false
        };
        tree.AddChild(Bake(LoadClip(IdlePath, "Idle")), 0f);
        tree.AddChild(Bake(LoadClip(WalkPath, "Walk_ver_A_Front")), 1f);
        // A lumbering stride — the walk clip slowed so each step lands heavy.
        var kids = tree.children;
        kids[1].timeScale = 0.7f;
        tree.children = kids;
        AssetDatabase.AddObjectToAsset(tree, controller);
        return tree;
    }

    // ---------- scene ----------

    private static BossGolem EnsureBoss(AnimatorController controller)
    {
        var boss = Object.FindFirstObjectByType<BossGolem>();
        if (boss == null)
        {
            var go = new GameObject("FortGolemBoss");
            go.transform.SetPositionAndRotation(ArenaCenter, Quaternion.Euler(0f, -135f, 0f));
            boss = go.AddComponent<BossGolem>();
        }
        var health = boss.GetComponent<Health>();
        Require(health != null, "BossGolem requires Health (RequireComponent failed).");
        var hso = new SerializedObject(health);
        SetIfFound(hso, "maxHealth", 900f);
        var bf = hso.FindProperty("bloodFx");
        if (bf != null) bf.objectReferenceValue = ProjectRestartCombat.FindFx("FX_BloodSplat_01");
        hso.ApplyModifiedPropertiesWithoutUndo();
        SetIfFound(hso, "soulsReward", 4000);
        hso.ApplyModifiedPropertiesWithoutUndo();

        if (boss.GetComponent<Targetable>() == null) boss.gameObject.AddComponent<Targetable>();
        var cc = boss.GetComponent<CharacterController>();
        if (cc == null) cc = boss.gameObject.AddComponent<CharacterController>();

        // Visual: unpack the Rivals prefab so bones/hierarchy are editable.
        var old = boss.transform.Find("FortGolemVisual");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GolemPrefabPath);
        Require(prefab != null, "FortGolem prefab missing: " + GolemPrefabPath);
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        visual.name = "FortGolemVisual";
        visual.transform.SetParent(boss.transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;

        var animator = visual.GetComponentInChildren<Animator>(true);
        Require(animator != null && animator.avatar != null && animator.avatar.isHuman,
                "FortGolem needs a humanoid-avatar Animator.");
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = true;  // RootMotionRelay consumes the deltas
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        // Size the capsule to the real mesh — it's the physics body AND the
        // lock-on hitbox, so a stock 1.8m capsule would make the golem whiff-proof.
        var bounds = CalcBounds(visual);
        cc.height = Mathf.Clamp(bounds.size.y, 2.2f, 4.5f);
        cc.radius = Mathf.Clamp(Mathf.Max(bounds.size.x, bounds.size.z) * 0.28f, 0.5f, 1.3f);
        cc.center = new Vector3(0f, cc.height * 0.5f + 0.05f, 0f);
        cc.slopeLimit = 50f;
        cc.stepOffset = 0.45f;

        // Arm it — Rivals names its hand bones Hand_R / Hand_L like the other Synty rigs.
        var handR = FindDeep(visual.transform, "Hand_R") ?? animator.GetBoneTransform(HumanBodyBones.RightHand);
        ArmWeapon(handR, animator);

        // Root motion + grounding live on the ANIMATOR object (OnAnimatorMove /
        // OnAnimatorIK only fire there).
        if (animator.GetComponent<RootMotionRelay>() == null)
            animator.gameObject.AddComponent<RootMotionRelay>();
        var fg = animator.GetComponent<FootGrounding>();
        if (fg == null) fg = animator.gameObject.AddComponent<FootGrounding>();
        var fso = new SerializedObject(fg);
        // The golem is ~1.5x skeleton height — retarget gaps scale with it.
        SetIfFound(fso, "pelvisOffsetMax", 1.1f);
        SetIfFound(fso, "attackPelvisOffsetMax", 1.5f);
        fso.ApplyModifiedPropertiesWithoutUndo();

        var so = new SerializedObject(boss);
        var animProp = so.FindProperty("bossAnimator");
        if (animProp != null) animProp.objectReferenceValue = animator;
        var roarProp = so.FindProperty("roarFx");
        if (roarProp != null) WriteCue(roarProp, "Fire_Circle_FX", "root", new Vector3(0f, 0.5f, 0f), 2.2f, 5, 3f);
        var slamProp = so.FindProperty("slamFx");
        if (slamProp != null) WriteCue(slamProp, "FX_GroundCrack_Blast_01", "root", Vector3.zero, 2.4f, 0, 2.5f);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(boss.gameObject);
        return boss;
    }

    /// <summary>Flat arena pad under the boss — P6 replaces it with dressed
    /// ruins; for now a dark slab so the fight is playable immediately.</summary>
    private static void EnsureArena()
    {
        if (GameObject.Find("BossArenaFloor") != null) return;
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "BossArenaFloor";
        floor.transform.position = ArenaCenter + Vector3.down * 0.55f;
        floor.transform.localScale = new Vector3(26f, 1f, 26f);
        var r = floor.GetComponent<MeshRenderer>();
        if (r != null)
            r.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/_Project/Materials/TestGround.mat");
        EditorUtility.SetDirty(floor);
    }

    private static void EnsureGate(BossGolem boss)
    {
        // Mouth of the arena — the ruin corridor approaches from the south
        // along x≈101, so the gate spans it axis-aligned.
        var gate = GameObject.Find("FogGate");
        if (gate == null)
        {
            gate = new GameObject("FogGate");
            gate.transform.position = new Vector3(101f, 1.6f, 96.5f);
        }
        var trig = gate.GetComponent<BoxCollider>();
        if (trig == null) trig = gate.AddComponent<BoxCollider>();
        trig.isTrigger = true;
        trig.size = new Vector3(10f, 4f, 3f);

        var blocker = GameObject.Find("FogGateBlocker");
        if (blocker == null)
        {
            blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blocker.name = "FogGateBlocker";
            blocker.transform.SetParent(gate.transform, false);
            blocker.transform.localPosition = Vector3.zero;
            blocker.transform.localScale = new Vector3(10f, 5f, 0.8f);
            var r = blocker.GetComponent<MeshRenderer>();
            if (r != null) r.enabled = false; // invisible wall — P6 dresses it with a gate mesh
            blocker.SetActive(false);
        }

        var fg = gate.GetComponent<FogGate>();
        if (fg == null) fg = gate.AddComponent<FogGate>();
        var so = new SerializedObject(fg);
        var bp = so.FindProperty("boss");
        if (bp != null) bp.objectReferenceValue = boss;
        var bl = so.FindProperty("blocker");
        if (bl != null) bl.objectReferenceValue = blocker;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(gate);
    }

    // ---------- helpers ----------

    private static void ArmWeapon(Transform hand, Animator animator)
    {
        if (hand == null)
        {
            Debug.LogWarning("[ProjectRestart] FortGolem hand bone not found — weapon skipped.");
            return;
        }
        var old = FindDeep(hand.root, "BossWeapon");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WeaponPrefabPath);
        if (prefab == null) { Debug.LogWarning("[ProjectRestart] Weapon prefab missing: " + WeaponPrefabPath); return; }
        var item = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        item.name = "BossWeapon";
        item.transform.SetParent(hand, false);
        // Palm grip, not wrist pivot: the weapon's pivot is its handle, so it
        // sits on the wrist→middle-knuckle line like WeaponSocket.palmGrip.
        var knuckle = animator != null ? animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal) : null;
        item.transform.position = knuckle != null
            ? Vector3.Lerp(hand.position, knuckle.position, 0.55f)
            : hand.position;
        item.transform.localRotation = Quaternion.identity;
        item.transform.localScale = Vector3.one;
        Debug.Log($"[ProjectRestart] BossWeapon grip -> {hand.name} " +
                  $"localPos={item.transform.localPosition} localRot={item.transform.localEulerAngles} " +
                  "(tune by editing BossWeapon's local transform in the scene)", item);
    }

    /// <summary>Fill a serialized FxCue (prefab resolved from the Rivals FX dir).</summary>
    private static void WriteCue(SerializedProperty cue, string prefabName, string attach,
                                 Vector3 offset, float scale, int palette, float life)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{FxDir}/{prefabName}.prefab")
                     ?? ProjectRestartCombat.FindFx(prefabName); // PolygonParticles etc. live outside FxDir
        if (prefab == null)
        {
            Debug.LogWarning("[ProjectRestart] FX prefab missing: " + prefabName + " — cue left empty.");
            return;
        }
        cue.FindPropertyRelative("prefab").objectReferenceValue = prefab;
        cue.FindPropertyRelative("attach").stringValue = attach;
        cue.FindPropertyRelative("offset").vector3Value = offset;
        cue.FindPropertyRelative("scale").floatValue = scale;
        cue.FindPropertyRelative("palette").intValue = palette;
        cue.FindPropertyRelative("life").floatValue = life;
    }

    private static Bounds CalcBounds(GameObject visual)
    {
        Bounds? b = null;
        foreach (var r in visual.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            b = b.HasValue ? Encapsulate(b.Value, r.bounds) : r.bounds;
        }
        return b ?? new Bounds(Vector3.up * 1.5f, new Vector3(1.6f, 3f, 1.6f));
    }

    private static Bounds Encapsulate(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

    private static void SetIfFound(SerializedObject so, string name, float v)
    {
        var p = so.FindProperty(name);
        if (p != null && p.propertyType == SerializedPropertyType.Float) p.floatValue = v;
        else if (p != null && p.propertyType == SerializedPropertyType.Integer) p.intValue = (int)v;
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

    private static AnimatorState EnsureState(AnimatorStateMachine sm, string name, Motion clip, Vector3 pos, float speed = 1f)
    {
        foreach (var s in sm.states)
            if (s.state.name == name) { s.state.motion = clip; s.state.speed = speed; return s.state; }
        var st = sm.AddState(name, pos);
        st.motion = clip;
        st.speed = speed;
        st.writeDefaultValues = false;
        return st;
    }

    /// <summary>Stationary clips: feet-baked root Y + shared avatar (same bake
    /// the enemy controller uses — corpse/idle ground correctly unbaked would
    /// strand them at hip height once the relay discards deltas).</summary>
    private static AnimationClip Bake(AnimationClip clip) => ProjectRestartCombat.EnsureClipYBake(clip);

    /// <summary>Root-motion strikes: authored XZ live for the relay.</summary>
    private static AnimationClip Root(AnimationClip clip) => ProjectRestartCombat.EnsureAttackRootClip(clip);

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
