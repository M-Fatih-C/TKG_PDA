<#
.SYNOPSIS
    Master canonical acceptance runner for TKG PDA local development stack.
.DESCRIPTION
    Validates the local TKG PDA environment against the persistent local development stack.
    Executes acceptance phases in deterministic order, preserving evidence for:
    - Git baseline and branch state
    - Stack preflight and metric invariants
    - Runtime slot state persistence across restart
    - 9-machine production start lifecycle
    - Mamul Kasa business action state matrix
    - Repeated ALT demand dependency chains
    - Real Production End planning and atomic rollback
    - Real Operator Web Browser E2E automation
    - WorkSite static plans and live mirror audit
    
    Returns 0 on PASS (LOCAL E2E PASS — FIELD COMMISSIONING INPUTS PENDING).
    Returns non-zero on software acceptance failure.
.EXAMPLE
    pwsh -File tools/dev/acceptance/run_local_acceptance.ps1
.EXAMPLE
    pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase MamulKasa
.EXAMPLE
    pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -SkipBrowser
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Git', 'Preflight', 'Persistence', 'NineMachine', 'MamulKasa', 'Alt', 'ProductionEnd', 'Browser', 'WorkSites')]
    [string]$Phase = 'All',
    [switch]$SkipBrowser,
    [switch]$SkipRestart,
    [string]$ApiBaseUrl = 'http://127.0.0.1:5000',
    [string]$OperatorWebUrl = 'http://127.0.0.1:7357',
    [string]$HostName = '127.0.0.1',
    [int]$PgPort = 55450,
    [string]$Database = 'tkg_pda_dev',
    [string]$User = 'postgres'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$scriptDir = $PSScriptRoot

$results = [ordered]@{}
$startTime = [System.Diagnostics.Stopwatch]::StartNew()

function Log-Header {
    param([string]$Title)
    Write-Host "`n==================================================" -ForegroundColor Cyan
    Write-Host $Title -ForegroundColor Cyan
    Write-Host "==================================================" -ForegroundColor Cyan
}

function Test-TcpPort {
    param([int]$Port)
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    return ($null -ne $conn)
}

# --- Service & Tooling Verification ---
Log-Header "TKG PDA ACCEPTANCE RUNNER — PRE-CHECK"

$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path -LiteralPath $psql)) {
    $psql = (Get-Command psql.exe -ErrorAction SilentlyContinue)?.Source
}
if (-not $psql) {
    throw "PostgreSQL client (psql.exe) is required to run acceptance suites."
}

# Check service availability
$pgListening = Test-TcpPort -Port $PgPort
$apiListening = Test-TcpPort -Port 5000
$webListening = Test-TcpPort -Port 7357

Write-Host ("Services: PostgreSQL ({0}) = {1} | API (5000) = {2} | Operator Web (7357) = {3}" -f `
    $PgPort, $(if ($pgListening) {'UP'} else {'DOWN'}), `
    $(if ($apiListening) {'UP'} else {'DOWN'}), `
    $(if ($webListening) {'UP'} else {'DOWN'}))

if (-not $pgListening -or -not $apiListening -or (-not $webListening -and -not $SkipBrowser)) {
    Write-Host "`nRequired development stack is not running. Starting local stack..." -ForegroundColor Yellow
    pwsh -File (Join-Path $projectRoot 'tools\dev\start_local_stack.ps1') -Password 'DevPass123!'
    Start-Sleep -Seconds 3
}

# ==============================================================================
# PHASE 0: Git Baseline Check
# ==============================================================================
if ($Phase -in @('All', 'Git')) {
    Log-Header "PHASE: GIT BASELINE"
    $branch = (git branch --show-current).Trim()
    $head = (git rev-parse HEAD).Trim()
    $statusOut = (git status --porcelain).Trim()
    $isClean = [string]::IsNullOrWhiteSpace($statusOut)
    $baselineSha = '3389ecd2244be86f677a3fcaf84868696f1503b3'

    Write-Host "Current Branch: $branch"
    Write-Host "HEAD SHA:       $head"
    Write-Host "Working Tree:   $(if ($isClean) { 'Clean' } else { 'Modified/Untracked changes present' })"

    git merge-base --is-ancestor $baselineSha HEAD 2>$null
    $hasBaseline = ($LASTEXITCODE -eq 0) -or ($head -eq $baselineSha)

    if ($hasBaseline) {
        Write-Host "-> PASS: HEAD contains accepted baseline $baselineSha" -ForegroundColor Green
        $results['Git baseline'] = 'PASS'
    } else {
        Write-Host "-> FAIL: HEAD does not contain required baseline $baselineSha" -ForegroundColor Red
        $results['Git baseline'] = 'FAIL'
        exit 1
    }
}

