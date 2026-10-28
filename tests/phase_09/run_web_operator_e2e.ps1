param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$PgPort = 55446,
    [int]$ApiPort = 55447,
    [int]$ChromePort = 7357,
    [int]$EdgePort = 7358
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$tempRoot = Join-Path $tempParent 'tkg-pda-phase09-web-e2e'
$dataDir = Join-Path $tempRoot 'data'
$database = 'tkg_pda_phase09'
$apiProcess = $null
$webDriverProcess = $null
$webDriverProxyProcess = $null
$postgresStarted = $false

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE"
    }
}

function New-PasswordHash {
    param([Parameter(Mandatory = $true)][string]$Password)

    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        [Text.Encoding]::UTF8.GetBytes($Password),
        $salt,
        210000,
        [Security.Cryptography.HashAlgorithmName]::SHA512,
        32)
    return '$tkg-pbkdf2-sha512$v=1$i=210000${0}${1}' -f `
        [Convert]::ToBase64String($salt), [Convert]::ToBase64String($hash)
}

function Install-WebDriver {
    param(
        [Parameter(Mandatory = $true)][string]$BrowserPath,
        [Parameter(Mandatory = $true)][ValidateSet('Chrome', 'Edge')][string]$Browser,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $version = (Get-Item -LiteralPath $BrowserPath).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Could not discover the installed $Browser version."
    }

    $archive = Join-Path $Destination "$($Browser.ToLowerInvariant())driver.zip"
    $extract = Join-Path $Destination "$($Browser.ToLowerInvariant())driver"
    $url = if ($Browser -eq 'Chrome') {
        "https://storage.googleapis.com/chrome-for-testing-public/$version/win64/chromedriver-win64.zip"
    }
    else {
        "https://msedgedriver.microsoft.com/$version/edgedriver_win64.zip"
    }

    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $executableName = if ($Browser -eq 'Chrome') { 'chromedriver.exe' } else { 'msedgedriver.exe' }
    $driver = Get-ChildItem -LiteralPath $extract -Filter $executableName -Recurse | Select-Object -First 1
    if (-not $driver) {
        throw "$Browser WebDriver was not present in the downloaded archive."
    }

    return $driver.FullName
}

function Start-WebDriver {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [int]$Port = 4444
    )

    $script:webDriverProcess = Start-Process -FilePath $Executable `
        -ArgumentList @("--port=$Port", "--log-path=$LogPath") `
        -WindowStyle Hidden -PassThru
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($script:webDriverProcess.HasExited) {
            throw "WebDriver exited before readiness. See $LogPath"
        }
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
            return
        }
        Start-Sleep -Milliseconds 100
    }

    throw "WebDriver did not listen on port $Port. See $LogPath"
}

function Stop-WebDriver {
    param([int]$Port = 4444)

    if ($script:webDriverProcess -and -not $script:webDriverProcess.HasExited) {
        Stop-Process -Id $script:webDriverProcess.Id -Force
        $script:webDriverProcess.WaitForExit()
    }
    $script:webDriverProcess = $null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if (-not (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "WebDriver port $Port did not close cleanly."
}

function Start-EdgeCapabilityProxy {
    $env:WEBDRIVER_PROXY_PORT = '4444'
    $env:WEBDRIVER_TARGET_PORT = '4445'
    $script:webDriverProxyProcess = Start-Process -FilePath 'node' `
        -ArgumentList @((Join-Path $PSScriptRoot 'webdriver_edge_proxy.mjs')) `
        -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $tempRoot 'edge-proxy.out.log') `
        -RedirectStandardError (Join-Path $tempRoot 'edge-proxy.err.log')
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($script:webDriverProxyProcess.HasExited) {
            throw "Edge capability proxy exited before readiness. See $tempRoot\edge-proxy.err.log"
        }
        if (Get-NetTCPConnection -State Listen -LocalPort 4444 -ErrorAction SilentlyContinue) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Edge capability proxy did not listen on port 4444.'
}

