using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class WeaponPolishSetup
{
 const string Folder="Assets/_Project/WeaponPolish/";
 const string Output=@"C:\Users\milkw\Documents\Codex\2026-10-04\open-my-blender-character-project-and\outputs\Weapons";
 [Serializable] class Topology {public string[] edges=Array.Empty<string>(),points=Array.Empty<string>();}
 static string Key(Vector2 u)=>Mathf.RoundToInt(u.x*10000)+","+Mathf.RoundToInt(u.y*10000);
 static string Edge(string a,string b)=>string.CompareOrdinal(a,b)<0?a+"|"+b:b+"|"+a;
 [MenuItem("Tools/Weapon Polish/1 Build and replace both weapons")]
 static void Build()
 {
  if(EditorApplication.isPlaying)throw new InvalidOperationException("Exit Play Mode first.");
  var shader=AssetDatabase.LoadAssetAtPath<Shader>(Folder+"Shaders/WeaponStylizedWire.shader");
  if(!shader||ShaderUtil.ShaderHasError(shader))throw new InvalidOperationException("Weapon shader has an import error.");
  var report=new List<string>();
  foreach(string kind in new[]{"Katana","BigSword"})
  {
   var set=AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/Combat/"+kind+".asset");
   string before=Folder+kind+"_BeforeReplacement.asset";
   if(!AssetDatabase.LoadAssetAtPath<WeaponSet>(before))AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(set),before);
   var old=AssetDatabase.LoadAssetAtPath<WeaponSet>(before);
   var importer=(ModelImporter)AssetImporter.GetAtPath(Folder+kind+"_Painted.fbx");
   importer.isReadable=true;importer.importAnimation=false;importer.importCameras=false;importer.importLights=false;
   importer.materialImportMode=ModelImporterMaterialImportMode.None;importer.meshCompression=ModelImporterMeshCompression.Off;
   importer.SaveAndReimport();
   foreach(string suffix in new[]{"_BaseColor.png","_EmissionMask.png"})
   {
    var ti=(TextureImporter)AssetImporter.GetAtPath(Folder+kind+suffix);
    ti.textureType=TextureImporterType.Default;ti.sRGBTexture=suffix.StartsWith("_BaseColor");
    ti.mipmapEnabled=true;ti.wrapMode=TextureWrapMode.Clamp;ti.filterMode=FilterMode.Bilinear;ti.anisoLevel=2;
    ti.maxTextureSize=2048;ti.textureCompression=TextureImporterCompression.CompressedHQ;ti.SaveAndReimport();
   }
   var source=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+kind+"_Painted.fbx");
   var filter=source.GetComponentInChildren<MeshFilter>();var mesh=filter.sharedMesh;
   var uv=mesh.uv;var vertices=mesh.vertices;var normals=mesh.normals;var tri=mesh.triangles;
   if(uv.Length!=vertices.Length)throw new InvalidOperationException(kind+" UVs missing.");
   var topo=JsonUtility.FromJson<Topology>(AssetDatabase.LoadAssetAtPath<TextAsset>(Folder+kind+"_Topology.json").text);
   var edges=new HashSet<string>(topo.edges);var points=new HashSet<string>(topo.points);
   int matches=uv.Count(p=>points.Contains(Key(p)));
   if(matches<uv.Length*.997f)throw new InvalidOperationException(kind+" UV metadata mismatch: "+matches+"/"+uv.Length);
   float grip=kind=="Katana"?.857f:.835f;
   float desiredTip=kind=="Katana"?1.25f:1.58f;
   if(BladeGeometry.TryMeasure(old.weaponPrefab.transform,out var oldBase,out var oldTip,out var oldAxis))
    desiredTip=Mathf.Clamp(oldTip.magnitude,kind=="Katana"?1.05f:1.35f,kind=="Katana"?1.45f:1.85f);
   float scale=desiredTip/grip;
   Quaternion rotation=Quaternion.Euler(-90,0,0);
   var vp=new Vector3[tri.Length];var np=new Vector3[tri.Length];var up=new Vector2[tri.Length];var colors=new Color[tri.Length];
   var indices=new int[tri.Length];int hidden=0;
   for(int i=0;i<tri.Length;i+=3)
   {
    var keys=new[]{Key(uv[tri[i]]),Key(uv[tri[i+1]]),Key(uv[tri[i+2]])};int bits=0;
    if(edges.Contains(Edge(keys[1],keys[2])))bits|=1;else hidden++;
    if(edges.Contains(Edge(keys[2],keys[0])))bits|=2;else hidden++;
    if(edges.Contains(Edge(keys[0],keys[1])))bits|=4;else hidden++;
    for(int c=0;c<3;c++)
    {
     int id=tri[i+c];var p=filter.transform.TransformPoint(vertices[id]);
     vp[i+c]=rotation*(p-new Vector3(0,grip,0))*scale;
     np[i+c]=(rotation*filter.transform.TransformDirection(normals[id])).normalized;
     up[i+c]=uv[id];colors[i+c]=new Color(c==0?1:0,c==1?1:0,bits,1);indices[i+c]=i+c;
    }
   }
   var wire=new Mesh{name=kind+" Original Polygon Wire",indexFormat=IndexFormat.UInt32};
   wire.vertices=vp;wire.normals=np;wire.uv=up;wire.SetColors(colors);wire.triangles=indices;wire.RecalculateBounds();
   string meshPath=Folder+kind+"_Wire.asset";var existing=AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
   wire=MeshAssetWriter.Write(wire,meshPath);
   string matPath=Folder+"M_"+kind+"_StylizedWire.mat";var mat=AssetDatabase.LoadAssetAtPath<Material>(matPath);
   if(!mat){mat=new Material(shader);AssetDatabase.CreateAsset(mat,matPath);}
   mat.SetTexture("_BaseMap",AssetDatabase.LoadAssetAtPath<Texture2D>(Folder+kind+"_BaseColor.png"));
   mat.SetTexture("_EmissionMask",AssetDatabase.LoadAssetAtPath<Texture2D>(Folder+kind+"_EmissionMask.png"));
   mat.SetColor("_BaseColor",Color.white);mat.SetColor("_WireTint",Color.black);
   mat.SetColor("_EmissionColor",kind=="Katana"?new Color(.48f,.06f,1):new Color(1,.015f,.03f));
   mat.SetFloat("_WireStrength",1);mat.SetFloat("_WireThickness",.50f);mat.SetFloat("_FaceWireStrength",1);
   mat.SetFloat("_EmissionStrength",1.35f);mat.SetFloat("_ShadowStrength",.25f);EditorUtility.SetDirty(mat);
   var root=new GameObject(kind+"_Stylized");var mf=root.AddComponent<MeshFilter>();mf.sharedMesh=wire;
   var renderer=root.AddComponent<MeshRenderer>();renderer.sharedMaterial=mat;
   var baseMarker=new GameObject("BladeBase");baseMarker.transform.SetParent(root.transform,false);baseMarker.transform.localPosition=new Vector3(0,0,(grip-(kind=="Katana"?.69f:.64f))*scale);
   var tipMarker=new GameObject("BladeTip");tipMarker.transform.SetParent(root.transform,false);tipMarker.transform.localPosition=new Vector3(0,0,desiredTip);
   var prefab=PrefabUtility.SaveAsPrefabAsset(root,Folder+kind+"_Stylized.prefab");UnityEngine.Object.DestroyImmediate(root);
   Undo.RecordObject(set,"Replace weapon visual with painted working copy");
   set.weaponPrefab=prefab;set.bladeGeometryCalibrated=true;set.bladeAxis=Vector3.forward;
   set.bladeBaseLocal=new Vector3(0,0,(grip-(kind=="Katana"?.69f:.64f))*scale);
   set.bladeTipLocal=new Vector3(0,0,desiredTip);
   EditorUtility.SetDirty(set);
   report.Add(kind+": "+tri.Length/3+" triangles; "+vertices.Length+" source vertices; "+wire.vertexCount+" wire vertices; "+hidden+" triangle-edge sides hidden; texture 2048; tip "+desiredTip.ToString("F3")+"m; original grip/animation fields preserved.");
  }
  AssetDatabase.SaveAssets();File.WriteAllLines(Output+"/Unity_Weapon_Installation.txt",report);
  Debug.Log("Painted Katana and Big Sword installed with original polygon wire. Backups kept; combat timing, clips and damage unchanged.");
 }
 [MenuItem("Tools/Weapon Polish/2 Inspect katana in gameplay")]
 static void Katana()=>Inspect("Katana");
 [MenuItem("Tools/Weapon Polish/3 Inspect big sword in gameplay")]
 static void Big()=>Inspect("BigSword");
 [MenuItem("Tools/Weapon Polish/4 Test katana close view")]
 static void TestKatana()=>Test("Katana");
 [MenuItem("Tools/Weapon Polish/5 Test big sword close view")]
 static void TestBig()=>Test("BigSword");
 [MenuItem("Tools/Weapon Polish/6 Rotate inspection camera")]
 static void RotateView()
 {
  var camera=UnityEngine.Object.FindFirstObjectByType<PlayerCameraController>();
  if(!EditorApplication.isPlaying||!camera)return;
  var f=typeof(PlayerCameraController).GetField("yaw",BindingFlags.Instance|BindingFlags.NonPublic);
  f.SetValue(camera,(float)f.GetValue(camera)+90);
  EditorApplication.isPaused=false;
 }
 static WeaponSocket testingSocket;static AttackController testingAttack;static Animator testingAnimator;
 static string testingKind;static double testStart;static bool startedFirst,startedSecond,acceptedFirst,acceptedSecond;
 static int attackFrames,samples;static float maxGripError;static readonly List<string> testReport=new List<string>();
 static void Test(string kind)
 {
  EditorApplication.isPaused=false;Inspect(kind);
  var player=GameObject.Find("Player");testingSocket=player.GetComponent<WeaponSocket>();
  testingAttack=player.GetComponent<AttackController>();testingAnimator=player.GetComponentInChildren<Animator>();
  var camera=UnityEngine.Object.FindFirstObjectByType<PlayerCameraController>();
  if(camera)
  {
   const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
   typeof(PlayerCameraController).GetField("distance",flags).SetValue(camera,2.25f);
   typeof(PlayerCameraController).GetField("combatDistance",flags).SetValue(camera,2.25f);
   typeof(PlayerCameraController).GetField("pitch",flags).SetValue(camera,8f);
   typeof(PlayerCameraController).GetField("yaw",flags).SetValue(camera,player.transform.eulerAngles.y+135);
  }
  testingKind=kind;testStart=EditorApplication.timeSinceStartup;
  startedFirst=startedSecond=acceptedFirst=acceptedSecond=false;attackFrames=samples=0;maxGripError=0;testReport.Clear();
  EditorApplication.update-=SampleTest;EditorApplication.update+=SampleTest;
 }
 static void SampleTest()
 {
  if(!EditorApplication.isPlaying||!testingSocket){EditorApplication.update-=SampleTest;return;}
  double age=EditorApplication.timeSinceStartup-testStart;
  if(age>1&&!startedFirst)
  {
   startedFirst=true;acceptedFirst=(bool)typeof(AttackController).GetMethod("CommitLight",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(testingAttack,null);
  }
  if(age>4&&!startedSecond)
  {
   startedSecond=true;acceptedSecond=(bool)typeof(AttackController).GetMethod("CommitLight",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(testingAttack,null);
  }
  if(testingSocket.ActiveBlade&&testingAnimator&&testingAnimator.isHuman)
  {
   var hand=testingAnimator.GetBoneTransform(HumanBodyBones.RightHand);
   var knuckle=testingAnimator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
   var palm=BladePoseResolver.Palm(hand,knuckle,testingSocket.PalmGripAmount);
   if(age>0.5){maxGripError=Mathf.Max(maxGripError,Vector3.Distance(palm,testingSocket.ActiveBlade.position));samples++;}
   if(testingAttack.IsAttacking)attackFrames++;
  }
  if(age<4.4)return;
  EditorApplication.update-=SampleTest;
  var visual=testingSocket.SpawnedWeapon;
  testReport.Add("Active visual: "+(visual?visual.name:"missing"));
  if(visual)foreach(var f in visual.GetComponentsInChildren<MeshFilter>(true))testReport.Add("Mesh: "+AssetDatabase.GetAssetPath(f.sharedMesh)+"; bounds "+f.sharedMesh.bounds);
  if(visual)foreach(var r in visual.GetComponentsInChildren<Renderer>(true))foreach(var m in r.sharedMaterials)
   testReport.Add("Material: "+m.name+"; shader "+m.shader.name+"; Base Color "+AssetDatabase.GetAssetPath(m.GetTexture("_BaseMap"))+"; energy mask "+(m.HasProperty("_EmissionMask")?AssetDatabase.GetAssetPath(m.GetTexture("_EmissionMask")):"missing"));
  testReport.Add("Two normal attacks accepted: "+acceptedFirst+", "+acceptedSecond+"; sampled attack updates: "+attackFrames+"; total pose samples: "+samples+"; max grip-to-palm error: "+maxGripError.ToString("F4")+" metres.");
  testReport.Add("Temporary close gameplay camera for visual inspection; animation paused during second swing. Stop Play Mode to restore normal camera and selection.");
  File.WriteAllLines(Output+"/"+testingKind+"_Live_Inspection.txt",testReport);
  EditorApplication.isPaused=true;
  Debug.Log(testingKind+" attack/grip test recorded; paused on second swing for inspection.");
 }
 static void Inspect(string kind)
 {
  if(!EditorApplication.isPlaying)throw new InvalidOperationException("Enter Play Mode first.");
  var player=GameObject.Find("Player");var socket=player.GetComponent<WeaponSocket>();
  var set=AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/Combat/"+kind+".asset");
  socket.Equip(set);socket.KeepDrawn(600);
  var camera=UnityEngine.Object.FindFirstObjectByType<PlayerCameraController>();
  if(camera)
  {
   const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
   typeof(PlayerCameraController).GetField("mouseSensitivity",flags)?.SetValue(camera,0f);
   typeof(PlayerCameraController).GetField("pitch",flags)?.SetValue(camera,14f);
   typeof(PlayerCameraController).GetField("yaw",flags)?.SetValue(camera,player.transform.eulerAngles.y+180);
  }
  File.WriteAllText(Output+"/"+kind+"_Live_Inspection.txt","Equipped through existing WeaponSocket. Shader and grip checks pending visual inspection. Temporary camera and weapon selection revert on leaving Play Mode.");
  Debug.Log(kind+" gameplay inspection active. Existing camera distance and lighting; temporary test settings only.");
 }
}


public static class PlayerPolishAudit
{
 const string Report=@"C:\Users\milkw\Documents\Codex\2026-10-04\open-my-blender-character-project-and\outputs\PlayerPolish";
 [MenuItem("Tools/Player Polish/1 Audit weapon and animation")]
 static void Audit()
 {
  Directory.CreateDirectory(Report);var lines=new List<string>();
  var controller=AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>("Assets/_Project/Animators/PlayerBase.controller");
  var sprint=controller.layers[0].stateMachine.states.First(s=>s.state.name=="CombatSprint").state.motion as AnimationClip;
  lines.Add("Base sprint: "+Describe(sprint));
  foreach(var kind in new[]{"Katana","BigSword"})
  {
   var set=AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/Combat/"+kind+".asset");
   var before=AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/WeaponPolish/"+kind+"_BeforeReplacement.asset");
   lines.Add(kind+" override controller: "+(set.overrideController?set.overrideController.name:"none"));
   if(set.overrideController && set.overrideController is AnimatorOverrideController aoc)lines.Add(kind+" resolved sprint: "+Describe(aoc[sprint.name]));
   if(BladeGeometry.TryMeasure(before.weaponPrefab.transform,out var b,out var t,out var axis))
    lines.Add(kind+" ORIGINAL base="+b.ToString("F5")+" tip="+t.ToString("F5")+" axis="+axis.ToString("F5"));
   foreach(var prefab in new[]{before.weaponPrefab,set.weaponPrefab})
   {
    foreach(var f in prefab.GetComponentsInChildren<MeshFilter>(true))
    {
     var points=f.sharedMesh.vertices.Select(p=>prefab.transform.InverseTransformPoint(f.transform.TransformPoint(p))).ToArray();
     var bounds=new Bounds(points[0],Vector3.zero);foreach(var p in points)bounds.Encapsulate(p);
     lines.Add(prefab.name+" / "+f.name+" ROOT bounds="+bounds.ToString("F5")+" root Euler="+prefab.transform.eulerAngles);
     var bladePoints=points.Where(p=>p.z>.4f&&p.z<.8f).ToArray();
     if(bladePoints.Length>0){var slice=new Bounds(bladePoints[0],Vector3.zero);foreach(var p in bladePoints)slice.Encapsulate(p);lines.Add("Blade midsection: "+slice.ToString("F5"));}
    }
   }
  }
  var paths=AssetDatabase.FindAssets("Run_Fast",new[]{"Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_Katana_Blade"});
  foreach(var guid in paths)
   foreach(var clip in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)).OfType<AnimationClip>())
    if(!clip.name.StartsWith("__"))lines.Add("Available: "+Describe(clip));
  if(EditorApplication.isPlaying)
  {
   var player=GameObject.Find("Player");var animator=player.GetComponentInChildren<Animator>();
   lines.Add("LIVE controller: "+animator.runtimeAnimatorController.name);
   lines.Add("LIVE current: "+string.Join(",",animator.GetCurrentAnimatorClipInfo(0).Select(c=>c.clip.name)));
  }
  File.WriteAllLines(Report+"/Audit.txt",lines);Debug.Log(string.Join("\n",lines));
 }
 static string Describe(AnimationClip clip)
 {
  if(!clip)return "NULL";
  AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip,out string guid,out long id);
  return clip.name+" | "+AssetDatabase.GetAssetPath(clip)+" | "+guid+":"+id+" | loop="+AnimationUtility.GetAnimationClipSettings(clip).loopTime+" human="+clip.humanMotion;
 }
 [MenuItem("Tools/Player Polish/2 Preview death banner")]
 static void PreviewDeath()
 {
  if(!EditorApplication.isPlaying)throw new InvalidOperationException("Enter Play Mode first.");
  EditorApplication.isPaused=false;
  var host=GameObject.Find("Player").GetComponent<WeaponSocket>();
  host.StartCoroutine(DeathPreview());
 }
 static System.Collections.IEnumerator DeathPreview()
 {
  yield return GameHud.DeathOverlay(2f);
  EditorApplication.isPaused=true;
 }
 static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 [MenuItem("Tools/Player Polish/5 Inspect katana run A")]
 static void InspectRunA()=>InspectRun("Run_Fast_ver_A");
 [MenuItem("Tools/Player Polish/6 Inspect katana run B")]
 static void InspectRunB()=>InspectRun("Run_Fast_ver_B");
 static void InspectRun(string take)
 {
  if(!EditorApplication.isPlaying)throw new InvalidOperationException("Enter Play Mode first.");
  EditorApplication.isPaused=false;GameObject.Find("Player").GetComponent<WeaponSocket>().StartCoroutine(RunPose(take));
 }
 static System.Collections.IEnumerator RunPose(string take)
 {
  yield return GameHud.FadeDeathOut(.1f);
  var player=GameObject.Find("Player");var socket=player.GetComponent<WeaponSocket>();var loco=player.GetComponent<PlayerLocomotion>();
  loco.enabled=false;
  var set=AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/Combat/Katana.asset");socket.Equip(set);socket.KeepDrawn(600);
  var cam=UnityEngine.Object.FindFirstObjectByType<PlayerCameraController>();
  typeof(PlayerCameraController).GetField("mouseSensitivity",Private).SetValue(cam,0f);
  typeof(PlayerCameraController).GetField("yaw",Private).SetValue(cam,player.transform.eulerAngles.y+140f);
  typeof(PlayerCameraController).GetField("pitch",Private).SetValue(cam,10f);
  typeof(PlayerCameraController).GetField("distance",Private).SetValue(cam,2.8f);
  typeof(PlayerCameraController).GetField("combatDistance",Private).SetValue(cam,2.8f);
  yield return new WaitForSecondsRealtime(.7f);
  var animator=player.GetComponentInChildren<Animator>();
  var clip=AssetDatabase.LoadAllAssetsAtPath("Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_Katana_Blade/1_Movements/4__Run/M_katana_Blade@"+take+".FBX").OfType<AnimationClip>().First(c=>c.name==take);
  var oc=UnityEngine.Object.Instantiate(set.overrideController);var pairs=new List<KeyValuePair<AnimationClip,AnimationClip>>();oc.GetOverrides(pairs);
  var index=pairs.FindIndex(p=>p.Key.name=="BS_Run_Fast_ver_A_Loop");pairs[index]=new KeyValuePair<AnimationClip,AnimationClip>(pairs[index].Key,clip);oc.ApplyOverrides(pairs);
  animator.runtimeAnimatorController=oc;
  var shadow=(Animator)typeof(WeaponSocket).GetField("shadowAnim",Private).GetValue(socket);if(shadow)shadow.runtimeAnimatorController=oc;
  animator.ResetTrigger("WeaponDraw");animator.SetBool("Locked",true);animator.SetBool("Sprinting",true);animator.SetFloat("SprintSpeedScale",1);
  animator.Play("CombatSprint",0,.15f);
  yield return new WaitForSecondsRealtime(.2f);
  Debug.Log("Katana run preview: "+take+"; temporary runtime override only. Stop Play to restore.");EditorApplication.isPaused=true;
 }
 [MenuItem("Tools/Player Polish/4 Test combat and exploration running")]
 static void TestRunning()
 {
  if(!EditorApplication.isPlaying)throw new InvalidOperationException("Enter Play Mode first.");
  EditorApplication.isPaused=false;
  GameObject.Find("Player").GetComponent<WeaponSocket>().StartCoroutine(RunTest());
 }
 static System.Collections.IEnumerator RunTest()
 {
  Directory.CreateDirectory(Report);var lines=new List<string>();
  var player=GameObject.Find("Player");var socket=player.GetComponent<WeaponSocket>();var loco=player.GetComponent<PlayerLocomotion>();
  var animator=player.GetComponentInChildren<Animator>();var cc=player.GetComponent<CharacterController>();
  var position=player.transform.position;var rotation=player.transform.rotation;
  var moveField=typeof(PlayerLocomotion).GetField("moveAction",Private);var sprintField=typeof(PlayerLocomotion).GetField("sprintAction",Private);
  var oldMove=(UnityEngine.InputSystem.InputAction)moveField.GetValue(loco);var oldSprint=(UnityEngine.InputSystem.InputAction)sprintField.GetValue(loco);
  var toggleField=typeof(PlayerLocomotion).GetField("toggleSprint",Private);var toggle=(bool)toggleField.GetValue(loco);
  var pad=UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>();
  var move=new UnityEngine.InputSystem.InputAction("PolishMove",UnityEngine.InputSystem.InputActionType.Value,"<Gamepad>{PlayerPolishTest}/leftStick");
  var sprint=new UnityEngine.InputSystem.InputAction("PolishSprint",UnityEngine.InputSystem.InputActionType.Button,"<Gamepad>{PlayerPolishTest}/leftStickPress");
  UnityEngine.InputSystem.InputSystem.SetDeviceUsage(pad,"PlayerPolishTest");
  var camera=UnityEngine.Object.FindFirstObjectByType<PlayerCameraController>();var sensitivity=typeof(PlayerCameraController).GetField("mouseSensitivity",Private);
  var oldSensitivity=camera?(float)sensitivity.GetValue(camera):0f;
  if(camera)sensitivity.SetValue(camera,0f);
  oldMove.Disable();oldSprint.Disable();move.Enable();sprint.Enable();moveField.SetValue(loco,move);sprintField.SetValue(loco,sprint);toggleField.SetValue(loco,false);
  try
  {
   foreach(var phase in new[]{"Katana","BigSword","Exploration"})
   {
    cc.enabled=false;player.transform.SetPositionAndRotation(position,rotation);cc.enabled=true;loco.SetLocomotionSpeed(0f);
    socket.Equip(AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/Combat/"+(phase=="Exploration"?"Katana":phase)+".asset"));
    if(phase!="Exploration")socket.KeepDrawn(30);
    else typeof(WeaponSocket).GetField("drawnUntil",Private).SetValue(socket,-1f);
    UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState());
    yield return new WaitForSecondsRealtime(0.6f);
    UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState{leftStick=Vector2.up}.WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.LeftStick));
    float age=0;var clips=new HashSet<string>();int combatSprintFrames=0;float peakSpeed=0;float maxPalmError=0;
    while(age<1.5f)
    {
     age+=Time.unscaledDeltaTime;
     if(age>.5f)
     {
      foreach(var ci in animator.GetCurrentAnimatorClipInfo(0))if(ci.weight>.1f)clips.Add(Describe(ci.clip));
      if(animator.GetCurrentAnimatorStateInfo(0).IsName("CombatSprint"))combatSprintFrames++;
      peakSpeed=Mathf.Max(peakSpeed,loco.ActualPlanarSpeed);
      if(socket.ActiveBlade){var hand=animator.GetBoneTransform(HumanBodyBones.RightHand);var knuckle=animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);maxPalmError=Mathf.Max(maxPalmError,Vector3.Distance(socket.ActiveBlade.position,BladePoseResolver.Palm(hand,knuckle,socket.PalmGripAmount)));}
     }
     yield return null;
    }
    lines.Add(phase+": InCombat="+socket.InCombat+" Sprinting="+loco.Sprinting+" CombatSprint frames="+combatSprintFrames+" peakSpeed="+peakSpeed.ToString("F2")+" palm error="+maxPalmError.ToString("F4")+" position="+player.transform.position);
    foreach(var clip in clips)lines.Add("  "+clip);
    UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,new UnityEngine.InputSystem.LowLevel.GamepadState());
    yield return new WaitForSecondsRealtime(.3f);
   }
  }
  finally
  {
   move.Disable();sprint.Disable();moveField.SetValue(loco,oldMove);sprintField.SetValue(loco,oldSprint);oldMove.Enable();oldSprint.Enable();toggleField.SetValue(loco,toggle);
   move.Dispose();sprint.Dispose();UnityEngine.InputSystem.InputSystem.RemoveDevice(pad);
   if(camera)sensitivity.SetValue(camera,oldSensitivity);
   cc.enabled=false;player.transform.SetPositionAndRotation(position,rotation);cc.enabled=true;loco.SetLocomotionSpeed(0f);
   File.WriteAllLines(Report+"/RunningTest.txt",lines);
  }
  socket.Equip(AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/Combat/Katana.asset"));
  Debug.Log("Combat and exploration run test finished; temporary input/camera settings restored.");
 }
 [MenuItem("Tools/Player Polish/3 Apply katana corrections")]
 static void FixKatana()
 {
  if(EditorApplication.isPlaying)throw new InvalidOperationException("Exit Play Mode first.");
  Directory.CreateDirectory(Report);
  var set=AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/Combat/Katana.asset");
  var controller=AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>("Assets/_Project/Animators/PlayerBase.controller");
  var sprint=controller.layers[0].stateMachine.states.First(s=>s.state.name=="CombatSprint").state.motion as AnimationClip;
  var source=AssetDatabase.LoadAllAssetsAtPath("Assets/ThirdParty/GrruzamPowerfulSword/Animation/M_Katana_Blade/1_Movements/4__Run/M_katana_Blade@Run_Fast_Root_ver_A.FBX").OfType<AnimationClip>().First(c=>c.name=="Run_Fast_Root_ver_A");
  const string clipPath="Assets/_Project/Combat/Clips/KT_Run_Fast_ver_B_Loop.anim";
  var clip=source;
  if(!clip)
  {
   clip=UnityEngine.Object.Instantiate(source);clip.name="KT_Run_Fast_ver_B_Loop";
   var settings=AnimationUtility.GetAnimationClipSettings(clip);settings.loopTime=true;
   settings.keepOriginalPositionY=true;settings.loopBlendPositionY=true;
   AnimationUtility.SetAnimationClipSettings(clip,settings);AssetDatabase.CreateAsset(clip,clipPath);
  }
  Undo.RecordObject(set,"Correct katana blade roll");set.bladeRoll=90f;EditorUtility.SetDirty(set);
  var overrides=new List<KeyValuePair<AnimationClip,AnimationClip>>();set.overrideController.GetOverrides(overrides);
  var index=overrides.FindIndex(pair=>pair.Key==sprint);
  if(index<0)throw new InvalidOperationException("CombatSprint key is missing.");
  Undo.RecordObject(set.overrideController,"Use katana combat sprint");overrides[index]=new KeyValuePair<AnimationClip,AnimationClip>(sprint,clip);
  set.overrideController.ApplyOverrides(overrides);EditorUtility.SetDirty(set.overrideController);
  AssetDatabase.SaveAssets();File.WriteAllText(Report+"/Corrections.txt","Katana blade roll: 90 degrees around +Z; palm pivot and endpoints preserved.\nCombatSprint -> "+Describe(clip)+"\nExploration locomotion and Big Sword controller unchanged.");
  Debug.Log("Katana roll and project-owned combat sprint updated. Original assets preserved.");
 }
}

