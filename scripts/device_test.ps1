<#
.SYNOPSIS
    QSS Device Intake — Equipment Test Runner
    Runs your existing test logic, then auto-reports Pass/Fail to the
    intake server so stickers are updated without any manual data entry.

.DESCRIPTION
    1. Reads Serial Number and MAC Address directly from this machine (WMI).
    2. Runs your test block (replace the TODO section below).
    3. POSTs the result to the Flask server on the imaging PC.
    4. Prints a clear PASS / FAIL summary to the console.

.PARAMETER ServerUrl
    Base URL of the intake server.  Default: http://192.168.1.100:5000
    Override per-run:  .\device_test.ps1 -ServerUrl http://10.0.0.50:5000

.PARAMETER SessionId
    Optional. If provided, the server will only match records within that
    specific session. Omit to let the server search all active sessions.

.EXAMPLE
    .\device_test.ps1
    .\device_test.ps1 -ServerUrl http://10.0.0.50:5000 -SessionId AB12CD34
#>

param(
    [string]$ServerUrl  = "http://192.168.1.100:5000",
    [string]$SessionId  = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 1 — Read device identity from WMI
#  These values must match what the goggles scanned off the box label.
# ─────────────────────────────────────────────────────────────────────────────

Write-Host "`n[QSS] Reading device identity..." -ForegroundColor Cyan

$bios    = Get-WmiObject -Class Win32_BIOS
$product = Get-WmiObject -Class Win32_ComputerSystemProduct
$serial  = $bios.SerialNumber.Trim()

# MAC: first active physical adapter, formatted AA:BB:CC:DD:EE:FF
$adapter = Get-NetAdapter | Where-Object {
    $_.Status -eq "Up" -and $_.PhysicalMediaType -ne "Unspecified"
} | Select-Object -First 1

if ($null -eq $adapter) {
    Write-Warning "No active network adapter found. MAC will be empty."
    $mac = ""
} else {
    $mac = ($adapter.MacAddress -replace "-", ":").ToUpper()
}

Write-Host "  Serial : $serial"
Write-Host "  MAC    : $mac"

if ([string]::IsNullOrWhiteSpace($serial)) {
    Write-Error "Could not read serial number from WMI. Aborting."
    exit 1
}

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 2 — YOUR TEST LOGIC
#  Replace / extend the block below with the actual checks you run.
#  Set $testPassed = $true / $false based on your results.
#  Populate $testNotes with anything useful for the Excel report.
# ─────────────────────────────────────────────────────────────────────────────

Write-Host "`n[QSS] Running device tests..." -ForegroundColor Cyan

$testPassed = $true
$testNotes  = @()

try {
    # ── TODO: replace these stubs with your real checks ──────────────────────

    # Example 1 — Ping a critical server
    # $ping = Test-Connection -ComputerName "8.8.8.8" -Count 2 -Quiet
    # if (-not $ping) { $testPassed = $false; $testNotes += "Network unreachable" }

    # Example 2 — Check a required service is running
    # $svc = Get-Service -Name "wuauserv" -ErrorAction SilentlyContinue
    # if ($svc.Status -ne "Running") { $testPassed = $false; $testNotes += "Windows Update service not running" }

    # Example 3 — Check available disk space (>10 GB free on C:)
    # $disk = Get-PSDrive C
    # if ($disk.Free -lt 10GB) { $testPassed = $false; $testNotes += "Low disk space: $([math]::Round($disk.Free/1GB,1)) GB free" }

    # ── Placeholder — remove when you add real checks ─────────────────────────
    $testNotes += "No tests configured — edit SECTION 2 in device_test.ps1"

} catch {
    $testPassed = $false
    $testNotes += "Test exception: $_"
}

$passFail   = if ($testPassed) { "PASS" } else { "FAIL" }
$notesJoined = $testNotes -join "; "

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 3 — POST result to intake server
# ─────────────────────────────────────────────────────────────────────────────

Write-Host "`n[QSS] Reporting result to server..." -ForegroundColor Cyan

$payload = @{
    SerialNumber = $serial
    MacAddress   = $mac
    PassFail     = $passFail
    Notes        = $notesJoined
    SessionId    = $SessionId
    Timestamp    = (Get-Date -Format "o")
} | ConvertTo-Json

try {
    $response = Invoke-RestMethod `
        -Uri         "$ServerUrl/api/results" `
        -Method      POST `
        -Body        $payload `
        -ContentType "application/json" `
        -TimeoutSec  10

    Write-Host "  Server matched: Session $($response.SessionId)  Box $($response.BoxId)" -ForegroundColor Green
} catch {
    Write-Warning "Could not reach server ($ServerUrl). Result NOT recorded remotely."
    Write-Warning "Error: $_"

    # Fall back — write result to a local CSV so it can be imported later
    $fallbackPath = "$PSScriptRoot\fallback_results.csv"
    $exists       = Test-Path $fallbackPath

    $row = [PSCustomObject]@{
        Timestamp    = (Get-Date -Format "o")
        SerialNumber = $serial
        MacAddress   = $mac
        PassFail     = $passFail
        Notes        = $notesJoined
        SessionId    = $SessionId
    }

    $row | Export-Csv -Path $fallbackPath -Append -NoTypeInformation
    Write-Host "  Saved to fallback: $fallbackPath" -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 4 — Console summary
# ─────────────────────────────────────────────────────────────────────────────

Write-Host ""
if ($testPassed) {
    Write-Host "  ██████╗  █████╗ ███████╗███████╗" -ForegroundColor Green
    Write-Host "  ██╔══██╗██╔══██╗██╔════╝██╔════╝" -ForegroundColor Green
    Write-Host "  ██████╔╝███████║███████╗███████╗ " -ForegroundColor Green
    Write-Host "  ██╔═══╝ ██╔══██║╚════██║╚════██║" -ForegroundColor Green
    Write-Host "  ██║     ██║  ██║███████║███████║" -ForegroundColor Green
    Write-Host "  ╚═╝     ╚═╝  ╚═╝╚══════╝╚══════╝" -ForegroundColor Green
} else {
    Write-Host "  ███████╗ █████╗ ██╗██╗     " -ForegroundColor Red
    Write-Host "  ██╔════╝██╔══██╗██║██║     " -ForegroundColor Red
    Write-Host "  █████╗  ███████║██║██║     " -ForegroundColor Red
    Write-Host "  ██╔══╝  ██╔══██║██║██║     " -ForegroundColor Red
    Write-Host "  ██║     ██║  ██║██║███████╗" -ForegroundColor Red
    Write-Host "  ╚═╝     ╚═╝  ╚═╝╚═╝╚══════╝" -ForegroundColor Red
    Write-Host ""
    Write-Host "  Reasons:" -ForegroundColor Red
    foreach ($note in $testNotes) {
        Write-Host "    • $note" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "  Serial : $serial"
Write-Host "  MAC    : $mac"
Write-Host "  Result : $passFail"
if ($notesJoined) { Write-Host "  Notes  : $notesJoined" }
Write-Host ""
