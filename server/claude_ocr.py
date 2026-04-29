"""
QSS Device Intake — Claude Vision OCR
Receives a base64-encoded camera frame from the Meta Quest,
calls Claude Vision (claude-opus-4-7) to extract serial number
and MAC address, and returns structured JSON.

Usage:
    from claude_ocr import extract_device_info
    result = extract_device_info(base64_jpeg, media_type="image/jpeg")
    # result → {"serial": "SN-ABC123", "mac": "AA:BB:CC:DD:EE:FF", "confidence": "high", "raw": "..."}
"""

import anthropic
import base64
import config

# Initialise client once — API key read from ANTHROPIC_API_KEY env var via config
_client = anthropic.Anthropic(api_key=config.ANTHROPIC_API_KEY)

# ── System prompt (cached) ────────────────────────────────────────────────────
# This prompt never changes between requests so we pin a cache breakpoint on it.
_SYSTEM_PROMPT = """\
You are a precise OCR assistant that reads device labels on shipping boxes.
Your only job is to extract the Serial Number and MAC Address from the image.

Rules:
- Return ONLY valid JSON — no markdown, no explanation, no preamble.
- If you cannot read a value with confidence, use an empty string "" for that field.
- Normalise the MAC address to uppercase colon-separated format: AA:BB:CC:DD:EE:FF
- Strip any leading/trailing whitespace from all values.
- Set "confidence" to "high", "medium", or "low" based on image clarity.

Response schema (always):
{
  "serial": "<serial number or empty string>",
  "mac": "<MAC address AA:BB:CC:DD:EE:FF or empty string>",
  "confidence": "<high|medium|low>",
  "raw": "<all text you could read from the label, verbatim>"
}
"""

_USER_PROMPT = "Extract the serial number and MAC address from this device label."


def extract_device_info(image_base64: str, media_type: str = "image/jpeg") -> dict:
    """
    Send a camera frame to Claude Vision and extract serial + MAC.

    Args:
        image_base64: Base64-encoded image bytes (no data-URI prefix).
        media_type:   MIME type — "image/jpeg" or "image/png".

    Returns:
        dict with keys: serial, mac, confidence, raw
        On any error the dict contains an "error" key instead.
    """
    import json

    try:
        response = _client.messages.create(
            model="claude-opus-4-7",
            max_tokens=256,
            system=[
                {
                    "type": "text",
                    "text": _SYSTEM_PROMPT,
                    "cache_control": {"type": "ephemeral"},   # cache the static prompt
                }
            ],
            messages=[
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "image",
                            "source": {
                                "type": "base64",
                                "media_type": media_type,
                                "data": image_base64,
                            },
                        },
                        {
                            "type": "text",
                            "text": _USER_PROMPT,
                        },
                    ],
                }
            ],
        )

        raw_text = response.content[0].text.strip()

        # Strip accidental markdown code fences if Claude adds them
        if raw_text.startswith("```"):
            raw_text = raw_text.split("```")[1]
            if raw_text.startswith("json"):
                raw_text = raw_text[4:]

        result = json.loads(raw_text)
        return result

    except json.JSONDecodeError as exc:
        return {"error": f"JSON parse error: {exc}", "raw": raw_text if "raw_text" in dir() else ""}
    except anthropic.APIError as exc:
        return {"error": f"Anthropic API error: {exc}"}
    except Exception as exc:
        return {"error": f"Unexpected error: {exc}"}
