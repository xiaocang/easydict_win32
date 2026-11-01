#!/usr/bin/env python3
"""Loopback-only OpenAI chat mock for investigating lookup latency."""

import argparse
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=18765)
    parser.add_argument("--delay", type=float, default=0, help="seconds before response headers")
    parser.add_argument("--chunk-delay", type=float, default=0, help="seconds before streaming content")
    parser.add_argument("--mode", choices=("success", "empty", "error"), default="success")
    args = parser.parse_args()
    if args.delay < 0 or args.chunk_delay < 0:
        parser.error("delays must be non-negative")

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *values):
            print(time.strftime("%H:%M:%S"), fmt % values, flush=True)

        def do_POST(self):
            if self.path != "/v1/chat/completions":
                self.send_error(404)
                return
            try:
                size = int(self.headers.get("Content-Length", "0"))
                if size <= 0 or size > 1024 * 1024:
                    raise ValueError("invalid content length")
                request = json.loads(self.rfile.read(size))
            except (ValueError, json.JSONDecodeError):
                self.send_error(400)
                return
            started = time.monotonic()
            print(f"request received: stream={bool(request.get('stream'))}", flush=True)
            time.sleep(args.delay)
            if args.mode == "error":
                body = b'{"error":{"message":"mock failure","type":"server_error"}}'
                self.send_response(500)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            content = "" if args.mode == "empty" else "Mock translation"
            if request.get("stream"):
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Connection", "close")
                self.end_headers()
                time.sleep(args.chunk_delay)
                event = {"choices": [{"index": 0, "delta": {"content": content}, "finish_reason": None}]}
                try:
                    self.wfile.write(("data: " + json.dumps(event) + "\n\ndata: [DONE]\n\n").encode())
                    self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError):
                    print("client disconnected", flush=True)
            else:
                body = json.dumps({"choices": [{"index": 0, "message": {"role": "assistant", "content": content}, "finish_reason": "stop"}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            print(f"response finished after {time.monotonic() - started:.3f}s", flush=True)

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"Listening on http://127.0.0.1:{args.port}/v1/chat/completions", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
