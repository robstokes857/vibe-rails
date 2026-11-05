import * as assert from 'assert/strict';
import * as path from 'path';
import * as vscode from 'vscode';
import { WebviewPanelManager } from '../../webview-panel';

suite('Board spelling in a VS Code webview', () => {
    test('loads the offline dictionary under the production CSP, corrects with undo and disposes', async function () {
        this.timeout(60000);
        const wwwroot = vscode.Uri.file(path.resolve(__dirname, '..', '..', '..', '..', 'VibeRails', 'wwwroot'));
        const panel = vscode.window.createWebviewPanel('viberailsSpellingTest', 'Board spelling test', vscode.ViewColumn.One,
            { enableScripts: true, localResourceRoots: [wwwroot] });
        const manager = new WebviewPanelManager(wwwroot.fsPath);
        let timer: ReturnType<typeof setTimeout> | undefined;
        let subscription: vscode.Disposable | undefined;
        try {
            const builder = manager as unknown as {
                buildHtml(webview: vscode.Webview, port: number, session: null, tab: null): string;
            };
            const html = builder.buildHtml(panel.webview, 5000, null, null);
            const policy = html.match(/<meta http-equiv="Content-Security-Policy"[^>]*>/)?.[0];
            const nonce = html.match(/<script nonce="([^"]+)"/)?.[1];
            assert.ok(policy && nonce);
            const moduleUrl = panel.webview.asWebviewUri(vscode.Uri.joinPath(wwwroot, 'js', 'modules', 'board-spelling.js'));
            const styleUrl = panel.webview.asWebviewUri(vscode.Uri.joinPath(wwwroot, 'style.css'));
            const outcome = new Promise<{ ok: boolean; message?: string }>((resolve, reject) => {
                timer = setTimeout(() => reject(new Error('Spelling webview did not report a result')), 45000);
                subscription = panel.webview.onDidReceiveMessage(resolve);
            });
            panel.webview.html = `<!doctype html><html><head>${policy}<link rel="stylesheet" href="${styleUrl}"></head><body>
<label for="description">Description</label><textarea id="description" rows="5" spellcheck="true"></textarea>
<script type="module" nonce="${nonce}">
const bridge = acquireVsCodeApi();
const check = (value, message) => { if (!value) throw new Error(message); };
const until = async predicate => {
    const deadline = Date.now() + 15000;
    while (!predicate()) {
        if (Date.now() > deadline) throw new Error('Timed out waiting for offline spelling');
        await new Promise(resolve => setTimeout(resolve, 25));
    }
};
let dispose;
try {
    const { bindBoardSpelling, setBoardSpellingEnabled } = await import('${moduleUrl}');
    const input = document.getElementById('description');
    input.value = 'This is mispelled. @src/fakeerrror.cs';
    dispose = bindBoardSpelling(input);
    check(!document.querySelector('.board-spelling'), 'Spelling should default off');
    setBoardSpellingEnabled(true);
    await until(() => document.querySelector('.board-spelling-error'));
    check(document.querySelectorAll('.board-spelling-error').length === 1, 'Reference was checked as prose');
    check(document.querySelector('.board-spelling-error').textContent === 'mispelled', 'Wrong spelling marker');
    check(getComputedStyle(document.querySelector('.board-spelling-overlay')).pointerEvents === 'none', 'Overlay intercepts editing');
    document.querySelector('.board-spelling-check').click();
    await until(() => document.querySelector('.board-spelling-popup'));
    const replacement = [...document.querySelectorAll('.board-spelling-popup button')].find(button => button.textContent === 'misspelled');
    check(replacement, 'Dictionary did not suggest the correction');
    replacement.click();
    check(input.value === 'This is misspelled. @src/fakeerrror.cs', 'Replacement changed unrelated text');
    document.execCommand('undo');
    check(input.value === 'This is mispelled. @src/fakeerrror.cs', 'Correction cannot be undone');
    input.dispatchEvent(new Event('compositionstart'));
    setBoardSpellingEnabled(false);
    check(!document.querySelector('.board-spelling'), 'Disabling left the custom field binding active');
    dispose(); dispose = null;
    input.dispatchEvent(new Event('compositionend'));
    await new Promise(resolve => setTimeout(resolve, 500));
    check(!document.querySelector('.board-spelling, .board-spelling-popup, .board-spelling-tools'), 'Disposed spelling UI remains');
    check(input.spellcheck, 'Native spellcheck setting was not restored');
    bridge.postMessage({ ok: true });
} catch (error) { bridge.postMessage({ ok: false, message: String(error?.stack || error) }); }
finally { dispose?.(); }
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
