using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Explicit manual setup only. Derived clips and FX belong to the
/// project; imported CLazy takes retain their own humanoid avatar.</summary>
public static class ProjectRestartPlunge
{
    private const string SourceFolder = "Assets/ThirdParty/CLazyRunner/Animations/P4_CLazyAttack/Attack_Wp_Jump_Air/";
    private const string NativeModel = "Assets/ThirdParty/CLazyRunner/Models/ClazyRunner.FBX";
    private const string FallSource = "Assets/ThirdParty/CLazyRunner/Animations/P3_CLazyJump/Jmp_Air_Loop/CLazy@Jump_Down_D_Loop.FBX";
    private const string ClipFolder = "Assets/_Project/Combat/Clips";
    private const string FxFolder = "Assets/_Project/FX/Crowd";
    public const string StartPath = ClipFolder + "/Plunge_Start.anim";
    public const string FallPath = ClipFolder + "/Plunge_Fall.anim";
    public const string LandPath = ClipFolder + "/Plunge_Land.anim";
    public const string ImpactPath = FxFolder + "/FX_CrimsonPlungeImpact.prefab";
    private const float FallLength = 0.4f;

    public static void CalibrateBladeSet(WeaponSet set)
    {
        if (set == null) return;
        set.socketDeltaScale = 0f;
        if (set.weaponPrefab != null && BladeGeometry.TryMeasure(set.weaponPrefab.transform, out var bladeBase, out var bladeTip, out var axis))
        {
            set.bladeBaseLocal = bladeBase; set.bladeTipLocal = bladeTip;
            set.bladeAxis = axis; set.bladeGeometryCalibrated = true;
        }
        EditorUtility.SetDirty(set);
    }

    public static bool Configure(AnimatorController controller)
    {
        if (controller == null || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            return false;
        EnsureFolder(ClipFolder);
        EnsureFolder(FxFolder);
        var avatar = AssetDatabase.LoadAllAssetsAtPath(NativeModel).OfType<Avatar>().FirstOrDefault(a => a.isHuman);
        if (avatar == null) { Debug.LogError("[PlungeSetup] Native CLazy humanoid avatar is missing; phases were not changed."); return false; }
        var start = NativeClip(SourceFolder + "CLazy@WpJAttack_AirDrop_Start.FBX", avatar, false);
        var keep = NativeClip(SourceFolder + "CLazy@WpJAttack_AirDrop_Keep.FBX", avatar, true);
        var end = NativeClip(SourceFolder + "CLazy@WpJAttack_AirDrop_End.FBX", avatar, false);
        var down = NativeClip(FallSource, avatar, true);
        if (start == null || keep == null || end == null || down == null)
        { Debug.LogError("[PlungeSetup] A native plunge source clip is missing; no partial phase installation."); return false; }

        var ownStart = CopyNativeClip(start, StartPath, false);
        var ownLand = CopyNativeClip(end, LandPath, false);
        var ownFall = BuildFallLoop(keep, down, avatar);
        if (ownFall == null) return false;
        var sm = controller.layers[0].stateMachine;
        InstallState(sm, "DiveStart", ownStart, ownStart.length / 0.35f);
        InstallState(sm, "DiveAttack", ownFall, 1f);
        InstallState(sm, "DiveLand", ownLand, 1.2f);
        var impact = EnsurePlungeImpact();
        foreach (var attack in UnityEngine.Object.FindObjectsByType<AttackController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var data = new SerializedObject(attack);
            SetObject(data, "plungeStartClip", ownStart);
            SetObject(data, "plungeFallClip", ownFall);
            SetObject(data, "plungeLandClip", ownLand);
            SetObject(data, "diveImpactFx", impact);
            SetFloat(data, "plungePreparationSeconds", 0.35f);
            SetFloat(data, "plungeBrakeSeconds", 0.08f);
            SetFloat(data, "plungeAccelerationSeconds", 0.18f);
            SetFloat(data, "plungeTerminalVelocity", -18f);
            SetFloat(data, "plungeDriftSpeed", 1f);
            SetFloat(data, "diveRadius", 2.6f);
            SetFloat(data, "fadeOut", 0.10f);
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        EditorUtility.SetDirty(controller);
        Debug.Log("[PlungeSetup] Native-avatar Start / animated Fall / owned Land installed. Floor feedback and anti-penetration require a live playtest.");
        return true;
    }

    private static AnimationClip NativeClip(string path, Avatar avatar, bool loop)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null) return null;
        var changed = importer.sourceAvatar != avatar || importer.avatarSetup != ModelImporterAvatarSetup.CopyFromOther;
        importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
        importer.sourceAvatar = avatar;
        var infos = importer.clipAnimations;
        if (infos.Length == 0) infos = importer.defaultClipAnimations;
        foreach (var info in infos)
        {
            if (info.loopTime != loop) { info.loopTime = loop; changed = true; }
        }
        if (changed) { importer.clipAnimations = infos; importer.SaveAndReimport(); }
        return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"));
    }

