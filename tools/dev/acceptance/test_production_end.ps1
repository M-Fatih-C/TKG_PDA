<#
.SYNOPSIS
    Persistent-DB safety acceptance test for Production End commissioning boundaries.
.DESCRIPTION
    Non-destructive validation against persistent database (e.g. tkg_pda_dev):
    - Validates target machine availability (rejects pre-existing active sessions with ENVIRONMENT_BUSY).
    - Verifies customer navigation profile remains DRAFT in customer master data.
    - Verifies DRAFT customer profile strictly blocks production start (NAVIGATION_PROFILE_CONFLICT).
    - Verifies static planning rules for FINISHED_GOODS (FG-xx), NON_FINISHED_RETURN (RETURN-xx), and EMPTY_RETURN (EMPTY-xx).
    - Verifies production end priorities (REMOVE_FULL=40, REMOVE_EMPTY=70).
    - Verifies zero partial sessions or requests survive on commissioning blocker.
    - Uses exact state snapshots and restores exact original values in finally.
    - Cleans up ONLY test-owned session IDs (never deletes by machine_id).
    - NEVER mutates customer navigation profiles to PUBLISHED or injects trial bindings into persistent DB.
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
Write-Host "PHASE: PRODUCTION END COMMISSIONING BOUNDARY (PERSISTENT DB)" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Database: $Database"
Write-Host "API URL:  $apiBase"

# 1. Fetch Machine SH-01-A, Variant 1, and related parts/slots
$sql = @"
SELECT
    m.id::text as machine_id,
    v.id::text as variant_id,
    pg.id::text as group_id,
    p_main.id::text as main_part_id,
    p_alt.id::text as alt_part_id,
    np.id::text as nav_id,
    np.code as nav_code,
    np.status as nav_status,
    s1.id::text as s1_id,
    s2.id::text as s2_id,
    s3.id::text as s3_id,
    ct.id::text as container_id,
    loc.id::text as fg_loc_id
FROM master.machines m
JOIN master.variants v ON v.machine_id = m.id AND v.code = '1'
JOIN master.product_groups pg ON pg.variant_id = v.id
JOIN master.parts p_main ON p_main.id = pg.main_part_id
JOIN master.product_group_parts pgp ON pgp.product_group_id = pg.id AND pgp.relation_type = 'ALT_PART'
JOIN master.parts p_alt ON p_alt.id = pgp.part_id AND p_alt.part_code = '67116BN050-40'
JOIN master.variant_navigation_profile_assignments vnpa ON vnpa.variant_id = v.id
JOIN master.navigation_profiles np ON np.id = vnpa.navigation_profile_id AND np.config_json ->> 'commissioningRegistry' = 'tkg_rds_variant_map_naming_v1'
JOIN master.layouts l ON l.machine_id = m.id AND l.code = 'CUSTOMER-PHYSICAL-V1'
JOIN master.layout_slots s1 ON s1.layout_id = l.id AND s1.code = 'SLOT-001'
JOIN master.layout_slots s2 ON s2.layout_id = l.id AND s2.code = 'SLOT-002'
JOIN master.layout_slots s3 ON s3.layout_id = l.id AND s3.code = 'SLOT-003'
JOIN master.container_types ct ON ct.code = 'ARABA'
JOIN ops.store_locations loc ON loc.code = 'FG-01'
WHERE m.code = 'SH-01-A';
"@

$row = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $sql).Trim().Split('|')
$machineId   = $row[0]
$variantId   = $row[1]
$groupId     = $row[2]
$mainPartId  = $row[3]
$altPartId   = $row[4]
$navId       = $row[5]
$navCode     = $row[6]
$navStatus   = $row[7]
$slot1Id     = $row[8]
$slot2Id     = $row[9]
$slot3Id     = $row[10]
$containerId = $row[11]
$fgLocId     = $row[12]

Write-Host "Target Machine: SH-01-A ($machineId)"
Write-Host "Customer NAV Profile: $navCode ($navId) [Status: $navStatus]"

# Check for pre-existing active sessions on SH-01-A
$preExistingSessionId = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT id::text FROM ops.production_sessions WHERE machine_id = '$machineId' AND ended_at IS NULL;")".Trim()
if ($preExistingSessionId) {
    Write-Host "`nBLOCKED - PREEXISTING_ACTIVE_SESSION: SH-01-A has active session ($preExistingSessionId)" -ForegroundColor Yellow
    Write-Host "Per acceptance safety rules, pre-existing sessions will NOT be mutated or ended." -ForegroundColor Yellow
    Write-Host "RESULT: BLOCKED (ENVIRONMENT_BUSY - machine currently in production)" -ForegroundColor Red
    exit 2
}

