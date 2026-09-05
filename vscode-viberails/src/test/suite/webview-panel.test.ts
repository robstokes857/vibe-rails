import * as assert from 'assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
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
            } finally {
                manager.dispose();
                fs.rmSync(directory, { recursive: true, force: true });
            }
        });
    }
});
