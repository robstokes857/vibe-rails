import * as vscode from 'vscode';
import * as http from 'http';
import * as path from 'path';
import * as fs from 'fs';
import { BackendManager } from './backend-manager';
import { WebviewPanelManager } from './webview-panel';

interface BundledAssets {
    target: string;
    exePath: string;
    wwwrootPath: string;
}

let backendManager: BackendManager | null = null;
let webviewManager: WebviewPanelManager | null = null;
let statusBarItem: vscode.StatusBarItem | null = null;
let stopBarItem: vscode.StatusBarItem | null = null;
let closingPromise: Promise<void> | null = null;

export function activate(context: vscode.ExtensionContext) {
    statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 1000);
    statusBarItem.text = "$(rocket) VibeRails";
    statusBarItem.tooltip = "Open VibeRails Dashboard";
    statusBarItem.command = 'viberails.open';
    statusBarItem.color = '#c084fc';
    statusBarItem.show();
    context.subscriptions.push(statusBarItem);

    stopBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 999);
    stopBarItem.text = "$(close)";
    stopBarItem.tooltip = "Stop VibeRails";
    stopBarItem.command = 'viberails.stop';
    stopBarItem.color = '#c084fc';
    context.subscriptions.push(stopBarItem);

    const openCommand = vscode.commands.registerCommand('viberails.open', async () => {
        try {
            await openDashboard(context);
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`Failed to open VibeRails Dashboard: ${message}`);
        }
    });

    const stopCommand = vscode.commands.registerCommand('viberails.stop', async () => {
        await closeDashboard(true, false);
    });

    context.subscriptions.push(openCommand);
    context.subscriptions.push(stopCommand);
    context.subscriptions.push({
        dispose: () => {
            void closeDashboard(false, true);
        }
    });
}

async function closeDashboard(showMessage: boolean, shutdownBackend: boolean): Promise<void> {
    if (closingPromise) {
        await closingPromise;
        return;
    }

    closingPromise = (async () => {
        webviewManager?.dispose();
        webviewManager = null;
        stopBarItem?.hide();

        const manager = backendManager;
        if (manager) {
            if (shutdownBackend) {
                await manager.shutdown();
            } else {
                await manager.stop();
            }
        }

        if (showMessage) {
            vscode.window.showInformationMessage('VibeRails closed');
        }
    })();

    try {
        await closingPromise;
    } finally {
        closingPromise = null;
    }
}

async function openDashboard(context: vscode.ExtensionContext): Promise<void> {
    if (webviewManager?.isVisible()) {
        webviewManager.reveal();
        return;
    }

    const bundledAssets = resolveBundledAssets(context);
    const targetProjectFolder = getCurrentWorkspaceFolder();
    backendManager ??= new BackendManager(bundledAssets.exePath);

    await vscode.window.withProgress({
        location: vscode.ProgressLocation.Notification,
        title: 'Starting VibeRails...',
        cancellable: false
    }, async (progress) => {
        if (!backendManager!.isRunning()) {
            progress.report({ message: 'Starting backend server...' });
            await backendManager!.start(targetProjectFolder);
        }

        const port = backendManager!.getPort();
        if (!port) {
            throw new Error('Backend started but port not available');
        }

        progress.report({ message: 'Creating dashboard...' });

        const bootstrapUrl = backendManager!.getBootstrapUrl();
        if (!bootstrapUrl) {
            throw new Error('Backend started but bootstrap URL was not available');
        }

        const { sessionToken, tabToken } = await fetchTokens(bootstrapUrl);

        webviewManager = new WebviewPanelManager(bundledAssets.wwwrootPath);

        webviewManager.onCloseRequested(() => { void closeDashboard(true, false); });

        await webviewManager.create(port, sessionToken, tabToken);
        stopBarItem?.show();
    });
}

function getCurrentWorkspaceFolder(): string | null {
    const activeUri = vscode.window.activeTextEditor?.document?.uri;
    if (activeUri) {
        const activeFolder = vscode.workspace.getWorkspaceFolder(activeUri);
        if (activeFolder) {
            return activeFolder.uri.fsPath;
        }
    }

    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (workspaceFolders && workspaceFolders.length > 0) {
        return workspaceFolders[0].uri.fsPath;
    }
    return null;
}

function resolveBundledAssets(context: vscode.ExtensionContext): BundledAssets {
    const target = getSupportedExtensionTarget();
    const exeName = process.platform === 'win32' ? 'vb.exe' : 'vb';
    const basePath = path.join(context.extensionPath, 'bin', target);
    const exePath = path.join(basePath, exeName);
    const wwwrootPath = path.join(basePath, 'wwwroot');
    const indexPath = path.join(wwwrootPath, 'index.html');

    if (!fs.existsSync(exePath) || !fs.existsSync(indexPath)) {
        throw new Error(
            `Bundled VibeRails backend is missing for ${target}. Reinstall the extension or rebuild the packaged assets.`
        );
    }

    return {
        target,
        exePath,
        wwwrootPath
    };
}

function getSupportedExtensionTarget(): string {
    if (process.platform === 'win32' && process.arch === 'x64') {
        return 'win32-x64';
    }

    if (process.platform === 'linux' && process.arch === 'x64') {
        return 'linux-x64';
    }

    if (process.platform === 'darwin' && process.arch === 'x64') {
        return 'darwin-x64';
    }

    if (process.platform === 'darwin' && process.arch === 'arm64') {
        return 'darwin-arm64';
    }

    throw new Error(`Unsupported platform for bundled VibeRails backend: ${process.platform}-${process.arch}`);
}

function fetchTokens(bootstrapUrl: string): Promise<{ sessionToken: string, tabToken: string }> {
    return new Promise((resolve, reject) => {
        const req = http.get(bootstrapUrl, (res) => {
            res.resume();

            const statusCode = res.statusCode ?? 0;
            if (statusCode < 200 || statusCode >= 300) {
                reject(new Error(`Bootstrap request failed with status ${statusCode}`));
                return;
            }

            let sessionToken: string | null = null;
            const setCookie = res.headers['set-cookie'];
            if (setCookie) {
                for (const cookie of setCookie) {
                    const match = cookie.match(/viberails_session=([^;]+)/);
                    if (!match) { continue; }
                    // Cookies URL-encode base64 characters (%2F, %2B, %3D).
                    // Backend header auth expects the raw token value.
                    const encodedToken = match[1].replace(/^"|"$/g, '');
                    try {
                        sessionToken = decodeURIComponent(encodedToken);
                    } catch {
                        sessionToken = encodedToken;
                    }
                    break;
                }
            }

            const tabHeader = res.headers['viberails_tab'];
            const tabToken = Array.isArray(tabHeader) ? (tabHeader[0] ?? null) : (tabHeader ?? null);

            if (!sessionToken) {
                reject(new Error('Bootstrap did not return a session token'));
                return;
            }

            if (!tabToken) {
                reject(new Error('Bootstrap did not return a tab token'));
                return;
            }

            resolve({ sessionToken, tabToken });
        });
        req.on('error', (error) => reject(error));
        req.setTimeout(5000, () => {
            req.destroy(new Error('Bootstrap request timed out'));
        });
    });
}

export async function deactivate(): Promise<void> {
    await closeDashboard(false, true);
    webviewManager = null;
    backendManager = null;
}
