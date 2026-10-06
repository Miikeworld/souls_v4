$ErrorActionPreference = 'Stop'
$resolver = Get-Content -LiteralPath 'Assets/_Project/Scripts/Core/BladePoseResolver.cs' -Raw
# Numerics host only: runs the actual resolver math without loading Unity native runtime.
$hostTypes = @"
namespace UnityEngine {
 public struct Vector3 {
  public float x,y,z;
  public Vector3(float x,float y,float z) {this.x=x;this.y=y;this.z=z;}
  public static Vector3 zero {get{return new Vector3();}}
  public static Vector3 forward {get{return new Vector3(0,0,1);}}
  public float sqrMagnitude {get{return x*x+y*y+z*z;}}
  public Vector3 normalized {get{var l=(float)System.Math.Sqrt(sqrMagnitude);return l>0?this*(1/l):zero;}}
  public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
  public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
  public static Vector3 operator *(Vector3 a,float b){return new Vector3(a.x*b,a.y*b,a.z*b);}
  public static Vector3 Lerp(Vector3 a,Vector3 b,float t){return a+(b-a)*Mathf.Clamp01(t);}
 }
 public struct Quaternion {
  public float x,y,z,w;
  public Quaternion(float x,float y,float z,float w){this.x=x;this.y=y;this.z=z;this.w=w;}
  public static Quaternion identity {get{return new Quaternion(0,0,0,1);}}
  private System.Numerics.Quaternion N {get{return new System.Numerics.Quaternion(x,y,z,w);}}
  private static Quaternion Q(System.Numerics.Quaternion q){return new Quaternion(q.X,q.Y,q.Z,q.W);}
  public static Quaternion operator *(Quaternion a,Quaternion b){return Q(a.N*b.N);}
  public static Vector3 operator *(Quaternion q,Vector3 v){var r=System.Numerics.Vector3.Transform(new System.Numerics.Vector3(v.x,v.y,v.z),q.N);return new Vector3(r.X,r.Y,r.Z);}
  public static Quaternion Inverse(Quaternion q){return Q(System.Numerics.Quaternion.Inverse(q.N));}
  public static Quaternion Slerp(Quaternion a,Quaternion b,float t){return Q(System.Numerics.Quaternion.Slerp(a.N,b.N,t));}
  public static Quaternion AngleAxis(float angle,Vector3 axis){axis=axis.normalized;return Q(System.Numerics.Quaternion.CreateFromAxisAngle(new System.Numerics.Vector3(axis.x,axis.y,axis.z),angle*(float)System.Math.PI/180));}
  public static Quaternion Euler(Vector3 e){return AngleAxis(e.y,new Vector3(0,1,0))*AngleAxis(e.x,new Vector3(1,0,0))*AngleAxis(e.z,new Vector3(0,0,1));}
 }
 public static class Mathf {
  public static float Clamp01(float f){return System.Math.Max(0,System.Math.Min(1,f));}
  public static float Abs(float f){return System.Math.Abs(f);}
 }
 public class Transform {public Vector3 position;}
}
public class WeaponSet {
 public float socketDeltaScale,bladeRoll;
 public UnityEngine.Vector3 weaponPosOffset,weaponRotOffset,bladeAxis=UnityEngine.Vector3.forward;
}
public static class BladeResolverCases {
 private static int count;
 private static void Same(UnityEngine.Vector3 a,UnityEngine.Vector3 b,string what){if((a-b).sqrMagnitude>0.000001f)throw new System.Exception(what);count++;}
 private static UnityEngine.Quaternion Y(float angle){return UnityEngine.Quaternion.AngleAxis(angle,new UnityEngine.Vector3(0,1,0));}
 private static void Rotation(UnityEngine.Quaternion a,UnityEngine.Quaternion b,string what){Same(a*UnityEngine.Vector3.forward,b*UnityEngine.Vector3.forward,what);Same(a*new UnityEngine.Vector3(0,1,0),b*new UnityEngine.Vector3(0,1,0),what+" up");}
 public static int Run(){
  var palm=new UnityEngine.Vector3(1,2,3);
  var localDelta=new UnityEngine.Vector3(.1f,.2f,.3f);
  var sourceFrame=Y(90); var targetFrame=Y(-30);
  var source=BladePoseResolver.Capture(sourceFrame,sourceFrame*localDelta,sourceFrame*Y(45),UnityEngine.Vector3.zero);
  Same(source.handToSocket,localDelta,"Source displacement must be frame-local metres");
  var set=new WeaponSet(); var pose=BladePoseResolver.Resolve(source,targetFrame,palm,Y(90),set);
  Same(pose.position,palm,"Palm lock independent of source translation");
  Rotation(pose.rotation,Y(15),"Source yaw must map into target frame");
  set.socketDeltaScale=.5f;set.weaponPosOffset=new UnityEngine.Vector3(0,0,.1f);
  pose=BladePoseResolver.Resolve(source,targetFrame,palm,Y(90),set);
  Same(pose.position,palm+targetFrame*localDelta*.5f+new UnityEngine.Vector3(.1f,0,0),"Active set offsets applied once in correct frame");
  set.weaponRotOffset=new UnityEngine.Vector3(0,20,0);set.bladeRoll=10;
  pose=BladePoseResolver.Resolve(source,targetFrame,palm,Y(90),set);
  Rotation(pose.rotation,Y(35)*UnityEngine.Quaternion.AngleAxis(10,UnityEngine.Vector3.forward),"Socket correction and blade roll applied once");
  var outgoing=new BladePoseResolver.SourcePose(Y(0),UnityEngine.Vector3.zero);
  var incoming=new BladePoseResolver.SourcePose(Y(90),new UnityEngine.Vector3(2,0,0));
  var blend=BladePoseResolver.Blend(outgoing,incoming,.25f);
  Rotation(blend.rotation,Y(22.5f),"Crossfade uses incoming/outgoing authored orientation");
  Same(blend.handToSocket,new UnityEngine.Vector3(.5f,0,0),"Crossfade position weight");
  Rotation(BladePoseResolver.Blend(outgoing,incoming,-1).rotation,Y(0),"Outgoing endpoint clamp");
  Rotation(BladePoseResolver.Blend(outgoing,incoming,2).rotation,Y(90),"Incoming endpoint clamp");
  foreach(var scale in new[]{.01f,1f,2f})Same(BladePoseResolver.LocalScale(new UnityEngine.Vector3(1,1,1),new UnityEngine.Vector3(scale,scale,scale)),new UnityEngine.Vector3(1/scale,1/scale,1/scale),"World scale compensation "+scale);
  var hand=new UnityEngine.Transform{position=UnityEngine.Vector3.zero};var knuckle=new UnityEngine.Transform{position=new UnityEngine.Vector3(0,0,1)};
  Same(BladePoseResolver.Palm(hand,knuckle,.55f),new UnityEngine.Vector3(0,0,.55f),"Palm grip fraction");
  Same(BladePoseResolver.Palm(hand,null,.55f),hand.position,"Missing knuckle fallback");
  return count;
 }
}
"@
Add-Type -TypeDefinition ($resolver + $hostTypes)
$count = [BladeResolverCases]::Run()
Write-Output "$count actual-source blade pose checks passed using a System.Numerics host. Unity visual/Animator sampling acceptance remains separate."
