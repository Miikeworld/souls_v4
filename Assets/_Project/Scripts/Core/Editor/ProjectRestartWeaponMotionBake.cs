using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes the Grruzam source rig's weapon-socket bone (ik_hand_gun, a
/// non-humanoid bone dropped by humanoid retarget) into a WeaponMotionSet the
/// runtime socket replays. Values are converted into the Hero right-hand's
/// local frame via a constant frame offset computed from both rigs' bind
/// poses, so baked rotations reproduce the authored blade world orientation.
/// </summary>
public static class ProjectRestartWeaponMotionBake
{
    private const int SamplesPerClip = 48;
    private const string AssetPath = "Assets/_Project/Combat/WeaponMotion.asset";

    [MenuItem("Tools/Project Restart/Bake Weapon Bone Motion")]
    private static void Bake()
    {
        var log = new StringBuilder();

        // --- Source rig bind pose (T-pose skeleton) -------------------------
        var srcRoot = LoadSkeletonRoot("Modeling_T-Pose_Grrrru_Man") ?? LoadSkeletonRoot("Modeling_Grrrru_Man");
        if (srcRoot == null)
        {
            Debug.LogError("[WeaponBake] Grruzam skeleton model not found (Modeling_T-Pose_Grrrru_Man).");
            return;
        }
        var srcHand = FindDeep(srcRoot.transform, "hand_r");
        var gun = FindDeep(srcRoot.transform, "ik_hand_gun");
        if (srcHand == null || gun == null)
        {
            Debug.LogError("[WeaponBake] hand_r / ik_hand_gun not found in the Grruzam skeleton.");
            return;
        }
        var srcHandRot = WorldRotation(srcHand);
        log.AppendLine($"src hand_r bindWorldRot={srcHandRot.eulerAngles}  gun parent={gun.parent.name}");
        log.AppendLine($"gun bindLocal pos={gun.localPosition} rot={gun.localEulerAngles} scale={gun.localScale}");

        // --- Hero bind pose --------------------------------------------------
        var heroPath = AssetDatabase.GUIDToAssetPath("071b4da7cfff18347a371c677585b749");
        var hero = AssetDatabase.LoadAssetAtPath<GameObject>(heroPath);
        var heroAnim = hero != null ? hero.GetComponentInChildren<Animator>() : null;
        var heroHand = heroAnim != null ? heroAnim.GetBoneTransform(HumanBodyBones.RightHand) : null;
        if (heroHand == null)
        {
            Debug.LogError("[WeaponBake] Hero right hand not resolvable on " + heroPath);
            return;
        }
        var heroHandRot = WorldRotation(heroHand);
        log.AppendLine($"hero RightHand bindWorldRot={heroHandRot.eulerAngles} lossyScale={heroHand.lossyScale}");

        // Frame conversion: hero-hand local = D * source-hand local, so the
        // sword's WORLD rotation matches the authored gun world rotation.
        var d = Quaternion.Inverse(heroHandRot) * srcHandRot;
        log.AppendLine($"frame offset D euler={d.eulerAngles}");

        // Position units: if the source socket offset looks meter-scale while
        // the Hero bones are cm-scale, rescale so the grip lands on the palm.
        var bindPos = d * gun.localPosition;
        var posScale = bindPos.magnitude < 1f ? 100f : 1f;
        bindPos *= posScale;
        var bindRot = d * gun.localRotation;
        log.AppendLine($"bindPos(hero-local)={bindPos} posScale={posScale}  bindRot euler={bindRot.eulerAngles}");

        // --- Per-clip curves -------------------------------------------------
        var set = LoadOrCreate();
        set.bindPos = bindPos;
        set.bindRot = bindRot;

        var entries = new List<WeaponMotionSet.Entry>();
        var fbxGuids = AssetDatabase.FindAssets("t:Model M_", new[] { "Assets/ThirdParty/GrruzamPowerfulSword" });
        foreach (var g in fbxGuids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var clip = asset as AnimationClip;
                if (clip == null || clip.name.StartsWith("__preview")) continue;
                var entry = BakeClip(clip, d, posScale, gun, bindRot, log);
                if (entry != null) entries.Add(entry);
            }
        }
        set.entries = entries.ToArray();

        EditorUtility.SetDirty(set);
        AssetDatabase.SaveAssets();
        log.AppendLine($"Wrote {entries.Count} clip motion entries → {AssetPath}");

