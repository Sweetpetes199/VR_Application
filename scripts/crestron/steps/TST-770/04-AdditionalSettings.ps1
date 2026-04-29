<#
.SYNOPSIS
    Step 04 — Additional Settings  (TST-770)
    Applies final settings. No reboot — device is ready for deployment after this step.
    Output is captured and sent to Claude to confirm all settings applied cleanly.
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

Write-Output "Opening session to $Device for additional settings..."
try {
    $s = Open-CrestronSession -Device $Device @auth
    Invoke-CrestronSession $s "" | Out-Null
    Invoke-CrestronSession $s "ECHO OFF" | Out-Null
} catch {
    Write-Output "Session open failed: $_"
    exit 1
}

# ── Additional settings (no reboot) ──────────────────────────────────────────
# TODO: add all remaining settings your workflow requires
# Each Write-Output line becomes part of what Claude reads to verify success.
#
# Example — timezone:
#   $r = Invoke-CrestronSession $s "TIMEZONE -5"
#   Write-Output "Timezone: $r"
#
# Example — NTP server:
#   $r = Invoke-CrestronSession $s "NTPSERVER pool.ntp.org"
#   Write-Output "NTP server: $r"
#
# Example — display timeout:
#   $r = Invoke-CrestronSession $s "DISPLAYTIMEOUT 30"
#   Write-Output "Display timeout: $r"
#
# Example — read back a value to confirm:
#   $r = Invoke-CrestronSession $s "IPCONFIG"
#   Write-Output "IPCONFIG: $r"

Close-CrestronSession $s

# ── Final device info dump (feeds into the CSV report) ───────────────────────
Write-Output "Reading final device state..."
try {
    $info = Invoke-CrestronCommand -Device $Device -Command "VER" @auth
    Write-Output "Final VER: $($info.Trim())"

    $ip = Invoke-CrestronCommand -Device $Device -Command "IPCONFIG" @auth
    Write-Output "IPCONFIG: $($ip.Trim())"
} catch {
    Write-Output "Could not read final device state: $_"
}

Write-Output "Step 04 Additional Settings complete — device ready for deployment"
exit 0
