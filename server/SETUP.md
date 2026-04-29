# QSS Device Intake — Server Setup Guide

The server runs on a Windows PC on the same WiFi network as the Meta Quest headset.
It takes ~5 minutes to get running from scratch.

---

## Prerequisites

| Requirement | Minimum | Check |
|---|---|---|
| Python | 3.11+ | `python --version` |
| pip | bundled with Python | `pip --version` |
| Zebra label printer | Any ZPL-capable model | Installed in Windows |
| WiFi | PC and Quest on **same network** | — |

> **No Python?** Download from https://www.python.org/downloads/  
> Tick **"Add Python to PATH"** during install.

---

## 1 — Configure your environment

Inside the `server/` folder, copy the example env file:

```
copy .env.example .env
```

Open `.env` in Notepad and update two things:

### Printer name
1. Open **Start → Settings → Bluetooth & devices → Printers & scanners**
2. Click your Zebra printer
3. Copy the name **exactly** (spaces and capitalisation matter)
4. Paste it as `PRINTER_NAME=` in `.env`

### Output folders (optional)
`OUTPUT_DIR` and `STICKER_DIR` default to subfolders inside `server/`.  
Change them to full paths if you want files saved elsewhere, e.g.:
```
OUTPUT_DIR=C:\QSS\DeviceIntake\Excel
STICKER_DIR=C:\QSS\DeviceIntake\Stickers
```

---

## 2 — Install Python dependencies

Open a terminal inside the `server/` folder and run:

```
pip install -r requirements.txt
```

This installs: Flask, flask-cors, openpyxl, zebra, python-dotenv.

---

## 3 — Find your PC's local IP address

The Quest headset needs to know this address to talk to the server.

```
ipconfig
```

Look for **IPv4 Address** under your WiFi adapter — it will look like `192.168.x.x`.

> **Keep this stable:** Set a DHCP reservation on your router for this PC,
> or use a static IP, so the address doesn't change between jobs.

---

## 4 — Update the Unity project

Open `unity-app/Assets/Scripts/Sync/DataSyncManager.cs` and set `_serverBaseUrl`
to match your PC's IP and port:

```csharp
[SerializeField] private string _serverBaseUrl = "http://192.168.1.100:5000";
```

Or set it in the Unity Inspector on the `DataSyncManager` component — no recompile needed.

---

## 5 — Start the server

```
python app.py
```

You should see:
```
[Server] Starting on http://0.0.0.0:5000
 * Running on http://192.168.1.100:5000
```

Leave this terminal open while using the goggles.

---

## 6 — Verify it's working

From any browser on the same network, open:

```
http://<YOUR-PC-IP>:5000/api/sessions
```

You should get back: `[]`  — an empty list. That means the server is reachable.

To test from the PC itself:

```
curl http://localhost:5000/api/sessions
```

---

## 7 — Firewall (if the Quest can't connect)

Windows Firewall may block port 5000 from other devices on the network.

Quick fix:
1. **Start → Windows Defender Firewall → Advanced Settings**
2. **Inbound Rules → New Rule**
3. **Port → TCP → 5000 → Allow the connection → All profiles**
4. Name it `QSS Device Intake`

Or from an admin terminal:
```
netsh advfirewall firewall add rule name="QSS Device Intake" protocol=TCP dir=in localport=5000 action=allow
```

---

## Daily Workflow

```
1. Plug in Zebra printer, confirm it shows Ready.
2. Open terminal → server\ → python app.py
3. Put on Quest — start Scan phase.
4. After scan, goggles auto-upload session to server.
   Server prints stickers automatically.
5. Switch Quest to Apply phase — follow AR arrows to each box.
6. Excel report saved to OUTPUT_DIR when session is uploaded.
```

---

## Reprint a single sticker

If a sticker tears or is misplaced, use the PATCH endpoint from any browser or curl:

```
curl -X PATCH http://localhost:5000/api/sessions/<SESSION_ID>/records/<BOX_ID> \
     -H "Content-Type: application/json" \
     -d "{\"StickerApplied\": false}"
```

Then re-trigger the print job from the Quest Apply phase, or call `reprint_by_box_id()`
directly in a Python shell:

```python
from print_manager import reprint_by_box_id
reprint_by_box_id(session_dict, "BOX-042")
```

---

## Folder structure after first run

```
server/
├── output/
│   └── session_AB12CD34.xlsx     ← Excel report per session
├── stickers/
│   └── BOX-001_F3A1.zpl          ← ZPL file per box (for reprints)
├── .env                          ← your local config (gitignored)
└── ...
```
