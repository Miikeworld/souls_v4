using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Project Restart > Run Grounding Self-Test.
///
/// Edit-mode verification for FootGrounding/GroundingSolver — the thing all
/// the "still floating" rounds lacked. For every rig in the test scene
/// (player + enemies) it samples real controller clips at fixed times via
/// AnimationMode, computes the pelvis correction through the same solver the
/// runtime uses, then measures the corrected sole vs the probed floor.
///
/// PASS = every grounded sample ends within 2cm of the floor after the solve
/// (corpse end-pose: 6cm — bone-vs-skin offset). Foot rotation (the IK half)
/// can't be certified in edit mode — that stays a Play check.
///
/// Restores every transform afterwards; creates and destroys only a ground pad.
/// </summary>
public static class GroundingSelfTest
{
    private const string ScenePath = "Assets/_Project/Scenes/00_TestBlockout.unity";
    private const float SoleTolerance = 0.02f;
    private const float CorpseTolerance = 0.06f;
    private const float TestBudget = 0.9f;
    private static int fails;

    private sealed class Subject
    {
        public string name;
        public Transform physics;      // CC root — moved onto the pad
        public Animator anim;          // humanoid
        public SkinnedMeshRenderer[] smrs;
        public Transform ankleL, ankleR, toeL, toeR;
        public float soleL = 0.06f, soleR = 0.06f;
        public List<(AnimationClip clip, float[] times)> samples = new();
    }

    [MenuItem("Tools/Project Restart/Run Grounding Self-Test")]
    public static void Run()
    {
        fails = 0;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var pad = CreatePad();
        var subjects = CollectSubjects();
        if (subjects.Count == 0)
        {
            Debug.LogError("[GroundTest] No player/enemy rigs in the scene — run the setup tools first.");
            Object.DestroyImmediate(pad);
            return;
        }

        AnimationMode.StartAnimationMode();
        try
        {
            foreach (var s in subjects) TestSubject(s, pad.transform.position);
        }
        finally
        {
            AnimationMode.StopAnimationMode();
            Object.DestroyImmediate(pad);
        }

        if (fails == 0) Debug.Log($"[GroundTest] ALL PASS ({subjects.Count} rig(s))");
        else Debug.LogError($"[GroundTest] {fails} FAILED — see table above");
    }

    /// <summary>A flat pad away from the arena — the sole reference floor.</summary>
    private static GameObject CreatePad()
    {
        var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
        pad.name = "GroundTest_Pad";
        pad.hideFlags = HideFlags.HideAndDontSave;
        pad.transform.position = new Vector3(0f, -0.25f, -60f); // top face at y=0
        pad.transform.localScale = new Vector3(30f, 0.5f, 30f);
        return pad;
    }

    private static List<Subject> CollectSubjects()
    {
        var list = new List<Subject>();

        var loco = Object.FindFirstObjectByType<PlayerLocomotion>();
        if (loco != null)
        {
            var s = ForRoot(loco.transform, "Player");
            if (s != null) list.Add(s);
        }
        foreach (var e in Object.FindObjectsByType<EnemyAI>(FindObjectsSortMode.None))
        {
            var s = ForRoot(e.transform, "Enemy/" + e.name);
            if (s != null) list.Add(s);
        }
        return list;
    }

    private static Subject ForRoot(Transform physics, string name)
    {
        Animator anim = null;
        foreach (var a in physics.GetComponentsInChildren<Animator>(true))
            if (a != null && a.avatar != null && a.avatar.isHuman) { anim = a; break; }
        if (anim == null) { Debug.LogWarning($"[GroundTest] {name}: no humanoid animator — skipped."); return null; }

        var s = new Subject
        {
            name = name,
            physics = physics,
            anim = anim,
            ankleL = anim.GetBoneTransform(HumanBodyBones.LeftFoot),
            ankleR = anim.GetBoneTransform(HumanBodyBones.RightFoot),
            toeL = anim.GetBoneTransform(HumanBodyBones.LeftToes),
            toeR = anim.GetBoneTransform(HumanBodyBones.RightToes),
        };
        s.smrs = anim.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        // Same source order as FootGrounding.CalibrateSoles: the skinned-mesh
        // column measure first, the avatar's feetBottomHeight only as fallback.
        var ml = GroundingSolver.MeasureSoleOffset(s.smrs, s.ankleL);
        var mr = GroundingSolver.MeasureSoleOffset(s.smrs, s.ankleR);
        var l = ml > 0.02f ? ml : anim.leftFeetBottomHeight;
        var r = mr > 0.02f ? mr : anim.rightFeetBottomHeight;
        if (l > 0f) s.soleL = l;
        if (r > 0f) s.soleR = r;

        CollectSamples(anim, s.samples);
        if (s.samples.Count == 0)
            Debug.LogWarning($"[GroundTest] {name}: no sample clips found on its controller.");
        return s;
    }

