param(
    [Parameter(Mandatory = $true)][int]$RemoveFullPriority,
    [Parameter(Mandatory = $true)][int]$RemoveEmptyPriority,
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password,
    [switch]$Apply
)

if ($RemoveFullPriority -lt 1 -or $RemoveEmptyPriority -lt 1) {
    Write-Output 'blocker INVALID_PRIORITY'
    Write-Output 'Production end priority must be 1 or greater. No value was saved.'
    exit 1
}

Write-Output "proposed REMOVE_FULL $RemoveFullPriority"
Write-Output "proposed REMOVE_EMPTY $RemoveEmptyPriority"
if (-not $Apply) {
    Write-Output 'dry-run; pass -Apply to store the customer values'
    exit 0
}

. "$PSScriptRoot\_tkg_commissioning.ps1"
$session = Get-TkgAdminSession -BaseUrl $BaseUrl -Username $Username -Password $Password
foreach ($change in @(
    @{ OperationCode = 'REMOVE_FULL'; Priority = $RemoveFullPriority },
    @{ OperationCode = 'REMOVE_EMPTY'; Priority = $RemoveEmptyPriority }
)) {
    $body = $change | ConvertTo-Json -Compress
    Invoke-RestMethod -Method Put -Uri "$($session.Root)/api/v1/admin/production-end-priorities" -Headers $session.Headers -ContentType 'application/json' -Body $body | Out-Null
    Write-Output "saved $($change.OperationCode) $($change.Priority)"
}
Write-Output 'audited PRODUCTION_END_PRIORITY_SET'
