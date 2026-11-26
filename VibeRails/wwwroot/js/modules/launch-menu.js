import { escapeHtml } from './utils.js';
import { LaunchApi } from './launch-api.js';
import { launchPresentation, libraryScripts } from './launch-presentation.js';
import { hiddenQuickActions, quickActionKey, openQuickActionPreferences } from './quick-action-preferences.js';

const footer = '<div class="launch-menu-footer"><button type="button" class="btn btn-secondary launch-menu-manage" data-launch-customize>Manage quick actions…<i class="fa-solid fa-gear" aria-hidden="true"></i></button><button type="button" class="btn btn-secondary launch-menu-manage" data-launch-manage>Manage AI Agents<i class="fa-solid fa-arrow-right" aria-hidden="true"></i></button></div>';

/**
 * The optional user message, collapsed behind a link so rows stay compact (VIBE-334).
 * The textarea keeps its text while collapsed; Run sends whatever it holds.
 */
function messageDisclosure(name, messageId) {
    const safeName = escapeHtml(name);
    return `<div class="launch-menu-message">
            <button type="button" class="btn btn-link btn-sm launch-menu-message-toggle" data-launch-message-toggle
                aria-expanded="false" aria-controls="${messageId}-panel" aria-label="Add message for ${safeName}">
                <i class="fa-solid fa-chevron-right" aria-hidden="true"></i><span>Add message</span>
            </button>
            <div class="launch-menu-message-panel" id="${messageId}-panel" hidden>
                <label class="form-label" for="${messageId}">User message <span class="text-muted">(optional)</span></label>
                <textarea class="form-control" id="${messageId}" data-launch-message rows="3" maxlength="8000" autocomplete="off"
                    aria-label="User message for ${safeName} (optional)"></textarea>
            </div>
        </div>`;
}

/** One row for a saved launch (`data-launch-id`) or a signed library script (`data-script-id`). */
export function menuRow(name, presentation, target, messageId = null) {
    return `<li class="launch-menu-row">
        <span class="launch-menu-icon">${presentation.logoHtml}</span>
        <div class="launch-menu-details">
            <span class="launch-menu-name">${escapeHtml(name)}</span>
            ${presentation.subtitle ? `<span class="launch-menu-subtitle">${escapeHtml(presentation.subtitle)}</span>` : ''}
        </div>
        <button type="button" class="btn btn-secondary launch-menu-run" ${target} aria-label="Run ${escapeHtml(name)}"><i class="fa-solid fa-play" aria-hidden="true"></i><span>Run</span></button>
        ${messageId ? messageDisclosure(name, messageId) : ''}
    </li>`;
}

/** Opens or closes a row's message box; the link reads "Edit message" while text is waiting. */
export function toggleMessage(toggle, open) {
    const panel = document.getElementById(toggle.getAttribute('aria-controls'));
    const input = panel?.querySelector('[data-launch-message]');
    if (!panel || !input) return;
    panel.hidden = !open;
    const row = toggle.closest('.launch-menu-row');
    const run = row.querySelector('.launch-menu-run');
    row.classList.toggle('has-message', open);
    // Move the existing button so Tab follows the expanded form's visual order.
    if (open) panel.append(run);
    else panel.closest('.launch-menu-message').before(run);
    toggle.setAttribute('aria-expanded', String(open));
    const verb = !open && input.value.trim() ? 'Edit' : 'Add';
    toggle.querySelector('span').textContent = `${verb} message`;
    toggle.setAttribute('aria-label', toggle.getAttribute('aria-label').replace(/^(Add|Edit) message/, `${verb} message`));
    if (open) input.focus();
}

/** Enter in a message box runs its row, as it did in the one-line field; Shift+Enter keeps a newline. */
export function runFromMessageKey(event) {
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing || !event.target.matches('[data-launch-message]')) return false;
    event.preventDefault();
    event.target.closest('.launch-menu-row').querySelector('[data-launch-id]').click();
    return true;
}

