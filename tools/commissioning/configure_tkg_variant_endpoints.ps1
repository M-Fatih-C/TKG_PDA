param(
    [Parameter(Mandatory = $true)][string]$Machine,
    [Parameter(Mandatory = $true)][string]$Variant,
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password,
    [Parameter(Mandatory = $true)][ValidateSet('PREPARATION', 'EMPTY_SOURCE', 'EMPTY_RETURN', 'FINISHED_GOODS', 'NON_FINISHED_RETURN')][string]$EndpointClass,
    [Parameter(Mandatory = $true)][string]$BusinessCode,
    [Parameter(Mandatory = $true)][string]$PlannedWorkSite,
    [switch]$Apply
)

. "$PSScriptRoot\_tkg_commissioning.ps1"
$session = Get-TkgAdminSession -BaseUrl $BaseUrl -Username $Username -Password $Password
$body = @{
    machineCode = $Machine
    variantCode = $Variant
    endpointClass = $EndpointClass
    businessCode = $BusinessCode
    plannedWorkSiteName = $PlannedWorkSite
    apply = [bool]$Apply
} | ConvertTo-Json -Compress
$decision = Invoke-RestMethod -Method Post -Uri "$($session.Root)/api/v1/admin/rds/commissioning/shared-endpoints" -Headers $session.Headers -ContentType 'application/json' -Body $body
$mode = if ($Apply) { 'apply' } else { 'dry-run' }
Write-Output "$mode $EndpointClass $BusinessCode -> $PlannedWorkSite"
Write-Output "applied $($decision.applied)"
Write-Output "resolved $($decision.resolvedSiteId)"
if ($decision.blockers) {
    $decision.blockers | ForEach-Object { Write-Output "blocker $_" }
}
if (-not $Apply) {
    Write-Output "No row was written. Pass -Apply after the exact WorkSite has been discovered on this NAV."
}
