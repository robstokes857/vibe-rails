import * as assert from 'assert/strict';
import * as path from 'path';
import * as vscode from 'vscode';
import { WebviewPanelManager } from '../../webview-panel';

// A normal browser does not exercise VS Code's service worker resource routing.
// Keep this regression in a real webview, with the production content policy.
suite('Session replay in a VS Code webview', () => {
    test('loads assets, playback and patches, retains CSP, and disposes cleanly', async function () {
        this.timeout(60000);
        const wwwroot = vscode.Uri.file(path.resolve(__dirname, '..', '..', '..', '..', 'VibeRails', 'wwwroot'));
        const panel = vscode.window.createWebviewPanel('viberailsReplayTest', 'Replay test', vscode.ViewColumn.One,
            { enableScripts: true, localResourceRoots: [wwwroot] });
        const manager = new WebviewPanelManager(wwwroot.fsPath);
        let timer: ReturnType<typeof setTimeout> | undefined;
        let subscription: vscode.Disposable | undefined;
        try {
            const webview = panel.webview;
            const builder = manager as unknown as {
                buildHtml(webview: vscode.Webview, port: number, sessionToken: null, tabToken: null): string;
            };
            const productionHtml = builder.buildHtml(webview, 5000, null, null);
            const policy = productionHtml.match(/<meta http-equiv="Content-Security-Policy"[^>]*>/)?.[0];
            const nonce = productionHtml.match(/<script nonce="([^"]+)"/)?.[1];
            assert.ok(policy && nonce, 'The production webview must supply its CSP and nonce.');
            const viewerUrl = webview.asWebviewUri(vscode.Uri.joinPath(wwwroot, 'session-replay', 'viewer.mjs'));
            const outcome = new Promise<{ ok: boolean; message?: string }>((resolve, reject) => {
                timer = setTimeout(() => reject(new Error('Replay webview did not report a result')), 45000);
                subscription = webview.onDidReceiveMessage(resolve);
            });
            webview.html = `<!doctype html><html><head>${policy}</head><body>
<div id="viewer" style="height:700px"></div><div id="second" style="height:700px"></div>
<script type="module" nonce="${nonce}">
const bridge = acquireVsCodeApi();
const check = (condition, message) => { if (!condition) throw new Error(message); };
const until = async predicate => {
    const deadline = Date.now() + 15000;
    while (!predicate()) {
        if (Date.now() > deadline) throw new Error('Timed out waiting for the patch editor');
        await new Promise(resolve => setTimeout(resolve, 25));
    }
};
const viewers = [];
try {
    const { mountSessionViewer } = await import('${viewerUrl}');
    const { createEnvelopeSource } = await import(new URL('./envelope.mjs', '${viewerUrl}'));
    const source = createEnvelopeSource({
        session: { id:'fixture', cli:'codex', sessionDisplayName:'Replay fixture',
            startedUtc:'2026-09-30T00:00:00Z', endedUtc:'2026-09-30T00:01:00Z' },
        sessionLogs: [
            { id:1, timestampUtc:'2026-09-30T00:00:00Z', rawBytes:btoa('First screen') },
            { id:2, timestampUtc:'2026-09-30T00:00:10Z', rawBytes:btoa('Second screen') }
        ],
        userInputs: [{ id:1, sequence:1, inputText:'Make it readable',
            timestampUtc:'2026-09-30T00:00:02Z', fileChanges:[{ id:1, filePath:'example.js',
                diffContent:'--- a/example.js\\n+++ b/example.js\\n+const works = true;', linesAdded:1, linesDeleted:0 }] }]
    });
    const errors = [];
    const mount = id => {
        const viewer = mountSessionViewer(document.getElementById(id), {
            sessionId:'fixture', request:source, library:false,
            onEvent:event => { if (event.type === 'error') errors.push(event.error); }
        });
        viewers.push(viewer);
        return viewer;
    };
    const viewer = mount('viewer');
    await viewer.ready;
    check(viewer.getState().ready, 'Recording failed to load: ' + errors.join('; '));
    const child = viewer.element.contentWindow;
    const doc = child.document;
    check(doc.getElementById('session-title').textContent === 'Replay fixture', 'Missing recording title');
    check(child.getComputedStyle(doc.getElementById('play')).display === 'flex', 'Replay stylesheet did not load');
    check(doc.querySelector('meta[http-equiv="Content-Security-Policy"]').content ===
        document.querySelector('meta[http-equiv="Content-Security-Policy"]').content, 'Frame lost its content policy');
    const forbiddenScript = doc.createElement('script');
    forbiddenScript.textContent = 'window.unexpectedReplayScript = true';
    doc.body.append(forbiddenScript);
    check(!child.unexpectedReplayScript, 'Frame allowed a script without its required nonce');
    forbiddenScript.remove();

    const other = mount('second');
    await other.ready;
    await viewer.seek(viewer.getState().started + 10000);
    check(viewer.getState().position === viewer.getState().started + 10000, 'Seek failed');
    check(other.getState().position === other.getState().started, 'Viewers shared playback state');
    await viewer.play();
    check(viewer.getState().playing, 'Playback did not start');
    viewer.pause();
    doc.querySelector('.file-row').click();
    await until(() => doc.querySelector('.monaco-editor') && !doc.getElementById('editor').hidden);
    check(child.monaco.editor.getModels().some(model => model.getValue().includes('const works = true')), 'Patch did not load');
    check(errors.length === 0, errors.join('; '));
    viewer.dispose(); viewer.dispose(); other.dispose();
    check(!document.querySelector('iframe'), 'Disposed replay frame remains mounted');

    // Closing between shell navigation and content initialization must settle ready.
    const observer = new MutationObserver(() => {
        if (document.querySelector('iframe')) pending.dispose();
    });
    observer.observe(document.getElementById('viewer'), {childList:true});
    const pending = mount('viewer');
    await pending.ready;
    observer.disconnect();
    check(!document.querySelector('iframe'), 'Closing during bootstrap leaked a frame');
    const reopened = mount('viewer');
    await reopened.ready;
    check(reopened.getState().ready, 'Reopening replay failed');
    bridge.postMessage({ok:true});
} catch (error) { bridge.postMessage({ok:false, message:String(error?.stack || error)}); }
finally { viewers.forEach(viewer => viewer.dispose()); }
</script></body></html>`;
            const result = await outcome;
            assert.ok(result.ok, result.message);
        } finally {
            clearTimeout(timer);
            subscription?.dispose();
            panel.dispose();
            manager.dispose();
        }
    });
});
