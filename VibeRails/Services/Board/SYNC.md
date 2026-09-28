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
Publication consent is bound to a hash of the endpoint and API key. Changing either stops
automatic uploads until the user switches publishing off and on. No credential is returned by
status routes. Requests have a full 30-second timeout and responses have a 128 MiB ceiling.
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

Pull validates each whole page before applying entries: sequences must be contiguous from the
cursor, no entry may exceed the page's `LastSeq`, and `HasMore` must agree with whether the final
sequence reaches `LastSeq`. Creation requires title and lane; known fields and entry metadata
must meet the hosted wire bounds. A missing remote card requires a stored random key; an
existing legacy card may still receive its baseline. Unknown fields remain history only.
Local unsent edits and already acknowledged later
edits protect only their changed fields. The complete remote event is retained even when a
field loses. Application and conflict checks share a SQLite transaction. Missing cards,
unsupported events and invalid pages stop the cursor for retry. An empty page cannot skip to
an advertised sequence. Applied entries never echo back to the server.

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
`BoardHistory`'s NOT NULL; no row is rewritten. All are automatic additive migrations. Older binaries can still open the database but do not
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
