# Runbook repository availability

`RunbookRepoCheckJob` calls `GET https://viberails.ai/api/v1/runbook_repo` once an hour while
an active root backend is open. The first check is one hour after startup. Terminal children,
Automation execution children and fake-CLI UI test hosts do not start this job. Each open
root has its own timer; no operating-system task or daemon is installed.

Each tick reads the currently saved account API key through `IRemoteAccountKeyStore` and sends
it only in `X-Api-Key`. No key means a logged skip. The fixed HTTPS destination has a named
HTTP client with redirects and cookies disabled, a 20-second request timeout, a 10-second
connect timeout and a 16 KiB response-header limit. Response bodies are neither read nor
drained. Host shutdown cancels in-flight requests. Failed checks wait for the next hourly tick.

The log file records `HTTP 200: true/false`, the numeric status (when available), and a fixed
outcome such as `http_response`, `no_api_key`, `timeout` or `network_error`. It never records
the key, response body or raw exception. Only an exact 200 counts as success, including when
another 2xx status is received. This is a cheap status check and is not deferred by CPU pressure.

The server requires a Runbooks-capable key and the owning account's Pro entitlement. An empty
200 means eligible; 403 can mean `pro_required` or a missing capability, and 401 means invalid
authentication. It uses its existing cached billing policy (active/trialing/past_due in the
configured billing mode), and also supports authenticated browser cookies with browser
precedence. The desktop does not persist or infer entitlement from a failed request.

This is initial setup only. Repository documents and synchronization will follow later.
No documents, project names or paths are uploaded, and no local data schema changes are needed.
