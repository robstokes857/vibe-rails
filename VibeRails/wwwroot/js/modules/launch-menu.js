import { escapeHtml } from './utils.js';
import { LaunchApi } from './launch-api.js';

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
        panel.className = 'launch-menu';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', 'Quick actions');
        panel.textContent = 'Loading…';
        document.body.append(panel);
        const bounds = trigger.getBoundingClientRect();
        panel.style.top = `${bounds.bottom + 6}px`;
        panel.style.right = `${Math.max(8, window.innerWidth - bounds.right)}px`;
        this.trigger = trigger;
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
            panel.innerHTML =
                items
                    .filter((item) => item.canLaunch)
                    .map(
                        (item) =>
                            `<button class="btn btn-link" data-launch-id="${escapeHtml(item.id)}">${escapeHtml(item.name)} · ${escapeHtml(item.kind)}</button>`
                    )
                    .join('') +
                '<hr><button class="btn btn-outline-secondary" data-launch-manage>Manage AI Agents</button>';
            panel.addEventListener(
                'click',
                (event) => {
                    const button = event.target.closest('button');
                    if (!button) return;
                    const item = items.find(
                        (item) => item.id === button.dataset.launchId
                    );
                    this.closeFlyout();
                    if (item)
                        void this.app.aiAgentsController.launch(
                            item.id,
                            item.definitionType
                        );
                    else this.app.navigate('ai-agents');
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
