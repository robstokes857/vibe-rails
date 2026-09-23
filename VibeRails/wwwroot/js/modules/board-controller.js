// ============================================
// Board (view 'board')
// ============================================
//
// A lane board for the current project: drag cards between lanes, drag lanes to
// reorder, filter the whole board down to a slice, and open a card for the full
// editor with comments.
//
// Data comes from board-api.js, a thin client over /api/v1/board/* (boards are
// per project; the server scopes every call). A project can hold several boards
// (sprints, sub-projects): the picker top-left switches between them, and the
// card editor's Lane field lists every board's lanes so a card can move across.
// Card keys (VB-n) are unique across the project. Cards are work items for LLMs: the
// assignee is an LLM picker key (base:claude / env:7:codex), "Start work" opens a
// terminal tab with the card prepended to that LLM's initial message, and the
// LLM reads/updates the card over the viberails-mcp board tools. A card's
// Sessions rail lists those terminals; a live one shows a dot on the lane card.
//
// TODO(board): "Auto Launch" — an option on the card (and/or a lane) so that
// dropping an assigned card into that lane starts work by itself. Not built yet;
// see the plan from 2026-09-09. The server side would live next to the launch
// route (BoardLaunchService).
//
// UI conventions this view follows (shared with the rest of the app):
//   - app.showModal / app.closeModal for the card and lane editors
//   - confirmDialog() for destructive confirmation, never window.confirm
//   - app.showToast for success/failure, never a bespoke toast stack
//   - Font Awesome icons and the --color-* theme tokens

import { escapeHtml, confirmDialog, parseLlmSelection, getCliBrand, canonicalLlmSelection } from './utils.js';
import { mountLlmPicker, setLlmPickerValue, getEnabledLlmItems } from './pickers/llm-picker.js';
import { BoardApi } from './board-api.js';
import { boardContextSection, laneAutomationSection, mountBoardContext, mountLaneAutomation } from './board-settings.js';
import { renderCardLinksSection, bindCardLinks } from './board-card-links.js';
import { renderCommentHtml, wrapSelectionAsCode, toPlainPreview } from './board-text.js';
import { openDiffModal } from './diff-modal.js';
import * as SessionDebug from './session-viewer.js';
import { renderBoardLaunchOptions, readBoardLaunchOptions, bindBoardLaunchOptions } from './board-launch-options.js';
import { openBoardAttachment, disposeBoardAttachmentPreview, fileToAttachmentPayload, getAttachmentPreviewKind } from './board-attachments.js';

const PRIORITIES = ['critical', 'high', 'medium', 'low'];
const CARD_TYPES = [
    { value: 'task', label: 'Task' },
    { value: 'bug', label: 'Bug' },
    { value: 'feature', label: 'Feature' },
    { value: 'research-spike', label: 'Research spike' },
    { value: 'chore', label: 'Chore / tech debt' }
];
const POINTS = [1, 2, 3, 5, 8, 13];
const LANE_COLORS = ['#64748b', '#3b82f6', '#06b6d4', '#f59e0b', '#10b981', '#a855f7', '#ec4899'];
const FILTERS_STORAGE_KEY = 'viberails.board.filters.v1';
// The last board the user looked at. Board ids are unique across projects, so a stale id from
// another workspace simply fails to match and the first board is shown.
const BOARD_STORAGE_KEY = 'viberails.board.selected.v1';
const RASTER_DATA_URL_RE = /^data:image\/(?:png|jpeg|gif|webp);base64,/i;

const emptyFilters = () => ({ q: '', assignee: '', type: '', priority: '', tag: '' });
// Task-key namespace for the terminal tab that works a card (see wwwroot/AGENTS.md).
const CARD_TASK_KEY = cardId => `board-card:${cardId}`;

function formatFileSize(bytes) {
    const size = Math.max(0, Number(bytes) || 0);
    return size >= 1024 * 1024 ? `${(size / (1024 * 1024)).toFixed(1)} MB`
        : size >= 1024 ? `${Math.ceil(size / 1024)} KB` : `${size} bytes`;
}

function cardType(value) {
    return CARD_TYPES.find(item => item.value === value) || CARD_TYPES[0];
}

export class BoardController {
    constructor(app) {
        this.app = app;
        this.root = null;
        this.sortables = [];
        // Nested layers opened from the card editor. Both must be torn down on
        // navigation: the diff one owns a Monaco editor and two models.
        this.diffModal = null;
        this.sessionLayer = null;
        // Shared LLM pickers for assignment and the independent discussion target.
        this.assigneePickerDispose = null;
        this.chatPickerDispose = null;
        this.launchOptionsDispose = null;
        this.cardLinksDispose = null;
        this._openCardGeneration = 0;
        this._openCardAbort = null;
        this._refreshGeneration = 0;
        this._cardPageGeneration = -1;
        this._loadedBoardId = null;
        this._pageRequests = new Map();
        this._filterTimer = null;
        this._refreshAbort = null;
        this.cardPage = null;
        BoardApi.attach(app);
        this.state = {
            boards: [],
            boardId: null,
            columns: [],
            cards: [],
            filters: emptyFilters(),
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

        this.state.columns = [];
        this.state.cards = [];
        await this.refresh({ restoreSelection: true });
    }

    unload() {
        clearTimeout(this._filterTimer);
        this.cancelPageRequests();
        this._refreshAbort?.abort();
        this.boardSettingsDispose?.();
        this.boardSettingsDispose = null;
        this._refreshGeneration += 1;
        this._openCardGeneration += 1;
        this._openCardAbort?.abort();
        this._openCardAbort = null;
        this.destroySortables();
        this.closeDiffModal();
        this.closeSessionModal();
        this.disposeCardPickers();
        this.cardLinksDispose?.();
        this.cardLinksDispose = null;
        disposeBoardAttachmentPreview();
        this.root = null;
    }

    disposeCardPickers() {
        try { this.launchOptionsDispose?.(); } catch { /* already torn down */ }
        this.launchOptionsDispose = null;
        try { this.assigneePickerDispose?.(); } catch { /* already torn down */ }
        this.assigneePickerDispose = null;
        try { this.chatPickerDispose?.(); } catch { /* already torn down */ }
        this.chatPickerDispose = null;
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
    // Board selection
    // ============================================

    readStoredBoardId() {
        try {
            return localStorage.getItem(BOARD_STORAGE_KEY) || null;
        } catch {
            return null;
        }
    }

    persistBoardSelection() {
        try {
            if (this.state.boardId) localStorage.setItem(BOARD_STORAGE_KEY, this.state.boardId);
        } catch {
            // Storage blocked: the first board opens next time.
        }
    }

    // The remembered board when it still exists, else the project's first board.
    pickBoardId(boards) {
        const stored = this.readStoredBoardId();
        const sorted = boards.slice().sort((a, b) => a.position - b.position);
        return (stored && sorted.find(board => board.id === stored)?.id) || sorted[0]?.id || null;
    }

    boardById(id) {
        return this.state.boards.find(board => board.id === id) || null;
    }

    currentBoard() {
        return this.boardById(this.state.boardId);
    }

    async switchBoard(boardId) {
        if (!boardId || boardId === this.state.boardId) return;
        this.state.boardId = boardId;
        this.persistBoardSelection();
        await this.refresh();
    }

    // ============================================
    // Derived data
    // ============================================

    // An assignee is an LLM picker key. Label comes from the picker catalog
    // (which knows environment names), then from the raw key; the avatar is the
    // CLI's brand mark, the closest thing an LLM has to a face.
    assigneeInfo(selection) {
        const key = canonicalLlmSelection(selection);
        if (!key) return null;
        let item = null;
        try {
            item = getEnabledLlmItems(this.app, 'sandbox').find(entry => entry.key === key) || null;
        } catch {
            item = null;
        }
        const parsed = parseLlmSelection(key, this.app.data?.environments || []);
        const cli = item?.cli || parsed?.cli || '';
        const brand = getCliBrand(cli);
        const label = item?.label || parsed?.displayName || parsed?.environmentName || brand?.label || key;
        return { key, cli, label, logo: brand?.logo || '', logoFilter: brand?.logoFilter || '', color: brand?.accentColor || '#64748b' };
    }

    // Comment authors are either the user ("You") or an agent session.
    authorInfo(author) {
        if (!author) return null;
        if (author.kind === 'user') {
            return { key: 'user', cli: '', label: author.label || 'You', logo: '', logoFilter: '', color: '#3b82f6', initials: 'ME' };
        }
        const brand = getCliBrand(author.cli || '');
        return {
            key: `agent:${author.cli || ''}`,
            cli: author.cli || '',
            label: author.label || 'Agent',
            logo: brand?.logo || '',
            logoFilter: brand?.logoFilter || '',
            color: brand?.accentColor || '#64748b',
            initials: 'AI'
        };
    }

    columnById(id) {
        return this.state.columns.find(c => c.id === id) || null;
    }

    cardMatches(card) {
        const filters = this.state.filters;
        const query = filters.q.trim().toLowerCase();
        if (query) {
            const type = cardType(card.type);
            const haystack = [card.key, card.title, card.description, type.value, type.label, ...(card.tags || [])].join(' ').toLowerCase();
            if (!haystack.includes(query)) return false;
        }
        if (filters.assignee && canonicalLlmSelection(card.assignee) !== canonicalLlmSelection(filters.assignee)) return false;
        if (filters.type && cardType(card.type).value !== filters.type) return false;
        if (filters.priority && card.priority !== filters.priority) return false;
        if (filters.tag && !(card.tags || []).includes(filters.tag)) return false;
        return true;
    }

    filteredCards() {
        // Paged responses are filtered against the complete board by the server,
        // including cards that have never been loaded in this browser.
        if (this.cardPage) return this.state.cards;
        return this.state.cards.filter(card => this.cardMatches(card));
    }

    // Distinct assignees present on the board, for the toolbar filter.
    allAssignees() {
        if (this.cardPage?.assignees) return this.cardPage.assignees
            .map(key => this.assigneeInfo(key)).filter(Boolean).sort((a, b) => a.label.localeCompare(b.label));
        const seen = new Map();
        this.state.cards.forEach(card => {
            const key = String(card.assignee || '');
            if (!key || seen.has(key)) return;
            const info = this.assigneeInfo(key);
            if (info) seen.set(key, info);
        });
        return [...seen.values()].sort((a, b) => a.label.localeCompare(b.label));
    }

    allTags() {
        if (this.cardPage?.tags) return this.cardPage.tags;
        const tags = new Set();
        this.state.cards.forEach(card => (card.tags || []).forEach(tag => tags.add(tag)));
        return [...tags].sort();
    }

    hasActiveFilters() {
        const { q, assignee, type, priority, tag } = this.state.filters;
        return Boolean(q.trim() || assignee || type || priority || tag);
    }

    stats() {
        if (this.cardPage) return {
            cards: this.cardPage.filteredCount,
            points: this.cardPage.remainingPoints,
            blocked: this.cardPage.blockedCount
        };
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
            this.filtersChanged(true);
        });

        const bindSelect = (selector, key) => {
            const select = this.query(selector);
            select?.addEventListener('change', () => {
                this.state.filters[key] = select.value;
                this.persistFilters();
                this.filtersChanged();
            });
        };
        bindSelect('[data-board-filter-assignee]', 'assignee');
        bindSelect('[data-board-filter-type]', 'type');
        bindSelect('[data-board-filter-priority]', 'priority');
        bindSelect('[data-board-filter-tag]', 'tag');

        const picker = this.query('[data-board-select]');
        picker?.addEventListener('change', () => this.switchBoard(picker.value));
    }

