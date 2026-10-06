param(
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin',
    [int]$PgPort = 55443,
    [int]$ApiPort = 55444
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$tempRoot = Join-Path $tempParent 'tkg-pda-phase07b-integration'
$dataDir = Join-Path $tempRoot 'data'
$database = 'tkg_pda_phase07b'
$apiProcess = $null
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

if (Test-Path -LiteralPath $tempRoot) {
    throw "Temporary integration path already exists: $tempRoot"
}
if (Get-NetTCPConnection -State Listen -LocalPort $PgPort -ErrorAction SilentlyContinue) {
    throw "PostgreSQL test port is already in use: $PgPort"
}
if (Get-NetTCPConnection -State Listen -LocalPort $ApiPort -ErrorAction SilentlyContinue) {
    throw "API test port is already in use: $ApiPort"
}

try {
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
    $common = @('-X', '-h', '127.0.0.1', '-p', "$PgPort", '-U', 'postgres', '-d', $database, '-v', 'ON_ERROR_STOP=1')
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\001_initial_schema.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\002_operator_employee_identity.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\003_transport_batch.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\004_transport_request_readiness.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\005_layout_slot_part_mapping.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\006_production_sessions.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\007_bring_full_task_type.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\008_warehouse_preparation_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\009_preparation_placement_key.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\010_finished_goods_store.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\014_transport_event_context.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\015_finished_goods_areas.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\migrations\016_android_push_notifications.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002a_customer_textual_master_data.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002b_customer_physical_layouts.sql')))
    Invoke-Checked $psql ($common + @('-f', (Join-Path $projectRoot 'db\seeds\generated\002c_customer_layout_slot_part_assignments.sql')))

    $testPassword = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $passwordHash = New-PasswordHash -Password $testPassword
    $seedSql = @'
INSERT INTO auth.users (username, password_hash, full_name, employee_no, is_active)
VALUES
  ('phase07b-admin', :'password_hash', 'Phase 07B Admin', 'P07B-A', true),
  ('phase07b-warehouse', :'password_hash', 'Phase 07B Warehouse', 'P07B-W', true);

INSERT INTO auth.user_roles (user_id, role_id)
SELECT u.id, r.id
FROM auth.users u
JOIN auth.roles r ON
  (u.username = 'phase07b-admin' AND r.code = 'ADMIN') OR
  (u.username = 'phase07b-warehouse' AND r.code = 'WAREHOUSE_OPERATOR');

INSERT INTO audit.client_devices (device_code, name, device_type, is_active)
VALUES
  ('P07B-PDA-ADMIN', 'Phase 07B Admin PDA', 'PDA', true),
  ('P07B-PDA-WAREHOUSE', 'Phase 07B Warehouse PDA', 'PDA', true),
  ('P07B-PDA-INACTIVE', 'Phase 07B Inactive PDA', 'PDA', false);

WITH selected AS (
  SELECT m.id AS machine_id, v.id AS variant_id, a.layout_id
  FROM master.machines m
  JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
  JOIN master.variant_layout_assignments a ON a.machine_id = m.id AND a.variant_id = v.id
  JOIN master.layouts l ON l.id = a.layout_id AND l.status = 'PUBLISHED'
  WHERE m.code = 'SH-02-P1 -SH-02-P2'
  ORDER BY a.is_default DESC, l.code
  LIMIT 1
)
INSERT INTO ops.machine_runtime_state (machine_id, active_variant_id, active_layout_id)
SELECT machine_id, variant_id, layout_id FROM selected;

INSERT INTO auth.user_machine_permissions (
  user_id, machine_id, can_view, can_operate, can_change_variant, can_manage_slots)
SELECT u.id, m.id, true, true, false, false
FROM auth.users u
CROSS JOIN master.machines m
WHERE u.username = 'phase07b-warehouse' AND m.code = 'SH-02-P1 -SH-02-P2';

WITH slots AS (
  SELECT s.id, row_number() OVER (ORDER BY s.display_order, s.code) AS rn
  FROM master.layout_slots s
  JOIN ops.machine_runtime_state state ON state.active_layout_id = s.layout_id
  JOIN master.machines m ON m.id = state.machine_id
  WHERE m.code = 'SH-02-P1 -SH-02-P2'
    AND s.slot_role <> 'MACHINE'
    AND s.is_enabled
)
INSERT INTO ops.slot_runtime_state (layout_slot_id, occupancy_state, load_state)
SELECT id,
       CASE WHEN rn = 1 THEN 'VACANT' ELSE 'OCCUPIED' END,
       CASE WHEN rn = 1 THEN 'UNKNOWN' ELSE 'FULL' END
FROM slots
WHERE rn <= 2;
'@
    $seedPath = Join-Path $tempRoot 'seed.sql'
    Set-Content -LiteralPath $seedPath -Value $seedSql -Encoding utf8NoBOM
    Invoke-Checked $psql ($common + @('-v', "password_hash=$passwordHash", '-f', $seedPath))

    $connectionString = "Host=127.0.0.1;Port=$PgPort;Database=$database;Username=postgres;Pooling=true;Maximum Pool Size=50"
    $env:ConnectionStrings__TkgPda = $connectionString
    $env:Authentication__Jwt__SigningKey = [Convert]::ToBase64String(
        [Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$ApiPort"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    Push-Location $tempRoot
    try {
        Invoke-Checked 'dotnet' @(
            'build', (Join-Path $projectRoot 'src\TkgPda.Api\TkgPda.Api.csproj'),
            '--configuration', 'Release', '--nologo'
        )
    }
    finally {
        Pop-Location
    }

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
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$ApiPort/health/ready" -UseBasicParsing -TimeoutSec 2
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
        throw 'API did not become ready in time.'
    }

    $env:TKG_REAL_API_BASE_URL = "http://127.0.0.1:$ApiPort"
    $env:TKG_REAL_API_DEVICE_CODE = 'P07B-PDA-ADMIN'
    $env:TKG_REAL_API_USERNAME = 'phase07b-admin'
    $env:TKG_REAL_API_PASSWORD = $testPassword
    $env:TKG_REAL_API_WAREHOUSE_DEVICE_CODE = 'P07B-PDA-WAREHOUSE'
    $env:TKG_REAL_API_WAREHOUSE_USERNAME = 'phase07b-warehouse'
    $env:TKG_REAL_API_INACTIVE_DEVICE_CODE = 'P07B-PDA-INACTIVE'
    $env:TKG_REAL_API_MACHINE_CODE = 'SH-02-P1 -SH-02-P2'

    Push-Location (Join-Path $projectRoot 'src\TkgPda.Pda')
    try {
        Invoke-Checked 'flutter' @(
            'test', 'tool\phase_07b\real_backend_contract_test.dart',
            '--reporter', 'expanded'
        )
    }
    finally {
        Pop-Location
    }

    $verification = (& $psql @common -Atc @"
SELECT json_build_object(
  'postgresql_version', current_setting('server_version'),
  'machine_master_records', (SELECT count(*) FROM master.machines),
  'rds_worksites', (SELECT count(*) FROM integration.rds_worksites),
  'rds_tasks', (SELECT count(*) FROM integration.rds_tasks),
  'result', 'PASS'
);
"@) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL verification query failed' }
    $verification | ConvertTo-Json
}
finally {
    @(
        'ConnectionStrings__TkgPda',
        'Authentication__Jwt__SigningKey',
        'ASPNETCORE_URLS',
        'TKG_REAL_API_BASE_URL',
        'TKG_REAL_API_DEVICE_CODE',
        'TKG_REAL_API_USERNAME',
        'TKG_REAL_API_PASSWORD',
        'TKG_REAL_API_WAREHOUSE_DEVICE_CODE',
        'TKG_REAL_API_WAREHOUSE_USERNAME',
        'TKG_REAL_API_INACTIVE_DEVICE_CODE',
        'TKG_REAL_API_MACHINE_CODE'
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
            (Split-Path -Leaf $resolvedTemp) -ne 'tkg-pda-phase07b-integration') {
            throw "Refusing unsafe cleanup target: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
