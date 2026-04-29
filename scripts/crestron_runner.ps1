<#
.SYNOPSIS
    QSS Crestron Device Setup Runner
    Discovers the target device, identifies its type, runs the matching
    setup scripts in sequence, asks Claude to grade each step, POSTs the
    full result to the intake server, and saves a local CSV report.

.PARAMETER Device
    IP address or hostname.  If omitted, auto-discovery runs on the local
    subnet and you choose from the list.

.PARAMETER ServerUrl
    Base URL of the QSS intake server.  Default: http://192.168.1.100:5000

.PARAMETER SessionId
    Optional.  Ties this run to an existing intake session (from the
    Phase 1 VR scan) so the sticker in Phase 3 reflects the setup result.

.PARAMETER Username
    Crestron device username.  Default: admin

.PARAMETER Password
    Crestron device password.

.PARAMETER Secure
    Use SSH (port 41797) instead of plain CTP (port 41795).

.PARAMETER FirmwarePath
    Folder that contains the PUF firmware files.
    Default: C:\Crestron\Firmware

.PARAMETER ReportDir
    Folder where the CSV report is saved.
    Default: next to this script in a Reports\ subfolder.

.PARAMETER SkipAI
    Skip Claude verdict — fall back to step exit codes only.

.EXAMPLE
    .\crestron_runner.ps1 -Device 192.168.1.50 -FirmwarePath "C:\FW\Crestron"
    .\crestron_runner.ps1 -Device TSW-RM101 -Secure -Username admin -Password Pa$$w0rd -SessionId AB12CD34
#>

param(
    [string]$Device       = "",
    [string]$ServerUrl    = "http://192.168.1.100:5000",
    [string]$SessionId    = "",
    [string]$Username     = "admin",
    [string]$Password     = "",
    [switch]$Secure,
    [string]$FirmwarePath = "C:\Crestron\Firmware",
    [string]$ReportDir    = "",
    [switch]$SkipAI
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $ReportDir) {
    $ReportDir = Join-Path $PSScriptRoot "Reports"
}
New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 1 — Import EDK and resolve device
# ─────────────────────────────────────────────────────────────────────────────

Write-Host "`n[QSS] Importing Crestron EDK..." -ForegroundColor Cyan
Import-Module PSCrestron -ErrorAction Stop

if ([string]::IsNullOrWhiteSpace($Device)) {
    Write-Host "[QSS] No device specified — running auto-discovery..." -ForegroundColor Cyan
    $discovered = Get-AutoDiscovery
    if ($null -eq $discovered -or $discovered.Count -eq 0) {
        Write-Error "No Crestron devices found on this subnet."
        exit 1
    }
    Write-Host "`nDiscovered devices:"
    $discovered | ForEach-Object -Begin { $i = 1 } -Process {
        Write-Host ("  [{0}] {1,-18} {2,-30} {3}" -f $i, $_.IPAddress, $_.Hostname, $_.Description)
        $i++
    }
    $pick   = Read-Host "`nEnter device number"
    $Device = $discovered[[int]$pick - 1].IPAddress
    Write-Host "  Selected: $Device" -ForegroundColor Green
}

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 2 — Identify device type and read identity
# ─────────────────────────────────────────────────────────────────────────────

Write-Host "`n[QSS] Connecting to $Device..." -ForegroundColor Cyan

$authParams = @{}
if ($Secure)   { $authParams.Secure   = $true }
if ($Username) { $authParams.Username = $Username }
if ($Password) { $authParams.Password = $Password }

try {
    $verOutput = Invoke-CrestronCommand -Device $Device -Command "VER" @authParams
} catch {
    Write-Error "Cannot connect to $Device — $_"
    exit 1
}

# Device type — order matters: TSW-1070 before TSW (to avoid partial match)
$typePattern = 'TSW-1070|TSW-770|TST-770|CP4N(?![\w-])|CP4(?!N|[\w-])'
$typeMatch   = [Regex]::Match($verOutput, $typePattern, 'IgnoreCase')

if (-not $typeMatch.Success) {
    Write-Warning "Could not auto-detect device type from VER output:`n$verOutput"
    $deviceType = Read-Host "Enter device type (TSW-770 / TST-770 / TSW-1070 / CP4N / CP4)"
} else {
    $deviceType = $typeMatch.Value.ToUpper()
}

