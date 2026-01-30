import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';

export class WebviewPanelManager {
    private panel: vscode.WebviewPanel | null = null;
    private wwwrootPath: string;
    private extensionUri: vscode.Uri;
    private _onCloseRequested: vscode.EventEmitter<void> = new vscode.EventEmitter<void>();
    public readonly onCloseRequested: vscode.Event<void> = this._onCloseRequested.event;

    constructor(extensionUri: vscode.Uri, workspaceFolder: string) {
        this.extensionUri = extensionUri;
        this.wwwrootPath = path.join(workspaceFolder, 'VibeRails', 'wwwroot');
    }

    public isVisible(): boolean {
        return this.panel !== null && this.panel.visible;
    }

    public reveal(): void {
        if (this.panel) {
            this.panel.reveal(vscode.ViewColumn.One);
        }
    }

    public async create(port: number): Promise<vscode.WebviewPanel> {
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
                localResourceRoots: [wwwrootUri]
            }
        );

        this.panel.webview.html = await this.buildHtml(this.panel.webview, port);

        // Handle messages from webview
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

    private async buildHtml(webview: vscode.Webview, port: number): Promise<string> {
        const indexPath = path.join(this.wwwrootPath, 'index.html');
        let html = fs.readFileSync(indexPath, 'utf8');

        // Generate nonce for scripts
        const nonce = this.getNonce();

        // Extract template elements before any modifications
        const templateMatches = html.match(/<template[\s\S]*?<\/template>/g) || [];

        // Rewrite asset paths to use webview URIs
        html = this.rewriteAssetPaths(html, webview);

        // Build CSP policy
        const csp = [
            `default-src 'none'`,
            `script-src 'nonce-${nonce}' ${webview.cspSource}`,
            `style-src ${webview.cspSource} 'unsafe-inline' https://fonts.googleapis.com`,
            `img-src ${webview.cspSource} data: https:`,
            `font-src ${webview.cspSource} https://fonts.gstatic.com`,
            `connect-src http://localhost:${port}`,
        ].join('; ');

        // Get the base URI for assets
        const wwwrootUri = vscode.Uri.file(this.wwwrootPath);
        const assetsBaseUri = webview.asWebviewUri(wwwrootUri).toString();

        // Inject CSP meta tag, API base URL, and VS Code API script
        const headInjection = `
    <meta http-equiv="Content-Security-Policy" content="${csp}">
    <script nonce="${nonce}">
        window.__viberails_API_BASE__ = 'http://localhost:${port}';
        window.__viberails_VSCODE__ = true;
        window.__viberails_ASSETS_BASE__ = '${assetsBaseUri}';
        const vscode = acquireVsCodeApi();
        window.__viberails_close__ = function() {
            vscode.postMessage({ command: 'close' });
        };
    </script>`;

        // Insert after opening <head> tag
        html = html.replace(/<head>/i, `<head>${headInjection}`);

        // Add nonce to all script tags
        html = html.replace(/<script/g, `<script nonce="${nonce}"`);

        return html;
    }

    private rewriteAssetPaths(html: string, webview: vscode.Webview): string {
        // Rewrite href and src attributes that point to local files
        const wwwrootUri = vscode.Uri.file(this.wwwrootPath);

        // Pattern to match src="..." or href="..." with relative paths
        const assetPattern = /(src|href)=["'](?!http|https|data:|#|javascript:)([^"']+)["']/g;

        html = html.replace(assetPattern, (match, attr, relativePath) => {
            // Skip if it's already an absolute URL or special protocol
            if (relativePath.startsWith('//') || relativePath.startsWith('data:')) {
                return match;
            }

            // Handle paths that start with / or ./
            let cleanPath = relativePath;
            if (cleanPath.startsWith('/')) {
                cleanPath = cleanPath.substring(1);
            } else if (cleanPath.startsWith('./')) {
                cleanPath = cleanPath.substring(2);
            }

            const fileUri = vscode.Uri.joinPath(wwwrootUri, cleanPath);
            const webviewUri = webview.asWebviewUri(fileUri);

            return `${attr}="${webviewUri}"`;
        });

        return html;
    }

    private getNonce(): string {
        let text = '';
        const possible = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
        for (let i = 0; i < 32; i++) {
            text += possible.charAt(Math.floor(Math.random() * possible.length));
        }
        return text;
    }

    public dispose(): void {
        if (this.panel) {
            this.panel.dispose();
            this.panel = null;
        }
    }
}
