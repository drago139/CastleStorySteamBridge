# 0.3.58: pure-logic tests that need no game, Unity or Steam. Release.ps1 runs them before building; any failure stops the release.
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) ('CastleStoryUnitTests_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$suites = [ordered]@{
 'LoopbackAddressTests' = @('LoopbackAddressTests.cs','LoopbackAddress.cs')
 'RadminTimeoutRulesTests' = @('RadminTimeoutRulesTests.cs','RadminTimeoutRules.cs')
 'CatapultMarkerTests' = @('CatapultMarkerTests.cs','CatapultMarker.cs')
 'SmoothingTests' = @('SmoothingTests.cs','SmoothingCore.cs','RotationCore.cs')
 'RotationTests' = @('RotationTests.cs','RotationCore.cs','SmoothingCore.cs')
 'AimReconcileTests' = @('AimReconcileTests.cs','AimReconciler.cs')
 'VirtualPlayerTests' = @('VirtualPlayerTests.cs','VirtualPlayerRules.cs')
 'HostMarkerTests' = @('HostMarkerTests.cs','HostMarkerRules.cs')
 'JoinLoadTests' = @('JoinLoadTests.cs','JoinLoadCore.cs','RollingTrace.cs')
 'ReliabilityTests' = @('ReliabilityTests.cs','ReliabilityCore.cs','UpdateFileStore.cs')
 'ProposalTests'    = @('ProposalTests.cs','ProposalProtocol.cs','ReliabilityCore.cs')
 'ReconnectTests'   = @('ReconnectTests.cs','ReconnectRules.cs')
 'CrateMixTests'    = @('CrateMixTests.cs','CrateMixRules.cs')
 'PingSpikeTests'   = @('PingSpikeTests.cs','PingSpikeRules.cs')
 'TreeCutTests'     = @('TreeCutTests.cs','TreeCutRules.cs')
 'StuckRescueTests' = @('StuckRescueTests.cs','StuckRescueRules.cs')
 'RecipeSlotTests'  = @('RecipeSlotTests.cs','RecipeSlotRules.cs')
 'FreeRunsProbeTests' = @('FreeRunsProbeTests.cs','FreeRunsProbe.cs')
 'OrangeWardPatchTests' = @('OrangeWardPatchTests.cs','OrangeWardPatchRules.cs')
 'BugPackTests'     = @('BugPackTests.cs','BugPackRules.cs')
 'ModInventoryTests' = @('ModInventoryTests.cs','ModInventoryRules.cs','ModBaseline.cs')
 'LinkProbeTests'   = @('LinkProbeTests.cs','LinkRules.cs')
 'WatchdogTests'     = @('WatchdogTests.cs','WatchdogRules.cs')
 'ResilienceTests'   = @('ResilienceTests.cs','ResilienceRules.cs')
 'WardInWallTests'   = @('WardInWallTests.cs','WardInWallRules.cs')
 'JoinMenuTests'     = @('JoinMenuTests.cs','JoinMenuRules.cs')
 'LooseFetchTests'   = @('LooseFetchTests.cs','LooseFetchRules.cs')
 'SaveMapMpTests'    = @('SaveMapMpTests.cs','SaveMapMpRules.cs')
 'DoorAttackTests'   = @('DoorAttackTests.cs','DoorAttackRules.cs','DoorApproachRules.cs')
 'AllocCutTests'    = @('AllocCutTests.cs','AllocCutRules.cs')
 'BotTests'         = @('BotTests.cs','BotScript.cs','BotRules.cs','BotRunner.cs','VirtualPlayerRules.cs','BotGridRules.cs')
}
# The output is kept next to the package (unit-tests.log) as evidence for the release.
$report = Join-Path (Split-Path $PSScriptRoot -Parent) 'unit-tests.log'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('== ' + (Get-Date -Format 's'))
try {
 foreach ($name in $suites.Keys) {
  $exe = Join-Path $work ($name + '.exe')
  $sources = $suites[$name] | ForEach-Object { Join-Path $PSScriptRoot $_ }
  $built = & $compiler /nologo "/out:$exe" @sources
  if ($LASTEXITCODE -ne 0) { $lines.Add("$name did not compile"); $built | ForEach-Object { $lines.Add($_) }; throw "$name did not compile" }
  $out = & $exe
  $out | ForEach-Object { $lines.Add("${name}: $_"); $_ }
  if ($LASTEXITCODE -ne 0) { throw "$name failed" }
 }
} finally {
 [IO.File]::WriteAllLines($report, $lines.ToArray(), (New-Object System.Text.UTF8Encoding($true)))
 Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
