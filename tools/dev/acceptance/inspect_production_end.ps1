[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Guid]$SessionId,
    [string]$HostName = '127.0.0.1',
    [int]$Port = 55450,
    [string]$Database = 'tkg_pda_dev',
    [string]$User = 'postgres'
)
$ErrorActionPreference = 'Stop'
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path -LiteralPath $psql)) { $psql = (Get-Command psql.exe -ErrorAction Stop).Source }
# Always read-only. There is deliberately no Apply option: unknown ownership and
# previous physical execution cannot authorize automatic artifact recovery.
$result = & $psql -X -h $HostName -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -v "session_id=$SessionId" -q -A -t -f (Join-Path $PSScriptRoot 'inspect_production_end.sql')
if ($LASTEXITCODE -ne 0) { throw 'Read-only Production End inspection failed.' }
$evidence = ($result -join "`n") | ConvertFrom-Json
if ($null -eq $evidence.session) { throw 'Session not found; no recovery action is authorized.' }
$evidence | ConvertTo-Json -Depth 30
Write-Host 'READ ONLY: No API command, RDS call, or business-state mutation was performed.'
Write-Host 'Recovery requires physical-state verification and an authorized normal End command with a fresh key; same-key replay does not re-plan.'
