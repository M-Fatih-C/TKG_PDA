<#
.SYNOPSIS
    Master canonical acceptance runner for TKG PDA local development stack.
.DESCRIPTION
    Validates the local TKG PDA environment with strict safety guarantees:
    
    TIER 1: PERSISTENT SAFETY ACCEPTANCE (Target: tkg_pda_dev)
    - Git baseline and branch state
    - Stack preflight and metric invariants
    - Runtime slot state persistence across restart
    - 9-machine production start lifecycle (rejects pre-existing active sessions safely)
    - Mamul Kasa business action state matrix (exact snapshot restore)
    - Commissioning boundary verification & persistent Production End invariants
    - Customer master ALT part invariants & missing container blocker projection
    - Real Operator Web Browser E2E automation (owned session tracking, pre-existing session safe)
    - WorkSite static plans and live mirror audit
    - Read-only before/after comparison of complete pre-existing business rows
    
    TIER 2: ISOLATED SYNTHETIC ACCEPTANCE (Target: tkg_pda_acceptance)
    - Recreates dedicated acceptance database with deterministic customer/dev seeds
    - Synthetic Production End full workflow (FG, RETURN, EMPTY, dependencies, idempotency, capacity atomicity)
    - Synthetic Repeated ALT full workflow (Cycle 1 & Cycle 2 dependency chains, runtime rejection)
    - Strict safety gate: refuses execution against tkg_pda_dev
    - Safety regression sentinel test with exact-ID cleanup in isolated acceptance DB

    TIER 3: API TEST SUITE VERIFICATION
    - Actual DB-backed API suite via phase 04 throwaway PostgreSQL cluster
    
    Returns 0 for successful selected checks or fully evaluated LOCAL E2E PASS.
    Returns 1 on software/safety failure and 2 on blocked/skipped full acceptance.
.EXAMPLE
    pwsh -File tools/dev/acceptance/run_local_acceptance.ps1
.EXAMPLE
    pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -Phase Sentinel
.EXAMPLE
    pwsh -File tools/dev/acceptance/run_local_acceptance.ps1 -SkipBrowser
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Git', 'Preflight', 'Persistence', 'NineMachine', 'MamulKasa', 'Alt', 'ProductionEnd', 'Browser', 'WorkSites', 'Sentinel', 'SyntheticProductionEnd', 'SyntheticAlt', 'Synthetic', 'ApiUnit')]
    [string]$Phase = 'All',
    [switch]$SkipBrowser,
    [switch]$SkipRestart,
    [switch]$SkipSynthetic,
    [switch]$DropAcceptanceDb,
    [string]$ApiBaseUrl = 'http://127.0.0.1:5000',
    [string]$OperatorWebUrl = 'http://127.0.0.1:7357',
    [string]$HostName = '127.0.0.1',
    [int]$PgPort = 55450,
    [string]$Database = 'tkg_pda_dev',
    [string]$AcceptanceDatabase = 'tkg_pda_acceptance',
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

$Port = $PgPort
. (Join-Path $scriptDir 'persistent_safety.ps1')
$persistentBefore = $null
# Check service availability
$pgListening = Test-TcpPort -Port $PgPort
$apiListening = Test-TcpPort -Port 5000
$webListening = Test-TcpPort -Port 7357
if ($pgListening) { $persistentBefore = Get-PersistentBusinessSnapshot }

# Targeted runs and -SkipRestart must not reuse a dispatch-enabled API.
# Startup records the mode and owned API process; unverifiable state is unsafe.
if ($ApiBaseUrl.TrimEnd('/') -ne 'http://127.0.0.1:5000') {
    throw 'Persistent acceptance requires its managed local API on http://127.0.0.1:5000.'
}
$apiKnownOff = $false
$stackStatePath = Join-Path $env:LOCALAPPDATA 'TKG_PDA_DEV\state\stack_state.json'
if ($apiListening -and (Test-Path -LiteralPath $stackStatePath)) {
    $stackState = Get-Content -LiteralPath $stackStatePath -Raw | ConvertFrom-Json
    $apiListener = Get-NetTCPConnection -State Listen -LocalPort 5000 | Select-Object -First 1
    $apiProcessInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$($apiListener.OwningProcess)"
    $ownedApi = $stackState.apiPid -eq $apiListener.OwningProcess -or
        $stackState.apiPid -eq $apiProcessInfo.ParentProcessId
    $apiKnownOff = $stackState.rdsMode -eq 'Off' -and $ownedApi -and
        $stackState.database -eq $Database -and $stackState.postgresPort -eq $PgPort
}

