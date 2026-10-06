# Board sync (VB-51)

## VIBE-26: remote Start work

The hosted card's **Start work** button saves edits and asks an open desktop for that project
to launch the saved assignee. One desktop is selected automatically; multiple desktops require
a choice. Only the Board owner can request execution, and only a key belonging to that owner
can advertise the Board. Imported/shared boards never advertise launch targets.

`BoardRemoteLaunchHostedService` runs only in an active root backend (excluded from fake-CLI
test hosts). Every ten seconds it sends a bounded outbound poll through `IBoardSyncClient`,
using the existing HTTPS/no-redirect/header-key transport and destination fingerprint. It
advertises at most 100 published boards from the root's resolved project, not other projects.
No terminal must already be running. Closing the root stops polling; there is no OS service.

`BoardRemoteLaunchService` syncs through the hosted request's sequence, checks local project,
Board, card, destination and imported status again, and calls `IBoardLaunchService` with no
override. Local environments, argv handling, Board grants and launch conflict checks still apply.
Cursor progress alone does not authorize launch: `IBoardStore.IsCardSyncAppliedAsync` reads
the target card's complete skipped-entry ledger and current field protection in one read snapshot.
It rejects unapplied entries through the synced cursor and pending/protected rejected fields.
Other cards' failures and historical rejections whose fields were corrected do not block it.
The status response's latest-50 entry preview is never used as proof of application.
The command carries no directory, executable, arguments or prompt. The hosted broker consumes
each request once; the desktop also suppresses repeat IDs. Only result acknowledgements retry.
Lost results become unknown, never success or an automatic second launch. The website polls
status every two seconds while waiting and keeps the user on the Board.

The companion Front release is required. Its process-local live registry expires desktops after
35 seconds and requests after ten minutes; outstanding results become unknown after two minutes.
Requests are interactive, not durable offline jobs. Multi-instance hosted deployments need
consistent routing, as with the existing live relay. See Front `Services/Boards/RemoteLaunch.md`
for the five hosted routes and credential contract. No schema or local endpoint changes.

## VB-52: shared boards

Owners use **Share** on either client to manage three collaborators, including pending invitations.
Inviting has the same response for registered, unregistered and blocked addresses. Recipients use
the website's **Invitations** and **Blocked users** screens; only a verified account email can
accept. No email notification is sent. The hosted API enforces owner-only member management and
grants accepted members portable card, discussion and layout edits.

Desktop **Shared boards** lists accepted boards and imports one into the current project on
request. `BoardSharingService` orchestrates transport; `IBoardStore` owns atomic persistence.
Automatic additive `board/26` adds `BoardSharedOrigins` in the normal `board.db`. It pins remote
board ID, API destination/key fingerprint and remote key prefix independently of legacy sync
fields. Each remote board is imported at most once per machine. Imported boards never publish
as a new owner after revocation or destination changes; downloaded data stays. The original API
key is required for these POC imports; credential relinking is future work. Older binaries can
read local data but do not understand shared-board sync; use the updated version for sharing.

Cards and discussion reuse bounded Card Log push/pull. Portable layouts reconcile both ways;
transactional snapshot checks preserve concurrent local edits. Removed lanes remain until pending
card moves arrive. Launch settings, environments and Automations stay machine-local. Owner activity
snapshots and collaborator contributions are readable by members on the website. Imported desktop
boards publish their own session, commit, attachment and linked-card activity through the same bounded
rotation. The host attributes uploads to the authenticated account and preserves other publishers,
including when this desktop has empty rails. Hosted card playback checks current Board membership
and the recording publisher; account-wide archives and live terminals retain their own ACLs.
Apply the Front BoardActivityContributors migration and release its server changes before installing
this companion update. This does not import another desktop's activity into local rails.

The companion Front `BoardSharing` migration adds invitations, memberships, blocks and a
concurrency token. It must ship through the normal hosted release workflow before deploying
sharing. This work does not apply production migrations; desktop setup remains automatic.

### VIBE-85: the sharing boundary

An imported board is another account's data with a local copy. Four rules keep a member's view
from being written into it, or into the member's own boards, by accident. None needs a schema change.

