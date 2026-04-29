"""
QSS Device Intake — Local PC Server
Receives scan sessions from the Meta Quest goggles, generates Excel reports,
and triggers ZPL sticker print jobs.

Run:  python app.py
"""

from flask import Flask, request, jsonify
from flask_cors import CORS
import os, json, uuid, config
from excel_writer import write_session_excel
from print_manager import queue_print_job
from claude_ocr import extract_device_info
from claude_result_parser import parse_ps_output
from crestron_report import write_crestron_excel

app = Flask(__name__)
CORS(app)  # allow requests from the Quest headset on the same LAN

# In-memory session store (swap for SQLite if sessions need to persist across restarts)
_sessions: dict[str, dict] = {}

os.makedirs(config.OUTPUT_DIR,  exist_ok=True)
os.makedirs(config.STICKER_DIR, exist_ok=True)


# ── GET / ────────────────────────────────────────────────────────────────────
@app.route("/", methods=["GET"])
def index():
    """Simple status page — confirms the server is running and lists endpoints."""
    session_count = len(_sessions)
    record_count  = sum(len(s.get("Records", [])) for s in _sessions.values())

    html = f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>QSS Device Intake — Server</title>
  <style>
    body {{ font-family: system-ui, sans-serif; max-width: 640px; margin: 40px auto; padding: 0 20px; color: #e8e8e8; background: #111; }}
    h1   {{ font-size: 1.3rem; color: #fff; }}
    .ok  {{ display: inline-block; background: #1a7a1a; color: #7fff7f; padding: 2px 10px; border-radius: 4px; font-size: .85rem; }}
    table{{ width: 100%; border-collapse: collapse; margin-top: 1.2rem; font-size: .88rem; }}
    th   {{ text-align: left; color: #aaa; border-bottom: 1px solid #333; padding: 4px 8px; }}
    td   {{ padding: 5px 8px; border-bottom: 1px solid #222; }}
    .method {{ color: #7cb8ff; font-family: monospace; }}
    .path   {{ font-family: monospace; color: #c9d; }}
    .stat   {{ margin-top: 1rem; color: #aaa; font-size: .85rem; }}
  </style>
</head>
<body>
  <h1>QSS Device Intake &nbsp;<span class="ok">● running</span></h1>
  <div class="stat">Sessions in memory: <b>{session_count}</b> &nbsp;|&nbsp; Total records: <b>{record_count}</b></div>
  <table>
    <tr><th>Method</th><th>Endpoint</th><th>Called by</th></tr>
    <tr><td class="method">POST</td><td class="path">/api/sessions</td><td>Goggles — upload scan session</td></tr>
    <tr><td class="method">GET</td> <td class="path">/api/sessions</td><td>Browser — list sessions</td></tr>
    <tr><td class="method">GET</td> <td class="path">/api/sessions/&lt;id&gt;/results</td><td>Goggles — fetch Pass/Fail</td></tr>
    <tr><td class="method">PATCH</td><td class="path">/api/sessions/&lt;id&gt;/records/&lt;box&gt;</td><td>Manual — correct a record</td></tr>
    <tr><td class="method">POST</td><td class="path">/api/results</td><td>device_test.ps1 — report result</td></tr>
    <tr><td class="method">POST</td><td class="path">/api/results/fallback</td><td>Manual — import offline CSV</td></tr>
    <tr><td class="method">POST</td><td class="path">/api/ocr</td><td>Goggles — Claude Vision OCR</td></tr>
    <tr><td class="method">POST</td><td class="path">/api/parse-result</td><td>device_test.ps1 — Claude verdict</td></tr>
    <tr><td class="method">POST</td><td class="path">/api/crestron-results</td><td>crestron_runner.ps1 — step-by-step setup result</td></tr>
  </table>
</body>
</html>"""
    return html, 200


# ── POST /api/sessions ───────────────────────────────────────────────────────
@app.route("/api/sessions", methods=["POST"])
def receive_session():
    """
    Goggles POST the full ScanSession JSON here after Phase 1 is complete.
    Server writes the Excel file and queues sticker print jobs.
    """
    data = request.get_json(force=True)
    if not data or "SessionId" not in data:
        return jsonify({"error": "Invalid session payload"}), 400

    session_id = data["SessionId"]
    _sessions[session_id] = data

    # Write Excel
    excel_path = os.path.join(config.OUTPUT_DIR, f"session_{session_id}.xlsx")
    write_session_excel(data, excel_path)

    # Queue a print job per record
    for record in data.get("Records", []):
        job_id = str(uuid.uuid4())[:8].upper()
        record["PrintJobId"] = job_id
        queue_print_job(record, job_id)

    print(f"[Server] Session {session_id} received — "
          f"{len(data.get('Records', []))} records, Excel → {excel_path}")

    return jsonify({"status": "ok", "session_id": session_id}), 201


# ── GET /api/sessions/<id>/results ──────────────────────────────────────────
@app.route("/api/sessions/<session_id>/results", methods=["GET"])
def get_results(session_id):
    """
    Goggles poll this before Phase 3 to get PassFail, RoomName, and PrintJobId
    written back into each DeviceRecord.
    """
    session = _sessions.get(session_id)
    if not session:
        return jsonify({"error": "Session not found"}), 404

    results = [
        {
            "BoxId":      r.get("BoxId"),
            "PassFail":   r.get("PassFail", "PENDING"),
            "RoomName":   r.get("RoomName", ""),
            "PrintJobId": r.get("PrintJobId", ""),
        }
        for r in session.get("Records", [])
    ]

    return jsonify({"SessionId": session_id, "Results": results}), 200


# ── PATCH /api/sessions/<id>/records/<box_id> ────────────────────────────────
@app.route("/api/sessions/<session_id>/records/<box_id>", methods=["PATCH"])
def update_record(session_id, box_id):
    """
    Allow manual updates from the PC (e.g. set PassFail or RoomName after
    results come in from a test script).
    """
    session = _sessions.get(session_id)
    if not session:
        return jsonify({"error": "Session not found"}), 404

    record = next((r for r in session["Records"] if r["BoxId"] == box_id), None)
    if not record:
        return jsonify({"error": "Record not found"}), 404

    updates = request.get_json(force=True)
    allowed = {"PassFail", "RoomName", "StickerApplied"}
    for key, val in updates.items():
        if key in allowed:
            record[key] = val

    return jsonify({"status": "updated", "record": record}), 200


# ── POST /api/results ────────────────────────────────────────────────────────
@app.route("/api/results", methods=["POST"])
def receive_ps_result():
    """
    Called by device_test.ps1 running ON the equipment being tested.
    Matches the incoming serial/MAC to a session record, updates PassFail,
    and reprints the sticker if it hasn't been applied yet.

    Payload (from PowerShell ConvertTo-Json):
      { SerialNumber, MacAddress, PassFail, Notes, SessionId, Timestamp }
    """
    data = request.get_json(force=True)
    if not data or not data.get("SerialNumber"):
        return jsonify({"error": "SerialNumber is required"}), 400

    serial    = data["SerialNumber"].strip()
    mac       = data.get("MacAddress", "").strip().upper()
    pass_fail = data.get("PassFail", "FAIL").upper()
    notes     = data.get("Notes", "")
    hint_sid  = data.get("SessionId", "").strip()  # optional session hint from PS param

    # Search — prefer the hinted session, fall back to all sessions
    search_order = []
    if hint_sid and hint_sid in _sessions:
        search_order = [_sessions[hint_sid]] + [s for sid, s in _sessions.items() if sid != hint_sid]
    else:
        search_order = list(_sessions.values())

    matched_record  = None
    matched_session = None

    for session in search_order:
        for record in session.get("Records", []):
            sn_match  = record.get("SerialNumber", "").strip().upper() == serial.upper()
            mac_match = record.get("MacAddress",   "").strip().upper() == mac
            if sn_match or (mac and mac_match):
                matched_record  = record
                matched_session = session
                break
        if matched_record:
            break

    if not matched_record:
        print(f"[Results] No match for S/N={serial} MAC={mac}")
        return jsonify({"error": "No matching record found", "serial": serial}), 404

    # Update the record
    matched_record["PassFail"] = pass_fail
    if notes:
        matched_record["TestNotes"] = notes

    print(f"[Results] {matched_record['BoxId']} → {pass_fail}  (S/N {serial})")

    # Reprint sticker with updated Pass/Fail if it hasn't been physically applied yet
    if not matched_record.get("StickerApplied", False):
        job_id = str(uuid.uuid4())[:8].upper()
        matched_record["PrintJobId"] = job_id
        queue_print_job(matched_record, job_id)
        print(f"[Results] Reprint queued for {matched_record['BoxId']} — job {job_id}")

    # Regenerate Excel so the file on disk reflects the latest results
    sid        = matched_session["SessionId"]
    excel_path = os.path.join(config.OUTPUT_DIR, f"session_{sid}.xlsx")
    write_session_excel(matched_session, excel_path)

    return jsonify({
        "status":    "updated",
        "SessionId": sid,
        "BoxId":     matched_record["BoxId"],
        "PassFail":  pass_fail,
    }), 200


# ── POST /api/results/fallback ────────────────────────────────────────────────
@app.route("/api/results/fallback", methods=["POST"])
def import_fallback_csv():
    """
    If device_test.ps1 couldn't reach the server during testing, it wrote
    results to fallback_results.csv.  POST that file here to bulk-import.

    Use curl or PowerShell:
      curl -X POST http://localhost:5000/api/results/fallback \
           -F "file=@scripts/fallback_results.csv"
    """
    if "file" not in request.files:
        return jsonify({"error": "No file in request"}), 400

    import csv, io
    file    = request.files["file"]
    content = file.read().decode("utf-8-sig")   # strip BOM from PowerShell CSV
    reader  = csv.DictReader(io.StringIO(content))

    imported = 0
    skipped  = []

    for row in reader:
        # Re-use the main result handler logic by faking a request payload
        serial    = row.get("SerialNumber", "").strip()
        mac       = row.get("MacAddress",   "").strip().upper()
        pass_fail = row.get("PassFail",     "FAIL").upper()
        notes     = row.get("Notes",        "")
        hint_sid  = row.get("SessionId",    "").strip()

        if not serial:
            continue

        matched_record  = None
        matched_session = None
        search_order    = []

        if hint_sid and hint_sid in _sessions:
            search_order = [_sessions[hint_sid]] + [s for sid, s in _sessions.items() if sid != hint_sid]
        else:
            search_order = list(_sessions.values())

        for session in search_order:
            for record in session.get("Records", []):
                if record.get("SerialNumber", "").strip().upper() == serial.upper():
                    matched_record  = record
                    matched_session = session
                    break
            if matched_record:
                break

        if not matched_record:
            skipped.append(serial)
            continue

        matched_record["PassFail"]  = pass_fail
        matched_record["TestNotes"] = notes

        if not matched_record.get("StickerApplied", False):
            job_id = str(uuid.uuid4())[:8].upper()
            matched_record["PrintJobId"] = job_id
            queue_print_job(matched_record, job_id)

        sid        = matched_session["SessionId"]
        excel_path = os.path.join(config.OUTPUT_DIR, f"session_{sid}.xlsx")
        write_session_excel(matched_session, excel_path)

        imported += 1

    print(f"[Results] Fallback import: {imported} matched, {len(skipped)} skipped")
    return jsonify({"imported": imported, "skipped": skipped}), 200


# ── GET /api/sessions ────────────────────────────────────────────────────────
@app.route("/api/sessions", methods=["GET"])
def list_sessions():
    summary = [
        {
            "SessionId":    sid,
            "CreatedAt":    s.get("CreatedAt"),
            "TotalRecords": len(s.get("Records", [])),
        }
        for sid, s in _sessions.items()
    ]
    return jsonify(summary), 200


# ── POST /api/ocr ─────────────────────────────────────────────────────────────
@app.route("/api/ocr", methods=["POST"])
def ocr_image():
    """
    Called by the Meta Quest headset during Phase 1 scanning.
    Receives a base64-encoded camera frame and returns the extracted
    serial number and MAC address via Claude Vision.

    Request JSON:
      { "ImageBase64": "<base64 string>", "MediaType": "image/jpeg" }

    Response JSON:
      { "serial": "...", "mac": "...", "confidence": "high|medium|low", "raw": "..." }
      or on error:
      { "error": "..." }
    """
    if not config.ANTHROPIC_API_KEY:
        return jsonify({"error": "ANTHROPIC_API_KEY not configured on server"}), 503

    data = request.get_json(force=True)
    if not data or "ImageBase64" not in data:
        return jsonify({"error": "ImageBase64 field is required"}), 400

    image_b64  = data["ImageBase64"]
    media_type = data.get("MediaType", "image/jpeg")

    result = extract_device_info(image_b64, media_type)

    if "error" in result:
        print(f"[OCR] Error: {result['error']}")
        return jsonify(result), 502

    print(f"[OCR] S/N={result.get('serial','?')}  MAC={result.get('mac','?')}  "
          f"confidence={result.get('confidence','?')}")
    return jsonify(result), 200


# ── POST /api/crestron-results ───────────────────────────────────────────────
@app.route("/api/crestron-results", methods=["POST"])
def receive_crestron_result():
    """
    Called by crestron_runner.ps1 after completing all setup steps on a device.

    Payload:
      {
        DeviceType, Hostname, SerialNumber, TSID, SessionId,
        Timestamp, DurationSec, Overall,
        Steps: [{ Name, Result, Reason, Notes, DurationSec }, ...]
      }

    Writes / appends to the daily Crestron setup Excel report.
    If a SessionId is provided and the device serial matches a session record,
    that record's PassFail is updated so Phase 3 sticker reflects setup status.
    """
    data = request.get_json(force=True)
    if not data or "DeviceType" not in data:
        return jsonify({"error": "DeviceType is required"}), 400

    # Write Excel report
    excel_path = write_crestron_excel(data, config.OUTPUT_DIR)

    # Mirror result into the main intake session record (optional)
    session_id = data.get("SessionId", "").strip()
    serial     = data.get("SerialNumber", "").strip()
    overall    = data.get("Overall", "FAIL").upper()

    matched_box = None
    if session_id and serial and session_id in _sessions:
        session = _sessions[session_id]
        for record in session.get("Records", []):
            if record.get("SerialNumber", "").strip().upper() == serial.upper():
                record["PassFail"] = overall
                failed = [s["Name"] for s in data.get("Steps", []) if s.get("Result", "").upper() != "PASS"]
                if failed:
                    record["TestNotes"] = "Failed steps: " + "; ".join(failed)
                matched_box = record.get("BoxId")
                # Reprint sticker if not yet applied
                if not record.get("StickerApplied", False):
                    job_id = str(uuid.uuid4())[:8].upper()
                    record["PrintJobId"] = job_id
                    queue_print_job(record, job_id)
                # Regenerate main session Excel
                sid_path = os.path.join(config.OUTPUT_DIR, f"session_{session_id}.xlsx")
                write_session_excel(session, sid_path)
                break

    print(f"[Crestron] {data.get('DeviceType')} {data.get('Hostname')} → {overall}"
          f"  ({len(data.get('Steps', []))} steps)  Excel → {excel_path}")

    return jsonify({
        "status":      "ok",
        "overall":     overall,
        "report_path": excel_path,
        "matched_box": matched_box,
    }), 201


# ── POST /api/parse-result ────────────────────────────────────────────────────
@app.route("/api/parse-result", methods=["POST"])
def parse_result():
    """
    Called by device_test.ps1 after running Section 2 test logic.
    Receives raw PowerShell stdout and uses Claude to determine
    PASS or FAIL with a structured reason.

    Request JSON:
      { "Output": "<raw PS stdout>", "SerialNumber": "...", "SessionId": "..." }

    Response JSON:
      { "result": "PASS|FAIL", "reason": "...", "details": [...] }
    """
    if not config.ANTHROPIC_API_KEY:
        return jsonify({"error": "ANTHROPIC_API_KEY not configured on server"}), 503

    data = request.get_json(force=True)
    if not data or "Output" not in data:
        return jsonify({"error": "Output field is required"}), 400

    raw_output = data["Output"]
    serial     = data.get("SerialNumber", "unknown")

    verdict = parse_ps_output(raw_output)

    print(f"[ParseResult] S/N={serial}  →  {verdict.get('result','?')}  "
          f"({verdict.get('reason','')[:60]})")

    return jsonify(verdict), 200


if __name__ == "__main__":
    print(f"[Server] Starting on http://{config.HOST}:{config.PORT}")
    app.run(host=config.HOST, port=config.PORT, debug=True)
