#!/usr/bin/env python3

"""Easydict sidecar mock service.

Protocol: JSON Lines over stdio.
- stdin: one JSON object per line (request)
- stdout: one JSON object per line (response/event) (protocol only)
- stderr: one JSON object per line (structured logs)

This is a cross-platform mock implementation used to validate the IPC contract
before the Swift-on-Windows service is available.
"""

from __future__ import annotations

import json
import sys
import time
from datetime import datetime, timezone
from typing import Any, Dict, Optional


def _utc_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def log(level: str, msg: str, request_id: Optional[str] = None, subsystem: str = "sidecar") -> None:
    payload: Dict[str, Any] = {
        "level": level,
        "ts": _utc_iso(),
        "msg": msg,
        "subsystem": subsystem,
    }
    if request_id is not None:
        payload["requestId"] = request_id
    sys.stderr.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stderr.flush()


def respond(obj: Dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def error_response(request_id: Optional[str], code: str, message: str) -> Dict[str, Any]:
    return {
        "id": request_id,
        "error": {
            "code": code,
            "message": message,
        },
    }


def handle_health(request_id: Optional[str]) -> None:
    respond(
        {
            "id": request_id,
            "result": {
                "version": "0.0.1-mock",
                "build": "dev",
                "capabilities": ["health", "translate", "shutdown"],
            },
        }
    )


def handle_translate(request_id: Optional[str], params: Dict[str, Any]) -> None:
    start = time.perf_counter()

    text = params.get("text")
    to_lang = params.get("toLang")
    if not isinstance(text, str) or text == "":
        respond(error_response(request_id, "invalid_params", "params.text must be a non-empty string"))
        return
    if not isinstance(to_lang, str) or to_lang == "":
        respond(error_response(request_id, "invalid_params", "params.toLang must be a non-empty string"))
        return

    # Optional delay for E2E timeout tests.
    delay_ms = params.get("delayMs")
    if isinstance(delay_ms, int) and delay_ms > 0:
        time.sleep(delay_ms / 1000.0)

    translated = f"[{to_lang}] {text}"
    elapsed_ms = int((time.perf_counter() - start) * 1000)
    respond(
        {
            "id": request_id,
            "result": {
                "translatedText": translated,
                "detectedLang": params.get("fromLang"),
                "engine": "mock",
                "timingMs": elapsed_ms,
            },
        }
    )


def main() -> int:
    log("info", "mock sidecar service started")

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue

        try:
            req = json.loads(line)
        except Exception as e:
            log("warn", f"invalid json: {e}")
            respond(error_response(None, "invalid_json", "failed to parse json"))
            continue

        request_id = req.get("id")
        method = req.get("method")
        params = req.get("params")

        if request_id is not None and not isinstance(request_id, str):
            request_id = None

        if not isinstance(method, str) or method == "":
            respond(error_response(request_id, "invalid_request", "method must be a non-empty string"))
            continue

        if params is None:
            params = {}
        if not isinstance(params, dict):
            respond(error_response(request_id, "invalid_request", "params must be an object"))
            continue

        log("info", f"request: {method}", request_id=request_id)

        if method == "health":
            handle_health(request_id)
        elif method == "translate":
            handle_translate(request_id, params)
        elif method == "crash":
            # Exit immediately with nonzero code (no response), used by E2E tests.
            log("error", "crash requested", request_id=request_id)
            return 2
        elif method == "shutdown":
            respond({"id": request_id, "result": {"ok": True}})
            log("info", "shutdown requested", request_id=request_id)
            return 0
        else:
            respond(error_response(request_id, "method_not_found", f"unknown method: {method}"))

    log("info", "stdin closed, exiting")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
