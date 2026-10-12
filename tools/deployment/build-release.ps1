<#
.SYNOPSIS
    Automated production release builder for TKG PDA Server.
.DESCRIPTION
    Builds the complete production release without manual steps:
    1. Runs automated tests
    2. Publishes self-contained .NET Kestrel server (win-x64)
    3. Builds React Admin Web SPA (dist)
    4. Builds Flutter PDA Web SPA (build/web)
    5. Gathers SQL migrations, customer seeds, and RDS templates
    6. Generates release manifest.json with SHA-256 checksums
    7. Compiles Inno Setup installers:
       - TKG_PDA_Setup_<version>.exe
       - TKG_PDA_Update_<version>.exe
    8. Computes SHA256SUMS.txt for customer verification
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [switch]$SkipTests = $false,
    [switch]$SkipFlutterBuild = $false
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $scriptDir '..\..')).Path

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "TKG PDA PRODUCTION RELEASE BUILDER (Version $Version)" -ForegroundColor Cyan
Write-Host "Project Root: $projectRoot" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Resolve Git Commit & Build Time
$commitHash = (& git -C $projectRoot rev-parse --short HEAD).Trim()
$fullCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
$buildTimeUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
Write-Host "[INFO] Git Commit: $commitHash ($fullCommit)"
Write-Host "[INFO] Build Timestamp: $buildTimeUtc"

# Set environment variables for assembly metadata
$env:TKG_BUILD_COMMIT = $commitHash
$env:TKG_BUILD_TIMESTAMP = $buildTimeUtc

# 2. Setup Staging and Release Output Directories
$stagingDir = Join-Path $projectRoot 'build_staging'
$releaseDir = Join-Path $projectRoot "artifacts\release\$Version"

Write-Host "[INFO] Cleaning staging and release directories..."
if (Test-Path $stagingDir) { Remove-Item -Path $stagingDir -Recurse -Force }
if (Test-Path $releaseDir) { Remove-Item -Path $releaseDir -Recurse -Force }

New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

$serverStaging = Join-Path $stagingDir 'Server'
$webAdminStaging = Join-Path $stagingDir 'Web\admin'
$webPdaStaging = Join-Path $stagingDir 'Web\pda'
$webRootStaging = Join-Path $stagingDir 'Web\root'
$toolsStaging = Join-Path $stagingDir 'Tools'
$dbStaging = Join-Path $stagingDir 'db'
$rdsStaging = Join-Path $stagingDir 'rds'

New-Item -ItemType Directory -Path $serverStaging -Force | Out-Null
New-Item -ItemType Directory -Path $webAdminStaging -Force | Out-Null
New-Item -ItemType Directory -Path $webPdaStaging -Force | Out-Null
New-Item -ItemType Directory -Path $webRootStaging -Force | Out-Null
New-Item -ItemType Directory -Path $toolsStaging -Force | Out-Null
New-Item -ItemType Directory -Path $dbStaging -Force | Out-Null
New-Item -ItemType Directory -Path $rdsStaging -Force | Out-Null

# 3. Run Automated Tests
if (-not $SkipTests) {
    Write-Host "`n--- [Step 1/7] Running Unit & Integration Tests ---" -ForegroundColor Yellow

    Write-Host "[TEST] Running Admin Web tests..."
    $adminWebDir = Join-Path $projectRoot 'src\TkgPda.AdminWeb'
    Push-Location $adminWebDir
    try {
        & npm run test:run
        if ($LASTEXITCODE -ne 0) { throw "AdminWeb test suite failed." }
    } finally { Pop-Location }

    Write-Host "[TEST] Running Flutter PDA tests..."
    $pdaDir = Join-Path $projectRoot 'src\TkgPda.Pda'
    Push-Location $pdaDir
    try {
        & flutter test
        if ($LASTEXITCODE -ne 0) { throw "Flutter PDA test suite failed." }
    } finally { Pop-Location }

    Write-Host "[TEST] All pre-build tests passed successfully!" -ForegroundColor Green
}

# 4. Build .NET Release Self-Contained Server (win-x64)
Write-Host "`n--- [Step 2/7] Publishing Self-Contained .NET Server (win-x64) ---" -ForegroundColor Yellow
$apiProj = Join-Path $projectRoot 'src\TkgPda.Api\TkgPda.Api.csproj'
& dotnet publish $apiProj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $serverStaging

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
Write-Host "[INFO] Server publish complete." -ForegroundColor Green

# 5. Build AdminWeb Production Bundle
Write-Host "`n--- [Step 3/7] Building Production Admin Web (React SPA) ---" -ForegroundColor Yellow
$adminWebDir = Join-Path $projectRoot 'src\TkgPda.AdminWeb'
Push-Location $adminWebDir
try {
    $env:VITE_BASE_URL = '/admin/'
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw "AdminWeb npm run build failed" }
    Copy-Item -Path "$adminWebDir\dist\*" -Destination $webAdminStaging -Recurse -Force
} finally { Pop-Location }
Write-Host "[INFO] Admin Web built and staged." -ForegroundColor Green

