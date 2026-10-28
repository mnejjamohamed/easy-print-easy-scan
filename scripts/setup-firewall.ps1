#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Sets up just the firewall rules for PrintBridge development.
    Run this once before testing to allow IPP (TCP 631) and mDNS (UDP 5353).
#>

param(
    [int]$IppPort = 631
)

$ErrorActionPreference = 'Stop'

Write-Host "Setting up PrintBridge firewall rules..." -ForegroundColor Cyan

# Remove old rules
Get-NetFirewallRule -DisplayName "PrintBridge*" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue

# IPP port
New-NetFirewallRule -DisplayName "PrintBridge IPP (TCP $IppPort)" `
    -Direction Inbound -Protocol TCP -LocalPort $IppPort `
    -Action Allow -Profile Any `
    -Description "Allows inbound IPP connections for PrintBridge." |
    Out-Null

# mDNS
New-NetFirewallRule -DisplayName "PrintBridge mDNS (UDP 5353)" `
    -Direction Inbound -Protocol UDP -LocalPort 5353 `
    -Action Allow -Profile Any `
    -Description "Allows mDNS/Bonjour discovery for PrintBridge." |
    Out-Null

Write-Host "Done. Firewall rules created:" -ForegroundColor Green
Write-Host "  - TCP $IppPort (IPP)" -ForegroundColor Gray
Write-Host "  - UDP 5353  (mDNS)" -ForegroundColor Gray
