// Assign from the card face (VIBE-145).
//
// The lane card's assignee chip is one button: it opens a small anchored menu and never
// filters the board (the toolbar's assignee filter does that). The menu lists the same launch
// targets as the editor's Assignee picker ('assignee' context: Built-in CLIs, Quick Launches
// and Agents; never a shell, never an Automation Worker), including any hidden from the launch
// picker, since that list curates launching, not assignment. Choosing one saves at once through
// the ordinary card patch, so the server clears the previous base launch options exactly as a
// Save from the editor would. The menu owns its own positioning, keyboard handling and
// document listeners, the same way the lane Agents panel does; the controller only opens it,
// re-anchors it after a lane repaint and performs the save.

import { escapeHtml, isConfirmDialogOpen, canonicalLlmSelection } from './utils.js';
import { getAllLlmItems } from './pickers/llm-picker.js';
import { LAUNCH_KIND_LABELS, launchKind } from './launch-kind.js';
import { cardDisplayId } from './board-card-label.js';

const KIND_RANK = Object.freeze({ base: 0, 'quick-launch': 1, agent: 2 });
export const ASSIGN_ACTION = 'assign-card';

/**
 * The picker catalog as menu rows: built-ins first, then Quick Launches, then Agents, each in
 * the saved picker order. A saved setup the app has not loaded yet counts as an Agent, the
 * same conservative answer the picker gives. Pure: node-tested without a DOM.
 */
export function assignMenuItems(items = [], environments = []) {
    const rows = (items || []).map((item, order) => {
        let kind = 'base';
        if (item.kind === 'environment') {
            const environment = (environments || []).find(candidate =>
                Number(candidate?.id) === Number(item.environmentId)
                && String(candidate?.cli || '').toLowerCase() === String(item.cli || '').toLowerCase());
            kind = environment ? launchKind(environment) : 'agent';
        }
        return { key: canonicalLlmSelection(item.key), label: String(item.label || item.key || ''), cli: String(item.cli || ''), kind, order };
    }).filter(row => row.key && row.label);
    return rows.sort((left, right) => (KIND_RANK[left.kind] - KIND_RANK[right.kind]) || left.order - right.order);
}

/** Case-insensitive substring match on the label or the CLI name. */
export function matchesAssignQuery(row, query) {
    const text = String(query || '').trim().toLowerCase();
    if (!text) return true;
    return row.label.toLowerCase().includes(text) || row.cli.toLowerCase().includes(text);
}

/**
 * The grouped option list. `current` marks the saved assignee; `avatar(row)` renders the
 * brand mark (the controller's avatarHtml). An Unassign row leads when the card is assigned.
 */
export function assignMenuMarkup(rows, { current = '', query = '', avatar = () => '' } = {}) {
    const selected = canonicalLlmSelection(current);
    const visible = rows.filter(row => matchesAssignQuery(row, query));
    const groups = [];
    for (const row of visible) {
        const last = groups.at(-1);
        if (last?.kind === row.kind) last.rows.push(row);
        else groups.push({ kind: row.kind, rows: [row] });
    }
    const option = row => `<button type="button" class="board-assign-option${row.key === selected ? ' is-current' : ''}" role="menuitemradio"
        aria-checked="${row.key === selected ? 'true' : 'false'}" data-assign-value="${escapeHtml(row.key)}" title="${escapeHtml(row.label)}">
        ${avatar(row)}<span class="board-assign-option-label">${escapeHtml(row.label)}</span>
        ${row.key === selected ? '<i class="fa-solid fa-check board-assign-check" aria-hidden="true"></i>' : ''}
    </button>`;
    const unassign = selected && !query.trim()
        ? `<button type="button" class="board-assign-option board-assign-unassign" role="menuitem" data-assign-value="">
            <span class="board-assign-option-icon" aria-hidden="true"><i class="fa-solid fa-user-slash"></i></span>
            <span class="board-assign-option-label">Unassign</span>
        </button>`
        : '';
    if (!visible.length) {
        return `${unassign}<p class="board-assign-empty" role="presentation">${rows.length ? 'No matching agents.' : 'No agents are available.'}</p>`;
    }
    return unassign + groups.map(group => `<div class="board-assign-group" role="group" aria-label="${escapeHtml(LAUNCH_KIND_LABELS[group.kind] || group.kind)}">
        <div class="board-assign-group-title" aria-hidden="true">${escapeHtml(LAUNCH_KIND_LABELS[group.kind] || group.kind)}</div>
        ${group.rows.map(option).join('')}
    </div>`).join('');
}

/** The anchored assignee menu for lane cards. One instance per Board view. */
export class BoardCardAssign {
    /**
     * @param app the application (picker catalog, environments)
     * @param options.avatar renders a brand mark for a menu row
     * @param options.assign async (card, selection) => void; the controller saves and repaints
     */
    constructor(app, { avatar = () => '', assign = async () => {} } = {}) {
        this.app = app;
        this.avatar = avatar;
        this.assign = assign;
        this.root = null;
        this.anchor = null;
        this.panel = null;
        this.card = null;
        this.cleanup = null;
    }

