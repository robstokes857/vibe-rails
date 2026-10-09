/**
 * Authenticated JSON calls from the extension host to the bundled backend.
 *
 * Nothing here may import `vscode`, so the launcher helpers and their tests stay runnable
 * outside the extension host. The shutdown ladder keeps its own `postShutdown` in
 * backend-manager.ts on purpose: that call must never throw and gives up after two seconds.
 */
import * as http from 'http';
import { BACKEND_REQUEST_TIMEOUT_MS, SESSION_TOKEN_HEADER, TAB_TOKEN_HEADER } from './constants';

export interface BackendConnection {
    host: string;
    port: number;
    sessionToken: string;
    tabToken: string;
}

export type BackendMethod = 'GET' | 'POST' | 'PUT' | 'DELETE';

/** A non-2xx reply. `message` is the backend's own `{ error }` text whenever it sent one. */
export class BackendRequestError extends Error {
    public readonly status: number;

    constructor(status: number, message: string) {
        super(message);
        this.name = 'BackendRequestError';
        this.status = status;
    }
}

/**
 * The backend answers failures with `ErrorResponse { error }` (DTOs/ResponseRecords.cs) and
 * the dashboard shows that text verbatim (`preferErrorResponseMessage`), so the host does too.
 */
export function errorMessageFromBody(status: number, raw: string, fallback?: string): string {
    try {
        const parsed = JSON.parse(raw) as { error?: unknown } | null;
        if (parsed && typeof parsed === 'object' && typeof parsed.error === 'string' && parsed.error.trim()) {
            return parsed.error.trim();
        }
    } catch {
        // Not JSON: fall through to the generic text.
    }
    return fallback ?? `The backend answered with HTTP ${status}.`;
}

/**
 * Sends one request with the session and tab headers every `/api/*` route requires.
 * Resolves the parsed JSON body (`{}` for an empty 2xx body); rejects with
 * `BackendRequestError` for any other status, and with a plain `Error` for transport
 * failures and timeouts.
 */
export function requestJson<T = unknown>(
    connection: BackendConnection,
    method: BackendMethod,
    path: string,
    body?: unknown,
    timeoutMs: number = BACKEND_REQUEST_TIMEOUT_MS
): Promise<T> {
    const payload = body === undefined ? null : JSON.stringify(body);

    return new Promise<T>((resolve, reject) => {
        const request = http.request(
            {
                host: connection.host,
                port: connection.port,
                path,
                method,
                headers: {
                    'Accept': 'application/json',
                    [SESSION_TOKEN_HEADER]: connection.sessionToken,
                    [TAB_TOKEN_HEADER]: connection.tabToken,
                    ...(payload !== null ? { 'Content-Type': 'application/json' } : {}),
                    'Content-Length': payload !== null ? Buffer.byteLength(payload) : 0
                }
            },
            (response) => {
                let raw = '';
                response.setEncoding('utf8');
                response.on('data', (chunk: string) => { raw += chunk; });
                response.on('error', reject);
                response.on('end', () => {
                    const status = response.statusCode ?? 0;
                    if (status < 200 || status >= 300) {
                        reject(new BackendRequestError(status, errorMessageFromBody(status, raw)));
                        return;
                    }
                    if (!raw.trim()) {
                        resolve({} as T);
                        return;
                    }
                    try {
                        resolve(JSON.parse(raw) as T);
                    } catch (error) {
                        reject(new Error(`The backend sent an unreadable reply for ${method} ${path}: ${String(error)}`));
                    }
                });
            }
        );

        request.on('error', reject);
        request.setTimeout(timeoutMs, () => {
            request.destroy(new Error(`${method} ${path} timed out after ${timeoutMs} ms.`));
        });
        if (payload !== null) {
            request.write(payload);
        }
        request.end();
    });
}
