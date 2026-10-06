$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath 'Assets/_Project/Scripts/Core/AttackController.cs' -Raw
$methods = [regex]::Matches($source, 'public static (?:float|bool) Plunge(?:PreparationVelocity|DescentVelocity|SurfaceWalkable)\([^;]+?=>[^;]+;', 'Singleline')
if ($methods.Count -ne 3) { throw 'Missing actual plunge timing/ground seams' }
$actual = ($methods | ForEach-Object { $_.Value }) -join "`n"
$numericHost = @'
using System;
using UnityEngine;
namespace UnityEngine {
 public struct Vector3 {
  public float x,y,z;
  public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
  public static Vector3 up {get{return new Vector3(0,1,0);}}
  public Vector3 normalized {get{float l=(float)Math.Sqrt(x*x+y*y+z*z);return l>0?new Vector3(x/l,y/l,z/l):new Vector3();}}
  public static float Dot(Vector3 a,Vector3 b){return a.x*b.x+a.y*b.y+a.z*b.z;}
 }
 public static class Mathf {
  public const float Deg2Rad=(float)Math.PI/180;
  public static float Clamp(float x,float a,float b){return Math.Max(a,Math.Min(b,x));}
  public static float Clamp01(float x){return Clamp(x,0,1);}
  public static float Max(float a,float b){return Math.Max(a,b);}
  public static float Min(float a,float b){return Math.Min(a,b);}
  public static float Cos(float a){return (float)Math.Cos(a);}
  public static float Lerp(float a,float b,float t){return a+(b-a)*Clamp01(t);}
  public static float SmoothStep(float a,float b,float t){t=Clamp01(t);return Lerp(a,b,t*t*(3-2*t));}
 }
}
'@
Add-Type -TypeDefinition ($numericHost + "`npublic static class ActualPlunge {`n" + $actual + "`n}")
$checks=0
function Assert-Plunge([bool]$pass,[string]$name) {
 if (-not $pass) { throw "FAIL: $name" }
 $script:checks++
}
# Rising and falling retain their inherited sign while braking; neither receives an upward impulse.
foreach ($inherited in @(6.0,-12.0,0.0)) {
 Assert-Plunge ([ActualPlunge]::PlungePreparationVelocity($inherited,0) -eq $inherited) 'Preparation preserves entry velocity'
 $half=[ActualPlunge]::PlungePreparationVelocity($inherited,0.04)
 Assert-Plunge ([Math]::Abs($half) -le [Math]::Abs($inherited) -and $half*$inherited -ge 0) 'Preparation brakes without reversing velocity'
 Assert-Plunge ([ActualPlunge]::PlungePreparationVelocity($inherited,0.08) -eq 0) 'Braking reaches hang'
 Assert-Plunge ([ActualPlunge]::PlungePreparationVelocity($inherited,0.35) -eq 0) 'Hang remains still'
}
$previous=0.0
foreach ($age in @(0.0,0.03,0.09,0.18,2.0,10.0)) {
 $velocity=[ActualPlunge]::PlungeDescentVelocity($age)
 Assert-Plunge ($velocity -le $previous -and $velocity -ge -18) 'Fall accelerates down without overshoot'
 $previous=$velocity
}
Assert-Plunge ([Math]::Abs([ActualPlunge]::PlungeDescentVelocity(0.18)+18) -lt 0.001) 'Terminal speed reached at 0.18 seconds'
Assert-Plunge ([ActualPlunge]::PlungeDescentVelocity(30) -eq -18) 'High drop stays at terminal speed'
Assert-Plunge ([ActualPlunge]::PlungeSurfaceWalkable([UnityEngine.Vector3]::up,45)) 'Floor is walkable'
Assert-Plunge (-not [ActualPlunge]::PlungeSurfaceWalkable([UnityEngine.Vector3]::new(1,0,0),45)) 'Wall is not landing'
Assert-Plunge (-not [ActualPlunge]::PlungeSurfaceWalkable([UnityEngine.Vector3]::new(0,-1,0),45)) 'Ceiling is not landing'
foreach ($angle in @(30.0,60.0)) {
 $rad=$angle*[Math]::PI/180
 $normal=[UnityEngine.Vector3]::new([Math]::Sin($rad)*3,[Math]::Cos($rad)*3,0)
 Assert-Plunge ([ActualPlunge]::PlungeSurfaceWalkable($normal,45) -eq ($angle -lt 45)) 'Slope uses normalized surface normal'
}
Write-Output "PASS: $checks actual-source plunge velocity/surface checks. Unity contact, pose and device acceptance remain separate."