export class LaunchMenu {
    constructor(app) {
        this.app = app;
    }
    async toggle(trigger) {
        if (this.panel) {
            this.closeFlyout();
            return;
        }
        const abort = (this.abort = new AbortController());
        const panel = (this.panel = document.createElement('div'));
        panel.id = 'launch-menu';
        panel.className = 'launch-menu';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', 'Quick actions');
        panel.textContent = 'Loading…';
        document.body.append(panel);
        const bounds = trigger.getBoundingClientRect();
        panel.style.top = `${bounds.bottom + 6}px`;
        panel.style.right = `${Math.max(8, Math.min(window.innerWidth - bounds.right, window.innerWidth - panel.offsetWidth - 8))}px`;
        panel.style.maxHeight = `min(70vh, ${Math.max(0, window.innerHeight - bounds.bottom - 14)}px)`;
        this.trigger = trigger;
        trigger.setAttribute('aria-controls', panel.id);
        trigger.setAttribute('aria-haspopup', 'dialog');
        trigger.setAttribute('aria-expanded', 'true');
        document.addEventListener(
            'pointerdown',
            (event) => {
                if (
                    !panel.contains(event.target) &&
                    !trigger.contains(event.target)
                )
                    this.closeFlyout();
            },
            { signal: abort.signal }
        );
        document.addEventListener(
            'keydown',
            (event) => {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    this.closeFlyout();
                    trigger.focus();
                }
            },
            { signal: abort.signal }
        );
        try {
            // Signed library scripts follow the saved launches (VIBE-311). The scripts
            // controller reports its own load failure as an empty list, so a library that
            // cannot be read never hides the agents.
            const library = this.app.aiAgentsController?.pythonScripts;
            const [{ items }, hidden] = await Promise.all([
                new LaunchApi(this.app).catalog({ signal: abort.signal }),
                hiddenQuickActions(this.app, { signal: abort.signal }),
                library?.refresh({ quiet: true })
            ]);
            if (abort.signal.aborted) return;
            const available = items.filter((item) => item.definitionType === 'preset' && item.canLaunch && !hidden.has(quickActionKey(item)));
            const scripts = libraryScripts(library?.state).filter((script) => script.canRun && !hidden.has(`script:${script.id}`));
            const rows = [
                ...available.map((item, index) => menuRow(item.name, launchPresentation(item, { showProviderLabel: false }), `data-launch-id="${escapeHtml(item.id)}"`, item.definitionType === 'preset' ? `launch-message-${index}` : null)),
                ...scripts.map((script) => menuRow(script.name, script, `data-script-id="${escapeHtml(script.id)}"`))
            ];
            panel.innerHTML = '<h2 class="launch-menu-heading">Quick actions</h2>' +
                (rows.length
                    ? `<ul class="launch-menu-list">${rows.join('')}</ul>`
                    : '<p class="launch-menu-empty" role="status">No quick actions shown. Manage quick actions to show hidden items, or add one in AI Agents.</p>') +
                footer;
            panel.addEventListener('keydown', runFromMessageKey, { signal: abort.signal });
            panel.addEventListener(
                'click',
                (event) => {
                    const button = event.target.closest('button');
                    if (!button || !panel.contains(button)) return;
                    if (button.hasAttribute('data-launch-message-toggle')) {
                        toggleMessage(button, button.getAttribute('aria-expanded') !== 'true');
                        return;
                    }
                    const item = available.find(
                        (item) => item.id === button.dataset.launchId
                    );
                    const script = scripts.find(
                        (script) => script.id === button.dataset.scriptId
                    );
                    const userMessage = button.closest('.launch-menu-row')?.querySelector('[data-launch-message]')?.value ?? null;
                    this.closeFlyout();
                    if (item)
                        void this.app.aiAgentsController.launch(
                            item.id,
                            item.definitionType,
                            { userMessage }
                        );
                    // The private script terminal, exactly as Run on the script's own page.
                    else if (script)
                        void library.run(script.id).catch((error) =>
                            this.app.showError(error?.message || 'Could not run that script.'));
                    else if (button.hasAttribute('data-launch-manage')) this.app.navigate('ai-agents');
                    else if (button.hasAttribute('data-launch-customize')) openQuickActionPreferences(this.app, trigger);
                },
                { signal: abort.signal }
            );
            (panel.querySelector('.launch-menu-run') ?? panel.querySelector('button'))?.focus();
        } catch (error) {
            if (!abort.signal.aborted) panel.textContent = error.message;
        }
    }
    closeFlyout() {
        this.abort?.abort();
        this.panel?.remove();
        this.panel = null;
        this.trigger?.setAttribute('aria-expanded', 'false');
    }
    openCustomizationFromHost() {
        this.closeFlyout();
        openQuickActionPreferences(this.app, document.querySelector('[data-action="automation-launcher"]') || this.trigger);
    }
}
