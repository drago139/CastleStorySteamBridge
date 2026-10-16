param([string]$GameFolder='D:\SteamLibrary\steamapps\common\Castle Story')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$managed=Join-Path $GameFolder 'Castle Story_Data\Managed'
$patcher=Join-Path $root 'Components\CastleStoryWorkshopUpdate.dll'
& $compiler /nologo /noconfig /target:library /nostdlib+ "/out:$patcher" "/reference:$managed\mscorlib.dll" "/reference:$managed\System.dll" "/reference:$managed\System.Core.dll" "/reference:$GameFolder\BepInEx\core\BepInEx.dll" "/reference:$GameFolder\BepInEx\core\Mono.Cecil.dll" "$PSScriptRoot\CastleStoryWorkshopUpdate.cs" "$PSScriptRoot\LegacyTrialCleanup.cs" "$PSScriptRoot\WorkshopAutoUpdate.cs" "$PSScriptRoot\UpdatePackage.cs" "$PSScriptRoot\UpdateKey.cs"
if($LASTEXITCODE -ne 0){throw 'Workshop updater compilation failed'}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$runtime=Join-Path $root 'Components\runtime.zip'
$zip=[IO.Compression.ZipFile]::Open($runtime,[IO.Compression.ZipArchiveMode]::Update)
try{
 $old=$zip.GetEntry('BepInEx/patchers/CastleStoryWorkshopUpdate.dll');if($old){$old.Delete()}
 [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$patcher,'BepInEx/patchers/CastleStoryWorkshopUpdate.dll',[IO.Compression.CompressionLevel]::Optimal) | Out-Null
}finally{$zip.Dispose()}
Write-Output $patcher
