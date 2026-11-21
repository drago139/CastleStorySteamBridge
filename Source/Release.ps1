# Full release pipeline: plugin -> Components\bridge.dll -> signature -> hashes -> installer -> self-test.
#   Release.ps1 [-GameFolder "D:\SteamLibrary\steamapps\common\Castle Story"]
param([string]$GameFolder = 'D:\SteamLibrary\steamapps\common\Castle Story')
$ErrorActionPreference = 'Stop'
if (Get-Process -Name 'Castle Story' -ErrorAction SilentlyContinue) { throw 'Castle Story is running.' }
# Build ID: stamped into UpdatePackage.cs on every release so two builds of one version never look alike.
$buildId = 'B' + (Get-Date -Format 'yyyyMMdd-HHmmss'); $upd = Join-Path $PSScriptRoot 'UpdatePackage.cs'; $ut = [IO.File]::ReadAllText($upd); [IO.File]::WriteAllText($upd, [regex]::Replace($ut, 'Build = "[^"]*"', 'Build = "' + $buildId + '"'), (New-Object Text.UTF8Encoding($true)))
& (Join-Path $PSScriptRoot 'RunUnitTests.ps1')
$root = Split-Path $PSScriptRoot -Parent
$version = [regex]::Match((Get-Content (Join-Path $PSScriptRoot 'UpdatePackage.cs') -Raw), 'Text = "([\d\.]+)"').Groups[1].Value
if (-not $version) { throw 'Version not found in UpdatePackage.cs' }
"== version $version"
# 0.4.23 (user: seasonal content cut): capturethepumpkin.zip / halloween2017.zip are no longer built, shipped or hashed (SeasonalMode stays only in older version folders).

& (Join-Path $PSScriptRoot 'BuildPlugin.ps1') -GameFolder $GameFolder | Out-Null
Copy-Item (Join-Path $root 'CastleStorySteamBridge-rebuilt.dll') (Join-Path $root 'Components\bridge.dll') -Force
& (Join-Path $PSScriptRoot 'Sign-Bridge.ps1') -Dll (Join-Path $root 'Components\bridge.dll') -Version $version
& (Join-Path $PSScriptRoot 'BuildWorkshopUpdater.ps1') -GameFolder $GameFolder | Out-Null
$entries = foreach ($n in 'bridge.dll', 'bridge.dll.sig', 'runtime.zip', 'uk.csv', 'uk.json') { ' { "' + $n + '", "' + (Get-FileHash (Join-Path $root "Components\$n") -Algorithm SHA256).Hash + '" },' }
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
