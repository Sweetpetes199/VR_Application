# VR Device Intake — Meta Quest AR Tool

A Meta Quest mixed-reality application that replaces manual serial number / MAC address
data entry during bulk device deployments. Built for QSS Specialists internal use.

---

## The Problem

Deploying 300+ devices means:
- Manually reading serial numbers and MAC addresses off box labels
- Copying them into a spreadsheet
- Running a PowerShell test on each device
- Printing stickers and hunting through a stack of boxes to find the right one

That process takes **2–4 hours** and is error-prone.

---

## The Solution

| Phase | What happens |
|---|---|
| **1 — Scan** | Technician walks the stack wearing the Quest. Camera reads each label via OCR. A spatial anchor is saved at each box location. Session uploads to the PC server automatically. |
| **2 — Test** | PowerShell script runs on each booted device. It reads its own serial/MAC from Windows, runs your test logic, and reports PASS/FAIL directly to the server. Stickers print automatically. |
| **3 — Apply** | Technician puts the goggles back on. AR arrows guide them box-by-box to place each printed sticker. No hunting. |

Total time for 300 boxes: **~20 minutes**.

---

## Repository Structure

```
VR_Application/
├── unity-app/
│   └── Assets/Scripts/
│       ├── Data/
│       │   ├── DeviceRecord.cs          # Data model — one record per box
│       │   └── ScanSession.cs           # Session collection + sticker queue
│       ├── Scanning/
│       │   └── OCRScanController.cs     # ML Kit OCR loop + stable-hold confirm
│       ├── Anchors/
│       │   └── SpatialAnchorController.cs  # Place / save / reload Meta anchors
│       ├── Navigation/
│       │   ├── NavigationController.cs  # Phase 3 sticker queue + confirm flow
│       │   └── WaypointIndicator.cs     # Floating AR arrow + distance label
│       ├── Sync/
│       │   └── DataSyncManager.cs       # POST session up / GET results back
│       ├── ScanPhaseManager.cs          # Phase 1 scene orchestrator
│       └── ApplyPhaseManager.cs         # Phase 3 scene orchestrator
├── server/
│   ├── app.py                           # Flask REST API
│   ├── excel_writer.py                  # Generates formatted .xlsx report
│   ├── print_manager.py                 # Builds ZPL + sends to Zebra printer
│   ├── config.py                        # Reads .env settings
│   ├── requirements.txt                 # Python dependencies
│   ├── .env.example                     # Config template — copy to .env
│   └── SETUP.md                         # Full server setup walkthrough
├── scripts/
│   └── device_test.ps1                  # PowerShell test runner (runs on device)
└── README.md
```

---

## Quick Start

### Server (PC)

```bash
cd server
copy .env.example .env        # edit with your printer name and IP
pip install -r requirements.txt
python app.py
```

Full walkthrough: [`server/SETUP.md`](server/SETUP.md)

### Unity (Meta Quest)

1. Open Unity Hub → Add project from `unity-app/`
2. Install required packages (see [Programmer Notes](PROGRAMMER_NOTES.md))
3. Open `ScanPhase` scene → wire Inspector references on `ScanPhaseManager`
4. Open `ApplyPhase` scene → wire Inspector references on `ApplyPhaseManager`
5. Set server IP in `DataSyncManager._serverBaseUrl`
6. Build → Android → Deploy to Quest

### PowerShell test script

```powershell
# Run on each device being deployed — after it boots, before stickers go on
.\scripts\device_test.ps1 -ServerUrl http://192.168.1.100:5000
```

Add your test logic to **Section 2** in `device_test.ps1`.

---

## System Requirements

| Component | Requirement |
|---|---|
| Headset | Meta Quest 3 / 3S |
| Unity | 2022 LTS |
| Meta XR SDK | Core SDK via Package Manager |
| Python | 3.11+ |
| Label printer | Any Zebra ZPL-capable printer |
| Network | PC and Quest on the same WiFi / LAN |

---

## Server API — Quick Reference

| Method | Endpoint | Called by |
|---|---|---|
| `POST` | `/api/sessions` | Goggles — upload completed scan session |
| `GET` | `/api/sessions/<id>/results` | Goggles — fetch Pass/Fail before Phase 3 |
| `POST` | `/api/results` | `device_test.ps1` — report test result |
| `POST` | `/api/results/fallback` | Manual — import offline CSV |
| `PATCH` | `/api/sessions/<id>/records/<box>` | Manual — correct a record |
| `GET` | `/api/sessions` | Browser — list all sessions |
| `POST` | `/api/ocr` | Goggles — Claude Vision OCR on a camera frame |
| `POST` | `/api/parse-result` | `device_test.ps1` — Claude interprets PS output |

---

## Built By

QSS Specialists — Internal Tools Division
