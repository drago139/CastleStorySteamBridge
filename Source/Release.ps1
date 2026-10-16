# Full release pipeline: plugin -> Components\bridge.dll -> signature -> hashes -> installer -> self-test.
#   Release.ps1 [-GameFolder "D:\SteamLibrary\steamapps\common\Castle Story"]
param([string]$GameFolder = 'D:\SteamLibrary\steamapps\common\Castle Story')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = [regex]::Match((Get-Content (Join-Path $PSScriptRoot 'UpdatePackage.cs') -Raw), 'Text = "([\d\.]+)"').Groups[1].Value
if (-not $version) { throw 'Version not found in UpdatePackage.cs' }
"== version $version"
# Always package the current mode source, never a stale component copied from a prior release.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageRoot = Join-Path $root 'SeasonalMode\PackageRoot'
foreach ($component in 'capturethepumpkin','halloween2017') {
 $staging = Join-Path ([IO.Path]::GetTempPath()) ('CastleStoryPackage_' + [Guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Path $staging | Out-Null
 if ($component -eq 'capturethepumpkin') {
  $relative = 'Info\Lua\Gamemodes\capturethepumpkin'
 } else { $relative = 'Info\Lua\Mods\halloween2017' }
 $target = Join-Path $staging $relative
 New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
 Copy-Item -LiteralPath (Join-Path $packageRoot $relative) -Destination $target -Recurse
 if ($component -eq 'capturethepumpkin') {
  $languageTarget = Join-Path $staging 'Info\Languages'
  Copy-Item -LiteralPath (Join-Path $packageRoot 'Info\Languages') -Destination $languageTarget -Recurse
 }
 $archive = Join-Path $root ('Components\' + $component + '.zip')
 if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
 [IO.Compression.ZipFile]::CreateFromDirectory($staging,$archive)
}

& (Join-Path $PSScriptRoot 'BuildPlugin.ps1') -GameFolder $GameFolder | Out-Null
Copy-Item (Join-Path $root 'CastleStorySteamBridge-rebuilt.dll') (Join-Path $root 'Components\bridge.dll') -Force
& (Join-Path $PSScriptRoot 'Sign-Bridge.ps1') -Dll (Join-Path $root 'Components\bridge.dll') -Version $version
& (Join-Path $PSScriptRoot 'BuildWorkshopUpdater.ps1') -GameFolder $GameFolder | Out-Null
$entries = foreach ($n in 'bridge.dll', 'bridge.dll.sig', 'runtime.zip', 'capturethepumpkin.zip', 'halloween2017.zip', 'uk.csv', 'uk.json') { ' { "' + $n + '", "' + (Get-FileHash (Join-Path $root "Components\$n") -Algorithm SHA256).Hash + '" },' }
$code = "using System.Collections.Generic;`r`nstatic class ComponentHashes { internal static readonly Dictionary<string,string> Values = new Dictionary<string,string> {`r`n" + ($entries -join "`r`n") + "`r`n}; }`r`n"
[System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'ComponentHashes.cs'), $code, (New-Object System.Text.UTF8Encoding($true)))
$installer = & (Join-Path $PSScriptRoot 'BuildInstaller.ps1') | Select-Object -Last 1
$testDir = Join-Path $root 'SelfTest'
New-Item -ItemType Directory -Force $testDir | Out-Null
Copy-Item $installer $testDir -Force
$exe = Join-Path $testDir (Split-Path $installer -Leaf)
$p = Start-Process -FilePath $exe -ArgumentList '--self-test' -Wait -PassThru
$result = Join-Path $testDir 'SelfTest-result.txt'
if ($p.ExitCode -ne 0 -or -not (Test-Path $result)) { throw "Self-test failed (exit code $($p.ExitCode))" }
Get-Content $result
"== installer: $installer"
"== sha256: " + (Get-FileHash $installer -Algorithm SHA256).Hash
