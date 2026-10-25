import * as assert from 'assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import * as vm from 'vm';
import { WebviewPanelManager } from '../../webview-panel';

suite('Webview HTML', () => {
    for (const prefix of ['', '\uFEFF']) {
        test(`preserves the document head ${prefix ? 'with' : 'without'} a UTF-8 BOM`, () => {
            const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'viberails-webview-'));
            const manager = new WebviewPanelManager(directory);
            try {
                fs.writeFileSync(path.join(directory, 'index.html'), `${prefix}<!DOCTYPE html>
<html><head><style>.app-subnav { opacity: 0; }</style></head>
<body><nav class="app-subnav">Vibe Rails · 导航</nav></body></html>`, 'utf8');
                const webview = {
                    cspSource: 'https://webview.example',
                    asWebviewUri: (uri: vscode.Uri) => uri
                } as vscode.Webview;
                const builder = manager as unknown as {
                    buildHtml(webview: vscode.Webview, port: number, sessionToken: null, tabToken: null): string;
                };

                const html = builder.buildHtml(webview, 5000, null, null);

                // A leading U+FEFF makes DOMParser create an empty head and move
                // the original head contents into body before VS Code loads it.
                assert.ok(html.startsWith('<!DOCTYPE html>'));
                assert.match(html, /<head>[\s\S]*<style>\.app-subnav \{ opacity: 0; \}<\/style><\/head>/);
                assert.ok(html.includes('Vibe Rails · 导航'));
                assert.match(html, /<meta http-equiv="Content-Security-Policy"/);
                assert.match(html, /<script nonce="[a-f0-9]+">/);
                const messages: unknown[] = [];
                const globals: Record<string, unknown> = {};
                const injection = html.match(/<script nonce="[a-f0-9]+">([\s\S]*?)<\/script>/)?.[1];
                assert.ok(injection);
                vm.runInNewContext(injection, {
                    window: globals,
                    document: { documentElement: { dataset: {} } },
                    acquireVsCodeApi: () => ({ postMessage: (message: unknown) => messages.push(message) })
                });
                const openExternal = globals.__viberails_openExternal__ as (url: string) => void;
                assert.equal(typeof openExternal, 'function');
                openExternal('https://viberails.ai/link');
                assert.equal(JSON.stringify(messages), JSON.stringify([{ command: 'openExternal', url: 'https://viberails.ai/link' }]));
                const launcherChanged = globals.__viberails_launcherChanged__ as () => void;
                assert.equal(typeof launcherChanged, 'function');
                launcherChanged();
                assert.equal(JSON.stringify(messages[1]), JSON.stringify({ command: 'launcherChanged' }));
                const reviewRequestChanged = globals.__viberails_reviewRequestChanged__ as (payload: unknown) => void;
                assert.equal(typeof reviewRequestChanged, 'function');
                reviewRequestChanged({ id: 'r1', status: 'approved' });
                assert.equal(JSON.stringify(messages[2]), JSON.stringify({ command: 'reviewRequestChanged', payload: { id: 'r1', status: 'approved' } }));
            } finally {
                manager.dispose();
                fs.rmSync(directory, { recursive: true, force: true });
            }
        });
    }
});

