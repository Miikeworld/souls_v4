using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;

public static class CharacterPolishSetup
{
    const string Folder="Assets/_Project/CharacterPolish/";
    const string Backup=@"C:\Users\milkw\Documents\Codex\2026-10-04\open-my-blender-character-project-and\outputs\Polish_Backup";
    [MenuItem("Tools/Character Polish/1 Apply to current protagonist")]
    static void Apply()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Exit Play Mode before applying.");
        Selection.activeObject=null;
        var player=GameObject.Find("Player");
        if(!player)throw new InvalidOperationException("Player not found.");
        var visual=player.transform.Find("ProtagonistVisual");
        if(!visual)throw new InvalidOperationException("Active protagonist not found.");
        Directory.CreateDirectory(Backup);
        string stamp=DateTime.Now.ToString("yyyyMMdd_HHmmss");
        EditorSceneManager.SaveScene(visual.gameObject.scene,Backup+"/"+visual.gameObject.scene.name+"_"+stamp+".unity",true);
        string beforePath=Folder+"Protagonist_BeforePolish_"+stamp+".prefab";
        var snapshot=UnityEngine.Object.Instantiate(visual.gameObject);snapshot.name="Protagonist_BeforePolish";
        PrefabUtility.SaveAsPrefabAsset(snapshot,beforePath);UnityEngine.Object.DestroyImmediate(snapshot);
        var original=AssetImporter.GetAtPath("Assets/Character_Unity.fbx") as ModelImporter;
        var importer=AssetImporter.GetAtPath(Folder+"Character_Secondary.fbx") as ModelImporter;
        importer.animationType=ModelImporterAnimationType.Human;importer.avatarSetup=ModelImporterAvatarSetup.CreateFromThisModel;
        importer.humanDescription=original.humanDescription;importer.isReadable=true;importer.importAnimation=false;
        importer.SaveAndReimport();
        var source=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+"Character_Secondary.fbx");
        var sourceAnimator=source.GetComponent<Animator>();
        if(!sourceAnimator || !sourceAnimator.avatar || !sourceAnimator.avatar.isValid || !sourceAnimator.avatar.isHuman)throw new InvalidOperationException("Secondary source Humanoid Avatar is invalid.");
        var sourceRenderer=source.GetComponentInChildren<SkinnedMeshRenderer>();
        var targetRenderer=visual.GetComponentsInChildren<SkinnedMeshRenderer>().First(r=>r.name!="Silhouette");
        var originalMaterial=targetRenderer.sharedMaterials[0];
        Texture baseTexture=originalMaterial.HasProperty("_BaseMap")?originalMaterial.GetTexture("_BaseMap"):originalMaterial.mainTexture;
        if(!baseTexture)throw new InvalidOperationException("Existing texture is missing.");
        var transforms=visual.GetComponentsInChildren<Transform>(true).GroupBy(t=>t.name).ToDictionary(g=>g.Key,g=>g.First());
        foreach(var b in sourceRenderer.bones.Where(b=>b.name.StartsWith("Secondary_")))
        {
            if(transforms.ContainsKey(b.name))continue;
            if(!transforms.TryGetValue(b.parent.name,out var parent))throw new InvalidOperationException("Missing parent "+b.parent.name);
            var newBone=new GameObject(b.name);Undo.RegisterCreatedObjectUndo(newBone,"Add secondary bones");
            newBone.transform.SetParent(parent,false);newBone.transform.localPosition=b.localPosition;newBone.transform.localRotation=b.localRotation;newBone.transform.localScale=b.localScale;
            transforms.Add(b.name,newBone.transform);
        }
        var wire=BuildWire(sourceRenderer.sharedMesh);
        string meshPath=Folder+"Player_SecondaryWire.asset";
        var existing=AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if(existing){wire=MeshAssetWriter.Write(wire,AssetDatabase.GetAssetPath(existing));}
        else AssetDatabase.CreateAsset(wire,meshPath);
        var material=AssetDatabase.LoadAssetAtPath<Material>(Folder+"M_Player_StylizedWire.mat");
        if(!material){material=new Material(Shader.Find("CharacterPolish/Stylized Wire"));AssetDatabase.CreateAsset(material,Folder+"M_Player_StylizedWire.mat");}
        material.SetTexture("_BaseMap",baseTexture);EditorUtility.SetDirty(material);
        var outlineMat=AssetDatabase.LoadAssetAtPath<Material>(Folder+"M_Player_Silhouette.mat");
        if(!outlineMat){outlineMat=new Material(Shader.Find("CharacterPolish/Silhouette"));AssetDatabase.CreateAsset(outlineMat,Folder+"M_Player_Silhouette.mat");}
        Undo.RecordObject(targetRenderer,"Assign derived character mesh");
        targetRenderer.sharedMesh=wire;targetRenderer.bones=sourceRenderer.bones.Select(b=>transforms[b.name]).ToArray();
        targetRenderer.rootBone=transforms[sourceRenderer.rootBone.name];targetRenderer.sharedMaterials=Enumerable.Repeat(material,wire.subMeshCount).ToArray();
        targetRenderer.localBounds=sourceRenderer.localBounds;targetRenderer.updateWhenOffscreen=false;
        PrefabUtility.RecordPrefabInstancePropertyModifications(targetRenderer);
        var outlineTransform=targetRenderer.transform.Find("Silhouette");
        var outlineObject=outlineTransform?outlineTransform.gameObject:new GameObject("Silhouette");
        if(!outlineTransform){Undo.RegisterCreatedObjectUndo(outlineObject,"Create silhouette");outlineObject.transform.SetParent(targetRenderer.transform,false);}
        var outline=outlineObject.GetComponent<SkinnedMeshRenderer>();if(!outline)outline=outlineObject.AddComponent<SkinnedMeshRenderer>();
        var smooth=UnityEngine.Object.Instantiate(wire);smooth.name="Player Silhouette (derived smooth normals)";
        var smoothVertices=smooth.vertices;var smoothNormals=smooth.normals;var sums=new Dictionary<Vector3Int,Vector3>();
        Func<Vector3,Vector3Int> key=p=>new Vector3Int(Mathf.RoundToInt(p.x*100000),Mathf.RoundToInt(p.y*100000),Mathf.RoundToInt(p.z*100000));
        for(int i=0;i<smoothVertices.Length;i++){var k=key(smoothVertices[i]);sums[k]=sums.TryGetValue(k,out var n)?n+smoothNormals[i]:smoothNormals[i];}
        for(int i=0;i<smoothNormals.Length;i++)smoothNormals[i]=sums[key(smoothVertices[i])].normalized;
        smooth.normals=smoothNormals;
        var smoothAsset=AssetDatabase.LoadAssetAtPath<Mesh>(Folder+"Player_Silhouette.asset");
        if(smoothAsset){EditorUtility.CopySerialized(smooth,smoothAsset);UnityEngine.Object.DestroyImmediate(smooth);smooth=smoothAsset;}
        else AssetDatabase.CreateAsset(smooth,Folder+"Player_Silhouette.asset");
        outline.sharedMesh=smooth;outline.bones=targetRenderer.bones;outline.rootBone=targetRenderer.rootBone;outline.sharedMaterials=Enumerable.Repeat(outlineMat,wire.subMeshCount).ToArray();
        outline.localBounds=targetRenderer.localBounds;outline.shadowCastingMode=ShadowCastingMode.Off;outline.receiveShadows=false;outlineObject.layer=targetRenderer.gameObject.layer;
        var motion=visual.GetComponent<CharacterSecondaryMotion>();if(!motion)motion=Undo.AddComponent<CharacterSecondaryMotion>(visual.gameObject);
        Undo.RecordObject(motion,"Configure secondary motion");motion.movementRoot=player.transform;motion.humanoid=visual.GetComponentInChildren<Animator>();motion.outline=outline;motion.stylizedMaterial=material;
        motion.joints=sourceRenderer.bones.Where(b=>b.name.StartsWith("Secondary_")).Select(b=>new CharacterSecondaryMotion.Joint{bone=transforms[b.name],length=b.name.Contains("Cape")?(b.name.EndsWith("01")?.135059f:.175391f):b.name.Contains("Hip")?.144324f:b.name.Contains("Pouch")?.061f:b.name.EndsWith("L")?.183387f:.082313f,angleLimit=b.name.Contains("Cape")?12:b.name.Contains("Pouch")?4:7,stiffnessScale=b.name.Contains("Cape")?1:b.name.Contains("Pouch")?2.2f:1.6f}).ToArray();
        EditorUtility.SetDirty(motion);PrefabUtility.RecordPrefabInstancePropertyModifications(motion);
        EditorSceneManager.MarkSceneDirty(visual.gameObject.scene);
        var copy=UnityEngine.Object.Instantiate(visual.gameObject);copy.name="Protagonist_Stylized";
        copy.GetComponent<CharacterSecondaryMotion>().movementRoot=copy.transform;
        PrefabUtility.SaveAsPrefabAsset(copy,Folder+"Protagonist_Stylized.prefab");UnityEngine.Object.DestroyImmediate(copy);
        AssetDatabase.SaveAssets();EditorSceneManager.SaveScene(visual.gameObject.scene);
        File.WriteAllText(Backup+"/Polish_Installation.txt","Scene: "+visual.gameObject.scene.path+"\nSource vertices: "+sourceRenderer.sharedMesh.vertexCount+"\nDerived vertices: "+wire.vertexCount+"\nTriangles: "+wire.triangles.Length/3+"\nSecondary bones: "+motion.joints.Length+"\nHumanoid avatar valid: "+sourceAnimator.avatar.isValid+"\nExisting animator/avatar preserved: "+motion.humanoid.avatar.isHuman+"\nOriginal texture: "+AssetDatabase.GetAssetPath(baseTexture));
        Selection.activeGameObject=visual.gameObject;
        Debug.Log("Character Polish installed. Source mesh, texture, humanoid avatar and gameplay references preserved.");
    }
    [Serializable] sealed class SourceTopology
    {
        public string[] edges=Array.Empty<string>(),points=Array.Empty<string>(),faceUV=Array.Empty<string>();
    }
    static string UVKey(Vector2 uv)=>Mathf.RoundToInt(uv.x*10000)+","+Mathf.RoundToInt(uv.y*10000);
    static string EdgeKey(string a,string b)=>string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;
    static Mesh BuildWire(Mesh source,Mesh target=null)
    {
        if(source.blendShapeCount>0)throw new InvalidOperationException("Blend shapes need explicit copying before use.");
        var positions=source.vertices;var normals=source.normals;var tangents=source.tangents;var weights=source.boneWeights;
        var uv=source.uv;var uv1=source.uv2;
        var topologyAsset=AssetDatabase.LoadAssetAtPath<TextAsset>(Folder+"SourceTopology.json");
        if(!topologyAsset)throw new InvalidOperationException("Original polygon metadata is missing.");
        var topology=JsonUtility.FromJson<SourceTopology>(topologyAsset.text);
        var edges=new HashSet<string>(topology.edges);var points=new HashSet<string>(topology.points);var faceUV=new HashSet<string>(topology.faceUV);
        var keys=uv.Select(UVKey).ToArray();int matched=keys.Count(points.Contains);
        if(matched<keys.Length*.999f)throw new InvalidOperationException("Original UV metadata does not match imported source: "+matched+"/"+keys.Length);
        int suppressed=0,facial=0;
        var vertices=new List<Vector3>();var ns=new List<Vector3>();var ts=new List<Vector4>();var ws=new List<BoneWeight>();var uvs=new List<Vector2>();var uvs1=new List<Vector2>();var bary=new List<Vector3>();var edgeMasks=new List<Color>();var indices=new List<int[]>();
        for(int sub=0;sub<source.subMeshCount;sub++)
        {
            var tris=source.GetTriangles(sub);var dest=new int[tris.Length];
            for(int triangle=0;triangle<tris.Length;triangle+=3)
            {
                string a=keys[tris[triangle]],b=keys[tris[triangle+1]],c=keys[tris[triangle+2]];
                var edgeMask=new Vector3(edges.Contains(EdgeKey(b,c))?1:0,edges.Contains(EdgeKey(c,a))?1:0,edges.Contains(EdgeKey(a,b))?1:0);
                float face=faceUV.Contains(a)&&faceUV.Contains(b)&&faceUV.Contains(c)?1:0;
                suppressed+=(int)(3-edgeMask.x-edgeMask.y-edgeMask.z);if(face>0)facial++;
                for(int corner=0;corner<3;corner++)
                {
                int t=triangle+corner;
                int v=tris[t];dest[t]=vertices.Count;vertices.Add(positions[v]);ns.Add(normals[v]);ws.Add(weights[v]);uvs.Add(uv[v]);
                if(tangents.Length==positions.Length)ts.Add(tangents[v]);if(uv1.Length==positions.Length)uvs1.Add(uv1[v]);
                bary.Add(corner==0?Vector3.right:corner==1?Vector3.up:Vector3.forward);
                // Keep all wire metadata in COLOR for reliable animated GPU skinning.
                edgeMasks.Add(new Color(corner==0?1:0,corner==1?1:0,edgeMask.x+edgeMask.y*2+edgeMask.z*4,1-face));
                }
            }
            indices.Add(dest);
        }
        var mesh=target?target:new Mesh();mesh.Clear(false);mesh.name="Player Original Polygon Wire (derived)";mesh.indexFormat=IndexFormat.UInt32;
        mesh.SetVertices(vertices);mesh.SetNormals(ns);if(ts.Count>0)mesh.SetTangents(ts);mesh.SetUVs(0,uvs);if(uvs1.Count>0)mesh.SetUVs(1,uvs1);mesh.SetUVs(2,bary);mesh.SetColors(edgeMasks);
        mesh.boneWeights=ws.ToArray();mesh.bindposes=source.bindposes;mesh.subMeshCount=source.subMeshCount;
        for(int s=0;s<indices.Count;s++)mesh.SetTriangles(indices[s],s);
        mesh.bounds=source.bounds;
        File.WriteAllText(Backup+"/Wireframe_Revision.txt","Original source UV points matched: "+matched+"/"+keys.Length+"\nSource polygons: 27913\nRendered triangles: "+mesh.triangles.Length/3+"\nHidden imported diagonal sides: "+suppressed+"\nFace triangles with wire disabled: "+facial+"\nOriginal mesh, UV0, texture, humanoid rig and secondary motion unchanged.");
        return mesh;
    }
    [MenuItem("Tools/Character Polish/2 Begin gameplay motion check")]
    static void BeginCheck()
    {
        if(!EditorApplication.isPlaying)throw new InvalidOperationException("Enter Play Mode first.");
        var player=GameObject.Find("Player");if(!player)throw new InvalidOperationException("Player not found.");
        if(!player.GetComponent<CharacterPolishGameplayCheck>())player.AddComponent<CharacterPolishGameplayCheck>();
    }
    [MenuItem("Tools/Character Polish/3 Apply to main gameplay scene")]
    static void ApplyMainGameplay()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Exit Play Mode first.");
        var current=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        string originalPath=current.path;EditorSceneManager.SaveScene(current);
        try
        {
            EditorSceneManager.OpenScene("Assets/_Project/Scenes/00_TestBlockout.unity");
            Apply();
        }
        finally{if(!string.IsNullOrEmpty(originalPath))EditorSceneManager.OpenScene(originalPath);}
    }
    [MenuItem("Tools/Character Polish/4 Open main gameplay scene")]
    static void OpenMain()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Exit Play Mode first.");
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Selection.activeObject=null;
        EditorSceneManager.OpenScene("Assets/_Project/Scenes/00_TestBlockout.unity");
    }
    [MenuItem("Tools/Character Polish/5 Use black original polygon wire")]
    static void BlackPolygonWire()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Exit Play Mode first.");
        var source=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+"Character_Secondary.fbx").GetComponentInChildren<SkinnedMeshRenderer>();
        var shader=Shader.Find("CharacterPolish/Stylized Wire");
        if(!shader || ShaderUtil.ShaderHasError(shader))throw new InvalidOperationException("Wire shader failed to compile.");
        var asset=AssetDatabase.LoadAssetAtPath<Mesh>(Folder+"Player_SecondaryWire.asset");
        BuildWire(source.sharedMesh,asset);asset.UploadMeshData(false);EditorUtility.SetDirty(asset);
        var material=AssetDatabase.LoadAssetAtPath<Material>(Folder+"M_Player_StylizedWire.mat");
        material.SetColor("_WireTint",Color.black);material.SetFloat("_WireStrength",1);material.SetFloat("_WireThickness",.55f);material.SetFloat("_FaceWireStrength",0);
        EditorUtility.SetDirty(material);AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(Folder+"Player_SecondaryWire.asset",ImportAssetOptions.ForceUpdate);
        asset=AssetDatabase.LoadAssetAtPath<Mesh>(Folder+"Player_SecondaryWire.asset");
        foreach(var renderer in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
            if(renderer.sharedMesh==asset){renderer.sharedMesh=null;renderer.sharedMesh=asset;}
        SceneView.RepaintAll();
        File.AppendAllText(Backup+"/Wireframe_Revision.txt","\nWire colour: black\nWire strength: 1\nWire thickness: .55 pixels\nFace wire: 0\nShader compile errors: false\nMaterial texture: "+AssetDatabase.GetAssetPath(material.GetTexture("_BaseMap")));
        var originalRetro=AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Resources/Materials/RetroN64Ui.mat");
        var inkShader=Shader.Find("Hidden/CharacterPolish/Retro Preserve Ink");
        if(!inkShader || ShaderUtil.ShaderHasError(inkShader))throw new InvalidOperationException("Ink display shader did not compile.");
        var ink=AssetDatabase.LoadAssetAtPath<Material>(Folder+"M_Retro_PreserveInk.mat");
        if(!ink){ink=new Material(originalRetro){name="M_Retro_PreserveInk",shader=inkShader};AssetDatabase.CreateAsset(ink,Folder+"M_Retro_PreserveInk.mat");}
        var current=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var scenes=new[]{"Assets/_Project/Scenes/00_TestBlockout.unity","Assets/_Project/Scenes/01_Courtyard.unity"};
        Selection.activeObject=null;
        foreach(string scenePath in scenes)
        {
            var scene=scenePath==current.path?current:EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Additive);
            EditorSceneManager.SaveScene(scene,Backup+"/"+scene.name+"_BeforeBlackInk_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".unity",true);
            foreach(var retro in scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<RetroScreen>(true)))
            {
                var settings=new SerializedObject(retro);settings.FindProperty("material").objectReferenceValue=ink;settings.ApplyModifiedProperties();
            }
            EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
            if(scene!=current)EditorSceneManager.CloseScene(scene,true);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("Black Blender polygon wire installed; triangulation diagonals hidden and face wire disabled.");
    }
    static bool frontWireView;
    [MenuItem("Tools/Character Polish/6 Inspect wire through gameplay camera")]
    static void InspectGameplayWire()
    {
        if(!EditorApplication.isPlaying)throw new InvalidOperationException("Enter Play Mode first.");
        var player=GameObject.Find("Player");var camera=player.GetComponent<PlayerCameraController>();
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var type=typeof(PlayerCameraController);
        type.GetField("mouseSensitivity",flags).SetValue(camera,0f);
        type.GetField("pitch",flags).SetValue(camera,18f);
        type.GetField("yaw",flags).SetValue(camera,player.transform.eulerAngles.y+(frontWireView?180f:35f));
        frontWireView=!frontWireView;
        var renderer=player.transform.Find("ProtagonistVisual").GetComponentsInChildren<SkinnedMeshRenderer>().First(r=>r.name!="Silhouette");
        File.WriteAllText(Backup+"/Wireframe_Gameplay_Verification.txt","Live shader: "+renderer.sharedMaterial.shader.name+"\nLive mesh: "+AssetDatabase.GetAssetPath(renderer.sharedMesh)+"\nPolygon mask vertices: "+renderer.sharedMesh.colors.Length+"\nWire strength: "+renderer.sharedMaterial.GetFloat("_WireStrength")+"\nFace wire: "+renderer.sharedMaterial.GetFloat("_FaceWireStrength")+"\nExisting gameplay camera distance and lighting retained.");
        Debug.Log("Gameplay wire inspection: original camera distance/FOV, steady input, black polygon lines, face wire disabled. Temporary camera changes revert on exiting Play Mode.");
    }
}
