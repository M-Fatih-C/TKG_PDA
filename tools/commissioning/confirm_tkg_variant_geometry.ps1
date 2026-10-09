param(
    [Parameter(Mandatory = $true)][string]$Machine,
    [Parameter(Mandatory = $true)][string]$Variant,
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password,
    [switch]$Apply
)

. "$PSScriptRoot\_tkg_commissioning.ps1"
$session = Get-TkgAdminSession -BaseUrl $BaseUrl -Username $Username -Password $Password
$body = @{ machineCode = $Machine; variantCode = $Variant; apply = [bool]$Apply } | ConvertTo-Json -Compress
$decision = Invoke-RestMethod -Method Post -Uri "$($session.Root)/api/v1/admin/rds/commissioning/confirm-geometry" -Headers $session.Headers -ContentType 'application/json' -Body $body
Write-Output "machine $($decision.machineCode)"
Write-Output "variant $($decision.variantCode)"
Write-Output "NAV $($decision.navigationCode)"
Write-Output "required $($decision.requiredPlanCount)"
Write-Output "confirmed $($decision.confirmedCount)"
Write-Output "remaining $($decision.remainingCount)"
Write-Output "applied $($decision.applied)"
if (-not $Apply) { Write-Output "dry-run; pass -Apply to record field geometry" }
if ($decision.blockers -and $decision.blockers.Count -gt 0) {
    $decision.blockers | ForEach-Object { Write-Output "blocker $_" }
    exit 1
}
if ($Apply -and -not $decision.applied) { exit 1 }
