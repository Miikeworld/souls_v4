using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Tools > Project Restart > Build Authored HUD Canvas.
/// Creates a real HUD_Canvas in the test scene matching the runtime layout
/// (same frame texture, scale, margin and normalized slot rects read off
/// PlayerHud), then wires the three fill RectTransforms into PlayerHud's
/// inspector fields so the runtime canvas is skipped entirely. The Frame is
/// the LAST sibling so it draws over the bar ends. Re-runnable: rebuilds
/// HUD_Canvas in place. Adjust the fills' RectTransforms afterwards — pivot
/// stays (0,0.5) so the bars shrink left-anchored.
/// </summary>
public static class ProjectRestartHud
{
    private const string ScenePath = "Assets/_Project/Scenes/00_TestBlockout.unity";

    private static readonly Rect DefHp = new Rect(0.129f, 0.415f, 0.730f, 0.060f);
    private static readonly Rect DefMp = new Rect(0.129f, 0.510f, 0.575f, 0.055f);
    private static readonly Rect DefSp = new Rect(0.129f, 0.585f, 0.575f, 0.050f);

    [MenuItem("Tools/Project Restart/Build Authored HUD Canvas")]
    public static void Build()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var hud = Object.FindFirstObjectByType<PlayerHud>();
        if (hud == null) { Debug.LogError("[ProjectRestart] No PlayerHud in " + ScenePath); return; }

        var so = new SerializedObject(hud);
        var hudFrame = so.FindProperty("hudFrame").objectReferenceValue as Texture2D;
        if (hudFrame == null) { Debug.LogError("[ProjectRestart] PlayerHud.hudFrame is not assigned."); return; }

        float frameScale = so.FindProperty("frameScale").floatValue;
        Vector2 margin = so.FindProperty("margin").vector2Value;
        var hpSlot = Slot(so.FindProperty("healthSlot").rectValue, DefHp);
        var mpSlot = Slot(so.FindProperty("manaSlot").rectValue, DefMp);
        var spSlot = Slot(so.FindProperty("staminaSlot").rectValue, DefSp);
        var hpColor = so.FindProperty("hpColor").colorValue;
        var mpColor = so.FindProperty("mpColor").colorValue;
        var spColor = so.FindProperty("spColor").colorValue;

        var old = GameObject.Find("HUD_Canvas");
        if (old != null) Object.DestroyImmediate(old);

        var canvasGo = new GameObject("HUD_Canvas", typeof(Canvas), typeof(CanvasScaler));
        Undo.RegisterCreatedObjectUndo(canvasGo, "Build authored HUD");
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        float W = hudFrame.width * frameScale, H = hudFrame.height * frameScale;

        // HudRoot carries the whole HUD (parallax target — PlayerHud climbs to
        // it), Bars is the shake/punch rect so bars and frame flinch together.
        var root = NewRect("HudRoot", canvasGo.transform);
        AnchorTopLeft(root, margin, W, H);
        var bars = NewRect("Bars", root);
        bars.anchorMin = Vector2.zero; bars.anchorMax = Vector2.one;
        bars.offsetMin = bars.offsetMax = Vector2.zero;

        var ghost = Fill(bars, "HpGhost", hpSlot, new Color(1f, 0.92f, 0.85f, 0.45f), W, H);
        var hp = Fill(bars, "HpFill", hpSlot, hpColor, W, H);
        var mpGhost = Fill(bars, "MpGhost", mpSlot, new Color(0.85f, 0.92f, 1f, 0.45f), W, H);
        var mp = Fill(bars, "MpFill", mpSlot, mpColor, W, H);
        var sp = Fill(bars, "SpFill", spSlot, spColor, W, H);

        var frameRt = NewRect("Frame", bars); // last sibling = on top
        frameRt.anchorMin = Vector2.zero; frameRt.anchorMax = Vector2.one;
        frameRt.offsetMin = frameRt.offsetMax = Vector2.zero;
        var frameImg = frameRt.gameObject.AddComponent<RawImage>();
        frameImg.texture = hudFrame;
        frameImg.raycastTarget = false;

        so.FindProperty("hpFill").objectReferenceValue = hp;
        so.FindProperty("mpFill").objectReferenceValue = mp;
        so.FindProperty("spFill").objectReferenceValue = sp;
        so.FindProperty("hpGhostFill").objectReferenceValue = ghost;
        so.FindProperty("mpGhostFill").objectReferenceValue = mpGhost;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[ProjectRestart] Authored HUD built — HpGhost/HpFill/MpFill/SpFill wired into PlayerHud, " +
                  $"frame {hudFrame.width}x{hudFrame.height} on top, parallax+shake share HudRoot/Bars. " +
                  "Drag the fills' RectTransforms to restyle.");
    }

    /// <summary>The hand-drawn frame is pixel art — bilinear + compression smear
    /// it. Point filter, uncompressed, no mips. Standalone so it never rebuilds
    /// (and stomps) the authored HUD_Canvas.</summary>
    [MenuItem("Tools/Project Restart/Pixel-Crisp HUD Frame Import")]
    public static void CrispFrame()
    {
        var hud = Object.FindFirstObjectByType<PlayerHud>();
        var tex = hud != null ? new SerializedObject(hud).FindProperty("hudFrame").objectReferenceValue as Texture2D : null;
        var path = tex != null ? AssetDatabase.GetAssetPath(tex) : "Assets/IMG_0040-Photoroom.png";
        if (AssetImporter.GetAtPath(path) is not TextureImporter ti)
        {
            Debug.LogError("[ProjectRestart] HUD frame texture importer not found at " + path);
            return;
        }
        if (ti.filterMode == FilterMode.Point && ti.textureCompression == TextureImporterCompression.Uncompressed && !ti.mipmapEnabled)
        {
            Debug.Log("[ProjectRestart] HUD frame already pixel-crisp: " + path);
            return;
        }
        ti.filterMode = FilterMode.Point;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.mipmapEnabled = false;
        ti.SaveAndReimport();
        Debug.Log("[ProjectRestart] HUD frame set to Point / Uncompressed / no mips: " + path);
    }

    private static Rect Slot(Rect r, Rect def)
    {
        return r.width <= 0f || r.height <= 0f ? def : r;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static void AnchorTopLeft(RectTransform rt, Vector2 margin, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(margin.x, -margin.y);
        rt.sizeDelta = new Vector2(w, h);
    }

    /// <summary>A fill at full size inside the frame-space Bars container.
    /// Pivot (0,0.5) anchors left-center so PlayerHud's localScale.x shrink
    /// drains the bar right-to-left without moving the left edge.</summary>
    private static RectTransform Fill(RectTransform bars, string name, Rect slot, Color color, float W, float H)
    {
        var rt = NewRect(name, bars);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(slot.x * W, -(slot.y * H) - slot.height * H * 0.5f);
        rt.sizeDelta = new Vector2(slot.width * W, slot.height * H);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return rt;
    }
}
