"""Failure fixture #1 — a missing library in the conda environment.

The code itself is perfectly fine. It just needs a third-party package (numpy). Run it in a
conda env that HAS numpy and it succeeds (exit 0). Run it in a bare env that lacks numpy and it
dies with an unhandled ModuleNotFoundError -- a real traceback on stderr and a non-zero exit
code. That is exactly the "if it fails at the terminal, it fails through .NET too" case the
wrapper must capture and hand back.

The import is intentionally NOT wrapped in try/except: we want the raw, ugly failure.
"""

import numpy as np  # ModuleNotFoundError here in an env without numpy (unhandled on purpose)

arr = np.arange(10)
print(f"numpy is present in this env; sum(0..9) = {int(arr.sum())}")
