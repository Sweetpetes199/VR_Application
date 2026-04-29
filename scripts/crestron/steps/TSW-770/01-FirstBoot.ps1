<#
.SYNOPSIS
    Step 01 — First Boot, Firmware Push & Initial Settings  (TSW-770)

STUB — replace the TODO blocks with your actual commands.
The runner captures every Write-Output / Write-Host line and sends the full
text to Claude, which determines PASS/FAIL from the content.

Interface contract (required by crestron_runner.ps1):
  • Accept -Device, -Username, -Password, -Secure, -FirmwarePath as params
  • Write readable status lines to stdout throughout
  • Exit 0 on success, Exit 1 on any unrecoverable failure
  • Do NOT call exit 0 until the reboot-and-verify is confirmed
#>
param(
    [string]$Device,
    [string]$Username     = "admin",
    [string]$Password     = "",
    [switch]$Secure,
    [string]$FirmwarePath = "C:\Crestron\Firmware"
)

$ErrorActionPreference = "Continue"

$auth = @{}
if ($Secure)                           { $auth.Secure   = $true     }
if (-not [string]::IsNullOrEmpty($Username)) { $auth.Username = $Username }
if (-not [string]::IsNullOrEmpty($Password)) { $auth.Password = $Password }

# ── 1. Push firmware ──────────────────────────────────────────────────────────
Write-Output "Pushing firmware from: $FirmwarePath"
try {
    $pufResult = Update-PUF -DeviceList $Device -PUFPath $FirmwarePath @auth
    if ($pufResult.Result -eq "Pass") {
        Write-Output "Firmware PASS — version: $($pufResult.EndVersion)"
    } else {
        Write-Output "Firmware FAIL — $($pufResult.Error)"
        exit 1
    }
} catch {
    Write-Output "Firmware push exception: $_"
    exit 1
}

# ── 2. Open console session ───────────────────────────────────────────────────
Write-Output "Opening session to $Device..."
try {
    $s = Open-CrestronSession -Device $Device @auth
    Invoke-CrestronSession $s "" | Out-Null          # clear buffer
    Invoke-CrestronSession $s "ECHO OFF" | Out-Null  # suppress command echo
} catch {
    Write-Output "Session open failed: $_"
    exit 1
}

# ── 3. Set hostname ───────────────────────────────────────────────────────────
# TODO: set $newHostname from your naming convention / CSV / param
# $newHostname = "TSW-ROOMNAME"
# $r = Invoke-CrestronSession $s "HOSTNAME $newHostname"
# Write-Output "Hostname set to $newHostname : $r"

# ── 4. Initial settings ───────────────────────────────────────────────────────
# TODO: add your first-boot settings commands
# $r = Invoke-CrestronSession $s "YOUR-SETTING-COMMAND"
# Write-Output "Setting X: $r"

# ── 5. Reboot and verify ──────────────────────────────────────────────────────
Write-Output "Sending REBOOT..."
Invoke-CrestronSession $s "REBOOT" | Out-Null
Close-CrestronSession $s

Write-Output "Waiting for device to come back online..."
try {
    Reset-CrestronDevice -Device $Device @auth
    Write-Output "Device online after reboot"
} catch {
    Write-Output "Device did not respond after reboot: $_"
    exit 1
}

# Verify with VER
$ver = Invoke-CrestronCommand -Device $Device -Command "VER" @auth
Write-Output "VER after reboot: $($ver.Trim())"
Write-Output "Step 01 First Boot complete"

exit 0
