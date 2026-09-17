param(
    [Parameter(Mandatory = $true)]
    [string]$ApkPath,

    [Parameter(Mandatory = $true)]
    [int]$VersionCode,

    [Parameter(Mandatory = $true)]
    [string]$VersionName,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseNotes
)

$ErrorActionPreference = "Stop"

$TargetDir = "C:\Scan2EnterGateway\updates\test"
$TargetApk = Join-Path $TargetDir "Scan2Enter.apk"
$TempApk = Join-Path $TargetDir "Scan2Enter.apk.tmp"
$Manifest = Join-Path $TargetDir "manifest.json"
$TempManifest = Join-Path $TargetDir "manifest.json.tmp"

if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) {
    throw "APK non trovato: $ApkPath"
}

$sourceFile = Get-Item -LiteralPath $ApkPath
if ($sourceFile.Length -le 0) {
    throw "APK vuoto: $ApkPath"
}

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

Remove-Item -LiteralPath $TempApk -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $TempManifest -Force -ErrorAction SilentlyContinue

Write-Host "Copio APK nel canale TEST..."
Copy-Item -LiteralPath $ApkPath -Destination $TempApk -Force

$sourceHash = (Get-FileHash -LiteralPath $ApkPath -Algorithm SHA256).Hash
$tempHash = (Get-FileHash -LiteralPath $TempApk -Algorithm SHA256).Hash

if ($sourceHash -ne $tempHash) {
    Remove-Item -LiteralPath $TempApk -Force -ErrorAction SilentlyContinue
    throw "SHA256 non corrispondente dopo la copia."
}

Move-Item -LiteralPath $TempApk -Destination $TargetApk -Force

$manifestObject = [ordered]@{
    versionCode  = $VersionCode
    versionName  = $VersionName
    releaseNotes = $ReleaseNotes
}

$manifestObject |
    ConvertTo-Json |
    Set-Content -LiteralPath $TempManifest -Encoding UTF8

Move-Item -LiteralPath $TempManifest -Destination $Manifest -Force

$finalHash = (Get-FileHash -LiteralPath $TargetApk -Algorithm SHA256).Hash

Write-Host ""
Write-Host "TEST pubblicato correttamente"
Write-Host "VersionCode : $VersionCode"
Write-Host "VersionName : $VersionName"
Write-Host "SHA256      : $finalHash"
Write-Host "APK         : $TargetApk"
Write-Host "Manifest    : $Manifest"