- **No republish by an older binary.** An import's `BoardSyncLinks` row stores
  `ActivitySchema = 1` (`BoardStore.ImportedLinkActivitySchema`), the value an owned publication
  reaches once activity sync is set up, so v10.11.4's "already published" predicate
  (`Enabled && ActivitySchema >= 1 && same destination`) holds and that binary only syncs the
  board. An import stored with 0 before this change is brought up to 1 on its next sync tick by
  the current binary (the link's own state, written in the ordinary loop; not a startup backfill).
  The host guards the rest: `POST /api/v1/boards/publish` refuses to **create** a board whose
  lanes include a lane id of any board the caller has or had a membership on, with HTTP 400 and
  code `shared_board_copy`. The desktop reports it as an ordinary publish error and changes no
  data; it also covers the older binary's 404 recreate path after revocation. A member's push
  may rename the board and edit lanes, but the host ignores `keyPrefix` and `displayPrefix` from
  anyone but the owner, so a ≤ v10.11.3 member build cannot relabel the shared board.
- **Labels never cross the boundary.** Display IDs stay unique per project (the board/20 index is
  unchanged). When a pulled label collides with a card already on this machine, and either card is
  on an imported board, no card is renamed: the incoming card takes a locally allocated label on its
  own board's prefix, and the correction is recorded as local-only History (`RemoteSeq = 0`, body
  "Display ID X is shown as Y on this machine …"), never queued, protecting no field. The shared
  board keeps the owner's label, and a later web relabel still applies. The VB-69 rename-and-correct
  path remains for collisions where both cards are on owned boards.
