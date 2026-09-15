import * as assert from 'assert/strict';
import * as crypto from 'crypto';
import * as path from 'path';
import * as vscode from 'vscode';

/**
 * PDF.js is the one frontend library the dashboard vendors, and the VS Code panel is the
 * host where it is least likely to work: webview resources are served from a different
 * origin than the webview document, so PDF.js cannot construct a Worker, its `blob:`
 * wrapper is refused by the panel's CSP, and it must fall back to its main-thread path.
 * None of that is reproducible in a browser test, which is same-origin and would pass
 * while the panel stayed broken. So this renders a real PDF inside a real webview under
 * the real CSP.
 */

/** A two-page PDF with computed xref offsets, so PDF.js takes its normal parse path. */
function twoPagePdf(): Buffer {
    const objects = [
        '<</Type/Catalog/Pages 2 0 R>>',
        '<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>',
        '<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>',
        '<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>'
    ];
    let pdf = '%PDF-1.4\n';
    const offsets: number[] = [];
    objects.forEach((body, index) => {
        offsets.push(pdf.length);
        pdf += `${index + 1} 0 obj\n${body}\nendobj\n`;
    });
    const startxref = pdf.length;
    pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`
        + offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')
        + `trailer\n<</Size ${objects.length + 1}/Root 1 0 R>>\nstartxref\n${startxref}\n%%EOF\n`;
    return Buffer.from(pdf, 'latin1');
}

interface RenderOutcome {
    ok: boolean;
    pages?: number;
    width?: number;
    height?: number;
    painted?: boolean;
    message?: string;
}

suite('PDF preview in a VS Code webview', () => {
    test('renders the vendored pdf.js under the panel CSP with no Worker available', async function () {
        this.timeout(120000);

        const wwwroot = path.resolve(__dirname, '..', '..', '..', '..', 'VibeRails', 'wwwroot');
        const wwwrootUri = vscode.Uri.file(wwwroot);
        const panel = vscode.window.createWebviewPanel(
            'viberailsPdfProbe',
            'PDF probe',
            vscode.ViewColumn.One,
            { enableScripts: true, localResourceRoots: [wwwrootUri] }
        );

        try {
            const webview = panel.webview;
            const pdfUri = webview.asWebviewUri(vscode.Uri.joinPath(wwwrootUri, 'assets', 'board', 'pdf.min.js'));
            const workerUri = webview.asWebviewUri(vscode.Uri.joinPath(wwwrootUri, 'assets', 'board', 'pdf.worker.min.js'));
            const nonce = crypto.randomBytes(16).toString('hex');

            // Deliberately the production policy: no `blob:` in script-src, so PDF.js's
            // cross-origin Worker wrapper stays blocked and the fallback is what runs.
            const csp = [
                `default-src 'none'`,
                `script-src 'nonce-${nonce}' ${webview.cspSource}`,
                `style-src ${webview.cspSource} 'unsafe-inline'`,
                `img-src ${webview.cspSource} data:`,
                `connect-src ${webview.cspSource}`
            ].join('; ');

            const outcome = new Promise<RenderOutcome>((resolve, reject) => {
                const timer = setTimeout(() => reject(new Error('the webview never reported a result')), 90000);
                panel.webview.onDidReceiveMessage((message: RenderOutcome) => {
                    clearTimeout(timer);
                    resolve(message);
                });
            });

            webview.html = `<!DOCTYPE html>
<html><head><meta http-equiv="Content-Security-Policy" content="${csp}"></head>
<body><script type="module" nonce="${nonce}">
const api = acquireVsCodeApi();
(async () => {
    try {
        const pdfjs = await import('${pdfUri}');
        pdfjs.GlobalWorkerOptions.workerSrc = '${workerUri}';
        const bytes = Uint8Array.from(atob('${twoPagePdf().toString('base64')}'), character => character.charCodeAt(0));
        const document_ = await pdfjs.getDocument({ data: bytes,
            isEvalSupported: false, useWasm: false, enableXfa: false, useSystemFonts: true }).promise;
        const page = await document_.getPage(1);
        const viewport = page.getViewport({ scale: 1 });
        const canvas = document.createElement('canvas');
        canvas.width = Math.ceil(viewport.width);
        canvas.height = Math.ceil(viewport.height);
        await page.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;
        api.postMessage({ ok: true, pages: document_.numPages, width: canvas.width, height: canvas.height,
            painted: canvas.getContext('2d').getImageData(0, 0, 1, 1).data.length === 4 });
    } catch (error) {
        api.postMessage({ ok: false, message: String((error && error.message) || error) });
    }
})();
</script></body></html>`;

            const result = await outcome;
            assert.ok(result.ok, `PDF.js failed inside the webview: ${result.message}`);
            assert.equal(result.pages, 2);
            assert.equal(result.width, 200);
            assert.equal(result.height, 100);
            assert.equal(result.painted, true);
        } finally {
            panel.dispose();
        }
    });
});
