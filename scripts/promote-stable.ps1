$ErrorActionPreference = "Stop"

$BaseDir = "C:\Scan2EnterGateway\updates"
$TestDir = Join-Path $BaseDir "test"
$StableDir = Join-Path $BaseDir "stable"

$TestApk = Join-Path $TestDir "Scan2Enter.apk"
$TestManifest = Join-Path $TestDir "manifest.json"

$StableApk = Join-Path $StableDir "Scan2Enter.apk"
$StableManifest = Join-Path $StableDir "manifest.json"

$TempApk = Join-Path $StableDir "Scan2Enter.apk.tmp"
$TempManifest = Join-Path $StableDir "manifest.json.tmp"

if (-not (Test-Path -LiteralPath $TestApk -PathType Leaf)) {
    throw "APK TEST non trovato: $TestApk"
}

if (-not (Test-Path -LiteralPath $TestManifest -PathType Leaf)) {
    throw "Manifest TEST non trovato: $TestManifest"
}

$manifest = Get-Content -LiteralPath $TestManifest -Raw | ConvertFrom-Json

if (-not $manifest.versionCode -or -not $manifest.versionName) {
    throw "Manifest TEST non valido."
}

$testHash = (Get-FileHash -LiteralPath $TestApk -Algorithm SHA256).Hash

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$BackupDir = Join-Path $BaseDir "backup-stable-$timestamp"
New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null

if (Test-Path -LiteralPath $StableApk) {
    Copy-Item -LiteralPath $StableApk -Destination (Join-Path $BackupDir "Scan2Enter.apk") -Force
}

if (Test-Path -LiteralPath $StableManifest) {
    Copy-Item -LiteralPath $StableManifest -Destination (Join-Path $BackupDir "manifest.json") -Force
}

Remove-Item -LiteralPath $TempApk -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $TempManifest -Force -ErrorAction SilentlyContinue

Write-Host "Promuovo TEST -> STABLE..."

Copy-Item -LiteralPath $TestApk -Destination $TempApk -Force

$tempHash = (Get-FileHash -LiteralPath $TempApk -Algorithm SHA256).Hash
if ($tempHash -ne $testHash) {
    Remove-Item -LiteralPath $TempApk -Force -ErrorAction SilentlyContinue
    throw "SHA256 non corrispondente durante la promozione."
}

Copy-Item -LiteralPath $TestManifest -Destination $TempManifest -Force

Move-Item -LiteralPath $TempApk -Destination $StableApk -Force
Move-Item -LiteralPath $TempManifest -Destination $StableManifest -Force

$stableHash = (Get-FileHash -LiteralPath $StableApk -Algorithm SHA256).Hash

if ($stableHash -ne $testHash) {
    throw "ERRORE: hash STABLE diverso da TEST dopo la promozione."
}

Write-Host ""
Write-Host "Promozione TEST -> STABLE completata"
Write-Host "VersionCode : $($manifest.versionCode)"
Write-Host "VersionName : $($manifest.versionName)"
Write-Host "SHA256 TEST : $testHash"
Write-Host "SHA256 STABLE: $stableHash"
Write-Host "Backup      : $BackupDir"
