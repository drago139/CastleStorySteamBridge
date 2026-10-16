param([string]$GameFolder='D:\SteamLibrary\steamapps\common\Castle Story')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach($helper in 'WorkshopPublisher','WorkshopProbe'){
 $names=@(($helper+'.cs'),'WorkshopSteam.cs','GameGuard.cs','WorkshopChannel.cs','DesktopRelease.cs','UpdatePackage.cs','UpdateKey.cs')
 $sources=@($names | ForEach-Object {Join-Path $PSScriptRoot $_})
 & $compiler /nologo /target:exe /platform:x64 "/out:$root\$helper.exe" @sources
 if($LASTEXITCODE -ne 0){throw "$helper compilation failed"}
}
& $compiler /nologo /target:exe /platform:x64 "/out:$root\WorkshopAutoUpdateTests.exe" "/reference:$GameFolder\BepInEx\core\Mono.Cecil.dll" "$PSScriptRoot\WorkshopAutoUpdateTests.cs" "$PSScriptRoot\WorkshopAutoUpdate.cs" "$PSScriptRoot\UpdatePackage.cs" "$PSScriptRoot\UpdateKey.cs"
if($LASTEXITCODE -ne 0){throw 'Workshop updater tests compilation failed'}
New-Item -ItemType Directory -Path (Join-Path $root 'TestSupport') -Force | Out-Null
Copy-Item -LiteralPath "$GameFolder\BepInEx\core\Mono.Cecil.dll" -Destination (Join-Path $root 'Mono.Cecil.dll') -Force