suite('Webview credential boundary', () => {
    test('keeps credentials private and authenticates only the exact backend origin', () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'viberails-credentials-'));
        const manager = new WebviewPanelManager(directory);
        try {
            fs.writeFileSync(path.join(directory, 'index.html'), '<html><head></head><body><script nonce="{{VIBERAILS_CSP_NONCE}}">void 0;</script></body></html>');
            const webview = { cspSource: 'https://webview.example', asWebviewUri: (uri: vscode.Uri) => uri } as vscode.Webview;
            const builder = manager as unknown as { buildHtml(webview: vscode.Webview, port: number, session: string, tab: string): string };
            const html = builder.buildHtml(webview, 5000, 'secret-session', 'secret-tab');
            assert.ok(!html.includes('{{VIBERAILS_CSP_NONCE}}'));
            const nonce = html.match(/<script nonce="([^"]+)"/)?.[1];
            assert.ok(nonce);
            for (const script of html.matchAll(/<script([^>]*)>/gi)) assert.ok(script[1].includes(`nonce="${nonce}"`));
            const injection = html.match(/<script nonce="[^"]+">([\s\S]*?)<\/script>/)?.[1];
            assert.ok(injection);
            const fetched: Array<{ input: unknown; init?: RequestInit }> = [];
            const sockets: Array<{ url: string; protocols: unknown }> = [];
            class FakeSocket {
                constructor(url: string, protocols?: unknown) { sockets.push({ url, protocols }); }
            }
            const globals: Record<string, any> = {
                fetch: (input: unknown, init?: RequestInit) => { fetched.push({ input, init }); }, WebSocket: FakeSocket
            };
            const sandbox = vm.createContext({ window: globals, URL, Request, Headers,
                document: { baseURI: 'https://webview.example/assets/', documentElement: { dataset: {} } },
                sessionStorage: { setItem() {} }, acquireVsCodeApi: () => ({ postMessage() {} }) });
            vm.runInContext(injection, sandbox);
            assert.equal(globals.__viberails_SESSION_TOKEN__, undefined);
            assert.equal(vm.runInContext('typeof __vb_token__', sandbox), 'undefined');
            assert.equal(vm.runInContext('typeof __vb_tab_token__', sandbox), 'undefined');
            const init = { headers: { existing: 'preserved' } };
            globals.fetch('http://localhost:5000/api/v1/test', init);
            globals.fetch(new URL('http://localhost:5000/api/v1/test'));
            globals.fetch(new Request('http://localhost:5000/api/v1/test', { headers: { existing: 'request' } }));
            for (const call of fetched) {
                const headers = new Headers(call.init?.headers);
                assert.equal(headers.get('viberails_session'), 'secret-session');
                assert.equal(headers.get('viberails_tab'), 'secret-tab');
            }
            assert.equal(new Headers(fetched[2].init?.headers).get('existing'), 'request');
            assert.deepEqual(init, { headers: { existing: 'preserved' } });
            for (const url of ['https://example.com/', 'http://localhost:5001/', 'http://localhost.evil.test:5000/',
                'https://localhost:5000/', 'http://127.0.0.1:5000/', './asset.js']) {
                globals.fetch(url, init);
                assert.equal(fetched[fetched.length - 1]?.init, init);
                assert.equal(new Headers(fetched[fetched.length - 1]?.init?.headers).get('viberails_session'), null);
            }
            new globals.WebSocket('ws://localhost:5000/api/v1/events', ['application', 'secret-tab']);
            assert.equal(JSON.stringify(sockets[0].protocols), JSON.stringify(['application', 'secret-tab', 'secret-session']));
            for (const url of ['wss://example.com/', 'ws://localhost:5001/', 'wss://localhost:5000/']) {
                new globals.WebSocket(url, ['application']);
                assert.equal(JSON.stringify(sockets[sockets.length - 1]?.protocols), JSON.stringify(['application']));
            }
        } finally {
            manager.dispose();
            fs.rmSync(directory, { recursive: true, force: true });
        }
    });
});

async function waitFor(condition: () => boolean, timeoutMs: number, message: string): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (!condition()) {
        if (Date.now() > deadline) throw new Error(message);
        await new Promise((resolve) => setTimeout(resolve, 25));
    }
}

