param([Parameter(Mandatory=$true)][string]$GameFolder)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$managed = Join-Path $GameFolder 'Castle Story_Data\Managed'
$output = Join-Path (Split-Path $PSScriptRoot -Parent) 'CastleStorySteamBridge-rebuilt.dll'
& $compiler /nologo /noconfig /target:library /nostdlib+ "/out:$output" "/reference:$managed\mscorlib.dll" "/reference:$managed\System.dll" "/reference:$managed\System.Core.dll" "/reference:$managed\UnityEngine.dll" "/reference:$managed\UnityEngine.Networking.dll" "/reference:$managed\UnityEngine.UI.dll" "/reference:$managed\Assembly-CSharp-firstpass.dll" "/reference:$managed\Brix.Core.dll" "/reference:$managed\MoonSharp.Interpreter.dll" "/reference:$GameFolder\BepInEx\core\BepInEx.dll" "/reference:$GameFolder\BepInEx\core\0Harmony.dll" "$PSScriptRoot\ReliabilityCore.cs" "$PSScriptRoot\BridgeCore.cs" "$PSScriptRoot\SteamBridgePlugin.cs" "$PSScriptRoot\ServerDiscovery.cs" "$PSScriptRoot\LoadGuard.cs" "$PSScriptRoot\CrateGuard.cs" "$PSScriptRoot\Fixes.cs" "$PSScriptRoot\PingMeter.cs" "$PSScriptRoot\LobbySocial.cs" "$PSScriptRoot\ChatMapControls.cs" "$PSScriptRoot\InGameChatSize.cs" "$PSScriptRoot\MapChatCards.cs" "$PSScriptRoot\WorkshopMaps.cs" "$PSScriptRoot\WorkshopMapImport.cs" "$PSScriptRoot\ProposalProtocol.cs" "$PSScriptRoot\Health.cs" "$PSScriptRoot\StallBox.cs" "$PSScriptRoot\Diagnostics.cs" "$PSScriptRoot\Updater.cs" "$PSScriptRoot\UpdateFileStore.cs" "$PSScriptRoot\PreviousLogs.cs" "$PSScriptRoot\LogSummary.cs" "$PSScriptRoot\UpdatePackage.cs" "$PSScriptRoot\UpdateKey.cs" "$PSScriptRoot\PluginAssemblyInfo.cs" "/reference:$managed\brix-common.dll" "$PSScriptRoot\GameplayIntegration.cs" "$PSScriptRoot\SavePreviewFix.cs" "$PSScriptRoot\SavePreviewRules.cs" "$PSScriptRoot\LoggerAssemblyFix.cs" "$PSScriptRoot\LegacyTrialCleanup.cs" "$PSScriptRoot\CastleStoryGameplayFixes.cs" "$PSScriptRoot\GameplayFixRules.cs" "$PSScriptRoot\DoorDemolitionFix.cs" "$PSScriptRoot\DoorApproachRules.cs" "$PSScriptRoot\DoorWorkReservations.cs" "$PSScriptRoot\SeasonalCalendar.cs" "$PSScriptRoot\SeasonalEvents.cs" "$PSScriptRoot\WinterEvents.cs" "$PSScriptRoot\WinterWeather.cs" "$PSScriptRoot\NetworkCompat.cs" "$PSScriptRoot\HostModeration.cs" "$PSScriptRoot\TestHooks.cs" "$PSScriptRoot\TimeoutPolicy.cs" "$PSScriptRoot\Extrapolation.cs" "$PSScriptRoot\PositionLink.cs" "$PSScriptRoot\PositionDiag.cs" "$PSScriptRoot\HitchProfiler.cs" "$PSScriptRoot\VisualOptions.cs" "$PSScriptRoot\ItemFilter.cs"
if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed' }
Write-Output $output






