"""
Generates ZPL sticker files and sends them to a Zebra label printer.
Sticker layout  (2" x 1" label — adjust ^LL and coordinates for your media):

  ┌────────────────────────────┐
  │  QSS    BOX-001   [PASS]   │
  │  Room: SERVER-ROOM-A       │
  │  S/N: SN-ABC123-XYZ        │
  │  MAC: AA:BB:CC:DD:EE:FF    │
  └────────────────────────────┘

Requires: zebra package  (pip install zebra)
Printer must be installed in Windows as a shared printer — name set in config.py
"""

import os
import config

try:
    import zebra
    _ZEBRA_AVAILABLE = True
except ImportError:
    _ZEBRA_AVAILABLE = False
    print("[Print] zebra package not installed — ZPL files will be written but not sent to printer.")


def build_zpl(record: dict) -> str:
    """Build a ZPL II label string for one DeviceRecord."""
    box_id     = record.get("BoxId", "???")
    room       = record.get("RoomName", "UNASSIGNED")[:24]   # truncate for label width
    serial     = record.get("SerialNumber", "")
    mac        = record.get("MacAddress", "")
    pass_fail  = record.get("PassFail", "PENDING").upper()
    session_id = record.get("SessionId", "")

    # Colour inversion for PASS (white-on-black badge) vs FAIL (normal)
    pf_invert  = "B" if pass_fail == "PASS" else "N"  # B = reverse video, N = normal

    zpl = f"""^XA
^CI28
^LH0,0
^LL203
^PW406

^FO10,10^A0N,22,22^FD{box_id}^FS
^FO280,10^FR^A0N,22,22^FB120,1,,C^FD{pass_fail}^FS

^FO10,42^A0N,18,18^FDRoom: {room}^FS

^FO10,70^A0N,16,16^FDS/N:^FS
^FO55,70^A0N,16,16^FD{serial}^FS

^FO10,94^A0N,16,16^FDMAC:^FS
^FO55,94^A0N,16,16^FD{mac}^FS

^FO10,122^GB386,1,1^FS

^FO10,132^A0N,14,14^FDQSS Device Intake  |  {session_id}^FS

^PQ1
^XZ"""
    return zpl


def queue_print_job(record: dict, job_id: str) -> None:
    """Write ZPL to disk and send to printer if available."""
    zpl = build_zpl(record)

    # Always save a copy so you can reprint manually
    zpl_path = os.path.join(config.STICKER_DIR, f"{record.get('BoxId','UNK')}_{job_id}.zpl")
    with open(zpl_path, "w") as f:
        f.write(zpl)

    if _ZEBRA_AVAILABLE and config.PRINTER_NAME:
        try:
            z = zebra.Zebra(config.PRINTER_NAME)
            z.output(zpl)
            print(f"[Print] Sent {record.get('BoxId')} → {config.PRINTER_NAME}")
        except Exception as e:
            print(f"[Print] ERROR sending {record.get('BoxId')}: {e}  (ZPL saved to {zpl_path})")
    else:
        print(f"[Print] ZPL saved → {zpl_path}  (no printer configured)")


def reprint_by_box_id(session: dict, box_id: str) -> bool:
    """Find a record by BoxId and reprint its sticker. Returns True on success."""
    record = next(
        (r for r in session.get("Records", []) if r.get("BoxId") == box_id),
        None
    )
    if not record:
        print(f"[Print] Reprint failed — BoxId {box_id} not found in session")
        return False

    job_id = record.get("PrintJobId", "REPRINT")
    queue_print_job(record, job_id)
    return True
