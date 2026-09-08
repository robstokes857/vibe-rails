// ============================================
// Board (view 'board')
// ============================================
//
// A lane board for the current project: drag cards between lanes, drag lanes to
// reorder, filter the whole board down to a slice, and open a card for the full
// editor with comments.
//
// Data comes from board-api.js, which is still a local placeholder — see the
// header of that file for the swap to real endpoints. Everything in this file
// is already written against those async functions, so it does not change when
// the backend lands.
//
// UI conventions this view follows (shared with the rest of the app):
//   - app.showModal / app.closeModal for the card and lane editors
//   - confirmDialog() for destructive confirmation, never window.confirm
//   - app.showToast for success/failure, never a bespoke toast stack
//   - Font Awesome icons and the --color-* theme tokens

import { escapeHtml, confirmDialog } from './utils.js';
import { BoardApi } from './board-api.js';
import { renderCommentHtml, wrapSelectionAsCode } from './board-text.js';
import { openDiffModal } from './diff-modal.js';

const PRIORITIES = ['critical', 'high', 'medium', 'low'];
const POINTS = [1, 2, 3, 5, 8, 13];
const LANE_COLORS = ['#64748b', '#3b82f6', '#06b6d4', '#f59e0b', '#10b981', '#a855f7', '#ec4899'];
const FILTERS_STORAGE_KEY = 'viberails.board.filters.v1';

const emptyFilters = () => ({ q: '', assigneeId: '', priority: '', tag: '' });

