import * as assert from 'assert/strict';
import * as path from 'path';
import * as vscode from 'vscode';
import { WebviewPanelManager } from '../../webview-panel';

suite('Board Markdown in a VS Code webview', () => {
    test('loads the local renderer under production CSP and preserves source, safety and focus', async function () {
        this.timeout(60000);
        const wwwroot = vscode.Uri.file(path.resolve(__dirname, '..', '..', '..', '..', 'VibeRails', 'wwwroot'));
        const panel = vscode.window.createWebviewPanel('viberailsMarkdownTest', 'Board Markdown test', vscode.ViewColumn.One,
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
            const moduleUrl = panel.webview.asWebviewUri(vscode.Uri.joinPath(wwwroot, 'js', 'modules', 'board-attachments.js'));
            const styleUrl = panel.webview.asWebviewUri(vscode.Uri.joinPath(wwwroot, 'style.css'));
            const outcome = new Promise<{ ok: boolean; message?: string }>((resolve, reject) => {
                timer = setTimeout(() => reject(new Error('Markdown webview did not report a result')), 45000);
                subscription = panel.webview.onDidReceiveMessage(resolve);
            });
            panel.webview.html = `<!doctype html><html><head>${policy}<link rel="stylesheet" href="${styleUrl}"></head><body>
<div id="modal-container"><div id="parent"><textarea id="draft">Unsaved card text</textarea></div></div>
<script type="module" nonce="${nonce}">
const bridge = acquireVsCodeApi();
const check = (value, message) => { if (!value) throw new Error(message); };
let dispose;
try {
    const { openBoardAttachment } = await import('${moduleUrl}');
    const draft = document.getElementById('draft');
    draft.focus();
    const source = '# Review\\n\\n**Ready**\\n\\n| Name | Value |\\n| --- | --- |\\n| State | Open |\\n\\n'
        + '<img src=x onerror="window.__markdownXss=1">\\n\\n![remote](https://attacker.invalid/pixel)\\n\\n'
        + '[bad](javascript:alert(1)) [internal](/api/v1/shutdown)';
    dispose = await openBoardAttachment({ apiCall: async () => new Blob([source]) }, 'fixture',
        { id: 'fixture-markdown', name: 'review.md', mimeType: 'text/markdown' });
    check(document.querySelector('.vb-board-attachment-markdown h1')?.textContent === 'Review', 'Heading did not render');
    check(document.querySelector('.vb-board-attachment-markdown td')?.textContent === 'State', 'Table did not render');
    check(!document.querySelector('.vb-board-attachment-body img, .vb-board-attachment-body a'), 'Unsafe active content');
    check(!window.__markdownXss, 'Attachment script executed');
    const stylesheet = document.getElementById('board-attachment-styles');
    check(stylesheet?.sheet, 'Viewer stylesheet failed to load');
    document.querySelector('[data-markdown-source]').click();
    const pre = document.querySelector('.vb-board-attachment-text');
    check(!pre.hidden && pre.textContent === source, 'Source was changed or remained hidden');
    document.querySelector('[data-markdown-preview]').click();
    check(pre.hidden, 'Preview did not hide source');
    dispose(); dispose = null;
    check(draft.value === 'Unsaved card text' && document.activeElement === draft, 'Draft or focus lost');
    check(!document.getElementById('parent').inert, 'Parent remains inert');
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
