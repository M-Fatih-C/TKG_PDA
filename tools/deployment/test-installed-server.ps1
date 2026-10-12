<#
.SYNOPSIS
    Smoke-test verification script for installed TKG PDA Server.
.DESCRIPTION
    Validates Windows Service state, TCP port listening, health endpoints,
    database readiness, version endpoint, Admin Web, PDA Web, and root landing page.
#>
[CmdletBinding()]
param(
    [string]$ServerHost = '127.0.0.1',
    [int]$Port = 5000,
    [string]$ServiceName = 'TKG PDA Server',
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

function Write-CheckResult {
    param([string]$CheckName, [bool]$Passed, [string]$Details = '')
    $status = if ($Passed) { "[PASS]" } else { "[FAIL]" }
    $color = if ($Passed) { "Green" } else { "Red" }
    Write-Host "$status $CheckName - $Details" -ForegroundColor $color
}

$allPassed = $true
$results = [System.Collections.Generic.List[PSObject]]::new()

Write-Host "=================================================="
Write-Host "TKG PDA SERVER DEPLOYMENT VALIDATION"
Write-Host "Target: http://$ServerHost`:$Port"
Write-Host "=================================================="

# 1. Check Windows Service
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$svcRunning = ($null -ne $svc -and $svc.Status -eq 'Running')
if (-not $svcRunning) { $allPassed = $false }
Write-CheckResult "Windows Service ($ServiceName)" $svcRunning ($svc ? $svc.Status.ToString() : "Not Installed")
$results.Add([PSCustomObject]@{ Check = 'Windows Service'; Passed = $svcRunning; Details = ($svc ? $svc.Status.ToString() : "Not Installed") })

# 2. Check Port Listening
$portListening = $false
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $iar = $tcp.BeginConnect($ServerHost, $Port, $null, $null)
    $portListening = $iar.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds(3))
    if ($portListening) { $tcp.EndConnect($iar) }
    $tcp.Close()
} catch {
    $portListening = $false
}
if (-not $portListening) { $allPassed = $false }
Write-CheckResult "TCP Port Listening ($Port)" $portListening ($portListening ? "Listening" : "Port closed")
$results.Add([PSCustomObject]@{ Check = 'TCP Port'; Passed = $portListening; Details = "Port $Port" })

# 3. Check Live Endpoint (/health/live)
$liveOk = $false
$liveDetail = ""
try {
    $res = Invoke-RestMethod -Uri "http://$ServerHost`:$Port/health/live" -TimeoutSec 5 -ErrorAction Stop
    if ($res.status -eq 'live') {
        $liveOk = $true
        $liveDetail = "HTTP 200 OK (status=live)"
    } else {
        $liveDetail = "Unexpected payload: $($res | ConvertTo-Json -Compress)"
    }
} catch {
    $liveDetail = $_.Exception.Message
}
if (-not $liveOk) { $allPassed = $false }
Write-CheckResult "Liveness Check (/health/live)" $liveOk $liveDetail
$results.Add([PSCustomObject]@{ Check = 'Liveness'; Passed = $liveOk; Details = $liveDetail })

# 4. Check Ready Endpoint (/health/ready)
$readyOk = $false
$readyDetail = ""
try {
    $res = Invoke-RestMethod -Uri "http://$ServerHost`:$Port/health/ready" -TimeoutSec 5 -ErrorAction Stop
    if ($res.status -eq 'ready') {
        $readyOk = $true
        $readyDetail = "HTTP 200 OK (status=ready, database connected)"
    } else {
        $readyDetail = "Status not ready: $($res | ConvertTo-Json -Compress)"
    }
} catch {
    $readyDetail = $_.Exception.Message
}
if (-not $readyOk) { $allPassed = $false }
Write-CheckResult "Readiness Check (/health/ready)" $readyOk $readyDetail
$results.Add([PSCustomObject]@{ Check = 'Readiness & Database'; Passed = $readyOk; Details = $readyDetail })

# 5. Check Version Endpoint (/version)
$versionOk = $false
$versionDetail = ""
try {
    $res = Invoke-RestMethod -Uri "http://$ServerHost`:$Port/version" -TimeoutSec 5 -ErrorAction Stop
    if ($res.version) {
        $versionOk = $true
        $versionDetail = "Version: $($res.version), Commit: $($res.commit)"
    } else {
        $versionDetail = "No version in payload"
    }
} catch {
    $versionDetail = $_.Exception.Message
}
if (-not $versionOk) { $allPassed = $false }
Write-CheckResult "Version Endpoint (/version)" $versionOk $versionDetail
$results.Add([PSCustomObject]@{ Check = 'Version API'; Passed = $versionOk; Details = $versionDetail })

# 6. Check Admin Web (/admin/)
$adminOk = $false
$adminDetail = ""
try {
    $webRes = Invoke-WebRequest -Uri "http://$ServerHost`:$Port/admin/" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    if ($webRes.StatusCode -eq 200 -and ($webRes.Content -like "*TKG PDA*" -or $webRes.Content -like "*root*")) {
        $adminOk = $true
        $adminDetail = "HTTP 200 OK (React SPA index.html returned)"
    } else {
        $adminDetail = "HTTP Status $($webRes.StatusCode)"
    }
} catch {
    $adminDetail = $_.Exception.Message
}
if (-not $adminOk) { $allPassed = $false }
Write-CheckResult "Admin Web Interface (/admin/)" $adminOk $adminDetail
$results.Add([PSCustomObject]@{ Check = 'Admin Web'; Passed = $adminOk; Details = $adminDetail })

# 7. Check PDA Web (/pda/)
$pdaOk = $false
$pdaDetail = ""
try {
    $webRes = Invoke-WebRequest -Uri "http://$ServerHost`:$Port/pda/" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    if ($webRes.StatusCode -eq 200 -and ($webRes.Content -like "*flutter*" -or $webRes.Content -like "*html*")) {
        $pdaOk = $true
        $pdaDetail = "HTTP 200 OK (Flutter Web index.html returned)"
    } else {
        $pdaDetail = "HTTP Status $($webRes.StatusCode)"
    }
} catch {
    $pdaDetail = $_.Exception.Message
}
if (-not $pdaOk) { $allPassed = $false }
Write-CheckResult "PDA / Operator Interface (/pda/)" $pdaOk $pdaDetail
$results.Add([PSCustomObject]@{ Check = 'PDA Web'; Passed = $pdaOk; Details = $pdaDetail })

# 8. Check Root Landing Page (/)
$rootOk = $false
$rootDetail = ""
try {
    $webRes = Invoke-WebRequest -Uri "http://$ServerHost`:$Port/" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
    if ($webRes.StatusCode -eq 200 -and $webRes.Content -like "*TKG PDA*") {
        $rootOk = $true
        $rootDetail = "HTTP 200 OK (Server Landing Page returned)"
    } else {
        $rootDetail = "HTTP Status $($webRes.StatusCode)"
    }
} catch {
    $rootDetail = $_.Exception.Message
}
if (-not $rootOk) { $allPassed = $false }
Write-CheckResult "Root Landing Page (/)" $rootOk $rootDetail
$results.Add([PSCustomObject]@{ Check = 'Landing Page'; Passed = $rootOk; Details = $rootDetail })

Write-Host "=================================================="
if ($allPassed) {
    Write-Host "OVERALL RESULT: PASS (All checks succeeded)" -ForegroundColor Green
    return @{ Result = "PASS"; Checks = $results }
} else {
    Write-Host "OVERALL RESULT: FAIL (One or more checks failed)" -ForegroundColor Red
    throw "Deployment smoke tests failed."
}
