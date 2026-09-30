# VibeRails account-link authentication flow

This describes the implemented browser and VS Code account-link protocol as of
2026-09-30. It follows the shape of [OAuth Device Authorization (RFC 8628)](https://www.rfc-editor.org/rfc/rfc8628).
It is a custom API-key issuance protocol: its camelCase JSON, endpoints and HTTP
statuses are not a drop-in implementation of an OAuth token endpoint.

There are two authorization flows:

1. **Website login:** viberails.ai uses its existing Auth0 OpenID Connect
   authorization-code flow to authenticate the browser user.
2. **Local account linking:** the local VibeRails backend starts a device request,
   the authenticated browser approves it, and the backend polls for a VibeRails
   API key. This second flow has no local redirect callback, PKCE exchange or
   refresh token. Auth0's website flow has its own OIDC protections.

## Actors and credentials

| Actor | Role |
| --- | --- |
| Dashboard in a browser or VS Code webview | Starts linking and displays progress through the authenticated local API. |
| Local VibeRails root backend | Device client; holds the device secret, polls the website and saves the issued API key. |
| System browser | Authenticates through Auth0 and submits the user's approval on viberails.ai. |
| viberails.ai | Hosts the pending grants, resolves the account, issues API keys and serves API-key-protected resources. |
| Auth0 | Identity provider for the website's browser login. |

| Value | Purpose and where it travels |
| --- | --- |
| `viberails_session` and `viberails_tab` | Existing local process credentials. Both protect the dashboard's `/api/v1/settings/remote-link` calls. They are not sent to viberails.ai. |
| Auth0 authorization code | Returned to the **website's** `/callback`; the website's OIDC middleware redeems it with Auth0. It is not returned to the local app. |
| Auth0 access token | Authenticates the website user. Browser transport is the website's Secure, HttpOnly, SameSite=Lax `access_token` cookie; the server validates the JWT and resolves a local account ID. |
| `deviceCode` | Random 256-bit bearer capability for one pending device request. Kept in local backend memory and sent to hosted token/cancel endpoints in JSON bodies. |
| `userCode` | Eight characters displayed as `ABCD-EFGH`. Locates the same pending request for the signed-in browser. It cannot redeem the API key. |
| `approvalCode` | Separate random proof created when the browser reviews the user code. Bound to that website account; submitted in the approve/deny form body. |
| Antiforgery token | Protects each browser POST independently of the approval proof. |
| VibeRails API key | Issued to the approved account, returned once to the local backend, saved in normal settings, then sent as `X-Api-Key` on supported hosted API requests. The server persists its SHA-512 hash. |

The short code connects the authenticated browser to the local client's pending
request. In an authorization-code flow, a redirect URI returns the result to the
client. Here, polling with the secret `deviceCode` is the return channel. The
`userCode` provides the correlation on the browser side.

The backend verification URL is exactly `https://viberails.ai/link`. The UI adds only
the validated public user code as `#code=ABCD-EFGH`. The website clears this fragment
before login, retains the code in same-tab session storage for at most ten minutes,
and submits the normal authenticated form by POST with a fresh antiforgery token.
The device secret and API key never enter the browser URL. Manual entry is a fallback.

## Sequence

```mermaid
sequenceDiagram
    participant UI as Dashboard / VS Code
    participant Local as Local backend
    participant Browser as System browser
    participant Site as viberails.ai
    participant Auth0

    UI->>Local: POST /api/v1/settings/remote-link (session + tab)
    Local->>Site: POST /api/v1/device-links (computerName, clientVersion)
    Site-->>Local: deviceCode, userCode, verificationUri, expiresIn, interval
    Local-->>UI: userCode, verificationUri, expiry, interval
    UI->>Browser: User opens /link#code=ABCD-EFGH
    Browser->>Site: GET /link
    opt Website login required
        Site-->>Browser: Login handoff clears fragment, retains public code in tab storage
        Browser->>Site: GET /Account/Login?returnUrl=/link
        Browser->>Auth0: Authenticate / authorize website login
        Auth0-->>Browser: Redirect to website /callback with authorization code
        Browser->>Site: GET /callback
        Site->>Auth0: Redeem website authorization code
        Auth0-->>Site: OIDC tokens
        Site-->>Browser: Set website auth cookie, return to /link
    end
    Browser->>Site: POST /link (automatic userCode + antiforgery)
    Site-->>Browser: Computer/account confirmation + owner-bound approvalCode
    Browser->>Site: POST /link/approve (approvalCode + antiforgery)
    Note over Site: Create API key; persist hash; retain plaintext for pickup
    Site-->>Browser: Approved (no API key)
    loop While account modal is open and request is pending
        UI->>Local: GET /api/v1/settings/remote-link (session + tab)
        Local->>Site: POST /api/v1/device-links/token (deviceCode)
        Site-->>Local: Pending, denied, expired, or one-time API key
        opt API key received
            Note over Local: Save API key and activate it for future hosted requests
        end
        Local-->>UI: Progress or linked with masked key/account
    end
```

### 1. Create the pending grant

The local backend sends an anonymous HTTPS request:

```http
POST /api/v1/device-links
Content-Type: application/json

{"computerName":"Example computer","clientVersion":"<version>"}
```

The website creates an in-memory attempt and returns:

```json
{
  "deviceCode": "<secret device capability>",
  "userCode": "ABCD-EFGH",
  "verificationUri": "https://viberails.ai/link",
  "expiresIn": 600,
  "interval": 3
}
```

The local backend retains `deviceCode`; its UI response omits that secret. No
existing API key is required or sent to start a grant. The requester-supplied
computer name is display context, not proof of device identity.

### 2. Authenticate the browser and approve

`GET /link` serves a minimal login handoff to an unauthenticated browser. Its first-party
script removes the fragment, retains the public code in tab storage, and navigates to
`/Account/Login?returnUrl=%2Flink`. The website challenges Auth0, which returns its
authorization code to the website's `/callback`. After login, browser requests
must resolve to a positive local account ID.

After login, the page consumes the retained code and automatically submits it using
the existing form. `POST /link` accepts this public `userCode` with antiforgery validation. It binds
the pending request to the reviewing account and creates an `approvalCode` for
that account. The confirmation shows the account, requester-supplied computer
details and requester IP, with a warning to approve only a request the user started.

`POST /link/approve` requires that account, its approval proof and antiforgery
token. It creates a normal VibeRails API key, subject to the account's ten-key
limit. `POST /link/deny` denies the request under the same credential checks.
An API key alone cannot authorize these browser actions.

### 3. Poll and collect the credential

The UI polls the authenticated local GET route. Each call lets the backend perform
at most one hosted token request per interval:

```http
POST /api/v1/device-links/token
Content-Type: application/json

{"deviceCode":"<secret device capability>"}
```

| Hosted response | Meaning |
| --- | --- |
| `202 authorization_pending` | Keep waiting. |
| `429 slow_down` | Back off; the current local client adds five seconds, up to 60 seconds. |
| `403 access_denied` | Approval was denied, including a refused key-limit request. |
| `410 expired_token` | Expired, unknown, cancelled or already redeemed. |
| `200 {apiKey,keyPrefix,keySuffix,account:{email,name}}` | Successful one-time delivery; remove the pending grant and its retained plaintext. |

The backend saves the raw key to `~/.vibe_rails/settings.json`, updates the runtime
credential and resets the HTTP relay. It reports only a masked hint and account
display details to the UI and through the `remote-account-linked` event. Future
hosted API requests use `X-Api-Key: <saved key>`.

The key has no refresh-token mechanism or automatic expiry in this protocol.
Creating a linked key preserves previous hosted keys. The owner can revoke a key
through the site's existing `/ApiKey` page. Logging out of the website does not
revoke an issued API key.

## Cancellation, persistence and failure handling

- `DELETE /api/v1/settings/remote-link` invalidates the local attempt immediately
  and best-effort sends `DELETE /api/v1/device-links` with `deviceCode` in its body.
- Pending grants expire after ten minutes; a website restart also loses them.
  Approved, uncollected plaintext is discarded on expiry, cancellation or shutdown.
- Late responses from a cancelled or replaced local attempt cannot save a key.
  Cancellation reports `linked` if the key was already saved and is still current.
- A received key stays in local backend memory when saving fails, allowing a save
  retry without another hosted redemption, even after the original code expires.
- The local save compares the original saved credential under the settings
  transaction. Current-version processes coordinate the entire read/compare/write
  through an OS mutex. Older binaries and external file editors do not participate
  in that mutex and cannot receive the same concurrency guarantee.
- A lost successful token response cannot be replayed. The user must start a new
  request and can revoke the named unused key on the website. Cancellation after
  approval can also leave an issued key that was never collected.
- Closing the account modal stops UI-driven polling; reopening it resumes the backend's
  pending attempt. No background daemon is introduced.

## Boundaries and source references

The local routes require the existing session and tab credentials and are mapped
only by a root backend. This flow adds no listener, local callback, CORS exception
or authentication bypass. See [API_SEC.md](../API_SEC.md) for the security contract.
Outbound requests follow no redirects, use bounded response bodies and deadlines,
and validate the returned verification page. Production registration reads
`VibeRails:FrontendUrl`; the shipped UI and VS Code bridge allow only the production
verification page. Alternate origins are transport-test fixtures, not a supported
browser sign-in configuration.

- [Local service](../VibeRails/Services/Integrations/VibeCodeRemote/RemoteAccountLinkService.cs)
- [Local routes](../VibeRails/Routes/RemoteAccountLinkRoutes.cs)
- [Key persistence](../VibeRails/Services/Integrations/VibeCodeRemote/ApiKeyStore.cs)
- [Settings transaction](../VibeRails/Utils/SettingsFile.cs)
- [Dashboard flow](../VibeRails/wwwroot/js/modules/remote-account-link.js)
- [VS Code URL policy](../vscode-viberails/src/external-sign-in.ts)
- Hosted implementation, in the sibling `VibeRails-Front` repository:
  `Controllers/DeviceLinksController.cs`, `Controllers/LinkController.cs`,
  `Services/DeviceLinkStore.cs`, `Services/ApiKeyService.cs` and
  `Services/DeviceLinks.md` beneath its application directory.

The private companion explanation and investigation context are in
`C:\source\vibe-books\OAuth\README.md`.
