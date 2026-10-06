param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password
)

. "$PSScriptRoot\_tkg_commissioning.ps1"
$rows = Get-TkgCommissioningReport -Session (Get-TkgAdminSession -BaseUrl $BaseUrl -Username $Username -Password $Password)
Write-Output "variants $($rows.Count)"
foreach ($row in $rows) {
    $ready = if ($row.ready) { 'ready' } else { 'not ready' }
    $layout = if ($row.layoutBindingState) { $row.layoutBindingState } else { '-' }
    Write-Output ("{0} V{1} area={2} map={3} nav={4} status={5} state={6} geometryVerified={7} planned={8} mapped={9} verifiedBindings={10} missingBindings={11} default={12} layout={13} {14}" -f `
        $row.machineCode, $row.variantCode, $row.areaName, $row.mapName, $row.navigationCode, $row.profileStatus, $row.commissioningState, $row.geometryVerified, $row.plannedWorkSites, $row.mappedWorkSites, $row.verifiedBindings, $row.missingBindings, $row.isDefault, $layout, $ready)
}
$sealer = $rows | Where-Object { $_.machineCode -eq 'SEALER' -and $_.variantCode -eq '3' } | Select-Object -First 1
if ($sealer) {
    Write-Output "SEALER V3 $($sealer.layoutBindingState)"
}