suite('Webview bridge messages', () => {
    test('reports current visibility at readiness and every later hide/reveal', async function () {
        this.timeout(60000);
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'viberails-visibility-'));
        fs.writeFileSync(path.join(directory, 'index.html'), `<!doctype html><html><head></head><body><script>
const states = [];
window.addEventListener('message', ({ data }) => {
    if (data.command === 'releaseReady') window.__viberails_scriptImportReady__();
    if (data.command === 'hostVisibility') {
        states.push(data.visible);
        window.__viberails_setTitle__('visibility:' + states.join(','));
    }
});
window.__viberails_setTitle__('visibility-listening');
</script></body></html>`, 'utf8');
        const manager = new WebviewPanelManager(directory);
        let document: vscode.TextDocument | undefined;
        try {
            const panel = await manager.create(5000);
            await waitFor(() => panel.title === 'visibility-listening', 30000, 'Visibility test page did not load.');
            document = await vscode.workspace.openTextDocument({ content: 'Hide the retained dashboard behind this editor.' });
            await vscode.window.showTextDocument(document, vscode.ViewColumn.One);
            await waitFor(() => !panel.visible, 10000, 'Dashboard did not hide.');
            await panel.webview.postMessage({ command: 'releaseReady' });
            await waitFor(() => panel.title === 'visibility:false', 10000, 'Readiness did not send the current hidden state.');
            panel.reveal();
            await waitFor(() => panel.title === 'visibility:false,true', 10000, 'Reveal was not forwarded.');
            await vscode.window.showTextDocument(document, vscode.ViewColumn.One);
            await waitFor(() => panel.title === 'visibility:false,true,false', 10000, 'Hide was not forwarded.');
            panel.reveal();
            await waitFor(() => panel.title === 'visibility:false,true,false,true', 10000, 'Second reveal was not forwarded.');
        } finally {
            manager.dispose();
            const tab = vscode.window.tabGroups.all.flatMap(group => group.tabs).find(tab =>
                tab.input instanceof vscode.TabInputText && tab.input.uri.toString() === document?.uri.toString());
            if (tab) await vscode.window.tabGroups.close(tab);
            fs.rmSync(directory, { recursive: true, force: true });
        }
    });

    test('host messages wait for the dashboard bridge, then arrive in order', async function () {
        this.timeout(60000);
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'viberails-bridge-'));
        fs.writeFileSync(path.join(directory, 'index.html'), `<!doctype html><html><head></head><body><script>
const received = [];
window.addEventListener('message', (event) => {
    if (event.data.command === 'hostVisibility') return;
    received.push(event.data.command);
    window.__viberails_setTitle__('bridge-test:' + received.join(','));
});
Promise.resolve().then(() => window.__viberails_scriptImportReady__());
</script></body></html>`, 'utf8');
        const manager = new WebviewPanelManager(directory);
        let subscription: vscode.Disposable | undefined;
        try {
            const panel = await manager.create(5000);
            const titles: string[] = [];
            subscription = panel.webview.onDidReceiveMessage((message) => {
                if (message?.command === 'setTitle' && typeof message.title === 'string' && message.title.startsWith('bridge-test:')) {
                    titles.push(message.title);
                }
            });

            const first = manager.postWhenReady({ command: 'one' });
            const second = manager.postWhenReady({ command: 'two' });
            assert.deepEqual(await Promise.all([first, second]), [true, true]);
            await waitFor(() => titles.includes('bridge-test:one,two'), 30000, 'The queued messages did not reach the page in order.');

            assert.equal(await manager.postWhenReady({ command: 'three' }), true);
            await waitFor(() => titles.includes('bridge-test:one,two,three'), 30000, 'A message sent after readiness did not reach the page.');
        } finally {
            subscription?.dispose();
            manager.dispose();
            fs.rmSync(directory, { recursive: true, force: true });
        }
    });

    test('pending messages settle false without a panel, on timeout and on disposal', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'viberails-bridge-idle-'));
        fs.writeFileSync(path.join(directory, 'index.html'), '<!doctype html><html><head></head><body></body></html>');
        const manager = new WebviewPanelManager(directory);
        try {
            assert.equal(await manager.postWhenReady({ command: 'none' }), false);
            await manager.create(5000);
            assert.equal(await manager.postWhenReady({ command: 'late' }, 30), false);
            const closing = manager.postWhenReady({ command: 'closing' });
            manager.dispose();
            assert.equal(await closing, false);
            assert.equal(manager.hasPanel(), false);
        } finally {
            manager.dispose();
            fs.rmSync(directory, { recursive: true, force: true });
        }
    });
});
