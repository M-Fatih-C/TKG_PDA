[CmdletBinding()]
param(
    [string]$AppVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stagingDir = Join-Path $repoRoot 'build_staging'
$outputDir = Join-Path $repoRoot "artifacts\release\$AppVersion"
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }
$shortCommit = (& git -C $repoRoot rev-parse --short HEAD).Trim()
$fullCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

function Reset-StagingDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullStaging = [System.IO.Path]::GetFullPath($stagingDir).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullStaging, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside build staging: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

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
Reset-StagingDirectory $serverStaging
$apiProj = Join-Path $repoRoot 'src\TkgPda.Api\TkgPda.Api.csproj'
& dotnet publish $apiProj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    "-p:Version=$AppVersion" `
    "-p:InformationalVersion=$AppVersion+$fullCommit" `
    "-p:TkgBuildTimestamp=$now" `
    -o $serverStaging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Syncing updated deployment tools, db migrations, seeds, web interfaces and demo seed to staging..." -ForegroundColor Cyan
$toolsStaging = Join-Path $stagingDir 'Tools'
Reset-StagingDirectory $toolsStaging
Get-ChildItem -Path (Join-Path $repoRoot 'tools\deployment\*.ps1') | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $toolsStaging $_.Name) -Force
}

$dbMigrationsStaging = Join-Path $stagingDir 'db\migrations'
Reset-StagingDirectory $dbMigrationsStaging
Copy-Item (Join-Path $repoRoot 'db\migrations\*') $dbMigrationsStaging -Recurse -Force

$dbSeedsStaging = Join-Path $stagingDir 'db\seeds'
Reset-StagingDirectory $dbSeedsStaging
Copy-Item (Join-Path $repoRoot 'db\seeds\*') $dbSeedsStaging -Recurse -Force

$adminDist = Join-Path $repoRoot 'src\TkgPda.AdminWeb\dist'
$adminStaging = Join-Path $stagingDir 'Web\admin'
if (Test-Path $adminDist) {
    Write-Host "Syncing Admin Web ($adminDist) to staging ($adminStaging)..." -ForegroundColor Cyan
    Reset-StagingDirectory $adminStaging
    Copy-Item (Join-Path $adminDist '*') $adminStaging -Recurse -Force
}

$pdaBuild = Join-Path $repoRoot 'src\TkgPda.Pda\build\web'
$pdaStaging = Join-Path $stagingDir 'Web\pda'
if (Test-Path $pdaBuild) {
    Write-Host "Syncing Flutter PDA Web ($pdaBuild) to staging ($pdaStaging)..." -ForegroundColor Cyan
    Reset-StagingDirectory $pdaStaging
    Copy-Item (Join-Path $pdaBuild '*') $pdaStaging -Recurse -Force
    $pdaIndex = Join-Path $pdaStaging 'index.html'
    if (Test-Path $pdaIndex) {
        $c = Get-Content $pdaIndex -Raw
        if ($c -match '<base href="[^"]*">') {
            $c = $c -replace '<base href="[^"]*">', '<base href="/pda/">'
            Set-Content -Path $pdaIndex -Value $c -Encoding UTF8
        }
    }
}

# 3. Update Manifests (Full and Update)
Write-Host "Updating cryptographic manifests (full and update)..." -ForegroundColor Cyan
$fileHashes = @{}
$updateFileHashes = @{}
$files = Get-ChildItem -Path $stagingDir -File -Recurse | Where-Object { 
    $_.Name -ne 'manifest.json' -and $_.Name -ne 'update_manifest.json' 
}
foreach ($file in $files) {
    $rel = $file.FullName.Substring($stagingDir.Length).TrimStart('\', '/').Replace('\', '/')
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $fileHashes[$rel] = $hash

    # Update manifest excludes PostgreSQL runtime since update package targets existing host
    if (-not ($rel.StartsWith('PostgreSQL/', [StringComparison]::OrdinalIgnoreCase) -or 
              $rel.StartsWith('PostgreSQL\', [StringComparison]::OrdinalIgnoreCase))) {
        $updateFileHashes[$rel] = $hash
    }
}

$fullManifest = @{
    name = "TKG PDA Demo Server"
    version = $AppVersion
    buildCommit = $shortCommit
    fullCommit = $fullCommit
    releaseTimestamp = $now
    architecture = "win-x64"
    databaseMigrationLevel = "032"
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
$fullManifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8

$updateManifest = @{
    name = "TKG PDA Server Update Package"
    version = $AppVersion
    buildCommit = $shortCommit
    fullCommit = $fullCommit
    releaseTimestamp = $now
    architecture = "win-x64"
    databaseMigrationLevel = "032"
    mode = "IN_PLACE_UPDATE"
    components = @{
        server = "ASP.NET Core Kestrel (Native Windows Service, Self-Contained win-x64)"
        adminWeb = "React SPA (/admin/)"
        pdaWeb = "Flutter Web SPA (/pda/)"
    }
    files = $updateFileHashes
}
$updateManifestPath = Join-Path $stagingDir 'update_manifest.json'
$updateManifest | ConvertTo-Json -Depth 5 | Set-Content -Path $updateManifestPath -Encoding UTF8

# 4. Compile Inno Setup Installer Script
$issPath = Join-Path $PSScriptRoot 'demo_installer.iss'
Write-Host "Compiling Inno Setup Full Package ($issPath)..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$AppVersion" "/DStagingDir=$stagingDir" "/DOutputDir=$outputDir" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC compilation of demo installer failed with exit code $LASTEXITCODE"
}

# 4b. Compile Inno Setup In-Place Update Package Script
$updaterIssPath = Join-Path $PSScriptRoot 'updater.iss'
Write-Host "Compiling Inno Setup In-Place Update Package ($updaterIssPath)..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$AppVersion" "/DStagingDir=$stagingDir" "/DOutputDir=$outputDir" $updaterIssPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC compilation of update package failed with exit code $LASTEXITCODE"
}

# 5. Verify Output Artifacts
$installerExe = Join-Path $outputDir "TKG_PDA_Demo_Setup_$AppVersion.exe"
$updaterExe = Join-Path $outputDir "TKG_PDA_Update_$AppVersion.exe"

if (-not (Test-Path $installerExe)) {
    throw "Expected setup executable not found: $installerExe"
}
if (-not (Test-Path $updaterExe)) {
    throw "Expected update executable not found: $updaterExe"
}

$item = Get-Item $installerExe
$sizeMb = [Math]::Round($item.Length / 1MB, 2)
$exeHash = (Get-FileHash -Path $installerExe -Algorithm SHA256).Hash.ToLowerInvariant()

$updaterItem = Get-Item $updaterExe
$updaterSizeMb = [Math]::Round($updaterItem.Length / 1MB, 2)
$updaterHash = (Get-FileHash -Path $updaterExe -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " RELEASE PACKAGES READY!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " [1] DEMO FULL SETUP (Fresh / Isolated Install):" -ForegroundColor Cyan
Write-Host "     File:    $installerExe" -ForegroundColor Gray
Write-Host "     Size:    $sizeMb MB ($($item.Length) bytes)" -ForegroundColor Gray
Write-Host "     SHA-256: $exeHash" -ForegroundColor Gray
Write-Host ""
Write-Host " [2] IN-PLACE UPDATE PACKAGE (Serverda Kurulu Sistemi Guncelleme):" -ForegroundColor Cyan
Write-Host "     File:    $updaterExe" -ForegroundColor Gray
Write-Host "     Size:    $updaterSizeMb MB ($($updaterItem.Length) bytes)" -ForegroundColor Gray
Write-Host "     SHA-256: $updaterHash" -ForegroundColor Gray

# Copy directly to user Desktop
$desktopDir = [Environment]::GetFolderPath('Desktop')
$desktopInstaller = Join-Path $desktopDir "TKG_PDA_Demo_Setup_$AppVersion.exe"
$desktopUpdater = Join-Path $desktopDir "TKG_PDA_Update_$AppVersion.exe"

Copy-Item $installerExe $desktopInstaller -Force
Copy-Item $updaterExe $desktopUpdater -Force

Write-Host ""
Write-Host " Desktop Shortcuts Created:" -ForegroundColor Green
Write-Host "  * Full Setup:   $desktopInstaller" -ForegroundColor Yellow
Write-Host "  * Update Patch: $desktopUpdater" -ForegroundColor Yellow
Write-Host "==========================================================" -ForegroundColor Green
