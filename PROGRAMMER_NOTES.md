# Programmer Notes — VR Device Intake

Technical reference for QSS specialists integrating or extending this project.

---

## 1. Unity Project Setup (From Scratch)

### Create the project
1. Unity Hub → **New Project** → **3D (URP)** → Unity 2022 LTS
2. Point it at `unity-app/` in this repo (or copy the `Assets/Scripts/` folder into an existing project)

### Required packages — install via Package Manager

| Package | Where to get it |
|---|---|
| **Meta XR Core SDK** | [developer.oculus.com/downloads](https://developer.oculus.com/downloads) or Unity Asset Store |
| **Meta XR Interaction SDK** | Same — bundled with Core SDK |
| **Google ML Kit Text Recognition** | [External Dependency Manager](https://github.com/googlesamples/unity-jar-resolver) — add `com.google.mlkit:text-recognition:16.0.0` to `mainTemplate.gradle` |
| **TextMeshPro** | Package Manager → Unity Registry (usually pre-installed) |

### Android build settings
- File → Build Settings → Android
- Switch Platform
- Player Settings:
  - **Minimum API Level**: Android 10 (API 29)
  - **Target API Level**: Android 12L (API 32)
  - **Scripting Backend**: IL2CPP
  - **Target Architectures**: ARM64 only

### Meta Quest settings (OVR)
- Add `OVRCameraRig` prefab to both scenes
- In `OVRManager` component:
  - **Target Device**: Quest 3
  - **Tracking Origin Type**: Floor Level
  - **Passthrough Support**: Required
  - **Spatial Anchor Support**: Required

---

## 2. Scene Setup — Inspector Wiring

### ScanPhase.unity

Create a persistent `GameObject` named **ScanPhaseManager** and add these components:

```
ScanPhaseManager
  ├── OCRScanController        (on a child GO)
  ├── SpatialAnchorController  (on a child GO)
  └── DataSyncManager          (on a child GO)
```

**ScanPhaseManager Inspector slots:**

| Slot | What to assign |
|---|---|
| `_ocr` | `OCRScanController` component |
| `_anchors` | `SpatialAnchorController` component |
| `_sync` | `DataSyncManager` component |
| `_panelIdle` | Canvas panel shown before scanning starts |
| `_panelScanning` | Canvas panel shown during active scanning |
| `_panelConfirm` | Canvas panel shown when OCR finds a match |
| `_panelUploading` | Canvas panel shown during server upload |
| `_panelComplete` | Canvas panel shown when session is fully uploaded |
| `_confirmSerialText` | TMP label inside `_panelConfirm` |
| `_confirmMacText` | TMP label inside `_panelConfirm` |
| `_btnConfirmAccept` | "Yes, that's correct" button |
| `_btnConfirmRescan` | "Rescan this box" button |
| `_scanCounterText` | TMP label showing "X scanned" |
| `_scanStatusText` | TMP label showing current status message |
| `_btnDoneScanning` | "Finished scanning all boxes" button |
| `_uploadStatusText` | TMP label inside `_panelUploading` |

Wire `_btnDoneScanning.onClick` → `ScanPhaseManager.OnDoneScanningPressed()`
Wire `Start Scanning` button → `ScanPhaseManager.OnStartPressed()`

---

### ApplyPhase.unity

Same pattern — persistent `GameObject` named **ApplyPhaseManager**:

```
ApplyPhaseManager
  ├── NavigationController     (on a child GO)
  ├── SpatialAnchorController  (on a child GO, separate instance)
  └── DataSyncManager          (on a child GO)
```

**ApplyPhaseManager Inspector slots:**

| Slot | What to assign |
|---|---|
| `_navigation` | `NavigationController` component |
| `_anchors` | `SpatialAnchorController` component |
| `_sync` | `DataSyncManager` component |
| `_panelIdle` | Entry panel — "Begin Sticker Run" button lives here |
| `_panelLoading` | Shown during result fetch + anchor load |
| `_panelNavigating` | Shown while walking to a box |
| `_panelConfirm` | Shown when standing at target box |
| `_panelComplete` | Final summary panel |
| `_loadingStatusText` | TMP label for load progress messages |
| `_navProgressText` | TMP label "X / Y stickered" |
| `_navBoxInfoText` | TMP label with current box details |
| `_confirmBoxIdText` | TMP label showing BoxId on confirm panel |
| `_confirmDetailsText` | TMP label with room/serial/MAC/result |
| `_btnStickerPlaced` | "Sticker is on" confirm button |
| `_btnSkipBox` | "Skip this one" button |
| `_completeStatsText` | TMP label on complete panel |

---

## 3. OCR — Tuning for Your Labels

`OCRScanController.cs` has two regex patterns near the top. Adjust them to match the
exact format printed on your box labels.

```csharp
// Current patterns — edit as needed:
private static readonly Regex _serialPattern =
    new(@"S/N[:\s]*([A-Z0-9\-]{6,30})", RegexOptions.IgnoreCase);

private static readonly Regex _macPattern =
    new(@"([0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}");
```

**Testing OCR patterns without a headset:**
The controller has an editor stub that fires `SimulateEditorScan()` every 0.5s.
Paste a sample label string there to test your regex in Play mode on PC.

```csharp
// In RunOCRFrame(), UNITY_EDITOR path:
SimulateEditorScan("S/N: YOUR-ACTUAL-LABEL-FORMAT\nMAC: AA:BB:CC:DD:EE:FF");
```

**Stable-hold timer:**
OCR must return the same serial+MAC for `_confirmationHoldSeconds` (default 1.5s)
before firing `OnDeviceConfirmed`. Increase this if you're getting false positives,
decrease it for faster scanning.

---

## 4. Spatial Anchors — Key Behaviours

- Anchors are **saved to local device storage** (`OVRSpatialAnchor.StorageLocation.Local`)
- They persist across app restarts as long as the physical environment hasn't changed significantly
- The anchor is placed **1.5m in front of the user's gaze** at the moment of confirmation
  — the technician should be standing directly in front of the box when they confirm
- If anchor creation times out (5 second default), the record is removed and the user
  is sent back to re-confirm — this is intentional to avoid orphaned records
- Anchor UUIDs are stored in `DeviceRecord.AnchorUuid` and saved locally via `DataSyncManager`

**If anchors fail to reload in Phase 3:**
- Check that Phase 3 is run in the same physical space as Phase 1
- Quest spatial anchors degrade if the room lighting changes dramatically
- As a fallback, `BoxId` is printed on every sticker so boxes can be found manually

---

## 5. DataSyncManager — Server URL

The server URL is a serialized field — set it in the Inspector, not in code.
This means a QSS specialist can change the IP without touching any script.

```
DataSyncManager component → _serverBaseUrl → http://192.168.1.100:5000
```

**Session persistence:**
The session is saved to `PlayerPrefs` after every confirmed scan and after every
sticker is applied. If the app crashes mid-run, the session survives.
`ClearLocalSession()` is called automatically after a successful upload.

---

## 6. PowerShell Test Script — Adding Your Tests

Open `scripts/device_test.ps1` and find **SECTION 2**. Everything before and after
it is infrastructure — only this block needs editing:

```powershell
# ── YOUR TEST LOGIC ──────────────────────────────────────────────
$testPassed = $true    # flip to $false on any failure
$testNotes  = @()      # add failure reasons here for the Excel report

# Call your existing test script:
# & "C:\Path\To\Your\Existing\test.ps1"
# if ($LASTEXITCODE -ne 0) { $testPassed = $false; $testNotes += "Test script failed" }
```

The script automatically:
- Reads serial from `Win32_BIOS.SerialNumber`
- Reads MAC from the first active physical network adapter
- Formats MAC as `AA:BB:CC:DD:EE:FF`
- POSTs to `/api/results` on the server
- Falls back to `fallback_results.csv` if the server is unreachable
- Prints a large PASS (green) or FAIL (red) banner to the console

---

## 7. Sticker Layout — ZPL Customisation

Stickers are generated in `server/print_manager.py` → `build_zpl()`.
Default layout targets a **2" × 1" label** (406 dots wide, 203 dots tall at 203 DPI).

```python
# Key ZPL coordinates to adjust for different label sizes:
^LL203          # label length in dots (203 = 1 inch at 203dpi)
^PW406          # print width in dots  (406 = 2 inches at 203dpi)
```

To test ZPL without a printer, paste the contents of any `.zpl` file from
`server/stickers/` into the online viewer at **labelary.com/viewer.html**.

**Fields on the sticker:**
- Box ID (top left) — e.g. `BOX-042`
- PASS / FAIL badge (top right, reverse-video block)
- Room Name
- Serial Number
- MAC Address
- Session ID (bottom, small)

---

## 8. Server — Adding Persistence

The current server keeps sessions in memory — they are lost on restart.
For jobs that span multiple days, replace the in-memory dict with SQLite:

```python
# app.py — swap _sessions dict for:
import sqlite3
# See: https://flask.palletsprojects.com/en/3.0.x/patterns/sqlite3/
```

A SQLite implementation is on the backlog but not yet built.

---

## 9. Known Limitations

| Limitation | Workaround |
|---|---|
| Boxes moved after Phase 1 | Box ID is on sticker — find manually if anchor is wrong |
| OCR struggles with small/worn labels | Increase `_confirmationHoldSeconds`; use editor stub to test patterns |
| Spatial anchors degrade across days | Run Phase 1 and Phase 3 same day where possible |
| Server loses sessions on restart | Promote to SQLite (see section 8) |
| One Zebra printer only | Call `queue_print_job()` in a loop for multiple printers |
| WMI serial blank on some hardware | PS script will warn and abort — enter serial manually via PATCH endpoint |

---

## 10. Backlog / Future Work

- [ ] SQLite session persistence on server
- [ ] Web dashboard showing live PASS/FAIL count as PS script runs
- [ ] QR code on sticker that links to the device record
- [ ] Multi-printer support (round-robin by room)
- [ ] Unity scene transitions (ScanPhase → ApplyPhase within same app)
- [ ] Proximity trigger — auto-show confirm panel when user is within 0.5m of target anchor
- [ ] Export to CSV as well as Excel