public static class MovementPolishCheck
{
 static bool previewWall;
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
 const string Output=@"C:\Users\milkw\Documents\Codex\2026-10-04\open-my-blender-character-project-and\outputs\MovementPolish";
 static FieldInfo Field(object obj,string name)=>obj.GetType().GetField(name,Flags);
 static object Get(object obj,string name)=>Field(obj,name).GetValue(obj);
 static void Set(object obj,string name,object value)=>Field(obj,name).SetValue(obj,value);
 [MenuItem("Tools/Movement Polish/1 Test traversal and boss contact windows")]
 static void Test()
 {
  if(!EditorApplication.isPlaying)throw new InvalidOperationException("Enter Play Mode first.");
  EditorApplication.isPaused=false;
  GameObject.Find("Player").GetComponent<PlayerLocomotion>().StartCoroutine(Run());
 }
 static System.Collections.IEnumerator Run()
 {
  Directory.CreateDirectory(Output);var lines=new List<string>();
  var player=GameObject.Find("Player");var loco=player.GetComponent<PlayerLocomotion>();var slide=player.GetComponent<SlideController>();
  var wall=player.GetComponent<WallRunController>();var socket=player.GetComponent<WeaponSocket>();var cc=player.GetComponent<CharacterController>();
  var anim=player.GetComponentsInChildren<Animator>().First(a=>a.isHuman&&a.runtimeAnimatorController);var fx=player.GetComponent<TraversalEffects>();
  var start=player.transform.position;var rotation=player.transform.rotation;var visual=anim.transform;var visualPosition=visual.localPosition;
  var cam=UnityEngine.Object.FindFirstObjectByType<PlayerCameraController>();var sensitivity=Get(cam,"mouseSensitivity");Set(cam,"mouseSensitivity",0f);
  var pad=UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>();UnityEngine.InputSystem.InputSystem.SetDeviceUsage(pad,"TraversalTest");
  var replacements=new List<Tuple<object,string,UnityEngine.InputSystem.InputAction,UnityEngine.InputSystem.InputAction>>();
  UnityEngine.InputSystem.InputAction Swap(object owner,string field,string binding,UnityEngine.InputSystem.InputActionType type)
  {
   var old=(UnityEngine.InputSystem.InputAction)Get(owner,field);old?.Disable();
   var next=new UnityEngine.InputSystem.InputAction(field,type,"<Gamepad>{TraversalTest}/"+binding);next.Enable();Set(owner,field,next);
   replacements.Add(Tuple.Create(owner,field,old,next));return next;
  }
  Swap(loco,"moveAction","leftStick",UnityEngine.InputSystem.InputActionType.Value);
  Swap(loco,"sprintAction","leftStickPress",UnityEngine.InputSystem.InputActionType.Button);
  Swap(loco,"jumpAction","buttonSouth",UnityEngine.InputSystem.InputActionType.Button);
  Swap(slide,"moveAction","leftStick",UnityEngine.InputSystem.InputActionType.Value);
  Swap(slide,"slideAction","buttonEast",UnityEngine.InputSystem.InputActionType.Button);
  bool toggle=(bool)Get(loco,"toggleSprint");Set(loco,"toggleSprint",false);
  void Pad(Vector2 move,bool sprint=false,bool dodge=false,bool jump=false)
  {
   var input=new UnityEngine.InputSystem.LowLevel.GamepadState{leftStick=move};
   if(sprint)input=input.WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.LeftStick);
   if(dodge)input=input.WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.East);
   if(jump)input=input.WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.South);
   UnityEngine.InputSystem.InputSystem.QueueStateEvent(pad,input);
  }
  void Reset()
  {
   Pad(Vector2.zero);cc.enabled=false;player.transform.SetPositionAndRotation(start,rotation);cc.enabled=true;
   loco.CompleteExternalLanding();loco.SetLocomotionSpeed(0);anim.Play("Locomotion",0,0);
  }
  GameObject testWall=null;
  bool socketEnabled=socket.enabled;
  try
  {
   socket.Equip(AssetDatabase.LoadAssetAtPath<WeaponSet>("Assets/_Project/Combat/Katana.asset"));Set(socket,"drawnUntil",-1f);
   // Nearby live enemies must not turn the out-of-combat slide test into a combat dodge.
   socket.enabled=false;Set(socket,"<InCombat>k__BackingField",false);
   if(socket.ActiveBlade)socket.ActiveBlade.gameObject.SetActive(false);
   Reset();yield return new WaitForSeconds(1f);Pad(Vector2.up,true);yield return new WaitForSeconds(.5f);
   float settle=0f;while(anim.IsInTransition(0)&&settle<2f){settle+=Time.deltaTime;yield return null;}
   Pad(Vector2.up,true,true);float age=0,maxOffset=0;int slideFrames=0,invisible=0;var mesh=anim.GetComponentsInChildren<SkinnedMeshRenderer>().First();
   while(age<1.7f)
   {
    age+=Time.deltaTime;maxOffset=Mathf.Max(maxOffset,Vector3.ProjectOnPlane(anim.GetBoneTransform(HumanBodyBones.Hips).position-player.transform.position,Vector3.up).magnitude);
    if(slide.IsSliding)slideFrames++;if(!mesh.isVisible)invisible++;yield return null;
   }
   lines.Add($"Held slide: active frames={slideFrames}, max hips/capsule planar separation={maxOffset:F3}m, renderer invisible frames={invisible}, recovering={Get(slide,"recovering")}");
   Pad(Vector2.zero);yield return new WaitForSeconds(.25f);Reset();yield return new WaitForSeconds(.3f);
   Pad(Vector2.up,true);yield return new WaitForSeconds(.4f);Pad(Vector2.up,true,true);yield return new WaitForSeconds(.15f);
   Pad(Vector2.up,true,false,true);yield return new WaitForSeconds(.1f);Pad(Vector2.up,true);yield return new WaitForSeconds(.4f);
   Pad(Vector2.up,true,false,true);yield return new WaitForSeconds(.12f);Pad(Vector2.up,true);
   yield return new WaitForSeconds(1.4f);lines.Add($"Slide-jump/air jump: ground bursts={fx.JumpBursts}, double bursts={fx.DoubleJumpBursts}, boost bursts={fx.BoostBursts}, particles={fx.GetComponentInChildren<ParticleSystem>().particleCount}");
   Reset();yield return new WaitForSeconds(.4f);
   testWall=GameObject.CreatePrimitive(PrimitiveType.Cube);testWall.name="Temporary traversal test wall";testWall.AddComponent<WallRunSurface>();
   var forward=Vector3.ProjectOnPlane(Camera.main.transform.forward,Vector3.up).normalized;var right=Vector3.Cross(Vector3.up,forward);
   testWall.transform.SetPositionAndRotation(start+right*.9f+Vector3.up*2f+forward*10f,Quaternion.LookRotation(forward));testWall.transform.localScale=new Vector3(.3f,6f,35f);
   Pad(Vector2.up,true);yield return new WaitForSeconds(.4f);Pad(Vector2.up,true,false,true);yield return new WaitForSeconds(.1f);Pad(Vector2.up,true);
   age=0;int attached=0,maxRings=0;while(age<1.3f){age+=Time.deltaTime;if(wall.AttachedToWall)attached++;maxRings=Mathf.Max(maxRings,fx.VisibleWallRings);if(previewWall&&fx.VisibleWallRings==4&&attached>3){previewWall=false;EditorApplication.isPaused=true;}yield return null;}
   lines.Add($"Actual wall attachment: frames={attached}, maximum hand/foot rings={maxRings}");
   Pad(Vector2.zero);yield return new WaitForSeconds(1.5f);UnityEngine.Object.Destroy(testWall);testWall=null;Reset();yield return new WaitForSeconds(.5f);
   socket.enabled=socketEnabled;socket.KeepDrawn(60);yield return new WaitForSeconds(1f);Pad(Vector2.up,true);yield return new WaitForSeconds(.65f);
   lines.Add("Actual combat sprint: "+string.Join(", ",anim.GetCurrentAnimatorClipInfo(0).Where(c=>c.weight>.1f).Select(c=>c.clip.name+" weight="+c.weight.ToString("F2")+" rootMotion="+c.clip.hasRootCurves)));
   lines.Add("Weapon="+socket.Set.name+" inCombat="+socket.InCombat+" Sprinting="+loco.Sprinting+" state="+anim.GetCurrentAnimatorStateInfo(0).shortNameHash);
   Pad(Vector2.zero);yield return new WaitForSeconds(.5f);
   var dodge=player.GetComponent<DodgeController>();var dodgeStart=player.transform.position;bool dodgeStarted=dodge.TryDodge(Vector2.right);float immuneAge=0;int dodgeFrames=0;
   while(dodge.IsDodging&&dodgeFrames<120){if(player.GetComponent<PlayerState>().IsInvulnerable)immuneAge+=Time.deltaTime;dodgeFrames++;yield return null;}
   bool earlyRepeat=dodge.TryDodge(Vector2.right);
   lines.Add($"Combat dodge: started={dodgeStarted}, travel={Vector3.ProjectOnPlane(player.transform.position-dodgeStart,Vector3.up).magnitude:F2}m, measured immunity={immuneAge:F3}s, immediate repeat accepted={earlyRepeat}");
   BossCheck(player,lines);File.WriteAllLines(Output+"/PlayModeChecks.txt",lines);
   Debug.Log("Movement polish checks: "+string.Join("\n",lines));
  }
  finally
  {
   Pad(Vector2.zero);foreach(var r in replacements){r.Item4.Disable();Set(r.Item1,r.Item2,r.Item3);r.Item3?.Enable();r.Item4.Dispose();}
   Set(loco,"toggleSprint",toggle);Set(cam,"mouseSensitivity",sensitivity);UnityEngine.InputSystem.InputSystem.RemoveDevice(pad);
   socket.enabled=socketEnabled;
   if(testWall)UnityEngine.Object.Destroy(testWall);
   cc.enabled=false;player.transform.SetPositionAndRotation(start,rotation);cc.enabled=true;
   loco.CompleteExternalLanding();loco.SetLocomotionSpeed(0f);visual.localPosition=visualPosition;anim.speed=1f;
   anim.Play("Locomotion",0,0f);player.GetComponent<PlayerState>().IsDisplacing=false;
  }
 }
 static void BossCheck(GameObject player,List<string> report)
 {
  var hp=player.GetComponent<PlayerHealth>();var state=player.GetComponent<PlayerState>();var cc=player.GetComponent<CharacterController>();
  var pos=player.transform.position;var wasInvul=state.IsInvulnerable;
  foreach(var boss in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None).Where(b=>b is BossGolem||b is BossLord))
  {
   // Exercise the real contact method: a miss stays armed, immunity stays armed,
   // contact consumes the window. No attack data or scene objects are saved.
   var moves=(Array)Get(boss,"p1Moves");if(moves==null||moves.Length==0)continue;
   var move=moves.GetValue(0);var bossPos=boss.transform.position;var bossRot=boss.transform.rotation;
   var oldHp=hp.Current;var oldGrace=Get(hp,"graceT");
   try
   {
    Set(boss,"player",player.transform);Set(boss,"playerHealth",hp);Set(boss,"playerState",state);
    boss.transform.SetPositionAndRotation(pos+Vector3.forward*2,Quaternion.LookRotation(Vector3.back));
    var strike=boss.GetType().GetMethod("Strike",Flags);
    state.IsInvulnerable=false;Set(hp,"graceT",0f);cc.enabled=false;player.transform.position=pos+Vector3.right*20;
    bool miss=(bool)strike.Invoke(boss,new[]{move,(object)false});player.transform.position=pos;
    state.IsInvulnerable=true;bool immune=(bool)strike.Invoke(boss,new[]{move,(object)false});state.IsInvulnerable=false;
    bool hit=(bool)strike.Invoke(boss,new[]{move,(object)false});float damage=oldHp-hp.Current;
    report.Add(boss.GetType().Name+$" contact: out-of-range consumes={miss}, i-frame consumes={immune}, exposed contact={hit}, damage={damage:F1}");
    hp.Heal();Set(hp,"graceT",0f);
    var bossAnim=(Animator)Get(boss,"bossAnimator");var oldCurrent=Get(boss,"current");var oldState=bossAnim.GetCurrentAnimatorStateInfo(0);
    var windows=(Vector2[])move.GetType().GetField("windows").GetValue(move);
    var moveId=(int)move.GetType().GetField("id").GetValue(move);var tick=boss.GetType().GetMethod("TickAttack",Flags);
    try
    {
     Set(boss,"current",move);move.GetType().GetField("win").SetValue(move,0);move.GetType().GetField("struck").SetValue(move,false);
     move.GetType().GetField("effectPlayed").SetValue(move,true);move.GetType().GetField("sampledTime").SetValue(move,0f);
     bossAnim.Play(moveId,0,Mathf.Lerp(windows[0].x,windows[0].y,.2f));bossAnim.Update(0f);
     player.transform.position=pos+Vector3.right*20;tick.Invoke(boss,new object[]{0f});
     player.transform.position=pos;tick.Invoke(boss,new object[]{0f});float firstDamage=hp.Max-hp.Current;
     Set(hp,"graceT",0f);tick.Invoke(boss,new object[]{0f});
     report.Add(boss.GetType().Name+$" active window: miss then enter damage={firstDamage:F1}, repeated same-window damage={hp.Max-hp.Current-firstDamage:F1}");
    }
    finally{Set(boss,"current",oldCurrent);bossAnim.Play(oldState.fullPathHash,0,oldState.normalizedTime);bossAnim.Update(0f);}
   }
   finally
   {
    boss.transform.SetPositionAndRotation(bossPos,bossRot);player.transform.position=pos;cc.enabled=true;state.IsInvulnerable=wasInvul;
    hp.Heal();Set(hp,"graceT",oldGrace);
   }
  }
 }
 [MenuItem("Tools/Movement Polish/2 Preview double-jump effect")]
 static void Preview()
 {
  if(!EditorApplication.isPlaying)throw new InvalidOperationException("Enter Play Mode first.");EditorApplication.isPaused=false;
  var loco=GameObject.Find("Player").GetComponent<PlayerLocomotion>();loco.StartCoroutine(PreviewPulse(loco));
 }
 static System.Collections.IEnumerator PreviewPulse(PlayerLocomotion loco)
 {
  loco.GetComponent<TraversalEffects>().JumpBurst(true);yield return new WaitForSeconds(.1f);
  var ps=loco.GetComponent<TraversalEffects>().GetComponentInChildren<ParticleSystem>();
  Directory.CreateDirectory(Output);File.WriteAllText(Output+"/VfxPreview.txt","Double jump preview: live particles="+ps.particleCount+", simulation playing="+ps.isPlaying+", shader supported="+ps.GetComponent<ParticleSystemRenderer>().sharedMaterial.shader.isSupported);
  EditorApplication.isPaused=true;
 }
 [MenuItem("Tools/Movement Polish/3 Inspect actual wall-run rings")]
 static void WallPreview(){previewWall=true;Test();}
}

