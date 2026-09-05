import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import * as crypto from 'crypto';
import { WEBVIEW_VIEW_TYPE } from './constants';

export class WebviewPanelManager {
    private panel: vscode.WebviewPanel | null = null;
    private readonly wwwrootPath: string;

    private _onCloseRequested: vscode.EventEmitter<void> = new vscode.EventEmitter<void>();
    public readonly onCloseRequested: vscode.Event<void> = this._onCloseRequested.event;

    constructor(wwwrootPath: string) {
        this.wwwrootPath = wwwrootPath;
    }

    public isVisible(): boolean {
        return this.panel !== null && this.panel.visible;
    }

    public reveal(): void {
        this.panel?.reveal(vscode.ViewColumn.One);
    }

    public async create(port: number, sessionToken: string | null = null, tabToken: string | null = null): Promise<vscode.WebviewPanel> {
        if (this.panel) {
            this.panel.reveal(vscode.ViewColumn.One);
            return this.panel;
        }

        const wwwrootUri = vscode.Uri.file(this.wwwrootPath);

        this.panel = vscode.window.createWebviewPanel(
            WEBVIEW_VIEW_TYPE,
            this.getInitialPanelTitle(),
            vscode.ViewColumn.One,
            {
                enableScripts: true,
                retainContextWhenHidden: true,
                localResourceRoots: [wwwrootUri],
                portMapping: [{ webviewPort: port, extensionHostPort: port }]
            }
        );

        this.panel.webview.html = this.buildHtml(this.panel.webview, port, sessionToken, tabToken);

        this.panel.webview.onDidReceiveMessage(message => {
            if (message.command === 'close') {
                this._onCloseRequested.fire();
            } else if (message.command === 'openFile' && typeof message.path === 'string') {
                void this.openFileInEditor(message.path);
            } else if (message.command === 'setTitle' && this.panel && typeof message.title === 'string') {
                // Strip Unicode format (Cf — RTL/LTR overrides, zero-width joiners) and
                // control (Cc) characters before assigning the tab title, so a crafted
                // project display name can't spoof adjacent VS Code tab titles.
                const sanitized = message.title.replace(/[\p{Cf}\p{Cc}]/gu, '');
                const trimmed = sanitized.trim().slice(0, 200);
                if (trimmed.length > 0) {
                    this.panel.title = trimmed;
                }
            }
        });

        this.panel.onDidDispose(() => {
            this.panel = null;
            this._onCloseRequested.fire();
        });

        return this.panel;
    }

    /**
     * Opens a file from the dashboard in a real editor tab (the Python scripts section
     * hands off here instead of opening its in-app Monaco modal). Beside, not on top: the
     * panel owns column one and must stay visible next to the file being edited.
     *
     * The path comes from the webview, which only ever runs our own dashboard against the
     * loopback backend — but it is still checked for being a real file so a bad path
     * surfaces as a message rather than an empty untitled editor.
     */
    private async openFileInEditor(filePath: string): Promise<void> {
        try {
            const uri = vscode.Uri.file(filePath);
            const stat = await vscode.workspace.fs.stat(uri);
            if (stat.type !== vscode.FileType.File) {
                throw new Error('not a file');
            }
            const document = await vscode.workspace.openTextDocument(uri);
            await vscode.window.showTextDocument(document, {
                viewColumn: vscode.ViewColumn.Beside,
                preview: false
            });
        } catch {
            vscode.window.showErrorMessage(`Vibe Rails could not open ${filePath}`);
        }
    }

    private getInitialPanelTitle(): string {
        const workspaceName = vscode.workspace.workspaceFolders?.[0]?.name?.trim();
        return workspaceName && workspaceName.length > 0 ? workspaceName : 'Vibe Rails';
    }

