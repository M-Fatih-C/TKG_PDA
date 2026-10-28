[CmdletBinding()]
param([string]$HostName = '127.0.0.1', [int]$Port = 55450,
    [string]$Database = 'tkg_pda_dev', [string]$User = 'postgres')
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
. (Join-Path $PSScriptRoot 'persistent_safety.ps1')

# Restart testing is read-only on the persistent database. In particular it
# remains safe when a machine has a pre-existing ACTIVE/ENDING session.
$before = Get-PersistentBusinessSnapshot
Write-Host 'PHASE: Runtime persistence across two normal startups (RDS Off)'
& pwsh -NoProfile -File (Join-Path $projectRoot 'tools\dev\stop_local_stack.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Local stack stop failed.' }
for ($iteration = 1; $iteration -le 2; $iteration++) {
    & pwsh -NoProfile -File (Join-Path $projectRoot 'tools\dev\start_local_stack.ps1') -RdsMode Off
    if ($LASTEXITCODE -ne 0) { throw "Normal startup $iteration failed." }
    Assert-PersistentBusinessSnapshot $before
}
Write-Host 'RESULT: PASS (all original runtime rows, versions, timestamps and business state survived two normal startups)'
