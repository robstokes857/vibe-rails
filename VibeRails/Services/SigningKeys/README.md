# Signing keys

Settings → KEYS creates RSA-4096 key pairs using the platform-neutral .NET cryptography
APIs. This uses the same code on Windows 11, supported modern macOS, and Linux, including
Native AOT; it needs no shell, OpenSSL executable, or Windows-only key provider. RSA-4096
is not post-quantum cryptography. The wire algorithm is explicitly `RSA-PSS-SHA256` so
a future additional algorithm can use its own format and validation contract.

The password is mandatory, nonblank, 8–128 Unicode scalar characters long, and not digits
only (a numeric PIN is refused). It is unrelated to the remote-viewer PIN. Password entropy
is the only protection for a copied key file or an exported backup, so the UI asks for a
passphrase. The dashboard composes the password to Unicode NFC before sending it; the
backend uses the bytes it receives as-is because this build runs with invariant
globalization, where `string.Normalize` is a pass-through, so any other caller must send
NFC too or risk creating a key it cannot unlock from another keyboard. Each key has its
own random encryption salt/IV and is saved in `~/.vibe_rails/signing-keys/{id}.json` as
public metadata plus password-encrypted PKCS#8 PEM (AES-256-CBC, PBKDF2-SHA256, 600,000
iterations). No password or unencrypted private key is persisted. Unix directories/files
are owner-only; Windows inherits user-profile ACLs. A link or reparse point in place of the
signing-keys folder or a key file is refused; ancestors such as a relocated profile or
macOS's `/var` symlink are allowed. Writes are atomic, and a filesystem lock prevents
concurrent root dashboards from losing changes. Up to 50 keys are retained locally. A
stray or damaged `*.json` in the folder is skipped and reported by name in the KEYS list
(`warnings`) rather than failing the list; single-key operations on a damaged file still
fail with a storage error.

The local API requires the existing session and tab credentials and is mapped only in an
active root backend. Signing, encrypted-backup export, and sync each unlock the selected
key for that one operation. After five failed unlocks, that backend refuses another attempt
on the key (429 with `Retry-After`) for the remainder of the one-minute window measured
from the first failure; a successful unlock clears the count. The counter is per key and
per backend process, so a restart or a second root dashboard starts fresh; it slows a
local guesser at the API and nothing more. It does not prevent offline guessing of a
copied key file, which is why the password policy above is the real control. Keep
encrypted backups and the password: there is no password recovery or local key-import
workflow yet.

When a saved cloud API key exists, creation automatically registers the public key. An
upload failure preserves the local key and offers a manual sync retry with the password.
The account must have a verified email. The client requests a challenge from
`POST https://viberails.ai/api/v1/signing-keys/challenge`, validates its exact registration
domain/ID/fingerprint/32-byte nonce and expiry, signs it, and submits the proof with public
PEM and name to `POST /api/v1/signing-keys`. Both requests use `X-Api-Key` and disable
redirects. The private key, password, and ordinary signing payloads never leave the computer.
Changing the saved API key hides the previous sync association until sync proves possession
again for the current credential; cloud storage prevents rebinding a key to another account.

Message/file signing returns a verification JSON request carrying `keyId`, `fingerprint`,
`publicKeyPem`, `algorithm`, `payloadBase64` and `signatureBase64`, so a signature can also
be checked offline against the included public key. Text is UTF-8, files are their exact
bytes, and payloads are capped at 64 KiB. RSA-PSS uses SHA-256 and a 32-byte salt. Anyone
can POST the file verbatim to `https://viberails.ai/public/api/v1/signatures/verify`; the
server cross-checks `fingerprint` against the registered key and ignores `publicKeyPem`
beyond its length. The registration-challenge prefix is refused on both sides: the desktop
will not sign such content outside the sync flow, and the server reports
`registration_challenge` instead of verifying it, so a proof it saw during registration
can never be replayed as a signed message.
Successful verification returns `signerEmail` for the verified account owning that key.
The account's `/Keys` page lists public keys, revocation state, and verification results
with timestamps, SHA-256 payload hashes and sizes. The cloud does not retain raw payloads
or signatures. A matching signature proves use of the private key, not who was physically
at the keyboard; the email is the account's current verified email.

Desktop coverage: `Tests/Routes/SigningKeyRoutesTests.cs`,
`Tests/Services/SigningKeys/SigningKeyServiceTests.cs`, and
`Tests/wwwroot/js/settings-keys.test.mjs`. Server storage, API/security tests, and EF
migration live in the sibling `VibeRails-Front` repository. Runbooks and diagnostics belong
in `vibe-books`. The cloud migration must be applied through the normal deployment workflow
before the desktop's first successful sync; offline key creation still works beforehand.
