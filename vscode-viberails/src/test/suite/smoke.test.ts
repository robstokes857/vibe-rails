import * as assert from 'assert/strict';
import * as http from 'http';
import * as vscode from 'vscode';

interface TestConnectionInfo {
    port: number | null;
    sessionToken: string | null;
    tabToken: string | null;
    webviewVisible: boolean;
}

interface TerminalTabStatusResponse {
    tabId: string;
    hasActiveSession: boolean;
    sessionId?: string | null;
}

interface UserInputRecord {
    inputText: string;
    sessionId: string;
    sequence: number;
}

type JsonValue = null | boolean | number | string | JsonObject | JsonValue[];
type JsonObject = { [key: string]: JsonValue };

const EXTENSION_ID = 'viberails.vscode-viberails';

function sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

async function poll<T>(
    action: () => Promise<T | null>,
    timeoutMs: number,
    intervalMs: number,
    errorMessage: string
): Promise<T> {
    const started = Date.now();
    let lastError: unknown = null;

    while ((Date.now() - started) < timeoutMs) {
        try {
            const result = await action();
            if (result !== null) {
                return result;
            }
        } catch (error) {
            lastError = error;
        }

        await sleep(intervalMs);
    }

    const suffix = lastError instanceof Error ? ` Last error: ${lastError.message}` : '';
    throw new Error(`${errorMessage}${suffix}`);
}

function requestJson<T extends JsonValue = JsonObject>(
    info: TestConnectionInfo,
    method: 'GET' | 'POST' | 'DELETE',
    route: string,
    body?: JsonObject
): Promise<T> {
    if (!info.port || !info.sessionToken || !info.tabToken) {
        return Promise.reject(new Error('Smoke test connection info is incomplete.'));
    }

    const port = info.port;
    const sessionToken = info.sessionToken;
    const tabToken = info.tabToken;
    const payload = body ? JSON.stringify(body) : null;

    return new Promise((resolve, reject) => {
        const request = http.request(
            {
                host: '127.0.0.1',
                port,
                path: route,
                method,
                headers: {
                    'Accept': 'application/json',
                    'Content-Type': 'application/json',
                    'viberails_session': sessionToken,
                    'viberails_tab': tabToken,
                    ...(payload ? { 'Content-Length': Buffer.byteLength(payload) } : {})
                }
            },
            (response) => {
                let raw = '';

                response.setEncoding('utf8');
                response.on('data', (chunk) => {
                    raw += chunk;
                });
                response.on('end', () => {
                    const statusCode = response.statusCode ?? 0;
                    if (statusCode < 200 || statusCode >= 300) {
                        reject(new Error(`${method} ${route} failed with status ${statusCode}: ${raw}`));
                        return;
                    }

                    if (!raw.trim()) {
                        resolve({} as T);
                        return;
                    }

                    try {
                        resolve(JSON.parse(raw) as T);
                    } catch (error) {
                        reject(new Error(`Failed to parse JSON from ${method} ${route}: ${String(error)}\n${raw}`));
                    }
                });
            }
        );

        request.on('error', reject);
        request.setTimeout(30000, () => {
            request.destroy(new Error(`${method} ${route} timed out.`));
        });

        if (payload) {
            request.write(payload);
        }

        request.end();
    });
}

async function getConnectionInfo(): Promise<TestConnectionInfo> {
    return await poll<TestConnectionInfo>(
        async () => {
            const info = await vscode.commands.executeCommand<TestConnectionInfo>('viberails._test.getConnectionInfo');
            if (!info) {
                return null;
            }

            if (!info.port || !info.sessionToken || !info.tabToken || !info.webviewVisible) {
                return null;
            }

            return info;
        },
        60000,
        500,
        'Timed out waiting for the VibeRails dashboard to open.'
    );
}

async function sendTerminalInput(
    info: TestConnectionInfo,
    tabId: string,
    text: string
): Promise<void> {
    if (!info.port || !info.sessionToken || !info.tabToken) {
        throw new Error('Smoke test connection info is incomplete.');
    }

    const WebSocketCtor = (globalThis as { WebSocket?: new (url: string, protocols?: string | string[]) => any }).WebSocket;
    if (!WebSocketCtor) {
        throw new Error('Global WebSocket implementation is not available in the test runner.');
    }

    const port = info.port;
    const sessionToken = info.sessionToken;
    const tabToken = info.tabToken;
    // Both tokens ride WebSocket subprotocols — the server no longer accepts (and never logs)
    // query-string tokens.
    const url = `ws://127.0.0.1:${port}/api/v1/terminal/tabs/${encodeURIComponent(tabId)}/ws?cols=120&rows=30`;

    await new Promise<void>((resolve, reject) => {
        const socket = new WebSocketCtor(url, [sessionToken, tabToken]);
        let sent = false;
        let settled = false;
        let sendTimer: NodeJS.Timeout | null = null;
        let closeTimer: NodeJS.Timeout | null = null;

        const cleanup = () => {
            if (sendTimer) {
                clearTimeout(sendTimer);
                sendTimer = null;
            }
            if (closeTimer) {
                clearTimeout(closeTimer);
                closeTimer = null;
            }
        };

        const finish = (error?: Error) => {
            if (settled) {
                return;
            }

            settled = true;
            cleanup();

            try {
                socket.close();
            } catch {
                // Best-effort only.
            }

            if (error) {
                reject(error);
                return;
            }

            resolve();
        };

        const sendPayload = () => {
            if (sent) {
                return;
            }

            sent = true;
            socket.send(`${text}\r`);
            closeTimer = setTimeout(() => finish(), 500);
        };

        socket.onopen = () => {
            sendTimer = setTimeout(() => {
                sendPayload();
            }, 250);
        };

        socket.onmessage = () => {
            sendPayload();
        };

        socket.onerror = () => {
            finish(new Error('Terminal WebSocket reported an error while sending smoke-test input.'));
        };

        socket.onclose = () => {
            if (!settled) {
                finish();
            }
        };
    });
}