export class BoardController {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.sortables = [];
        // Nested layers opened from the card editor. Both must be torn down on
        // navigation: the diff one owns a Monaco editor and two models.
        this.diffModal = null;
        this.sessionLayer = null;
        this.state = {
            columns: [],
            cards: [],
            members: [],
            filters: emptyFilters(),
            editingCardId: null,
            editingColumnId: null,
            editingColumnColor: LANE_COLORS[1]
        };
    }

    // ============================================
    // View lifecycle
    // ============================================

    async loadView() {
        const content = document.getElementById('app-content');
        if (!content) return;

        this.destroySortables();
        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('board-template');
        this.root = fragment.querySelector('[data-view="board"]');
        content.appendChild(fragment);
        if (!this.root) return;

        this.state.filters = this.readStoredFilters();
        this.bindShell();

        this.setBusy(true);
        let columns;
        let cards;
        let members;
        try {
            [columns, cards, members] = await Promise.all([
                BoardApi.getBoardColumnsAsync(),
                BoardApi.getBoardCardsAsync(),
                BoardApi.getBoardMembersAsync()
            ]);
        } catch (error) {
            // The view stayed mounted, so the failure belongs on screen, not in the console only.
            if (this.app.currentView !== 'board' || !this.root?.isConnected) return;
            this.setBusy(false);
            this.app.showToast('Board', error?.message || 'Failed to load the board.', 'error');
            return;
        }

        // Stand-down guard: the user may have navigated on while the load was in
        // flight, in which case this render would paint over the new view.
        if (this.app.currentView !== 'board' || !this.root?.isConnected) return;

        this.state.columns = columns;
        this.state.cards = cards;
        this.state.members = members;
        this.setBusy(false);
        this.renderAll();
    }

    unload() {
        this.destroySortables();
        this.closeDiffModal();
        this.closeSessionModal();
        this.root = null;
    }

    setBusy(busy) {
        this.root?.classList.toggle('is-loading', Boolean(busy));
    }

    // ============================================
    // Filter persistence
    // ============================================

    readStoredFilters() {
        try {
            const raw = localStorage.getItem(FILTERS_STORAGE_KEY);
            if (!raw) return emptyFilters();
            return { ...emptyFilters(), ...JSON.parse(raw) };
        } catch {
            return emptyFilters();
        }
    }

    persistFilters() {
        try {
            localStorage.setItem(FILTERS_STORAGE_KEY, JSON.stringify(this.state.filters));
        } catch {
            // Storage blocked: filters simply do not survive a reload.
        }
    }

    // ============================================
    // Derived data
    // ============================================

    memberById(id) {
        return this.state.members.find(m => m.id === id) || null;
    }

    columnById(id) {
        return this.state.columns.find(c => c.id === id) || null;
    }

    cardMatches(card) {
        const filters = this.state.filters;
        const query = filters.q.trim().toLowerCase();
        if (query) {
            const haystack = [card.key, card.title, card.description, ...(card.tags || [])].join(' ').toLowerCase();
            if (!haystack.includes(query)) return false;
        }
        if (filters.assigneeId && card.assigneeId !== filters.assigneeId) return false;
        if (filters.priority && card.priority !== filters.priority) return false;
        if (filters.tag && !(card.tags || []).includes(filters.tag)) return false;
        return true;
    }

    filteredCards() {
        return this.state.cards.filter(card => this.cardMatches(card));
    }

    allTags() {
        const tags = new Set();
        this.state.cards.forEach(card => (card.tags || []).forEach(tag => tags.add(tag)));
        return [...tags].sort();
    }

    hasActiveFilters() {
        const { q, assigneeId, priority, tag } = this.state.filters;
        return Boolean(q.trim() || assigneeId || priority || tag);
    }

    stats() {
        const visible = this.filteredCards();
        const remaining = visible.filter(card => {
            const column = this.columnById(card.columnId);
            return column && !this.isDoneLane(column);
        });
        return {
            cards: visible.length,
            points: remaining.reduce((sum, card) => sum + (Number(card.points) || 0), 0),
            blocked: visible.filter(card => card.blocked).length
        };
    }

    isDoneLane(column) {
        return /ship|done|complete/i.test(column?.name || '');
    }

    // ============================================
    // Dates
    // ============================================

    formatDateTime(iso) {
        const date = new Date(iso);
        if (Number.isNaN(date.getTime())) return '';
        return date.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
    }

    // ============================================
    // Shell binding
    // ============================================

    query(selector) {
        return this.root?.querySelector(selector) || null;
    }

    queryAll(selector) {
        return Array.from(this.root?.querySelectorAll(selector) || []);
    }

    bindShell() {
        this.root.addEventListener('click', event => this.onClick(event));
        this.root.addEventListener('keydown', event => this.onKeydown(event));

        const search = this.query('[data-board-search]');
        search?.addEventListener('input', () => {
            this.state.filters.q = search.value;
            this.persistFilters();
            this.renderAll();
        });

        const bindSelect = (selector, key) => {
            const select = this.query(selector);
            select?.addEventListener('change', () => {
                this.state.filters[key] = select.value;
                this.persistFilters();
                this.renderAll();
            });
        };
        bindSelect('[data-board-filter-assignee]', 'assigneeId');
        bindSelect('[data-board-filter-priority]', 'priority');
        bindSelect('[data-board-filter-tag]', 'tag');
    }

    // ============================================
    // Rendering
    // ============================================

    renderAll() {
        this.renderToolbar();
        this.renderLanes();
    }

    renderToolbar() {
        const stats = this.stats();
        const statsHost = this.query('[data-board-stats]');
        if (statsHost) {
            statsHost.innerHTML = `
                <span class="board-stat"><strong>${stats.cards}</strong> ${stats.cards === 1 ? 'card' : 'cards'}</span>
                <span class="board-stat"><strong>${stats.points}</strong> pts left</span>
                <span class="board-stat${stats.blocked ? ' is-warn' : ''}"><strong>${stats.blocked}</strong> blocked</span>`;
        }

        const assignee = this.query('[data-board-filter-assignee]');
        if (assignee) {
            assignee.innerHTML = '<option value="">Anyone</option>'
                + this.state.members.map(member =>
                    `<option value="${escapeHtml(member.id)}">${escapeHtml(member.name)}</option>`).join('');
            assignee.value = this.state.filters.assigneeId;
        }

        const priority = this.query('[data-board-filter-priority]');
        if (priority) priority.value = this.state.filters.priority;

        const tag = this.query('[data-board-filter-tag]');
        if (tag) {
            tag.innerHTML = '<option value="">Any tag</option>'
                + this.allTags().map(name =>
                    `<option value="${escapeHtml(name)}">${escapeHtml(name)}</option>`).join('');
            tag.value = this.state.filters.tag;
        }

        const search = this.query('[data-board-search]');
        if (search && document.activeElement !== search) search.value = this.state.filters.q;

        const clear = this.query('[data-board-clear-filters]');
        if (clear) clear.hidden = !this.hasActiveFilters();
    }

    avatarHtml(member, size = 22) {
        if (!member) {
            return `<span class="board-avatar is-empty" style="width:${size}px;height:${size}px"
                title="Unassigned" aria-label="Unassigned">?</span>`;
        }
        const fontSize = Math.max(9, Math.round(size * 0.38));
        return `<span class="board-avatar" data-assignee-id="${escapeHtml(member.id)}" role="button" tabindex="-1"
            title="${escapeHtml(member.name)} — click to filter"
            style="width:${size}px;height:${size}px;background:${escapeHtml(member.color)};font-size:${fontSize}px"
            >${escapeHtml(member.initials)}</span>`;
    }

    renderCard(card) {
        const member = this.memberById(card.assigneeId);
        const tags = (card.tags || []).slice(0, 3).map(tag => {
            const active = tag === this.state.filters.tag ? ' is-on' : '';
            return `<button type="button" class="board-tag${active}" data-board-action="filter-tag"
                data-tag="${escapeHtml(tag)}">${escapeHtml(tag)}</button>`;
        }).join('');

        return `
            <article class="board-card${card.blocked ? ' is-blocked' : ''}" data-card-id="${escapeHtml(card.id)}"
                tabindex="0" role="button" aria-label="${escapeHtml(card.key)}: ${escapeHtml(card.title)}">
                <span class="board-card-rail" data-priority="${escapeHtml(card.priority)}"
                    title="${escapeHtml(card.priority)} priority"></span>
                <div class="board-card-body">
                    <div class="board-card-top">
                        <span class="board-key">${escapeHtml(card.key)}</span>
                        ${card.points != null ? `<span class="board-points" title="Story points">${escapeHtml(card.points)}</span>` : ''}
                    </div>
                    <h3 class="board-card-title">${escapeHtml(card.title)}</h3>
                    <div class="board-card-meta">
                        <div class="board-tags">${tags}</div>
                        <div class="board-card-aside">
                            ${card.blocked ? `<i class="fa-solid fa-triangle-exclamation board-blocked"
                                title="Blocked" aria-hidden="true"></i>` : ''}
                            ${this.avatarHtml(member, 22)}
                        </div>
                    </div>
                </div>
            </article>`;
    }

    emptyLaneCopy(column, filteredOut) {
        if (filteredOut) return 'No cards match these filters.';
        if (this.isDoneLane(column)) return 'Nothing shipped yet.';
        if (/review/i.test(column.name)) return 'Park a card here when it is ready for eyes.';
        if (/build|progress/i.test(column.name)) return 'Drag work in, or add a card below.';
        if (/ready|next/i.test(column.name)) return 'Queue the next thing to build.';
        return 'Nothing here. Add a card or drag one in.';
    }

    renderLanes() {
        const scroll = this.captureScroll();
        const visible = this.filteredCards();

        const html = this.state.columns
            .slice()
            .sort((a, b) => a.position - b.position)
            .map(column => {
                const cards = visible
                    .filter(card => card.columnId === column.id)
                    .sort((a, b) => a.position - b.position);
                const total = this.state.cards.filter(card => card.columnId === column.id).length;
                const over = column.wipLimit != null && total > column.wipLimit;
                const wip = column.wipLimit != null ? `${total} / ${column.wipLimit}` : String(total);
                const list = cards.map(card => this.renderCard(card)).join('')
                    || `<p class="board-lane-empty">${escapeHtml(this.emptyLaneCopy(column, total > 0))}</p>`;

                return `
                    <section class="board-lane${over ? ' is-over-wip' : ''}" data-column-id="${escapeHtml(column.id)}"
                        style="--lane-color:${escapeHtml(column.color)}">
                        <header class="board-lane-head">
                            <button type="button" class="board-lane-grip" title="Drag to reorder this lane"
                                aria-label="Reorder ${escapeHtml(column.name)}">
                                <i class="fa-solid fa-grip-vertical" aria-hidden="true"></i>
                            </button>
                            <h2 class="board-lane-title">${escapeHtml(column.name)}</h2>
                            <span class="board-wip" title="${column.wipLimit != null ? 'Cards in lane / WIP limit' : 'Cards in lane'}">${escapeHtml(wip)}</span>
                            <button type="button" class="board-icon-btn" data-board-action="edit-lane"
                                data-column-id="${escapeHtml(column.id)}" title="Lane settings"
                                aria-label="Settings for ${escapeHtml(column.name)}">
                                <i class="fa-solid fa-ellipsis-vertical" aria-hidden="true"></i>
                            </button>
                        </header>
                        <div class="board-lane-list" data-column-id="${escapeHtml(column.id)}">${list}</div>
                        <div class="board-quick-add">
                            <input type="text" class="board-quick-add-input" data-quick-add="${escapeHtml(column.id)}"
                                placeholder="Add a card" autocomplete="off"
                                aria-label="Add a card to ${escapeHtml(column.name)}">
                        </div>
                    </section>`;
            })
            .join('');

        const host = this.query('[data-board-lanes]');
        if (host) host.innerHTML = html;
        this.restoreScroll(scroll);
        this.bindDragAndDrop();
    }

    captureScroll() {
        const map = { board: this.query('[data-board-canvas]')?.scrollLeft || 0, lanes: {} };
        this.queryAll('.board-lane').forEach(lane => {
            map.lanes[lane.dataset.columnId] = lane.querySelector('.board-lane-list')?.scrollTop || 0;
        });
        return map;
    }

    restoreScroll(map) {
        if (!map) return;
        const canvas = this.query('[data-board-canvas]');
        if (canvas) canvas.scrollLeft = map.board;
        this.queryAll('.board-lane').forEach(lane => {
            const list = lane.querySelector('.board-lane-list');
            const top = map.lanes[lane.dataset.columnId];
            if (list && top != null) list.scrollTop = top;
        });
    }

    // ============================================
    // Drag and drop
    // ============================================

    destroySortables() {
        this.sortables.forEach(sortable => {
            try {
                sortable.destroy();
            } catch {
                // Already torn down with its DOM.
            }
        });
        this.sortables = [];
    }

    bindDragAndDrop() {
        this.destroySortables();
        if (typeof window.Sortable === 'undefined') return;
        const animation = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 160;

        this.queryAll('.board-lane-list').forEach(list => {
            this.sortables.push(window.Sortable.create(list, {
                group: 'board-cards',
                animation,
                draggable: '.board-card',
                ghostClass: 'is-ghost',
                chosenClass: 'is-chosen',
                dragClass: 'is-dragging',
                emptyInsertThreshold: 12,
                onMove: () => {
                    this.queryAll('.board-lane').forEach(lane => lane.classList.remove('is-drop-target'));
                },
                onChange: event => {
                    this.queryAll('.board-lane').forEach(lane => lane.classList.remove('is-drop-target'));
                    event.to.closest('.board-lane')?.classList.add('is-drop-target');
                },
                onEnd: event => this.onCardDropped(event)
            }));
        });

        const lanes = this.query('[data-board-lanes]');
        if (!lanes) return;
        this.sortables.push(window.Sortable.create(lanes, {
            animation,
            handle: '.board-lane-grip',
            draggable: '.board-lane',
            onEnd: () => this.onLanesReordered()
        }));
    }

    async onCardDropped(event) {
        this.queryAll('.board-lane').forEach(lane => lane.classList.remove('is-drop-target'));
        const cardId = event.item.dataset.cardId;
        const columnId = event.to.dataset.columnId;
        if (!cardId || !columnId) return;

        event.to.querySelector('.board-lane-empty')?.remove();
        const position = Array.from(event.to.querySelectorAll('.board-card')).indexOf(event.item);

        try {
            await BoardApi.moveBoardCardAsync(cardId, { columnId, position });
            const column = this.columnById(columnId);
            const count = event.to.querySelectorAll('.board-card').length;
            if (column?.wipLimit != null && count > column.wipLimit) {
                this.app.showToast('Over WIP limit', `${column.name} now holds ${count} cards, over its limit of ${column.wipLimit}.`, 'warning');
            }
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to move the card.', 'error');
        }
        await this.refresh();
    }

    async onLanesReordered() {
        const orderedIds = this.queryAll('.board-lane').map(lane => lane.dataset.columnId);
        try {
            await BoardApi.reorderBoardColumnsAsync(orderedIds);
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to reorder lanes.', 'error');
        }
        await this.refresh();
    }

    async refresh() {
        try {
            const [columns, cards] = await Promise.all([
                BoardApi.getBoardColumnsAsync(),
                BoardApi.getBoardCardsAsync()
            ]);
            if (this.app.currentView !== 'board' || !this.root?.isConnected) return;
            this.state.columns = columns;
            this.state.cards = cards;
            this.renderAll();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to refresh the board.', 'error');
        }
    }

    // ============================================
    // Events
    // ============================================

    onClick(event) {
        const trigger = event.target.closest('[data-board-action]');
        const action = trigger?.dataset.boardAction;
        const cardEl = event.target.closest('.board-card');
        const avatar = event.target.closest('[data-assignee-id]');

        if (action === 'filter-tag') {
            event.preventDefault();
            event.stopPropagation();
            const next = trigger.dataset.tag;
            this.state.filters.tag = this.state.filters.tag === next ? '' : next;
            this.persistFilters();
            this.renderAll();
            return;
        }

        if (avatar && cardEl) {
            event.stopPropagation();
            const id = avatar.dataset.assigneeId;
            this.state.filters.assigneeId = this.state.filters.assigneeId === id ? '' : id;
            this.persistFilters();
            this.renderAll();
            return;
        }

        if (cardEl && !trigger) {
            this.openCardEditor(cardEl.dataset.cardId);
            return;
        }

        switch (action) {
            case 'new-card':
                this.openCardEditor(null);
                break;
            case 'add-lane':
                this.openLaneEditor(null);
                break;
            case 'edit-lane':
                this.openLaneEditor(trigger.dataset.columnId);
                break;
            case 'clear-filters':
                this.state.filters = emptyFilters();
                this.persistFilters();
                this.renderAll();
                break;
            case 'reset-data':
                this.resetBoardData();
                break;
            default:
                break;
        }
    }

    onKeydown(event) {
        if (event.key === 'Enter' && event.target.matches('[data-quick-add]')) {
            event.preventDefault();
            const input = event.target;
            const value = input.value;
            input.value = '';
            this.quickAdd(input.dataset.quickAdd, value);
            return;
        }

        if ((event.key === 'Enter' || event.key === ' ') && event.target.classList?.contains('board-card')) {
            event.preventDefault();
            this.openCardEditor(event.target.dataset.cardId);
        }
    }

    // ============================================
    // Card editor
    // ============================================
    //
    // Laid out like a Jira / older Azure DevOps work item rather than a tabbed
    // dialog: the body flows top to bottom (title, description, then the comment
    // thread at the bottom) and everything *about* the card — the fields, its
    // commits and its sessions — sits in the right-hand rail.

    async openCardEditor(cardId) {
        let card = null;
        if (cardId) {
            card = this.state.cards.find(c => c.id === cardId) || null;
            if (!card) {
                try {
                    card = await BoardApi.getBoardCardAsync(cardId);
                } catch (error) {
                    this.app.showToast('Board', error?.message || 'That card could not be opened.', 'error');
                    return;
                }
            }
        }

        this.state.editingCardId = card?.id || null;
        const columnId = card?.columnId || this.state.columns[0]?.id || '';

        this.app.showModal(card ? `${card.key} · Card` : 'New card', `
            <div class="board-card-editor" data-board-card-editor data-card-id="${escapeHtml(card?.id || '')}">
                <div class="board-editor-main">
                    <input type="text" class="form-control board-editor-title" id="board-card-title"
                        placeholder="What needs to happen" value="${escapeHtml(card?.title || '')}"
                        aria-label="Card title">

                    <section class="board-block">
                        <h3 class="board-block-label">Description</h3>
                        ${this.composerMarkup({
                            name: 'description',
                            value: card?.description || '',
                            placeholder: 'Context, repro steps, links. Use Code for a snippet.'
                        })}
                    </section>

                    <section class="board-block board-activity">
                        <h3 class="board-block-label">
                            Comments <span class="board-count" data-board-count="comments">${card?.comments?.length || 0}</span>
                        </h3>
                        <div class="board-comments" data-board-comments></div>
                        ${card ? this.composerMarkup({
                            name: 'comment',
                            value: '',
                            placeholder: 'Write a comment. Ctrl+Enter to post.',
                            submitLabel: 'Save'
                        }) : '<p class="board-editor-muted">Save the card to start a comment thread.</p>'}
                    </section>
                </div>

                <aside class="board-editor-side">
                    <div class="board-side-scroll">
                    <div class="board-side-fields">
                        <div>
                            <label class="board-editor-label" for="board-card-lane">Lane</label>
                            <select class="form-select form-select-sm" id="board-card-lane">
                                ${this.state.columns.map(column => `
                                    <option value="${escapeHtml(column.id)}"${column.id === columnId ? ' selected' : ''}>${escapeHtml(column.name)}</option>
                                `).join('')}
                            </select>
                        </div>
                        <div>
                            <label class="board-editor-label" for="board-card-assignee">Assignee</label>
                            <select class="form-select form-select-sm" id="board-card-assignee">
                                <option value="">Unassigned</option>
                                ${this.state.members.map(member => `
                                    <option value="${escapeHtml(member.id)}"${member.id === card?.assigneeId ? ' selected' : ''}>${escapeHtml(member.name)}</option>
                                `).join('')}
                            </select>
                        </div>
                        <div class="row g-2">
                            <div class="col-6">
                                <label class="board-editor-label" for="board-card-priority">Priority</label>
                                <select class="form-select form-select-sm" id="board-card-priority">
                                    ${PRIORITIES.map(priority => `
                                        <option value="${priority}"${priority === (card?.priority || 'medium') ? ' selected' : ''}>${priority[0].toUpperCase()}${priority.slice(1)}</option>
                                    `).join('')}
                                </select>
                            </div>
                            <div class="col-6">
                                <label class="board-editor-label" for="board-card-points">Points</label>
                                <select class="form-select form-select-sm" id="board-card-points">
                                    <option value="">None</option>
                                    ${POINTS.map(points => `
                                        <option value="${points}"${String(points) === String(card?.points ?? '') ? ' selected' : ''}>${points}</option>
                                    `).join('')}
                                </select>
                            </div>
                        </div>
                        <div>
                            <label class="board-editor-label" for="board-card-tags">Tags</label>
                            <input type="text" class="form-control form-control-sm" id="board-card-tags"
                                placeholder="auth, bug" value="${escapeHtml((card?.tags || []).join(', '))}">
                        </div>
                        <div class="form-check">
                            <input class="form-check-input" type="checkbox" id="board-card-blocked"
                                data-board-blocked${card?.blocked ? ' checked' : ''}>
                            <label class="form-check-label" for="board-card-blocked">Blocked</label>
                        </div>
                    </div>

                    <section class="board-side-section">
                        <h3 class="board-side-label">
                            <i class="fa-solid fa-code-branch" aria-hidden="true"></i>
                            Commits <span class="board-count" data-board-count="commits">${card?.commits?.length || 0}</span>
                        </h3>
                        <div class="board-side-list" data-board-commits></div>
                        ${card ? `
                        <form class="board-side-form" data-board-add-commit>
                            <input type="text" class="form-control form-control-sm" name="sha"
                                placeholder="Link a commit sha" autocomplete="off" aria-label="Commit sha">
                            <input type="hidden" name="message" value="">
                            <button type="submit" class="board-side-add" title="Link this commit" aria-label="Link this commit">
                                <i class="fa-solid fa-plus" aria-hidden="true"></i>
                            </button>
                        </form>` : ''}
                    </section>

                    <section class="board-side-section">
                        <h3 class="board-side-label">
                            <i class="fa-solid fa-terminal" aria-hidden="true"></i>
                            Sessions <span class="board-count" data-board-count="sessions">${card?.sessions?.length || 0}</span>
                        </h3>
                        <div class="board-side-list" data-board-sessions></div>
                        ${card ? `
                        <form class="board-side-form" data-board-add-session>
                            <input type="text" class="form-control form-control-sm" name="displayName"
                                placeholder="Name a session" autocomplete="off" aria-label="Session name">
                            <button type="submit" class="board-side-add" title="Add this session" aria-label="Add this session">
                                <i class="fa-solid fa-plus" aria-hidden="true"></i>
                            </button>
                        </form>` : ''}
                    </section>

                    </div>

                    <div class="board-editor-actions">
                        ${card ? `<button type="button" class="btn btn-sm btn-outline-danger" data-board-delete-card>
                            <i class="fa-solid fa-trash" aria-hidden="true"></i> Delete
                        </button>` : '<span></span>'}
                        <button type="button" class="btn btn-sm btn-outline-primary" data-board-save-card>Save</button>
                    </div>
                </aside>
            </div>
        `, { onClose: () => { this.state.editingCardId = null; } });

        const container = document.getElementById('modal-container');
        const dialog = container?.querySelector('.modal-dialog');
        dialog?.classList.remove('modal-lg');
        dialog?.classList.add('modal-xl', 'board-card-modal-dialog');

        const editor = container?.querySelector('[data-board-card-editor]');
        if (!editor) return;
        this.bindCardEditor(editor, card);
    }

    bindCardEditor(editor, card) {
        this.renderCommentsPanel(editor, card);
        this.renderCommitsPanel(editor, card);
        this.renderSessionsPanel(editor, card);

        this.bindComposer(editor.querySelector('[data-board-composer="description"]'), { card });
        this.bindComposer(editor.querySelector('[data-board-composer="comment"]'), {
            card,
            onSubmit: text => this.postComment(editor, text)
        });

        editor.querySelector('[data-board-save-card]')?.addEventListener('click', () => this.saveCard(editor));
        editor.querySelector('[data-board-delete-card]')?.addEventListener('click', () => this.deleteCurrentCard());

        editor.querySelector('[data-board-add-commit]')?.addEventListener('submit', event => {
            event.preventDefault();
            this.addCommit(editor, new FormData(event.currentTarget));
        });
        editor.querySelector('[data-board-add-session]')?.addEventListener('submit', event => {
            event.preventDefault();
            this.addSession(editor, new FormData(event.currentTarget));
        });

        // Ctrl+Enter saves the card from anywhere except the comment composer,
        // which owns the shortcut for posting (see bindComposer).
        editor.addEventListener('keydown', event => {
            if (!(event.ctrlKey || event.metaKey) || event.key !== 'Enter') return;
            if (event.target.closest('[data-board-composer="comment"]')) return;
            event.preventDefault();
            this.saveCard(editor);
        });

        editor.querySelector('#board-card-title')?.focus();
    }

    // ============================================
    // Composer (shared by the description and comments)
    // ============================================

    composerMarkup({ name, value = '', placeholder = '', disabled = false, submitLabel = '' }) {
        const off = disabled ? ' disabled' : '';
        return `
            <div class="board-composer" data-board-composer="${name}">
                <div class="board-composer-toolbar">
                    <button type="button" class="board-composer-btn" data-board-composer-action="code"
                        title="Format the selection as code"${off}>
                        <i class="fa-solid fa-code" aria-hidden="true"></i><span>Code</span>
                    </button>
                    <button type="button" class="board-composer-btn" data-board-composer-action="image"
                        title="Add an image (or just paste one)"${off}>
                        <i class="fa-regular fa-image" aria-hidden="true"></i><span>Image</span>
                    </button>
                    <span class="board-composer-hint">Paste or drop an image</span>
                </div>
                <textarea class="form-control board-composer-input" data-board-composer-input
                    placeholder="${escapeHtml(placeholder)}" rows="3"${off}>${escapeHtml(value)}</textarea>
                <input type="file" accept="image/*" hidden data-board-composer-file multiple>
                <div class="board-composer-busy" data-board-composer-busy hidden>Adding image…</div>
                ${submitLabel ? `<div class="board-composer-footer">
                    <button type="button" class="btn btn-sm btn-outline-primary board-composer-submit"
                        data-board-composer-action="submit"${off}>${escapeHtml(submitLabel)}</button>
                </div>` : ''}
            </div>`;
    }

    bindComposer(composer, { card = null, onSubmit = null } = {}) {
        if (!composer) return;
        const input = composer.querySelector('[data-board-composer-input]');
        const file = composer.querySelector('[data-board-composer-file]');
        if (!input) return;

        const autoGrow = () => {
            input.style.height = 'auto';
            // Grow with the content up to the CSS max-height, then let it scroll.
            input.style.height = `${input.scrollHeight}px`;
        };
        input.addEventListener('input', autoGrow);
        // Sized now rather than on the next frame: an occluded page never gets one,
        // and the description box would open at its one-line default.
        autoGrow();

        const submit = () => {
            if (!onSubmit) return;
            const text = input.value.trim();
            if (!text) return;
            onSubmit(text);
        };

        composer.querySelectorAll('[data-board-composer-action]').forEach(button => {
            button.addEventListener('click', () => {
                const action = button.dataset.boardComposerAction;
                if (action === 'code') {
                    const next = wrapSelectionAsCode(input.value, input.selectionStart, input.selectionEnd);
                    input.value = next.value;
                    input.setSelectionRange(next.selectionStart, next.selectionEnd);
                    input.focus();
                    autoGrow();
                } else if (action === 'image') {
                    file?.click();
                } else if (action === 'submit') {
                    submit();
                }
            });
        });

        input.addEventListener('keydown', event => {
            if ((event.ctrlKey || event.metaKey) && event.key === 'Enter' && onSubmit) {
                event.preventDefault();
                event.stopPropagation();
                submit();
            }
        });

        file?.addEventListener('change', () => {
            this.attachImages(composer, input, card, Array.from(file.files || []));
            file.value = '';
        });

        // The first paste handler in this codebase: pull image files off the
        // clipboard and let normal text paste through untouched.
        input.addEventListener('paste', event => {
            const images = Array.from(event.clipboardData?.files || [])
                .filter(item => item.type.startsWith('image/'));
            if (images.length === 0) return;
            event.preventDefault();
            this.attachImages(composer, input, card, images);
        });

        input.addEventListener('dragover', event => {
            if (!Array.from(event.dataTransfer?.types || []).includes('Files')) return;
            event.preventDefault();
            composer.classList.add('is-drop-target');
        });
        input.addEventListener('dragleave', () => composer.classList.remove('is-drop-target'));
        input.addEventListener('drop', event => {
            const images = Array.from(event.dataTransfer?.files || [])
                .filter(item => item.type.startsWith('image/'));
            composer.classList.remove('is-drop-target');
            if (images.length === 0) return;
            event.preventDefault();
            this.attachImages(composer, input, card, images);
        });
    }

    async attachImages(composer, input, card, files) {
        if (!files.length) return;
        if (!card?.id) {
            this.app.showToast('Board', 'Save the card before adding images.', 'warning');
            return;
        }
        const busy = composer.querySelector('[data-board-composer-busy]');
        if (busy) busy.hidden = false;
        try {
            for (const file of files) {
                const shrunk = await downscaleImage(file);
                const attachment = await BoardApi.addCardAttachmentAsync(card.id, {
                    name: file.name || 'pasted image',
                    dataUrl: shrunk.dataUrl,
                    bytes: shrunk.bytes,
                    mimeType: shrunk.mimeType
                });
                card.attachments = card.attachments || [];
                card.attachments.push(attachment);
                insertAtCursor(input, `![${attachment.name}](attachment:${attachment.id})`);
            }
            input.dispatchEvent(new Event('input', { bubbles: true }));
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to add the image.', 'error');
        } finally {
            if (busy) busy.hidden = true;
        }
    }

    // ============================================
    // Comments
    // ============================================

    renderCommentsPanel(editor, card) {
        const host = editor.querySelector('[data-board-comments]');
        if (!host) return;
        const comments = card?.comments || [];
        if (!comments.length) {
            host.innerHTML = '<p class="board-editor-muted">No comments yet. This is where the work on a card gets recorded.</p>';
            return;
        }

        const attachments = card?.attachments || [];
        host.innerHTML = comments.map(comment => {
            const author = this.memberById(comment.authorId);
            return `
                <article class="board-comment">
                    ${this.avatarHtml(author, 28)}
                    <div class="board-comment-content">
                        <div class="board-comment-meta">
                            <span class="board-comment-author">${escapeHtml(author?.name || 'Someone')}</span>
                            <span>${escapeHtml(this.formatDateTime(comment.createdAt))}</span>
                        </div>
                        <div class="board-comment-body" data-board-comment-body>${renderCommentHtml(comment.body, { attachments })}</div>
                        <button type="button" class="board-comment-more" data-board-comment-more hidden>Show more</button>
                    </div>
                </article>`;
        }).join('');

        // Measure NOW: reading scrollHeight forces a synchronous layout, so this
        // does not need to wait for a frame. That matters because
        // requestAnimationFrame does not fire at all while the page is occluded —
        // a backgrounded VS Code webview — which would otherwise leave every long
        // comment unclamped for the life of the dialog with nothing to re-measure
        // it later. The frame callback is a second pass for the case where the
        // dialog genuinely has no layout yet; it is a refinement, not the mechanism.
        this.applyCommentClamps(host);
        requestAnimationFrame(() => this.applyCommentClamps(host));
    }

    // A long comment is clamped to a readable height with an expander, so one
    // wall of text cannot bury the rest of the thread.
    applyCommentClamps(host) {
        if (!host.isConnected) return;
        host.querySelectorAll('[data-board-comment-body]').forEach(body => {
            const more = body.parentElement.querySelector('[data-board-comment-more]');
            if (!more || body.classList.contains('is-expanded')) return;
            // Clamp FIRST, then measure. Measuring an unclamped body always reports
            // scrollHeight === clientHeight, so the old order could never detect
            // overflow and nothing was ever clamped.
            body.classList.add('is-clamped');
            const overflowing = body.scrollHeight > body.clientHeight + 4;
            more.hidden = !overflowing;
            if (!overflowing) {
                body.classList.remove('is-clamped');
                return;
            }
            more.onclick = () => {
                const expanded = body.classList.toggle('is-expanded');
                body.classList.toggle('is-clamped', !expanded);
                more.textContent = expanded ? 'Show less' : 'Show more';
            };
        });
    }

    async postComment(editor, body) {
        if (!this.state.editingCardId) return;
        const composerInput = editor.querySelector('[data-board-composer="comment"] [data-board-composer-input]');
        try {
            await BoardApi.addBoardCommentAsync(this.state.editingCardId, {
                body,
                authorId: this.state.members[0]?.id || null
            });
            const card = await this.reloadEditingCard();
            if (composerInput) {
                composerInput.value = '';
                composerInput.style.height = 'auto';
            }
            this.renderCommentsPanel(editor, card);
            this.updateSectionCount(editor, 'comments', card.comments.length);
            // The thread is at the bottom of a single scrolling body, so bring the
            // new comment into view rather than leaving the reader where they were.
            // Synchronous for the same reason as the clamps: no frame is delivered
            // while the page is occluded.
            const main = editor.querySelector('.board-editor-main');
            if (main) main.scrollTop = main.scrollHeight;
            composerInput?.focus();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to post the comment.', 'error');
        }
    }

    updateSectionCount(editor, name, count) {
        const badge = editor.querySelector(`[data-board-count="${name}"]`);
        if (badge) badge.textContent = String(count);
    }

    // ============================================
    // Commits (right rail)
    // ============================================

    renderCommitsPanel(editor, card) {
        const host = editor.querySelector('[data-board-commits]');
        if (!host) return;
        const commits = [...(card?.commits || [])]
            .sort((a, b) => String(b.committedAt).localeCompare(String(a.committedAt)));

        if (!commits.length) {
            host.innerHTML = '<p class="board-side-empty">No commits linked.</p>';
            return;
        }

        host.innerHTML = commits.map(commit => `
            <div class="board-side-row" data-commit-sha="${escapeHtml(commit.sha)}">
                <button type="button" class="board-side-main" data-board-open-diff="${escapeHtml(commit.sha)}"
                    title="${escapeHtml(commit.message)} — ${escapeHtml(commit.author)}">
                    <i class="fa-solid fa-code-branch board-side-icon" aria-hidden="true"></i>
                    <span class="board-side-text">
                        <span class="board-sha">${escapeHtml(commit.shortSha)}</span>
                        <span class="board-side-sub">${escapeHtml(commit.message)}</span>
                    </span>
                </button>
                <button type="button" class="board-side-remove" data-board-remove-commit="${escapeHtml(commit.sha)}"
                    title="Unlink ${escapeHtml(commit.shortSha)}" aria-label="Unlink commit ${escapeHtml(commit.shortSha)}">
                    <i class="fa-solid fa-xmark" aria-hidden="true"></i>
                </button>
            </div>`).join('');

        host.querySelectorAll('[data-board-open-diff]').forEach(button => {
            button.addEventListener('click', () => this.openCommitDiff(card, button.dataset.boardOpenDiff));
        });
        host.querySelectorAll('[data-board-remove-commit]').forEach(button => {
            button.addEventListener('click', () => this.removeCommit(editor, card, button.dataset.boardRemoveCommit));
        });
    }

    async openCommitDiff(card, sha) {
        try {
            const commit = (card.commits || []).find(c => c.sha === sha);
            const diff = await BoardApi.getCommitDiffAsync(card.id, sha);
            this.closeDiffModal();
            // A nested layer, so the card editor underneath survives and gets focus back.
            this.diffModal = openDiffModal({
                title: `${commit?.shortSha || sha.slice(0, 7)} · ${commit?.message || 'Changes'}`,
                files: diff.files,
                onClose: () => { this.diffModal = null; }
            });
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to load the diff.', 'error');
        }
    }

    closeDiffModal() {
        this.diffModal?.close();
        this.diffModal = null;
    }

    async addCommit(editor, formData) {
        const sha = String(formData.get('sha') || '').trim();
        const message = String(formData.get('message') || '').trim();
        try {
            await BoardApi.addCardCommitAsync(this.state.editingCardId, {
                sha,
                message: message || '(no message)',
                author: this.state.members[0]?.name || 'You'
            });
            const card = await this.reloadEditingCard();
            this.renderCommitsPanel(editor, card);
            this.updateSectionCount(editor, 'commits', card.commits.length);
            editor.querySelector('[data-board-add-commit]')?.reset();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to add the commit.', 'error');
        }
    }

    async removeCommit(editor, card, sha) {
        try {
            await BoardApi.removeCardCommitAsync(card.id, sha);
            const fresh = await this.reloadEditingCard();
            this.renderCommitsPanel(editor, fresh);
            this.updateSectionCount(editor, 'commits', fresh.commits.length);
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to remove the commit.', 'error');
        }
    }

    // ============================================
    // Sessions (right rail)
    // ============================================

    renderSessionsPanel(editor, card) {
        const host = editor.querySelector('[data-board-sessions]');
        if (!host) return;
        const sessions = card?.sessions || [];

        if (!sessions.length) {
            host.innerHTML = '<p class="board-side-empty">No sessions linked.</p>';
            return;
        }

        host.innerHTML = sessions.map(session => `
            <div class="board-side-row" data-session-id="${escapeHtml(session.id)}">
                <button type="button" class="board-side-main" data-board-open-session="${escapeHtml(session.id)}"
                    title="${escapeHtml(session.displayName)}">
                    <i class="fa-solid fa-terminal board-side-icon" aria-hidden="true"></i>
                    <span class="board-side-text">
                        <span class="board-side-title">${escapeHtml(session.displayName)}</span>
                        <span class="board-side-sub board-sha">${escapeHtml(session.id.slice(0, 8))}</span>
                    </span>
                </button>
                <button type="button" class="board-side-remove" data-board-remove-session="${escapeHtml(session.id)}"
                    title="Remove this session" aria-label="Remove session ${escapeHtml(session.displayName)}">
                    <i class="fa-solid fa-xmark" aria-hidden="true"></i>
                </button>
            </div>`).join('');

        host.querySelectorAll('[data-board-open-session]').forEach(button => {
            button.addEventListener('click', () => {
                const session = sessions.find(item => item.id === button.dataset.boardOpenSession);
                if (session) this.openSessionModal(session);
            });
        });
        host.querySelectorAll('[data-board-remove-session]').forEach(button => {
            button.addEventListener('click', () => this.removeSession(editor, card, button.dataset.boardRemoveSession));
        });
    }

    // Stub for now: a session is an id and a name. The body is a deliberate
    // placeholder until sessions are tied to real captured VibeRails sessions.
    // Opened as a nested layer for the same reason the diff viewer is one: the
    // card editor underneath must survive and get focus back.
    openSessionModal(session) {
        this.closeSessionModal();
        const host = document.getElementById('modal-container');
        if (!host) return;

        const layer = document.createElement('div');
        layer.className = 'llm-picker-modal-layer board-session-layer';
        layer.innerHTML = `
            <div class="modal fade show d-block board-session-modal" tabindex="-1" role="dialog" aria-modal="true"
                aria-label="${escapeHtml(session.displayName)}">
                <div class="modal-dialog modal-dialog-centered">
                    <div class="modal-content">
                        <div class="modal-header">
                            <div>
                                <h5 class="modal-title">
                                    <i class="fa-solid fa-terminal" aria-hidden="true"></i>
                                    ${escapeHtml(session.displayName)}
                                </h5>
                                <p class="board-sha mb-0">${escapeHtml(session.id)}</p>
                            </div>
                            <button type="button" class="btn-close" data-board-session-close aria-label="Close"></button>
                        </div>
                        <div class="modal-body">
                            <p class="board-editor-muted mb-0">
                                Session content is not wired up yet. This will show the captured
                                working session once the backend records them.
                            </p>
                        </div>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show board-session-backdrop"></div>`;

        const underlying = Array.from(host.children).map(element => ({
            element,
            inert: Boolean(element.inert),
            ariaHidden: element.getAttribute('aria-hidden')
        }));
        underlying.forEach(({ element }) => {
            element.inert = true;
            element.setAttribute('aria-hidden', 'true');
        });
        const previousFocus = document.activeElement;
        host.appendChild(layer);

        const close = () => {
            document.removeEventListener('keydown', onKeydown, true);
            layer.remove();
            underlying.forEach(({ element, inert, ariaHidden }) => {
                if (!element.isConnected) return;
                element.inert = inert;
                if (ariaHidden == null) element.removeAttribute('aria-hidden');
                else element.setAttribute('aria-hidden', ariaHidden);
            });
            if (previousFocus?.isConnected) {
                try { previousFocus.focus({ preventScroll: true }); } catch { /* detached */ }
            }
            this.sessionLayer = null;
        };
        const onKeydown = event => {
            if (event.key !== 'Escape' || event.defaultPrevented) return;
            event.preventDefault();
            event.stopImmediatePropagation();
            close();
        };
        document.addEventListener('keydown', onKeydown, true);
        layer.querySelector('[data-board-session-close]')?.addEventListener('click', close);
        this.sessionLayer = { close };
        requestAnimationFrame(() => layer.querySelector('[data-board-session-close]')?.focus());
    }

    closeSessionModal() {
        this.sessionLayer?.close();
        this.sessionLayer = null;
    }

    async addSession(editor, formData) {
        try {
            await BoardApi.addCardSessionAsync(this.state.editingCardId, {
                displayName: String(formData.get('displayName') || '').trim()
            });
            const card = await this.reloadEditingCard();
            this.renderSessionsPanel(editor, card);
            this.updateSectionCount(editor, 'sessions', card.sessions.length);
            editor.querySelector('[data-board-add-session]')?.reset();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to add the session.', 'error');
        }
    }

    async removeSession(editor, card, sessionId) {
        try {
            await BoardApi.removeCardSessionAsync(card.id, sessionId);
            const fresh = await this.reloadEditingCard();
            this.renderSessionsPanel(editor, fresh);
            this.updateSectionCount(editor, 'sessions', fresh.sessions.length);
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to remove the session.', 'error');
        }
    }

    // Re-reads the open card and keeps the board's copy in step, so a panel
    // re-render and the tile behind the dialog never disagree.
    async reloadEditingCard() {
        const card = await BoardApi.getBoardCardAsync(this.state.editingCardId);
        const index = this.state.cards.findIndex(c => c.id === card.id);
        if (index >= 0) this.state.cards[index] = card;
        return card;
    }

    readCardForm(editor) {
        const value = selector => editor.querySelector(selector)?.value ?? '';
        return {
            title: value('#board-card-title').trim(),
            description: value('[data-board-composer="description"] [data-board-composer-input]'),
            columnId: value('#board-card-lane'),
            assigneeId: value('#board-card-assignee') || null,
            priority: value('#board-card-priority'),
            points: value('#board-card-points'),
            tags: value('#board-card-tags').split(',').map(tag => tag.trim()).filter(Boolean),
            blocked: Boolean(editor.querySelector('[data-board-blocked]')?.checked)
        };
    }

    async saveCard(editor) {
        const payload = this.readCardForm(editor);
        if (!payload.title) {
            this.app.showToast('Board', 'A card needs a title.', 'warning');
            editor.querySelector('#board-card-title')?.focus();
            return;
        }

        try {
            if (this.state.editingCardId) {
                await BoardApi.updateBoardCardAsync(this.state.editingCardId, payload);
                this.app.showToast('Board', 'Card saved.', 'success');
            } else {
                const created = await BoardApi.createBoardCardAsync(payload);
                this.app.showToast('Board', `Created ${created.key}.`, 'success');
            }
            this.app.closeModal();
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to save the card.', 'error');
        }
    }

    async deleteCurrentCard() {
        if (!this.state.editingCardId) return;
        const card = this.state.cards.find(c => c.id === this.state.editingCardId);
        const confirmed = await confirmDialog({
            title: 'Delete card',
            message: `Delete ${card?.key || 'this card'}? This cannot be undone.`,
            confirmLabel: 'Delete',
            danger: true
        });
        if (!confirmed) return;

        try {
            await BoardApi.deleteBoardCardAsync(this.state.editingCardId);
            this.app.closeModal();
            this.app.showToast('Board', 'Card deleted.', 'success');
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to delete the card.', 'error');
        }
    }

    async quickAdd(columnId, title) {
        const text = String(title || '').trim();
        if (!text) return;
        try {
            const card = await BoardApi.createBoardCardAsync({ columnId, title: text, priority: 'medium' });
            this.app.showToast('Board', `Created ${card.key}.`, 'success');
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to add the card.', 'error');
        }
    }

    // ============================================
    // Lane editor
    // ============================================

    openLaneEditor(columnId) {
        const column = columnId ? this.columnById(columnId) : null;
        this.state.editingColumnId = column?.id || null;
        this.state.editingColumnColor = column?.color || LANE_COLORS[1];

        this.app.showModal(column ? `Lane · ${column.name}` : 'New lane', `
            <div class="board-lane-editor" data-board-lane-editor>
                <label class="board-editor-label" for="board-lane-name">Name</label>
                <input type="text" class="form-control form-control-sm mb-3" id="board-lane-name"
                    placeholder="Lane name" value="${escapeHtml(column?.name || '')}">

                <label class="board-editor-label" for="board-lane-wip">WIP limit</label>
                <input type="number" class="form-control form-control-sm mb-3" id="board-lane-wip" min="1"
                    placeholder="None" value="${escapeHtml(column?.wipLimit ?? '')}">
                <p class="board-editor-muted mb-3">A lane over its limit is flagged, never blocked.</p>

                <span class="board-editor-label">Colour</span>
                <div class="board-swatches" data-board-swatches role="group" aria-label="Lane colour">
                    ${LANE_COLORS.map(color => `
                        <button type="button" class="board-swatch${color === this.state.editingColumnColor ? ' is-on' : ''}"
                            data-board-color="${color}" style="background:${color}"
                            aria-label="Lane colour ${color}" aria-pressed="${color === this.state.editingColumnColor}"></button>
                    `).join('')}
                </div>

                <div class="board-editor-actions mt-4">
                    ${column ? `<button type="button" class="btn btn-sm btn-outline-danger" data-board-delete-lane>
                        <i class="fa-solid fa-trash" aria-hidden="true"></i> Delete lane
                    </button>` : '<span></span>'}
                    <button type="button" class="btn btn-sm btn-outline-primary" data-board-save-lane>Save</button>
                </div>
            </div>
        `, { onClose: () => { this.state.editingColumnId = null; } });

        const container = document.getElementById('modal-container');
        const editor = container?.querySelector('[data-board-lane-editor]');
        if (!editor) return;

        editor.querySelector('[data-board-swatches]')?.addEventListener('click', event => {
            const swatch = event.target.closest('[data-board-color]');
            if (!swatch) return;
            this.state.editingColumnColor = swatch.dataset.boardColor;
            editor.querySelectorAll('[data-board-color]').forEach(button => {
                const on = button === swatch;
                button.classList.toggle('is-on', on);
                button.setAttribute('aria-pressed', String(on));
            });
        });

        editor.querySelector('[data-board-save-lane]')?.addEventListener('click', () => this.saveLane(editor));
        editor.querySelector('[data-board-delete-lane]')?.addEventListener('click', () => this.deleteCurrentLane());
        editor.querySelector('#board-lane-name')?.focus();
    }

    async saveLane(editor) {
        const name = editor.querySelector('#board-lane-name')?.value.trim();
        if (!name) {
            this.app.showToast('Board', 'A lane needs a name.', 'warning');
            editor.querySelector('#board-lane-name')?.focus();
            return;
        }
        const rawWip = editor.querySelector('#board-lane-wip')?.value ?? '';
        const wipLimit = rawWip === '' ? null : Number(rawWip);
        const color = this.state.editingColumnColor;

        try {
            if (this.state.editingColumnId) {
                await BoardApi.updateBoardColumnAsync(this.state.editingColumnId, { name, wipLimit, color });
                this.app.showToast('Board', 'Lane saved.', 'success');
            } else {
                await BoardApi.createBoardColumnAsync({ name, wipLimit, color });
                this.app.showToast('Board', 'Lane added.', 'success');
            }
            this.app.closeModal();
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to save the lane.', 'error');
        }
    }

    async deleteCurrentLane() {
        if (!this.state.editingColumnId) return;
        const column = this.columnById(this.state.editingColumnId);
        const count = this.state.cards.filter(card => card.columnId === this.state.editingColumnId).length;
        const suffix = count
            ? ` ${count} ${count === 1 ? 'card moves' : 'cards move'} to the first remaining lane.`
            : '';
        const confirmed = await confirmDialog({
            title: 'Delete lane',
            message: `Delete the ${column?.name || 'selected'} lane?${suffix}`,
            confirmLabel: 'Delete',
            danger: true
        });
        if (!confirmed) return;

        try {
            await BoardApi.deleteBoardColumnAsync(this.state.editingColumnId);
            this.app.closeModal();
            this.app.showToast('Board', 'Lane deleted.', 'success');
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to delete the lane.', 'error');
        }
    }

    // ============================================
    // Sample data
    // ============================================

    async resetBoardData() {
        const confirmed = await confirmDialog({
            title: 'Reset board',
            message: 'Restore the sample board? Every card and lane you have changed here is discarded.',
            confirmLabel: 'Reset',
            danger: true
        });
        if (!confirmed) return;

        try {
            await BoardApi.resetBoardDataAsync();
            this.state.filters = emptyFilters();
            this.persistFilters();
            await this.loadView();
            this.app.showToast('Board', 'Sample board restored.', 'success');
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to reset the board.', 'error');
        }
    }
}

