<#
.SYNOPSIS
    Configures Windows Firewall rule for TKG PDA Server.
.DESCRIPTION
    Creates or updates an inbound TCP firewall rule exclusively for the configured server port.
#>
[CmdletBinding()]
param(
    [int]$Port = 5000,
    [ValidateSet('Add', 'Remove')]
    [string]$Action = 'Add',
    [string]$RuleName = 'TKG PDA Server (HTTP)'
)

$ErrorActionPreference = 'Stop'

if ($Action -eq 'Add') {
    Write-Host "[INFO] Checking existing firewall rule: $RuleName"
    $existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "[INFO] Updating existing firewall rule to port $Port..."
        Set-NetFirewallRule -DisplayName $RuleName -LocalPort $Port
    } else {
        Write-Host "[INFO] Creating inbound firewall rule for port $Port..."
        New-NetFirewallRule -DisplayName $RuleName `
            -Direction Inbound `
            -LocalPort $Port `
            -Protocol TCP `
            -Action Allow `
            -Description "Inbound HTTP traffic for TKG PDA Server and Web UI" | Out-Null
    }
    Write-Host "[INFO] Firewall rule active for TCP port $Port."
} elseif ($Action -eq 'Remove') {
    Write-Host "[INFO] Removing firewall rule: $RuleName"
    Remove-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
    Write-Host "[INFO] Firewall rule removed."
}
