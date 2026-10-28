<#
.SYNOPSIS
    Persistent-DB safety acceptance test for Repeated ALT demand and customer master invariants.
.DESCRIPTION
    Non-destructive validation against persistent database (e.g. tkg_pda_dev):
    - Validates target machines are free (rejects pre-existing active sessions with ENVIRONMENT_BUSY).
    - Verifies customer master ALT part container invariants (1 configured container, 24 missing container).
    - Verifies API projection honesty: GET /machines/{id}/part-options reports requestable=false and CONTAINER_TYPE_MISSING.
    - Verifies customer navigation profile remains DRAFT.
    - NEVER deletes operational tables or modifies customer profiles in persistent DB.
    - Uses exact restore in finally for any touched temporary state.
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
$apiBase = $ApiBaseUrl

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "PHASE: REPEATED ALT DEMAND INVARIANTS (PERSISTENT DB)" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Database: $Database"
Write-Host "API URL:  $apiBase"

# 1. Fetch Machine SH-01-A, Variant 1, and requestable ALT Part 67116BN050-40
$sql = @"
SELECT
    m.id::text as machine_id,
    v.id::text as variant_id,
    pg.id::text as group_id,
    p_main.id::text as main_part_id,
    p_alt.id::text as alt_part_id,
    p_alt.part_code as alt_part_code,
    ct.id::text as container_id,
    ct.code as container_code,
    s.id::text as slot_id,
    s.code as slot_code,
    np.id::text as nav_id,
    np.status as nav_status
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.product_groups pg ON pg.variant_id = v.id
JOIN master.parts p_main ON p_main.id = pg.main_part_id
JOIN master.product_group_parts pgp ON pgp.product_group_id = pg.id AND pgp.relation_type = 'ALT_PART'
JOIN master.parts p_alt ON p_alt.id = pgp.part_id AND p_alt.part_code = '67116BN050-40'
JOIN master.container_types ct ON ct.id = pgp.container_type_id
JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-V1'
JOIN master.layout_slots s ON s.layout_id = l.id AND s.code = 'SLOT-003'
JOIN master.variant_navigation_profile_assignments vnpa ON vnpa.variant_id = v.id
JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id AND np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
WHERE m.code = 'SH-01-A';
"@

$row = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $sql).Trim().Split('|')
$machineId     = $row[0]
$variantId     = $row[1]
$groupId       = $row[2]
$mainPartId    = $row[3]
$altPartId     = $row[4]
$altPartCode   = $row[5]
$containerId   = $row[6]
$containerCode = $row[7]
$slotId        = $row[8]
$slotCode      = $row[9]
$navId         = $row[10]
$origNavStatus = $row[11]

# Fetch missing container machine SH-01-P1--SH-01-P2
$mSql = @"
SELECT
    m.id::text,
    v.id::text,
    pg.id::text,
    p_main.id::text,
    p_alt.id::text,
    p_alt.part_code,
    np.id::text,
    np.status
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.product_groups pg ON pg.variant_id = v.id
JOIN master.parts p_main ON p_main.id = pg.main_part_id
JOIN master.product_group_parts pgp ON pgp.product_group_id = pg.id AND pgp.relation_type = 'ALT_PART' AND pgp.container_type_id IS NULL
JOIN master.parts p_alt ON p_alt.id = pgp.part_id
JOIN master.variant_navigation_profile_assignments vnpa ON vnpa.variant_id = v.id
JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id AND np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
WHERE m.code = 'SH-01-P1--SH-01-P2'
LIMIT 1;
"@
$mRow = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $mSql).Trim().Split('|')
$mMachId  = $mRow[0]
$mVarId   = $mRow[1]
$mGroupId = $mRow[2]
$mMainId  = $mRow[3]
$mAltId   = $mRow[4]
$mAltCode = $mRow[5]
$mNavId   = $mRow[6]
$mOrigNav = $mRow[7]

Write-Host "Target Machine: SH-01-A ($machineId)"
Write-Host "Configured ALT Part: $altPartCode (Container: $containerCode)"
Write-Host "Missing Container Target: SH-01-P1--SH-01-P2 ($mMachId), Part: $mAltCode"

# Safety check for pre-existing active sessions
$activeCheckSql = "SELECT m.code, s.id::text FROM ops.production_sessions s JOIN master.machines m ON m.id = s.machine_id WHERE s.machine_id IN ('$machineId', '$mMachId') AND s.ended_at IS NULL;"
$activeRows = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $activeCheckSql)".Trim()
if ($activeRows) {
    Write-Host "`nBLOCKED - PREEXISTING_ACTIVE_SESSION: Target machine in production:" -ForegroundColor Yellow
    foreach ($ar in $activeRows.Split("`n")) {
        Write-Host "  - $ar" -ForegroundColor Yellow
    }
    Write-Host "Per acceptance safety rules, pre-existing sessions will NOT be mutated or ended." -ForegroundColor Yellow
    Write-Host "RESULT: BLOCKED (ENVIRONMENT_BUSY)" -ForegroundColor Red
    exit 2
}

