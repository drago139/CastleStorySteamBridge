param([Parameter(Mandatory=$true)][string]$App,[Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d{1,4}\.\d{1,4}\.\d{1,4}$'){throw 'Invalid desktop version'}
$rsa=New-Object System.Security.Cryptography.RSACryptoServiceProvider
try {
 $rsa.FromXmlString([IO.File]::ReadAllText((Join-Path $env:USERPROFILE '.castlestory-signing\private-key.xml')))
 $appBytes=[IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $App))
 $prefix=[Text.Encoding]::UTF8.GetBytes("CastleStorySetup/$Version")
 $signedBytes=New-Object byte[] ($prefix.Length+1+$appBytes.Length)
 [Array]::Copy($prefix,0,$signedBytes,0,$prefix.Length)
 [Array]::Copy($appBytes,0,$signedBytes,$prefix.Length+1,$appBytes.Length)
 [IO.File]::WriteAllBytes((Resolve-Path -LiteralPath $App).Path+'.sig',$rsa.SignData($signedBytes,(New-Object System.Security.Cryptography.SHA256Managed)))
} finally {$rsa.Clear()}