// ============================================
// Image helpers
// ============================================

const MAX_IMAGE_DIMENSION = 1200;
// Above this, re-encode as JPEG: a full-size PNG screenshot will exhaust the
// placeholder's localStorage budget after only a handful of images.
const PNG_BUDGET_BYTES = 300 * 1024;

/** Decodes a File into something drawImage accepts, with a release callback. */
async function loadImageSource(file) {
    if (typeof createImageBitmap === 'function') {
        try {
            const bitmap = await createImageBitmap(file);
            return { source: bitmap, width: bitmap.width, height: bitmap.height, release: () => bitmap.close?.() };
        } catch {
            // Some webviews reject createImageBitmap; fall through to an <img>.
        }
    }
    const url = URL.createObjectURL(file);
    try {
        const image = new Image();
        await new Promise((resolve, reject) => {
            image.onload = resolve;
            image.onerror = () => reject(new Error('That image could not be read.'));
            image.src = url;
        });
        // The object URL has to outlive drawImage, so it is revoked in release().
        return {
            source: image,
            width: image.naturalWidth,
            height: image.naturalHeight,
            release: () => URL.revokeObjectURL(url)
        };
    } catch (error) {
        URL.revokeObjectURL(url);
        throw error;
    }
}

/**
 * Shrinks an image to a sane size for a comment and returns a data URL.
 * The placeholder data layer stores that URL directly; the real backend will
 * store bytes and hand back its own URL instead.
 */
