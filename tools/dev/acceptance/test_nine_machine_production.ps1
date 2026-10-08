<#
.SYNOPSIS
    Acceptance test for 9-machine production start and clean end matrix.
.DESCRIPTION
    Dynamically resolves real active machine, source-backed variant, published layout,
    product group, and main part from the database for all 9 machines:
    412B, GAR, PAH-01-A, PAH-02-A, SEALER, SH-01-A, SH-01-B, SH-01-P1--SH-01-P2, SH-02-P1 -SH-02-P2.
    Tests production start, verifies active session, and performs clean production end.
    Leaves the database with no lingering active sessions.
#>
[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://127.0.0.1:5000',
    [string]$HostName = '127.0.0.1',
    [int]$Port = 55450,
    [string]$Database = 'tkg_pda_dev',
    [string]$User = 'postgres'
)

$ErrorActionPreference = 'Stop'
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path -LiteralPath $psql)) {
    $psql = (Get-Command psql.exe -ErrorAction SilentlyContinue)?.Source
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "PHASE: 9-MACHINE PRODUCTION SESSION ACCEPTANCE" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Login to obtain API bearer token
$loginRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/auth/login" `
    -Method Post `
    -Headers @{ 'X-Device-Code' = 'DEV-SUPERVISOR' } `
    -ContentType 'application/json' `
    -Body '{"employeeNumber": "DEV-SUP"}'

$token = $loginRes.accessToken
if (-not $token) {
    throw "Failed to authenticate dev supervisor on API."
}

$headers = @{
    'Authorization' = "Bearer $token"
    'X-Device-Code' = 'DEV-SUPERVISOR'
}

# 2. Check and cleanly end any existing active sessions
$activeSql = @"
SELECT 
    ps.id::text, 
    ps.machine_id::text, 
    m.code 
FROM ops.production_sessions ps 
JOIN master.machines m ON m.id = ps.machine_id 
WHERE ps.ended_at IS NULL;
"@
$activeRows = @(& $psql -h $HostName -p $Port -U $User -d $Database -A -F ',' -t -c $activeSql) |
    Where-Object { $_ -and -not ($_ -match '\([0-9]+ sat') }

foreach ($ar in $activeRows) {
    $parts = $ar.Trim().Split(',')
    if ($parts.Length -ge 3) {
        $sId = $parts[0]
        $mId = $parts[1]
        $mCode = $parts[2]
        Write-Host "Ending pre-existing active session on $mCode ($sId)..." -NoNewline
        $endHeaders = $headers.Clone()
        $endHeaders['Idempotency-Key'] = [Guid]::NewGuid().ToString()
        try {
            Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/machines/$mId/production-sessions/$sId/end" `
                -Method Post -Headers $endHeaders -ContentType 'application/json' -Body '{}' | Out-Null
            Write-Host " OK" -ForegroundColor Green
        } catch {
            Write-Host " (Ignored: $($_.Exception.Message))" -ForegroundColor Yellow
        }
    }
}

# 3. Derive valid test contexts dynamically from database for all active machines
$contextQuery = @"
SELECT 
    m.id::text AS machine_id,
    m.code AS machine_code,
    rs.active_variant_id::text AS variant_id,
    v.code AS variant_code,
    pg.id::text AS product_group_id,
    p.id::text AS main_part_id,
    p.part_code,
    rs.active_layout_id::text AS layout_id,
    l.code AS layout_code
FROM master.machines m
JOIN ops.machine_runtime_state rs ON rs.machine_id = m.id
JOIN master.variants v ON v.id = rs.active_variant_id AND v.machine_id = m.id AND v.is_active
JOIN master.product_groups pg ON pg.variant_id = v.id AND pg.is_active
JOIN master.parts p ON p.id = pg.main_part_id AND p.is_active
JOIN master.layouts l ON l.id = rs.active_layout_id AND l.machine_id = m.id AND l.status = 'PUBLISHED'
WHERE m.is_active
ORDER BY m.code;
"@

$contextCsv = @(& $psql -h $HostName -p $Port -U $User -d $Database -A -F ',' -t -c $contextQuery) |
    Where-Object { $_ -and -not ($_ -match '\([0-9]+ sat') }

$expectedMachines = @(
    '412B',
    'GAR',
    'PAH-01-A',
    'PAH-02-A',
    'SEALER',
    'SH-01-A',
    'SH-01-B',
    'SH-01-P1--SH-01-P2',
    'SH-02-P1 -SH-02-P2'
)

$testedMachines = @{}
$results = [ordered]@{}

Write-Host "`nTesting production start/end lifecycle across all 9 machines:`n"

foreach ($line in $contextCsv) {
    $cols = $line.Trim().Split(',')
    if ($cols.Length -lt 9) { continue }
    $mId = $cols[0]
    $mCode = $cols[1]
    $vId = $cols[2]
    $vCode = $cols[3]
    $pgId = $cols[4]
    $partId = $cols[5]
    $partCode = $cols[6]

    if ($testedMachines.ContainsKey($mCode)) { continue }
    $testedMachines[$mCode] = $true

    Write-Host ("- {0,-20} (Variant: {1}, Part: {2})..." -f $mCode, $vCode, $partCode) -NoNewline

    $startBody = @{
        variantId = $vId
        productGroupId = $pgId
        mainPartId = $partId
    } | ConvertTo-Json

    $startHeaders = $headers.Clone()
    $startHeaders['Idempotency-Key'] = [Guid]::NewGuid().ToString()

    try {
        $startRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/machines/$mId/production-sessions" `
            -Method Post -Headers $startHeaders -ContentType 'application/json' -Body $startBody

        $sessionId = $startRes.session.id
        if (-not $sessionId) {
            Write-Host " FAIL (No session ID returned)" -ForegroundColor Red
            $results[$mCode] = "FAIL"
            continue
        }

        # Verify active session
        $activeRes = Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/machines/$mId/production-sessions/active" `
            -Method Get -Headers $headers
        if ($activeRes.id -ne $sessionId) {
            Write-Host " FAIL (Active session ID mismatch)" -ForegroundColor Red
            $results[$mCode] = "FAIL"
            continue
        }

        # Cleanly end session
        $endHeaders = $headers.Clone()
        $endHeaders['Idempotency-Key'] = [Guid]::NewGuid().ToString()
        Invoke-RestMethod -Uri "$ApiBaseUrl/api/v1/machines/$mId/production-sessions/$sessionId/end" `
            -Method Post -Headers $endHeaders -ContentType 'application/json' -Body '{}' | Out-Null

        Write-Host " PASS" -ForegroundColor Green
        $results[$mCode] = "PASS"
    }
    catch {
        $msg = $_.Exception.Message
        Write-Host " FAIL ($msg)" -ForegroundColor Red
        $results[$mCode] = "FAIL ($msg)"
    }
}

Write-Host "`n--------------------------------------------------" -ForegroundColor Cyan
Write-Host "9-MACHINE ACCEPTANCE SUMMARY:" -ForegroundColor Cyan
$passedCount = 0
foreach ($m in $expectedMachines) {
    $res = $results[$m] ?? 'NOT_TESTED'
    if ($res -eq 'PASS') { $passedCount++ }
    Write-Host ("  {0,-22} : {1}" -f $m, $res)
}
Write-Host "--------------------------------------------------" -ForegroundColor Cyan

if ($passedCount -eq 9) {
    Write-Host "`nRESULT: PASS (9/9 Machines verified)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nRESULT: FAIL ($passedCount/9 Machines passed)" -ForegroundColor Red
    exit 1
}
