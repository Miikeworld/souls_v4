using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ProjectRestartStrideCalibration
{
    public readonly struct Result
    {
        public readonly float SlowWalkSpeed, WalkSpeed, RunSpeed;
        public readonly Vector4 CycleDurations;
        public readonly Bounds RestBounds;

        public Result(float slowWalkSpeed, float walkSpeed, float runSpeed, Vector4 cycleDurations, Bounds restBounds)
        {
            SlowWalkSpeed = slowWalkSpeed;
            WalkSpeed = walkSpeed;
            RunSpeed = runSpeed;
            CycleDurations = cycleDurations;
            RestBounds = restBounds;
        }
    }

    public static Result Measure(GameObject heroPrefab, AnimatorController controller)
    {
        Require(!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating, "Wait for Edit Mode and completed compilation/import.");
        Require(heroPrefab != null && PrefabUtility.IsPartOfPrefabAsset(heroPrefab) && controller != null, "Hero prefab asset and controller are required.");
        var names = new[] { "Idle_Wait_A", "Mvm_Walk", "Mvm_Jog", "Mvm_Dash", "Esc_Slide_All_Long_Root" };
        var guids = new[] { "4f9b2cc802bffcd468bf59be5599a8d1", "6e950ce0deb6c22419d4f264e8441f3b", "2052e59abeb75e741848e7102f47a677", "82aaa36036bdcb246bdc018a9b15e3f0", "313b29954b78fba42866cb5b4ac69109" };
        var clips = new AnimationClip[5];
        for (var i = 0; i < clips.Length; i++)
        {
            var matches = controller.animationClips.Where(clip => clip != null && clip.name == names[i]).Distinct().ToArray();
            Require(matches.Length == 1, "Expected exactly one controller clip named " + names[i]);
            clips[i] = matches[0];
            Require(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(clips[i])) == guids[i], "Unexpected source for " + names[i]);
            Require(clips[i].isHumanMotion && !clips[i].legacy && Finite(clips[i].length) && clips[i].length > 0f && clips[i].length <= 100f, "Invalid cycle: " + names[i]);
        }
        Require(controller.layers.Length == 1 && controller.layers[0].name == "Base Layer", "Expected only Base Layer.");
        foreach (var (name, expectedSpeed) in new[] { ("Locomotion", 1f), ("Dash", ProjectRestartLocomotion.DashStateSpeed) })
        {
            var state = controller.layers[0].stateMachine.states.Select(item => item.state).SingleOrDefault(item => item.name == name);
            Require(state != null && state.iKOnFeet && Mathf.Approximately(state.speed, expectedSpeed) && !state.speedParameterActive, "Expected Foot IK state at configured speed: " + name);
        }
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            VerifyScaledBounds(scene);
            var instance = PrefabUtility.InstantiatePrefab(heroPrefab, scene) as GameObject;
            Require(instance != null && instance.activeInHierarchy, "Could not instantiate an active Hero preview.");
            Require((instance.transform.localScale - Vector3.one).sqrMagnitude < 0.000001f, "Hero root must retain its original unit scale.");
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var animator = instance.GetComponent<Animator>();
            Require(animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman, "Hero requires a valid root Humanoid Animator.");
            animator.enabled = true;
            animator.applyRootMotion = false;
            animator.fireEvents = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = controller;
            animator.speed = 1f;
            animator.Rebind();
            animator.Update(0f);
            animator.SetFloat("Speed", 0f);
            Pose(animator, "Locomotion", 0f, clips[0]);
            var bounds = BakeBounds(instance);
            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            var leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            Debug.Log($"[ProjectRestart] Hero idle bounds: height={bounds.size.y:F4} m; min={bounds.min.ToString("F4")}; max={bounds.max.ToString("F4")}; headY={head.position.y:F4}; footY L/R={leftFoot.position.y:F4}/{rightFoot.position.y:F4}; humanScale={animator.humanScale:F4}. Bounds use scale-compensated baked vertices transformed to world space once.");
            VerifyBoundsHandling(instance, bounds);
            var slowWalk = Estimate(animator, clips[1], "Locomotion", 1f / 3f);
            var walk = Estimate(animator, clips[2], "Locomotion", 2f / 3f);
            var run = Estimate(animator, clips[3], "Locomotion", 1f);
            Require(slowWalk < walk && walk < run, $"Stride ordering failed: slowWalk={slowWalk:F4}, walk={walk:F4}, run={run:F4} m/s.");
            Debug.Log($"[ProjectRestart] Hero stride estimates: slowWalk={slowWalk:F3}, walk={walk:F3}, run={run:F3} m/s; cycles Idle/SlowWalk/Walk/Run={clips[0].length:F3}/{clips[1].length:F3}/{clips[2].length:F3}/{clips[3].length:F3} s. Low-foot backward-velocity medians, not measured root motion; slide stride is not measured. Visual verification of retargeting and foot sliding is still required.", heroPrefab);
            return new Result(slowWalk, walk, run, new Vector4(clips[0].length, clips[1].length, clips[2].length, clips[3].length), bounds);
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    private static void Pose(Animator animator, string state, float phase, AnimationClip clip)
    {
        var hash = Animator.StringToHash("Base Layer." + state);
        animator.Play(hash, 0, phase);
        animator.Update(0f);
        var info = animator.GetCurrentAnimatorStateInfo(0);
        Require(info.fullPathHash == hash && !animator.IsInTransition(0) && Mathf.Abs(info.normalizedTime - phase) < 0.001f, "Unexpected state/transition/phase sampling " + clip.name);
        Require(animator.GetCurrentAnimatorClipInfo(0).Any(item => item.clip == clip && item.weight > 0.99f) && Finite(info.length) && info.length > 0f, "Expected unblended clip active in state: " + clip.name);
        var root = animator.transform;
        Require(root.position.sqrMagnitude < 0.000001f && Quaternion.Angle(root.rotation, Quaternion.identity) < 0.01f && (root.localScale - Vector3.one).sqrMagnitude < 0.000001f, "Sampling displaced or rescaled the Hero root: " + clip.name);
    }

    private static float Estimate(Animator animator, AnimationClip clip, string state, float blend)
    {
        var count = Mathf.Max(32, Mathf.CeilToInt(clip.length * 120f));
        var minimum = 0;
        var contacts = new int[2];
        var feet = new[] { animator.GetBoneTransform(HumanBodyBones.LeftFoot), animator.GetBoneTransform(HumanBodyBones.RightFoot) };
        var hip = animator.GetBoneTransform(HumanBodyBones.Hips);
        var positions = new Vector3[2, count];
        var hipPositions = hip != null ? new Vector3[count] : null;
        animator.SetFloat("Speed", blend);
        for (var i = 0; i < count; i++)
        {
            Pose(animator, state, (float)i / count, clip);
            for (var foot = 0; foot < 2; foot++)
            {
                if (feet[foot] != null)
                {
                    positions[foot, i] = feet[foot].position;
                    Require(Finite(positions[foot, i]), $"Nonfinite foot position: {clip.name}, foot={foot}, sample={i}");
                }
            }
            if (hip != null) hipPositions[i] = hip.position;
        }

        if (feet.All(foot => foot != null))
        {
            var velocities = new List<float>();
            for (var foot = 0; foot < 2; foot++)
            {
                var heights = new float[count];
                for (var i = 0; i < count; i++) heights[i] = positions[foot, i].y;
                Array.Sort(heights);
                var low = heights[Mathf.FloorToInt((count - 1) * 0.4f)];
                for (var i = 1; i < count; i++)
                {
                    var previous = positions[foot, i - 1];
                    var current = positions[foot, i];
                    var speed = (previous.z - current.z) * count / clip.length;
                    if (!Finite(speed) || speed >= 20f || previous.y > low || current.y > low || speed <= 0.01f) continue;
                    velocities.Add(speed);
                    contacts[foot]++;
                }
            }
            minimum = Mathf.Max(6, Mathf.CeilToInt(count * 0.03f));
            if (contacts.All(value => value >= minimum))
            {
                velocities.Sort();
                var median = (velocities[(velocities.Count - 1) / 2] + velocities[velocities.Count / 2]) * 0.5f;
                if (Finite(median) && median > 0f && median < 20f)
                {
                    Debug.Log($"[ProjectRestart] {clip.name}: median={median:F4} m/s, low/backward samples L/R={contacts[0]}/{contacts[1]}, cycle={clip.length:F4}s, samples={count}; loop-wrap interval excluded.");
                    return median;
                }
            }
        }

        if (hip == null)
        {
            if (feet.Any(foot => foot == null))
                throw new InvalidOperationException($"Cannot calibrate {clip.name}: missing foot bones and no hip bone.");
            throw new InvalidOperationException($"[ProjectRestart] Stride calibration: Insufficient backward low-foot samples: {clip.name}, left={contacts[0]}, right={contacts[1]}, required per foot={minimum}/{count}; no speed inferred.");
        }

        var hipVelocities = new List<float>();
        for (var i = 1; i < count; i++)
        {
            var previous = hipPositions[i - 1];
            var current = hipPositions[i];
            var speed = (previous.z - current.z) * count / clip.length;
            if (!Finite(speed) || speed >= 20f || speed <= 0.01f) continue;
            hipVelocities.Add(speed);
        }
        if (hipVelocities.Count < 6)
            throw new InvalidOperationException($"[ProjectRestart] Stride calibration: {clip.name} has no foot or hip backward samples (left={contacts[0]}, right={contacts[1]}, hip={hipVelocities.Count}, required per foot={minimum}/{count}).");
        hipVelocities.Sort();
        var hipMedian = (hipVelocities[(hipVelocities.Count - 1) / 2] + hipVelocities[hipVelocities.Count / 2]) * 0.5f;
        Debug.LogWarning($"[ProjectRestart] {clip.name}: foot-contact calibration failed; using hip/body fallback median={hipMedian:F4} m/s, hip samples={hipVelocities.Count}, cycle={clip.length:F4}s. Verify locomotion foot sliding visually in Play Mode.");
        return hipMedian;
    }

    public static bool HasGeometry(SkinnedMeshRenderer renderer)
    {
        return renderer.sharedMesh != null && renderer.sharedMesh.vertexCount > 0;
    }

    public static SkinnedMeshRenderer[] FindMissingGeometry(GameObject root)
    {
        return root.GetComponentsInChildren<SkinnedMeshRenderer>(false)
            .Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy && !renderer.forceRenderingOff && !HasGeometry(renderer)).ToArray();
    }

    private static Bounds BakeBounds(GameObject instance, bool reportMissing = true)
    {
        var bounds = new Bounds();
        var hasVertices = false;
        var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            foreach (var renderer in instance.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff || !HasGeometry(renderer)) continue;
                mesh.Clear();
                renderer.BakeMesh(mesh, true);
                Require(mesh.vertexCount > 0, "Nonempty Hero mesh baked no vertices: " + renderer.name);
                foreach (var vertex in mesh.vertices)
                {
                    var world = renderer.transform.TransformPoint(vertex);
                    Require(Finite(world), "Nonfinite baked Hero vertex: " + renderer.name);
                    if (!hasVertices) bounds = new Bounds(world, Vector3.zero);
                    else bounds.Encapsulate(world);
                    hasVertices = true;
                }
            }
            Require(hasVertices, "No visible active enabled Hero skinned meshes with vertices at idle.");
            if (reportMissing)
            {
                var missing = FindMissingGeometry(instance);
                if (missing.Length > 0)
                {
                    var paths = missing.Take(6).Select(renderer => AnimationUtility.CalculateTransformPath(renderer.transform, instance.transform));
                    Debug.LogWarning("[ProjectRestart] Bounds excluded " + missing.Length + " active renderers with missing/empty meshes: " + string.Join(", ", paths) + (missing.Length > 6 ? " (first 6 shown)" : "") + ". Only rendered geometry contributes to capsule bounds; these mesh references have NOT been repaired and visual completeness remains unresolved.");
                }
            }
            return bounds;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }

    private static void VerifyScaledBounds(UnityEngine.SceneManagement.Scene scene)
    {
        var root = new GameObject("BoundsScaleRegression") { hideFlags = HideFlags.HideAndDontSave };
        var source = new Mesh { name = "BoundsScaleRegressionMesh", hideFlags = HideFlags.HideAndDontSave };
        try
        {
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.SetPositionAndRotation(new Vector3(3f, 1f, -2f), Quaternion.Euler(0f, 37f, 0f));
            var bone = new GameObject("Bone") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(bone, scene);
            bone.transform.SetParent(root.transform, false);
            var geometry = new GameObject("Geometry") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(geometry, scene);
            geometry.transform.SetParent(root.transform, false);
            var renderer = geometry.AddComponent<SkinnedMeshRenderer>();
            renderer.bones = new[] { bone.transform };
            renderer.rootBone = bone.transform;
            renderer.quality = SkinQuality.Bone1;
            foreach (var scale in new[] { 1f, 0.01f, 2f })
            {
                renderer.sharedMesh = null;
                root.transform.localScale = Vector3.one * scale;
                bone.transform.localPosition = Vector3.zero;
                var vertices = new[]
                {
                    new Vector3(-0.25f, 0f, 0f) / scale,
                    new Vector3(0.25f, 0f, 0f) / scale,
                    new Vector3(-0.25f, 2f, 0f) / scale,
                    new Vector3(0.25f, 2f, 0f) / scale
                };
                source.Clear();
                source.vertices = vertices;
                source.triangles = new[] { 0, 2, 1, 1, 2, 3 };
                source.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
                source.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, 4).ToArray();
                source.bindposes = new[] { bone.transform.worldToLocalMatrix * geometry.transform.localToWorldMatrix };
                source.RecalculateBounds();
                renderer.sharedMesh = source;
                bone.transform.localPosition = Vector3.up * (0.25f / scale);
                var skinToWorld = bone.transform.localToWorldMatrix * source.bindposes[0];
                var expected = new Bounds(skinToWorld.MultiplyPoint3x4(vertices[0]), Vector3.zero);
                foreach (var vertex in vertices)
                    expected.Encapsulate(skinToWorld.MultiplyPoint3x4(vertex));
                var actual = BakeBounds(root, false);
                Require((actual.min - expected.min).sqrMagnitude < 0.000001f && (actual.max - expected.max).sqrMagnitude < 0.000001f,
                    $"Scale regression failed at {scale:F4}: expected min/max={expected.min.ToString("F4")}/{expected.max.ToString("F4")}, actual={actual.min.ToString("F4")}/{actual.max.ToString("F4")}; do not resize the Hero or relax the height limit.");
            }
            Debug.Log("[ProjectRestart] Scaled bounds regression PASS: known two-metre skinned geometry at parent scales 1, 0.01 and 2 matches independent bone/bind-pose world bounds, including translation, rotation and bone movement.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(source);
        }
    }

    private static void VerifyBoundsHandling(GameObject instance, Bounds expected)
    {
        var empty = new GameObject("BoundsRegressionEmptyRenderer") { hideFlags = HideFlags.HideAndDontSave };
        var emptyMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(empty, instance.scene);
            empty.transform.SetParent(instance.transform, false);
            var renderer = empty.AddComponent<SkinnedMeshRenderer>();
            foreach (var fixture in new Mesh[] { null, emptyMesh })
            {
                renderer.sharedMesh = fixture;
                var actual = BakeBounds(instance, false);
                Require((actual.min - expected.min).sqrMagnitude < 0.000001f && (actual.max - expected.max).sqrMagnitude < 0.000001f, "A meshless/empty renderer changed the rendered bounds.");
                var rejected = false;
                try
                {
                    BakeBounds(empty, false);
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("No visible active enabled Hero skinned meshes with vertices at idle."))
                {
                    rejected = true;
                }
                Require(rejected, "Bounds calculation accepted a character with no usable geometry.");
            }
            Debug.Log("[ProjectRestart] Bounds regression PASS: missing/empty renderers do not affect usable geometry bounds; entirely meshless characters are rejected. Source meshes were not changed.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(empty);
            UnityEngine.Object.DestroyImmediate(emptyMesh);
        }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[ProjectRestart] Stride calibration: " + message);
    }
}
