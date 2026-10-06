using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Tools > Project Restart > Setup Retro Post (N64) — the N64 approach for
/// real: attaches RetroScreen to the active scene's Main Camera so it renders
/// into a ~240-line point-filtered RenderTexture, upscaled fullscreen through
/// the RetroN64Ui material (dither + ~5-bit colour). Runs under every UI
/// canvas so HUD/menus stay crisp. Also strips the renderer-feature variant —
/// its color-buffer copy sourced an empty backbuffer alias on this URP version.
/// Idempotent: re-running updates rather than duplicating.
/// </summary>
public static class ProjectRestartRetroPost
{
    private const string ShaderPath = "Assets/_Project/Resources/Shaders/RetroN64Ui.shader";
    private const string MatPath = "Assets/_Project/Resources/Materials/RetroN64Ui.mat";
    private const string RendererPath = "Assets/Settings/PC_Renderer.asset";
    private const string FeatureName = "RetroN64";

    [MenuItem("Tools/Project Restart/Setup Retro Post (N64)")]
    public static void Run()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null) { Debug.LogError("[RetroPost] missing shader at " + ShaderPath); return; }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (mat == null)
        {
            AssetDatabase.CreateFolder("Assets/_Project/Resources", "Materials");
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, MatPath);
        }
        mat.shader = shader;
        mat.SetFloat("_Levels", 48f);
        mat.SetFloat("_Dither", 0.2f);
        mat.SetFloat("_Saturation", 1.15f);
        mat.SetFloat("_Warmth", 0.05f);
        mat.SetFloat("_Vignette", 0.3f);
        mat.SetFloat("_Fringe", 0.5f);
        mat.SetFloat("_Scanline", 0f);
        EditorUtility.SetDirty(mat);

        // OoT atmosphere: fog was half the N64 mood (dressed-up draw distance).
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.022f;
        RenderSettings.fogColor = new Color(0.16f, 0.2f, 0.28f);

        // Strip the renderer-feature variant — in this URP version its color
        // source resolves to an empty buffer (flat grey output).
        var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
        if (renderer != null)
        {
            var features = renderer.rendererFeatures;
            var stale = features.Where(f => f != null && f.name == FeatureName).ToList();
            foreach (var s in stale)
            {
                features.Remove(s);
                AssetDatabase.RemoveObjectFromAsset(s);
                Object.DestroyImmediate(s, true);
            }
            features.RemoveAll(f => f == null);
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
        }

        // Attach RetroScreen to the scene's main (screen-target) camera.
        var cam = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)
            .FirstOrDefault(c => c.targetTexture == null && c.CompareTag("MainCamera"));
        if (cam == null) { Debug.LogWarning("[RetroPost] no MainCamera-tagged camera in the active scene — material prepared, attach RetroScreen manually."); }
        else
        {
            var rs = cam.GetComponent<RetroScreen>();
            if (rs == null) rs = Undo.AddComponent<RetroScreen>(cam.gameObject);
            var so = new SerializedObject(rs);
            so.FindProperty("material").objectReferenceValue = mat;
            so.FindProperty("retroHeight").intValue = 640;
            so.FindProperty("softUpscale").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(cam.gameObject);
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[RetroPost] RetroN64Ui wired — camera renders to a 240-line RT upscaled with dither + 5-bit colour.");
    }
}

