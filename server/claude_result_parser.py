"""
QSS Device Intake — Claude PS Output Parser
Receives the raw stdout of device_test.ps1 and uses Claude
to determine whether the device passed or failed, extracting
a structured reason and any notable details.

Usage:
    from claude_result_parser import parse_ps_output
    result = parse_ps_output(raw_stdout)
    # result → {"result": "PASS", "reason": "All checks passed", "details": [...]}
"""

import anthropic
import config

_client = anthropic.Anthropic(api_key=config.ANTHROPIC_API_KEY)

# ── System prompt (cached) ────────────────────────────────────────────────────
_SYSTEM_PROMPT = """\
You are a QA analyst that interprets PowerShell device test output.
Your job is to read the raw console output from a device acceptance test and
determine whether the device passed or failed.

Rules:
- Return ONLY valid JSON — no markdown, no explanation, no preamble.
- Look for explicit PASS/FAIL indicators, error messages, warnings, and exceptions.
- If the output is ambiguous or empty, default to "FAIL" with reason "Inconclusive output".
- Keep "reason" to one short sentence (under 100 characters).
- Put actionable specifics (error codes, service names, etc.) in "details" as an array of strings.

Response schema (always):
{
  "result": "<PASS|FAIL>",
  "reason": "<one-sentence summary>",
  "details": ["<detail 1>", "<detail 2>"]
}
"""

_USER_PROMPT_TEMPLATE = "Analyse this device test output and return the JSON verdict:\n\n```\n{output}\n```"


def parse_ps_output(raw_output: str) -> dict:
    """
    Send raw PowerShell test stdout to Claude and get a structured verdict.

    Args:
        raw_output: Full stdout captured from device_test.ps1 Section 2 execution.

    Returns:
        dict with keys: result ("PASS" or "FAIL"), reason, details
        On any error the dict contains "result": "FAIL" and an "error" key.
    """
    import json

    if not raw_output or not raw_output.strip():
        return {"result": "FAIL", "reason": "No test output received", "details": []}

    # Truncate extremely long output to avoid large token costs
    truncated = raw_output[:8000]
    if len(raw_output) > 8000:
        truncated += "\n[... output truncated ...]"

    try:
        response = _client.messages.create(
            model="claude-opus-4-7",
            max_tokens=512,
            system=[
                {
                    "type": "text",
                    "text": _SYSTEM_PROMPT,
                    "cache_control": {"type": "ephemeral"},
                }
            ],
            messages=[
                {
                    "role": "user",
                    "content": _USER_PROMPT_TEMPLATE.format(output=truncated),
                }
            ],
        )

        raw_text = response.content[0].text.strip()

        # Strip accidental markdown code fences
        if raw_text.startswith("```"):
            raw_text = raw_text.split("```")[1]
            if raw_text.startswith("json"):
                raw_text = raw_text[4:]

        result = json.loads(raw_text)

        # Normalise result field to uppercase PASS/FAIL
        result["result"] = result.get("result", "FAIL").upper()
        if result["result"] not in ("PASS", "FAIL"):
            result["result"] = "FAIL"

        return result

    except json.JSONDecodeError as exc:
        return {
            "result": "FAIL",
            "reason": "Could not parse Claude response",
            "details": [f"JSON error: {exc}"],
            "error": str(exc),
        }
    except anthropic.APIError as exc:
        return {
            "result": "FAIL",
            "reason": "Claude API unavailable — manual review required",
            "details": [str(exc)],
            "error": str(exc),
        }
    except Exception as exc:
        return {
            "result": "FAIL",
            "reason": "Unexpected parser error",
            "details": [str(exc)],
            "error": str(exc),
        }