async function downscaleImage(file, maxDimension = MAX_IMAGE_DIMENSION) {
    const { source, width, height, release } = await loadImageSource(file);
    try {
        const scale = Math.min(1, maxDimension / Math.max(width, height));
        const targetWidth = Math.max(1, Math.round(width * scale));
        const targetHeight = Math.max(1, Math.round(height * scale));

        const canvas = document.createElement('canvas');
        canvas.width = targetWidth;
        canvas.height = targetHeight;
        const context = canvas.getContext('2d');
        if (!context) throw new Error('This browser would not give us a canvas to resize the image.');
        context.drawImage(source, 0, 0, targetWidth, targetHeight);

        // PNG first — screenshots of text stay crisp. Fall back to JPEG when that
        // is too heavy to be worth it.
        let mimeType = 'image/png';
        let dataUrl = canvas.toDataURL(mimeType);
        if (dataUrl.length * 0.75 > PNG_BUDGET_BYTES) {
            mimeType = 'image/jpeg';
            dataUrl = canvas.toDataURL(mimeType, 0.85);
        }
        return { dataUrl, mimeType, bytes: Math.round(dataUrl.length * 0.75) };
    } finally {
        release();
    }
}

/** Inserts text on its own line at the caret and leaves the caret after it. */
function insertAtCursor(input, text) {
    const start = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? start;
    const before = input.value.slice(0, start);
    const after = input.value.slice(end);
    const lead = before && !before.endsWith('\n') ? '\n' : '';
    input.value = `${before}${lead}${text}\n${after}`;
    const caret = before.length + lead.length + text.length + 1;
    input.setSelectionRange(caret, caret);
    input.focus();
}
