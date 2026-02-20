import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as http from 'http';
import * as path from 'path';
import * as fs from 'fs';
import { BackendManager } from './backend-manager';
import { WebviewPanelManager } from './webview-panel';

const INSTALL_DIR = path.join(process.env.USERPROFILE || process.env.HOME || '', '.vibe_rails');

let backendManager: BackendManager | null = null;
let webviewManager: WebviewPanelManager | null = null;
let statusBarItem: vscode.StatusBarItem | null = null;
let stopBarItem: vscode.StatusBarItem | null = null;

export function activate(context: vscode.ExtensionContext) {
    backendManager = new BackendManager();

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
        webviewManager?.dispose();
        await backendManager?.stop();
        webviewManager = null;
        stopBarItem?.hide();
        vscode.window.showInformationMessage('VibeRails closed');
    });

    context.subscriptions.push(openCommand);
    context.subscriptions.push(stopCommand);
    context.subscriptions.push({
        dispose: () => {
            webviewManager?.dispose();
            backendManager?.dispose();
        }
    });
}

async function openDashboard(context: vscode.ExtensionContext): Promise<void> {
    if (webviewManager?.isVisible()) {
        webviewManager.reveal();
        return;
    }

    const targetProjectFolder = getCurrentWorkspaceFolder();
    await ensureInstalled(targetProjectFolder);
    const webviewWwwrootPath = resolveWebviewWwwrootPath(targetProjectFolder);

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
        let sessionToken: string | null = null;
        if (bootstrapUrl) {
            sessionToken = await fetchSessionToken(bootstrapUrl);
        }

        webviewManager = new WebviewPanelManager(webviewWwwrootPath);

        webviewManager.onCloseRequested(async () => {
            webviewManager?.dispose();
            await backendManager?.stop();
            webviewManager = null;
            stopBarItem?.hide();
            vscode.window.showInformationMessage('VibeRails closed');
        });

        await webviewManager.create(port, sessionToken);
        stopBarItem?.show();
    });
}

function getCurrentWorkspaceFolder(): string | null {
    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (workspaceFolders && workspaceFolders.length > 0) {
        return workspaceFolders[0].uri.fsPath;
    }
    return null;
}

async function ensureInstalled(targetProjectFolder: string | null): Promise<void> {
    const exeName = process.platform === 'win32' ? 'vb.exe' : 'vb';
    const exePath = path.join(INSTALL_DIR, exeName);
    const installedWwwrootPath = path.join(INSTALL_DIR, 'wwwroot', 'index.html');
    const overrideWwwrootPath = path.join(resolveWebviewWwwrootPath(targetProjectFolder, false), 'index.html');
    const hasAnyWwwroot = fs.existsSync(installedWwwrootPath) || fs.existsSync(overrideWwwrootPath);

    if (fs.existsSync(exePath) && hasAnyWwwroot) {
        return;
    }

    const choice = await vscode.window.showInformationMessage(
        'VibeRails is not installed. Would you like to install it now?',
        { modal: true },
        'Install',
        'Cancel'
    );

    if (choice !== 'Install') {
        throw new Error('VibeRails is not installed.');
    }

    await vscode.window.withProgress({
        location: vscode.ProgressLocation.Notification,
        title: 'Installing VibeRails...',
        cancellable: false
    }, async (progress) => {
        progress.report({ message: 'Downloading from GitHub releases...' });
        await runInstallCommand();
    });

    if (!fs.existsSync(exePath)) {
        throw new Error(`Installation failed. Binary not found at ${exePath}. Check the "VibeRails Installer" output for details.`);
    }
}

function resolveWebviewWwwrootPath(targetProjectFolder: string | null, showWarning: boolean = true): string {
    const configured = vscode.workspace.getConfiguration('viberails').get<string>('devWwwroot', '').trim();
    const installedPath = path.join(INSTALL_DIR, 'wwwroot');

    if (!configured) {
        return installedPath;
    }

    const expanded = expandHomeDirectory(configured);
    const candidates: string[] = [];

    if (path.isAbsolute(expanded)) {
        candidates.push(expanded);
    }

    if (targetProjectFolder) {
        candidates.push(path.resolve(targetProjectFolder, expanded));
    }

    candidates.push(path.resolve(expanded));

    for (const candidate of new Set(candidates)) {
        if (fs.existsSync(path.join(candidate, 'index.html'))) {
            return candidate;
        }
    }

    if (showWarning) {
        vscode.window.showWarningMessage(
            `VibeRails: configured viberails.devWwwroot "${configured}" was not found. Falling back to installed wwwroot.`
        );
    }
    return installedPath;
}

function expandHomeDirectory(inputPath: string): string {
    if (!inputPath.startsWith('~')) {
        return inputPath;
    }

    const home = process.env.USERPROFILE || process.env.HOME || '';
    if (!home) {
        return inputPath;
    }

    if (inputPath === '~') {
        return home;
    }

    if (inputPath.startsWith('~/') || inputPath.startsWith('~\\')) {
        return path.join(home, inputPath.slice(2));
    }

    return inputPath;
}

async function runInstallCommand(): Promise<void> {
    return new Promise((resolve, reject) => {
        let command: string;
        let args: string[];

        if (process.platform === 'win32') {
            command = 'powershell.exe';
            args = [
                '-NoProfile',
                '-ExecutionPolicy', 'Bypass',
                '-Command',
                'irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.ps1 | iex'
            ];
        } else {
            command = '/bin/bash';
            args = [
                '-c',
                'wget -qO- https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash'
            ];
        }

        const outputChannel = vscode.window.createOutputChannel('VibeRails Installer');
        outputChannel.show(true);

        const proc = cp.spawn(command, args, {
            cwd: process.env.USERPROFILE || process.env.HOME || undefined,
            stdio: ['pipe', 'pipe', 'pipe'],
            shell: false
        });

        proc.stdout?.on('data', (data: Buffer) => outputChannel.append(data.toString()));
        proc.stderr?.on('data', (data: Buffer) => outputChannel.append(data.toString()));
        proc.on('error', (err) => reject(new Error(`Install process error: ${err.message}`)));
        proc.on('exit', (code) => {
            if (code === 0) {
                resolve();
            } else {
                reject(new Error(`Install script exited with code ${code}`));
            }
        });

        setTimeout(() => {
            if (proc.exitCode === null) {
                proc.kill();
                reject(new Error('Installation timed out after 2 minutes.'));
            }
        }, 120000);
    });
}

function fetchSessionToken(bootstrapUrl: string): Promise<string | null> {
    return new Promise((resolve) => {
        const req = http.get(bootstrapUrl, (res) => {
            res.resume();
            const setCookie = res.headers['set-cookie'];
            if (!setCookie) { resolve(null); return; }
            for (const cookie of setCookie) {
                const match = cookie.match(/viberails_session=([^;]+)/);
                if (!match) { continue; }

                // Cookies URL-encode base64 characters (%2F, %2B, %3D).
                // Backend header auth expects the raw token value.
                const encodedToken = match[1].replace(/^"|"$/g, '');
                try {
                    resolve(decodeURIComponent(encodedToken));
                } catch {
                    resolve(encodedToken);
                }
                return;
            }
            resolve(null);
        });
        req.on('error', () => resolve(null));
        req.setTimeout(5000, () => { req.destroy(); resolve(null); });
    });
}

export function deactivate() {
    webviewManager?.dispose();
    backendManager?.dispose();
    webviewManager = null;
    backendManager = null;
}