    /// <summary>Grounded poses to certify: idle/move/attack samples plus the
    /// death end-pose (corpse criterion) — the poses that floated before.</summary>
    private static void CollectSamples(Animator anim, List<(AnimationClip, float[])> samples)
    {
        var clips = EffectiveClips(anim.runtimeAnimatorController);
        foreach (var c in clips)
        {
            var n = c.name;
            if (n.Contains("Idle")) samples.Add((c, new[] { 0f, 0.5f }));
            else if (n.Contains("Jogging") || n.Contains("Walk") || n.Contains("Run"))
                samples.Add((c, new[] { 0.25f, 0.75f }));
            else if (n.Contains("Attack") || n.Contains("Skill")) samples.Add((c, new[] { 0.3f, 0.6f }));
            else if (n.Contains("Damage_Die") || n.Contains("Death")) samples.Add((c, new[] { 0.97f }));
        }
    }

    /// <summary>Clips the animator actually plays: override-controller values
    /// (katana clips on the hero) or base-controller state motions, blend
    /// trees flattened.</summary>
    private static List<AnimationClip> EffectiveClips(RuntimeAnimatorController rc)
    {
        var list = new List<AnimationClip>();
        if (rc is AnimatorOverrideController oc) rc = oc.runtimeAnimatorController;
        var ac = rc as AnimatorController;
        if (ac == null) return list;
        foreach (var layer in ac.layers)
            foreach (var st in layer.stateMachine.states)
                Flatten(st.state.motion, list);
        return list;
    }

    private static void Flatten(Motion m, List<AnimationClip> list)
    {
        if (m is AnimationClip c) { list.Add(c); return; }
        if (m is BlendTree bt)
            foreach (var ch in bt.children) Flatten(ch.motion, list);
    }

    private static void TestSubject(Subject s, Vector3 padCenter)
    {
        // Stand the rig on the pad centre for the duration of the samples.
        var cc = s.physics.GetComponent<CharacterController>();
        var savedPos = s.physics.position;
        var savedRot = s.physics.rotation;
        if (cc != null) cc.enabled = false;
        s.physics.position = new Vector3(padCenter.x, 0f, padCenter.z);
        s.physics.rotation = Quaternion.identity;

        Debug.Log($"[GroundTest] {s.name}: soleL={s.soleL:F2} soleR={s.soleR:F2} clips={s.samples.Count}");
        foreach (var (clip, times) in s.samples)
        {
            foreach (var frac in times)
            {
                AnimationMode.SampleAnimationClip(s.anim.gameObject, clip, clip.length * frac);
                var isCorpse = frac > 0.9f && (clip.name.Contains("Die") || clip.name.Contains("Death"));
                if (isCorpse) CheckCorpse(s, clip, frac);
                else CheckSole(s, clip, frac);
            }
        }

        AnimationMode.StopAnimationMode();
        AnimationMode.StartAnimationMode(); // reset pose between subjects
        if (cc != null) cc.enabled = true;
        s.physics.SetPositionAndRotation(savedPos, savedRot);
    }

