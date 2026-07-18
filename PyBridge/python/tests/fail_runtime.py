"""Failure fixture #2 — buggy code that throws an unhandled runtime exception.

No missing libraries; the standard library is all it uses. The code writes some output to
stdout, then hits a ZeroDivisionError deep in a helper and crashes with a Python traceback on
stderr and a non-zero exit code. This tests that the wrapper captures BOTH the partial stdout
produced before the crash AND the full stderr traceback, without the .NET side itself blowing up.
"""

import sys


def running_total(values):
    total = 0.0
    for value in values:
        total += 10 / value  # ZeroDivisionError when value == 0
    return total


def main():
    data = [1, 2, 5, 0, 3]  # the 0 is the landmine
    print("starting computation...", flush=True)          # stdout, before the crash
    print(f"processing {len(data)} values", flush=True)   # more stdout
    result = running_total(data)                           # <-- raises, unhandled
    print(f"result = {result}")                            # never reached
    return 0


if __name__ == "__main__":
    sys.exit(main())
