import * as path from 'path';
import * as vscode from 'vscode';

/** Resolve the clicked file before opening a dashboard changes the active editor. */
export async function resolveScriptImportTarget(selected?: vscode.Uri): Promise<{
    filePath: string;
    projectFolder: string;
}> {
    if (!vscode.workspace.isTrusted) {
        throw new Error('Trust this workspace before adding a VibeRails script.');
    }
    const uri = selected ?? vscode.window.activeTextEditor?.document.uri;
    if (!uri || uri.scheme !== 'file' || uri.authority || !/\.(ps1|sh|py)$/i.test(uri.path)) {
        throw new Error('Select a local .ps1, .sh or .py file to add to VibeRails.');
    }
    const stat = await vscode.workspace.fs.stat(uri);
    if (stat.type !== vscode.FileType.File) {
        throw new Error('Select a script file, not a folder or symbolic link.');
    }
    return {
        filePath: uri.fsPath,
        projectFolder: vscode.workspace.getWorkspaceFolder(uri)?.uri.fsPath ?? path.dirname(uri.fsPath)
    };
}
