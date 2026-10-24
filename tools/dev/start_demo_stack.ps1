<#
.SYNOPSIS
    Starts TKG PDA in strictly offline DEMO_MODE for customer presentations.
.DESCRIPTION
    Runs the end-user interfaces (Admin Web and PDA Web) backed by local PostgreSQL,
    with ZERO connection to real RDS Core, PLC, robots, or field networks.
    All robot operations are safely simulated by the DemoSimulationWorker.
#>
[CmdletBinding()]
param(
    [int]$ApiPort = 5000,
    [int]$PostgresPort = 55450,
    [string]$Database = 'tkg_pda_acceptance',
    [int]$SimulationDelaySeconds = 3,
    [int]$RunningDurationSeconds = 30,
    [bool]$AutoPrepareWarehouseDemands = $false,
    [switch]$Foreground = $false
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runtimeDir = Join-Path $projectRoot '.runtime'
if (-not (Test-Path $runtimeDir)) { New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null }
$apiLog = Join-Path $runtimeDir 'tkg_demo_api.log'
$apiErrLog = Join-Path $runtimeDir 'tkg_demo_api.err.log'

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "       TKG PDA -- OFFLINE CUSTOMER DEMO LAUNCHER                " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "Safety Gate: Live RDS / Robot / PLC dispatch is GLOBALLY BLOCKED." -ForegroundColor Yellow
Write-Host "Simulation:  DemoSimulationWorker active (Delay: ${SimulationDelaySeconds}s, AGV: DEMO-AGV-01)." -ForegroundColor Green
Write-Host "Database:    $Database on port $PostgresPort" -ForegroundColor Gray
Write-Host "API Port:    $ApiPort" -ForegroundColor Gray
Write-Host "=================================================================" -ForegroundColor Cyan

# 1. Port Check
$busy = Get-NetTCPConnection -State Listen -LocalPort $ApiPort -ErrorAction SilentlyContinue
if ($busy) {
    Write-Warning "Port $ApiPort is currently in use. Attempting to check if existing process is TkgPda..."
    $procId = $busy | Select-Object -ExpandProperty OwningProcess -Unique | Select-Object -First 1
    $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
    $isDemoPid = $false
    $demoPidFile = Join-Path $runtimeDir 'demo_api.pid'
    if (Test-Path $demoPidFile) {
        $savedPid = (Get-Content $demoPidFile -Raw -ErrorAction SilentlyContinue)
        if ($savedPid -and $savedPid.Trim() -eq [string]$procId) { $isDemoPid = $true }
    }
    if ($proc -and ($proc.ProcessName -like "*TkgPda*" -or $proc.ProcessName -eq "dotnet" -or $isDemoPid)) {
        Write-Host "Stopping previous TkgPda process (PID $procId)..." -ForegroundColor Yellow
        Stop-Process -Id $procId -Force
        Start-Sleep -Seconds 1
    } else {
        throw "Port $ApiPort is occupied by process $($proc.ProcessName) (PID $procId). Run tools\dev\stop_local_stack.ps1 first."
    }
}

# 2. Build API
Write-Host "[1/4] Building API..." -ForegroundColor Cyan
$apiCsproj = Join-Path $projectRoot 'src\TkgPda.Api\TkgPda.Api.csproj'
& dotnet build $apiCsproj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    throw "Failed to compile TkgPda.Api."
}

# 3. Configure Environment Variables
Write-Host "[2/4] Setting environment variables for DEMO_MODE..." -ForegroundColor Cyan
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://0.0.0.0:$ApiPort"
$env:ConnectionStrings__TkgPda = "Host=127.0.0.1;Port=$PostgresPort;Database=$Database;Username=postgres;Pooling=true;Maximum Pool Size=50"
$stagingWeb = Join-Path $projectRoot 'build_staging\Web'
if (Test-Path $stagingWeb) {
    $env:Web__RootPath = $stagingWeb
}

# Explicit Demo Mode Flags
$env:TKG_DEMO_MODE = 'true'
$env:DemoMode__Enabled = 'true'
$env:DemoMode__SimulationDelaySeconds = [string]$SimulationDelaySeconds
$env:DemoMode__RunningDurationSeconds = [string]$RunningDurationSeconds
$env:DemoMode__AutoPrepareWarehouseDemands = [string]$AutoPrepareWarehouseDemands.ToString().ToLowerInvariant()
$env:DemoMode__SimulatedAgvId = 'DEMO-AGV-01'

# Hardware / Network Isolation (Fail-Safe)
$env:Rds__Enabled = 'false'
$env:Rds__DispatchEnabled = 'false'
$env:Rds__BaseUrl = 'http://127.0.0.1:8080'
$env:RdsCoreExecution__Enabled = 'false'
$env:RdsCoreDiscovery__Enabled = 'false'
$env:RdsCore__BaseUrl = 'http://127.0.0.1:8088'