suite('VibeRails VS Code Smoke', function () {
    this.timeout(180000);

    let connectionInfo: TestConnectionInfo | null = null;
    let createdTabId: string | null = null;

    suiteSetup(async () => {
        const extension = vscode.extensions.getExtension(EXTENSION_ID);
        assert.ok(extension, `Extension ${EXTENSION_ID} was not found.`);
        await extension.activate();
    });

    teardown(async () => {
        if (connectionInfo && createdTabId) {
            try {
                await requestJson(connectionInfo, 'POST', `/api/v1/terminal/tabs/${encodeURIComponent(createdTabId)}/stop`);
            } catch {
                // Best-effort cleanup only.
            }

            try {
                await requestJson(connectionInfo, 'DELETE', `/api/v1/terminal/tabs/${encodeURIComponent(createdTabId)}`);
            } catch {
                // Best-effort cleanup only.
            }
        }

        createdTabId = null;
        connectionInfo = null;

        try {
            await vscode.commands.executeCommand('viberails.stop');
        } catch {
            // Best-effort cleanup only.
        }
    });

    test('opens the dashboard and starts a codex terminal tab', async () => {
        const smokeInput = 'This is a test';

        await vscode.commands.executeCommand('viberails.open');

        connectionInfo = await getConnectionInfo();
        assert.equal(connectionInfo.webviewVisible, true, 'The VibeRails dashboard webview did not become visible.');

        const contextResponse = await requestJson(connectionInfo, 'GET', '/api/v1/context');
        assert.equal(contextResponse.isInGit, true, 'Smoke workspace is not running in git context.');

        const createTabResponse = await requestJson(connectionInfo, 'POST', '/api/v1/terminal/tabs');
        const tabId = createTabResponse.tabId;
        assert.equal(typeof tabId, 'string', 'Tab creation did not return a tab id.');
        createdTabId = tabId as string;

        const startResponse = await requestJson(
            connectionInfo,
            'POST',
            `/api/v1/terminal/tabs/${encodeURIComponent(createdTabId)}/start`,
            { cli: 'codex' }
        );

        assert.equal(startResponse.hasActiveSession, true, 'Codex terminal did not report an active session on startup.');

        const activeStatus = await poll<TerminalTabStatusResponse>(
            async () => {
                const statusResponse = await requestJson(
                    connectionInfo!,
                    'GET',
                    `/api/v1/terminal/tabs/${encodeURIComponent(createdTabId!)}/status`
                );

                if (statusResponse.hasActiveSession !== true || typeof statusResponse.tabId !== 'string') {
                    return null;
                }

                return {
                    tabId: statusResponse.tabId as string,
                    hasActiveSession: true,
                    sessionId: typeof statusResponse.sessionId === 'string' ? statusResponse.sessionId : null
                };
            },
            60000,
            1000,
            'Timed out waiting for the codex terminal session to become active.'
        );

        assert.ok(activeStatus.sessionId, 'Active terminal status did not include a session id.');

        await sendTerminalInput(connectionInfo, createdTabId, smokeInput);

        const inputs = await poll<UserInputRecord[]>(
            async () => {
                const response = await requestJson<JsonValue[]>(
                    connectionInfo!,
                    'GET',
                    `/api/v1/sessions/${encodeURIComponent(activeStatus.sessionId!)}/inputs`
                );

                if (!Array.isArray(response)) {
                    return null;
                }

                const parsed = response
                    .filter((item): item is JsonObject => item !== null && typeof item === 'object' && !Array.isArray(item))
                    .map((item) => ({
                        inputText: typeof item.inputText === 'string' ? item.inputText : '',
                        sessionId: typeof item.sessionId === 'string' ? item.sessionId : '',
                        sequence: typeof item.sequence === 'number' ? item.sequence : -1
                    }))
                    .filter((item) => item.inputText.length > 0);

                return parsed.some((item) => item.inputText.includes(smokeInput))
                    ? parsed
                    : null;
            },
            30000,
            500,
            'Timed out waiting for terminal input to be recorded.'
        );

        assert.ok(
            inputs.some((item) => item.inputText.includes(smokeInput)),
            'The terminal session did not record the smoke-test input.'
        );

        for (let attempt = 0; attempt < 3; attempt += 1) {
            await sleep(1000);
            const statusResponse: JsonObject = await requestJson<JsonObject>(
                connectionInfo,
                'GET',
                `/api/v1/terminal/tabs/${encodeURIComponent(createdTabId)}/status`
            );

            assert.equal(
                statusResponse.hasActiveSession,
                true,
                `Codex terminal exited before the smoke test could confirm it stayed alive (attempt ${attempt + 1}).`
            );
        }
    });
});
