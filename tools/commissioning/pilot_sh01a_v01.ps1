param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password,
    [switch]$Live
)

. "$PSScriptRoot\_tkg_commissioning.ps1"
$session = Get-TkgAdminSession -BaseUrl $BaseUrl -Username $Username -Password $Password
$row = Get-TkgCommissioningRow -Report (Get-TkgCommissioningReport -Session $session) -Machine 'SH-01-A' -Variant '1'
$status = Invoke-RestMethod -Method Get -Uri "$($session.Root)/api/v1/admin/rds/status" -Headers $session.Headers
$failed = $false

function Write-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    if ($Passed) { Write-Output "PASS $Name $Detail" }
    else {
        Write-Output "FAIL $Name $Detail"
        $script:failed = $true
    }
}

Write-Output 'SH-01-A V1 pilot: dry run / readiness only'
Write-Check 'navigation' ($row.navigationCode -eq 'TKG_SH01A_V01_NAV') $row.navigationCode
Write-Check 'area' ($row.areaName -eq 'TKG_SH01A_V01') $row.areaName
Write-Check 'map' ($row.mapName -eq 'TKG_SH01A_V01_MAP') $row.mapName
Write-Check 'profile' $true "status=$($row.profileStatus) state=$($row.commissioningState) geometryVerified=$($row.geometryVerified)"
Write-Check 'bindings' ($row.missingBindings -eq 0 -and $row.layoutBindingState -eq 'SOURCE_BACKED') "missing=$($row.missingBindings) planned=$($row.plannedWorkSites) mapped=$($row.mappedWorkSites)"
Write-Check 'cross-profile' ($row.crossProfileMappings -eq 0) "$($row.crossProfileMappings)"
Write-Check 'cross-machine' ($row.crossMachineMappings -eq 0) "$($row.crossMachineMappings)"
Write-Check 'task-templates' ($row.activeTaskTemplates -gt 0) "$($row.activeTaskTemplates)"
Write-Check 'rds-read' ([bool]$status.enabled) "reachable=$($status.reachable) lastRead=$($status.lastSuccessfulRead) error=$($status.errorCode)"
$production = if ($row.productionSessionStatus) { $row.productionSessionStatus } else { 'none' }
$activeNav = if ($row.activeNavigationCode) { $row.activeNavigationCode } else { 'none' }
Write-Output "production $production"
Write-Output "activeNavigation $activeNav"
Write-Output 'no robot motion was requested'

if ($Live) {
    if (-not $status.dispatchEnabled) {
        Write-Output 'BLOCKED Rds:DispatchEnabled is closed. Live motion was not started.'
        exit 2
    }
    Write-Output 'Rds:DispatchEnabled is open. This pilot still does not create a transport or bypass the normal dispatch gate.'
}

if ($failed) { exit 1 }
Write-Output 'NOT READY for field motion until verification and promotion are explicit'