    private buildHtml(webview: vscode.Webview, port: number, sessionToken: string | null, tabToken: string | null): string {
        const indexPath = path.join(this.wwwrootPath, 'index.html');
        // VS Code parses this string with DOMParser before loading the webview.
        // A decoded BOM becomes body text there, moving head styles into the body
        // where the nav's initial opacity: 0 overrides the app stylesheet.
        let html = fs.readFileSync(indexPath, 'utf8').replace(/^\uFEFF/, '');

        const nonce = crypto.randomBytes(16).toString('hex');
        const wwwrootUri = vscode.Uri.file(this.wwwrootPath);
        // VS Code's portMapping only proxies "localhost", not "127.0.0.1".
        // The webview is sandboxed — requests to 127.0.0.1 bypass port mapping
        // and silently fail. Always use localhost here.
        const loopbackHttpOrigin = `http://localhost:${port}`;
        const loopbackWsOrigin = `ws://localhost:${port}`;

        // Avoid external CDN dependency inside VS Code webview.
        html = html.replace(
            /https:\/\/cdn\.jsdelivr\.net\/npm\/@xterm\/addon-fit@0\.11\.0\/lib\/addon-fit\.js/g,
            'assets/xterm/addon-fit.js'
        );

        // VS Code webviews should be self-contained. On cold VS Code starts,
        // external font negotiation can stall behind Electron/service-worker
        // startup work and competes with xterm rendering.
        html = html.replace(
            /\s*<link[^>]+href=["']https:\/\/fonts\.(?:googleapis|gstatic)\.com[^"']*["'][^>]*>\s*/gi,
            '\n'
        );

        html = this.rewriteAssetPaths(html, webview, wwwrootUri);

        const csp = [
            `default-src 'none'`,
            // Note: 'unsafe-inline' is ignored when a nonce is present per CSP spec.
            // All app event handlers use addEventListener (no inline onclick).
            `script-src 'nonce-${nonce}' ${webview.cspSource}`,
            // The fonts.googleapis.com / fonts.gstatic.com entries below are NOT dead.
            // Stripping the <link> tags above only removes HTML tags; assets/bootstrap.min.css
            // still carries an `@import url(https://fonts.googleapis.com/css2?family=Lato...)`
            // which in turn pulls woff2 files from fonts.gstatic.com. Removing either entry
            // breaks the dashboard's body font. Self-hosting Lato would let both go away.
            `style-src ${webview.cspSource} 'unsafe-inline' https://fonts.googleapis.com`,
            `img-src ${webview.cspSource} https: data:`,
            `font-src ${webview.cspSource} https://fonts.gstatic.com`,
            `connect-src ${loopbackHttpOrigin} ${loopbackWsOrigin} http://localhost:${port} ws://localhost:${port} ${webview.cspSource}`,
            `form-action 'none'`,
            `base-uri ${webview.cspSource} 'self'`,
        ].join('; ');

        const assetsBaseUri = webview.asWebviewUri(wwwrootUri).toString();

        const fetchPatch = sessionToken ? `
        const __vb_token__ = '${sessionToken}';
        const __vb_tab_token__ = ${tabToken ? `'${tabToken}'` : 'null'};
        if (__vb_tab_token__) { sessionStorage.setItem('viberails_tab', __vb_tab_token__); }
        window.__viberails_SESSION_TOKEN__ = __vb_token__;
        const __vb_orig_fetch__ = window.fetch;
        window.fetch = function(input, init) {
            init = init || {};
            const headers = new Headers(init.headers || (input instanceof Request ? input.headers : undefined));
            headers.set('viberails_session', __vb_token__);
            if (__vb_tab_token__) { headers.set('viberails_tab', __vb_tab_token__); }
            init.headers = headers;
            return __vb_orig_fetch__.call(this, input, init);
        };
        const __vb_orig_ws__ = window.WebSocket;
        window.WebSocket = function(url, protocols) {
            // Merge BOTH tokens into subprotocols so the server can validate them. NEVER put the
            // session token in the URL: query strings land verbatim in the server's request log.
            // Both tokens are URL-safe base64 specifically so they are valid subprotocol values;
            // the server acknowledges the tab token's subprotocol, the session entry just rides
            // along unselected. Deduplicate via Set — base-websocket.js already includes the tab
            // token in its protocols array (browsers reject duplicates).
            const mergedProtocols = [...new Set([
                ...(protocols ? (Array.isArray(protocols) ? protocols : [protocols]) : []),
                __vb_token__,
                ...(__vb_tab_token__ ? [__vb_tab_token__] : [])
            ])];
            return new __vb_orig_ws__(url, mergedProtocols);
        };
        window.WebSocket.prototype = __vb_orig_ws__.prototype;
        window.WebSocket.CONNECTING = __vb_orig_ws__.CONNECTING;
        window.WebSocket.OPEN = __vb_orig_ws__.OPEN;
        window.WebSocket.CLOSING = __vb_orig_ws__.CLOSING;
        window.WebSocket.CLOSED = __vb_orig_ws__.CLOSED;
        ` : `
        window.__viberails_SESSION_TOKEN__ = null;
        `;

        // The dashboard owns its own Exit buttons (`.nav-exit-btn-sm` in the top nav and
        // sidebar, both visible by default) and wires them in app.js `setupVSCodeIntegration()`.
        // The contract between the two is the injected globals below — `__viberails_VSCODE__`,
        // `__viberails_close__`, `__viberails_setTitle__`, `__viberails_openFile__` — not any
        // DOM structure. Do not reintroduce markup-scraping button injection here.
        // The dashboard feature-detects `__viberails_openFile__`, so an older extension host
        // simply falls back to its in-app editor instead of posting a message nobody handles.
        const headInjection = `
    <meta http-equiv="Content-Security-Policy" content="${csp}">
    <base href="${assetsBaseUri}/">
    <script nonce="${nonce}">
        document.documentElement.dataset.viberailsHost = 'vscode';
        window.__viberails_API_BASE__ = '${loopbackHttpOrigin}';
        window.__viberails_VSCODE__ = true;
        window.__viberails_ASSETS_BASE__ = '${assetsBaseUri}';
        window.__viberails_NONCE__ = '${nonce}';
        const vscode = acquireVsCodeApi();
        window.__viberails_close__ = function() { vscode.postMessage({ command: 'close' }); };
        window.__viberails_setTitle__ = function(title) { vscode.postMessage({ command: 'setTitle', title: title }); };
        window.__viberails_openFile__ = function(path) { vscode.postMessage({ command: 'openFile', path: path }); };
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
