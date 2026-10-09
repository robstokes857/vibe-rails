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
            } finally {
                manager.dispose();
                fs.rmSync(directory, { recursive: true, force: true });
            }
        });
    }
});

async function waitFor(condition: () => boolean, timeoutMs: number, message: string): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (!condition()) {
        if (Date.now() > deadline) throw new Error(message);
        await new Promise((resolve) => setTimeout(resolve, 25));
    }
}

suite('Webview bridge messages', () => {
    test('host messages wait for the dashboard bridge, then arrive in order', async function () {
        this.timeout(60000);
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'viberails-bridge-'));
        fs.writeFileSync(path.join(directory, 'index.html'), `<!doctype html><html><head></head><body><script>
const received = [];
window.addEventListener('message', (event) => {
    received.push(event.data.command);
    window.__viberails_setTitle__('bridge-test:' + received.join(','));
});
setTimeout(() => window.__viberails_scriptImportReady__(), 300);
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