    /**
     * Lane repaints replace the chip and the card summaries: keep an open menu attached to the
     * card's new chip and bound to its refreshed summary, so the current marker and the Unassign
     * row follow an assignment made elsewhere (another client, the 10-second poll).
     */
    mount(root, cards = []) {
        if (this.root !== root) this.close(false);
        this.root = root;
        if (!this.panel) return;
        const anchor = [...root.querySelectorAll(`[data-board-action="${ASSIGN_ACTION}"]`)]
            .find(button => button.closest('.board-card')?.dataset.cardId === this.card?.id);
        if (!anchor) { this.close(false); return; }
        const latest = (cards || []).find(card => card?.id === this.card?.id);
        if (latest && latest !== this.card) {
            const changed = canonicalLlmSelection(latest.assignee) !== canonicalLlmSelection(this.card.assignee);
            this.card = latest;
            if (changed) this.render?.();
        }
        this.anchor = anchor;
        anchor.setAttribute('aria-expanded', 'true');
        anchor.setAttribute('aria-controls', this.panel.id);
        this.positionPanel?.();
    }

    rows() {
        let items = [];
        try { items = getAllLlmItems(this.app, 'assignee'); } catch { items = []; }
        return assignMenuItems(items, this.app?.data?.environments || []);
    }

    open(button, card) {
        if (this.anchor === button) { this.close(); return; }
        this.close(false);
        if (!button || !card) return;
        const rows = this.rows();
        const panel = document.createElement('div');
        panel.className = 'board-assign-panel';
        panel.id = 'board-assign-panel';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-labelledby', 'board-assign-title');
        const id = cardDisplayId(card);
        panel.innerHTML = `<div class="board-assign-head">
            <span id="board-assign-title" class="board-assign-heading">Assign ${escapeHtml(id)}</span>
            <button type="button" class="board-assign-close" data-assign-action="close" aria-label="Close assignee menu"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
        </div>
        <input type="search" class="board-assign-search" data-assign-search placeholder="Filter agents…" aria-label="Filter agents" autocomplete="off" spellcheck="false">
        <div class="board-assign-list" data-assign-list role="menu" aria-label="Assignee"></div>`;
        this.anchor = button;
        this.panel = panel;
        this.card = card;
        button.setAttribute('aria-expanded', 'true');
        button.setAttribute('aria-controls', panel.id);
        document.body.append(panel);

        const list = panel.querySelector('[data-assign-list]');
        const search = panel.querySelector('[data-assign-search]');
        const render = () => {
            list.innerHTML = assignMenuMarkup(rows, { current: this.card?.assignee || '', query: search.value, avatar: this.avatar });
        };
        this.render = render;
        render();

        const alive = () => panel.isConnected && this.panel === panel;
        const position = () => {
            if (!alive() || !this.anchor?.isConnected) return;
            const anchor = this.anchor.getBoundingClientRect();
            const bounds = panel.getBoundingClientRect();
            const left = Math.max(8, Math.min(anchor.left, window.innerWidth - bounds.width - 8));
            const below = anchor.bottom + 6;
            const top = below + bounds.height > window.innerHeight - 8 && anchor.top - bounds.height - 6 >= 8
                ? anchor.top - bounds.height - 6
                : Math.max(8, Math.min(below, window.innerHeight - bounds.height - 8));
            panel.style.left = `${left}px`;
            panel.style.top = `${top}px`;
        };
        this.positionPanel = position;

        let busy = false;
        const choose = async value => {
            if (busy || !alive()) return;
            busy = true;
            panel.classList.add('is-busy');
            panel.querySelectorAll('button, input').forEach(control => { control.disabled = true; });
            try {
                await this.assign(this.card, value);
                if (alive()) this.close(false);
            } catch {
                // The controller reported the failure; the menu stays open for another try.
                if (alive()) {
                    panel.classList.remove('is-busy');
                    panel.querySelectorAll('button, input').forEach(control => { control.disabled = false; });
                    search.focus();
                }
            } finally {
                busy = false;
            }
        };
        panel.addEventListener('click', event => {
            if (event.target.closest('[data-assign-action="close"]')) { this.close(); return; }
            const option = event.target.closest('[data-assign-value]');
            if (option) void choose(option.dataset.assignValue);
        });
        search.addEventListener('input', () => { render(); position(); });
        panel.addEventListener('keydown', event => {
            const options = [...panel.querySelectorAll('[data-assign-value]:not([disabled])')];
            const index = options.indexOf(document.activeElement);
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                if (!options.length) return;
                event.preventDefault();
                const step = event.key === 'ArrowDown' ? 1 : -1;
                const next = index < 0 ? (step > 0 ? 0 : options.length - 1) : (index + step + options.length) % options.length;
                options[next].focus();
            } else if (event.key === 'Enter' && event.target === search) {
                // Enter in the filter box takes the first match once the user has typed something;
                // an untouched box never assigns by accident.
                event.preventDefault();
                const first = options.find(option => option.dataset.assignValue !== '');
                if (search.value.trim() && first) void choose(first.dataset.assignValue);
            } else if (event.key === 'Home' && index >= 0) {
                event.preventDefault();
                options[0]?.focus();
            } else if (event.key === 'End' && index >= 0) {
                event.preventDefault();
                options.at(-1)?.focus();
            }
        });

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
        this.cleanup = () => {
            document.removeEventListener('pointerdown', outside);
            document.removeEventListener('keydown', keydown, true);
            window.removeEventListener('resize', position);
            document.removeEventListener('scroll', scroll, true);
        };
        position();
        search.focus();
    }

    close(restoreFocus = true) {
        this.cleanup?.();
        this.cleanup = null;
        this.positionPanel = null;
        this.render = null;
        this.panel?.remove();
        this.panel = null;
        this.card = null;
        this.anchor?.setAttribute('aria-expanded', 'false');
        this.anchor?.removeAttribute('aria-controls');
        if (restoreFocus && this.anchor?.isConnected) this.anchor.focus();
        this.anchor = null;
    }

    dispose() {
        this.close(false);
        this.root = null;
    }
}