    // ============================================
    // Rendering
    // ============================================

    renderAll() {
        this.renderBoardPicker();
        this.renderToolbar();
        this.renderLanes();
    }

    // The select top-left: one option per board, the current one selected. Card counts come from
    // the boards list, which every refresh re-reads.
    renderBoardPicker() {
        const select = this.query('[data-board-select]');
        if (!select) return;
        select.innerHTML = this.state.boards
            .slice()
            .sort((a, b) => a.position - b.position)
            .map(board => `<option value="${escapeHtml(board.id)}">${escapeHtml(board.name)}${board.cardCount ? ` (${Number(board.cardCount)})` : ''}</option>`)
            .join('');
        select.value = this.state.boardId || '';
        select.title = select.selectedOptions[0]?.textContent || 'Switch board';
        const settings = this.query('[data-board-action="edit-board"]');
        if (settings) settings.title = `Settings for ${this.currentBoard()?.name || 'this board'}`;
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
            assignee.innerHTML = '<option value="">Any LLM</option>'
                + this.allAssignees().map(info =>
                    `<option value="${escapeHtml(info.key)}">${escapeHtml(info.label)}</option>`).join('');
            assignee.value = this.state.filters.assignee;
            assignee.title = assignee.selectedOptions[0]?.textContent || '';
        }

        const priority = this.query('[data-board-filter-priority]');
        if (priority) priority.value = this.state.filters.priority;

        const type = this.query('[data-board-filter-type]');
        if (type) type.value = this.state.filters.type;

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

    // info comes from assigneeInfo()/authorInfo(). A CLI brand mark when there is
    // one, initials on the brand colour otherwise; the lane-card variant is a
    // click-to-filter control, the comment variant is not.
    avatarHtml(info, size = 22, { filterable = true } = {}) {
        if (!info) {
            return `<span class="board-avatar is-empty" style="width:${size}px;height:${size}px"
                title="Unassigned" aria-label="Unassigned">?</span>`;
        }
        const fontSize = Math.max(9, Math.round(size * 0.38));
        const initials = info.initials || info.label.replace(/[^A-Za-z0-9]/g, '').slice(0, 2).toUpperCase() || '?';
        const logoStyle = info.logoFilter ? ` style="filter:${escapeHtml(info.logoFilter)}"` : '';
        const face = info.logo
            ? `<img class="board-avatar-logo" src="${escapeHtml(info.logo)}" alt="" loading="lazy"${logoStyle}>`
            : escapeHtml(initials);
        const filterAttrs = filterable
            ? ` data-assignee-id="${escapeHtml(info.key)}" role="button" tabindex="-1" title="${escapeHtml(info.label)} — click to filter"`
            : ` title="${escapeHtml(info.label)}"`;
        // A logo needs contrast with its own brand colour (Claude's orange mark on Claude's orange
        // accent is a blank disc), so it sits on the surface inside an accent ring.
        const paint = info.logo
            ? `background:var(--color-bg-surface, #1e1e1e);border-color:${escapeHtml(info.color)}`
            : `background:${escapeHtml(info.color)}`;
        return `<span class="board-avatar${info.logo ? ' has-logo' : ''}"${filterAttrs}
            style="width:${size}px;height:${size}px;${paint};font-size:${fontSize}px"
            >${face}</span>`;
    }

    renderCard(card) {
        const member = this.assigneeInfo(card.assignee);
        const type = cardType(card.type);
        const live = card.activeTabId
            ? `<span class="board-live-dot" title="A terminal session is working this card" aria-label="Session open"></span>`
            : '';
        const tags = (card.tags || []).slice(0, 3).map(tag => {
            const active = tag === this.state.filters.tag ? ' is-on' : '';
            return `<button type="button" class="board-tag${active}" data-board-action="filter-tag"
                data-tag="${escapeHtml(tag)}">${escapeHtml(tag)}</button>`;
        }).join('');
        const excerpt = toPlainPreview(card.description || '', 110);
        const commentCount = Number(card.commentCount) || 0;
        const comments = commentCount > 0
            ? `<span class="board-card-comments" title="${commentCount} comment${commentCount === 1 ? '' : 's'}">
                <i class="fa-regular fa-comment" aria-hidden="true"></i>${commentCount}</span>`
            : '';
        // The assignee is an LLM, and which one is the point of the card — so it gets a name, not
        // just a mark. The chip is the click-to-filter control; the avatar inside it is inert.
        const assignee = member
            ? `<span class="board-assignee-chip" data-assignee-id="${escapeHtml(member.key)}" role="button" tabindex="-1"
                title="${escapeHtml(member.label)} — click to filter"
                >${this.avatarHtml(member, 18, { filterable: false })}<span class="board-assignee-name">${escapeHtml(member.label)}</span></span>`
            : `<span class="board-assignee-chip is-empty" title="Unassigned">${this.avatarHtml(null, 18)}<span class="board-assignee-name">Unassigned</span></span>`;

        // is-live paints the marching "an agent is on this" border (see the template CSS).
        return `
            <article class="board-card${card.flagged ? ' is-flagged' : ''}${card.blocked ? ' is-blocked' : ''}${card.activeTabId ? ' is-live' : ''}" data-card-id="${escapeHtml(card.id)}"
                tabindex="0" role="button" aria-label="${escapeHtml(card.key)}: ${escapeHtml(card.title)}${card.flagged ? ' — Needs your attention' : ''}">
                <span class="board-card-rail" data-priority="${escapeHtml(card.priority)}"
                    title="${escapeHtml(card.priority)} priority"></span>
                <div class="board-card-body">
                    <div class="board-card-top">
                        <span class="board-key">${card.flagged ? '<i class="fa-solid fa-flag board-attention-flag" title="Needs your attention" aria-hidden="true"></i> ' : ''}${escapeHtml(card.key)}</span>
                        <span class="board-card-top-right">
                            <span class="board-type-chip" data-type="${escapeHtml(type.value)}"
                                title="${escapeHtml(type.label)}">${escapeHtml(type.label)}</span>
                            <span class="board-priority-chip" data-priority="${escapeHtml(card.priority)}">${escapeHtml(card.priority)}</span>
                            ${card.points != null ? `<span class="board-points" title="Story points">${escapeHtml(card.points)}</span>` : ''}
                        </span>
                    </div>
                    <h3 class="board-card-title">${escapeHtml(card.title)}</h3>
                    ${excerpt ? `<p class="board-card-excerpt">${escapeHtml(excerpt)}</p>` : ''}
                    <div class="board-card-meta">
                        <div class="board-tags">${tags}</div>
                        <div class="board-card-aside">
                            ${live}
                            ${card.blocked ? `<i class="fa-solid fa-triangle-exclamation board-blocked"
                                title="Blocked" aria-hidden="true"></i>` : ''}
                            ${comments}
                        </div>
                    </div>
                    <div class="board-card-foot">${assignee}</div>
                </div>
            </article>`;
    }

