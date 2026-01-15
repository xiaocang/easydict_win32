#!/usr/bin/env python3
"""E2E test runner for SidecarClient protocol.

Tests: basic requests, concurrent requests, timeout, process crash detection.
This mirrors the .NET E2E tests but runs in Python for cross-platform validation.

Exit code 0 = all tests passed, non-zero = failure.
"""

from __future__ import annotations

import asyncio
import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, Optional


class SidecarClient:
    """Async client for communicating with sidecar via JSON Lines over stdio."""

    def __init__(self, executable: str, args: list[str], default_timeout: float = 10.0):
        self.executable = executable
        self.args = args
        self.default_timeout = default_timeout
        self._process: Optional[asyncio.subprocess.Process] = None
        self._pending: Dict[str, asyncio.Future[Dict[str, Any]]] = {}
        self._request_id = 0
        self._reader_task: Optional[asyncio.Task] = None
        self._stderr_logs: list[str] = []

    async def start(self) -> None:
        self._process = await asyncio.create_subprocess_exec(
            self.executable,
            *self.args,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        self._reader_task = asyncio.create_task(self._read_stdout())
        asyncio.create_task(self._read_stderr())

    async def _read_stdout(self) -> None:
        assert self._process and self._process.stdout
        while True:
            line = await self._process.stdout.readline()
            if not line:
                break
            try:
                msg = json.loads(line.decode())
                req_id = msg.get("id")
                if req_id and req_id in self._pending:
                    self._pending[req_id].set_result(msg)
            except json.JSONDecodeError:
                pass

    async def _read_stderr(self) -> None:
        assert self._process and self._process.stderr
        while True:
            line = await self._process.stderr.readline()
            if not line:
                break
            self._stderr_logs.append(line.decode().strip())

    def _next_id(self) -> str:
        self._request_id += 1
        return f"req-{self._request_id}"

    async def send_request(
        self, method: str, params: Optional[Dict[str, Any]] = None, timeout: Optional[float] = None
    ) -> Dict[str, Any]:
        if not self._process or self._process.returncode is not None:
            raise RuntimeError("Process not running")

        req_id = self._next_id()
        request = {"id": req_id, "method": method}
        if params:
            request["params"] = params

        future: asyncio.Future[Dict[str, Any]] = asyncio.get_event_loop().create_future()
        self._pending[req_id] = future

        try:
            assert self._process.stdin
            self._process.stdin.write((json.dumps(request) + "\n").encode())
            await self._process.stdin.drain()

            return await asyncio.wait_for(future, timeout=timeout or self.default_timeout)
        except asyncio.TimeoutError:
            raise TimeoutError(f"Request {req_id} timed out")
        finally:
            self._pending.pop(req_id, None)

    @property
    def is_running(self) -> bool:
        return self._process is not None and self._process.returncode is None

    @property
    def exit_code(self) -> Optional[int]:
        return self._process.returncode if self._process else None

    @property
    def stderr_logs(self) -> list[str]:
        return self._stderr_logs

    async def stop(self) -> None:
        if self._process and self._process.returncode is None:
            try:
                await self.send_request("shutdown", timeout=2.0)
            except Exception:
                pass
            try:
                await asyncio.wait_for(self._process.wait(), timeout=2.0)
            except asyncio.TimeoutError:
                self._process.kill()


async def run_test(name: str, test_fn) -> bool:
    print(f"[E2E] {name}... ", end="", flush=True)
    try:
        await test_fn()
        print("✅ PASS")
        return True
    except Exception as e:
        print(f"❌ FAIL: {e}")
        return False


def assert_true(condition: bool, msg: str) -> None:
    if not condition:
        raise AssertionError(msg)


MOCK_SERVICE = Path(__file__).parent / "ipc_mock_service.py"


async def test_basic_health() -> None:
    client = SidecarClient("python3", [str(MOCK_SERVICE)])
    await client.start()
    try:
        resp = await client.send_request("health")
        assert_true("result" in resp, "health should have result")
        assert_true("version" in resp["result"], "result should have version")
        assert_true("capabilities" in resp["result"], "result should have capabilities")
    finally:
        await client.stop()


async def test_basic_translate() -> None:
    client = SidecarClient("python3", [str(MOCK_SERVICE)])
    await client.start()
    try:
        resp = await client.send_request("translate", {"text": "hello", "toLang": "zh"})
        assert_true("result" in resp, "translate should have result")
        assert_true("translatedText" in resp["result"], "result should have translatedText")
        assert_true("hello" in resp["result"]["translatedText"], "translatedText should contain original")
    finally:
        await client.stop()


async def test_unknown_method() -> None:
    client = SidecarClient("python3", [str(MOCK_SERVICE)])
    await client.start()
    try:
        resp = await client.send_request("unknown_method_xyz")
        assert_true("error" in resp, "unknown method should return error")
        assert_true(resp["error"]["code"] == "method_not_found", f"error code should be method_not_found")
    finally:
        await client.stop()


async def test_concurrent_requests() -> None:
    client = SidecarClient("python3", [str(MOCK_SERVICE)])
    await client.start()
    try:
        tasks = [
            client.send_request("translate", {"text": f"msg-{i}", "toLang": "en"})
            for i in range(10)
        ]
        responses = await asyncio.gather(*tasks)
        assert_true(len(responses) == 10, "should have 10 responses")
        assert_true(all("result" in r for r in responses), "all should succeed")
        # Verify each response (order may vary due to concurrency, but content should match)
        texts = [r["result"]["translatedText"] for r in responses]
        for i in range(10):
            assert_true(any(f"msg-{i}" in t for t in texts), f"should have msg-{i} in responses")
    finally:
        await client.stop()


async def test_timeout() -> None:
    client = SidecarClient("python3", [str(MOCK_SERVICE)])
    await client.start()
    try:
        # Request with 2s delay, 0.5s timeout
        try:
            await client.send_request("translate", {"text": "slow", "toLang": "en", "delayMs": 2000}, timeout=0.5)
            raise AssertionError("should have timed out")
        except TimeoutError:
            pass  # Expected
    finally:
        await client.stop()


async def test_process_crash() -> None:
    client = SidecarClient("python3", [str(MOCK_SERVICE)])
    await client.start()
    try:
        # Verify running
        resp = await client.send_request("health")
        assert_true("result" in resp, "health should succeed before crash")

        # Send crash command
        try:
            await client.send_request("crash", timeout=1.0)
        except (TimeoutError, RuntimeError):
            pass  # Expected - process exits

        # Wait for process to exit
        await asyncio.sleep(0.5)
        assert_true(not client.is_running, "process should have exited")
        assert_true(client.exit_code == 2, f"exit code should be 2, got {client.exit_code}")
    finally:
        if client.is_running:
            await client.stop()


async def test_graceful_shutdown() -> None:
    client = SidecarClient("python3", [str(MOCK_SERVICE)])
    await client.start()
    resp = await client.send_request("shutdown")
    assert_true("result" in resp, "shutdown should succeed")
    await asyncio.sleep(0.3)
    assert_true(not client.is_running, "process should have exited")


async def test_stderr_logs() -> None:
    client = SidecarClient("python3", [str(MOCK_SERVICE)])
    await client.start()
    try:
        await client.send_request("health")
        await asyncio.sleep(0.2)
        assert_true(len(client.stderr_logs) > 0, "should have stderr logs")
        assert_true(any('"level"' in log for log in client.stderr_logs), "logs should be structured JSON")
    finally:
        await client.stop()


async def main() -> int:
    tests = [
        ("Basic health request", test_basic_health),
        ("Basic translate request", test_basic_translate),
        ("Unknown method returns error", test_unknown_method),
        ("Concurrent requests (multiplexing)", test_concurrent_requests),
        ("Timeout handling", test_timeout),
        ("Process crash detection", test_process_crash),
        ("Graceful shutdown", test_graceful_shutdown),
        ("Stderr log collection", test_stderr_logs),
    ]

    results = []
    for name, test_fn in tests:
        results.append(await run_test(name, test_fn))

    print()
    if all(results):
        print("[E2E] ✅ All tests passed!")
        return 0
    else:
        print("[E2E] ❌ Some tests failed!")
        return 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
