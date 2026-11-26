import { escapeHtml } from './utils.js';
import { LaunchApi } from './launch-api.js';
import { launchPresentation, libraryScripts } from './launch-presentation.js';

export const QUICK_ACTION_PREFERENCES = '/api/v1/quick-actions/preferences';
export const quickActionKey = (item) => `${item.definitionType}:${item.id}`;

/** The same server-backed visibility list is consumed by the VS Code Launch tree. */
export async function hiddenQuickActions(app, options = {}) {
    const result = await app.apiCall(QUICK_ACTION_PREFERENCES, 'GET', null, {
        showLoading: false, preferErrorResponseMessage: true, ...options
    });
    return new Set(result?.hiddenKeys || []);
}

/** Opens one native modal; switches save immediately and never edit a launch definition. */
export function openQuickActionPreferences(app, trigger) {
    const existing = document.getElementById('quick-action-preferences');
    if (existing) { existing.querySelector('button')?.focus(); return; }
    const dialog = document.createElement('dialog');
    dialog.id = 'quick-action-preferences';
    dialog.className = 'quick-action-preferences';
    dialog.setAttribute('aria-labelledby', 'quick-action-preferences-title');
    dialog.setAttribute('aria-describedby', 'quick-action-preferences-help');
    dialog.innerHTML = `
        <div class="quick-action-preferences-header">
            <h2 id="quick-action-preferences-title">Manage quick actions</h2>
            <button type="button" class="btn-close" aria-label="Close quick action settings" data-close></button>
        </div>
        <p id="quick-action-preferences-help">Choose what appears in the top-bar Play menu and VS Code Launch view on this computer. Changes save immediately. Hidden items stay available in AI Agents and Board pickers.</p>
        <div data-quick-action-list></div>
        <p role="status" data-status>Loading quick actions…</p>
        <p role="alert" class="launch-save-error" data-error></p>
        <div class="quick-action-preferences-footer">
            <button type="button" class="btn btn-link" data-manage>Manage AI Agents</button>
            <button type="button" class="btn btn-primary" data-close>Done</button>
        </div>`;
    document.body.append(dialog);
    const abort = new AbortController();
    const status = dialog.querySelector('[data-status]');
    const error = dialog.querySelector('[data-error]');
    const list = dialog.querySelector('[data-quick-action-list]');
    dialog.addEventListener('close', () => {
        abort.abort();
        dialog.remove();
        if (trigger?.isConnected) trigger.focus();
    }, { once: true });
    dialog.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click', () => dialog.close()));
    dialog.querySelector('[data-manage]').addEventListener('click', () => { dialog.close(); app.navigate('ai-agents'); });
    dialog.showModal();

    async function load() {
        status.textContent = 'Loading quick actions…';
        error.textContent = '';
        list.replaceChildren();
        try {
            const library = app.aiAgentsController?.pythonScripts;
            const [{ items }, hidden] = await Promise.all([
                new LaunchApi(app).catalog({ signal: abort.signal }),
                hiddenQuickActions(app, { signal: abort.signal }),
                library?.refresh({ quiet: true })
            ]);
            if (abort.signal.aborted) return;
            const groups = [
                ['Agents and Quick Launches', items.filter(item => !item.retired && item.definitionType === 'preset').map(item => ({ ...item, ...launchPresentation(item), key: quickActionKey(item) }))],
                ['Scripts', libraryScripts(library?.state).filter(item => item.canRun).map(item => ({ ...item, key: `script:${item.id}` }))]
            ];
            list.innerHTML = groups.filter(([, entries]) => entries.length).map(([name, entries]) => `
                <fieldset><legend>${name}</legend><div class="launch-menu-list">${entries.map(item => `
                    <label class="launch-menu-row">
                        <span class="launch-menu-icon" aria-hidden="true">${item.logoHtml}</span>
                        <span class="launch-menu-details"><span class="launch-menu-name">${escapeHtml(item.name)}</span>
                            <span class="launch-menu-subtitle">${escapeHtml(item.subtitle)}</span></span>
                        <input type="checkbox" role="switch" class="card-editor-switch" data-quick-action-key="${escapeHtml(item.key)}"
                            aria-label="Show ${escapeHtml(item.name)}" ${hidden.has(item.key) ? '' : 'checked'}>
                    </label>`).join('')}</div></fieldset>`).join('');
            status.textContent = groups.some(([, entries]) => entries.length) ? '' : 'No quick actions yet. Create one in AI Agents.';
            list.querySelectorAll('input').forEach(input => input.addEventListener('change', async () => {
                const previous = !input.checked;
                input.disabled = true;
                status.textContent = 'Saving…';
                error.textContent = '';
                try {
                    // Do not abort an accepted write when the dialog closes. Still notify the host.
                    await app.apiCall(QUICK_ACTION_PREFERENCES, 'PUT', {
                        key: input.dataset.quickActionKey, hidden: !input.checked
                    }, { showLoading: false, preferErrorResponseMessage: true });
                    status.textContent = 'Saved. Both Play menus use this choice.';
                    window.__viberails_launcherChanged__?.();
                } catch (cause) {
                    input.checked = previous;
                    status.textContent = '';
                    error.textContent = cause?.message || 'Could not save this choice. Try again.';
                    if (!dialog.isConnected) app.showError(error.textContent);
                } finally { input.disabled = false; }
            }));
        } catch (cause) {
            if (abort.signal.aborted) return;
            status.textContent = '';
            error.textContent = cause?.message || 'Could not load quick actions.';
            const retry = document.createElement('button');
            retry.type = 'button'; retry.className = 'btn btn-secondary'; retry.textContent = 'Retry';
            retry.addEventListener('click', load);
            list.append(retry);
        }
    }
    void load();
}
