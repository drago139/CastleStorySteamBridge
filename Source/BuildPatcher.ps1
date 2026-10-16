param([Parameter(Mandatory=$true)][string]$GameFolder)
# Rebuilds the BepInEx preloader patcher (10-minute UNet DisconnectTimeout) for verification.
# The shipped installer keeps the known-good binary inside Components\runtime.zip.
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$managed = Join-Path $GameFolder 'Castle Story_Data\Managed'
$output = Join-Path (Split-Path $PSScriptRoot -Parent) 'CastleStoryNetworkTimeout-rebuilt.dll'
& $compiler /nologo /noconfig /target:library /nostdlib+ "/out:$output" "/reference:$managed\mscorlib.dll" "/reference:$managed\System.dll" "/reference:$GameFolder\BepInEx\core\Mono.Cecil.dll" "$PSScriptRoot\CastleStoryNetworkTimeout.cs"
if ($LASTEXITCODE -ne 0) { throw 'Patcher compilation failed' }
Write-Output $output
