/**
 * VS Code launcher commands use the same definition editor and template/terminal
 * flow as the browser. Actions can be queued by the host; presets go through the
 * shared prompt resolver before creating a revision-checked run.
 * Older script and Automation command names remain compatibility aliases.
 * Install before setupVSCodeScriptImport releases queued host messages.
 */
import { isParentMessage } from './parent-message.js';

export function setupVSCodeLauncherBridge(app, host = window) {
    if (!host.__viberails_VSCODE__ || typeof host.addEventListener !== 'function') return;

    host.addEventListener('message', ({ source, data }) => {
        if (!isParentMessage({ source }, host)) return;
        if (!data || typeof data !== 'object') return;
        switch (data.command) {
            case 'launchDefinition':
                if (typeof data.definitionId !== 'string' || !data.definitionId || data.definitionType !== 'preset') return;
                void settle(app, () => app.aiAgentsController?.launch(data.definitionId, 'preset'), 'Could not launch this definition.');
                return;
            case 'launchQueued':
                app.showToast?.('AI Agents', 'Run queued.', 'success');
                if (typeof data.definitionId === 'string' && ['preset', 'action'].includes(data.definitionType))
                    void settle(app, () => app.aiAgentsController?.openEditor(data.definitionType, data.definitionId), null);
                return;
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
            case 'openReviewInbox':
                // The Activity Bar "Reviews requested of you" view: a row click lands on the dashboard page.
                void settle(app, () => app.navigate?.('review-inbox'), 'Could not open Reviews requested of you.');
                return;
            case 'openReviewRequest':
                // The Activity Bar "Review requests" view (slice F): a row click opens the request's card on
                // the Board, where its Request review section lists the decisions, or the Board itself.
                void settle(app, () => app.navigate?.('board', typeof data.cardKey === 'string' && data.cardKey ? { openCardId: data.cardKey } : {}), 'Could not open the Board.');
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

    followScriptChanges(app, host);
}

/**
 * Only the scripts controller learns that a script was signed, revoked, renamed, edited,
 * imported or deleted (it refetches its own state), so relay the launcher-relevant part of
 * every state update. A focus refresh that brings back the same list stays quiet.
 */
function followScriptChanges(app, host) {
    const scripts = app.jobController?.pythonScripts;
    if (typeof scripts?.onStateChange !== 'function') return;
    let last = launcherScriptSignature(scripts.state);
    scripts.onStateChange((state) => {
        const next = launcherScriptSignature(state);
        if (next === last) return;
        last = next;
        host.__viberails_launcherChanged__?.();
    });
}

/** The fields that decide a script's launcher row: id, label, file name (runtime icon) and approval. */
function launcherScriptSignature(state) {
    if (!Array.isArray(state?.scripts)) return null;
    return state.scripts
        .map((script) => [script.name, script.displayName, script.fileName, script.status].map((value) => value || '').join('\u001f'))
        .join('\n');
}

/** Runs one action; a failure surfaces as the dashboard's usual error toast (silent when `fallback` is null). */
async function settle(app, action, fallback) {
    try {
        await action();
    } catch (error) {
        if (fallback !== null) app.showError?.(error?.message || fallback);
    }
}
