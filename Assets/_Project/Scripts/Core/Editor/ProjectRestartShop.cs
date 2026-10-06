using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Setup Shop + Inventory.
/// Authors the ItemDef stock (Assets/_Project/Items/), builds the single-state
/// Merchant.controller (CLazy idle retargets onto the Synty humanoid), plants a
/// dressed stall beside Checkpoint A, and gives the player an Inventory
/// component with starter vials. Re-runnable: assets and scene objects are
/// found-or-created, never duplicated.
/// </summary>
public static class ProjectRestartShop
{
    private const string ScenePath = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const string ItemFolder = "Assets/_Project/Items";
    private const string ControllerPath = "Assets/_Project/Animations/Merchant.controller";
    private const string MerchantPrefabPath =
        "Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs/Characters/SM_Chr_Gravedigger_Male_01.prefab";
    private const string IdleClipPath =
        "Assets/ThirdParty/CLazyRunner/Animations/P1_CLazyMovement/Mvm_Idle/CLazy@Idle_Wait_A.FBX";
    private const string PropRoot = "Assets/ThirdParty/Synty/PolygonDarkFantasy/Prefabs/Props/";

    [MenuItem("Tools/Project Restart/Setup Shop + Inventory")]
    public static void Setup()
    {
        if (!AssetDatabase.IsValidFolder(ItemFolder))
            AssetDatabase.CreateFolder("Assets/_Project", "Items");

        var vial = Item("item_vial", "Ember Vial", "VIAL", "Vial",
            "A thumb of warm emberwine. Restores 60 HP over the sip.",
            120, ItemDef.Effect.Heal, 60f);
        var star = Item("item_starwater", "Starwater", "STAR", "Starwater",
            "Bottled night-sky condensation. Restores 45 Core Energy.",
            140, ItemDef.Effect.Mana, 45f);
        var wind = Item("item_secondwind", "Second Wind", "WIND", "Feather",
            "Smelling salts and spite. Restores stamina.",
            100, ItemDef.Effect.Stamina, 0f);
        var bomb = Item("item_gravewax", "Grave Wax Bomb", "BOMB", "Bomb",
            "Rendered fat and a spiteful spark. Thrown — 70 damage.",
            90, ItemDef.Effect.Throw, 70f);
        Item("item_soulcask", "Soul Cask", "CASK", "Cask",
            "A mason jar of the dead. Grants 400 souls.",
            0, ItemDef.Effect.Souls, 400f);            // loot stock — not sold
        Item("item_medallion", "Rusty Medallion", "MEDAL", "Medal",
            "Half a grave-robber's token. The other half is still out there.",
            0, ItemDef.Effect.Souls, 0f, ItemDef.Kind.Key); // key item — inspect only

        var controller = BuildMerchantController();

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var cp = Object.FindFirstObjectByType<Checkpoint>();
        var anchor = cp != null ? cp.transform.position : Vector3.zero;
        EnsureStall(anchor + new Vector3(3.2f, 0f, 2.4f), cp);
        EnsureInventory();
        Require(EditorSceneManager.SaveScene(scene), "Could not save " + ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectRestart] Shop ready — merchant stall beside Checkpoint A, " +
                  "6 ItemDefs authored, player Inventory holds 2 Ember Vials on a fresh save. " +
                  "Play: walk up → E TALK → BUY/SELL.");
    }

    // ---------- items ----------

    private static ItemDef Item(string id, string name, string shortName, string icon,
        string desc, int price, ItemDef.Effect effect, float magnitude,
        ItemDef.Kind kind = ItemDef.Kind.Consumable)
    {
        var path = $"{ItemFolder}/{id}.asset";
        var item = AssetDatabase.LoadAssetAtPath<ItemDef>(path);
        if (item == null)
        {
            item = ScriptableObject.CreateInstance<ItemDef>();
            AssetDatabase.CreateAsset(item, path);
        }
        item.itemId = id;
        item.itemName = name;
        item.shortName = shortName;
        item.icon = icon;
        item.description = desc;
        item.kind = kind;
        item.price = price;
        item.stackMax = kind == ItemDef.Kind.Key ? 1 : 99;
        item.effect = effect;
        item.magnitude = magnitude;
        EditorUtility.SetDirty(item);
        return item;
    }

    // ---------- merchant ----------

    private static AnimatorController BuildMerchantController()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath)
                         ?? AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        var sm = controller.layers[0].stateMachine;
        AnimationClip idleClip = null;
        foreach (var a in AssetDatabase.LoadAllAssetsAtPath(IdleClipPath))
            if (a is AnimationClip c && !c.name.StartsWith("__")) { idleClip = c; break; }
        Require(idleClip != null, "Idle clip missing: " + IdleClipPath);
        var idle = sm.states.Length > 0 ? sm.states[0].state : sm.AddState("Idle");
        idle.name = "Idle";
        idle.motion = idleClip;
        sm.defaultState = idle;
        EditorUtility.SetDirty(controller);
        return controller;
    }

    private static void EnsureStall(Vector3 pos, Checkpoint cp)
    {
        var root = GameObject.Find("MerchantStall");
        if (root == null) root = new GameObject("MerchantStall");
        root.transform.position = pos;
        if (cp != null)
            root.transform.rotation = Quaternion.LookRotation(
                FlatY(cp.transform.position - pos));

        var merchantGo = root.transform.Find("Merchant")?.gameObject;
        if (merchantGo == null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MerchantPrefabPath);
            Require(prefab != null, "Merchant prefab missing: " + MerchantPrefabPath);
            merchantGo = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            merchantGo.name = "Merchant";
            merchantGo.transform.SetParent(root.transform, false);
            merchantGo.transform.localPosition = Vector3.zero;
        }
        var merchant = merchantGo.GetComponent<NpcMerchant>()
                       ?? merchantGo.AddComponent<NpcMerchant>();
        var animator = merchantGo.GetComponentInChildren<Animator>(true);
        Require(animator != null, "Gravedigger prefab has no Animator.");
        Require(animator.avatar == null || animator.avatar.isHuman,
            "Gravedigger avatar is not humanoid — the idle clip won't retarget.");
        animator.runtimeAnimatorController =
            AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var so = new SerializedObject(merchant);
        var stock = so.FindProperty("stock");
        var stockIds = new[] { "item_vial", "item_starwater", "item_secondwind", "item_gravewax" };
        stock.arraySize = stockIds.Length;
        for (var i = 0; i < stockIds.Length; i++)
            stock.GetArrayElementAtIndex(i).objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{stockIds[i]}.asset");
        so.ApplyModifiedPropertiesWithoutUndo();

        // Dressing: altar-table counter, a lit candle, a barrel out back.
        Prop(root.transform, "Counter", PropRoot + "SM_Prop_Altar_Table_01.prefab",
            new Vector3(0f, 0f, 0.9f), Vector3.one);
        var candle = Prop(root.transform, "Candle", PropRoot + "SM_Prop_Candle_01.prefab",
            new Vector3(0.35f, 0.95f, 0.85f), Vector3.one);
        Prop(candle.transform, "Flame", PropRoot + "FX_Candle_Flame_01.prefab",
            new Vector3(0f, 0.12f, 0f), Vector3.one);
        Prop(root.transform, "Barrel", PropRoot + "SM_Prop_Barrel_01.prefab",
            new Vector3(-1.1f, 0f, 0.2f), Vector3.one);
        EditorUtility.SetDirty(root);
    }

    private static GameObject Prop(Transform parent, string name, string path,
        Vector3 localPos, Vector3 scale)
    {
        var existing = parent.Find(name);
        if (existing != null) return existing.gameObject;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null)
        {
            Debug.LogWarning("[ProjectRestart] Prop missing: " + path);
            return parent.gameObject;
        }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        return go;
    }

    private static void EnsureInventory()
    {
        var loco = Object.FindFirstObjectByType<PlayerLocomotion>();
        Require(loco != null, "No player in " + ScenePath);
        var inv = loco.GetComponent<Inventory>() ?? loco.gameObject.AddComponent<Inventory>();
        var so = new SerializedObject(inv);
        var starter = so.FindProperty("starterItems");
        starter.arraySize = 1;
        starter.GetArrayElementAtIndex(0).objectReferenceValue =
            AssetDatabase.LoadAssetAtPath<ItemDef>(ItemFolder + "/item_vial.asset");
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(loco.gameObject);
    }

    private static Vector3 FlatY(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 0.01f ? Vector3.forward : v.normalized;
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new System.Exception("[ProjectRestart] " + message);
    }
}
