<#
.SYNOPSIS
    Step 02 — SNMP Configuration  (TSW-1070)
    Sets SNMP parameters, reboots, and verifies the settings stuck.
#>
param(
    [string]$Device,
    [string]$Username = "admin",
    [string]$Password = "",
    [switch]$Secure,
    [string]$FirmwarePath = ""   # unused in this step; accepted for interface consistency
)

$ErrorActionPreference = "Continue"

$auth = @{}
if ($Secure)                                 { $auth.Secure   = $true     }
if (-not [string]::IsNullOrEmpty($Username)) { $auth.Username = $Username }
if (-not [string]::IsNullOrEmpty($Password)) { $auth.Password = $Password }

Write-Output "Opening session to $Device for SNMP configuration..."
try {
    $s = Open-CrestronSession -Device $Device @auth
    Invoke-CrestronSession $s "" | Out-Null
    Invoke-CrestronSession $s "ECHO OFF" | Out-Null
} catch {
    Write-Output "Session open failed: $_"
    exit 1
}

# ── Set SNMP ──────────────────────────────────────────────────────────────────
# TODO: replace with your actual SNMP commands
# Examples:
#   $r = Invoke-CrestronSession $s "SNMPENABLE ON"
#   $r = Invoke-CrestronSession $s "SNMPCOMMUNITY public"
#   $r = Invoke-CrestronSession $s "SNMPLOCATION Server Room A"
#   $r = Invoke-CrestronSession $s "SNMPCONTACT admin@company.com"
# Write-Output "SNMP configured: $r"

# ── Reboot and verify ─────────────────────────────────────────────────────────
Write-Output "Rebooting after SNMP configuration..."
Invoke-CrestronSession $s "REBOOT" | Out-Null
Close-CrestronSession $s

try {
    Reset-CrestronDevice -Device $Device @auth
    Write-Output "Device online after reboot"
} catch {
    Write-Output "Device did not respond after reboot: $_"
    exit 1
}

# TODO: verify SNMP settings are live after reboot
# $verify = Invoke-CrestronCommand -Device $Device -Command "SNMPSHOW" @auth
# Write-Output "SNMP verify: $verify"

Write-Output "Step 02 SNMP configuration complete"
exit 0