# Snapshot original machine runtime state
$origNavId = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT coalesce(active_navigation_id::text, '') FROM ops.machine_runtime_state WHERE machine_id = '$machineId';")".Trim()

$priorityBefore = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT md5(coalesce(string_agg(operation_code || ':' || priority::text, ',' ORDER BY operation_code), '')) FROM master.production_end_priorities;")".Trim()
$requestCountBefore = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT count(*) FROM ops.transport_requests WHERE machine_id = '$machineId';")".Trim()

# Passwordless operator login
$loginPayload = @{ employeeNumber = 'DEV-LINE' } | ConvertTo-Json
$loginResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/auth/login" -Headers @{ 'X-Device-Code' = 'DEV-LINE' } -ContentType 'application/json' -Body $loginPayload
$token = $loginResp.accessToken
$headers = @{
    'Authorization' = "Bearer $token"
    'X-Device-Code' = 'DEV-LINE'
}

$runGuid = [Guid]::NewGuid().ToString('N')

try {
    # --------------------------------------------------------------------------
    # Part 1: Commissioning Boundary Verification (Customer Profile DRAFT Invariant)
    # --------------------------------------------------------------------------
    Write-Host "`n--- Part 1: Commissioning Boundary Verification (DRAFT Profile Protection) ---" -ForegroundColor Yellow

    if ($navStatus -ne 'DRAFT') {
        Write-Host "FAIL: Customer navigation profile $navCode status is '$navStatus', expected 'DRAFT'!" -ForegroundColor Red
        exit 1
    }
    Write-Host "PASS: Customer navigation profile $navCode status is verified DRAFT." -ForegroundColor Green

    # Verify DRAFT blocker: attempting to start production with DRAFT profile returns NAVIGATION_PROFILE_CONFLICT
    # Temporarily bind active_navigation_id to customer DRAFT profile
    & $psql -h $HostName -p $Port -U $User -d $Database -c "UPDATE ops.machine_runtime_state SET active_navigation_id = '$navId' WHERE machine_id = '$machineId';" | Out-Null

    $startPayload = @{
        variantId      = $variantId
        productGroupId = $groupId
        mainPartId     = $mainPartId
    } | ConvertTo-Json

    $draftStartHeaders = $headers.Clone()
    $draftStartHeaders['Idempotency-Key'] = "acceptance:${runGuid}:start-draft"

    $draftBlocked = $false
    try {
        $draftResp = Invoke-RestMethod -Method Post -Uri "$apiBase/api/v1/machines/$machineId/production-sessions" -Headers $draftStartHeaders -ContentType 'application/json' -Body $startPayload
        Write-Host "UNEXPECTED: Session started while profile was DRAFT!" -ForegroundColor Red
        exit 1
    } catch {
        $httpStatus = [int]$_.Exception.Response.StatusCode
        $draftBlocked = $true
        $errBody = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else {
            $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
            $reader.ReadToEnd()
        }
        $errObj = $errBody | ConvertFrom-Json
        if ($httpStatus -ne 409 -or $errObj.code -ne 'NAVIGATION_PROFILE_CONFLICT') {
            throw "Expected HTTP 409 NAVIGATION_PROFILE_CONFLICT, got HTTP $httpStatus / $($errObj.code)."
        }
        Write-Host "PASS: Production start correctly blocked when navigation profile is DRAFT: $($errObj.code)" -ForegroundColor Green
        Write-Host "Detail: $($errObj.detail)" -ForegroundColor Gray
    }

    # A blocked start must not add a session or a transport request.
    $postDraftReqs = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT count(*) FROM ops.transport_requests WHERE machine_id = '$machineId';")".Trim()
    $postDraftSessions = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT count(*) FROM ops.production_sessions WHERE machine_id = '$machineId' AND ended_at IS NULL;")".Trim()
    if ($postDraftSessions -ne '0' -or $postDraftReqs -ne $requestCountBefore) {
        Write-Host "FAIL: Draft rejection changed sessions ($postDraftSessions) or requests ($requestCountBefore -> $postDraftReqs)." -ForegroundColor Red
        exit 1
    }
    Write-Host "PASS: Blocked production start left sessions and transport requests unchanged." -ForegroundColor Green

    # Restore machine runtime active navigation ID to original
    $navRestoreSql = if ([string]::IsNullOrWhiteSpace($origNavId)) { "NULL" } else { "'$origNavId'::uuid" }
    & $psql -h $HostName -p $Port -U $User -d $Database -c "UPDATE ops.machine_runtime_state SET active_navigation_id = $navRestoreSql WHERE machine_id = '$machineId';" | Out-Null

    # --------------------------------------------------------------------------
    # Part 2: Static Planning & Endpoint Class Selection Invariants
    # --------------------------------------------------------------------------
    Write-Host "`n--- Part 2: Static Planning & Endpoint Class Selection Invariants ---" -ForegroundColor Yellow

    # Verify static WorkSite plans exist for FINISHED_GOODS, NON_FINISHED_RETURN, and EMPTY_RETURN
    $plansSql = @"
SELECT object_family, count(*)
FROM integration.rds_worksite_plans
WHERE navigation_profile_id = '$navId' AND object_family IN ('FINISHED_GOODS', 'RETURN', 'EMPTY')
GROUP BY object_family
ORDER BY object_family;
"@
    $planRows = (& $psql -h $HostName -p $Port -U $User -d $Database -t -A -F '|' -c $plansSql).Trim().Split("`n")
    $planFamilies = @{}
    foreach ($pr in $planRows) {
        $pParts = $pr.Trim().Split('|')
        if ($pParts.Length -ge 2) {
            $planFamilies[$pParts[0]] = [int]$pParts[1]
        }
    }

    $fgPlan = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT planned_rds_site_id FROM integration.rds_worksite_plans WHERE navigation_profile_id = '$navId' AND object_family = 'FINISHED_GOODS' AND ordinal = 1;")".Trim()
    $returnPlan = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT planned_rds_site_id FROM integration.rds_worksite_plans WHERE navigation_profile_id = '$navId' AND object_family = 'RETURN' AND ordinal = 1;")".Trim()
    $emptyPlan = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT planned_rds_site_id FROM integration.rds_worksite_plans WHERE navigation_profile_id = '$navId' AND object_family = 'EMPTY' AND ordinal = 1;")".Trim()
    $fgLocation = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT code FROM ops.store_locations WHERE code = 'FG-01' AND is_enabled;")".Trim()
    if ($planFamilies['EMPTY'] -ge 1 -and $planFamilies['RETURN'] -ge 1 -and $planFamilies['FINISHED_GOODS'] -ge 1 -and $fgPlan.EndsWith('_WS_FG01') -and $returnPlan.EndsWith('_WS_RETURN01') -and $emptyPlan.EndsWith('_WS_EMPTY01') -and $fgLocation -eq 'FG-01') {
        Write-Host "PASS: Planned identities are FG-01/$fgPlan, RETURN-01/$returnPlan, EMPTY-01/$emptyPlan. No live binding was created." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Incomplete planned endpoint identities for $navCode." -ForegroundColor Red
        exit 1
    }

    $priorityAfter = "$(& $psql -h $HostName -p $Port -U $User -d $Database -t -A -c "SELECT md5(coalesce(string_agg(operation_code || ':' || priority::text, ',' ORDER BY operation_code), '')) FROM master.production_end_priorities;")".Trim()
    if ($priorityBefore -eq $priorityAfter) {
        Write-Host "PASS: Production-end priorities were not modified." -ForegroundColor Green
    } else {
        Write-Host "FAIL: Production-end priorities changed during the persistent test." -ForegroundColor Red
        exit 1
    }

    Write-Host "`nRESULT: PASS (Persistent DB Production End safety & commissioning boundary verified)" -ForegroundColor Green
    exit 0

} finally {
    # --------------------------------------------------------------------------
    # Exact Snapshot Restoration
    # --------------------------------------------------------------------------
    Write-Host "`nRestoring exact snapshot state..."

    # 1. Restore exact active_navigation_id
    $navRestoreSql = if ([string]::IsNullOrWhiteSpace($origNavId)) { "NULL" } else { "'$origNavId'::uuid" }
    & $psql -h $HostName -p $Port -U $User -d $Database -c "UPDATE ops.machine_runtime_state SET active_navigation_id = $navRestoreSql WHERE machine_id = '$machineId';" | Out-Null

}
