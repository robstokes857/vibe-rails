import * as assert from 'assert/strict';
import * as http from 'http';
import * as path from 'path';
import * as vscode from 'vscode';
import { WebviewPanelManager } from '../../webview-panel';
import { BackendManager } from '../../backend-manager';
import { deactivate } from '../../extension';

interface TestConnectionInfo {
    port: number | null;
    sessionToken: string | null;
    tabToken: string | null;
    webviewVisible: boolean;
    webviewOpen: boolean;
    backendReady: boolean;
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

interface LauncherViewSnapshot {
    backendReady: boolean;
    items: Array<{ key: string; kind: string; label: string }>;
    message: string | null;
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

async function getConnectionInfo(requireVisible = true): Promise<TestConnectionInfo> {
    return await poll<TestConnectionInfo>(
        async () => {
            const info = await vscode.commands.executeCommand<TestConnectionInfo>('viberails._test.getConnectionInfo');
            if (!info) {
                return null;
            }

            if (!info.backendReady || !info.port || !info.sessionToken || !info.tabToken || (requireVisible && !info.webviewVisible)) {
                return null;
            }

            return info;
        },
        60000,
        500,
        'Timed out waiting for VibeRails to be ready.'
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

    test('warms the backend and sidebar without a panel, then reuses it across dashboard opens', async () => {
        connectionInfo = await getConnectionInfo(false);
        assert.equal(connectionInfo.webviewOpen, false, 'Automatic startup must not create a dashboard panel.');
        const warm = { ...connectionInfo };
        const sidebar = await vscode.commands.executeCommand<LauncherViewSnapshot>('viberails._test.getLauncherItems');
        assert.equal(sidebar?.backendReady, true);
        const catalog = await requestJson(connectionInfo, 'GET', '/api/v1/automation-nav/preferences');
        assert.ok(Array.isArray(catalog.items), 'The hidden backend must already accept authenticated sidebar calls.');

        await Promise.all([
            vscode.commands.executeCommand('viberails.open'),
            vscode.commands.executeCommand('viberails.open')
        ]);
        connectionInfo = await getConnectionInfo();
        assert.equal(connectionInfo.port, warm.port);
        assert.equal(connectionInfo.sessionToken, warm.sessionToken);
        assert.equal(connectionInfo.tabToken, warm.tabToken);

        await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
        const hidden = await poll(async () => {
            const info = await vscode.commands.executeCommand<TestConnectionInfo>('viberails._test.getConnectionInfo');
            return info && !info.webviewOpen ? info : null;
        }, 10000, 50, 'Dashboard panel did not close.');
        assert.equal(hidden.backendReady, true, 'Closing the editor must leave the sidebar backend running.');
        assert.equal(hidden.sessionToken, warm.sessionToken);
        const stillReady = await vscode.commands.executeCommand<LauncherViewSnapshot>('viberails._test.getLauncherItems');
        assert.equal(stillReady?.backendReady, true);

        await vscode.commands.executeCommand('viberails.open');
        connectionInfo = await getConnectionInfo();
        assert.equal(connectionInfo.port, warm.port);
        assert.equal(connectionInfo.sessionToken, warm.sessionToken);
    });

    test('Stop cancels pending startup and prevents its delayed continuation from launching', async () => {
        await vscode.commands.executeCommand('viberails.stop');
        const originalStart = BackendManager.prototype.start;
        let release!: () => void;
        let entered!: () => void;
        const gate = new Promise<void>(resolve => { release = resolve; });
        const starting = new Promise<void>(resolve => { entered = resolve; });
        let finish!: () => void;
        const finished = new Promise<void>(resolve => { finish = resolve; });
        BackendManager.prototype.start = async function (...args) {
            entered();
            await gate;
            try { return await originalStart.apply(this, args); }
            finally { finish(); }
        };
        try {
            const open = vscode.commands.executeCommand('viberails.open');
            await starting;
            const stop = vscode.commands.executeCommand('viberails.stop');
            await Promise.race([
                stop,
                sleep(4500).then(() => { throw new Error('Stop waited for the blocked startup.'); })
            ]);
            release();
            await Promise.all([open, finished]);
            const info = await vscode.commands.executeCommand<TestConnectionInfo>('viberails._test.getConnectionInfo');
            assert.equal(info?.backendReady, false);
            assert.equal(info?.port, null);
            assert.equal(info?.sessionToken, null);
            assert.equal(info?.webviewOpen, false);
        } finally {
            release();
            BackendManager.prototype.start = originalStart;
        }
    });

    test('automatic startup can be disabled and enabled without stopping a running backend', async () => {
        const config = vscode.workspace.getConfiguration('viberails');
        const previous = config.inspect<boolean>('autoStart')?.globalValue;
        try {
            await config.update('autoStart', false, vscode.ConfigurationTarget.Global);
            const stopped = await vscode.commands.executeCommand<TestConnectionInfo>('viberails._test.getConnectionInfo');
            assert.equal(stopped?.port, null);
            await config.update('autoStart', true, vscode.ConfigurationTarget.Global);
            connectionInfo = await getConnectionInfo(false);
            assert.equal(connectionInfo.webviewOpen, false);
            await config.update('autoStart', false, vscode.ConfigurationTarget.Global);
            const running = await vscode.commands.executeCommand<TestConnectionInfo>('viberails._test.getConnectionInfo');
            assert.equal(running?.backendReady, true);
            assert.equal(running?.sessionToken, connectionInfo.sessionToken);
        } finally {
            await config.update('autoStart', previous, vscode.ConfigurationTarget.Global);
        }
    });

    test('a failed bootstrap stops the partial backend and a later open can retry', async () => {
        const originalBootstrap = BackendManager.prototype.getBootstrapUrl;
        BackendManager.prototype.getBootstrapUrl = () => 'not-a-valid-bootstrap-url';
        try {
            await vscode.commands.executeCommand('viberails.open');
            const failed = await vscode.commands.executeCommand<TestConnectionInfo>('viberails._test.getConnectionInfo');
            assert.equal(failed?.port, null);
            assert.equal(failed?.backendReady, false);
            assert.equal(failed?.webviewOpen, false);
        } finally {
            BackendManager.prototype.getBootstrapUrl = originalBootstrap;
        }
        await vscode.commands.executeCommand('viberails.open');
        connectionInfo = await getConnectionInfo();
    });

    test('a backend exit clears the sidebar and a later open obtains a fresh connection', async () => {
        await vscode.commands.executeCommand('viberails.open');
        connectionInfo = await getConnectionInfo();
        const oldToken = connectionInfo.sessionToken;
        await requestJson(connectionInfo, 'POST', '/api/v1/shutdown');
        await poll(async () => {
            const info = await vscode.commands.executeCommand<TestConnectionInfo>('viberails._test.getConnectionInfo');
            return info && !info.backendReady && !info.webviewOpen ? info : null;
        }, 15000, 100, 'Backend exit did not clear the extension state.');
        const sidebar = await vscode.commands.executeCommand<LauncherViewSnapshot>('viberails._test.getLauncherItems');
        assert.equal(sidebar?.backendReady, false);
        assert.deepEqual(sidebar?.items, []);
        await vscode.commands.executeCommand('viberails.open');
        connectionInfo = await getConnectionInfo();
        assert.notEqual(connectionInfo.sessionToken, oldToken);
    });

    test('code report loads real data under the installed webview CSP and disposes on navigation', async () => {
        await vscode.commands.executeCommand('viberails.open');
        connectionInfo = await getConnectionInfo();
        const extension = vscode.extensions.getExtension(EXTENSION_ID)!;
        const target = `${process.platform}-${process.arch}`;
        const manager = new WebviewPanelManager(path.join(extension.extensionPath, 'bin', target, 'wwwroot'));
        try {
            const panel = await manager.create(connectionInfo.port!, connectionInfo.sessionToken, connectionInfo.tabToken);
            const html = panel.webview.html;
            const nonce = html.match(/<script nonce="([^"]+)"/)?.[1];
            assert.ok(nonce, 'The real webview HTML must supply a script nonce.');
            const result = new Promise<{ checks?: string[]; error?: string }>((resolve, reject) => {
                const timer = setTimeout(() => reject(new Error('Report webview did not finish its checks.')), 90000);
                const subscription = panel.webview.onDidReceiveMessage(message => {
                    if (message.command !== 'setTitle' || !message.title?.startsWith('report-test-result:')) return;
                    clearTimeout(timer);
                    subscription.dispose();
                    resolve(JSON.parse(message.title.slice('report-test-result:'.length)));
                });
            });
            // Only test HTML receives this probe. The production host, CSP and modules are unchanged.
            panel.webview.html = html.replace('</body>', `<script type="module" nonce="${nonce}">
                const checks = [];
                const wait = async (predicate, label) => {
                    const deadline = Date.now() + 70000;
                    while (!predicate()) {
                        if (Date.now() > deadline) throw new Error('Timed out waiting for ' + label + '; view=' + window.app?.currentView + '; state=' + document.querySelector('.code-report .qr')?.dataset.state + '; error=' + document.querySelector('.qr-error')?.textContent);
                        await new Promise(resolve => setTimeout(resolve, 100));
                    }
                };
                try {
                    await wait(() => window.app?.data?.configs?.rootPath && document.querySelector('#app-content [data-view]'), 'initial view');
                    await document.fonts.ready;
                    const fonts = await document.fonts.load('16px Lato');
                    if (!fonts.length || !document.fonts.check('16px Lato')) throw new Error('The dashboard font failed to load');
                    window.app.navigate('code-quality');
                    await wait(() => document.querySelector('.code-report .qr')?.dataset.state === 'complete', 'report completion');
                    const viewer = window.app.ruleController.codeReportViewer;
                    await viewer.ready;
                    const atlas = viewer.atlas;
                    if (!atlas) throw new Error('The authenticated repository graph failed to mount: ' + viewer.mapHost.textContent);
                    const frame = viewer.root.querySelector('iframe');
                    if (!frame || frame.sandbox.contains('allow-same-origin')) throw new Error('Iframe isolation changed');
                    checks.push('real report and graph loaded with nonce and opaque-origin sandbox');
                    const file = viewer.response.report.files[0];
                    if (file) {
                        await viewer.focusFile(file.file);
                        const node = viewer.graph.nodes.find(n => n.kind === 'file' && n.path === file.file);
                        if (!node) throw new Error('A report file is missing from the map');
                        await atlas.openDetails(node.id);
                        await wait(() => !viewer.details.hidden, 'saved details');
                        if (!viewer.details.textContent.includes('Show in code explorer')) throw new Error('Details action missing');
                        viewer.closeDetails();
                        checks.push('file focus and saved details');
                    }
                    const originalPreference = document.documentElement.dataset.viberailsVscodeTheme;
                    document.documentElement.dataset.viberailsVscodeTheme = 'enabled';
                    viewer.themes.refresh();
                    await new Promise(resolve => setTimeout(resolve, 150));
                    if (viewer.atlas !== atlas) throw new Error('Theme switch remounted graph');
                    document.documentElement.dataset.viberailsVscodeTheme = originalPreference || 'disabled';
                    checks.push('VS Code theme preference updates in place');
                    window.app.navigate('environments');
                    await wait(() => !document.querySelector('.code-report'), 'route disposal');
                    if (!viewer.destroyed || frame.isConnected) throw new Error('Report not disposed');
                    checks.push('route disposal');
                    window.__viberails_setTitle__('report-test-result:' + JSON.stringify({checks}));
                } catch(error) { window.__viberails_setTitle__('report-test-result:' + JSON.stringify({error:String(error.stack || error)})); }
            </script></body>`);
            const outcome = await result;
            assert.equal(outcome.error, undefined, outcome.error);
            assert.ok(outcome.checks?.includes('route disposal'));
            assert.ok(outcome.checks?.includes('real report and graph loaded with nonce and opaque-origin sandbox'));
        } finally { manager.dispose(); }
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

    test('the Activity Bar launcher view lists the nav flyout catalog and empties when VibeRails stops', async () => {
        await vscode.commands.executeCommand('viberails.open');
        connectionInfo = await getConnectionInfo();

        await vscode.commands.executeCommand('viberails.launcher.refresh');
        const shown = await vscode.commands.executeCommand<LauncherViewSnapshot>('viberails._test.getLauncherItems');
        assert.ok(shown, 'The launcher test command returned nothing.');
        assert.equal(shown.backendReady, true);

        // Read-only on purpose: the suite runs against the user's real state.db, so the
        // catalog is compared with what the view shows, never run, customized or reset.
        const catalog = await requestJson<JsonObject>(connectionInfo, 'GET', '/api/v1/automation-nav/preferences');
        const expected = ((catalog.items ?? []) as JsonObject[])
            .filter((item) => item.enabled === true && (item.kind !== 'script' || item.status === 'approved'))
            .sort((a, b) => Number(a.order) - Number(b.order))
            .map((item) => `${String(item.kind)}|${String(item.key)}|${String(item.label)}`);
        assert.deepEqual(shown.items.map((item) => `${item.kind}|${item.key}|${item.label}`), expected);
        if (expected.length === 0) {
            assert.ok(shown.message, 'An empty launcher view must explain itself.');
        } else {
            assert.equal(shown.message, null);
        }

        await vscode.commands.executeCommand('viberails.stop');
        connectionInfo = null;
        const closed = await vscode.commands.executeCommand<LauncherViewSnapshot>('viberails._test.getLauncherItems');
        assert.equal(closed?.backendReady, false);
        assert.deepEqual(closed?.items, []);
        assert.equal(closed?.message, null);
    });

    // Last in this suite: actual deactivation permanently disables further launches.
    test('deactivation stops a live backend before the host deadline even while startup stays blocked', async () => {
        await vscode.commands.executeCommand('viberails.stop');
        const originalStart = BackendManager.prototype.start;
        let release!: () => void;
        let entered!: (port: number) => void;
        let finish!: () => void;
        const gate = new Promise<void>(resolve => { release = resolve; });
        const starting = new Promise<number>(resolve => { entered = resolve; });
        const finished = new Promise<void>(resolve => { finish = resolve; });
        BackendManager.prototype.start = async function (...args) {
            try {
                const port = await originalStart.apply(this, args);
                entered(port);
                await gate;
                return port;
            } finally { finish(); }
        };
        let open: Thenable<unknown> | undefined;
        try {
            open = vscode.commands.executeCommand('viberails.open');
            const port = await starting;
            const began = Date.now();
            await Promise.race([
                deactivate(),
                sleep(4500).then(() => { throw new Error('Deactivation exceeded the extension-host deadline.'); })
            ]);
            // Keep the startup continuation blocked past the real five-second host
            // deadline. It must not be needed to stop the already-running process.
            await sleep(Math.max(0, 5500 - (Date.now() - began)));
            const status = await new Promise<number | null>(resolve => {
                const request = http.get({ host: '127.0.0.1', port, path: '/health' }, response => {
                    response.resume();
                    resolve(response.statusCode ?? null);
                });
                request.on('error', () => resolve(null));
                request.setTimeout(1000, () => { request.destroy(); resolve(null); });
            });
            assert.equal(status, null, 'The owned backend was still listening after window deactivation.');
            release();
            await Promise.all([open, finished]);
            const info = await vscode.commands.executeCommand<TestConnectionInfo>('viberails._test.getConnectionInfo');
            assert.equal(info?.port, null);
            assert.equal(info?.backendReady, false);
            assert.equal(info?.webviewOpen, false);
        } finally {
            release();
            BackendManager.prototype.start = originalStart;
            await Promise.allSettled([open, deactivate()]);
        }
    });
});
