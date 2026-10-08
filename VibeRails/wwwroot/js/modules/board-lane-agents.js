import { BoardApi } from './board-api.js';
import { escapeHtml, isConfirmDialogOpen } from './utils.js';
import { mountLaneAgentBody } from './board-lane-agent-body.js';
import { selectedLaneJobIds as selectedIds } from './board-lane-agent-list.js';
export { laneScriptAction, findCheckAutomation } from './board-lane-agent-add.js';
export { laneReviewerSummary } from './board-lane-agent-display.js';
const icon = name => `<i class="fa-solid fa-${name}" aria-hidden="true"></i>`;

/** Compact entry points to the existing destination-lane Automation settings. */
export class BoardLaneAgents {
    constructor(app, openRunningAgent = null) {
        this.app = app;
        this.openRunningAgent = openRunningAgent;
    }

    button(column) {
        return `<button type="button" class="board-lane-agents-button" data-board-action="lane-agents"
            data-column-id="${escapeHtml(column.id)}" aria-haspopup="dialog" aria-expanded="false"
            aria-label="Agents on entry to ${escapeHtml(column.name)}" title="Manage agents on entry to ${escapeHtml(column.name)}">
            ${icon('robot')}<span class="board-lane-agents-count" aria-hidden="true">…</span>
            <span class="board-lane-agents-caption">Agents</span>
        </button>`;
    }

    mount(root) {
        this.countAbort?.abort();
        if (this.root !== root) this.close(false);
        this.root = root;
        const buttons = [...root.querySelectorAll('[data-board-action="lane-agents"]')];
        // Pagination replaces the lane buttons, but the body-mounted panel owns live
        // drafts, focus and requests. Keep it open and attach it to the new button.
        if (this.panel) {
            const anchor = buttons.find(button => button.dataset.columnId === this.anchor?.dataset.columnId);
            if (anchor) {
                this.anchor = anchor;
                anchor.setAttribute('aria-expanded', 'true');
                anchor.setAttribute('aria-controls', this.panel.id);
                this.positionPanel?.();
            } else this.close(false);
        }
        this.countAbort = new AbortController();
        const signal = this.countAbort.signal;
        for (const button of buttons) {
            BoardApi.getLaneAutomationAsync(button.dataset.columnId, { signal }).then(settings => {
                if (!signal.aborted && button.isConnected) this.updateCount(button, settings);
            }).catch(error => {
                if (signal.aborted || !button.isConnected) return;
                button.querySelector('.board-lane-agents-count').textContent = '!';
                button.title = `Could not load agents. Click to retry. ${error?.message || ''}`;
            });
        }
    }

    updateCount(button, settings) {
        if (Number(button.dataset.agentsRevision) > settings.revision) return;
        button.dataset.agentsRevision = String(settings.revision ?? 0);
        const count = selectedIds(settings).length;
        button.querySelector('.board-lane-agents-count').textContent = String(count);
        button.title = `${count} ${count === 1 ? 'Automation' : 'Automations'} on entry. Click to manage.`;
    }

    updateColumnCount(columnId, settings) {
        this.root?.querySelectorAll('[data-board-action="lane-agents"]').forEach(button => {
            if (button.dataset.columnId === columnId) this.updateCount(button, settings);
        });
    }

    updateActivity(columnIds = []) {
        const running = new Set(columnIds);
        this.root?.querySelectorAll('[data-board-action="lane-agents"]').forEach(button => {
            const active = running.has(button.dataset.columnId);
            button.classList.toggle('is-running', active);
            button.querySelector('.board-lane-agents-caption').textContent = active ? 'Running' : 'Agents';
            button.setAttribute('aria-description', active ? 'An Automation from this lane is running.' : '');
        });
    }

    open(button, column) {
        if (this.anchor === button) { this.close(); return; }
        this.close(false);
        if (!column) return;
        const abort = new AbortController();
        const panel = document.createElement('section');
        panel.className = 'board-lane-agents-panel';
        panel.id = 'board-lane-agents-panel';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-labelledby', 'board-lane-agents-title');
        panel.innerHTML = `<header class="board-lane-agents-heading">
            <span class="board-lane-agents-heading-icon" aria-hidden="true">${icon('robot')}</span>
            <div class="board-lane-agents-heading-copy"><h2 id="board-lane-agents-title">Lane agents</h2><p>On entry to <strong>${escapeHtml(column.name)}</strong></p></div>
            <button type="button" class="board-lane-agents-action" data-agent-action="close" aria-label="Close lane agents">${icon('xmark')}</button>
        </header>
        <p class="board-lane-agents-help">Automations run top to bottom after a card stays here for 60 seconds. Each step waits for the previous step to pass or be skipped. Each Automation handles one card at a time.</p>
        <section data-lane-running aria-label="Running agents" aria-live="polite"></section>
        <div data-lane-agents-content role="status">Loading agents…</div>`;
        this.anchor = button;
        this.panel = panel;
        button.setAttribute('aria-expanded', 'true');
        button.setAttribute('aria-controls', panel.id);
        document.body.append(panel);
        const alive = () => !abort.signal.aborted && panel.isConnected;
        const position = () => {
            if (!alive() || !this.anchor?.isConnected) return;
            const anchor = this.anchor.getBoundingClientRect();
            const bounds = panel.getBoundingClientRect();
            const left = Math.max(8, Math.min(anchor.left + anchor.width / 2 - bounds.width / 2, window.innerWidth - bounds.width - 8));
            panel.style.left = `${left}px`;
            panel.style.top = `${Math.max(8, Math.min(anchor.bottom + 22, window.innerHeight - bounds.height - 8))}px`;
        };
        this.positionPanel = position;

        const disposeBody = mountLaneAgentBody({ app: this.app, panel, column, signal: abort.signal, position,
            updateCount: settings => this.updateColumnCount(column.id, settings),
            close: restoreFocus => this.close(restoreFocus), openRunningAgent: this.openRunningAgent });

        const outside = event => {
            if (!panel.contains(event.target) && !this.anchor?.contains(event.target) && !isConfirmDialogOpen()) this.close(false);
        };
        const keydown = event => {
            if (event.key !== 'Escape' || isConfirmDialogOpen()) return;
            event.preventDefault();
            event.stopImmediatePropagation();
            this.close();
        };
        const scroll = event => { if (!panel.contains(event.target)) position(); };
        document.addEventListener('pointerdown', outside);
        document.addEventListener('keydown', keydown, true);
        window.addEventListener('resize', position);
        document.addEventListener('scroll', scroll, true);
        const resizeObserver = new ResizeObserver(position);
        resizeObserver.observe(panel);
        this.cleanup = () => {
            disposeBody();
            abort.abort();
            resizeObserver.disconnect();
            document.removeEventListener('pointerdown', outside);
            document.removeEventListener('keydown', keydown, true);
            window.removeEventListener('resize', position);
            document.removeEventListener('scroll', scroll, true);
        };
        position();
        panel.querySelector('[data-agent-action="close"]').focus();
    }

    close(restoreFocus = true) {
        this.cleanup?.();
        this.cleanup = null;
        this.positionPanel = null;
        this.panel?.remove();
        this.panel = null;
        this.anchor?.setAttribute('aria-expanded', 'false');
        this.anchor?.removeAttribute('aria-controls');
        if (restoreFocus && this.anchor?.isConnected) this.anchor.focus();
        this.anchor = null;
    }

    dispose() {
        this.countAbort?.abort();
        this.close(false);
        this.root = null;
    }
}
