$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath 'Assets/_Project/Scripts/Core/RecoveryBuffer.cs' -Raw
Add-Type -TypeDefinition $source
$checks = 0
function Assert-Combat([bool]$pass, [string]$name) {
    if (-not $pass) { throw "FAIL: $name" }
    $script:checks++
}
$buffer = [RecoveryBuffer[string]]::new()
$value = ''
Assert-Combat (-not $buffer.TryTake(0, $true, [ref]$value)) 'Empty buffer'
$buffer.Set('selected art A', 1)
Assert-Combat (-not $buffer.TryTake(1.1, $false, [ref]$value)) 'Windup cannot consume request'
Assert-Combat $buffer.HasRequest 'Request survives waiting'
Assert-Combat ($buffer.TryTake(1.2, $true, [ref]$value)) 'Recovery consumes request'
Assert-Combat ($value -eq 'selected art A') 'Selected art captured'
Assert-Combat (-not $buffer.TryTake(1.21, $true, [ref]$value)) 'One commit only'
$buffer.Set('normal', 2)
$buffer.Set('dodge', 2.05)
Assert-Combat ($buffer.TryTake(2.2, $true, [ref]$value)) 'Latest request commits'
Assert-Combat ($value -eq 'dodge') 'Latest request replaces previous'
$buffer.Set('art', 3)
Assert-Combat (-not $buffer.TryTake(3.251, $true, [ref]$value)) 'Expiry rejects request'
Assert-Combat (-not $buffer.HasRequest) 'Expired request cleared'
$buffer.Set('art', 4)
$buffer.Clear()
Assert-Combat (-not $buffer.TryTake(4.1, $true, [ref]$value)) 'Cancel clears request'
Assert-Combat ([CombatContactClock]::Crosses(.2,.7,.4,.45)) 'Narrow window crossed at low frame rate'
Assert-Combat ([CombatContactClock]::Crosses(.4,.42,.4,.45)) 'Entering contact'
Assert-Combat ([CombatContactClock]::Crosses(.42,.44,.4,.45)) 'Inside contact'
Assert-Combat ([CombatContactClock]::Crosses(.44,.6,.4,.45)) 'Leaving contact'
Assert-Combat (-not [CombatContactClock]::Crosses(.1,.2,.4,.45)) 'Before window'
Assert-Combat (-not [CombatContactClock]::Crosses(.5,.6,.4,.45)) 'After window'
Assert-Combat (-not [CombatContactClock]::Crosses(.6,.2,.4,.45)) 'Clock reset not contact'
Write-Output "$checks focused request/contact checks passed (actual shared source). Unity resource and collision tests pending."
$ErrorActionPreference = 'Stop'
$manaSource = Get-Content -LiteralPath 'Assets/_Project/Scripts/Core/PlayerMana.cs' -Raw
$stubs = @"
namespace UnityEngine {
 public class MonoBehaviour { }
 public class SerializeField : System.Attribute { }
 public class MinAttribute : System.Attribute { public MinAttribute(float f) {} }
 public class TooltipAttribute : System.Attribute { public TooltipAttribute(string s) {} }
 public static class Mathf {
  public static float Max(float a,float b) { return System.Math.Max(a,b); }
  public static float Min(float a,float b) { return System.Math.Min(a,b); }
 }
 public static class Time { public static float deltaTime; }
}
"@
Add-Type -TypeDefinition ($manaSource + $stubs)
$pool = [PlayerMana]::new()
$pool.ConfigureMax(50)
Assert-Combat ($pool.Current -eq 50) 'Mana configured'
Assert-Combat (-not $pool.TrySpend(51)) 'Unaffordable mana rejected'
Assert-Combat ($pool.Current -eq 50) 'Rejected spend preserves mana'
Assert-Combat ($pool.TrySpend(20)) 'Affordable mana accepted'
Assert-Combat ($pool.Current -eq 30) 'One spend subtracts exact cost'
Assert-Combat (-not $pool.TrySpend(31)) 'Second unaffordable request rejected'
Assert-Combat ($pool.Current -eq 30) 'Failed request leaves resource intact'
$pool.Restore(100)
Assert-Combat ($pool.Current -eq 50) 'Restore clamps pool'
Write-Output "$checks total request/contact/resource checks passed. Resource math uses actual PlayerMana source with minimal Unity host stubs; controller transactions and physics still need Unity."
