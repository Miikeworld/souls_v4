using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>Watch the runtime-equivalent Hero blade beside the untouched authored source reference.</summary>
public sealed class CrowdSkillPreview : EditorWindow
{
    private WeaponArt[] arts;
    private Vector2 scroll;
    private int selected;
    private float time, contactStart, palmGrip = 0.55f;
    private bool playing, advanced;
    private double previous;
    private PreviewRenderUtility preview;
    private GameObject hero, shadow, heroSword, shadowSword;
    private Animator heroAnimator;
    private Transform gun, sourceHand, heroHand, heroKnuckle;
    private AnimationClip clip;
    private WeaponSet bladeSet;
    private Editor inspector;
    private float angularError, gripError, lengthError;
    private string diagnostic = "";

    [MenuItem("Tools/Project Restart/Review Big Sword Contacts (Hero + Shadow)")]
    public static void Open() => GetWindow<CrowdSkillPreview>("Big Sword animation preview");

    [MenuItem("Tools/Project Restart/Check All Big Sword Blade Poses")]
    public static void CheckAllPoses()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        { Debug.LogWarning("[BladePoses] Exit Play Mode and wait for compilation before checking isolated poses."); return; }
        var window = GetWindow<CrowdSkillPreview>("Big Sword animation preview");
        window.LoadArts();
        window.EvaluateAllPoses();
    }

    private void OnEnable() { LoadArts(); previous = EditorApplication.timeSinceStartup; }
    private void LoadArts()
    {
        arts = AssetDatabase.FindAssets("t:WeaponArt", new[] { "Assets/_Project/Combat/Arts" })
            .Select(g => AssetDatabase.LoadAssetAtPath<WeaponArt>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(a => a != null && a.visualTheme == ArtVisualTheme.DarkCrimson && a.contactMode != ArtContactMode.ProjectileOnly)
            .OrderBy(a => StateOrder(a.stateName)).ToArray();
        selected = Mathf.Clamp(selected, 0, Mathf.Max(0, arts.Length - 1));
    }
    private static int StateOrder(string name)
        => !string.IsNullOrEmpty(name) && name.StartsWith("Art") && int.TryParse(name.Substring(3), out var order) ? order : int.MaxValue;

    private void Update()
    {
        var now = EditorApplication.timeSinceStartup;
        if (playing && clip != null && arts != null && selected < arts.Length)
        { time = Mathf.Repeat(time + (float)(now - previous) / Mathf.Max(0.1f, arts[selected].duration), 1f); Repaint(); }
        previous = now;
    }

    private void OnGUI()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        { EditorGUILayout.HelpBox("Exit Play Mode to watch isolated animation previews.", MessageType.Info); return; }
        if (arts == null || arts.Length == 0)
        { EditorGUILayout.HelpBox("Run Setup Fast Crowd Combat first.", MessageType.Info); return; }

        scroll = EditorGUILayout.BeginScrollView(scroll);
        var next = EditorGUILayout.Popup("Skill", selected, arts.Select(a => a.artName).ToArray());
        if (next != selected || preview == null) { selected = next; CreatePreview(); }
        EditorGUILayout.LabelField(selected + 1 + "/" + arts.Length + ": Hero left • authored source right");
        if (!Ready)
        {
            EditorGUILayout.HelpBox("Open the test scene with the Hero and its source shadow rig, then reopen this window.", MessageType.Warning);
            EditorGUILayout.EndScrollView(); return;
        }

        playing = EditorGUILayout.Toggle("Play at tuned speed", playing);
        time = EditorGUILayout.Slider("Animation position", time, 0f, 1f);
        SamplePose(time);
        var rect = GUILayoutUtility.GetRect(300f, 350f, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint)
        {
            preview.BeginPreview(rect, GUIStyle.none);
            preview.camera.transform.position = new Vector3(1.5f, 2f, -6f);
            preview.camera.transform.LookAt(new Vector3(1.5f, 1.1f, 0f));
            preview.camera.nearClipPlane = 0.1f; preview.camera.farClipPlane = 30f;
            preview.lights[0].intensity = 1.2f; preview.lights[0].transform.rotation = Quaternion.Euler(35f, 30f, 0f);
            preview.Render(); GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.StretchToFill, false);
        }
        EditorGUILayout.LabelField("Blade check", "rotation " + angularError.ToString("F2") + "° • grip offset " +
            (gripError * 100f).ToString("F1") + " cm • length difference " + (lengthError * 100f).ToString("F1") + " cm");
        EditorGUILayout.HelpBox("This preview uses the same blade pose and size calculation as gameplay. The source sword on the right stays attached to its authored bone. Watching is enough; timing editing is optional.", MessageType.Info);
        if (GUILayout.Button("Check all 15 blade poses")) EvaluateAllPoses();
        if (!string.IsNullOrEmpty(diagnostic)) EditorGUILayout.HelpBox(diagnostic, MessageType.Info);

        advanced = EditorGUILayout.Foldout(advanced, "Advanced: edit hit and trail timing", true);
        if (advanced) DrawContactEditor();
        EditorGUILayout.EndScrollView();
    }

    private bool Ready => clip != null && hero != null && shadow != null && heroAnimator != null &&
                          heroHand != null && sourceHand != null && gun != null && heroSword != null && shadowSword != null;

    private void SamplePose(float normalizedTime)
    {
        clip.SampleAnimation(hero, normalizedTime * clip.length);
        clip.SampleAnimation(shadow, normalizedTime * clip.length);
        var source = BladePoseResolver.Capture(shadow.transform.rotation, gun.position, gun.rotation, sourceHand.position);
        var palm = BladePoseResolver.Palm(heroHand, heroKnuckle, palmGrip);
        var pose = BladePoseResolver.Resolve(source, hero.transform.rotation, palm, heroHand.rotation, bladeSet);
        heroSword.transform.SetPositionAndRotation(pose.position, pose.rotation);
        heroSword.transform.localScale = BladePoseResolver.LocalScale(bladeSet.handLocalScale, heroHand.lossyScale);
        // Reference has identity local pose, as in the original pack's authored socket.
        shadowSword.transform.localPosition = Vector3.zero; shadowSword.transform.localRotation = Quaternion.identity;
        shadowSword.transform.localScale = BladePoseResolver.LocalScale(bladeSet.handLocalScale, gun.lossyScale);

        var expected = hero.transform.rotation * Quaternion.Inverse(shadow.transform.rotation) * shadowSword.transform.rotation *
                       Quaternion.Euler(bladeSet.weaponRotOffset) * Quaternion.AngleAxis(bladeSet.bladeRoll, BladePoseResolver.BladeAxis(bladeSet));
        angularError = Quaternion.Angle(expected, heroSword.transform.rotation);
        gripError = Vector3.Distance(palm, heroSword.transform.position);
        lengthError = 0f;
        if (BladeGeometry.TryGet(heroSword.transform, bladeSet, out var bladeBase, out var tip))
        {
            var heroLength = Vector3.Distance(heroSword.transform.TransformPoint(bladeBase), heroSword.transform.TransformPoint(tip));
            var sourceLength = Vector3.Distance(shadowSword.transform.TransformPoint(bladeBase), shadowSword.transform.TransformPoint(tip));
            lengthError = Mathf.Abs(heroLength - sourceLength);
        }
    }

    private void DrawContactEditor()
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Mark contact start")) contactStart = time;
        if (GUILayout.Button("Append contact end") && time > contactStart)
        {
            Undo.RecordObject(arts[selected], "Author blade contact");
            var windows = arts[selected].hitWindows ?? Array.Empty<Vector2>();
            arts[selected].hitWindows = windows.Concat(new[] { new Vector2(contactStart, time) }).OrderBy(w => w.x).ToArray();
            arts[selected].trailWindows = arts[selected].hitWindows.ToArray();
            arts[selected].contactMotionReviewed = false; EditorUtility.SetDirty(arts[selected]);
        }
        if (GUILayout.Button("Clear gates"))
        {
            Undo.RecordObject(arts[selected], "Clear blade gates");
            arts[selected].hitWindows = Array.Empty<Vector2>(); arts[selected].trailWindows = Array.Empty<Vector2>();
            arts[selected].contactMotionReviewed = false; EditorUtility.SetDirty(arts[selected]);
        }
        EditorGUILayout.EndHorizontal();
        using (new EditorGUI.DisabledScope(arts[selected].hitWindows == null || arts[selected].hitWindows.Length == 0))
        {
            if (GUILayout.Button("Save visually reviewed timing"))
            {
                Undo.RecordObject(arts[selected], "Review blade contacts");
                arts[selected].contactMotionReviewed = true; EditorUtility.SetDirty(arts[selected]); AssetDatabase.SaveAssets();
            }
        }
        EditorGUILayout.HelpBox("Each separate strike needs its own interval. Pose checks never mark damage timing reviewed automatically. Runtime floor contact, travel and combat transitions require Play Mode.", MessageType.Info);
        if (inspector != null) inspector.OnInspectorGUI();
    }

    private void EvaluateAllPoses()
    {
        if (arts == null || arts.Length == 0) { diagnostic = "No Big Sword skills found. Run manual setup first."; return; }
        var original = selected;
        var originalTime = time;
        playing = false;
        var log = new StringBuilder("[BladePoses] Isolated Hero/source socket comparison; no art assets changed.\n");
        var checkedClips = 0; var failures = 0;
        try
        {
            for (var i = 0; i < arts.Length; i++)
            {
                selected = i; CreatePreview();
                if (!Ready) { failures++; log.AppendLine(arts[i].artName + ": missing clip/rig/weapon wiring"); continue; }
                var maxAngle = 0f; var maxGrip = 0f; var maxLength = 0f;
                var initialSourceRotation = Quaternion.identity; var maxSourceTurn = 0f;
                for (var frame = 0; frame <= 60; frame++)
                {
                    SamplePose(frame / 60f);
                    maxAngle = Mathf.Max(maxAngle, angularError); maxGrip = Mathf.Max(maxGrip, gripError); maxLength = Mathf.Max(maxLength, lengthError);
                    if (frame == 0) initialSourceRotation = gun.rotation;
                    else maxSourceTurn = Mathf.Max(maxSourceTurn, Quaternion.Angle(initialSourceRotation, gun.rotation));
                }
                var palmLocked = bladeSet.socketDeltaScale == 0f && bladeSet.weaponPosOffset.sqrMagnitude < 0.000001f;
                var pass = maxAngle <= 1f && maxLength <= 0.01f && (!palmLocked || maxGrip <= 0.005f);
                if (!pass) failures++;
                checkedClips++;
                log.AppendLine(arts[i].artName + ": " + (pass ? "PASS" : "FAIL") + " • angle " + maxAngle.ToString("F3") +
                    "° • grip " + (maxGrip * 100f).ToString("F2") + "cm • blade-length delta " + (maxLength * 100f).ToString("F2") +
                    "cm • authored turn " + maxSourceTurn.ToString("F1") + "°");
            }
        }
        finally { selected = original; CreatePreview(); time = originalTime; Repaint(); }
        diagnostic = checkedClips + "/" + arts.Length + " clips sampled at 61 poses each; " + failures +
                     " failed pose checks. See Console for per-skill measurements. Visual contact timing and live transitions still need Play Mode.";
        log.AppendLine(diagnostic);
        if (failures > 0) Debug.LogWarning(log.ToString()); else Debug.Log(log.ToString());
    }

    private void CreatePreview()
    {
        Cleanup();
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ProjectRestartLocomotion.ControllerPath);
        clip = controller != null ? controller.layers[0].stateMachine.states.FirstOrDefault(s => s.state.name == arts[selected].stateName).state?.motion as AnimationClip : null;
        var socket = UnityEngine.Object.FindFirstObjectByType<WeaponSocket>();
        if (socket == null) return;
        var serialized = new SerializedObject(socket);
        var source = serialized.FindProperty("shadowRig").objectReferenceValue as GameObject;
        palmGrip = socket.PalmGripAmount;
        var rig = socket.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.avatar != null && a.avatar.isHuman && !a.name.StartsWith("_WeaponShadow"));
        bladeSet = AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/Combat/BigSword.asset");
        if (source == null || rig == null || bladeSet == null || bladeSet.weaponPrefab == null) return;
        preview = new PreviewRenderUtility();
        hero = UnityEngine.Object.Instantiate(rig.gameObject); shadow = UnityEngine.Object.Instantiate(source);
        foreach (var go in new[] { hero, shadow })
        {
            go.hideFlags = HideFlags.HideAndDontSave;
            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(behaviour);
            foreach (var animator in go.GetComponentsInChildren<Animator>(true))
            { animator.runtimeAnimatorController = null; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            preview.AddSingleGO(go);
        }
        hero.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        shadow.transform.SetPositionAndRotation(Vector3.right * 3f, Quaternion.identity);
        foreach (var renderer in shadow.GetComponentsInChildren<Renderer>(true)) renderer.enabled = true;
        heroAnimator = hero.GetComponent<Animator>();
        gun = shadow.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "ik_hand_gun");
        sourceHand = shadow.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "hand_r");
        heroHand = heroAnimator != null ? heroAnimator.GetBoneTransform(HumanBodyBones.RightHand) : null;
        heroKnuckle = heroAnimator != null ? heroAnimator.GetBoneTransform(HumanBodyBones.RightMiddleProximal) : null;
        if (gun != null && heroHand != null)
        {
            shadowSword = UnityEngine.Object.Instantiate(bladeSet.weaponPrefab, gun);
            shadowSword.transform.localPosition = Vector3.zero; shadowSword.transform.localRotation = Quaternion.identity;
            heroSword = UnityEngine.Object.Instantiate(bladeSet.weaponPrefab, heroHand);
        }
        inspector = Editor.CreateEditor(arts[selected]); time = 0f;
    }

    private void Cleanup()
    {
        if (inspector != null) UnityEngine.Object.DestroyImmediate(inspector);
        preview?.Cleanup(); preview = null;
        hero = shadow = heroSword = shadowSword = null;
        heroAnimator = null; heroHand = heroKnuckle = gun = sourceHand = null;
    }
    private void OnDisable() => Cleanup();
}