Write-Host ("Services: PostgreSQL ({0}) = {1} | API (5000) = {2} | Operator Web (7357) = {3}" -f `
    $PgPort, $(if ($pgListening) {'UP'} else {'DOWN'}), `
    $(if ($apiListening) {'UP'} else {'DOWN'}), `
    $(if ($webListening) {'UP'} else {'DOWN'}))

if (-not $pgListening -or -not $apiListening -or -not $apiKnownOff -or (-not $webListening -and -not $SkipBrowser)) {
    Write-Host "`nStarting managed development stack with RDS Off before acceptance..." -ForegroundColor Yellow
    $stackArgs = @()
    if ($env:TKG_TEST_PASSWORD) {
        $stackArgs += @('-Password', $env:TKG_TEST_PASSWORD)
    }
    pwsh -File (Join-Path $projectRoot 'tools\dev\start_local_stack.ps1') -RdsMode Off @stackArgs
    if ($LASTEXITCODE -ne 0) { throw 'RDS-Off acceptance bootstrap failed.' }
    Start-Sleep -Seconds 3
}
# Cold bootstrap cannot be queried until PostgreSQL is available. Subsequent
# restart checks and the entire acceptance run preserve this full baseline.
if ($null -eq $persistentBefore) { $persistentBefore = Get-PersistentBusinessSnapshot }

