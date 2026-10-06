# Security findings for owner review

## Script run PIN settings could overwrite concurrent signing changes (VB-9HAFG-182)

Review d4f6000c33f44e99bc0914e5a6d7b369 confirmed that the new run-PIN setting writer
omitted the signing document's cross-process lock. Concurrent successful updates could drop
a required PIN flag or overwrite another instance's signing changes. The unshipped desktop
release is held while this is corrected; production signing data was not used in reproductions.

The correction uses the existing shared write lock around read/verify/mutate/write, matching
the other signing-document writers. Regressions exercise two service instances and cancellation
while the shared lock is held. The full desktop suite passed (4,719 passed, 13 skipped),
the Python-focused suite passed (95 passed, 1 skipped), and all 862 frontend tests passed.
Fresh review is pending before release.
