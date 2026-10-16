# Signs a bridge DLL for in-game updates: the signature covers "<version>\0<dll bytes>" (RSA, SHA-256).
#   Sign-Bridge.ps1 -Dll ..\Components\bridge.dll -Version 0.3.7     -> bridge.dll.sig next to the DLL
param([Parameter(Mandatory = $true)][string]$Dll, [Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d{1,4}\.\d{1,4}\.\d{1,4}(\.\d{1,4})?$') { throw "Bad version: $Version" }
$private = Join-Path $env:USERPROFILE '.castlestory-signing\private-key.xml'
if (-not (Test-Path $private)) { throw "No signing key. Run Source\New-SigningKey.ps1 first." }
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.FromXmlString([System.IO.File]::ReadAllText($private))
$dllBytes = [System.IO.File]::ReadAllBytes((Resolve-Path $Dll))
$v = [System.Text.Encoding]::UTF8.GetBytes($Version)
$data = New-Object byte[] ($v.Length + 1 + $dllBytes.Length)
[Array]::Copy($v, 0, $data, 0, $v.Length); $data[$v.Length] = 0; [Array]::Copy($dllBytes, 0, $data, $v.Length + 1, $dllBytes.Length)
$sig = $rsa.SignData($data, (New-Object System.Security.Cryptography.SHA256Managed))
$out = (Resolve-Path $Dll).Path + '.sig'
[System.IO.File]::WriteAllBytes($out, $sig)
"signed $Version -> $out ($($sig.Length) bytes)"