# ==============================================================================
# PHASE 0: Git Baseline Check
# ==============================================================================
if ($Phase -in @('All', 'Git')) {
    Log-Header "PHASE: GIT BASELINE"
    $branch = (git branch --show-current).Trim()
    $head = (git rev-parse HEAD).Trim()
    $statusOut = (git status --porcelain | Out-String).Trim()
    $isClean = [string]::IsNullOrWhiteSpace($statusOut)
    $baselineSha = 'c5f67d3f0303afde18bab3fecb8025ab9dfee3b4'

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
# TIER 1: PERSISTENT SAFETY ACCEPTANCE (Target: tkg_pda_dev)
# ==============================================================================

# Phase 1: Stack Preflight & Metric Invariants
if ($Phase -in @('All', 'Preflight')) {
    Log-Header "PHASE 1: STACK PREFLIGHT & METRIC INVARIANTS"
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
    Write-Host ("  Live RDS WorkSites:       {0,4} (Dynamic observation)" -f $liveWs)

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

# Phase 2: Runtime Persistence Across Restart
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

# Phase 3: 9-Machine Production Lifecycle Matrix
if ($Phase -in @('All', 'NineMachine')) {
    $nmScript = Join-Path $scriptDir 'test_nine_machine_production.ps1'
    & pwsh -NoProfile -File $nmScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $PgPort -Database $Database -User $User
    if ($LASTEXITCODE -eq 0) {
        $results['9-machine production'] = 'PASS 9/9'
    } elseif ($LASTEXITCODE -eq 2) {
        $results['9-machine production'] = 'BLOCKED (ENVIRONMENT_BUSY)'
    } else {
        $results['9-machine production'] = 'FAIL'
        exit 1
    }
}

# Phase 4: Mamul Kasa Business Actions Matrix
if ($Phase -in @('All', 'MamulKasa')) {
    $mkScript = Join-Path $scriptDir 'test_mamul_kasa.ps1'
    & pwsh -NoProfile -File $mkScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $PgPort -Database $Database -User $User
    if ($LASTEXITCODE -eq 0) {
        $results['Mamul Kasa'] = 'PASS'
    } elseif ($LASTEXITCODE -eq 2) {
        $results['Mamul Kasa'] = 'BLOCKED (ENVIRONMENT_BUSY)'
    } else {
        $results['Mamul Kasa'] = 'FAIL'
        exit 1
    }
}

# Phase 5: Repeated ALT Customer Master Invariants
if ($Phase -in @('All', 'Alt')) {
    $altScript = Join-Path $scriptDir 'test_repeated_alt_demand.ps1'
    & pwsh -NoProfile -File $altScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $PgPort -Database $Database -User $User
    if ($LASTEXITCODE -eq 0) {
        $results['Repeated ALT (Persistent)'] = 'PASS'
    } elseif ($LASTEXITCODE -eq 2) {
        $results['Repeated ALT (Persistent)'] = 'BLOCKED (ENVIRONMENT_BUSY)'
    } else {
        $results['Repeated ALT (Persistent)'] = 'FAIL'
        exit 1
    }
}

# Phase 6: Production End Commissioning Blocker & Invariants
if ($Phase -in @('All', 'ProductionEnd')) {
    $peScript = Join-Path $scriptDir 'test_production_end.ps1'
    & pwsh -NoProfile -File $peScript -ApiBaseUrl $ApiBaseUrl -HostName $HostName -Port $PgPort -Database $Database -User $User
    if ($LASTEXITCODE -eq 0) {
        $results['Production End (Persistent)'] = 'PASS'
    } elseif ($LASTEXITCODE -eq 2) {
        $results['Production End (Persistent)'] = 'BLOCKED (ENVIRONMENT_BUSY)'
    } else {
        $results['Production End (Persistent)'] = 'FAIL'
        exit 1
    }
}

# Phase 7: Real Operator Web Browser E2E Automation
if ($Phase -in @('All', 'Browser')) {
    if ($SkipBrowser) {
        Write-Host "`nSkipping Browser E2E (-SkipBrowser requested)." -ForegroundColor Yellow
        $results['Operator Web Browser'] = 'SKIP (-SkipBrowser)'
    } else {
        $pyScript = Join-Path $scriptDir 'test_operator_web_e2e.py'
        $env:TKG_OPERATOR_WEB_URL = $OperatorWebUrl
        $env:TKG_API_BASE_URL = $ApiBaseUrl
        $env:TKG_TEST_DATABASE = $Database
        $env:TKG_TEST_PG_HOST = $HostName
        $env:TKG_TEST_PG_PORT = "$PgPort"
        $env:TKG_TEST_PG_USER = $User
        & python $pyScript
        $pyExit = $LASTEXITCODE
        if ($pyExit -eq 0) {
            $results['Operator Web Browser'] = 'PASS'
        } elseif ($pyExit -eq 2) {
            $results['Operator Web Browser'] = 'SKIP (Tooling/Env Busy)'
        } else {
            $results['Operator Web Browser'] = 'FAIL'
            exit 1
        }
    }
}

# Phase 8: WorkSite Audit & Dynamic Mirror Report
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

# Verify existing persistent rows; synthetic sentinel belongs to Tier 2.
Assert-PersistentBusinessSnapshot $persistentBefore
$results['Persistent business preservation'] = 'PASS'

# ==============================================================================
# TIER 2: ISOLATED SYNTHETIC ACCEPTANCE (Target: tkg_pda_acceptance)
# ==============================================================================
if ($Phase -in @('All', 'Sentinel', 'Synthetic', 'SyntheticProductionEnd', 'SyntheticAlt') -and -not $SkipSynthetic) {
    Log-Header "TIER 2: ISOLATED SYNTHETIC ACCEPTANCE (DATABASE: $AcceptanceDatabase)"

    # Safety Gate: MUST NOT run against persistent dev database
    if ($AcceptanceDatabase -eq 'tkg_pda_dev' -or $AcceptanceDatabase -eq 'tkg_pda') {
        throw "SAFETY GATE: AcceptanceDatabase cannot be a persistent database ('$AcceptanceDatabase')."
    }

    Write-Host "`nProving synthetic scripts refuse tkg_pda_dev before any SQL..." -ForegroundColor Cyan
    & pwsh -NoProfile -File (Join-Path $scriptDir 'test_synthetic_production_end.ps1') -Database tkg_pda_dev
    if ($LASTEXITCODE -eq 0) {
        throw "SAFETY GATE FAILED: synthetic Production End accepted tkg_pda_dev."
    }
    & pwsh -NoProfile -File (Join-Path $scriptDir 'test_synthetic_repeated_alt.ps1') -Database tkg_pda_dev
    if ($LASTEXITCODE -eq 0) {
        throw "SAFETY GATE FAILED: synthetic repeated ALT accepted tkg_pda_dev."
    }
    $results['Synthetic DB gate'] = 'PASS'

    # Step 1: Initialize isolated acceptance database
    Write-Host "`nInitializing isolated acceptance database '$AcceptanceDatabase'..." -ForegroundColor Cyan
    $initScript = Join-Path $scriptDir 'init_acceptance_db.ps1'
    & pwsh -NoProfile -File $initScript -Database $AcceptanceDatabase -HostName $HostName -Port $PgPort -User $User
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to initialize isolated acceptance database '$AcceptanceDatabase'."
    }

    # Step 2: Start temporary secondary API on port 5001 connected to $AcceptanceDatabase
    if (Test-TcpPort -Port 5001) { throw 'Isolated API port 5001 is already occupied; refusing to use an unowned API.' }
    Write-Host "`nStarting isolated secondary API on http://127.0.0.1:5001..." -ForegroundColor Cyan
    $apiDll = Join-Path $projectRoot 'src\TkgPda.Api\bin\Debug\net10.0\TkgPda.Api.dll'
    $connStr = "Host=$HostName;Port=$PgPort;Database=$AcceptanceDatabase;Username=$User;Pooling=true"

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'dotnet'
    $psi.Arguments = "`"$apiDll`" --urls http://127.0.0.1:5001"
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['ConnectionStrings__TkgPda'] = $connStr
    $psi.EnvironmentVariables['Authentication__Jwt__SigningKey'] = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $psi.EnvironmentVariables['Rds__Enabled'] = 'false'
    $psi.EnvironmentVariables['Rds__DispatchEnabled'] = 'false'
    $psi.EnvironmentVariables['RdsCoreExecution__Enabled'] = 'false'
    $psi.EnvironmentVariables['ASPNETCORE_URLS'] = 'http://127.0.0.1:5001'

    $synApiProc = [System.Diagnostics.Process]::Start($psi)

    try {
        # Wait for port 5001 ready
        $synReady = $false
        for ($i = 0; $i -lt 30; $i++) {
            if ($synApiProc.HasExited) { break }
            try {
                $r = Invoke-RestMethod -Uri "http://127.0.0.1:5001/health/ready" -TimeoutSec 1
                if ($r.status -eq 'ready') { $synReady = $true; break }
            } catch {
                Start-Sleep -Milliseconds 400
            }
        }
        if (-not $synReady) {
            throw "Isolated API on port 5001 failed to become ready."
        }
        Write-Host "Isolated secondary API is ready." -ForegroundColor Green

        if ($Phase -in @('All', 'Sentinel', 'Synthetic')) {
            & pwsh -NoProfile -File (Join-Path $scriptDir 'test_safety_sentinel.ps1') -ApiBaseUrl 'http://127.0.0.1:5001' -HostName $HostName -Port $PgPort -Database $AcceptanceDatabase -User $User
            if ($LASTEXITCODE -ne 0) { throw 'Isolated sentinel safety failed.' }
            $results['Safety Sentinel Guard'] = 'PASS (isolated DB)'
        }
        # Run Synthetic Production End
        if ($Phase -in @('All', 'Synthetic', 'SyntheticProductionEnd')) {
            $synPeScript = Join-Path $scriptDir 'test_synthetic_production_end.ps1'
            & pwsh -NoProfile -File $synPeScript -ApiBaseUrl 'http://127.0.0.1:5001' -HostName $HostName -Port $PgPort -Database $AcceptanceDatabase -User $User
            if ($LASTEXITCODE -eq 0) {
                $results['Synthetic Production End'] = 'PASS (Isolated DB)'
            } else {
                $results['Synthetic Production End'] = 'FAIL'
                exit 1
            }
        }

        # Run Synthetic Repeated ALT
        if ($Phase -in @('All', 'Synthetic', 'SyntheticAlt')) {
            $synAltScript = Join-Path $scriptDir 'test_synthetic_repeated_alt.ps1'
            & pwsh -NoProfile -File $synAltScript -ApiBaseUrl 'http://127.0.0.1:5001' -HostName $HostName -Port $PgPort -Database $AcceptanceDatabase -User $User
            if ($LASTEXITCODE -eq 0) {
                $results['Synthetic Repeated ALT'] = 'PASS (Isolated DB)'
            } else {
                $results['Synthetic Repeated ALT'] = 'FAIL'
                exit 1
            }
        }
    } finally {
        if ($synApiProc -and -not $synApiProc.HasExited) {
            $synApiProc.Kill()
            $synApiProc.WaitForExit()
        }
        if ($DropAcceptanceDb) {
            Write-Host "`nTearing down isolated acceptance database (-DropAcceptanceDb requested)..." -ForegroundColor Gray
            & pwsh -NoProfile -File $initScript -Database $AcceptanceDatabase -HostName $HostName -Port $PgPort -User $User -Drop
        }
    }
}