    private static AnimationClip CopyNativeClip(AnimationClip source, string path, bool loop)
    {
        var copy = UnityEngine.Object.Instantiate(source);
        copy.name = System.IO.Path.GetFileNameWithoutExtension(path);
        copy.hideFlags = HideFlags.None;
        // Root deltas are discarded by the relay during every plunge phase.
        // Keep body posture in the clip; do not bake travel into the visual.
        var settings = AnimationUtility.GetAnimationClipSettings(copy);
        settings.loopTime = loop;
        settings.loopBlend = loop;
        settings.loopBlendPositionY = false;
        settings.loopBlendPositionXZ = false;
        settings.keepOriginalPositionY = true;
        settings.keepOriginalPositionXZ = true;
        settings.heightFromFeet = false;
        AnimationUtility.SetAnimationClipSettings(copy, settings);
        return SaveClipInPlace(copy, path);
    }

    private static AnimationClip BuildFallLoop(AnimationClip keep, AnimationClip down, Avatar avatar)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(NativeModel);
        if (model == null) return null;
        var sample = UnityEngine.Object.Instantiate(model);
        sample.hideFlags = HideFlags.HideAndDontSave;
        HumanPoseHandler handler = null;
        AnimationClip output = null;
        try
        {
            var animator = sample.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.avatar != null && a.avatar.isHuman);
            if (animator == null) animator = sample.AddComponent<Animator>();
            animator.avatar = avatar; animator.runtimeAnimatorController = null; animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.enabled = false;
            handler = new HumanPoseHandler(avatar, animator.transform);
            keep.SampleAnimation(sample, 0f);
            var pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            handler.GetHumanPose(ref pose);
            var baseMuscles = (float[])pose.muscles.Clone();
            down.SampleAnimation(sample, 0f); handler.GetHumanPose(ref pose);
            var firstDown = (float[])pose.muscles.Clone();
            const int frames = 12; // 0.4s at 30fps, inclusive closing sample.
            var values = new float[HumanTrait.MuscleCount][];
            for (var muscle = 0; muscle < values.Length; muscle++) values[muscle] = new float[frames + 1];
            for (var frame = 0; frame <= frames; frame++)
            {
                down.SampleAnimation(sample, frame == frames ? 0f : down.length * frame / frames);
                handler.GetHumanPose(ref pose);
                for (var muscle = 0; muscle < values.Length; muscle++)
                    values[muscle][frame] = Mathf.Clamp(baseMuscles[muscle] +
                        (IsLegMuscle(HumanTrait.MuscleName[muscle]) ? (pose.muscles[muscle] - firstDown[muscle]) * 0.5f : 0f), -1f, 1f);
            }
            output = UnityEngine.Object.Instantiate(keep);
            output.name = "Plunge_Fall"; output.hideFlags = HideFlags.None; output.frameRate = 30f;
            // Freeze the Keep's non-muscle channels, including the source weapon
            // bone and pelvis orientation. Static RootT gives zero root DELTA
            // without erasing the body's anatomical height.
            var sourceBindings = AnimationUtility.GetCurveBindings(keep).Concat(AnimationUtility.GetCurveBindings(down))
                .Where(b => b.type == typeof(Animator)).ToArray();
            foreach (var binding in AnimationUtility.GetCurveBindings(output))
            {
                var curve = AnimationUtility.GetEditorCurve(output, binding);
                if (curve == null) continue;
                // Imported humanoid clips also contain baked IK foot goals.
                // Holding those would cancel the new leg variation whenever an
                // evaluator enables foot goals; this loop is muscle-driven.
                if (IsFootGoal(binding.propertyName))
                { AnimationUtility.SetEditorCurve(output, binding, null); continue; }
                AnimationUtility.SetEditorCurve(output, binding, AnimationCurve.Constant(0f, FallLength, curve.Evaluate(0f)));
            }
            for (var muscle = 0; muscle < values.Length; muscle++)
            {
                var keys = new Keyframe[frames + 1];
                for (var frame = 0; frame <= frames; frame++)
                {
                    var before = values[muscle][frame == 0 || frame == frames ? frames - 1 : frame - 1];
                    var after = values[muscle][frame == frames || frame == 0 ? 1 : frame + 1];
                    var tangent = (after - before) / (2f * FallLength / frames);
                    keys[frame] = new Keyframe(FallLength * frame / frames, values[muscle][frame], tangent, tangent);
                }
                var curve = new AnimationCurve(keys) { preWrapMode = WrapMode.Loop, postWrapMode = WrapMode.Loop };
                // Finger properties use LeftHand.Thumb.1 naming in imported
                // clips, while HumanTrait may use spaced labels. Resolve the
                // actual existing binding rather than inventing an invalid one.
                var label = HumanTrait.MuscleName[muscle];
                var binding = sourceBindings.FirstOrDefault(b => NormalizeMuscleName(b.propertyName) == NormalizeMuscleName(label));
                if (string.IsNullOrEmpty(binding.propertyName))
                {
                    if (IsLegMuscle(label)) throw new InvalidOperationException("Missing native leg muscle binding: " + label);
                    continue;
                }
                AnimationUtility.SetEditorCurve(output, binding, curve);
            }
            var settings = AnimationUtility.GetAnimationClipSettings(output);
            settings.startTime = 0f; settings.stopTime = FallLength;
            settings.loopTime = true; settings.loopBlend = true;
            settings.loopBlendPositionY = false; settings.loopBlendPositionXZ = false;
            settings.keepOriginalPositionY = true; settings.keepOriginalPositionXZ = true;
            settings.heightFromFeet = false;
            AnimationUtility.SetAnimationClipSettings(output, settings);
            output.EnsureQuaternionContinuity();
            if (!ValidateFallClip(output, out var problem)) throw new InvalidOperationException(problem);
            ValidateSampledFall(sample, handler, output);
            return SaveClipInPlace(output, FallPath);
        }
        catch (Exception error)
        {
            if (output != null && !AssetDatabase.Contains(output)) UnityEngine.Object.DestroyImmediate(output);
            Debug.LogError("[PlungeSetup] Could not derive native fall motion; no static-pose fallback was installed. " + error.Message);
            return null;
        }
        finally { handler?.Dispose(); UnityEngine.Object.DestroyImmediate(sample); }
    }

    public static bool IsLegMuscle(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var normalized = NormalizeMuscleName(name).ToLowerInvariant();
        return normalized.StartsWith("leftupperleg") || normalized.StartsWith("rightupperleg") ||
               normalized.StartsWith("leftlowerleg") || normalized.StartsWith("rightlowerleg") ||
               normalized.StartsWith("leftfoot") || normalized.StartsWith("rightfoot") ||
               normalized.StartsWith("lefttoes") || normalized.StartsWith("righttoes");
    }

    private static string NormalizeMuscleName(string name)
        => name.Replace("Hand", "").Replace(" ", "").Replace(".", "").Replace("-", "");

    private static bool IsFootGoal(string name)
        => name.StartsWith("LeftFootT.") || name.StartsWith("LeftFootQ.") ||
           name.StartsWith("RightFootT.") || name.StartsWith("RightFootQ.");

    /// <summary>Curve-level checks run during the explicit setup, before a
    /// generated loop can replace an existing asset.</summary>
    public static bool ValidateFallClip(AnimationClip clip, out string problem)
    {
        problem = null;
        if (clip == null) { problem = "Fall clip is null."; return false; }
        var varyingLegs = 0;
        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            if (IsFootGoal(binding.propertyName)) { problem = "Fall clip retains static IK foot goals."; return false; }
            if (binding.type != typeof(Animator)) continue;
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null || curve.length == 0) continue;
            var start = curve.Evaluate(0f);
            if (Mathf.Abs(start - curve.Evaluate(FallLength)) > 0.0001f)
            { problem = "Fall loop seam is open: " + binding.propertyName; return false; }
            var variation = 0f;
            for (var frame = 1; frame < 12; frame++) variation = Mathf.Max(variation, Mathf.Abs(curve.Evaluate(frame / 30f) - start));
            if (IsLegMuscle(binding.propertyName)) { if (variation > 0.001f) varyingLegs++; }
            else if (variation > 0.0001f)
            { problem = "Fall loop changes its held upper body, pelvis or root: " + binding.propertyName; return false; }
        }
        if (varyingLegs == 0) { problem = "Fall loop has no native leg variation."; return false; }
        return true;
    }

    private static void ValidateSampledFall(GameObject rig, HumanPoseHandler handler, AnimationClip clip)
    {
        var pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
        clip.SampleAnimation(rig, 0f); handler.GetHumanPose(ref pose);
        var first = (float[])pose.muscles.Clone();
        var firstBody = pose.bodyPosition;
        var firstRotation = pose.bodyRotation;
        var legMovement = 0f;
        foreach (var time in new[] { 0.1f, 0.2f, 0.3f, FallLength })
        {
            clip.SampleAnimation(rig, time); handler.GetHumanPose(ref pose);
            if (Vector3.Distance(firstBody, pose.bodyPosition) > 0.002f || Quaternion.Angle(firstRotation, pose.bodyRotation) > 0.5f)
                throw new InvalidOperationException("Generated fall loop moves its held root/pelvis during sampling.");
            for (var muscle = 0; muscle < first.Length; muscle++)
            {
                var difference = Mathf.Abs(first[muscle] - pose.muscles[muscle]);
                if (IsLegMuscle(HumanTrait.MuscleName[muscle])) legMovement = Mathf.Max(legMovement, difference);
                else if (difference > 0.002f) throw new InvalidOperationException("Generated loop changes the held upper-body pose.");
                if (Mathf.Approximately(time, FallLength) && difference > 0.002f)
                    throw new InvalidOperationException("Generated fall pose does not close its sampled seam.");
            }
        }
        if (legMovement < 0.001f) throw new InvalidOperationException("Generated loop curve bindings produced no visible leg motion.");
        Debug.Log("[PlungeSetup] Derived loop checks PASS: moving legs, held upper body/pelvis, zero root delta and closed seam. Hero retargeted appearance still requires live review.");
    }

    private static AnimationClip SaveClipInPlace(AnimationClip generated, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (existing == null) { AssetDatabase.CreateAsset(generated, path); return generated; }
        EditorUtility.CopySerialized(generated, existing);
        UnityEngine.Object.DestroyImmediate(generated);
        EditorUtility.SetDirty(existing);
        return existing;
    }

    private static void InstallState(AnimatorStateMachine sm, string name, AnimationClip clip, float speed)
    {
        var state = sm.states.Select(s => s.state).FirstOrDefault(s => s.name == name) ?? sm.AddState(name);
        state.motion = clip; state.speed = speed;
        // AttackController owns every phase boundary and recovery release.
        foreach (var transition in state.transitions.ToArray()) state.RemoveTransition(transition);
    }

    public static void RepairOwnedParticles(GameObject root, string id, GameObject vendor = null)
    {
        EnsureFolder(FxFolder);
        var sourceRenderers = vendor != null ? vendor.GetComponentsInChildren<ParticleSystemRenderer>(true) : Array.Empty<ParticleSystemRenderer>();
        var renderers = root.GetComponentsInChildren<ParticleSystemRenderer>(true);
        for (var index = 0; index < renderers.Length; index++)
        {
            var renderer = renderers[index];
            var paleEmbers = renderer.name.Contains("Ember") || renderer.name.Contains("Spark");
            if (paleEmbers)
            {
                var particles = renderer.GetComponent<ParticleSystem>();
                if (particles != null)
                {
                    var main = particles.main; main.startColor = new Color(1f, 0.82f, 0.74f, 0.75f);
                }
            }
            var materials = renderer.sharedMaterials;
            if (materials.Length < 2) Array.Resize(ref materials, 2);
            for (var slot = 0; slot < materials.Length; slot++)
            {
                var source = materials[slot];
                if (source == null && index < sourceRenderers.Length)
                {
                    var sourceMaterials = sourceRenderers[index].sharedMaterials;
                    if (slot < sourceMaterials.Length) source = sourceMaterials[slot];
                }
                materials[slot] = EnsureParticleMaterial(id + "_" + index + "_" + slot, source, id.Contains("Smoke"), paleEmbers);
            }
            renderer.sharedMaterials = materials;
            renderer.trailMaterial = materials[1];
            // Keep authored streams, adding only those required by URP Unlit.
            var streams = new List<ParticleSystemVertexStream>();
            renderer.GetActiveVertexStreams(streams);
            foreach (var stream in new[] { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV })
                if (!streams.Contains(stream)) streams.Add(stream);
            renderer.SetActiveVertexStreams(streams);
        }
    }

    private static Material EnsureParticleMaterial(string id, Material source, bool smoke, bool pale)
    {
        var path = FxFolder + "/" + id + ".mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) throw new InvalidOperationException("URP particle shader unavailable.");
        if (material == null)
        {
            material = source != null ? new Material(source) : new Material(shader);
            material.name = id; AssetDatabase.CreateAsset(material, path);
        }
        var texture = source != null && source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap") : null;
        if (texture == null && source != null && source.HasProperty("_MainTex")) texture = source.GetTexture("_MainTex");
        if (texture == null && material.HasProperty("_BaseMap")) texture = material.GetTexture("_BaseMap");
        if (texture == null) texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/ThirdParty/Synty/PolygonParticles/Textures/PolygonParticles_Soft_Spot.png");
        material.shader = shader;
        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", smoke ? new Color(0.035f, 0.01f, 0.016f, 0.3f) :
            pale ? new Color(1f, 0.8f, 0.72f, 0.55f) : new Color(0.8f, 0.035f, 0.065f, 0.55f));
        material.SetFloat("_Surface", 1f); material.SetFloat("_Blend", 0f);
        material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        material.SetFloat("_ZWrite", 0f); material.SetFloat("_Cull", (float)CullMode.Off);
        material.SetFloat("_SoftParticlesEnabled", 0f); material.SetFloat("_DistortionEnabled", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.DisableKeyword("_DISTORTION_ON");
        material.SetOverrideTag("RenderType", "Transparent"); material.renderQueue = 3000;
        EditorUtility.SetDirty(material);
        return material;
    }

    public static GameObject EnsurePlungeImpact()
    {
        EnsureFolder(FxFolder);
        var exists = AssetDatabase.LoadAssetAtPath<GameObject>(ImpactPath) != null;
        var root = exists ? PrefabUtility.LoadPrefabContents(ImpactPath) : new GameObject("FX_CrimsonPlungeImpact");
        try
        {
            if (!exists)
                foreach (var entry in new[] { ("FX_GroundCrack_Blast_01", 0.25f), ("FX_Impact_Dirt_01", 0.65f), ("FX_Sparks_01", 0.4f) })
                {
                    var source = FindPrefab(entry.Item1);
                    if (source == null) continue;
                    var child = UnityEngine.Object.Instantiate(source, root.transform);
                    child.transform.localPosition = Vector3.zero; child.transform.localRotation = Quaternion.identity;
                    child.transform.localScale = Vector3.one * entry.Item2;
                }
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var sparks = ps.name.Contains("Spark");
                var main = ps.main;
                main.loop = false; main.duration = 0.25f; main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
                main.startDelay = 0f; main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 3.5f);
                main.startSize3D = false; main.gravityModifier = 0.4f;
                main.startSize = new ParticleSystem.MinMaxCurve(sparks ? 0.02f : 0.25f, sparks ? 0.07f : 2.0f);
                main.startColor = sparks ? new Color(1f, 0.82f, 0.74f, 0.9f) : new Color(0.55f, 0.02f, 0.045f, 0.7f);
                main.maxParticles = 24;
                var velocity = ps.velocityOverLifetime; velocity.enabled = false;
                var force = ps.forceOverLifetime; force.enabled = false;
                var noise = ps.noise; noise.enabled = false;
                var sizing = ps.sizeOverLifetime; sizing.enabled = false;
                var trails = ps.trails; trails.enabled = false;
                var shape = ps.shape;
                shape.radius = Mathf.Min(shape.radius, 1.25f);
                var emission = ps.emission; emission.rateOverTime = 0f; emission.rateOverDistance = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)(sparks ? 8 : 3)) });
            }
            RepairOwnedParticles(root, "PlungeImpact");
            return PrefabUtility.SaveAsPrefabAsset(root, ImpactPath);
        }
        finally
        {
            if (exists) PrefabUtility.UnloadPrefabContents(root);
            else UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static GameObject FindPrefab(string name)
    {
        foreach (var guid in AssetDatabase.FindAssets(name + " t:Prefab", new[] { "Assets/ThirdParty/Synty/PolygonParticles" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (System.IO.Path.GetFileNameWithoutExtension(path) == name) return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        return null;
    }

    private static void SetObject(SerializedObject data, string name, UnityEngine.Object value)
    { var property = data.FindProperty(name); if (property != null) property.objectReferenceValue = value; }
    private static void SetFloat(SerializedObject data, string name, float value)
    { var property = data.FindProperty(name); if (property != null) property.floatValue = value; }
    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent); AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
}
