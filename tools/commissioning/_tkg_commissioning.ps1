Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-TkgAdminSession {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$Username,
        [Parameter(Mandatory = $true)][string]$Password
    )

    $root = $BaseUrl.TrimEnd('/')
    $body = @{ username = $Username; password = $Password } | ConvertTo-Json -Compress
    $login = Invoke-RestMethod -Method Post -Uri "$root/api/v1/admin/auth/login" -ContentType 'application/json' -Body $body
    if (-not $login.accessToken) {
        throw 'Admin login did not return an access token.'
    }

    return @{
        Root = $root
        Headers = @{ Authorization = "Bearer $($login.accessToken)" }
    }
}

function Get-TkgCommissioningReport {
    param($Session)
    return @(Invoke-RestMethod -Method Get -Uri "$($Session.Root)/api/v1/admin/rds/commissioning/report" -Headers $Session.Headers)
}

function Get-TkgCommissioningRow {
    param($Report, [string]$Machine, [string]$Variant)
    $row = $Report | Where-Object { $_.machineCode -eq $Machine -and $_.variantCode -eq $Variant } | Select-Object -First 1
    if (-not $row) {
        throw "No commissioning profile is registered for $Machine variant $Variant."
    }
    return $row
}
