import * as assert from 'assert/strict';
import * as vscode from 'vscode';
import { LAUNCHER_VIEW_ID, REVIEW_INBOX_VIEW_ID, REVIEW_REQUESTS_VIEW_ID } from '../../constants';
import { bindSidebarTreeView } from '../../sidebar-tree-view';

function fixture() {
    const changed = new vscode.EventEmitter<void>();
    const visibility = new vscode.EventEmitter<vscode.TreeViewVisibilityChangeEvent>();
    let message: string | undefined;
    let refreshes = 0;
    let disposed = false;
    const created: string[] = [];
    const provider = {
        onDidChangeTreeData: changed.event,
        getTreeItem: (label: string) => new vscode.TreeItem(label),
        getChildren: () => [],
        getMessage: () => message,
        refresh: async () => { refreshes++; }
    };
    const view = {
        message: undefined as string | undefined,
        onDidChangeVisibility: visibility.event,
        dispose: () => { disposed = true; }
    };
    const createTreeView: typeof vscode.window.createTreeView = <T>(id: string, options: vscode.TreeViewOptions<T>) => {
        created.push(id);
        assert.equal(options.treeDataProvider, provider);
        assert.equal(options.showCollapseAll, false);
        return view as vscode.TreeView<T>;
    };
    return {
        provider, view, created, createTreeView,
        refreshes: () => refreshes,
        disposed: () => disposed,
        change: (value?: string) => { message = value; changed.fire(); },
        show: (visible: boolean) => visibility.fire({ visible }),
        dispose: () => { changed.dispose(); visibility.dispose(); }
    };
}

suite('Sidebar view registration', () => {
    test('an old workbench registry binds Launch without registering the new review views', () => {
        const f = fixture();
        // 10.12.5 contributes only Launch. New review commands or their refresh actions
        // do not prove that the workbench has registered the corresponding views.
        const commands = new Set([
            `${LAUNCHER_VIEW_ID}.focus`,
            `${REVIEW_INBOX_VIEW_ID}.refresh`,
            `${REVIEW_REQUESTS_VIEW_ID}.open`
        ]);
        const subscriptions = [LAUNCHER_VIEW_ID, REVIEW_INBOX_VIEW_ID, REVIEW_REQUESTS_VIEW_ID]
            .flatMap(id => bindSidebarTreeView(id, f.provider, commands, f.createTreeView));
        try {
            assert.deepEqual(f.created, [LAUNCHER_VIEW_ID]);
            assert.equal(subscriptions.length, 3);
        } finally {
            subscriptions.forEach(subscription => subscription.dispose());
            f.dispose();
        }
    });

    test('no tree API calls or provider listeners are installed for an unregistered view', () => {
        const f = fixture();
        try {
            assert.deepEqual(bindSidebarTreeView(REVIEW_INBOX_VIEW_ID, f.provider, new Set(), f.createTreeView), []);
            f.change('No requests');
            f.show(true);
            assert.deepEqual(f.created, []);
            assert.equal(f.view.message, undefined);
            assert.equal(f.refreshes(), 0);
        } finally { f.dispose(); }
    });

    test('registered views retain empty messages, refresh on visibility and dispose their listeners', () => {
        for (const id of [LAUNCHER_VIEW_ID, REVIEW_INBOX_VIEW_ID, REVIEW_REQUESTS_VIEW_ID]) {
            const f = fixture();
            const subscriptions = bindSidebarTreeView(id, f.provider, new Set([`${id}.focus`]), f.createTreeView);
            try {
                assert.deepEqual(f.created, [id]);
                f.change('No requests');
                assert.equal(f.view.message, 'No requests');
                f.change();
                assert.equal(f.view.message, undefined);
                f.show(false);
                assert.equal(f.refreshes(), 0);
                f.show(true);
                assert.equal(f.refreshes(), 1);

                subscriptions.forEach(subscription => subscription.dispose());
                assert.equal(f.disposed(), true);
                f.change('Disposed');
                f.show(true);
                assert.equal(f.view.message, undefined);
                assert.equal(f.refreshes(), 1);
            } finally {
                subscriptions.forEach(subscription => subscription.dispose());
                f.dispose();
            }
        }
    });
});
