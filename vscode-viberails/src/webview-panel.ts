import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import * as crypto from 'crypto';

const DEFAULT_WWWROOT = path.join(process.env.USERPROFILE || process.env.HOME || '', '.vibe_rails', 'wwwroot');

export class WebviewPanelManager {
    private panel: vscode.WebviewPanel | null = null;
    private readonly wwwrootPath: string;

    private _onCloseRequested: vscode.EventEmitter<void> = new vscode.EventEmitter<void>();
    public readonly onCloseRequested: vscode.Event<void> = this._onCloseRequested.event;

    constructor(wwwrootPath?: string) {
        this.wwwrootPath = wwwrootPath || DEFAULT_WWWROOT;
    }

    public isVisible(): boolean {
        return this.panel !== null && this.panel.visible;
    }

    public reveal(): void {
        this.panel?.reveal(vscode.ViewColumn.One);
    }

    public async create(port: number, sessionToken: string | null = null): Promise<vscode.WebviewPanel> {
        if (this.panel) {
            this.panel.reveal(vscode.ViewColumn.One);
            return this.panel;
        }

        const wwwrootUri = vscode.Uri.file(this.wwwrootPath);

        this.panel = vscode.window.createWebviewPanel(
            'viberailsDashboard',
            'VibeRails Dashboard',
            vscode.ViewColumn.One,
            {
                enableScripts: true,
                retainContextWhenHidden: true,
                localResourceRoots: [wwwrootUri],
                portMapping: [{ webviewPort: port, extensionHostPort: port }]
            }
        );

        this.panel.webview.html = this.buildHtml(this.panel.webview, port, sessionToken);

        this.panel.webview.onDidReceiveMessage(message => {
            if (message.command === 'close') {
                this._onCloseRequested.fire();
            }
        });

        this.panel.onDidDispose(() => {
            this.panel = null;
            this._onCloseRequested.fire();
        });

        return this.panel;
    }

    private buildHtml(webview: vscode.Webview, port: number, sessionToken: string | null): string {
        const indexPath = path.join(this.wwwrootPath, 'index.html');
        let html = fs.readFileSync(indexPath, 'utf8');

        const nonce = crypto.randomBytes(16).toString('hex');
        const wwwrootUri = vscode.Uri.file(this.wwwrootPath);

        // Avoid external CDN dependency inside VS Code webview.
        html = html.replace(
            /https:\/\/cdn\.jsdelivr\.net\/npm\/@xterm\/addon-fit@0\.11\.0\/lib\/addon-fit\.js/g,
            'assets/xterm/addon-fit.js'
        );

        html = this.rewriteAssetPaths(html, webview, wwwrootUri);

        const csp = [
            `default-src 'none'`,
            // Note: 'unsafe-inline' is ignored when a nonce is present per CSP spec.
            // All app event handlers use addEventListener (no inline onclick).
            `script-src 'nonce-${nonce}' ${webview.cspSource}`,
            `style-src ${webview.cspSource} 'unsafe-inline' https://fonts.googleapis.com`,
            `img-src ${webview.cspSource} https: data:`,
            `font-src ${webview.cspSource} https://fonts.gstatic.com`,
            `connect-src http://localhost:${port} ws://localhost:${port} ${webview.cspSource}`,
            `form-action 'none'`,
            `base-uri 'self'`,
        ].join('; ');

        const assetsBaseUri = webview.asWebviewUri(wwwrootUri).toString();

        const fetchPatch = sessionToken ? `
        const __vb_token__ = '${sessionToken}';
        window.__viberails_SESSION_TOKEN__ = __vb_token__;
        const __vb_orig_fetch__ = window.fetch;
        window.fetch = function(input, init) {
            init = init || {};
            const headers = new Headers(init.headers || (input instanceof Request ? input.headers : undefined));
            headers.set('viberails_session', __vb_token__);
            init.headers = headers;
            return __vb_orig_fetch__.call(this, input, init);
        };
        const __vb_orig_ws__ = window.WebSocket;
        window.WebSocket = function(url, protocols) {
            let nextUrl = url;
            try {
                if (typeof nextUrl === 'string' && nextUrl.includes('/api/v1/terminal/ws')) {
                    const parsed = new URL(nextUrl, window.location.href);
                    parsed.searchParams.set('viberails_session', __vb_token__);
                    nextUrl = parsed.toString();
                }
            } catch { /* use original URL */ }
            return protocols !== undefined ? new __vb_orig_ws__(nextUrl, protocols) : new __vb_orig_ws__(nextUrl);
        };
        window.WebSocket.prototype = __vb_orig_ws__.prototype;
        window.WebSocket.CONNECTING = __vb_orig_ws__.CONNECTING;
        window.WebSocket.OPEN = __vb_orig_ws__.OPEN;
        window.WebSocket.CLOSING = __vb_orig_ws__.CLOSING;
        window.WebSocket.CLOSED = __vb_orig_ws__.CLOSED;
        ` : `
        window.__viberails_SESSION_TOKEN__ = null;
        `;

        const headInjection = `
    <meta http-equiv="Content-Security-Policy" content="${csp}">
    <base href="${assetsBaseUri}/">
    <script nonce="${nonce}">
        window.__viberails_API_BASE__ = 'http://localhost:${port}';
        window.__viberails_VSCODE__ = true;
        window.__viberails_ASSETS_BASE__ = '${assetsBaseUri}';
        const vscode = acquireVsCodeApi();
        window.__viberails_close__ = function() { vscode.postMessage({ command: 'close' }); };
        ${fetchPatch}
    </script>`;

        html = html.replace(/<head>/i, `<head>${headInjection}`);
        html = html.replace(/<script(?![^>]*\bnonce=)/g, `<script nonce="${nonce}"`);

        return html;
    }

    private rewriteAssetPaths(html: string, webview: vscode.Webview, wwwrootUri: vscode.Uri): string {
        const assetPattern = /(src|href)=["'](?!http|https|data:|#|javascript:)([^"']+)["']/g;

        return html.replace(assetPattern, (match, attr, relativePath) => {
            if (relativePath.startsWith('//') || relativePath.startsWith('data:')) {
                return match;
            }

            let cleanPath = relativePath;
            if (cleanPath.startsWith('/')) { cleanPath = cleanPath.substring(1); }
            else if (cleanPath.startsWith('./')) { cleanPath = cleanPath.substring(2); }

            const fileUri = vscode.Uri.joinPath(wwwrootUri, cleanPath);
            return `${attr}="${webview.asWebviewUri(fileUri)}"`;
        });
    }

    public dispose(): void {
        if (this.panel) {
            this.panel.dispose();
            this.panel = null;
        }
    }
}
