# Open security finding: legacy comment copies are published to Jira

Detected 2026-10-09 while reviewing the uncommitted main working tree against
`origin/main` at `690dff4a`. Severity: high. Status: unresolved; do not ship the
automatic Jira delivery change until this is corrected and reviewed.

`VibeRails.Data.Sqlite/Board/BoardStore.JiraDelivery.cs:25-39` installs an
`AFTER INSERT` trigger that treats legacy comment inserts as new outbound
activity. Its copy exclusion requires the new `Changes.jiraCopy` marker.
The prior-release `CopyDiscussionAsync` in `BoardStore.CardActions.cs` does not
write that marker. Older VibeRails processes are permitted to keep using the
same Board database after an additive upgrade.

A move across boards, or a merge into a Jira-linked card, performed by such an
older process inserts historical discussion with fresh IDs and no remote stamp.
The trigger queues those copies, and the new root's `JiraDeliveryService` sends
their bodies to Jira. This can expose discussion authored before automatic
sharing existed, contrary to the explicit no-historical/copy-delivery contract.

Verified with a disposable production `BoardStore`: create a comment before
linking Jira, confirm zero pending events, connect the card, execute the exact
prior-release copy INSERT shape, then drain the production delivery service
using a mocked Jira client. One historical comment was posted to the mock.
No real credentials, production data, or live outbound writes were used.

Require positive new-activity eligibility or another legacy-safe copy exclusion;
the absence of a marker unknown to older writers is not sufficient. Preserve
existing data and older-version reads/writes. Cover old move and merge writers
against the new schema in regression tests. Findings and handoff are recorded
on the VIBE-125 batch review and the flagged bug card `VB-O9ZR3-199`.
The bug's `jira-review-reproduction.txt` attachment preserves the harness and
observed output for follow-up.