# ==============================================================================
# PHASE 1: Stack Preflight & Metric Baseline
# ==============================================================================
if ($Phase -in @('All', 'Preflight')) {
    Log-Header "PHASE: STACK PREFLIGHT & METRIC INVARIANTS"
    $preflightSql = @"
SELECT
    (SELECT count(*) FROM master.machines WHERE is_active)::text
    || '|' || (SELECT count(*) FROM ops.machine_runtime_state rs JOIN master.machines m ON m.id = rs.machine_id WHERE m.is_active AND rs.active_variant_id IS NOT NULL AND rs.active_layout_id IS NOT NULL)::text
    || '|' || (SELECT count(*) FROM master.navigation_profiles WHERE config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1')::text
    || '|' || (SELECT count(DISTINCT v.id) FROM master.variants v JOIN master.variant_navigation_profile_assignments a ON a.variant_id = v.id JOIN master.navigation_profiles n ON n.id = a.navigation_profile_id WHERE n.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1' AND n.config_json ->> 'layoutBindingState' = 'SOURCE_BACKED')::text
    || '|' || (SELECT count(*) FROM master.task_templates)::text
    || '|' || (SELECT count(*) FROM ops.store_locations WHERE code LIKE 'FG-%')::text
    || '|' || (SELECT count(*) FROM integration.rds_worksite_plans)::text
    || '|' || (SELECT count(*) FROM integration.rds_worksites)::text;
"@
    $pfRaw = (& $psql -h $HostName -p $PgPort -U $User -d $Database -t -A -c $preflightSql).Trim().Split('|')
    $actMach = [int]$pfRaw[0]
    $cfgMach = [int]$pfRaw[1]
    $regProf = [int]$pfRaw[2]
    $srcVar  = [int]$pfRaw[3]
    $taskTpl = [int]$pfRaw[4]
    $fgLocs  = [int]$pfRaw[5]
    $plans   = [int]$pfRaw[6]
    $liveWs  = [int]$pfRaw[7]

    Write-Host ("  Active Machines:          {0,4} (Expected:  9)" -f $actMach)
    Write-Host ("  Configured Machine Rows:  {0,4} (Expected:  9)" -f $cfgMach)
    Write-Host ("  Registry NAV Profiles:    {0,4} (Expected: 25)" -f $regProf)
    Write-Host ("  Source-backed Variants:   {0,4} (Expected: 24)" -f $srcVar)
    Write-Host ("  Generic Task Templates:   {0,4} (Expected: 96)" -f $taskTpl)
    Write-Host ("  FG Internal Locations:    {0,4} (Expected: 19)" -f $fgLocs)
    Write-Host ("  Static WorkSite Plans:    {0,4} (Expected: 1775)" -f $plans)
    Write-Host ("  Live RDS WorkSites:       {0,4} (Dynamic: e.g. ~1888)" -f $liveWs)

    $pfPass = ($actMach -eq 9 -and $cfgMach -eq 9 -and $regProf -eq 25 -and $srcVar -eq 24 -and $taskTpl -eq 96 -and $fgLocs -eq 19 -and $plans -eq 1775)
    if ($pfPass) {
        Write-Host "-> PASS: All preflight baselines verified." -ForegroundColor Green
        $results['Stack preflight'] = 'PASS'
    } else {
        Write-Host "-> FAIL: Baseline preflight invariant mismatch." -ForegroundColor Red
        $results['Stack preflight'] = 'FAIL'
        exit 1
    }
}

# ==============================================================================
# PHASE 2: Runtime Persistence Across Restart
# ==============================================================================
if ($Phase -in @('All', 'Persistence')) {
    if ($SkipRestart) {
        Write-Host "`nSkipping restart persistence check (-SkipRestart requested)." -ForegroundColor Yellow
        $results['Runtime persistence'] = 'SKIP (-SkipRestart)'
    } else {
        $pScript = Join-Path $scriptDir 'test_runtime_persistence.ps1'
        & pwsh -NoProfile -File $pScript -HostName $HostName -Port $PgPort -Database $Database -User $User
        if ($LASTEXITCODE -eq 0) {
            $results['Runtime persistence'] = 'PASS'
        } else {
            $results['Runtime persistence'] = 'FAIL'
            exit 1
        }
    }
}

# ==============================================================================
# PHASE 3: 9-Machine Production Lifecycle Matrix
# ==============================================================================
if ($Phase -in @('All', 'NineMachine')) {
    $nmScript = Join-Path $scriptDir 'test_nine_machine_production.ps1'
    & pwsh -NoProfile -File $nmScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $PgPort -Database $Database -User $User
    if ($LASTEXITCODE -eq 0) {
        $results['9-machine production'] = 'PASS 9/9'
    } else {
        $results['9-machine production'] = 'FAIL'
        exit 1
    }
}

# ==============================================================================
# PHASE 4: Mamul Kasa Business Actions Matrix
# ==============================================================================
if ($Phase -in @('All', 'MamulKasa')) {
    $mkScript = Join-Path $scriptDir 'test_mamul_kasa.ps1'
    & pwsh -NoProfile -File $mkScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $PgPort -Database $Database -User $User
    if ($LASTEXITCODE -eq 0) {
        $results['Mamul Kasa'] = 'PASS'
    } else {
        $results['Mamul Kasa'] = 'FAIL'
        exit 1
    }
}

# ==============================================================================
# PHASE 5: Repeated ALT Demand & Dependency Blocking
# ==============================================================================
if ($Phase -in @('All', 'Alt')) {
    $altScript = Join-Path $scriptDir 'test_repeated_alt_demand.ps1'
    & pwsh -NoProfile -File $altScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $PgPort -Database $Database -User $User
    if ($LASTEXITCODE -eq 0) {
        $results['Repeated ALT'] = 'PASS'
    } else {
        $results['Repeated ALT'] = 'FAIL'
        exit 1
    }
}

# ==============================================================================
# PHASE 6: Real Production End Planning & Atomic Rollback
# ==============================================================================
if ($Phase -in @('All', 'ProductionEnd')) {
    $peScript = Join-Path $scriptDir 'test_production_end.ps1'
    & pwsh -NoProfile -File $peScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $PgPort -Database $Database -User $User
    if ($LASTEXITCODE -eq 0) {
        $results['Production End'] = 'PASS'
    } else {
        $results['Production End'] = 'FAIL'
        exit 1
    }
}

# ==============================================================================
# PHASE 7: Real Operator Web Browser E2E Automation
# ==============================================================================
if ($Phase -in @('All', 'Browser')) {
    if ($SkipBrowser) {
        Write-Host "`nSkipping Browser E2E (-SkipBrowser requested)." -ForegroundColor Yellow
        $results['Operator Web Browser'] = 'SKIP (-SkipBrowser)'
    } else {
        $pyScript = Join-Path $scriptDir 'test_operator_web_e2e.py'
        $env:TKG_OPERATOR_WEB_URL = $OperatorWebUrl
        $env:TKG_API_BASE_URL = $ApiBaseUrl
        & python $pyScript
        $pyExit = $LASTEXITCODE
        if ($pyExit -eq 0) {
            $results['Operator Web Browser'] = 'PASS'
        } elseif ($pyExit -eq 2) {
            $results['Operator Web Browser'] = 'SKIP (Tooling Unavailable)'
        } else {
            $results['Operator Web Browser'] = 'FAIL'
            exit 1
        }
    }
}

# ==============================================================================
# PHASE 8: WorkSite Audit & Dynamic Mirror Report
# ==============================================================================
if ($Phase -in @('All', 'WorkSites')) {
    $wsScript = Join-Path $scriptDir 'report_worksites.ps1'
    & pwsh -NoProfile -File $wsScript -HostName $HostName -Port $PgPort -Database $Database -User $User
    if ($LASTEXITCODE -eq 0) {
        $results['WorkSite report'] = 'PASS'
    } else {
        $results['WorkSite report'] = 'FAIL'
        exit 1
    }
}

# ==============================================================================
# FINAL ACCEPTANCE SUMMARY
# ==============================================================================
$startTime.Stop()
$elapsed = [Math]::Round($startTime.Elapsed.TotalSeconds, 1)

Write-Host "`n==================================================" -ForegroundColor Green
Write-Host "TKG PDA LOCAL ACCEPTANCE" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green

$allLocalPass = $true
foreach ($k in $results.Keys) {
    $val = $results[$k]
    $color = if ($val -like 'PASS*') { 'Green' } elseif ($val -like 'SKIP*') { 'Yellow' } else { 'Red' }
    Write-Host ("{0,-28} {1}" -f "$k .................", $val) -ForegroundColor $color
    if ($val -like 'FAIL*') { $allLocalPass = $false }
    if ($k -eq 'Operator Web Browser' -and $val -like 'SKIP*') {
        # Per rule: If browser E2E could not be completed, status cannot be LOCAL E2E PASS
        $allLocalPass = $false
    }
}

Write-Host "--------------------------------------------------" -ForegroundColor Cyan
Write-Host "Total Execution Time: ${elapsed}s"

if ($allLocalPass) {
    Write-Host "RESULT:" -ForegroundColor Green
    Write-Host "LOCAL E2E PASS — FIELD COMMISSIONING INPUTS PENDING" -ForegroundColor Green
    Write-Host "==================================================" -ForegroundColor Green
    exit 0
} else {
    Write-Host "RESULT:" -ForegroundColor Yellow
    Write-Host "CODE PASS — LOCAL E2E ACCEPTANCE SUITE INCOMPLETE" -ForegroundColor Yellow
    Write-Host "==================================================" -ForegroundColor Yellow
    exit 1
}
