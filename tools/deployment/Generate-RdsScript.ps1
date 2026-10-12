<#
.SYNOPSIS
    Generates the RDS integration script for robot commissioning.
.DESCRIPTION
    Interpolates integrationKey, callbackKey, and callbackUrl into the RDS script template
    and saves the file to C:\ProgramData\TKG PDA\RDS\tkg_integration_install.js.
    Generates cryptographically secure keys if not provided.
#>
[CmdletBinding()]
param(
    [string]$TemplatePath = '',
    [string]$OutputPath = 'C:\ProgramData\TKG PDA\RDS\tkg_integration_install.js',
    [string]$IntegrationKey = '',
    [string]$CallbackKey = '',
    [string]$CallbackUrl = 'http://127.0.0.1:5000/api/integrations/rds/events'
)

$ErrorActionPreference = 'Stop'

function New-SecureKeyHex {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    return ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

# 1. Resolve Template Path
if ([string]::IsNullOrWhiteSpace($TemplatePath)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $projectRoot = Resolve-Path (Join-Path $scriptDir '..\..') -ErrorAction SilentlyContinue
    $candidates = @(
        (Join-Path $scriptDir 'rds\scripts\tkg_integration_install.template.js'),
        (Join-Path $projectRoot 'rds\scripts\tkg_integration_install.template.js')
    )
    foreach ($cand in $candidates) {
        if (Test-Path $cand) { $TemplatePath = (Resolve-Path $cand).Path; break }
    }
}

if (-not (Test-Path $TemplatePath)) {
    throw "RDS Script template not found at: $TemplatePath"
}

# 2. Ensure Secure Keys
if ([string]::IsNullOrWhiteSpace($IntegrationKey)) {
    $IntegrationKey = New-SecureKeyHex
}

if ([string]::IsNullOrWhiteSpace($CallbackKey)) {
    $CallbackKey = New-SecureKeyHex
}

# 3. Read and Interpolate Template
$content = Get-Content -Path $TemplatePath -Raw -Encoding UTF8

$content = $content.Replace('<SAME VALUE AS BACKEND Rds__IntegrationKey>', $IntegrationKey)
$content = $content.Replace('<SAME VALUE AS BACKEND Rds__CallbackKey>', $CallbackKey)
$content = $content.Replace('http://<TKG_API_HOST>:<TKG_API_PORT>/api/integrations/rds/events', $CallbackUrl)

# 4. Write to Output Directory
$outDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

[System.IO.File]::WriteAllText($OutputPath, $content, [System.Text.Encoding]::UTF8)

Write-Host "[INFO] Generated RDS commissioning script at: $OutputPath"
return @{
    ScriptPath = $OutputPath
    IntegrationKey = $IntegrationKey
    CallbackKey = $CallbackKey
    CallbackUrl = $CallbackUrl
}