# ==============================================================================
# TIER 3: API TEST SUITE VERIFICATION (Requirement 9)
# ==============================================================================
if ($Phase -in @('All', 'ApiUnit')) {
    Log-Header "TIER 3: API TEST SUITE VERIFICATION"
    Write-Host "DB-backed API tests use the phase 04 throwaway cluster. They do not connect to tkg_pda_dev." -ForegroundColor Cyan
    $phase04 = Join-Path $projectRoot 'tests\phase_04\run_postgres_api_integration.ps1'
    $apiLines = [System.Collections.Generic.List[string]]::new()
    & powershell -NoProfile -ExecutionPolicy Bypass -File $phase04 -TestFilter 'FullyQualifiedName!~LiveRdsPostgresSyncTests' 2>&1 | ForEach-Object {
        $line = "$_"
        $apiLines.Add($line)
        Write-Host $line
    }
    $apiText = [string]::Join("`n", $apiLines)
    $passedLine = [regex]::Match($apiText, 'Ge.{1,4}ti:\s*(\d+)')
    $totalLine = [regex]::Match($apiText, 'Toplam test say.{1,8}:\s*(\d+)')
    $verificationPass = $apiText -match 'result.{0,30}PASS'
    if ($passedLine.Success -and $totalLine.Success -and $passedLine.Groups[1].Value -eq $totalLine.Groups[1].Value -and $verificationPass) {
        $count = $passedLine.Groups[1].Value
        $results['API DB integration'] = "PASS ($count/$count)"
        if ($LASTEXITCODE -ne 0) {
            $results['API harness teardown'] = 'WARN (pg_ctl cleanup after PASS)'
        }
    } else {
        $results['API DB integration'] = 'FAIL'
        exit 1
    }
}

