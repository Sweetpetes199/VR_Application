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

app = Flask(__name__)
CORS(app)  # allow requests from the Quest headset on the same LAN

# In-memory session store (swap for SQLite if sessions need to persist across restarts)
_sessions: dict[str, dict] = {}

os.makedirs(config.OUTPUT_DIR,  exist_ok=True)
os.makedirs(config.STICKER_DIR, exist_ok=True)


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


if __name__ == "__main__":
    print(f"[Server] Starting on http://{config.HOST}:{config.PORT}")
    app.run(host=config.HOST, port=config.PORT, debug=True)