    emptyLaneCopy(column, filteredOut) {
        if (filteredOut) return 'No cards match these filters.';
        if (this.isDoneLane(column)) return 'Nothing done yet.';
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
                const page = this.cardPage?.lanes?.find(lane => lane.columnId === column.id);
                const total = page?.totalCount ?? this.state.cards.filter(card => card.columnId === column.id).length;
                const more = page?.hasMore ? `<button type="button" class="btn btn-sm btn-outline-secondary board-load-more w-100"
                    data-board-action="load-more" data-column-id="${escapeHtml(column.id)}"
                    ${this._pageRequests.has(column.id) ? 'disabled' : ''}>${this._pageRequests.has(column.id) ? 'Loading…' : `Load more (${cards.length} of ${page.filteredCount})`}</button>` : '';
                const list = cards.map(card => this.renderCard(card)).join('')
                    || `<p class="board-lane-empty">${escapeHtml(this.emptyLaneCopy(column, total > 0))}</p>`;

                return `
                    <section class="board-lane" data-column-id="${escapeHtml(column.id)}"
                        style="--lane-color:${escapeHtml(column.color)}">
                        <header class="board-lane-head">
                            <button type="button" class="board-lane-grip" title="Drag to reorder this lane"
                                aria-label="Reorder ${escapeHtml(column.name)}">
                                <i class="fa-solid fa-grip-vertical" aria-hidden="true"></i>
                            </button>
                            <h2 class="board-lane-title">${escapeHtml(column.name)}</h2>
                            <span class="board-card-count" title="Cards in lane">${total}</span>
                            <button type="button" class="board-icon-btn" data-board-action="edit-lane"
                                data-column-id="${escapeHtml(column.id)}" title="Lane settings"
                                aria-label="Settings for ${escapeHtml(column.name)}">
                                <i class="fa-solid fa-ellipsis-vertical" aria-hidden="true"></i>
                            </button>
                        </header>
                        <div class="board-lane-list" data-column-id="${escapeHtml(column.id)}">${list}${more}</div>
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
        this.queryAll('.board-lane-list').forEach(list => {
            list.addEventListener('scroll', () => {
                if (list.scrollHeight - list.scrollTop - list.clientHeight < 240) {
                    void this.loadMoreCards(list.dataset.columnId);
                }
            }, { passive: true });
        });
    }

    cancelPageRequests() {
        for (const request of this._pageRequests.values()) request.abort();
        this._pageRequests.clear();
    }

    filtersChanged(debounce = false) {
        clearTimeout(this._filterTimer);
        // Invalidate both list and page requests immediately, before the debounce.
        this._refreshGeneration += 1;
        this._refreshAbort?.abort();
        this.cancelPageRequests();
        if (debounce) this._filterTimer = setTimeout(() => void this.refresh(), 200);
        else void this.refresh();
    }

    async loadMoreCards(columnId) {
        // A scroll can fire during a filter debounce or refresh. Its old offset
        // belongs to the previous full result, even after old requests were aborted.
        if (this._cardPageGeneration !== this._refreshGeneration) return;
        const lane = this.cardPage?.lanes?.find(item => item.columnId === columnId);
        if (!lane?.hasMore || this._pageRequests.has(columnId)) return;
        const generation = this._refreshGeneration;
        const root = this.root;
        const boardId = this.state.boardId;
        const request = new AbortController();
        this._pageRequests.set(columnId, request);
        const isCurrent = () => !request.signal.aborted && generation === this._refreshGeneration
            && root === this.root && root?.isConnected && boardId === this.state.boardId;
        const button = this.queryAll('[data-board-action="load-more"]').find(item => item.dataset.columnId === columnId);
        if (button) { button.disabled = true; button.textContent = 'Loading…'; }
        try {
            const response = await BoardApi.getBoardCardPageAsync(boardId, this.state.filters, {
                columnId, offset: lane.nextOffset, signal: request.signal
            });
            if (!isCurrent()) return;
            const cards = new Map(this.state.cards.map(card => [card.id, card]));
            for (const card of response.cards || []) cards.set(card.id, card);
            this.state.cards = [...cards.values()];
            const next = response.lanes?.find(item => item.columnId === columnId);
            if (next) Object.assign(lane, next);
            else lane.hasMore = false;
        } catch (error) {
            if (isCurrent()) this.app.showToast('Board', error?.message || 'Could not load more cards. Try again.', 'error');
        } finally {
            if (this._pageRequests.get(columnId) === request) this._pageRequests.delete(columnId);
            if (isCurrent()) this.renderLanes();
        }
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
                disabled: this.hasActiveFilters(),
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
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to move the card.', 'error');
        }
        await this.refresh();
    }

    async onLanesReordered() {
        const orderedIds = this.queryAll('.board-lane').map(lane => lane.dataset.columnId);
        try {
            await BoardApi.reorderBoardColumnsAsync(orderedIds, this.state.boardId);
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to reorder lanes.', 'error');
        }
        await this.refresh();
    }

    async refresh({ restoreSelection = false } = {}) {
        clearTimeout(this._filterTimer);
        this.cancelPageRequests();
        this._refreshAbort?.abort();
        const abort = this._refreshAbort = new AbortController();
        const generation = ++this._refreshGeneration;
        const root = this.root;
        const requestedBoardId = this.state.boardId;
        const isCurrent = () => generation === this._refreshGeneration && root === this.root
            && root?.isConnected && this.app.currentView === 'board' && requestedBoardId === this.state.boardId;
        this.setBusy(true);
        if (requestedBoardId !== this._loadedBoardId) {
            // The picker already names the new board. Old lanes must not remain actionable.
            this.state.columns = [];
            this.state.cards = [];
            this.cardPage = null;
            this.renderAll();
        }
        try {
            const boards = await BoardApi.getBoardsAsync();
            if (!isCurrent()) return;
            // The selected board may have been deleted (here or from another tab).
            const boardId = !restoreSelection && boards.some(board => board.id === requestedBoardId)
                ? requestedBoardId : this.pickBoardId(boards);
            const [columns, page] = await Promise.all([
                BoardApi.getBoardColumnsAsync(boardId),
                BoardApi.getBoardCardPageAsync(boardId, this.state.filters, { signal: abort.signal })
            ]);
            if (!isCurrent()) return;
            this.state.boards = boards;
            this.state.boardId = boardId;
            this.state.columns = columns;
            this.state.cards = page.cards || [];
            this.cardPage = page.lanes ? page : null;
            this._cardPageGeneration = generation;
            this._loadedBoardId = boardId;
            this.persistBoardSelection();
            this.renderAll();
        } catch (error) {
            if (isCurrent()) this.app.showToast('Board', error?.message || 'Failed to refresh the board.', 'error');
        } finally {
            if (generation === this._refreshGeneration && root === this.root) this.setBusy(false);
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
            this.filtersChanged();
            return;
        }

        if (avatar && cardEl) {
            event.stopPropagation();
            const id = avatar.dataset.assigneeId;
            this.state.filters.assignee = this.state.filters.assignee === id ? '' : id;
            this.persistFilters();
            this.filtersChanged();
            return;
        }

        if (cardEl && !trigger) {
            this.openCardEditor(cardEl.dataset.cardId);
            return;
        }

        switch (action) {
            case 'load-more':
                void this.loadMoreCards(trigger.dataset.columnId);
                break;
            case 'new-card':
                this.openCardEditor(null);
                break;
            case 'add-lane':
                this.openLaneEditor(null);
                break;
            case 'edit-lane':
                this.openLaneEditor(trigger.dataset.columnId);
                break;
            case 'new-board':
                this.openBoardEditor(null);
                break;
            case 'edit-board':
                this.openBoardEditor(this.state.boardId);
                break;
            case 'clear-filters':
                this.state.filters = emptyFilters();
                this.persistFilters();
                this.filtersChanged();
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
        const generation = ++this._openCardGeneration;
        this._openCardAbort?.abort();
        const abort = new AbortController();
        this._openCardAbort = abort;

        let card = null;
        if (cardId) {
            // Always the server's copy: the lane list carries summaries only, and an
            // LLM may have commented on or moved this card since the board loaded.
            try {
                card = await BoardApi.getBoardCardAsync(cardId, { signal: abort.signal });
            } catch (error) {
                if (abort.signal.aborted || error?.name === 'AbortError' || generation !== this._openCardGeneration)
                    return;
                this.app.showToast('Board', error?.message || 'That card could not be opened.', 'error');
                return;
            }
        }

        if (generation !== this._openCardGeneration) return;

        const columnId = card?.columnId || this.state.columns[0]?.id || '';

        this.app.showModal(card ? `${card.key} · ${card.title}` : 'New card', `
            <div class="board-card-editor" data-board-card-editor data-card-id="${escapeHtml(card?.id || '')}">
                <div class="board-editor-scroll">
                <div class="board-editor-main">
                    <input type="text" class="form-control board-editor-title" id="board-card-title"
                        placeholder="What needs to happen" value="${escapeHtml(card?.title || '')}"
                        aria-label="Card title">

                    <section class="board-block">
                        <h3 class="board-block-label">Description</h3>
                        ${this.composerMarkup({
                            name: 'description',
                            value: card?.description || '',
                            preview: true,
                            placeholder: 'Context, repro steps, links. Use Code for a snippet.'
                        })}
                    </section>

                    <section class="board-block">
                        <h3 class="board-block-label">Attachments <span class="board-count" data-board-count="attachments">${card?.attachments?.length || 0}</span></h3>
                        <div class="board-attachment-list" data-board-attachments></div>
                        <button type="button" class="btn btn-sm btn-outline-secondary" data-board-add-files>
                            <i class="fa-solid fa-paperclip" aria-hidden="true"></i> Upload files
                        </button>
                        <input type="file" hidden multiple data-board-files>
                        <p class="board-editor-muted mt-2">Up to 12 files, any size. Preview images, text and PDFs.</p>
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
                            <label class="board-editor-label" for="board-card-type">Type</label>
                            <select class="form-select form-select-sm" id="board-card-type">
                                ${CARD_TYPES.map(type => `
                                    <option value="${type.value}"${type.value === (card?.type || 'task') ? ' selected' : ''}>${escapeHtml(type.label)}</option>
                                `).join('')}
                            </select>
                        </div>
                        <div>
                            <label class="board-editor-label" for="board-card-lane">Lane</label>
                            <select class="form-select form-select-sm" id="board-card-lane">
                                ${this.laneOptionsHtml(columnId)}
                            </select>
                        </div>
                        <div>
                            <label class="board-editor-label" for="board-card-assignee">Assignee (LLM)</label>
                            <div class="board-assignee-row">
                                <select class="form-select form-select-sm" id="board-card-assignee" aria-label="Assignee"></select>
                                <button type="button" class="board-side-remove board-assignee-clear" data-board-clear-assignee
                                    title="Unassign" aria-label="Unassign">
                                    <i class="fa-solid fa-xmark" aria-hidden="true"></i>
                                </button>
                            </div>
                        </div>
                        <div data-board-launch-options></div>
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
                            <input class="form-check-input" type="checkbox" id="board-card-flagged"
                                data-board-flagged${card?.flagged ? ' checked' : ''}>
                            <label class="form-check-label" for="board-card-flagged"><i class="fa-solid fa-flag" aria-hidden="true"></i> Needs your attention</label>
                        </div>
                        <div class="form-check">
                            <input class="form-check-input" type="checkbox" id="board-card-blocked"
                                data-board-blocked${card?.blocked ? ' checked' : ''}>
                            <label class="form-check-label" for="board-card-blocked">Blocked</label>
                        </div>
                    </div>

                    ${renderCardLinksSection(card)}

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
                        <p class="board-side-empty">Link the same session to every card it is working on.</p>
                        ${card ? `
                        <form class="board-side-form" data-board-add-session>
                            <input type="text" class="form-control form-control-sm" name="displayName"
                                placeholder="Paste a session id" autocomplete="off" aria-label="Session id or name">
                            <button type="submit" class="board-side-add" title="Add this session" aria-label="Add this session">
                                <i class="fa-solid fa-plus" aria-hidden="true"></i>
                            </button>
                        </form>` : ''}
                    </section>

                    ${card ? `<section class="board-side-section">
                        <details data-board-notes-details>
                            <summary class="board-side-label"><i class="fa-solid fa-pen-ruler" aria-hidden="true"></i> Agent notes <span class="board-count" data-board-count="notes">${card?.notes?.length || 0}</span></summary>
                            <p class="board-editor-muted">The scratchpad agents checkpoint findings in while they work. Not part of the comment thread.</p>
                            <div data-board-notes class="board-notes-list"></div>
                        </details>
                    </section>` : ''}
                    </div>
                </aside>
                </div>

                    <div class="board-editor-actions">
                        ${card ? `<button type="button" class="btn btn-sm btn-outline-danger" data-board-delete-card>
                            <i class="fa-solid fa-trash" aria-hidden="true"></i> Delete
                        </button>` : '<span></span>'}
                        <span class="board-editor-actions-main">
                            ${card ? `<span class="project-health-fix-controls board-chat-controls" role="group" aria-label="Chat about this card">
                                <button type="button" class="btn btn-sm btn-outline-secondary" data-board-chat
                                    title="Open a terminal to discuss this card with the selected LLM">
                                    <i class="fa-solid fa-comments" aria-hidden="true"></i> Chat with:
                                </button>
                                <label class="project-health-fix-picker">
                                    <span class="visually-hidden">Agent for card discussion</span>
                                    <select class="form-select" data-board-chat-agent aria-label="Agent for card discussion"></select>
                                </label>
                            </span>` : ''}
                            ${card ? `<button type="button" class="btn btn-sm btn-outline-success" data-board-start-work
                                title="Start the assigned LLM in the background with this card as its first message">
                                <i class="fa-solid fa-play" aria-hidden="true"></i> <span data-board-start-work-label>Start work</span>
                            </button>` : ''}
                            <button type="button" class="btn btn-sm btn-outline-primary" data-board-save-card>Save</button>
                        </span>
                    </div>
            </div>
        `, { onClose: () => {
            this.disposeCardPickers();
            this.cardLinksDispose?.();
            this.cardLinksDispose = null;
            disposeBoardAttachmentPreview();
        } });

        if (generation !== this._openCardGeneration) return;

        const container = document.getElementById('modal-container');
        const dialog = container?.querySelector('.modal-dialog');
        // app.showModal always ships modal-dialog-scrollable, which puts a
        // scrollbar on .modal-body. This editor owns its own scroller, so that
        // extra bar has to come off or the dialog shows two.
        dialog?.classList.remove('modal-lg', 'modal-dialog-scrollable');
        dialog?.classList.add('modal-xl', 'board-card-modal-dialog');

        const editor = container?.querySelector('[data-board-card-editor]');
        if (!editor) return;
        this.bindCardEditor(editor, card);
    }

    // One <option> per lane. With several boards the lanes are grouped by board, so saving a
    // card into another board's lane is how a card moves between sprints.
    laneOptionsHtml(selectedId) {
        const option = column => `<option value="${escapeHtml(column.id)}"${column.id === selectedId ? ' selected' : ''}>${escapeHtml(column.name)}</option>`;
        const boards = this.state.boards
            .filter(board => (board.columns || []).length > 0)
            .sort((a, b) => a.position - b.position);
        if (boards.length <= 1) {
            return this.state.columns.slice().sort((a, b) => a.position - b.position).map(option).join('');
        }
        return boards.map(board => `<optgroup label="${escapeHtml(board.name)}">${board.columns
            .slice().sort((a, b) => a.position - b.position).map(option).join('')}</optgroup>`).join('');
    }

    // The card id lives on the editor (data-card-id), not on controller state.
    // showModal replacement runs the previous editor's onClose, which used to
    // clear a shared editingCardId and turn the next Save into a create.
    cardIdFromEditor(editor) {
        return String(editor?.dataset?.cardId || '').trim() || null;
    }

    bindCardEditor(editor, card) {
        card = card || { id: null, attachments: [], pendingAttachments: [] };
        editor._boardCard = card;
        this.cardLinksDispose = bindCardLinks(editor, card, {
            openCard: id => this.openLinkedCard(editor, id),
            showError: message => this.app.showToast('Board', message, 'error')
        });
        // Link navigation replaces this editor. Track form edits separately from link search.
        const trackEdits = event => {
            if (event.target.closest('.board-side-fields, [data-board-composer="description"], #board-card-title'))
                editor._boardHasEdits = true;
        };
        editor.addEventListener('input', trackEdits);
        editor.addEventListener('change', trackEdits);
        this.renderCommentsPanel(editor, card);
        this.renderCommitsPanel(editor, card);
        this.renderSessionsPanel(editor, card);
        this.renderAttachmentsPanel(editor, card);
        editor.querySelector('[data-board-add-files]')?.addEventListener('click', () => editor.querySelector('[data-board-files]')?.click());
        editor.querySelector('[data-board-files]')?.addEventListener('change', event => {
            const files = Array.from(event.target.files || []);
            event.target.value = '';
            this.attachImages(editor.querySelector('[data-board-composer="description"]'),
                editor.querySelector('[data-board-composer="description"] [data-board-composer-input]'), card, files, { inline: false });
        });
        editor.querySelector('[data-board-notes-details]')?.addEventListener('toggle', event => {
            if (event.target.open) this.renderNotesPanel(editor, card);
        });

        this.bindComposer(editor.querySelector('[data-board-composer="description"]'), { card });
        this.bindComposer(editor.querySelector('[data-board-composer="comment"]'), {
            card,
            onSubmit: text => this.postComment(editor, text)
        });

        editor.querySelector('[data-board-save-card]')?.addEventListener('click', () => this.saveCard(editor));
        editor.querySelector('[data-board-delete-card]')?.addEventListener('click', () => this.deleteCurrentCard(editor));
        editor.querySelector('[data-board-start-work]')?.addEventListener('click', () => this.startWork(editor, card));
        editor.querySelector('[data-board-chat]')?.addEventListener('click', () => this.startWork(editor, card, 'chat'));

        // The Assignee field is the app-wide LLM picker ('sandbox' context: saved
        // environments plus the bare CLIs, never a shell, never a Worker).
        this.disposeCardPickers();
        const assigneeSelect = editor.querySelector('#board-card-assignee');
        if (assigneeSelect) {
            this.assigneePickerDispose = mountLlmPicker(this.app, assigneeSelect, {
                context: 'sandbox',
                placeholder: 'Unassigned',
                selectedValue: card?.assignee || ''
            });
            const renderOptions = (options = {}, selection = assigneeSelect.value) => {
                this.launchOptionsDispose?.();
                const host = editor.querySelector('[data-board-launch-options]');
                if (!host) return;
                host.innerHTML = renderBoardLaunchOptions(selection, options);
                this.launchOptionsDispose = bindBoardLaunchOptions(host, selection);
            };
            renderOptions(card.baseLlmOptions || {}, card.assignee || '');
            assigneeSelect.addEventListener('change', () => renderOptions());
            editor.querySelector('[data-board-clear-assignee]')?.addEventListener('click', () => {
                setLlmPickerValue(this.app, assigneeSelect, '');
                renderOptions({}, '');
            });
        }

        // Chat can use any launch target without changing the card's assignment.
        const chatSelect = editor.querySelector('[data-board-chat-agent]');
        if (chatSelect) {
            this.chatPickerDispose = mountLlmPicker(this.app, chatSelect, {
                context: 'sandbox',
                placeholder: 'Select an agent…',
                selectedValue: card.assignee || getEnabledLlmItems(this.app, 'sandbox')[0]?.key || ''
            });
        }

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

    async openLinkedCard(editor, cardId) {
        if (editor._boardSaving || editor._boardUploading || editor._boardStarting || editor._boardOpeningLink) return;
        editor._boardOpeningLink = true;
        try {
            const comment = editor.querySelector('[data-board-composer="comment"] [data-board-composer-input]')?.value.trim();
            // Composer tools and the picker's Unassign button can change values silently.
            const description = editor.querySelector('[data-board-composer="description"] [data-board-composer-input]')?.value;
            const assignee = editor.querySelector('#board-card-assignee')?.value;
            const changed = editor._boardHasEdits || comment
                || description !== (editor._boardCard?.description || '')
                || assignee !== (editor._boardCard?.assignee || '');
            if (changed && !await confirmDialog({
                title: 'Open linked card',
                message: 'You have unsaved edits on this card. Discard them and open the linked card?',
                confirmLabel: 'Discard and open',
                danger: true
            })) return;
            if (editor.isConnected !== false) await this.openCardEditor(cardId);
        } finally {
            editor._boardOpeningLink = false;
        }
    }

    // ============================================
    // Composer (shared by the description and comments)
    // ============================================

    composerMarkup({ name, value = '', placeholder = '', disabled = false, submitLabel = '', preview = false }) {
        const off = disabled ? ' disabled' : '';
        return `
            <div class="board-composer" data-board-composer="${name}">
                <div class="board-composer-toolbar">
                    <button type="button" class="board-composer-btn" data-board-composer-action="code"
                        title="Format the selection as code"${off}>
                        <i class="fa-solid fa-code" aria-hidden="true"></i><span>Code</span>
                    </button>
                    <button type="button" class="board-composer-btn" data-board-composer-action="image"
                        title="Attach a file (or paste an image)"${off}>
                        <i class="fa-solid fa-paperclip" aria-hidden="true"></i><span>Attach</span>
                    </button>
                    <span class="board-composer-hint">Drop files or paste an image</span>
                    ${preview ? `<button type="button" class="board-composer-btn ms-auto"
                        data-board-composer-toggle aria-label="Preview description"${off}>Preview</button>` : ''}
                </div>
                <textarea class="form-control board-composer-input" data-board-composer-input
                    placeholder="${escapeHtml(placeholder)}" rows="3"${off}>${escapeHtml(value)}</textarea>
                ${preview ? '<div class="board-comment-body board-description-preview" data-board-composer-preview title="Click to edit" hidden></div>' : ''}
                <input type="file" hidden data-board-composer-file multiple>
                <div class="board-composer-busy" data-board-composer-busy hidden>Adding files…</div>
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
            const needed = input.scrollHeight;
            // Drop the inline height first so a flex parent (the create-card
            // description) can size the box. Only lock a pixel height when the
            // text is taller than that allocation.
            input.style.height = '';
            if (needed > input.clientHeight + 1) {
                input.style.height = `${needed}px`;
            }
        };
        const preview = composer.querySelector('[data-board-composer-preview]');
        const toggle = composer.querySelector('[data-board-composer-toggle]');
        const renderPreview = () => {
            if (preview) preview.innerHTML = renderCommentHtml(input.value, { attachments: card?.attachments || [] });
        };
        const setPreview = viewing => {
            if (!preview || !toggle) return;
            renderPreview();
            preview.hidden = !viewing;
            input.hidden = viewing;
            composer.querySelectorAll('[data-board-composer-action="code"], [data-board-composer-action="image"], .board-composer-hint')
                .forEach(element => { element.hidden = viewing; });
            toggle.textContent = viewing ? 'Edit' : 'Preview';
            toggle.setAttribute('aria-label', viewing ? 'Edit description' : 'Preview description');
            if (!viewing) autoGrow();
        };
        toggle?.addEventListener('click', () => {
            setPreview(preview.hidden);
            if (preview.hidden) input.focus();
        });
        // Clicking into the rendered description opens it for editing (the Edit button stays as
        // the discoverable way). Links and images keep their own click behaviour.
        preview?.addEventListener('click', event => {
            if (event.target.closest('a, img, button')) return;
            setPreview(false);
            input.focus();
            const end = input.value.length;
            try { input.setSelectionRange(end, end); } catch { /* not a text control */ }
        });
        input.addEventListener('input', autoGrow);
        // Sized now rather than on the next frame: an occluded page never gets one,
        // and the description box would open at its one-line default.
        autoGrow();
        setPreview(Boolean(input.value.trim()));

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
            const images = Array.from(event.dataTransfer?.files || []);
            composer.classList.remove('is-drop-target');
            if (images.length === 0) return;
            event.preventDefault();
            this.attachImages(composer, input, card, images);
        });
    }

    async attachImages(composer, input, card, files, { inline = true } = {}) {
        if (!files.length) return;
        const editor = composer?.closest('[data-board-card-editor]');
        if (editor?._boardUploading || editor?._boardSaving || editor?._boardStarting) return;
        if (editor) editor._boardUploading = true;
        const busy = composer.querySelector('[data-board-composer-busy]');
        if (busy) busy.hidden = false;
        try {
            for (const file of files) {
                if ((card.attachments?.length || 0) + (card.pendingAttachments?.length || 0) >= 12)
                    throw new Error('A card can hold at most 12 attachments.');
                const payload = await fileToAttachmentPayload(file);
                if (!card.id) {
                    card.pendingAttachments ||= [];
                    card.pendingAttachments.push(payload);
                    if (editor) this.renderAttachmentsPanel(editor, card);
                    continue;
                }
                const attachment = await BoardApi.addCardAttachmentAsync(card.id, payload);
                card.attachments = card.attachments || [];
                card.attachments.push(attachment);
                if (inline && attachment.url && getAttachmentPreviewKind(attachment) === 'image')
                    insertAtCursor(input, `![${attachment.name.replace(/[\[\]\r\n]/g, '')}](attachment:${attachment.id})`);
                if (editor) this.renderAttachmentsPanel(editor, card);
            }
            input.dispatchEvent(new Event('input', { bubbles: true }));
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to add the file.', 'error');
        } finally {
            if (busy) busy.hidden = true;
            if (editor) editor._boardUploading = false;
        }
    }

    renderAttachmentsPanel(editor, card) {
        const host = editor?.querySelector('[data-board-attachments]');
        if (!host) return;
        const attachments = card?.attachments || [];
        const pending = card?.pendingAttachments || [];
        this.updateSectionCount(editor, 'attachments', attachments.length + pending.length);
        // Small rasters come with a data: URL (the same one inline images use), so the row can
        // show the picture itself; everything else gets the file icon.
        const lead = attachment => RASTER_DATA_URL_RE.test(String(attachment.url || ''))
            ? `<img class="board-attachment-thumb" src="${escapeHtml(attachment.url)}" alt="" loading="lazy">`
            : '<i class="fa-solid fa-file" aria-hidden="true"></i>';
        host.innerHTML = attachments.map(attachment => `
            <div class="board-attachment-row">
                <button type="button" class="board-side-main" data-board-view-attachment="${escapeHtml(attachment.id)}">
                    ${lead(attachment)}
                    <span class="board-side-copy"><span class="board-side-title">${escapeHtml(attachment.name)}</span>
                    <span class="board-side-sub">${formatFileSize(attachment.bytes)} · ${getAttachmentPreviewKind(attachment) === 'download' ? 'Download' : 'Preview'}</span></span>
                </button>
                <button type="button" class="board-side-remove" data-board-remove-attachment="${escapeHtml(attachment.id)}" aria-label="Remove ${escapeHtml(attachment.name)}"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
            </div>`).join('') + pending.map((attachment, index) => `
            <div class="board-attachment-row"><span class="board-side-copy"><span class="board-side-title">${escapeHtml(attachment.name)}</span>
                <span class="board-side-sub">${formatFileSize(attachment.bytes)} · Uploads when you save</span></span>
                <button type="button" class="board-side-remove" data-board-remove-pending="${index}" aria-label="Remove ${escapeHtml(attachment.name)}"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
            </div>`).join('') || '<p class="board-editor-muted">No files attached.</p>';
        host.querySelectorAll('[data-board-view-attachment]').forEach(button => button.addEventListener('click', async () => {
            const attachment = attachments.find(item => item.id === button.dataset.boardViewAttachment);
            if (!attachment) return;
            try { await openBoardAttachment(this.app, card.id, attachment); }
            catch (error) { this.app.showToast('Board', error?.message || 'Could not open the file.', 'error'); }
        }));
        host.querySelectorAll('[data-board-remove-pending]').forEach(button => button.addEventListener('click', () => {
            pending.splice(Number(button.dataset.boardRemovePending), 1);
            this.renderAttachmentsPanel(editor, card);
        }));
        host.querySelectorAll('[data-board-remove-attachment]').forEach(button => button.addEventListener('click', async () => {
            if (editor._boardUploading || editor._boardSaving || editor._boardStarting) return;
            const attachment = attachments.find(item => item.id === button.dataset.boardRemoveAttachment);
            if (!attachment || !await confirmDialog({ title: 'Remove attachment', message: `Remove ${attachment.name} from this card?`, confirmLabel: 'Remove', danger: true })) return;
            try {
                await BoardApi.deleteCardAttachmentAsync(card.id, attachment.id);
                card.attachments = attachments.filter(item => item.id !== attachment.id);
                this.renderAttachmentsPanel(editor, card);
            } catch (error) { this.app.showToast('Board', error?.message || 'Could not remove the file.', 'error'); }
        }));
    }

    // Agent notes ride on the card response (card.notes), so no fetch: the section is collapsed by
    // default and rendered when opened. Same escape-first body renderer as comments.
    renderNotesPanel(editor, card) {
        const host = editor.querySelector('[data-board-notes]');
        if (!host) return;
        const notes = card?.notes || [];
        const count = editor.querySelector('[data-board-count="notes"]');
        if (count) count.textContent = String(notes.length);
        if (!notes.length) {
            host.innerHTML = '<p class="board-editor-muted">No notes yet. Agents add them with append_board_note.</p>';
            return;
        }
        const attachments = card?.attachments || [];
        host.innerHTML = notes.map(note => {
            const author = this.authorInfo(note.author);
            return `
                <article class="board-comment board-note${note.author?.kind === 'agent' ? ' is-agent' : ''}">
                    ${this.avatarHtml(author, 24, { filterable: false })}
                    <div class="board-comment-content">
                        <div class="board-comment-meta">
                            <span class="board-comment-author">${escapeHtml(author?.label || 'Someone')}</span>
                            <span class="board-comment-when">${escapeHtml(this.formatDateTime(note.createdAt))}</span>
                        </div>
                        <div class="board-comment-body">${renderCommentHtml(note.body, { attachments })}</div>
                    </div>
                </article>`;
        }).join('');
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
            const author = this.authorInfo(comment.author);
            // An agent comment knows the terminal session that wrote it and when: the link replays
            // that session seeked to this moment (session-viewer.js seekToUtc).
            const sessionId = comment.author?.kind === 'agent' ? String(comment.author.sessionId || '') : '';
            const jump = sessionId
                ? `<button type="button" class="board-comment-jump" data-board-comment-jump="${escapeHtml(sessionId)}"
                    data-board-comment-at="${escapeHtml(comment.createdAt || '')}"
                    title="Replay the session at the moment this was written">
                    <i class="fa-solid fa-clock-rotate-left" aria-hidden="true"></i> in session</button>`
                : '';
            return `
                <article class="board-comment${comment.author?.kind === 'agent' ? ' is-agent' : ''}">
                    ${this.avatarHtml(author, 28, { filterable: false })}
                    <div class="board-comment-content">
                        <div class="board-comment-meta">
                            <span class="board-comment-author">${escapeHtml(author?.label || 'Someone')}</span>
                            <span class="board-comment-when">${jump}${escapeHtml(this.formatDateTime(comment.createdAt))}</span>
                        </div>
                        <div class="board-comment-body" data-board-comment-body>${renderCommentHtml(comment.body, { attachments })}</div>
                        <button type="button" class="board-comment-more" data-board-comment-more hidden>Show more</button>
                    </div>
                </article>`;
        }).join('');

        host.querySelectorAll('[data-board-comment-jump]').forEach(button => {
            button.addEventListener('click', event => {
                event.stopPropagation();
                this.openSessionReplay({ id: button.dataset.boardCommentJump }, { seekToUtc: button.dataset.boardCommentAt || null });
            });
        });

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
        const cardId = this.cardIdFromEditor(editor);
        if (!cardId) return;
        const composerInput = editor.querySelector('[data-board-composer="comment"] [data-board-composer-input]');
        try {
            await BoardApi.addBoardCommentAsync(cardId, { body });
            const card = await this.reloadEditingCard(editor);
            if (!card) return;
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
            const scroller = editor.querySelector('.board-editor-scroll');
            if (scroller) scroller.scrollTop = scroller.scrollHeight;
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
        try {
            // The server reads author/message/date from the project's git history.
            const cardId = this.cardIdFromEditor(editor);
            if (!cardId) return;
            await BoardApi.addCardCommitAsync(cardId, { sha });
            const card = await this.reloadEditingCard(editor);
            if (!card) return;
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
            const fresh = await this.reloadEditingCard(editor);
            if (!fresh) return;
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
        this.updateStartWorkButton(editor, card);
        const host = editor.querySelector('[data-board-sessions]');
        if (!host) return;
        const sessions = card?.sessions || [];

        if (!sessions.length) {
            host.innerHTML = '<p class="board-side-empty">No sessions linked.</p>';
            return;
        }

        host.innerHTML = sessions.map(session => {
            const when = this.formatDateTime(session.createdAt);
            const status = session.active ? 'open' : 'ended';
            const sub = [when, session.cli ? getCliBrand(session.cli)?.label || session.cli : '', status].filter(Boolean).join(' · ');
            return `
            <div class="board-side-row${session.active ? ' is-live' : ''}" data-session-id="${escapeHtml(session.id)}">
                <button type="button" class="board-side-main" data-board-open-session="${escapeHtml(session.id)}"
                    title="${escapeHtml(session.active ? 'Open this terminal tab' : 'Replay this session')}">
                    <i class="fa-solid ${session.active ? 'fa-terminal' : 'fa-clock-rotate-left'} board-side-icon" aria-hidden="true"></i>
                    <span class="board-side-text">
                        <span class="board-side-title">${session.active ? '<span class="board-live-dot" aria-hidden="true"></span>' : ''}${escapeHtml(session.displayName)}</span>
                        <span class="board-side-sub">${escapeHtml(sub)}</span>
                    </span>
                </button>
                <button type="button" class="board-side-remove" data-board-remove-session="${escapeHtml(session.id)}"
                    title="Remove this session" aria-label="Remove session ${escapeHtml(session.displayName)}">
                    <i class="fa-solid fa-xmark" aria-hidden="true"></i>
                </button>
            </div>`;
        }).join('');

        host.querySelectorAll('[data-board-open-session]').forEach(button => {
            button.addEventListener('click', () => {
                const session = sessions.find(item => item.id === button.dataset.boardOpenSession);
                if (!session) return;
                if (session.active && session.tabId) {
                    this.focusSessionTab(card, session);
                } else {
                    this.openSessionReplay(session);
                }
            });
        });
        host.querySelectorAll('[data-board-remove-session]').forEach(button => {
            button.addEventListener('click', () => this.removeSession(editor, card, button.dataset.boardRemoveSession));
        });
    }

    // A live session's row jumps to its terminal tab: adopt it into whatever
    // terminal panel is on screen, else go to the Terminals view with it focused
    // (the same fallback the Python "run interactive" flow uses).
    async focusSessionTab(card, session) {
        const terminal = this.app.terminalController;
        const tabId = session.tabId;
        if (!terminal || !tabId) return;
        terminal.rememberTabLaunch?.(tabId, {
            selection: session.selection || null,
            label: card?.key ? `${card.key} · ${card.title || session.displayName}` : session.displayName,
            title: `${card?.key || ''} · ${card?.title || session.displayName}`.replace(/^ · /, ''),
            taskKey: CARD_TASK_KEY(card?.id || session.id),
            workingDirectory: null
        });
        this.app.closeModal();
        if (!(await terminal.adoptLaunchedTab?.(tabId))) {
            this.app.navigate?.('terminal-focus', { preferredTabId: tabId, preferredSelection: session.selection || null });
        }
    }

    // An ended session replays in the shared terminal replay modal (session-viewer.js — the same
    // one the chat history sidebar's "Replay Session" opens). It mounts its own overlay on
    // document.body above the card editor, so the editor survives; what it lacks is Escape
    // handling and a way to close it from here, so this wraps it: Escape (captured, so the
    // editor's own handler never sees it) closes the replay, and navigation closes it too.
    openSessionReplay(session, { seekToUtc = null } = {}) {
        this.closeSessionModal();
        const before = document.body.lastElementChild;
        void SessionDebug.showReplayModal(session.id, { seekToUtc });
        const overlay = document.body.lastElementChild;
        if (!overlay || overlay === before) return;
        overlay.classList.add('vb-session-replay-layer');

        const onKeydown = event => {
            if (event.key !== 'Escape' || event.defaultPrevented) return;
            if (!overlay.isConnected) {
                document.removeEventListener('keydown', onKeydown, true);
                this.sessionLayer = null;
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            close();
        };
        const close = () => {
            document.removeEventListener('keydown', onKeydown, true);
            // The replay modal's own close (disposes the xterm instance) hangs off its × button.
            if (overlay.isConnected) overlay.querySelector('button')?.click();
            this.sessionLayer = null;
        };
        document.addEventListener('keydown', onKeydown, true);
        this.sessionLayer = { close };
    }

    closeSessionModal() {
        this.sessionLayer?.close();
        this.sessionLayer = null;
    }

    // Links a session by hand. A pasted session id (dashed GUID or 32 hex chars) becomes the link
    // itself, so a terminal that was not started from the card can still be replayed from it;
    // anything else is just a name for a placeholder entry.
    async addSession(editor, formData) {
        const text = String(formData.get('displayName') || '').trim();
        const isSessionId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$|^[0-9a-f]{32}$/i.test(text);
        try {
            const cardId = this.cardIdFromEditor(editor);
            if (!cardId) return;
            await BoardApi.addCardSessionAsync(cardId, isSessionId
                ? { id: text, displayName: `Session ${text.slice(0, 8)}` }
                : { displayName: text });
            const card = await this.reloadEditingCard(editor);
            if (!card) return;
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
            const fresh = await this.reloadEditingCard(editor);
            if (!fresh) return;
            this.renderSessionsPanel(editor, fresh);
            this.updateSectionCount(editor, 'sessions', fresh.sessions.length);
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to remove the session.', 'error');
        }
    }

    // Re-reads the open card and keeps the board's copy in step, so a panel
    // re-render and the tile behind the dialog never disagree.
    async reloadEditingCard(editor) {
        const cardId = this.cardIdFromEditor(editor);
        if (!cardId) return null;
        const card = await BoardApi.getBoardCardAsync(cardId);
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
            type: value('#board-card-type'),
            assignee: value('#board-card-assignee'),
            baseLlmOptions: readBoardLaunchOptions(editor, value('#board-card-assignee')),
            priority: value('#board-card-priority'),
            points: value('#board-card-points'),
            tags: value('#board-card-tags').split(',').map(tag => tag.trim()).filter(Boolean),
            blocked: Boolean(editor.querySelector('[data-board-blocked]')?.checked),
            flagged: Boolean(editor.querySelector('[data-board-flagged]')?.checked)
        };
    }

    validateCardTitle(editor, payload) {
        if (!payload.title) {
            this.app.showToast('Board', 'A card needs a title.', 'warning');
            editor.querySelector('#board-card-title')?.focus();
            return false;
        }
        return true;
    }

    async saveCard(editor) {
        if (editor._boardSaving || editor._boardUploading || editor._boardStarting) return;
        const payload = this.readCardForm(editor);
        if (!this.validateCardTitle(editor, payload)) return;
        editor._boardSaving = true;
        let saved = null;
        try {
            const cardId = this.cardIdFromEditor(editor);
            if (cardId) {
                saved = await BoardApi.updateBoardCardAsync(cardId, payload);
                this.app.showToast('Board', 'Card saved.', 'success');
            } else {
                saved = await BoardApi.createBoardCardAsync(payload);
                editor.dataset.cardId = saved.id;
                if (editor._boardCard) Object.assign(editor._boardCard, saved);
                this.app.showToast('Board', `Created ${saved.key}.`, 'success');
            }
            const pending = editor._boardCard?.pendingAttachments || [];
            while (pending.length) {
                const attachment = await BoardApi.addCardAttachmentAsync(saved.id, pending[0]);
                pending.shift();
                editor._boardCard.attachments ||= [];
                editor._boardCard.attachments.push(attachment);
                this.renderAttachmentsPanel(editor, editor._boardCard);
            }
            if (editor.isConnected !== false) this.app.closeModal();
            await this.refresh();
        } catch (error) {
            // The card itself may already be saved and only a queued upload failed — saying the
            // card failed to save sends the user looking for a card that exists. Keep the saved
            // id and remaining upload queue so Save can retry the unfinished uploads.
            this.app.showToast('Board', saved
                ? `${saved.key} was saved, but a file did not upload. ${error?.message || ''}`.trim()
                : error?.message || 'Failed to save the card.', saved ? 'warning' : 'error');
        } finally {
            editor._boardSaving = false;
        }
    }

    // "Start work": the server opens a terminal tab for the assignee with the card
    // prepended to that environment's Initial Message, links the session to the
    // card, and hands back the tab id. Unsaved edits in the editor are saved first
    // so the LLM reads what is on screen.
    hasRunningSession(card) {
        return Boolean(card?.activeSessionId || card?.sessions?.some(session => session.active));
    }

    updateStartWorkButton(editor, card) {
        const chat = editor.querySelector('[data-board-chat]');
        if (chat) {
            chat.disabled = this.hasRunningSession(card);
            chat.title = chat.disabled ? 'An agent is already running. Open it from Sessions.' : 'Open a terminal to discuss this card with the selected LLM';
        }
        const button = editor.querySelector('[data-board-start-work]');
        if (!button) return;
        const running = this.hasRunningSession(card);
        button.disabled = Boolean(editor._boardStarting);
        button.title = running
            ? 'Go to the agent terminal for this card'
            : 'Start the assigned LLM in the background with this card as its first message';
        const label = button.querySelector?.('[data-board-start-work-label]');
        if (label) label.textContent = running ? 'Go to agent' : 'Start work';
        const icon = button.querySelector?.('i');
        if (icon) icon.className = `fa-solid ${running ? 'fa-terminal' : 'fa-play'}`;
    }

    async goToAgent(editor, card) {
        let session = card.sessions?.find(item => item.active && item.tabId);
        if (!session && card.activeTabId) {
            session = { id: card.activeSessionId, tabId: card.activeTabId, selection: card.assignee, displayName: card.title };
        }
        if (!session) {
            const fresh = await BoardApi.getBoardCardAsync(card.id);
            Object.assign(card, fresh);
            this.renderSessionsPanel(editor, card);
            session = card.sessions?.find(item => item.active && item.tabId);
            if (!session && card.activeTabId)
                session = { id: card.activeSessionId, tabId: card.activeTabId, selection: card.assignee, displayName: card.title };
        }
        if (session) await this.focusSessionTab(card, session);
        else this.app.showToast('Board', 'The agent terminal is no longer available. You can replay its session from Sessions.', 'info');
    }

    async startWork(editor, card, intent = 'work') {
        if (!card?.id) return;
        if (editor._boardSaving || editor._boardUploading || editor._boardStarting) return;
        if (this.hasRunningSession(card)) {
            this.updateStartWorkButton(editor, card);
            if (intent === 'work') {
                try { await this.goToAgent(editor, card); }
                catch (error) { this.app.showToast('Board', error?.message || 'Failed to open the agent terminal.', 'error'); }
            }
            return;
        }
        const button = editor.querySelector(intent === 'chat' ? '[data-board-chat]' : '[data-board-start-work]');
        if (button?.disabled) return;
        const payload = this.readCardForm(editor);
        if (!this.validateCardTitle(editor, payload)) return;
        const selection = intent === 'chat' ? editor.querySelector('[data-board-chat-agent]')?.value : payload.assignee;
        if (!selection) {
            this.app.showToast('Board', intent === 'chat' ? 'Choose an LLM beside Chat with.' : 'Assign an LLM to this card first.', 'warning');
            const select = editor.querySelector(intent === 'chat' ? '[data-board-chat-agent]' : '#board-card-assignee');
            if (select?.tomselect) select.tomselect.focus();
            else select?.focus();
            return;
        }
        if (button) button.disabled = true;
        editor._boardStarting = true;
        try {
            const saved = await BoardApi.updateBoardCardAsync(card.id, payload);
            if (this.hasRunningSession(saved)) {
                Object.assign(card, saved);
                editor._boardStarting = false;
                this.updateStartWorkButton(editor, saved);
                if (intent === 'work') await this.goToAgent(editor, card);
                return;
            }
            const result = await BoardApi.launchBoardCardAsync(card.id, { selection, intent });
            const tabId = String(result?.tabId || '').trim();
            if (!tabId) throw new Error('The launch did not return a terminal tab.');

            const info = this.assigneeInfo(result.selection || selection);
            this.app.terminalController?.rememberTabLaunch?.(tabId, {
                selection: result.selection || selection,
                label: `${result.cardKey || card.key} · ${payload.title || card.title}`,
                title: `${result.cardKey || card.key} · ${payload.title || card.title}`,
                taskKey: CARD_TASK_KEY(card.id),
                accentColor: info?.color || null,
                workingDirectory: result.workingDirectory || null
            });
            if (intent === 'chat') {
                if (editor.isConnected !== false) {
                    this.app.closeModal();
                    if (!(await this.app.terminalController?.adoptLaunchedTab?.(tabId))) {
                        this.app.navigate?.('terminal-focus', { preferredTabId: tabId, preferredSelection: result.selection || selection });
                    }
                }
                return;
            }
            if (editor.isConnected !== false) this.app.closeModal();
            this.app.showToast('Board', `${result.cardKey || card.key} started with ${info?.label || 'the LLM'}. Open it from Sessions when ready.`, 'success');
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to start work on the card.', 'error');
            if (button) button.disabled = false;
        } finally {
            editor._boardStarting = false;
        }
    }

    async deleteCurrentCard(editor) {
        const cardId = this.cardIdFromEditor(editor);
        if (!cardId) return;
        const card = this.state.cards.find(c => c.id === cardId);
        const confirmed = await confirmDialog({
            title: 'Delete card',
            message: `Delete ${card?.key || 'this card'}? This cannot be undone.`,
            confirmLabel: 'Delete',
            danger: true
        });
        if (!confirmed) return;

        try {
            await BoardApi.deleteBoardCardAsync(cardId);
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
            const card = await BoardApi.createBoardCardAsync({ columnId, title: text, type: 'task', priority: 'medium' });
            this.app.showToast('Board', `Created ${card.key}.`, 'success');
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to add the card.', 'error');
        }
    }

    // ============================================
    // Board editor (new / rename / delete)
    // ============================================

    openBoardEditor(boardId) {
        const board = boardId ? this.boardById(boardId) : null;
        this.app.showModal(board ? `Board · ${board.name}` : 'New board', `
            <div class="board-lane-editor" data-board-board-editor>
                <label class="board-editor-label" for="board-board-name">Name</label>
                <input type="text" class="form-control form-control-sm mb-3" id="board-board-name" maxlength="60"
                    placeholder="Sprint 12, Website, Q4 bugs…" value="${escapeHtml(board?.name || '')}">
                <p class="board-editor-muted mb-3">${board
                    ? 'Cards keep their keys when they move between boards.'
                    : 'A new board starts with the default lanes. Card keys stay unique across the whole project.'}</p>
                <div class="board-editor-actions mt-4">
                    ${board ? `<button type="button" class="btn btn-sm btn-outline-danger" data-board-delete-board>
                        <i class="fa-solid fa-trash" aria-hidden="true"></i> Delete board
                    </button>` : '<span></span>'}
                    <button type="button" class="btn btn-sm btn-outline-primary" data-board-save-board>${board ? 'Save name' : 'Create'}</button>
                </div>
                ${board ? boardContextSection() : '<p class="board-editor-muted">Save the board to configure agent context.</p>'}
            </div>
        `, { onClose: () => { this.boardSettingsDispose?.(); this.boardSettingsDispose = null; } });

        const container = document.getElementById('modal-container');
        const editor = container?.querySelector('[data-board-board-editor]');
        if (!editor) return;
        if (board) this.boardSettingsDispose = mountBoardContext(this.app, editor.querySelector('[data-board-context]'), board.id);
        editor.querySelector('[data-board-save-board]')?.addEventListener('click', () => this.saveBoard(editor, board));
        editor.querySelector('[data-board-delete-board]')?.addEventListener('click', () => this.deleteBoard(board));
        editor.addEventListener('keydown', event => {
            if (event.key !== 'Enter' || event.target.id !== 'board-board-name') return;
            event.preventDefault();
            this.saveBoard(editor, board);
        });
        editor.querySelector('#board-board-name')?.focus();
    }

    async saveBoard(editor, board) {
        const name = editor.querySelector('#board-board-name')?.value.trim();
        if (!name) {
            this.app.showToast('Board', 'A board needs a name.', 'warning');
            editor.querySelector('#board-board-name')?.focus();
            return;
        }
        try {
            if (board) {
                await BoardApi.updateBoardAsync(board.id, { name });
                this.app.showToast('Board', 'Board renamed.', 'success');
            } else {
                const created = await BoardApi.createBoardAsync({ name });
                // Open the new board straight away: that is what "add" was for.
                this.state.boardId = created.id;
                this.persistBoardSelection();
                this.app.showToast('Board', `Created board ${created.name}.`, 'success');
            }
            this.app.closeModal();
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to save the board.', 'error');
        }
    }

    async deleteBoard(board) {
        if (!board) return;
        if (this.state.boards.length <= 1) {
            this.app.showToast('Board', 'The project needs at least one board.', 'warning');
            return;
        }
        const count = Number(board.cardCount) || 0;
        const confirmed = await confirmDialog({
            title: 'Delete board',
            message: `Delete the ${board.name} board?${count ? ` Its ${count} ${count === 1 ? 'card goes' : 'cards go'} with it.` : ''} This cannot be undone.`,
            confirmLabel: 'Delete',
            danger: true
        });
        if (!confirmed) return;

        try {
            await BoardApi.deleteBoardAsync(board.id);
            if (this.state.boardId === board.id) this.state.boardId = null;
            this.app.closeModal();
            this.app.showToast('Board', 'Board deleted.', 'success');
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to delete the board.', 'error');
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
                ${column ? laneAutomationSection() : '<p class="board-editor-muted mt-3">Save the lane to configure an Automation.</p>'}
            </div>
        `, { onClose: () => { this.state.editingColumnId = null; this.boardSettingsDispose?.(); this.boardSettingsDispose = null; } });

        const container = document.getElementById('modal-container');
        const editor = container?.querySelector('[data-board-lane-editor]');
        if (!editor) return;
        if (column) this.boardSettingsDispose = mountLaneAutomation(this.app, editor.querySelector('[data-lane-automation]'), column.id);

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
        const color = this.state.editingColumnColor;

        try {
            if (this.state.editingColumnId) {
                await BoardApi.updateBoardColumnAsync(this.state.editingColumnId, { name, color });
                this.app.showToast('Board', 'Lane saved.', 'success');
            } else {
                await BoardApi.createBoardColumnAsync({ name, color, boardId: this.state.boardId });
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