# JWT Key
$keyBytes = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($keyBytes)
$env:Authentication__Jwt__SigningKey = [Convert]::ToBase64String($keyBytes)
Remove-Variable keyBytes

# CORS for local and browser access
$env:Cors__AllowedOrigins__0 = "http://127.0.0.1:$ApiPort"
$env:Cors__AllowedOrigins__1 = "http://localhost:$ApiPort"
$env:Cors__AllowedOrigins__2 = "http://127.0.0.1:7357"
$env:Cors__AllowedOrigins__3 = "http://localhost:7357"
$env:Cors__AllowedOrigins__4 = "http://127.0.0.1:8085"
$env:Cors__AllowedOrigins__5 = "http://localhost:8085"

# 4. Start Server Process
$apiDll = Join-Path $projectRoot 'src\TkgPda.Api\bin\Release\net10.0\TkgPda.Api.dll'
if (-not (Test-Path $apiDll)) {
    $apiDll = Join-Path $projectRoot 'src\TkgPda.Api\bin\Debug\net10.0\TkgPda.Api.dll'
}

Write-Host "[3/4] Launching API server with offline simulation..." -ForegroundColor Cyan
if ($Foreground) {
    & dotnet $apiDll
    return
}

$proc = Start-Process -FilePath 'dotnet' `
    -ArgumentList "`"$apiDll`"" `
    -WorkingDirectory (Join-Path $projectRoot 'src\TkgPda.Api') `
    -RedirectStandardOutput $apiLog `
    -RedirectStandardError $apiErrLog `
    -PassThru

Set-Content (Join-Path $runtimeDir 'demo_api.pid') $proc.Id

# 5. Wait for Health Check
Write-Host "[4/4] Verifying health and demo readiness on http://127.0.0.1:$ApiPort/health/live..." -ForegroundColor Cyan
$ready = $false
for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 1
    try {
        $res = Invoke-RestMethod -Uri "http://127.0.0.1:$ApiPort/health/live" -TimeoutSec 2 -ErrorAction Stop
        if ($res.status -eq 'live' -and $res.demoMode -eq $true) {
            $ready = $true
            break
        }
    } catch {}
}

if (-not $ready) {
    Write-Warning "API did not respond on /health/live within 15 seconds. Check logs: $apiLog"
    if (Test-Path $apiLog) { Get-Content $apiLog -Tail 20 }
    throw "Server startup timed out."
}

Write-Host ""
Write-Host "=================================================================" -ForegroundColor Green
Write-Host "       TKG PDA DEMO SERVER READY AND RUNNING                     " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
Write-Host " Web Arayuzleri (Tarayicida acin):" -ForegroundColor White
Write-Host "   * Ana Karsilama Sayfasi: http://127.0.0.1:$ApiPort/" -ForegroundColor Cyan
Write-Host "   * PDA Operatör Ekrani:   http://127.0.0.1:$ApiPort/pda/" -ForegroundColor Cyan
Write-Host "   * Admin Yönetim Paneli:  http://127.0.0.1:$ApiPort/admin/" -ForegroundColor Cyan
Write-Host ""
Write-Host " Demo Giris Bilgileri:" -ForegroundColor White
Write-Host "   * Pres Hatti Operatörü:  Sicil: DEV-LINE    | Cihaz: DEV-LINE (Makine: SH-01-A)" -ForegroundColor Yellow
Write-Host "   * Depo Operatörü:        Sicil: DEV-WH      | Cihaz: DEV-WAREHOUSE" -ForegroundColor Yellow
Write-Host "   * Yönetici / Admin:      Kullanici: dev-admin | Cihaz: DEV-SUPERVISOR" -ForegroundColor Yellow
Write-Host ""
Write-Host " Simülasyon Akisi:" -ForegroundColor White
Write-Host "   1. PDA ekraninda 'Kasa Doldu' veya 'Boş Kasa Getir' butonuna tiklayin."
Write-Host "   2. Talep olusur ve DemoSimulationWorker 2 saniye icinde 'ROBOT_ASSIGNED' yapar."
Write-Host "   3. 'IN_PROGRESS' durumunda tahmini süre (ETA) geri sayar."
Write-Host "   4. 'COMPLETED' durumuna gecince makine slot dolulugu (DOLU / BOS) otomatik güncellenir."
Write-Host "   5. Gercek robotlara veya dis aglara hicbir paket gonderilmez."
Write-Host "=================================================================" -ForegroundColor Green
Write-Host "Durdurmak icin: Stop-Process -Id $($proc.Id) veya tools\dev\stop_local_stack.ps1 calistirin." -ForegroundColor Gray