        // --- Wire the scene socket ------------------------------------------
        var rigPrefab = LoadSkeletonRoot("Modeling_Grrrru_Man");
        var wired = 0;
        foreach (var socket in Object.FindObjectsByType<WeaponSocket>(FindObjectsSortMode.None))
        {
            var so = new SerializedObject(socket);
            var motion = so.FindProperty("motionSet");
            var rig = so.FindProperty("shadowRig");
            if (motion == null && rig == null) continue;
            if (motion != null) motion.objectReferenceValue = set;
            if (rig != null && rigPrefab != null) rig.objectReferenceValue = rigPrefab;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(socket);
            wired++;
        }
        log.AppendLine(wired > 0
            ? $"Assigned motionSet + shadowRig on {wired} WeaponSocket(s) in open scenes — save the scene."
            : "No WeaponSocket found in open scenes — assign WeaponMotion.asset and Modeling_Grrrru_Man.prefab to the socket fields.");
        if (rigPrefab == null)
            log.AppendLine("WARN: Modeling_Grrrru_Man prefab not found — shadowRig left unassigned.");
        Debug.Log(log.ToString());
    }

    private static WeaponMotionSet.Entry BakeClip(AnimationClip clip, Quaternion d, float posScale, Transform gun, Quaternion bindRot, StringBuilder log)
    {
        // Gather transform curves on the ik_hand_gun path.
        var comps = new Dictionary<string, AnimationCurve>();
        var found = false;
        foreach (var b in AnimationUtility.GetCurveBindings(clip))
        {
            if (b.type != typeof(Transform) || !b.path.EndsWith("ik_hand_gun")) continue;
            if (!b.propertyName.StartsWith("m_LocalPosition") && !b.propertyName.StartsWith("m_LocalRotation")) continue;
            comps[b.propertyName] = AnimationUtility.GetEditorCurve(clip, b);
            found = true;
        }
        if (!found) return null;

        var bindQ = gun.localRotation;
        var bindP = gun.localPosition;
        var keys = new WeaponMotionSet.Key[SamplesPerClip];
        var prevQ = Quaternion.identity;
        float maxDev = 0f;
        for (var i = 0; i < SamplesPerClip; i++)
        {
            var t = clip.length * i / (SamplesPerClip - 1);
            var p = new Vector3(
                Eval(comps, "m_LocalPosition.x", t, bindP.x),
                Eval(comps, "m_LocalPosition.y", t, bindP.y),
                Eval(comps, "m_LocalPosition.z", t, bindP.z));
            var q = new Quaternion(
                Eval(comps, "m_LocalRotation.x", t, bindQ.x),
                Eval(comps, "m_LocalRotation.y", t, bindQ.y),
                Eval(comps, "m_LocalRotation.z", t, bindQ.z),
                Eval(comps, "m_LocalRotation.w", t, bindQ.w));
            q.Normalize();
            // Keep consecutive samples on the same hemisphere so Slerp takes
            // the short arc between baked keys.
            if (i > 0 && Quaternion.Dot(q, prevQ) < 0f)
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            prevQ = q;
            var k = new WeaponMotionSet.Key
            {
                t = i / (float)(SamplesPerClip - 1),
                pos = d * p * posScale,
                rot = d * q,
            };
            var dev = Quaternion.Angle(k.rot, bindRot);
            if (dev > maxDev) maxDev = dev;
            keys[i] = k;
        }
        log.AppendLine($"  {clip.name}: baked {SamplesPerClip} keys, max socket deviation={maxDev:F1}°");
        return new WeaponMotionSet.Entry { clipName = clip.name, keys = keys };
    }

    private static float Eval(Dictionary<string, AnimationCurve> comps, string prop, float t, float fallback)
    {
        return comps.TryGetValue(prop, out var c) && c != null ? c.Evaluate(t) : fallback;
    }

    private static WeaponMotionSet LoadOrCreate()
    {
        var set = AssetDatabase.LoadAssetAtPath<WeaponMotionSet>(AssetPath);
        if (set == null)
        {
            set = ScriptableObject.CreateInstance<WeaponMotionSet>();
            AssetDatabase.CreateAsset(set, AssetPath);
        }
        return set;
    }

    private static GameObject LoadSkeletonRoot(string name)
    {
        foreach (var g in AssetDatabase.FindAssets("t:Model " + name, new[] { "Assets/ThirdParty/GrruzamPowerfulSword" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            if (path.EndsWith(".FBX") || path.EndsWith(".fbx"))
                return AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
        }
        return null;
    }

    private static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t)
        {
            var r = FindDeep(c, name);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>Composed world rotation for a prefab-asset transform (bind pose).</summary>
    private static Quaternion WorldRotation(Transform t)
    {
        var rot = t.localRotation;
        var p = t.parent;
        while (p != null)
        {
            rot = p.localRotation * rot;
            p = p.parent;
        }
        return rot;
    }
}
