[CmdletBinding()]
param(
    [string]$AppVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stagingDir = Join-Path $repoRoot 'build_staging'
$outputDir = Join-Path $repoRoot "artifacts\release\$AppVersion"
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }

Write-Host "=== Building TKG PDA Demo Installer (Version $AppVersion) ===" -ForegroundColor Cyan

# 1. Locate ISCC.exe
$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $null
foreach ($path in $isccCandidates) {
    if (Test-Path $path) { $iscc = $path; break }
}
if (-not $iscc) {
    $found = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($found) { $iscc = $found.Source }
}
if (-not $iscc) {
    throw "Inno Setup Compiler (ISCC.exe) not found."
}
Write-Host "Found Inno Setup Compiler at: $iscc" -ForegroundColor Green

# 2. Publish updated server and sync tools to staging
Write-Host "Publishing self-contained .NET server to staging..." -ForegroundColor Cyan
$serverStaging = Join-Path $stagingDir 'Server'
$apiProj = Join-Path $repoRoot 'src\TkgPda.Api\TkgPda.Api.csproj'
& dotnet publish $apiProj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -o $serverStaging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Syncing updated deployment tools, web interfaces and demo seed to staging..." -ForegroundColor Cyan
$toolsStaging = Join-Path $stagingDir 'Tools'
if (-not (Test-Path $toolsStaging)) { New-Item -ItemType Directory -Path $toolsStaging -Force | Out-Null }
Get-ChildItem -Path (Join-Path $repoRoot 'tools\deployment\*.ps1') | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $toolsStaging $_.Name) -Force
}
$destDb = Join-Path $stagingDir 'db\seeds\demo\demo_seed.sql'
$destDbDir = [System.IO.Path]::GetDirectoryName($destDb)
if (-not (Test-Path $destDbDir)) { New-Item -ItemType Directory -Path $destDbDir -Force | Out-Null }
Copy-Item (Join-Path $repoRoot 'db\seeds\demo\demo_seed.sql') $destDb -Force

$adminDist = Join-Path $repoRoot 'src\TkgPda.AdminWeb\dist'
$adminStaging = Join-Path $stagingDir 'Web\admin'
if (Test-Path $adminDist) {
    Write-Host "Syncing Admin Web ($adminDist) to staging ($adminStaging)..." -ForegroundColor Cyan
    if (-not (Test-Path $adminStaging)) { New-Item -ItemType Directory -Path $adminStaging -Force | Out-Null }
    Copy-Item (Join-Path $adminDist '*') $adminStaging -Recurse -Force
}

$pdaBuild = Join-Path $repoRoot 'src\TkgPda.Pda\build\web'
$pdaStaging = Join-Path $stagingDir 'Web\pda'
if (Test-Path $pdaBuild) {
    Write-Host "Syncing Flutter PDA Web ($pdaBuild) to staging ($pdaStaging)..." -ForegroundColor Cyan
    if (-not (Test-Path $pdaStaging)) { New-Item -ItemType Directory -Path $pdaStaging -Force | Out-Null }
    Copy-Item (Join-Path $pdaBuild '*') $pdaStaging -Recurse -Force
}

# 3. Update Manifest
Write-Host "Updating manifest.json with cryptographic signatures..." -ForegroundColor Cyan
$fileHashes = @{}
$files = Get-ChildItem -Path $stagingDir -File -Recurse | Where-Object { $_.Name -ne 'manifest.json' }
foreach ($file in $files) {
    $rel = $file.FullName.Substring($stagingDir.Length).TrimStart('\', '/').Replace('\', '/')
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $fileHashes[$rel] = $hash
}

$shortCommit = (& git -C $repoRoot rev-parse --short HEAD).Trim()
$fullCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

$manifest = @{
    name = "TKG PDA Demo Server"
    version = $AppVersion
    buildCommit = $shortCommit
    fullCommit = $fullCommit
    releaseTimestamp = $now
    architecture = "win-x64"
    mode = "DEMO_SIMULATION"
    components = @{
        server = "ASP.NET Core Kestrel (Native Windows Service, Self-Contained win-x64)"
        adminWeb = "React SPA (/admin/)"
        pdaWeb = "Flutter Web SPA (/pda/)"
        database = "Bundled PostgreSQL 18"
    }
    files = $fileHashes
}
$manifestPath = Join-Path $stagingDir 'manifest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8

# 4. Compile Inno Setup Script
$issPath = Join-Path $PSScriptRoot 'demo_installer.iss'
Write-Host "Compiling Inno Setup package ($issPath)..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$AppVersion" "/DStagingDir=$stagingDir" "/DOutputDir=$outputDir" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC compilation failed with exit code $LASTEXITCODE"
}

# 5. Verify Output Artifact
$installerExe = Join-Path $outputDir "TKG_PDA_Demo_Setup_$AppVersion.exe"
if (-not (Test-Path $installerExe)) {
    throw "Expected setup executable not found: $installerExe"
}

$item = Get-Item $installerExe
$sizeMb = [Math]::Round($item.Length / 1MB, 2)
$exeHash = (Get-FileHash -Path $installerExe -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " DEMO INSTALLER PACKAGE READY!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " File:    $installerExe" -ForegroundColor Cyan
Write-Host " Size:    $sizeMb MB ($($item.Length) bytes)" -ForegroundColor Cyan
Write-Host " SHA-256: $exeHash" -ForegroundColor Gray

# Copy directly to user Desktop
$desktopExe = Join-Path ([Environment]::GetFolderPath('Desktop')) "TKG_PDA_Demo_Setup_$AppVersion.exe"
Copy-Item $installerExe $desktopExe -Force
Write-Host " Desktop: $desktopExe" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
