param(
    [Parameter(Mandatory = $true)][string]$Machine,
    [Parameter(Mandatory = $true)][string]$Variant,
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password
)

. "$PSScriptRoot\_tkg_commissioning.ps1"
$session = Get-TkgAdminSession -BaseUrl $BaseUrl -Username $Username -Password $Password
$row = Get-TkgCommissioningRow -Report (Get-TkgCommissioningReport -Session $session) -Machine $Machine -Variant $Variant
$syncBody = @{ navigationProfileId = $row.navigationProfileId } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri "$($session.Root)/api/v1/admin/rds/worksites/sync" -Headers $session.Headers -ContentType 'application/json' -Body $syncBody | Out-Null
$diff = Invoke-RestMethod -Method Get -Headers $session.Headers -Uri "$($session.Root)/api/v1/admin/rds/commissioning/worksite-diff?machineCode=$([uri]::EscapeDataString($Machine))&variantCode=$([uri]::EscapeDataString($Variant))"

Write-Output "NAV $($row.navigationCode)"
Write-Output "planned $($diff.plannedCount)"
Write-Output "actual $($diff.actualCount)"
Write-Output "exactMatches $($diff.exactMatchCount)"
Write-Output "missing $($diff.missingNames.Count)"
if ($diff.missingNames) { $diff.missingNames | ForEach-Object { Write-Output "  missing $_" } }
Write-Output "unexpected $($diff.unexpectedNames.Count)"
if ($diff.unexpectedNames) { $diff.unexpectedNames | ForEach-Object { Write-Output "  unexpected $_" } }
Write-Output "mappedState $($diff.mappedStateCount)"
Write-Output "commissioning was not changed"
