<#
.SYNOPSIS
    Reports static RDS WorkSite plans and dynamic live WorkSite mirrors.
.DESCRIPTION
    Validates the 1775 fixed worksite plans breakdown and provides a real-time
    SQL breakdown of live mirrored worksites, explaining the dynamic
    operational transit nodes (MAP_SWITCH_AREA).
#>
[CmdletBinding()]
param(
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
if (-not $psql) {
    throw "PostgreSQL client (psql.exe) could not be located."
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "TKG PDA WORKSITE AUDIT & REPORT" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Fixed RDS WorkSite Plans (1775 Expected Baseline)
$planQuery = @"
SELECT 
    object_family, 
    count(*)::text
FROM integration.rds_worksite_plans 
GROUP BY object_family 
ORDER BY count(*) DESC;
"@

$planCsv = @(& $psql -h $HostName -p $Port -U $User -d $Database -A -F ',' -t -c $planQuery)
$planRows = $planCsv | Where-Object { $_ -and -not ($_ -match '\([0-9]+ sat') }

$planTotal = 0
$planBreakdown = [ordered]@{}
foreach ($line in $planRows) {
    $parts = $line.Split(',')
    if ($parts.Length -ge 2) {
        $family = $parts[0]
        $count = [int]$parts[1]
        $planBreakdown[$family] = $count
        $planTotal += $count
    }
}

Write-Host "`n1. STATIC RDS WORKSITE PLANS (Baseline: 1775)" -ForegroundColor Yellow
Write-Host "   Total Planned Sites: $planTotal"
foreach ($k in $planBreakdown.Keys) {
    Write-Host ("   - {0,-20} : {1,5}" -f $k, $planBreakdown[$k])
}

$planPass = ($planTotal -eq 1775)
if ($planPass) {
    Write-Host "   -> PASS: Fixed plan count equals 1775 exactly." -ForegroundColor Green
} else {
    Write-Host "   -> FAIL: Expected 1775 planned worksites, found $planTotal" -ForegroundColor Red
}

# 2. Dynamic Live Mirrored Worksites (integration.rds_worksites)
$liveProfileQuery = @"
SELECT 
    coalesce(np.name, 'GLOBAL / UNASSIGNED'),
    count(w.id)::text
FROM integration.rds_worksites w
LEFT JOIN master.navigation_profiles np ON np.id = w.navigation_profile_id
GROUP BY 1
ORDER BY count(w.id) DESC;
"@

$liveCsv = @(& $psql -h $HostName -p $Port -U $User -d $Database -A -F ',' -t -c $liveProfileQuery)
$liveRows = $liveCsv | Where-Object { $_ -and -not ($_ -match '\([0-9]+ sat') }

$liveTotal = 0
$liveProfileBreakdown = [ordered]@{}
foreach ($line in $liveRows) {
    $parts = $line.Split(',')
    if ($parts.Length -ge 2) {
        $prof = $parts[0]
        $count = [int]$parts[1]
        $liveProfileBreakdown[$prof] = $count
        $liveTotal += $count
    }
}

Write-Host "`n2. DYNAMIC LIVE MIRRORED WORKSITES (integration.rds_worksites)" -ForegroundColor Yellow
Write-Host "   Total Live Mirrored Sites: $liveTotal"
foreach ($k in $liveProfileBreakdown.Keys) {
    Write-Host ("   - {0,-45} : {1,5}" -f $k, $liveProfileBreakdown[$k])
}

# 3. Dynamic Transit Nodes Breakdown (MAP_SWITCH_AREA)
$transitQuery = @"
SELECT 
    count(DISTINCT w.navigation_profile_id)::text,
    count(*)::text
FROM integration.rds_worksites w
WHERE w.group_name = 'MAP_SWITCH_AREA';
"@
$transitCsv = @(& $psql -h $HostName -p $Port -U $User -d $Database -A -F ',' -t -c $transitQuery)
$transitRows = @($transitCsv | Where-Object { $_ -and -not ($_ -match '\([0-9]+ sat') })
$transitParts = $transitRows[0].Trim().Split(',')
$transitProfiles = [int]$transitParts[0]
$transitNodes = [int]$transitParts[1]

Write-Host "`n3. DYNAMIC TRANSIT DISCOVERY (MAP_SWITCH_AREA)" -ForegroundColor Yellow
Write-Host "   - Navigation Profiles with Map Switch: $transitProfiles (observed mirrored profiles)"
Write-Host "   - Transit Points Total Count:          $transitNodes (observed; no fixed equality invariant)"

Write-Host "`n--------------------------------------------------" -ForegroundColor Cyan
Write-Host "WORKSITE RECONCILIATION (observed):" -ForegroundColor Cyan
Write-Host "  Static plans: $planTotal | Mirrored: $liveTotal | Map-switch transit: $transitNodes"
Write-Host "  Mirror minus static-plan count: $($liveTotal - $planTotal) (dynamic; not an equality invariant)"

if ($planPass) {
    Write-Host "`nRESULT: PASS (Plans: 1775, Live: $liveTotal)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nRESULT: FAIL" -ForegroundColor Red
    exit 1
}