function Stop-EdgeCapabilityProxy {
    if ($script:webDriverProxyProcess -and -not $script:webDriverProxyProcess.HasExited) {
        Stop-Process -Id $script:webDriverProxyProcess.Id -Force
        $script:webDriverProxyProcess.WaitForExit()
    }
    $script:webDriverProxyProcess = $null
    Remove-Item Env:WEBDRIVER_PROXY_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:WEBDRIVER_TARGET_PORT -ErrorAction SilentlyContinue
}

$ports = @($PgPort, $ApiPort, $ChromePort, $EdgePort, 4444, 4445)
if (Test-Path -LiteralPath $tempRoot) {
    throw "Temporary Phase 09 path already exists: $tempRoot"
}
foreach ($port in $ports) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
        throw "Phase 09 test port is already in use: $port"
    }
}

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $chromePath = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
    $edgePath = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
    $chromeDriver = Install-WebDriver -BrowserPath $chromePath -Browser Chrome -Destination $tempRoot
    $edgeDriver = Install-WebDriver -BrowserPath $edgePath -Browser Edge -Destination $tempRoot

    $env:PGCLIENTENCODING = 'UTF8'
    Invoke-Checked (Join-Path $PgBin 'initdb.exe') @(
        '-D', $dataDir, '-U', 'postgres', '-A', 'trust', '--no-locale', '-E', 'UTF8'
    )
    Invoke-Checked (Join-Path $PgBin 'pg_ctl.exe') @(
        '-D', $dataDir,
        '-l', (Join-Path $tempRoot 'postgres.log'),
        '-o', "-p $PgPort -h 127.0.0.1",
        '-w', 'start'
    )
    $postgresStarted = $true

    Invoke-Checked (Join-Path $PgBin 'createdb.exe') @(
        '-h', '127.0.0.1', '-p', "$PgPort", '-U', 'postgres', $database
    )

    $psql = Join-Path $PgBin 'psql.exe'
    $common = @(
        '-X', '-h', '127.0.0.1', '-p', "$PgPort", '-U', 'postgres',
        '-d', $database, '-v', 'ON_ERROR_STOP=1'
    )
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\001_initial_schema.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\002_operator_employee_identity.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\003_transport_batch.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\004_transport_request_readiness.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002a_customer_textual_master_data.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002b_customer_physical_layouts.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\005_layout_slot_part_mapping.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\006_production_sessions.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\007_bring_full_task_type.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\008_warehouse_preparation_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\009_preparation_placement_key.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\010_finished_goods_store.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\014_transport_event_context.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\015_part_demand_dependencies.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\016_finished_goods_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\004_finished_goods_internal_locations.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\017_internal_device_notifications.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\018_production_end_priorities.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\019_robot_alarm_observations.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\020_robot_alarm_observation_state.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\021_production_end_cleanup.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\022_variant_scoped_rds_worksite_commissioning.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\023_rds_variant_commissioning_hardening.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\024_operational_reporting.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\025_profile_scoped_shared_endpoints.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\026_canonical_shared_endpoint_identities.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\003_tkg_rds_variant_commissioning.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\005_tkg_variant_task_templates.sql')))

    $testPassword = [Convert]::ToBase64String(
        [Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $passwordHash = New-PasswordHash -Password $testPassword
    $seedSql = @'
INSERT INTO auth.users (username, password_hash, full_name, employee_no, is_active)
VALUES ('phase09-operator', :'password_hash', 'Phase 09 Web Operator', 'P09-WEB', true);

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id
FROM auth.users u
CROSS JOIN auth.roles r
WHERE u.username = 'phase09-operator' AND r.code = 'SUPERVISOR';

INSERT INTO audit.client_devices (device_code, name, device_type, is_active)
VALUES ('SH01A-TERM01', 'SH-01-A Hat Dokunmatik Terminali', 'PDA', true);

WITH selected AS (
  SELECT m.id AS machine_id, v.id AS variant_id, a.layout_id
  FROM master.machines m
  JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
  JOIN master.variant_layout_assignments a ON a.machine_id = m.id AND a.variant_id = v.id
  JOIN master.layouts l ON l.id = a.layout_id AND l.status = 'PUBLISHED'
  WHERE m.code = 'SH-01-A'
  ORDER BY a.is_default DESC, l.code
  LIMIT 1
)
INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id)
SELECT machine_id, variant_id, layout_id FROM selected;

INSERT INTO auth.user_machine_permissions (
  user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
SELECT u.id, m.id, true, true, true, false
FROM auth.users u
CROSS JOIN master.machines m
WHERE u.username = 'phase09-operator' AND m.code = 'SH-01-A';

WITH slots AS (
  SELECT s.id, row_number() OVER (ORDER BY s.display_order, s.code) AS rn
  FROM master.layout_slots s
  JOIN ops.machine_runtime_state state ON state.active_layout_id = s.layout_id
  JOIN master.machines m ON m.id = state.machine_id
  WHERE m.code = 'SH-01-A' AND s.slot_role <> 'MACHINE' AND s.is_enabled
)
INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
SELECT id,
       CASE WHEN rn = 1 THEN 'VACANT' ELSE 'OCCUPIED' END,
       CASE WHEN rn = 1 THEN 'UNKNOWN' WHEN rn = 2 THEN 'EMPTY' ELSE 'FULL' END
FROM slots
WHERE rn <= 3;

-- Customer Workflow â€” Empty Container Buffer: BRING_EMPTY auto-allocates its
-- source from a configured EMPTY_STORAGE slot. The real customer layout has
-- none, so this test provisions one directly (test-only, not production
-- master data) for the single BRING_EMPTY submission this test performs.
INSERT INTO master.layout_slots (layout_id, code, display_name, slot_role, display_order)
SELECT state.active_layout_id, 'P09-BUFFER-1', 'Phase 09 Test Buffer', 'EMPTY_STORAGE', 9000
FROM ops.machine_runtime_state state
JOIN master.machines m ON m.id = state.machine_id
WHERE m.code = 'SH-01-A';

INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
SELECT s.id, 'OCCUPIED', 'EMPTY'
FROM master.layout_slots s
WHERE s.code = 'P09-BUFFER-1';
'@
    $seedPath = Join-Path $tempRoot 'seed.sql'
    Set-Content -LiteralPath $seedPath -Value $seedSql -Encoding utf8NoBOM
    Invoke-Checked $psql ($common + @('-v', "password_hash=$passwordHash", '-f', $seedPath))

    $env:ConnectionStrings__TkgPda = "Host=127.0.0.1;Port=$PgPort;Database=$database;Username=postgres;Pooling=true;Maximum Pool Size=50"
    $env:Authentication__Jwt__SigningKey = [Convert]::ToBase64String(
        [Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$ApiPort"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:Cors__AllowedOrigins__0 = "http://127.0.0.1:$ChromePort"
    $env:Cors__AllowedOrigins__1 = "http://127.0.0.1:$EdgePort"
    $env:Rds__Enabled = 'false'
    $env:Rds__DispatchEnabled = 'false'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    Invoke-Checked 'dotnet' @(
        'build', (Join-Path $projectRoot 'src\TkgPda.Api\TkgPda.Api.csproj'),
        '--configuration', 'Release', '--nologo'
    )

    $apiProjectRoot = Join-Path $projectRoot 'src\TkgPda.Api'
    $apiDll = Join-Path $apiProjectRoot 'bin\Release\net10.0\TkgPda.Api.dll'
    $apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll) `
        -WorkingDirectory $apiProjectRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $tempRoot 'api.out.log') `
        -RedirectStandardError (Join-Path $tempRoot 'api.err.log')

    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($apiProcess.HasExited) {
            throw "API exited before readiness. See $tempRoot\api.err.log"
        }
        try {
            $response = Invoke-WebRequest `
                -Uri "http://127.0.0.1:$ApiPort/health/ready" `
                -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $ready) {
        Get-Content -LiteralPath (Join-Path $tempRoot 'api.out.log') -ErrorAction SilentlyContinue | Out-Host
        Get-Content -LiteralPath (Join-Path $tempRoot 'api.err.log') -ErrorAction SilentlyContinue | Out-Host
        throw 'Phase 09 API did not become ready in time.'
    }

    $pdaRoot = Join-Path $projectRoot 'src\TkgPda.Pda'
    $sharedDefines = @(
        "--dart-define=TKG_API_BASE_URL=http://127.0.0.1:$ApiPort",
        '--dart-define=TKG_TEST_DEVICE_CODE=SH01A-TERM01',
        '--dart-define=TKG_TEST_EMPLOYEE_NUMBER=P09-WEB',
        "--dart-define=TKG_TEST_PASSWORD=$testPassword"
    )
    Push-Location $pdaRoot
    try {
        Start-WebDriver -Executable $chromeDriver -LogPath (Join-Path $tempRoot 'chromedriver.log')
        Invoke-Checked 'flutter' (@(
            'drive', '-d', 'web-server',
            '--browser-name=chrome',
            '--web-hostname=127.0.0.1',
            "--web-port=$ChromePort",
            '--browser-dimension=1366x768@1',
            '--no-web-resources-cdn',
            '--headless',
            '--target=integration_test/operator_web_e2e_test.dart',
            '--driver=test_driver/integration_test.dart',
            '--timeout=240'
        ) + $sharedDefines)
        Stop-WebDriver
        # Flutter 3.44 still emits legacy Edge capability names. The local proxy
        # only translates those names before forwarding to matching EdgeDriver.
        Start-WebDriver -Executable $edgeDriver -LogPath (Join-Path $tempRoot 'msedgedriver.log') -Port 4445
        Start-EdgeCapabilityProxy
        Invoke-Checked 'flutter' (@(
            'drive', '-d', 'web-server',
            '--browser-name=chrome',
            '--web-hostname=127.0.0.1',
            "--web-port=$EdgePort",
            '--browser-dimension=1280x720@1',
            '--no-web-resources-cdn',
            '--headless',
            '--target=integration_test/operator_web_e2e_test.dart',
            '--driver=test_driver/integration_test.dart',
            '--timeout=240'
        ) + $sharedDefines)
        Stop-EdgeCapabilityProxy
        Stop-WebDriver -Port 4445
    }
    finally {
        Pop-Location
    }

    $verification = (& $psql @common -Atc @"
SELECT json_build_object(
  'postgresql_version', current_setting('server_version'),
  'machine_master_records', (SELECT count(*) FROM master.machines),
  'phase09_devices', (SELECT count(*) FROM audit.client_devices WHERE device_code = 'SH01A-TERM01'),
  'phase09_requests', (SELECT count(*) FROM ops.transport_requests),
  'orphan_requests', (
    SELECT count(*) FROM ops.transport_requests tr
    LEFT JOIN master.machines m ON m.id = tr.machine_id
    WHERE m.id IS NULL),
  'result', 'PASS'
);
"@) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Phase 09 PostgreSQL verification query failed.' }
    $verification | ConvertTo-Json
}
finally {
    Stop-EdgeCapabilityProxy
    Stop-WebDriver -Port 4444
    Stop-WebDriver -Port 4445
    @(
        'PGCLIENTENCODING',
        'ConnectionStrings__TkgPda',
        'Authentication__Jwt__SigningKey',
        'ASPNETCORE_URLS',
        'ASPNETCORE_ENVIRONMENT',
        'Cors__AllowedOrigins__0',
        'Cors__AllowedOrigins__1',
        'Rds__Enabled',
        'Rds__DispatchEnabled',
        'DOTNET_CLI_TELEMETRY_OPTOUT'
    ) | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force
        $apiProcess.WaitForExit()
    }
    if ($postgresStarted -and (Test-Path -LiteralPath $dataDir)) {
        & (Join-Path $PgBin 'pg_ctl.exe') -D $dataDir -m fast -w stop | Out-Host
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = (Resolve-Path -LiteralPath $tempRoot).Path
        if (-not [String]::Equals(
                (Split-Path -Parent $resolvedTemp),
                $tempParent,
                [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedTemp) -ne 'tkg-pda-phase09-web-e2e') {
            throw "Refusing unsafe Phase 09 cleanup target: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
