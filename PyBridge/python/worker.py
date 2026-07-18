"""Reference PyBridge session worker.

The whole contract, and it fits in one sentence: read one line from stdin, write exactly
one reply line to stdout (flushed), repeat until EOF. That's what `PythonSession` on the
C# side speaks.

Requests and replies are single-line JSON:

    -> {"op": "ping"}
    <- {"ok": true, "result": "pong"}

    -> {"op": "upper", "text": "hello"}
    <- {"ok": true, "result": "HELLO"}

    -> {"op": "add", "a": 2, "b": 40}
    <- {"ok": true, "result": 42}

    -> {"op": "sleep", "seconds": 0.2}     # simulates slow work
    <- {"ok": true, "result": 0.2}

    -> {"op": "boom"}                      # unknown op
    <- {"ok": false, "error": "unknown op: boom"}

Anything may be printed to *stderr* at any time (logging, warnings, progress) — the C#
session routes stderr separately, so it never corrupts replies.

This worker is dependency-free on purpose. A real one would do its heavy lifting
(import torch, load a model) BEFORE printing the ready line, so the C# side can
`await session.WaitForReadyAsync("ready")` and then enjoy warmed-up calls.
"""

import json
import sys
import time


def handle(request: dict) -> dict:
    op = request.get("op")
    if op == "ping":
        return {"ok": True, "result": "pong"}
    if op == "upper":
        return {"ok": True, "result": str(request.get("text", "")).upper()}
    if op == "add":
        return {"ok": True, "result": request.get("a", 0) + request.get("b", 0)}
    if op == "sleep":
        seconds = float(request.get("seconds", 0))
        time.sleep(seconds)
        return {"ok": True, "result": seconds}
    return {"ok": False, "error": f"unknown op: {op}"}


def main() -> int:
    # Heavy imports / model loading would happen here, before "ready".
    print("worker starting (imports done)", file=sys.stderr, flush=True)
    print("ready", flush=True)

    for line in sys.stdin:  # ends cleanly at EOF (C# CompleteInput / session dispose)
        line = line.strip()
        if not line:
            continue
        try:
            reply = handle(json.loads(line))
        except Exception as exc:  # a bad request must never kill the worker
            reply = {"ok": False, "error": f"{type(exc).__name__}: {exc}"}
        print(json.dumps(reply), flush=True)

    print("worker exiting on EOF", file=sys.stderr, flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
