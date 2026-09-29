# Board sync (VB-51)

A board owner can publish a local board to their viberails.ai account. Publishing is off by
default. The root backend pushes and pulls every 60 seconds while open, with a manual sync
button in Board settings. Pausing retains the remote copy and queues subsequent local edits.
There are no invitations, shared editing, or multi-user permissions in this slice.

## Conversation and history

Comments and Agent notes remain separate. Card settings and Board settings each have a
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
reads summaries without loading card descriptions.

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

Attachment bytes/metadata, commit snapshots, linked-card relationships, terminal sessions,
launch options, environment definitions, lane Automations, and agent-context settings remain
local. `@path` travels as text; no referenced file is read or uploaded by sync. User-written
text is uploaded verbatim and can itself contain paths or other private information.

The desktop uses `X-Api-Key` over HTTPS; plain HTTP is accepted only for a loopback test server.
Redirects, URL credentials, query strings and fragments in the configured endpoint are rejected.
Publication consent is bound to a hash of the endpoint and API key. Both are resolved on every
call, so changing either (including the frontend URL after startup) stops automatic uploads until
the user switches publishing off and on; nothing keeps uploading to the old host. No credential is
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
lane names; `board_not_found` (404) means the published copy was deleted on the website and asks
the user to switch publishing off and on, which publishes it again; `write_conflict` (409) simply
retries on the next tick. Publishing is never switched off automatically.
Board settings keep a rejected-entry count and the latest 50 identities visible even
after subsequent syncs succeed. Comments and notes remain in their normal rails; field changes
remain in History. Rejected creation entries keep that card and its dependent edits local; create
a replacement card with corrected data to publish it. Other cards continue syncing.

Rejected fields stay protected from incoming edits until a later local edit to that field is
acknowledged. Corrections release protection per field, without deleting the rejected record.
Incoming web history cannot release this protection. To resubmit an unchanged value, edit it and
save, then restore and save it; only actual field changes create entries. Rejected comments or
notes can be copied into a new comment or note. Pausing/resuming retains rejections; publishing
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
identity checks reject collisions. Cards stay on their board, whether published or local.
Create a new card to work on another board; moves between lanes remain supported.

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
The hosted LastSeq concurrency token serializes label allocation with all other card writes.