# Pull hostname and TSID / serial from VER
$hostnameMatch = [Regex]::Match($verOutput, '(?<=#\s*)([\w\-]+)')
$tsidMatch     = [Regex]::Match($verOutput, '[0-9A-Fa-f]{8}')
$hostname      = if ($hostnameMatch.Success) { $hostnameMatch.Groups[1].Value } else { $Device }
$tsid          = if ($tsidMatch.Success)     { $tsidMatch.Value.ToUpper() }      else { "" }
$serial        = if ($tsid)                 { Convert-TSIDToSerial $tsid }       else { $Device }

Write-Host "  Device type : $deviceType"
Write-Host "  Hostname    : $hostname"
Write-Host "  Serial      : $serial"
Write-Host "  TSID        : $tsid"

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 3 — Load step list for this device type
# ─────────────────────────────────────────────────────────────────────────────

$mapPath = Join-Path $PSScriptRoot "crestron\device_map.json"
if (-not (Test-Path $mapPath)) {
    Write-Error "device_map.json not found at $mapPath"
    exit 1
}

$deviceMap    = Get-Content $mapPath -Raw | ConvertFrom-Json
$deviceConfig = $deviceMap.$deviceType
if ($null -eq $deviceConfig) {
    Write-Error "No step configuration for '$deviceType' in device_map.json"
    exit 1
}

$steps = $deviceConfig.steps
Write-Host "`n[QSS] $($steps.Count) steps for $deviceType" -ForegroundColor Cyan
$steps | ForEach-Object { Write-Host "  → $($_.name)" }

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 4 — Run each step
# ─────────────────────────────────────────────────────────────────────────────

$results   = [System.Collections.Generic.List[PSCustomObject]]::new()
$allPassed = $true
$startTime = Get-Date

foreach ($step in $steps) {
    $stepPath = Join-Path $PSScriptRoot "crestron\$($step.script)"

    if (-not (Test-Path $stepPath)) {
        Write-Warning "Script not found: $stepPath — skipping step '$($step.name)'"
        $results.Add([PSCustomObject]@{
            Step      = $step.name
            Result    = "SKIP"
            Reason    = "Script not found: $($step.script)"
            Notes     = ""
            Duration  = 0
        })
        continue
    }

    Write-Host ("`n[QSS] ── {0} ──" -f $step.name) -ForegroundColor Yellow
    $stepStart = Get-Date

    # Run the step — capture stdout + stderr together
    $stepOutput = & {
        & $stepPath `
            -Device       $Device `
            -Username     $Username `
            -Password     $Password `
            -FirmwarePath $FirmwarePath `
            $(if ($Secure) { "-Secure" })
    } 2>&1 | Out-String

    $stepExitCode = $LASTEXITCODE
    $stepSec      = [math]::Round(($( Get-Date) - $stepStart).TotalSeconds)
    $stepPassed   = ($stepExitCode -eq 0)
    $stepReason   = ""
    $stepNotes    = ""

    Write-Host $stepOutput.Trim()

    # ── Claude verdict ────────────────────────────────────────────────────────
    if (-not $SkipAI) {
        try {
            $aiBody = @{
                Output       = $stepOutput
                SerialNumber = $serial
                StepName     = $step.name
                SessionId    = $SessionId
            } | ConvertTo-Json

            $aiResp     = Invoke-RestMethod `
                -Uri         "$ServerUrl/api/parse-result" `
                -Method      POST `
                -Body        $aiBody `
                -ContentType "application/json" `
                -TimeoutSec  30

            $stepPassed = ($aiResp.result -eq "PASS")
            $stepReason = $aiResp.reason
            $stepNotes  = if ($aiResp.details) { $aiResp.details -join "; " } else { "" }

            $col = if ($stepPassed) { "Green" } else { "Red" }
            Write-Host ("  Claude: {0} — {1}" -f $aiResp.result, $aiResp.reason) -ForegroundColor $col
        } catch {
            Write-Warning "AI verdict unavailable — using exit code ($stepExitCode)"
            $stepReason = "AI unavailable; exit code $stepExitCode"
        }
    } else {
        $stepReason = "SkipAI; exit code $stepExitCode"
    }

    if (-not $stepPassed) { $allPassed = $false }

    $results.Add([PSCustomObject]@{
        Step     = $step.name
        Result   = if ($stepPassed) { "PASS" } else { "FAIL" }
        Reason   = $stepReason
        Notes    = $stepNotes
        Duration = $stepSec
    })
}

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 5 — Save CSV report locally
# ─────────────────────────────────────────────────────────────────────────────

