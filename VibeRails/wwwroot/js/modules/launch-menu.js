import { escapeHtml } from './utils.js';
import { LaunchApi } from './launch-api.js';
import { launchPresentation } from './launch-presentation.js';

const footer = '<div class="launch-menu-footer"><button type="button" class="btn btn-link launch-menu-manage" data-launch-manage>Manage AI Agents<i class="fa-solid fa-arrow-right" aria-hidden="true"></i></button></div>';

function launchRow(item) {
    const presentation = launchPresentation(item);
    return `<li class="launch-menu-row">
        <span class="launch-menu-icon">${presentation.logoHtml}</span>
        <div class="launch-menu-details">
            <span class="launch-menu-name">${escapeHtml(item.name)}</span>
            <span class="launch-menu-subtitle">${escapeHtml(presentation.subtitle)}</span>
        </div>
        <button type="button" class="btn btn-secondary launch-menu-run" data-launch-id="${escapeHtml(item.id)}" aria-label="Run ${escapeHtml(item.name)}"><i class="fa-solid fa-play" aria-hidden="true"></i><span>Run</span></button>
    </li>`;
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
            const { items } = await new LaunchApi(this.app).catalog({
                signal: abort.signal
            });
            if (abort.signal.aborted) return;
            const available = items.filter((item) => item.canLaunch);
            panel.innerHTML = '<h2 class="launch-menu-heading">Quick actions</h2>' +
                (available.length
                    ? `<ul class="launch-menu-list">${available.map(launchRow).join('')}</ul>`
                    : '<p class="launch-menu-empty" role="status">No quick actions available. Manage AI Agents to add one.</p>') +
                footer;
            panel.addEventListener(
                'click',
                (event) => {
                    const button = event.target.closest('button');
                    if (!button || !panel.contains(button)) return;
                    const item = items.find(
                        (item) => item.id === button.dataset.launchId
                    );
                    this.closeFlyout();
                    if (item)
                        void this.app.aiAgentsController.launch(
                            item.id,
                            item.definitionType
                        );
                    else if (button.hasAttribute('data-launch-manage')) this.app.navigate('ai-agents');
                },
                { signal: abort.signal }
            );
            panel.querySelector('button')?.focus();
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
        this.app.navigate('ai-agents');
    }
}