// Manual, project-owned art pass. Never edits a vendor material or collider mesh.
public static class ProjectRestartMechanicalEnvironment
{
    const string Folder = "Assets/_Project/ArtDirection";
    [MenuItem("Tools/Project Restart/Art Direction/Apply Mechanical Environment %#m")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var shader = Shader.Find("Souls/Mechanical Environment");
        if (!shader || ShaderUtil.ShaderHasError(shader)) { Debug.LogError("[ArtDirection] Shader missing or has errors."); return; }
        EnsureFolder(Folder); EnsureFolder(Folder + "/Meshes"); EnsureFolder(Folder + "/Materials");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return;
        // Includes the currently unsaved user edits, before applying the pass.
        var backup = Folder + "/" + System.IO.Path.GetFileNameWithoutExtension(scene.path) + "_BeforeMechanical.unity";
        if (!System.IO.File.Exists(backup)) EditorSceneManager.SaveScene(scene, backup, true);
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Mechanical environment art pass");
        int applied = 0, skipped = 0;
        foreach (var root in scene.GetRootGameObjects())
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mf = r.GetComponent<MeshFilter>();
            if (!mf || !mf.sharedMesh || r.gameObject.layer == 5 || r.GetComponentInParent<Animator>() ||
                r.GetComponentInParent<Health>() || r.GetComponentInParent<PlayerLocomotion>() ||
                r.GetComponentInParent<ParticleSystem>() || r.name.ToLowerInvariant().Contains("weapon")) { skipped++; continue; }
            var mats = r.sharedMaterials;
            if (mats.Length == 0 || mats.Any(m => !m || m.renderQueue > 2450 ||
                m.shader.name.Contains("Unlit") || m.shader.name.Contains("Traversal") ||
                m.shader.name.Contains("CharacterPolish"))) { skipped++; continue; }
            if (mats.All(m => m.shader == shader)) continue;
            try
            {
                var sourceMesh = mf.sharedMesh;
                string key = AssetKey(sourceMesh);
                var path = Folder + "/Meshes/" + key + ".asset";
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (!mesh) { mesh = CreaseMesh(sourceMesh); AssetDatabase.CreateAsset(mesh, path); }
                bool foliage = IsFoliage(r.transform);
                for (int i = 0; i < mats.Length; i++)
                {
                    var source = mats[i]; var mp = Folder + "/Materials/" + AssetKey(source) + (foliage ? "_Foliage" : "_Structure") + ".mat";
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
                    if (!mat)
                    {
                        mat = new Material(shader) { name = source.name + " Mechanical" };
                        string tex = source.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                        if (source.HasProperty(tex)) { mat.SetTexture("_BaseMap", source.GetTexture(tex)); mat.SetTextureScale("_BaseMap", source.GetTextureScale(tex)); mat.SetTextureOffset("_BaseMap", source.GetTextureOffset(tex)); }
                        mat.SetColor("_BaseColor", source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white);
                        mat.SetFloat("_LineStrength", foliage ? 0.18f : 0.7f);
                        mat.SetFloat("_LineWidth", foliage ? 0.35f : 0.65f);
                        mat.SetFloat("_Saturation", foliage ? 0.5f : 0.7f);
                        if(source.HasProperty("_Cull")) mat.SetFloat("_Cull",source.GetFloat("_Cull"));
                        if(source.IsKeywordEnabled("_ALPHATEST_ON")) { mat.EnableKeyword("_ALPHATEST_ON"); if(source.HasProperty("_Cutoff"))mat.SetFloat("_Cutoff",source.GetFloat("_Cutoff")); }
                        if(source.IsKeywordEnabled("_EMISSION") && source.HasProperty("_EmissionColor")) {
                            mat.SetColor("_EmissionColor",source.GetColor("_EmissionColor"));
                            if(source.HasProperty("_EmissionMap"))mat.SetTexture("_EmissionMap",source.GetTexture("_EmissionMap"));
                        }
                        AssetDatabase.CreateAsset(mat, mp);
                    }
                    mats[i] = mat;
                }
                Undo.RecordObject(mf, "Crease mesh"); Undo.RecordObject(r, "Environment materials");
                mf.sharedMesh = mesh; r.sharedMaterials = mats; applied++;
            }
            catch (System.Exception e) { Debug.LogWarning("[ArtDirection] Preserved " + r.name + ": " + e.Message); skipped++; }
        }
        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene); AssetDatabase.SaveAssets();
        Debug.Log($"[ArtDirection] {applied} static renderers styled; {skipped} protected/unsupported renderers preserved. Creases only, quiet foliage, distance fade. Backup: {backup}. Review before saving.");
    }
    static bool IsFoliage(Transform t)
    {
        while(t) { string n = t.name.ToLowerInvariant(); if(new[]{"leaf", "leaves", "grass", "bush", "fern", "plant", "foliage"}.Any(n.Contains)) return true; t = t.parent; } return false;
    }
    static string AssetKey(Object o)
    {
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long id)) return guid + "_" + id;
        return "builtin_" + o.name.Replace('/', '_').Replace(':', '_');
    }
    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = System.IO.Path.GetDirectoryName(path).Replace('\\','/'); EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
    struct Edge : System.IEquatable<Edge>
    {
        public Vector3 a,b;
        public Edge(Vector3 x, Vector3 y) { bool first=x.x<y.x || (x.x==y.x && (x.y<y.y || (x.y==y.y && x.z<y.z))); a=first?x:y; b=first?y:x; }
        public bool Equals(Edge e) => a.Equals(e.a) && b.Equals(e.b);
        public override bool Equals(object o) => o is Edge e && Equals(e);
        public override int GetHashCode() => a.GetHashCode()*397 ^ b.GetHashCode();
    }
    static Mesh CreaseMesh(Mesh source)
    {
        var vertices = source.vertices; var normals = source.normals; var uv = source.uv; var uv2 = source.uv2; var colors = source.colors;
        var edges = new System.Collections.Generic.Dictionary<Edge, System.Collections.Generic.List<Vector3>>();
        var sub = new int[source.subMeshCount][];
        for(int s=0;s<sub.Length;s++)
        {
            if(source.GetTopology(s)!=MeshTopology.Triangles) throw new System.InvalidOperationException("Non triangle mesh");
            sub[s]=source.GetTriangles(s);
            for(int i=0;i<sub[s].Length;i+=3)
            {
                Vector3 a=vertices[sub[s][i]], b=vertices[sub[s][i+1]], c=vertices[sub[s][i+2]];
                Vector3 n=Vector3.Cross(b-a,c-a).normalized;
                foreach(var e in new[]{new Edge(b,c),new Edge(c,a),new Edge(a,b)}) { if(!edges.TryGetValue(e,out var list)) edges[e]=list=new System.Collections.Generic.List<Vector3>(); list.Add(n); }
            }
        }
        var p=new System.Collections.Generic.List<Vector3>(); var ns=new System.Collections.Generic.List<Vector3>();
        var u=new System.Collections.Generic.List<Vector2>(); var u2=new System.Collections.Generic.List<Vector2>(); var cs=new System.Collections.Generic.List<Color>();
        var bary=new System.Collections.Generic.List<Vector3>(); var masks=new System.Collections.Generic.List<Vector3>();
        var indices=new int[sub.Length][];
        System.Func<Edge,float> crease=e=> {var list=edges[e]; return list.Count==1 || list.Any(n=>Vector3.Dot(n,list[0])<0.88f) ? 1f:0f;};
        for(int s=0;s<sub.Length;s++)
        {
            indices[s]=new int[sub[s].Length];
            for(int i=0;i<sub[s].Length;i+=3)
            {
                Vector3 a=vertices[sub[s][i]],b=vertices[sub[s][i+1]],c=vertices[sub[s][i+2]];
                var mask=new Vector3(crease(new Edge(b,c)),crease(new Edge(c,a)),crease(new Edge(a,b)));
                for(int j=0;j<3;j++) { int v=sub[s][i+j]; indices[s][i+j]=p.Count;p.Add(vertices[v]);ns.Add(normals.Length==vertices.Length?normals[v]:Vector3.Cross(b-a,c-a).normalized);u.Add(uv.Length==vertices.Length?uv[v]:Vector2.zero); if(uv2.Length==vertices.Length)u2.Add(uv2[v]); if(colors.Length==vertices.Length)cs.Add(colors[v]); bary.Add(j==0?Vector3.right:j==1?Vector3.up:Vector3.forward);masks.Add(mask); }
            }
        }
        var mesh=new Mesh {name=source.name+" MechanicalCreases",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
        mesh.SetVertices(p);mesh.SetNormals(ns);mesh.SetUVs(0,u);if(u2.Count==p.Count)mesh.SetUVs(1,u2);if(cs.Count==p.Count)mesh.SetColors(cs);mesh.SetUVs(3,bary);mesh.SetUVs(4,masks);
        mesh.subMeshCount=sub.Length;for(int s=0;s<sub.Length;s++)mesh.SetTriangles(indices[s],s);mesh.bounds=source.bounds;return mesh;
    }
}
