import * as vscode from 'vscode';

interface SidebarTreeDataProvider<T> extends vscode.TreeDataProvider<T> {
    readonly onDidChangeTreeData: NonNullable<vscode.TreeDataProvider<T>['onDidChangeTreeData']>;
    getMessage(): string | undefined;
    refresh(): Promise<void>;
}

/**
 * Bind only views the workbench has registered. After an extension update the host can
 * run new code while the window still has the previous manifest's view contributions.
 * The workbench registers a <viewId>.focus command for each contributed view; checking
 * that registry avoids an asynchronous "No view is registered" notification that a
 * try/catch around createTreeView cannot catch. New views become available on reload.
 */
export function bindSidebarTreeView<T>(
    viewId: string,
    provider: SidebarTreeDataProvider<T>,
    registeredCommands: ReadonlySet<string>,
    createTreeView: typeof vscode.window.createTreeView = vscode.window.createTreeView
): vscode.Disposable[] {
    if (!registeredCommands.has(`${viewId}.focus`)) {
        return [];
    }

    const view = createTreeView<T>(viewId, { treeDataProvider: provider, showCollapseAll: false });
    return [
        view,
        provider.onDidChangeTreeData(() => { view.message = provider.getMessage(); }),
        view.onDidChangeVisibility(event => { if (event.visible) { void provider.refresh(); } })
    ];
}