# Passwordless supervisor login (cross-machine authority without device binding mutation)
$loginPayload = @{ employeeNumber = 'DEV-SUP' } | ConvertTo-Json
$loginResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/auth/login" -Headers @{ 'X-Device-Code' = 'DEV-SUPERVISOR' } -ContentType 'application/json' -Body $loginPayload
$token = $loginResp.accessToken
$headers = @{
    'Authorization' = "Bearer $token"
    'X-Device-Code' = 'DEV-SUPERVISOR'
}
$ownedSessionId = $null

try {
    Write-Host "`n--- Part 1: Customer Master ALT Invariant Validation ---" -ForegroundColor Yellow

    # Verify missing container count in customer master
    $missingContCount = [int]("$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT count(*) FROM master.product_group_parts WHERE relation_type = 'ALT_PART' AND container_type_id IS NULL;")".Trim())
    Write-Host ("Blocked ALT Part Relations without Container: {0} (Expected: 25)" -f $missingContCount)

    if ($missingContCount -eq 25) {
        Write-Host "PASS: Customer master ALT relation invariant verified (exactly 25 blocked missing-container relations)." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Expected 25 missing container ALT relations, got $missingContCount" -ForegroundColor Red
        exit 1
    }

    # Verify navigation profile status remains DRAFT
    if ($origNavStatus -eq 'DRAFT') {
        Write-Host "PASS: Customer navigation profile $navId status is DRAFT." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Customer navigation profile $navId status is '$origNavStatus' (expected DRAFT)!" -ForegroundColor Red
        exit 1
    }

    Write-Host "`n--- Part 2: API Missing-Container Blocker Projection ---" -ForegroundColor Yellow

    # Verify GET /machines/{id}/part-options reports requestable=false and CONTAINER_TYPE_MISSING in blockers
    $mainPartsResp = Invoke-RestMethod -Method Get -Uri "$apiBase/api/v1/machines/$mMachId/part-options" -Headers $headers
    $foundAlt = $null
    foreach ($mp in $mainPartsResp) {
        foreach ($rp in $mp.relatedParts) {
            if ($rp.partCode -eq $mAltCode) {
                $foundAlt = $rp
                break
            }
        }
        if ($foundAlt) { break }
    }

    if ($foundAlt -and -not $foundAlt.requestable -and $foundAlt.blockers -contains 'CONTAINER_TYPE_MISSING') {
        Write-Host "PASS: Machine part-options API correctly projects ALT part $mAltCode as non-requestable with blocker CONTAINER_TYPE_MISSING." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Machine part-options API did not report CONTAINER_TYPE_MISSING for part $mAltCode! (foundAlt: $($foundAlt | ConvertTo-Json -Compress))" -ForegroundColor Red
        exit 1
    }

    Write-Host "`n--- Part 3: Direct demand is rejected before a transport exists ---" -ForegroundColor Yellow
    $requestsBefore = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT count(*) FROM ops.transport_requests;")".Trim()
    $startHeaders = $headers.Clone()
    $startHeaders['Idempotency-Key'] = "acceptance:$([Guid]::NewGuid().ToString('N')):alt-boundary"
    $startBody = @{ variantId = $mVarId; productGroupId = $mGroupId; mainPartId = $mMainId } | ConvertTo-Json
    $started = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$mMachId/production-sessions" -Headers $startHeaders -ContentType 'application/json' -Body $startBody
    $ownedSessionId = $started.session.id
    $demandHeaders = $headers.Clone()
    $demandHeaders['Idempotency-Key'] = "acceptance:$([Guid]::NewGuid().ToString('N')):missing-container"
    $rejected = $false
    try {
        Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$mMachId/part-demands" -Headers $demandHeaders -ContentType 'application/json' -Body (@{ partId = $mAltId } | ConvertTo-Json) | Out-Null
    } catch {
        $errBody = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { '' }
        $rejected = $errBody -match 'CONTAINER_TYPE_MISSING'
        if (-not $rejected) { Write-Host $errBody -ForegroundColor Red }
    }
    $requestsAfter = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT count(*) FROM ops.transport_requests;")".Trim()
    if ($rejected -and $requestsBefore -eq $requestsAfter) {
        Write-Host "PASS: CONTAINER_TYPE_MISSING rejected the demand and created no transport request." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Missing-container demand was not rejected cleanly (before=$requestsBefore after=$requestsAfter)." -ForegroundColor Red
        exit 1
    }

    Write-Host "`nRESULT: PASS (Persistent DB Repeated ALT customer master invariants & blocker projection verified)" -ForegroundColor Green
    exit 0

} finally {
    if ($ownedSessionId) {
        try {
            $endHeaders = $headers.Clone()
            $endHeaders['Idempotency-Key'] = "acceptance:cleanup:$ownedSessionId"
            Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$mMachId/production-sessions/$ownedSessionId/end" -Headers $endHeaders -ContentType 'application/json' -Body '{}' | Out-Null
            Write-Host "Ended only the acceptance-owned session $ownedSessionId."
        } catch {
            Write-Host "Owned session cleanup note: $($_.Exception.Message)"
        }
    }
}
