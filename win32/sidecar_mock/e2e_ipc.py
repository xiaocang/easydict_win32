#!/usr/bin/env python3

"""End-to-end test runner for the JSONL stdio IPC.

This script spawns the mock sidecar service, sends requests, and validates the
end-to-end request/response loop. It exits with code 0 on success.
"""

from __future__ import annotations

import json
import os
import queue
import subprocess
import sys
import threading
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Optional


@dataclass
class ProcPipes:
    proc: subprocess.Popen
    stdout_q: "queue.Queue[str]"
    stderr_q: "queue.Queue[str]"


def _reader_thread(stream, out_q: "queue.Queue[str]") -> None:
    try:
        for line in stream:
            out_q.put(line.rstrip("\n"))
    finally:
        try:
            stream.close()
        except Exception:
            pass


def spawn_service() -> ProcPipes:
    service_path = Path(__file__).parent / "ipc_mock_service.py"
    env = os.environ.copy()
    env["PYTHONUNBUFFERED"] = "1"

    proc = subprocess.Popen(
        [sys.executable, str(service_path)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
        env=env,
    )
    assert proc.stdin is not None
    assert proc.stdout is not None
    assert proc.stderr is not None

    stdout_q: "queue.Queue[str]" = queue.Queue()
    stderr_q: "queue.Queue[str]" = queue.Queue()

    threading.Thread(target=_reader_thread, args=(proc.stdout, stdout_q), daemon=True).start()
    threading.Thread(target=_reader_thread, args=(proc.stderr, stderr_q), daemon=True).start()

    return ProcPipes(proc=proc, stdout_q=stdout_q, stderr_q=stderr_q)


def send_line(pipes: ProcPipes, line: str) -> None:
    assert pipes.proc.stdin is not None
    pipes.proc.stdin.write(line + "\n")
    pipes.proc.stdin.flush()


def send_request(pipes: ProcPipes, method: str, params: Optional[Dict[str, Any]] = None) -> str:
    req_id = str(uuid.uuid4())
    payload = {"id": req_id, "method": method, "params": params or {}}
    send_line(pipes, json.dumps(payload, ensure_ascii=False))
    return req_id


def recv_json_line(pipes: ProcPipes, timeout_s: float = 3.0) -> Dict[str, Any]:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            line = pipes.stdout_q.get(timeout=0.1)
        except queue.Empty:
            continue
        try:
            return json.loads(line)
        except Exception as e:
            raise AssertionError(f"stdout line is not valid JSON: {line!r}, error: {e}")
    raise AssertionError("timeout waiting for stdout JSON line")


def assert_error(resp: Dict[str, Any], code: str) -> None:
    if "error" not in resp:
        raise AssertionError(f"expected error response, got: {resp}")
    if resp["error"].get("code") != code:
        raise AssertionError(f"expected error code {code!r}, got: {resp}")


def main() -> int:
    pipes = spawn_service()
    proc = pipes.proc

    try:
        # health
        health_id = send_request(pipes, "health")
        resp = recv_json_line(pipes)
        assert resp.get("id") == health_id, resp
        assert "result" in resp and "capabilities" in resp["result"], resp

        # translate
        tid = send_request(pipes, "translate", {"text": "hello", "toLang": "zh"})
        resp = recv_json_line(pipes)
        assert resp.get("id") == tid, resp
        translated = resp.get("result", {}).get("translatedText")
        assert translated == "[zh] hello", resp

        # unknown method
        uid = send_request(pipes, "__unknown__")
        resp = recv_json_line(pipes)
        assert resp.get("id") == uid, resp
        assert_error(resp, "method_not_found")

        # invalid json (no id)
        send_line(pipes, "{")
        resp = recv_json_line(pipes)
        assert resp.get("id") is None, resp
        assert_error(resp, "invalid_json")

        # shutdown
        sid = send_request(pipes, "shutdown")
        resp = recv_json_line(pipes)
        assert resp.get("id") == sid, resp
        assert resp.get("result", {}).get("ok") is True, resp

        proc.wait(timeout=3)
        assert proc.returncode == 0, f"service return code: {proc.returncode}"
        return 0
    finally:
        try:
            if proc.poll() is None:
                proc.kill()
        except Exception:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
