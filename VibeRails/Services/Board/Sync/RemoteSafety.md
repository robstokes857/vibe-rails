# Account safety protocol

The front-end service publishes protocol 1 private status at `/api/v1/boards/remote-access`
(Boards key), `/api/v1/terminal/remote-access` (Terminal key), and
`/api/v1/data-exports/remote-access` (Sessions key). The counter reaches three only on an
explicit rejected safety verdict. A fixed 72-hour period ends in an owner recovery hold.
Recovery on the website changes permission, not work: it never resubmits or reconnects.

`RemoteSafetyClient` checks status before remote Board writes and terminal connections.
Session, complete-backup, Board-backup and token-report HTTP clients use
`RemoteSafetyUploadHandler`. The local hold is stored beside the state database in
`remote-safety/`, keyed by destination origin and authenticated account (not API key).
Files contain only the generation and hold bit, use private permissions, and are replaced
under the existing cross-process file lock. Account/destination changes cannot approve
another account's hold. Network or malformed status responses fail closed.

The generation is a canonical decimal string, not a credential. Generation 0 supports old
clients; after recovery, `X-VibeRails-Safety-Protocol: 1` and
`X-VibeRails-Safety-Generation` are required. A missing protocol receives
`account_remote_upgrade_required`; an old generation receives
`account_remote_reapproval_required`. Those responses and `board_remote_sync_paused` /
`account_uploads_blocked` use fixed local messages rather than invalid-key text.

Remote boards shows an explicit review/approve action after recovery. Approval takes the
shown account, destination and generation, and changes only the local permission record.
Queued payloads, ACKs and cursors stay under the existing Board store. Local Board editing
and discard controls remain available. Old terminal objects never adopt a new generation;
1008 and recovery/upgrade handshakes stop reconnection. Fresh Connect and Start actions are
still required. An independent upload block never becomes a terminal ban. Reapproval while uploads remain blocked clears only the remote-connection hold; queued uploads keep a separate unapproved hold until the block is removed and the user approves them.

Roll out the service's status/schema with recovery disabled, release this companion, drain
old server instances, and only then enable `RemoteAccess:RecoveryEnabled` on the service.
The server remains the authorization boundary. No local setting can shorten the 72 hours.