$overall      = if ($allPassed) { "PASS" } else { "FAIL" }
$totalSec     = [math]::Round(((Get-Date) - $startTime).TotalSeconds)
$timestamp    = Get-Date -Format "yyyy-MM-dd_HHmmss"
$csvFileName  = "${deviceType}_${hostname}_${timestamp}.csv"
$csvPath      = Join-Path $ReportDir $csvFileName

# Header row with device info, then one row per step
$csvRows = [System.Collections.Generic.List[PSCustomObject]]::new()
$csvRows.Add([PSCustomObject]@{
    Timestamp    = (Get-Date -Format "o")
    DeviceType   = $deviceType
    Hostname     = $hostname
    SerialNumber = $serial
    TSID         = $tsid
    Step         = "— SUMMARY —"
    Result       = $overall
    Reason       = "Total time: ${totalSec}s"
    Notes        = ""
    DurationSec  = $totalSec
})
foreach ($r in $results) {
    $csvRows.Add([PSCustomObject]@{
        Timestamp    = ""
        DeviceType   = $deviceType
        Hostname     = $hostname
        SerialNumber = $serial
        TSID         = $tsid
        Step         = $r.Step
        Result       = $r.Result
        Reason       = $r.Reason
        Notes        = $r.Notes
        DurationSec  = $r.Duration
    })
}
$csvRows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
Write-Host "`n[QSS] CSV report saved → $csvPath" -ForegroundColor Cyan

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 6 — POST to intake server
# ─────────────────────────────────────────────────────────────────────────────

Write-Host "[QSS] Reporting to server..." -ForegroundColor Cyan

$serverPayload = @{
    DeviceType   = $deviceType
    Hostname     = $hostname
    SerialNumber = $serial
    TSID         = $tsid
    SessionId    = $SessionId
    Timestamp    = (Get-Date -Format "o")
    DurationSec  = $totalSec
    Overall      = $overall
    Steps        = @($results | ForEach-Object {
        @{ Name = $_.Step; Result = $_.Result; Reason = $_.Reason; Notes = $_.Notes; DurationSec = $_.Duration }
    })
} | ConvertTo-Json -Depth 4

try {
    $srvResp = Invoke-RestMethod `
        -Uri         "$ServerUrl/api/crestron-results" `
        -Method      POST `
        -Body        $serverPayload `
        -ContentType "application/json" `
        -TimeoutSec  15

    Write-Host "  Server report: $($srvResp.report_path)" -ForegroundColor Green

    # Mirror result into the main intake session so Phase 3 sticker is correct
    if ($SessionId) {
        $intakePayload = @{
            SerialNumber = $serial
            MacAddress   = ""
            PassFail     = $overall
            Notes        = (($results | Where-Object Result -ne "PASS" | ForEach-Object { $_.Step }) -join "; ")
            SessionId    = $SessionId
            Timestamp    = (Get-Date -Format "o")
        } | ConvertTo-Json

        Invoke-RestMethod `
            -Uri         "$ServerUrl/api/results" `
            -Method      POST `
            -Body        $intakePayload `
            -ContentType "application/json" `
            -TimeoutSec  10 | Out-Null

        Write-Host "  Intake session updated" -ForegroundColor Green
    }
} catch {
    Write-Warning "Could not reach server — result only in CSV: $csvPath"
}

# ─────────────────────────────────────────────────────────────────────────────
#  SECTION 7 — Console summary
# ─────────────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host ("  {0,-14} {1}" -f "Device:",      "$deviceType  ($hostname)")
Write-Host ("  {0,-14} {1}" -f "Serial:",      $serial)
Write-Host ("  {0,-14} {1}" -f "Duration:",    "${totalSec}s")
Write-Host ("  {0,-14} {1}" -f "Report:",      $csvPath)
Write-Host ""
Write-Host "  Steps:"
foreach ($r in $results) {
    $col = switch ($r.Result) { "PASS" { "Green" } "FAIL" { "Red" } default { "Yellow" } }
    $line = ("    {0}  {1}" -f $r.Result.PadRight(5), $r.Step)
    if ($r.Reason) { $line += "  — $($r.Reason)" }
    Write-Host $line -ForegroundColor $col
}
Write-Host ""

if ($allPassed) {
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
    Write-Host "  Failed steps:" -ForegroundColor Red
    $results | Where-Object { $_.Result -ne "PASS" } | ForEach-Object {
        Write-Host "    • $($_.Step): $($_.Reason)" -ForegroundColor Red
    }
}
Write-Host ""
