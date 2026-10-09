/**
 * Host → dashboard commands behind the VS Code Activity Bar launcher view.
 *
 * The extension lists the same catalog the nav Play flyout shows and queues automation runs
 * itself. Everything that needs dashboard UI — a script's run window (arguments, stdin, PIN),
 * the "Customize list…" modal, the Automation page — and dashboard feedback (the queued toast
 * plus a run-list refresh) is delegated here, so both entry points share one code path.
 *
 * Install this BEFORE setupVSCodeScriptImport: that call posts the ready signal which releases
 * messages the extension queued while the page was still loading.
 */
export function setupVSCodeLauncherBridge(app, host = window) {
    if (!host.__viberails_VSCODE__ || typeof host.addEventListener !== 'function') return;

    host.addEventListener('message', ({ data }) => {
        if (!data || typeof data !== 'object') return;
        switch (data.command) {
            case 'runScript':
                if (typeof data.name !== 'string' || !data.name) return;
                void settle(app, () => app.jobController?.pythonScripts?.run?.(data.name), 'Could not run that script.');
                return;
            case 'openLauncherCustomize':
                void settle(app, () => app.automationNavLauncher?.openCustomizationFromHost?.(), 'Could not open the launcher customization.');
                return;
            case 'manageAutomations':
                void settle(app, () => app.navigate?.('jobs'), 'Could not open the Automation page.');
                return;
            case 'automationQueued': {
                const message = typeof data.message === 'string' && data.message ? data.message : 'Queued.';
                app.showToast?.('Automation', message, 'success');
                void settle(app, () => app.jobController?.refreshRuns?.({ quiet: true }), null);
                return;
            }
            default:
                return;
        }
    });
}

/** Runs one action; a failure surfaces as the dashboard's usual error toast (silent when `fallback` is null). */
async function settle(app, action, fallback) {
    try {
        await action();
    } catch (error) {
        if (fallback !== null) app.showError?.(error?.message || fallback);
    }
}