# ==============================================================================
# FINAL ACCEPTANCE SUMMARY
# ==============================================================================
$startTime.Stop()
$elapsed = [Math]::Round($startTime.Elapsed.TotalSeconds, 1)

Write-Host "`n==================================================" -ForegroundColor Green
Write-Host "TKG PDA SAFE LOCAL ACCEPTANCE" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green

$allLocalPass = $true
foreach ($k in $results.Keys) {
    $val = $results[$k]
    $color = if ($val -like 'PASS*') { 'Green' } elseif ($val -like 'SKIP*') { 'Yellow' } elseif ($val -like 'NOT RUN*') { 'DarkCyan' } else { 'Red' }
    Write-Host ("{0,-35} {1}" -f "$k .........................", $val) -ForegroundColor $color
    if ($val -like 'BLOCKED*') { $allLocalPass = $false }
    if ($val -like 'FAIL*') { $allLocalPass = $false }
    if ($k -eq 'Operator Web Browser' -and $val -like 'SKIP*') {
        $allLocalPass = $false
    }
}

Write-Host "--------------------------------------------------" -ForegroundColor Cyan
Write-Host "Total Execution Time: ${elapsed}s"

Assert-PersistentBusinessSnapshot $persistentBefore
$completeRun = $Phase -eq 'All' -and -not $SkipBrowser -and -not $SkipRestart -and -not $SkipSynthetic
if ($allLocalPass -and $completeRun) {
    Write-Host "`nRESULT:" -ForegroundColor Green
    Write-Host "ACCEPTANCE SUITE SAFE — LOCAL E2E PASS — FIELD COMMISSIONING INPUTS PENDING" -ForegroundColor Green
    Write-Host "==================================================" -ForegroundColor Green
    exit 0
} elseif ($allLocalPass) {
    Write-Host 'RESULT: SELECTED CHECKS PASS — CODE PASS (full LOCAL E2E not evaluated)'
    exit 0
} elseif (@($results.Values | Where-Object { $_ -like 'BLOCKED*' -or $_ -like 'SKIP*' }).Count -gt 0 -and @($results.Values | Where-Object { $_ -like 'FAIL*' }).Count -eq 0) {
    Write-Host 'RESULT: CODE PASS — LOCAL ACCEPTANCE BLOCKED/SKIPPED; persistent business state preserved'
    exit 2
} else {
    Write-Host "`nRESULT:" -ForegroundColor Yellow
    Write-Host "ACCEPTANCE SUITE SAFETY FAIL" -ForegroundColor Yellow
    Write-Host "==================================================" -ForegroundColor Yellow
    exit 1
}