- **Cards never move or merge across it.** A move whose source and destination boards differ is
  refused when either board is imported, in `MoveCardAsync` and in a lane patch alike; a merge is
  refused when either card is on an imported board. Both checks run inside the write transaction
  and say "copy the card instead". Lane-to-lane moves inside an imported board stay ordinary
  member edits. (A departure from an imported board would delete the card for every collaborator
  and publish a copy under the member's account.)

`Tests/Services/Board/BoardSharingSafetyTests.cs` holds the VIBE-14 proofs as regressions,
including a replay of the older binary's predicate against the stored link row; the Front
`BoardSharingTests` cover the host guard for a current and a revoked membership and the
member prefix lock.

## VIBE-1: transfers and one discussion stream

The Board settings sync section has been removed. Automatic publication and protected sync APIs
remain. Comments includes retained legacy notes; new notes are written as comments.

Cards can move between boards while keeping their immutable identity. In the move transaction,
existing log rows receive their source `SyncBoardId`, a departure is queued there, and a new
baseline, full current-state change, `restored` event and discussion copies are queued for the
destination. Superseded discussion remains stored but hidden. Outbox queries, rejections, resets,
conflict checks and acknowledgements use each event's board, so sequence numbers from different
boards cannot collide. Returning to an earlier board restores that hosted projection and reapplies
the current fields. Both boards synchronize independently; an offline transfer can appear on both
hosted boards until the departure is delivered.

Transferred events keep delivery marks in `TransferRemoteSeq`, exposed through `BoardSyncLog`.
Their legacy `RemoteSeq` stays 0 (local-only); a conditional trigger preserves that value even
when an older backend resets the destination's marks. An older outbox therefore cannot send a
source-board deletion or stale field change to the destination. Untransferred cards keep the
original ledger and write behavior. No historical rows are converted during schema setup.

`deletedComment: {to: entryId}` is an additive portable change. Desktop tombstones and the hosted
indexed `SyncedCardLogEntries.DeletedCommentId` projection hide only discussion on the same card
and board. Event bodies remain retained, retries remain idempotent and pull carries the original
change. The companion VibeRails-Front migration must ship for hosted hiding; older hosts retain
the unknown field but continue showing the comment. Merging uses ordinary destination changes,
copied comments/activity and source soft deletion. No additional listener or credential is used.

A configured viberails.ai API key automatically publishes all local boards and their linked
activity (VIBE-13). The root backend pushes and pulls every 60 seconds while open. Without a key,
local edits wait for a configured account. Protected APIs retain the manual sync action.
VB-52 adds invitations and shared editing through the hosted Board ACL described above.

## Conversation and history

Comments includes agent checkpoints and legacy notes. Card settings and Board settings each have a
collapsed History view, fetched only when opened and paged in groups of 100. History includes
card creation, supported field changes, deletion, and board/lane layout changes. Recorded field
values can be expanded for inspection. Routine card reads, MCP tools, and launch prompts never
include History. There is no History MCP tool.

History is diagnostic, not an undo mechanism. Description entries retain the new description;
older entries remain available. Existing data is retained without a historical conversion.
Board/lane history is local on each side; the website records layout snapshots as it receives
them. Reordering cards within a lane and machine-local resources are outside the sync protocol.

Synchronized card history follows the server sequence, so clock skew and offline timestamps
cannot reverse the order that decides a conflict. The desktop places local-only, rejected and
unsent entries (including local board/lane history) before synchronized entries in its newest-first
view, ordered by authored time with insertion order as a tie-breaker. These entries have no
server order yet. Authored timestamps remain visible regardless of the ordering.

The website groups board layout changes first and then card events in descending server
sequence. A snapshot pins the two streams while paging. SQL selects at most 101 metadata
rows before loading the selected payloads; History and sync pull responses use a conservative
8 MiB budget. Hosted comments/notes load in pages of 20 with a sequence cursor. Board polling
reads summaries with at most 180 description characters per card.

## What leaves the machine

The payload contains the board name and key prefix, lane IDs/names/colours/order, card identity,
title, description, type, priority, points, tags, blocked/attention flags, lane, portable assignee,
comments, notes, and their recorded changes/authorship/timestamps. An environment assignee is
represented by its base CLI; its environment ID stays local. Author session IDs are omitted.
Field changes use an explicit allowlist. Generated summaries are rebuilt from portable fields.
The allowlist also carries `context` (VB-63): not a card field but the agent-context sample a
launch records (estimated tokens, prompt and card-read split, intent, CLI and the breakdown),
sent as an ordinary `change` entry. The hosted contract stores unknown change fields verbatim
and never applies them, so no server change was needed; the desktop's pull side retains them
the same way. Session ids stay out of it.

Configured boards also send a desktop-owned snapshot per card: linked session
IDs, names, CLI, creation/end time, exit code, bounded summary and Automation classification; linked commit metadata and saved
before/after file contents; current attachment metadata/content; and linked card identities and
labels. The snapshot never reads the current checkout or follows a file reference. Session replay
bytes use the existing completed-session upload pipeline; the website enables replay only when
that same owner's uploaded session is available. These rails are read-only on the hosted board.

Launch options, tab IDs, project paths, environment definitions/IDs, lane Automation definitions
and agent-context settings remain local. `@path` travels as text; no referenced file is read or
uploaded by sync. User-written text and saved code can themselves contain private information.

VIBE-13 replaces the old publication and linked-activity switches. Existing paused or text-only
publications upgrade on the next scheduler tick. The legacy publish route accepts its old fields
for compatibility but they cannot disable sync. Existing `board/23` columns `ActivitySchema` and
`ActivityAfter` remain; no migration or stored-data removal is required. Older binaries retain
their old behavior. The website and desktop may ship separately; complete automatic activity
publication requires the updated desktop as well as a compatible website.

After each successful push/pull of an enabled publication, `PUT /api/v1/boards/{board}/cards/{card}/activity` replaces the
activity of up to ten cards. A bounded identity query rotates through the board across scheduler
scopes and processes using its stored cursor and the existing cross-process sync lock. A bounded
singleton .NET MemoryCache holds at most 10,000 successful check hashes/timestamps, partitioned
by destination, board and card. A repeat check within 60 seconds skips capture before any state,
saved-code or attachment reads; Sync now bypasses this window. Failed acknowledgements are never
cached. No recording or code payloads are retained in memory. Identical snapshots are skipped for
upload for up to an hour in this process; changed/removal snapshots are sent on that card's next
turn. The server must acknowledge `{schema:1,cardId}` before its hash is accepted.
A failed card does not stop the bounded rotation; it retries on its next turn. Older servers,
failures and invalid acknowledgements stay visible in sync status; completed Card Log progress
is preserved. Removing the configured API key stops both protocols. A large board's first activity
pass takes multiple ticks (ten cards per minute); **Sync now** advances another batch.

Each JSON snapshot is capped at 8 MiB, with 200 recent sessions, 200 recent commits, 100 linked
cards and 40 attachments. Commit file text is capped at 256 Ki characters per side and a shared
content budget; warnings/truncation markers explain omitted content. Attachment content is sent
only up to 1 MiB per file and when it fits the snapshot. Larger files keep their original size and
metadata with an explanation directing the reader to the desktop. Metadata is selected before
reading attachment content. Up to 200 linked session outcomes come from one state connection
and one joined SELECT, with schema checks once per batch and bounded summary text selected in
SQL. Session summaries, code and attachment content share the transfer budget; newest summaries
take priority over older summaries. If verbose metadata still exceeds the limit, trimming uses
individual encoded item sizes rather than repeatedly serializing the entire snapshot. No stored
summary or code is changed. SQL also checks actual BLOB/data URL size and stored commit JSON
length before materialization; a saved snapshot over 8 Mi characters keeps commit metadata with
an availability warning. Metadata queries fetch only the row limit plus one for truncation
reporting. Local upload/storage limits are unchanged. No live session control, remote launch
endpoint or new local listener is introduced.

The desktop uses `X-Api-Key` over HTTPS; plain HTTP is accepted only for a loopback test server.
Redirects, URL credentials, query strings and fragments in the configured endpoint are rejected.
Each exchange is bound to a hash of the endpoint and API key. Both are resolved on every
call, so changing either interrupts an in-flight exchange. The next tick republishes against the
configured destination and resets delivery marks when the remote identity changes. No credential is
returned by status routes. Requests have a full 30-second timeout and responses have a 16 MiB
ceiling, twice the hosted 8 MiB pull-page budget.
Push/pull pages contain at most 20 entries; a tick handles at most 25 pages in each direction.

## Ordering, retry and identity

Each entry has a stable ID. The server assigns a sequence in arrival order, and the last
received change to each field wins. An exact retry receives its original acknowledgement;
reusing an entry ID with different content is rejected. Failed or invalid acknowledgements
leave entries queued. The local outbox sends creation entries first and later entries in
insertion order, independent of clock skew.

Acknowledgements must match request identity order, use distinct positive sequences no greater
than `LastSeq`, and advertise a `LastSeq` at least as high as the applied cursor and previously
recorded sequences. A tick also rejects a `LastSeq` below an earlier response in that tick. Each
server sequence can identify only one entry on a board, checked transactionally across batches
and retries before any acknowledgement is committed. Creation entries
and other entries must each retain their insertion order. A retried change may have an older
sequence than a newly queued creation sent ahead of it, so those two groups may interleave.
An exact replay can return an old sequence, and `LastSeq` can include unrelated web writes.
Invalid acknowledgements leave the whole batch queued; they never advance the pull cursor.
Progress is persisted as it is made (the layout hash after a batch, the cursor after each pull
page), and a failure records its error on the stored link, so a bad page never rewinds the
cursor past pages that were already applied.

A bounded HTTP 400 `invalid_entry` error can name one entry from the attempted batch. The desktop
sets that entry aside, keeps its complete local row, and continues with later eligible entries.
Arbitrary remote error text, unknown codes, malformed IDs and IDs outside the batch cannot change
the outbox. The same bounded parser recognises three other codes, each reported in the desktop's
own wording: `invalid_request` (400) keeps entries queued and asks the user to check board and
lane names; `board_not_found` (404) recreates the deleted hosted copy and retries once, preserving
local history and restarting delivery for its new remote identity; `write_conflict` (409) simply
retries on the next tick. Publishing is never switched off automatically.
The sync status API retains a rejected-entry count and the latest 50 identities even
after subsequent syncs succeed. Comments and notes appear in Comments; field changes
remain in History. Rejected creation entries keep that card and its dependent edits local; create
a replacement card with corrected data to publish it. Other cards continue syncing.

Rejected fields stay protected from incoming edits until a later local edit to that field is
acknowledged. Corrections release protection per field, without deleting the rejected record.
Incoming web history cannot release this protection. To resubmit an unchanged value, edit it and
save, then restore and save it; only actual field changes create entries. Rejected comments or
notes can be copied into a new comment or note. Removing/re-adding a key retains rejections; publishing
to a different remote board resets their delivery marks and protection for the new destination.

Pull validates each whole page's shape before applying entries: sequences must rise from the
cursor (a gap, a sequence the server no longer serves, is passed over rather than waited on), no
entry may exceed the page's `LastSeq`, `HasMore` needs a page that stopped short of `LastSeq`, and
entry identity, author and sizes must meet the hosted wire bounds. A malformed page stops the
cursor for retry, and an empty page cannot skip to an advertised sequence.

Each entry is then checked for what this version can apply. Creation requires title and lane;
known fields must hold values this version accepts. A missing remote card requires a stored
random key; an existing legacy card may still receive its baseline. Unknown fields remain history
only. An entry this version can never apply (an unknown kind such as the reserved `restored`, a
value added after this version, a conflicting identity, a card of another board, or a card that
never reached this machine) is recorded in `BoardSyncSkippedEntries` (board/21) and passed over,
so it cannot hold back every later entry. Board settings count the skipped entries and list the
latest 50 with the desktop's own reason. Transient failures, such as a busy database, still stop
the cursor for retry. Publishing to a different remote board forgets the skipped records along with
the delivery marks.

Skipped entries stay on viberails.ai, and each record names the desktop version that last tried it
(board/22; a record from before it names none and counts as earlier). The first pull of a newer
version moves the cursor back to just before the earliest entry an earlier version skipped and marks
those records as tried by itself, in one transaction, then pulls on from there. Entries already
applied are recognised by id and passed again. A skipped entry that now applies has its record
removed in the same transaction as the write; one that still cannot keeps its record, with the new
reason. A replayed change is an older server sequence, so it leaves alone every field that a later
entry changed. An older version never retries a record a newer one wrote, and an entry the server
no longer serves stays recorded until a later version looks again. The rewind costs one pass over
the entries since the earliest skipped one, so newer web changes can wait a few ticks behind it.

Local unsent edits and already acknowledged later edits protect only their changed fields. A
publication baseline (the system-authored `created` entry Publish, or a later push, writes for a
card that had none) is not an edit: while unsent it protects no field, so a web change pulled
before a large board's baselines have all been pushed still applies locally. The server applies
entries in arrival order and a `created` entry overwrites the card's fields there, so if the
server already knew such a card (a lost acknowledgement, or a board database copied to another
machine), a web edit made before the baseline arrived is overwritten on viberails.ai while the
desktop keeps it; the next local edit of that field brings the two copies together again.
The complete remote event is retained even when a field loses. Application and conflict checks
share a SQLite transaction. Applied entries never echo back to the server.

Every push first writes a baseline for live cards of the board that have no `created` entry. Cards
an older binary created while the board was published (older binaries write no Card Log) would
otherwise never leave the queue, and neither would their comments. The check only reads when
nothing needs a baseline.

New cards have immutable `{PREFIX}-{5 random A-Z0-9}-{N}` keys; legacy keys stay unchanged.
Full keys are case-insensitive on input. Short forms resolve only when unambiguous, using the
stored key's number rather than an imported card's local sequence number. Unique indexes and
identity checks reject collisions.
Moves between boards retain that identity and use the transfer protocol above.

Web creation and lane moves queue the normal local lane Automations, including their existing
60-second settling time. A web edit aimed at a lane deleted locally is retained in history;
the surviving local lane generates a correction for the next push. Soft deletes retain card
data; restore/purge is VB-54. Deleting a local board does not delete its published copy.

## Storage and compatibility

`board/14` adds card keys, soft deletion, change JSON, acknowledgement sequences and sync links.
`board/15` adds board/lane history and triggers. `board/16` adds the destination fingerprint.
`board/17` adds `BoardSyncRejectedFields`, which tracks unresolved fields without changing the
rejected log data, and the partial index `IX_BoardComments_RemoteSeq` over acknowledged rows.
`BoardComments.RemoteSeq = -1` marks a retained rejection; NULL is eligible
unsent state, 0 is local-only, and a positive value is the server acknowledgement.
`board/18` drops and recreates the board/15 history triggers with `COALESCE(BoardId, '')`, so a
lane whose `BoardId` is still NULL (an older binary's write before adoption) never fails on
`BoardHistory`'s NOT NULL; no row is rewritten. `board/21` adds `BoardSyncSkippedEntries`, one row
per pulled entry the desktop passed over (board, entry id, sequence, card key, kind and reason),
cascading with the board. `board/22` adds its nullable `Version`, the desktop version that last tried
the entry. All are automatic additive migrations. Older binaries can still open the database but do not
participate in sync reliably: they cannot emit change entries and may show soft-deleted cards.
Use a current backend to edit a published board.

The website owns its authenticated representation and SQL Server migrations in
`VibeRails-Front/Services/Boards`, `Data/Entities/Synced*`, and `Migrations`.
The desktop's four new routes remain root-only behind session and tab credentials; see
[API_SEC.md](../../../API_SEC.md). Tests use disposable databases and fake network clients.
Passing these tests is not a production deployment or verification of a live account.

The coupled test project in [Tests/headless/BoardSync.Integration](../../../Tests/headless/BoardSync.Integration/README.md)
requires the sibling Front checkout. It sends actual desktop HTTP JSON through the hosted
controller and sync service using disposable data and an in-process transport. It covers
bidirectional fields/comments/notes, offline conflicts, lost acknowledgements, identities,
local lane Automations and upload boundaries. Authentication middleware and SQL Server
concurrency remain separate verification concerns.

## Display labels (VB-69)

`displayId` is a portable card field; `displayPrefix` travels with board layout. Immutable card
keys and IDs still identify every event. Old cards keep their original keys as display labels.
The hosted board accepts the first label, allocates an available numeric label on conflict,
and appends a distinct correction event. It retains the original event unchanged for retries.
Desktop imports reserve the incoming label, rename any local occupant, and queue that change
atomically. Immutable legacy keys are reserved; an import colliding with one gets another label.
When either card is on an imported shared board, nothing is renamed or queued; see the VIBE-85
boundary rules above. The hosted LastSeq concurrency token serializes label allocation with all
other card writes.