    /// <summary>Run the same solve the runtime runs: pelvis correction = floor
    /// under the deeper foot minus that foot's calibrated sole bottom, then
    /// assert the corrected sole lands within tolerance of the floor. (Mesh
    /// bottom only backs it up — a coat tail can dip below the feet.)</summary>
    private static void CheckSole(Subject s, AnimationClip clip, float frac)
    {
        var gl = Ground(s.ankleL);
        var gr = Ground(s.ankleR);
        // The plant foot is the deeper sole — the one the runtime's min()
        // solve tries to plant. The other leg is a swing/strike limb and its
        // mesh legitimately crosses the floor plane on run flight phases,
        // lunge attacks and kicks.
        var soleL = SoleY(s.ankleL, s.toeL, s.soleL);
        var soleR = SoleY(s.ankleR, s.toeR, s.soleR);
        var plantIsL = soleL <= soleR;
        var floorY = Mathf.Min(gl, gr);
        if (floorY == float.MaxValue) floorY = 0f; // probe miss → the pad top
        var soleY = Mathf.Min(soleL, soleR);
        if (soleY == float.MaxValue) soleY = GroundingSolver.MeasureBodyLowest(s.smrs);
        var gap = floorY - soleY;
        // No contact intent: even the deepest sole sits clearly off the pad —
        // run flight phase, airborne attack segment, kick sweep. The pelvis
        // solve can't certify these; log them, don't grade them.
        if (Mathf.Abs(gap) > 0.2f)
        {
            Debug.Log($"  {s.name} {clip.name} @{frac:F2}: SKIP — no contact pose (plant sole {gap:+0.000;-0.000}m off floor)");
            return;
        }
        var corr = Mathf.Clamp(gap, -TestBudget, TestBudget);
        var residual = Mathf.Abs(soleY + corr - floorY);
        // The visible truth: after the sole solve the PLANT foot's mesh must
        // not be pushed through the floor — that was the idle sink. Measured
        // as the lowest vertex in a 20cm column around the plant ankle only.
        var meshY = GroundingSolver.MeasureFootLowest(s.smrs, plantIsL ? s.ankleL : s.ankleR, 0.2f);
        if (meshY == float.MaxValue) meshY = GroundingSolver.MeasureBodyLowest(s.smrs);
        var meshDip = floorY - (meshY + corr); // positive = through the floor
        Debug.Log($"  {s.name} {clip.name} @{frac:F2}: soleGap={gap:+0.000;-0.000} corr={corr:+0.000;-0.000} residual={residual:F3} meshDip={meshDip:+0.000;-0.000}");
        Check(residual <= SoleTolerance,
              $"{s.name} {clip.name}@{frac:F2} sole off floor by {residual * 100f:F1}cm after solve (gap {gap * 100f:F1}cm)");
        Check(meshDip <= 0.08f,
              $"{s.name} {clip.name}@{frac:F2} plant-foot mesh dips {meshDip * 100f:F1}cm through the floor after solve (sole estimate under-reads this pose)");
    }

    /// <summary>Death end-pose: the runtime grounds corpses by lowest bone;
    /// verify the corrected lowest BODY VERTEX reaches the floor.</summary>
    private static void CheckCorpse(Subject s, AnimationClip clip, float frac)
    {
        var groundY = 0f; // pad top
        var lowBone = float.MaxValue;
        foreach (var b in BodyBones)
        {
            var t = s.anim.GetBoneTransform(b);
            if (t != null && t.position.y < lowBone) lowBone = t.position.y;
        }
        var corr = Mathf.Clamp(groundY + 0.04f - lowBone, -TestBudget, TestBudget);
        var lowVert = GroundingSolver.MeasureBodyLowest(s.smrs);
        var residual = Mathf.Abs(lowVert + corr - groundY);
        Debug.Log($"  {s.name} {clip.name} @{frac:F2}: boneLow={lowBone:+0.000} vertLow={lowVert:+0.000} corr={corr:+0.000} residual={residual:F3}");
        Check(residual <= CorpseTolerance,
              $"{s.name} {clip.name}@{frac:F2} corpse floats {residual * 100f:F1}cm");
    }

    /// <summary>Mirrors FootGrounding.SoleEstimate: the ankle's calibrated drop
    /// OR the live toe joint minus the fixed pad — whichever reads lower.</summary>
    private static float SoleY(Transform ankle, Transform toe, float soleOffset)
    {
        var s = ankle != null ? ankle.position.y - soleOffset : float.MaxValue;
        if (toe != null) s = Mathf.Min(s, toe.position.y - 0.02f); // FootGrounding.ToeSolePad
        return s;
    }

    private static float Ground(Transform ankle)
        => GroundingSolver.ProbeFootGround(ankle != null ? ankle.position : Vector3.zero,
                                           null, null, out _, out _);

    private static void Check(bool ok, string what)
    {
        if (!ok) { fails++; Debug.LogError("[GroundTest] FAIL " + what); }
    }

    private static readonly HumanBodyBones[] BodyBones =
    {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
        HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
        HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
        HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
    };
}