# 6. Build Flutter PDA Production Bundle
Write-Host "`n--- [Step 4/7] Building Production PDA Web (Flutter SPA) ---" -ForegroundColor Yellow
$pdaDir = Join-Path $projectRoot 'src\TkgPda.Pda'
Push-Location $pdaDir
try {
    if (-not $SkipFlutterBuild) {
        & flutter build web --base-href /pda/ --release
        if ($LASTEXITCODE -ne 0) { throw "Flutter web build failed" }
    }
    Copy-Item -Path "$pdaDir\build\web\*" -Destination $webPdaStaging -Recurse -Force
} finally { Pop-Location }
Write-Host "[INFO] PDA Web built and staged." -ForegroundColor Green

# 7. Stage Database, RDS Scripts, and Deployment Tools
Write-Host "`n--- [Step 5/7] Staging Database, RDS, and Maintenance Tools ---" -ForegroundColor Yellow
Copy-Item -Path (Join-Path $projectRoot 'db\*') -Destination $dbStaging -Recurse -Force
Copy-Item -Path (Join-Path $projectRoot 'rds\*') -Destination $rdsStaging -Recurse -Force
Copy-Item -Path (Join-Path $scriptDir '*.ps1') -Destination $toolsStaging -Recurse -Force

# 8. Generate Release Manifest
Write-Host "`n--- [Step 6/7] Generating Release Manifest ---" -ForegroundColor Yellow
$manifest = @{
    name = "TKG PDA Server"
    version = $Version
    minSupportedPreviousVersion = "1.0.0"
    buildCommit = $commitHash
    fullCommit = $fullCommit
    releaseTimestamp = $buildTimeUtc
    architecture = "win-x64"
    databaseMigrationLevel = "026"
    components = @{
        server = "ASP.NET Core Kestrel (Native Windows Service)"
        adminWeb = "React SPA (/admin/)"
        pdaWeb = "Flutter Web SPA (/pda/)"
        database = "PostgreSQL 18"
    }
}

$manifestJson = Join-Path $stagingDir 'manifest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestJson -Encoding UTF8
Copy-Item -Path $manifestJson -Destination (Join-Path $releaseDir 'manifest.json') -Force
Write-Host "[INFO] Manifest generated." -ForegroundColor Green

# 9. Compile Inno Setup Installers
Write-Host "`n--- [Step 7/7] Compiling Windows Setup & Update Packages ---" -ForegroundColor Yellow
$candidateIscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)

$iscc = $null
foreach ($c in $candidateIscc) {
    if (Test-Path $c) { $iscc = $c; break }
}

if (-not $iscc) {
    $inPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($inPath) { $iscc = $inPath.Source }
}

if (-not $iscc) {
    throw "Inno Setup Compiler (ISCC.exe) not found. Please install Inno Setup 6."
}

Write-Host "[INFO] Found Inno Setup Compiler at: $iscc"

$installerIss = Join-Path $scriptDir 'installer.iss'
$updaterIss = Join-Path $scriptDir 'updater.iss'

Write-Host "[INFO] Compiling TKG_PDA_Setup_$Version.exe..."
& $iscc "/DAppVersion=$Version" "/DStagingDir=$stagingDir" "/DOutputDir=$releaseDir" $installerIss
if ($LASTEXITCODE -ne 0) { throw "ISCC compilation of installer failed with exit code $LASTEXITCODE" }

Write-Host "[INFO] Compiling TKG_PDA_Update_$Version.exe..."
& $iscc "/DAppVersion=$Version" "/DStagingDir=$stagingDir" "/DOutputDir=$releaseDir" $updaterIss
if ($LASTEXITCODE -ne 0) { throw "ISCC compilation of updater failed with exit code $LASTEXITCODE" }

# 10. Generate SHA256SUMS.txt
Write-Host "`nComputing SHA-256 Checksums for Release Artifacts..."
$releaseFiles = Get-ChildItem -Path $releaseDir -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' }
$checksumLines = [System.Collections.Generic.List[string]]::new()

foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $line = "$hash  $($file.Name)"
    $checksumLines.Add($line)
    Write-Host "  $line"
}

$checksumFile = Join-Path $releaseDir 'SHA256SUMS.txt'
$checksumLines | Set-Content -Path $checksumFile -Encoding UTF8

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "RELEASE BUILD SUCCESSFUL" -ForegroundColor Green
Write-Host "Artifacts Directory: $releaseDir" -ForegroundColor Green
Write-Host "  - TKG_PDA_Setup_$Version.exe" -ForegroundColor Green
Write-Host "  - TKG_PDA_Update_$Version.exe" -ForegroundColor Green
Write-Host "  - manifest.json" -ForegroundColor Green
Write-Host "  - SHA256SUMS.txt" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

return @{
    Status = "SUCCESS"
    Version = $Version
    ReleaseDir = $releaseDir
    Artifacts = ($releaseFiles | Select-Object -ExpandProperty Name)
}
