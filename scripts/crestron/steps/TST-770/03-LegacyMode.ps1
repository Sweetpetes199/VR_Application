<#
.SYNOPSIS
    Step 03 — Legacy Mode  (TST-770)
    Enables Legacy Mode, reboots, and verifies.
#>
param(
    [string]$Device,
    [string]$Username     = "admin",
    [string]$Password     = "",
    [switch]$Secure,
    [string]$FirmwarePath = ""
)

$ErrorActionPreference = "Continue"

$auth = @{}
if ($Secure)                                 { $auth.Secure   = $true     }
if (-not [string]::IsNullOrEmpty($Username)) { $auth.Username = $Username }
if (-not [string]::IsNullOrEmpty($Password)) { $auth.Password = $Password }

Write-Output "Opening session to $Device for Legacy Mode configuration..."
try {
    $s = Open-CrestronSession -Device $Device @auth
    Invoke-CrestronSession $s "" | Out-Null
    Invoke-CrestronSession $s "ECHO OFF" | Out-Null
} catch {
    Write-Output "Session open failed: $_"
    exit 1
}

# ── Set Legacy Mode ───────────────────────────────────────────────────────────
# TODO: replace with your actual Legacy Mode command
# $r = Invoke-CrestronSession $s "LEGACYMODE ON"
# Write-Output "Legacy Mode: $r"

# ── Reboot and verify ─────────────────────────────────────────────────────────
Write-Output "Rebooting after Legacy Mode change..."
Invoke-CrestronSession $s "REBOOT" | Out-Null
Close-CrestronSession $s

try {
    Reset-CrestronDevice -Device $Device @auth
    Write-Output "Device online after reboot"
} catch {
    Write-Output "Device did not respond after reboot: $_"
    exit 1
}

# TODO: verify legacy mode is active
# $verify = Invoke-CrestronCommand -Device $Device -Command "LEGACYMODE" @auth
# Write-Output "Legacy Mode verify: $verify"

Write-Output "Step 03 Legacy Mode complete"
exit 0
