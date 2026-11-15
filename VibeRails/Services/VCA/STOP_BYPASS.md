# Signed STOP exceptions

A STOP violation blocks unless it is fixed or a person explicitly signs a bypass for it.
`COMMIT` acknowledgments and ordinary `vibe-signature` tree signatures cannot bypass STOP.

An agent calls `request_stop_bypass` with a required reason and the intended commit message
(needed to evaluate deferred message rules). This purely evaluates the current staged snapshot,
captures the violating STOP rules, and opens a separate Windows signing popup. The popup shows
the repository, staged tree, changed paths, exact rules and full reason before asking the person
to approve, choose a synced Settings > KEYS key and enter its password with echo disabled.
MCP receives only status. `get_stop_bypass_status` verifies approval against the current snapshot.
Cancellation, an unavailable popup, an invalid signature and offline verification never approve.
Named approvers in `Signed Approval` rules remain required. Ordinary STOP rules accept a key
registered to a verified account, as with `Human verified`.

The popup and status live independently of a pending `git commit`. After approval, retry the
commit without restaging. The pre-commit hook checks the stored signed exception; commit-msg
adds its audit trailers and verifies them against the same snapshot. The final message is then
evaluated, including its added reason, so new/uncovered violations still block. Rule-response
launches remain independent: a violated rule can queue its configured Agent or script even when
the exception allows the commit. Previews and check Actions never request approval or append text.

`VCA-STOP-Reason:` contains the readable reason and `VCA-STOP-Rule:` lists each file and exact
rule text. `VCA-STOP-Bypass: v1 <base64>` contains the signed scope and signature as JSON.
The readable lines must match the signed scope exactly. Removing or altering them invalidates
the exception. Audit text is inserted before a Git scissors line and existing line endings are
retained. A malformed supplied exception blocks; it is not silently repaired from local state.
Only signature evidence uses this conservative pre-scissors boundary. General message rules
still evaluate the tail, which Git can retain in non-editor commits and some cleanup modes.
Genuine trailers carried forward by an amend can be replaced by a freshly signed exception
for the new snapshot; stale evidence alone still blocks.

RSA-PSS-SHA256 signs `VibeRails STOP bypass v1\nscope <sha256>\n`, where the digest covers
the canonical source-generated JSON of `StopBypassScope`: a hash of the normalized repository
root, base commit (null for an unborn branch), base tree, staged tree, reason, and ordered
declaring-file/exact-rule pairs. This is separate from ordinary commit approval. Changing any
covered value requires a new signature. The repository identifier is a hash so machine paths
are not added to commits. The public key verifier receives only the domain-separated digest,
key id, fingerprint and signature, never the rule text or reason. Verification uses the existing
fixed HTTPS endpoint and its registered-key/verified-email authority; no new server is required.

One request is retained in `viberails-stop-bypass.json` inside the worktree's Git metadata
directory (resolved by Git, supporting linked worktrees). A short exclusive file lock plus atomic
replace protects writes; a request-id check prevents an old popup replacing a newer request.
New requests replace prior local state. Successful commits change the base, making the old
approval unusable for subsequent changes. The committed evidence remains independently verifiable.
No schema migration, backend daemon or OS registration is introduced.

The generic Settings signing route refuses the STOP-bypass payload prefix. Neither MCP method
accepts a password or key material. The popup refuses redirected input, non-popup execution and
CI. These are the same local threat boundaries as existing signing: a process with the user's
password or full control of their desktop is not contained by a Git hook, and `--no-verify`
can still skip local hooks. This feature provides explicit, signed audit evidence for normal
VibeRails commit validation.

Regressions: `Tests/Services/Mcp/StopBypassTests.cs`, `SigningKeyServiceTests`,
`CommitSignatureVerifierTests`, MCP HTTP discovery, and `vca-git-guard.test.mjs`.
