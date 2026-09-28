# Security findings

## 2026-09-28 — Code mapper reference expansion (VB-VP1W4-67)

Status: resolved; regression and corpus checks passed on 2026-09-28.

Commit `ac94b7f` retained every imported-namespace candidate for every C# identifier
in `SourceReferenceScope`. A repository file below the 128 KiB read limit could
therefore allocate hundreds of MiB, exhausting the root backend when Project Health
automatically requested a graph. The authenticated graph route and repository path
guard remained intact; the failure was an unbounded expansion of permitted input.

The review measured about 410 MiB retained for its 52,698-character reproduction.
A local reproduction with 2,000 imports and 2,000 distinct type/field pairs used
48,687 characters and allocated 1,001.62 MiB in `SourceOutline.Read` before this fix.

The fix enumerates candidates lazily from shared scope data, skips names absent from
the repository's type index, bounds scope/reference text in proportion to source size,
and caps candidate count and character work per file and graph. Exhausted analysis
omits incomplete matches and reports a coverage reason rather than publishing a
possibly ambiguous partial match. Resource and ambiguity regression tests cover it.

The same local reproduction now allocates 4.31 MiB and retains all 4,000 references
without truncation. Six resource tests cover allocation, work limits, partial-match
ambiguity, nested namespaces and unique/repeated qualified-name chains. The full
suite passed 3,590 tests (12 skipped); the final 190 mapper/parser/route tests passed
after the repeated-chain guard. All five corpus repositories retain their 17 sampled
links and pass seven provenance checks. This records a fixed availability defect,
with no change to authentication, listeners or repository containment.
