# VibeRails Rules (DB layer)

Rules VibeRails enforces on commits under VibeRails.Data.Sqlite/DB. Each rule is a list item under the
rules heading below; the optional Files section lists files covered by this policy.

## Vibe Rails Rules
- Log file changes > 10 lines (WARN)

## Files
- `JobStore.cs`: snapshot review purpose when queuing/retrying runs and adopt the additive purpose columns automatically.
- `SqlStrings.cs`: include explicit purpose in Environment reads/writes while keeping Work as the default for older writers.
