$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'CastleStory-Steam-Setup-0.3.53-SingleFile.exe'
$resources = @('bridge.dll','bridge.dll.sig','runtime.zip','capturethepumpkin.zip','halloween2017.zip','uk.csv','uk.json') | ForEach-Object { '/resource:' + (Join-Path $root ('Components\' + $_)) + ',' + $_ }
$appFolder=Join-Path $root 'DesktopApp'
New-Item -ItemType Directory -Path $appFolder -Force | Out-Null
$app=Join-Path $appFolder 'CastleStory-Steam-App.exe'
$version=[regex]::Match((Get-Content "$PSScriptRoot\UpdatePackage.cs" -Raw),'Text = "([\d\.]+)"').Groups[1].Value
& $compiler /nologo /target:winexe /platform:x64 "/out:$app" "/win32manifest:$PSScriptRoot\Installer.manifest" /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll @resources "$PSScriptRoot\SteamSetup.cs" "$PSScriptRoot\InstallerLanguages.cs" "$PSScriptRoot\LanguageOptions.cs" "$PSScriptRoot\LanguageCatalog.cs" "/resource:$PSScriptRoot\ui-packs.json,ui-packs.json" "/resource:$PSScriptRoot\ui-localization.tsv,ui-localization.tsv" "$PSScriptRoot\LegacyTrialCleanup.cs" "$PSScriptRoot\ComponentHashes.cs" "$PSScriptRoot\InstallerAssemblyInfo.cs" "$PSScriptRoot\BridgeCore.cs" "$PSScriptRoot\NetworkSelfTest.cs" "$PSScriptRoot\UpdatePackage.cs" "$PSScriptRoot\UpdateKey.cs" "$PSScriptRoot\UpdateSelfTest.cs" "$PSScriptRoot\LogSummary.cs" "$PSScriptRoot\WorkshopSteam.cs" "$PSScriptRoot\WorkshopUpdates.cs" "$PSScriptRoot\AutoModUpdate.cs" "$PSScriptRoot\GameGuard.cs" "$PSScriptRoot\WorkshopChannel.cs" "$PSScriptRoot\DesktopRelease.cs" "$PSScriptRoot\DesktopUpdateSelfTest.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
& "$PSScriptRoot\Sign-Desktop.ps1" -App $app -Version $version
& $compiler /nologo /target:winexe /platform:x64 "/out:$output" "/win32manifest:$PSScriptRoot\Installer.manifest" /reference:System.Windows.Forms.dll "/resource:$app,desktop.app" "/resource:$app.sig,desktop.sig" "$PSScriptRoot\BootstrapLauncher.cs" "$PSScriptRoot\DesktopRelease.cs" "$PSScriptRoot\UpdatePackage.cs" "$PSScriptRoot\UpdateKey.cs" "$PSScriptRoot\InstallerAssemblyInfo.cs"
if ($LASTEXITCODE -ne 0) { throw 'Bootstrapper compilation failed' }
Write-Output $output







