import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { BackendManager } from './backend-manager';
import { WebviewPanelManager } from './webview-panel';

let backendManager: BackendManager | null = null;
let webviewManager: WebviewPanelManager | null = null;
let lastUsedFolder: string | null = null;
let statusBarItem: vscode.StatusBarItem | null = null;

export function activate(context: vscode.ExtensionContext) {
    console.log('VibeRails extension activating...');

    backendManager = new BackendManager();

    // Create status bar button (high priority = shows on the left side of status bar)
    statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 1000);
    statusBarItem.text = "$(circuit-board) VibeRails";
    statusBarItem.tooltip = "Open VibeRails Dashboard";
    statusBarItem.command = 'viberails.open';
    statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.warningBackground');
    statusBarItem.show();
    context.subscriptions.push(statusBarItem);

    const openCommand = vscode.commands.registerCommand('viberails.open', async () => {
        try {
            await openDashboard(context);
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`Failed to open VibeRails Dashboard: ${message}`);
        }
    });

    context.subscriptions.push(openCommand);
    context.subscriptions.push({
        dispose: () => {
            webviewManager?.dispose();
            backendManager?.dispose();
        }
    });

    console.log('VibeRails extension activated');
}

async function openDashboard(context: vscode.ExtensionContext): Promise<void> {
    // Get the current workspace folder (where we want to run in local context)
    const targetProjectFolder = getCurrentWorkspaceFolder();

    // Find the VibeRails installation (for executable and wwwroot)
    const viberailsInstallFolder = await resolveVibeRailsInstallation();

    // If webview already exists, just reveal it
    if (webviewManager?.isVisible()) {
        webviewManager.reveal();
        return;
    }

    // Show progress while starting backend
    await vscode.window.withProgress({
        location: vscode.ProgressLocation.Notification,
        title: 'Starting VibeRails...',
        cancellable: false
    }, async (progress) => {
        // Start backend if not running
        if (!backendManager!.isRunning()) {
            progress.report({ message: 'Starting backend server...' });
            // Pass both: installation folder (for finding exe) and target folder (for cwd/local context)
            await backendManager!.start(viberailsInstallFolder, targetProjectFolder);
        }

        const port = backendManager!.getPort();
        if (!port) {
            throw new Error('Backend started but port not available');
        }

        progress.report({ message: 'Creating dashboard...' });

        // Create webview panel (uses viberails install folder for wwwroot)
        webviewManager = new WebviewPanelManager(context.extensionUri, viberailsInstallFolder);

        // Handle close request from webview
        webviewManager.onCloseRequested(async () => {
            webviewManager?.dispose();
            await backendManager?.stop();
            vscode.window.showInformationMessage('VibeRails closed');
        });

        await webviewManager.create(port);
    });
}

function getCurrentWorkspaceFolder(): string | null {
    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (workspaceFolders && workspaceFolders.length > 0) {
        return workspaceFolders[0].uri.fsPath;
    }
    return null;
}

async function resolveVibeRailsInstallation(): Promise<string> {
    // Find where VibeRails is installed (for executable and wwwroot)

    // 1. Check config setting for executable path and derive folder
    const config = vscode.workspace.getConfiguration('viberails');
    const execPath = config.get<string>('executablePath');
    if (execPath && fs.existsSync(execPath)) {
        const possibleRoot = findVibeRailsRoot(path.dirname(execPath));
        if (possibleRoot) {
            return possibleRoot;
        }
    }

    // 2. Check if current workspace contains VibeRails
    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (workspaceFolders && workspaceFolders.length > 0) {
        for (const folder of workspaceFolders) {
            if (hasVibeRailsStructure(folder.uri.fsPath)) {
                return folder.uri.fsPath;
            }
        }
    }

    // 3. Check last used folder
    if (lastUsedFolder && fs.existsSync(lastUsedFolder) && hasVibeRailsStructure(lastUsedFolder)) {
        return lastUsedFolder;
    }

    // 4. Check common development locations
    const commonPaths = [
        path.join(process.env.USERPROFILE || process.env.HOME || '', 'source', 'VibeControl2'),
        path.join(process.env.USERPROFILE || process.env.HOME || '', 'repos', 'VibeControl2'),
        path.join(process.env.USERPROFILE || process.env.HOME || '', 'projects', 'VibeControl2'),
    ];

    for (const p of commonPaths) {
        if (hasVibeRailsStructure(p)) {
            lastUsedFolder = p;
            return p;
        }
    }

    // 5. Prompt user to select folder
    const selected = await vscode.window.showOpenDialog({
        canSelectFiles: false,
        canSelectFolders: true,
        canSelectMany: false,
        openLabel: 'Select VibeRails Installation Folder',
        title: 'Select the folder containing VibeRails (where VibeRails.csproj or the executable is located)'
    });

    if (!selected || selected.length === 0) {
        throw new Error('No folder selected. Please select the VibeRails installation folder.');
    }

    lastUsedFolder = selected[0].fsPath;
    return selected[0].fsPath;
}

function hasVibeRailsStructure(folderPath: string): boolean {
    try {
        // Check for VibeRails/wwwroot structure
        const wwwrootPath = path.join(folderPath, 'VibeRails', 'wwwroot');
        const indexPath = path.join(wwwrootPath, 'index.html');
        return fs.existsSync(wwwrootPath) && fs.existsSync(indexPath);
    } catch {
        return false;
    }
}

function findVibeRailsRoot(startPath: string): string | null {
    let current = startPath;
    for (let i = 0; i < 5; i++) { // Look up to 5 levels
        if (hasVibeRailsStructure(current)) {
            return current;
        }
        const parent = path.dirname(current);
        if (parent === current) break;
        current = parent;
    }
    return null;
}

export function deactivate() {
    console.log('VibeRails extension deactivating...');
    webviewManager?.dispose();
    backendManager?.dispose();
    webviewManager = null;
    backendManager = null;
}
