import { AGENT_PURPOSES, agentPurposeLabel, matchesCommentFilter } from './agent-purpose.js';
import { cardChecksSection, bindCardChecks } from './board-card-checks.js';
import { previousWorkHtml } from './board-previous-work.js';
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
import { BoardPicker, openBoardOrderManager } from './board-picker.js';
import { boardBrandLogo } from './board-brand.js';
import { BoardLaneAgents } from './board-lane-agents.js';
import { BOARD_SELECTION_STORAGE_KEY } from './board-selection.js';
import { boardContextSection, laneAutomationSection, mountBoardContext, mountLaneAutomation, boardSettingsNavigation, mountBoardSettingsNavigation, boardSyncSection, mountBoardSync } from './board-settings.js';
import { boardJiraSection, BoardJiraPanel } from './board-jira.js';
import { cardOrganizeSection, bindCardOrganization } from './board-card-organize.js';
import { openBoardSharing, openSharedBoards } from './board-sharing.js';
import { openRemoteBoards } from './board-remote.js';
import { renderCardLinksSection, bindCardLinks } from './board-card-links.js';
import { BoardSearch } from './board-search.js';
import { openLocalCardEditor } from './board-local-card.js';
import { cardAutomationControls, bindCardAutomations } from './board-card-automations.js';
import { cardSharingSection, bindCardSharing } from './board-card-sharing.js';
import { contextSectionMarkup, bindCardContext } from './board-card-context.js';
import { cardDisplayId, cardLabel } from './board-card-label.js';
import { agentMadeSummary, agentProvenanceHtml, bindAgentProvenance } from './board-agent-provenance.js';
import { renderCommentHtml, wrapSelectionAsCode, toPlainPreview } from './board-text.js';
import { historySection, mountHistory } from './board-history.js';
import { bindBoardReferences, canonicalSessionId } from './board-references.js';
import { bindComposerPreview, boardTextOptions } from './board-composer-preview.js';
import { createBoardImagePreviews } from './board-image-previews.js';
import { openDiffModal } from './diff-modal.js';
import * as SessionDebug from './session-viewer.js';
import { renderBoardLaunchOptions, readBoardLaunchOptions, bindBoardLaunchOptions } from './board-launch-options.js';
import { openBoardAttachment, disposeBoardAttachmentPreview, fileToAttachmentPayload, getAttachmentPreviewKind } from './board-attachments.js';

const CARD_TYPES = [
    { value: 'task', label: 'Task' },
    { value: 'bug', label: 'Bug' },
    { value: 'feature', label: 'Feature' },
    { value: 'research-spike', label: 'Research spike' },
    { value: 'chore', label: 'Chore / tech debt' }
];
const LANE_COLORS = ['#64748b', '#3b82f6', '#06b6d4', '#f59e0b', '#10b981', '#a855f7', '#ec4899'];
const FILTERS_STORAGE_KEY = 'viberails.board.filters.v1';
// The last board the user looked at (see board-selection.js).
const BOARD_STORAGE_KEY = BOARD_SELECTION_STORAGE_KEY;
const RASTER_DATA_URL_RE = /^data:image\/(?:png|jpeg|gif|webp);base64,/i;

const emptyFilters = () => ({ q: '', assignee: '', type: '', priority: '', origin: '' });
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

function cardActivitySnapshot(card) {
    return JSON.stringify([card?.comments, card?.notes, card?.sessions, card?.attachments, card?.commits]);
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
        // One per bound composer (description, comment): the `@` file popups of the open editor.
        this.composerDisposers = [];
        this._openCardGeneration = 0;
        this._openCardAbort = null;
        this._refreshGeneration = 0;
        this._cardPageGeneration = -1;
        this._loadedBoardId = null;
        this._pageRequests = new Map();
        this._filterTimer = null;
        this._refreshAbort = null;
        this.cardPage = null;
        this._activityDisposers = [];
        this._activityGeneration = 0;
        BoardApi.attach(app);
        this.laneAgents = new BoardLaneAgents(app, agent => this.goToCardAutomation(agent.cardId, agent.terminalSessionId || agent.sessionId));
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

    async loadView(data = {}) {
        // One-shot (VIBE-36): a terminal tab's card link opens that card. Deleted from the
        // navigation entry so Back or a remount of this view does not open it again.
        const openCardId = typeof data?.openCardId === 'string' ? data.openCardId.trim() : '';
        if (data && Object.prototype.hasOwnProperty.call(data, 'openCardId')) delete data.openCardId;
        const content = document.getElementById('app-content');
        if (!content) return;

        this.destroySortables();
        this.boardPicker?.dispose();
        this.boardPicker = null;
        this.boardOrderDispose?.();
        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('board-template');
        this.root = fragment.querySelector('[data-view="board"]');
        content.appendChild(fragment);
        if (!this.root) return;

        this.state.filters = this.readStoredFilters();
        this.bindShell();

        this.state.columns = [];
        this.state.cards = [];
        if (openCardId) await this.openCardFromNavigation(openCardId);
        else await this.refresh({ restoreSelection: true });
        if (this.root?.isConnected && this.app.currentView === 'board') this.bindSessionActivity();
    }

    // Opens the editor over the card's own board, so closing it leaves the user on the card's lane.
    // An unreadable card (deleted since the link was drawn) loads the default board.
    async openCardFromNavigation(cardId) {
        const root = this.root;
        let card = null;
        let isCurrentProject = false;
        try {
            // History can refer to another local project. Resolve immutable identity first.
            const detail = await BoardApi.getLocalBoardCardAsync(cardId);
            card = detail.card;
            isCurrentProject = detail.isCurrentProject === true;
        } catch (error) {
            if (root === this.root) this.app.showToast('Board', error?.message || 'That card could not be opened.', 'error');
        }
        if (root !== this.root || !root?.isConnected || this.app.currentView !== 'board') return;
        if (card?.boardId && isCurrentProject) this.state.boardId = card.boardId;
        await this.refresh({ restoreSelection: !card?.boardId || !isCurrentProject });
        if (card && root === this.root && root.isConnected && this.app.currentView === 'board') {
            if (isCurrentProject) await this.openCardEditor(card.id);
            else await this.openLocalCard(card.id);
        }
    }

    unload() {
        this.boardPicker?.dispose();
        this.boardPicker = null;
        this.boardOrderDispose?.();
        this.boardOrderDispose = null;
        this.boardSearch?.dispose();
        this.boardSearch = null;
        this.localCardDispose?.();
        this.localCardDispose = null;
        this.laneAgents.dispose();
        this.sharingDispose?.();
        this.sharingDispose = null;
        this.disposeSessionActivity();
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
        this.disposeComposers();
        this.cardLinksDispose?.();
        this.cardLinksDispose = null;
        disposeBoardAttachmentPreview();
        this.root = null;
    }

    bindSessionActivity() {
        this.disposeSessionActivity();
        const refresh = () => {
            clearTimeout(this._activityTimer);
            this._activityTimer = setTimeout(() => void this.refreshSessionActivity(), 500);
        };
        for (const event of ['automation_terminal_started', 'session_started', 'session_completed']) {
            const dispose = this.app.appEventClient?.on(event, refresh);
            if (dispose) this._activityDisposers.push(dispose);
        }
        refresh();
        // Also catches launches in another root and a final event missed during a reconnect.
        this._activityPoll = setInterval(() => {
            if (!document.hidden) void this.refreshVisibleBoard();
        }, 10000);
        const resume = () => { if (!document.hidden) void this.refreshVisibleBoard(); };
        document.addEventListener('visibilitychange', resume);
        window.addEventListener('focus', resume);
        this._activityDisposers.push(() => {
            document.removeEventListener('visibilitychange', resume);
            window.removeEventListener('focus', resume);
        });
    }

    disposeSessionActivity() {
        clearTimeout(this._activityTimer);
        clearInterval(this._activityPoll);
        this._activityAbort?.abort();
        this._boardPollAbort?.abort();
        for (const dispose of this._activityDisposers) dispose();
        this._activityDisposers = [];
        this._activityGeneration++;
    }

    async refreshVisibleBoard() {
        if (this._boardPollPending) return;
        this._boardPollPending = true;
        try {
            await this.refreshBoardSnapshot();
            if (!document.hidden) await this.refreshSessionActivity();
        } finally { this._boardPollPending = false; }
    }

    // Refresh lane membership and summaries without replacing the editor or resetting loaded pages.
    async refreshBoardSnapshot() {
        const root = this.root;
        if (!root?.isConnected || this.app.currentView !== 'board' || !this.state.boardId
            || this._cardPageGeneration !== this._refreshGeneration || this._pageRequests.size || this._boardDragging) return;
        const generation = this._refreshGeneration;
        const boardId = this.state.boardId;
        this._boardPollAbort?.abort();
        const abort = this._boardPollAbort = new AbortController();
        const current = () => !abort.signal.aborted && root === this.root && root.isConnected
            && this.app.currentView === 'board' && boardId === this.state.boardId
            && generation === this._refreshGeneration && !this._boardDragging && !this._pageRequests.size;
        const filters = { ...this.state.filters };
        const loaded = new Map((this.cardPage?.lanes || []).map(lane => [lane.columnId, lane.nextOffset]));
        try {
            const [boards, columns, page] = await Promise.all([
                BoardApi.getBoardsAsync({ signal: abort.signal }),
                BoardApi.getBoardColumnsAsync(boardId, { signal: abort.signal }),
                BoardApi.getBoardCardPageAsync(boardId, filters, { signal: abort.signal })
            ]);
            if (!current()) return;
            if (!boards.some(board => board.id === boardId)) { await this.refresh(); return; }
            const cards = new Map((page.cards || []).map(card => [card.id, card]));
            for (const lane of page.lanes || []) {
                while (lane.hasMore && lane.nextOffset < (loaded.get(lane.columnId) || 0)) {
                    const response = await BoardApi.getBoardCardPageAsync(boardId, filters, {
                        columnId: lane.columnId, offset: lane.nextOffset,
                        continuationToken: lane.continuationToken, signal: abort.signal
                    });
                    if (!current()) return;
                    const next = response.lanes?.find(item => item.columnId === lane.columnId);
                    // A concurrent reorder invalidates this snapshot; retry at the next poll.
                    if (!next || next.restartRequired || next.nextOffset <= lane.nextOffset) return;
                    for (const card of response.cards || []) cards.set(card.id, card);
                    Object.assign(lane, next);
                }
            }
            if (!current()) return;
            const nextCards = [...cards.values()];
            const changed = JSON.stringify([this.state.boards, this.state.columns, this.state.cards, this.cardPage])
                !== JSON.stringify([boards, columns, nextCards, page.lanes ? { ...page, cards: nextCards } : null]);
            this.state.boards = boards;
            this.state.columns = columns;
            this.state.cards = nextCards;
            this.cardPage = page.lanes ? { ...page, cards: nextCards } : null;
            if (changed) this.renderAll(); // renderLanes preserves horizontal and lane scroll positions.
        } catch (error) {
            if (current()) console.warn('Could not refresh Board cards:', error);
        }
    }

    async refreshSessionActivity() {
        const root = this.root;
        if (!root?.isConnected || this.app.currentView !== 'board' || !this.state.boardId || this._activityPending) return;
        const generation = this._activityGeneration;
        const refreshGeneration = this._refreshGeneration;
        const boardId = this.state.boardId;
        const editor = document.querySelector('[data-board-card-editor]');
        const cardId = editor?.dataset.cardId;
        const editorActivity = cardActivitySnapshot(editor?._boardCard);
        const current = () => root === this.root && root.isConnected && generation === this._activityGeneration
            && refreshGeneration === this._refreshGeneration && boardId === this.state.boardId;
        const abort = this._activityAbort = new AbortController();
        this._activityPending = true;
        const loadedIds = [...new Set(this.state.cards.map(card => card.id))];
        const readActivity = async () => {
            const cards = [];
            let columnIds = [];
            // Bound every request and never fetch the unloaded remainder of a completed lane.
            for (let offset = 0; offset < Math.max(1, loadedIds.length); offset += 100) {
                if (!current() || abort.signal.aborted) return { cards: [], columnIds: [] };
                const activity = await BoardApi.getBoardCardActivityAsync(boardId,
                    loadedIds.slice(offset, offset + 100), { signal: abort.signal });
                cards.push(...activity.cards);
                columnIds = activity.activeAutomationColumnIds;
            }
            return { cards, columnIds };
        };
        try {
            const [activity, detail] = await Promise.all([
                readActivity(),
                cardId ? BoardApi.getBoardCardAsync(cardId, { signal: abort.signal }) : null
            ]);
            if (!current()) return;
            const { cards, columnIds } = activity;
            this._activeAutomationColumnIds = columnIds;
            this.laneAgents.updateActivity(columnIds);
            const byId = new Map(cards.map(card => [card.id, card]));
            const tiles = new Map([...root.querySelectorAll('[data-card-id]')].map(tile => [tile.dataset.cardId, tile]));
            for (const card of this.state.cards) {
                const fresh = byId.get(card.id);
                if (!fresh) continue;
                const changed = card.activeTabId !== fresh.activeTabId || card.hasActiveAutomation !== fresh.hasActiveAutomation
                    || card.hasWaitingAutomation !== fresh.hasWaitingAutomation;
                card.activeSessionId = fresh.activeSessionId;
                card.activeTabId = fresh.activeTabId;
                card.hasActiveAutomation = fresh.hasActiveAutomation;
                card.hasWaitingAutomation = fresh.hasWaitingAutomation;
                if (!changed) continue;
                const tile = tiles.get(card.id);
                if (!tile) continue;
                tile.classList.toggle('is-live', Boolean(card.activeTabId));
                const aside = tile.querySelector('.board-card-aside');
                aside?.querySelector('.board-automation-running')?.remove();
                tile.querySelector('.board-automation-waiting')?.remove();
                if (card.hasWaitingAutomation) tile.querySelector('.board-card-title')?.insertAdjacentHTML('afterend', this.waitingAutomationIndicator());
                aside?.querySelector('.board-live-dot')?.remove();
                if (card.hasActiveAutomation) aside?.insertAdjacentHTML('afterbegin', this.automationIndicator());
                if (card.activeTabId) aside?.insertAdjacentHTML('beforeend', '<span class="board-live-dot" title="A terminal session is working this card" aria-label="Session open"></span>');
            }
            // Update only the rails and live controls. Never replace the user's draft fields.
            if (detail && editor.isConnected && editor === document.querySelector('[data-board-card-editor]') && editor.dataset.cardId === cardId
                && !editor._boardUploading && !editor._boardSaving
                // Local comment/rail writes can finish while this GET is in flight. Keep their newer data.
                && cardActivitySnapshot(editor._boardCard) === editorActivity) {
                const card = editor._boardCard || detail;
                const changed = card === detail || JSON.stringify(card.sessions) !== JSON.stringify(detail.sessions)
                    || card.activeSessionId !== detail.activeSessionId || card.activeTabId !== detail.activeTabId;
                const attachmentsChanged = JSON.stringify(card.attachments) !== JSON.stringify(detail.attachments);
                const commitsChanged = JSON.stringify(card.commits) !== JSON.stringify(detail.commits);
                const discussionChanged = cardActivitySnapshot(card) !== cardActivitySnapshot(detail)
                    || JSON.stringify(card.jiraDeliveries) !== JSON.stringify(detail.jiraDeliveries);
                Object.assign(card, { sessions: detail.sessions, activeSessionId: detail.activeSessionId,
                    activeTabId: detail.activeTabId, hasActiveAutomation: detail.hasActiveAutomation,
                    comments: detail.comments, notes: detail.notes, attachments: detail.attachments, commits: detail.commits,
                    jiraDeliveries: detail.jiraDeliveries, jiraIssueUrl: detail.jiraIssueUrl });
                if (changed) this.renderSessionsPanel(editor, card);
                if (attachmentsChanged) this.renderAttachmentsPanel(editor, card);
                if (commitsChanged) this.renderCommitsPanel(editor, card);
                if (discussionChanged) {
                    this.renderCardDiscussion(editor, card);
                    editor.querySelectorAll('[data-board-composer]').forEach(composer => composer._refreshPreview?.());
                }
                void this.cardAutomations?.refresh();
            }
        } catch (error) {
            if (current()) console.warn('Could not refresh Board session activity:', error);
        } finally {
            this._activityPending = false;
        }
    }

    automationIndicator() {
        return '<button type="button" class="board-icon-btn board-automation-running" data-board-action="go-to-automation" title="Go to running Automation" aria-label="Go to running Automation"><i class="fa-solid fa-robot" aria-hidden="true"></i></button>';
    }

    waitingAutomationIndicator() {
        return '<button type="button" class="board-automation-waiting" data-board-action="waiting-automation" title="View waiting Automations or continue without a run"><i class="fa-solid fa-hourglass-half" aria-hidden="true"></i> Waiting for Automation</button>';
    }

    async goToCardAutomation(cardId, sessionId = null) {
        const root = this.root;
        const boardId = this.state.boardId;
        const generation = this._activityGeneration;
        try {
            const card = await BoardApi.getBoardCardAsync(cardId);
            if (root !== this.root || !root?.isConnected || boardId !== this.state.boardId
                || generation !== this._activityGeneration) return;
            const session = card.sessions?.find(item => item.active && item.isAutomation && item.tabId && (!sessionId || item.id === sessionId));
            if (session) await this.focusSessionTab(card, session);
            else this.app.showToast('Board', card.hasActiveAutomation
                ? 'This Automation is running in another VibeRails window. Open it there to view the live terminal.'
                : 'This Automation has finished. Its recording is available on the card.', 'info');
        } catch (error) {
            if (root === this.root && root?.isConnected) this.app.showError(error.message);
        }
    }

    disposeCardPickers() {
        this.cardChecks?.dispose();
        this.cardChecks = null;
        this.cardAutomations?.dispose();
        this.cardAutomations = null;
        this.cardSharing?.dispose();
        this.cardSharing = null;
        try { this.launchOptionsDispose?.(); } catch { /* already torn down */ }
        this.launchOptionsDispose = null;
        try { this.assigneePickerDispose?.(); } catch { /* already torn down */ }
        this.assigneePickerDispose = null;
        try { this.chatPickerDispose?.(); } catch { /* already torn down */ }
        this.chatPickerDispose = null;
    }

    // Separate from disposeCardPickers(): bindCardEditor re-runs that one AFTER the composers
    // are wired (to reset the assignee/chat pickers), which would tear the `@` popups down before
    // they ever opened. Composers live exactly as long as the editor: close or replacement.
    disposeComposers() {
        this.cardOrganizeDispose?.();
        this.cardOrganizeDispose = null;
        this.cardHistoryDispose?.();
        this.cardHistoryDispose = null;
        for (const dispose of this.composerDisposers.splice(0)) {
            try { dispose(); } catch { /* already torn down */ }
        }
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
            const saved = JSON.parse(raw);
            return Object.fromEntries(Object.keys(emptyFilters()).map(key =>
                [key, typeof saved?.[key] === 'string' ? saved[key] : '']));
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

    persistBoardSelection() {
        try {
            if (this.state.boardId) localStorage.setItem(BOARD_STORAGE_KEY, this.state.boardId);
        } catch {
            // Storage blocked: the first board opens next time.
        }
    }

    // The top board in the saved order is the default on every Board view load.
    pickBoardId(boards) {
        const sorted = boards.slice().sort((a, b) => a.position - b.position);
        return sorted[0]?.id || null;
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
            const haystack = [card.displayId, card.key, card.title, card.description, type.value, type.label, ...(card.tags || [])].join(' ').toLowerCase();
            if (!haystack.includes(query)) return false;
        }
        if (filters.assignee && canonicalLlmSelection(card.assignee) !== canonicalLlmSelection(filters.assignee)) return false;
        if (filters.type && cardType(card.type).value !== filters.type) return false;
        if (filters.priority && card.priority !== filters.priority) return false;
        if (filters.origin === 'agent' && !card.agentMade) return false;
        if (filters.origin === 'human' && (card.agentMade || card.jiraIssueKey)) return false;
        if (filters.origin === 'jira' && !card.jiraIssueKey) return false;
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
        const seen = new Map();
        const selections = this.cardPage?.assignees || this.state.cards.map(card => card.assignee);
        for (const selection of selections) {
            const key = canonicalLlmSelection(selection);
            if (!key || seen.has(key)) continue;
            const info = this.assigneeInfo(key);
            if (info) seen.set(key, info);
        }
        return [...seen.values()].sort((a, b) => a.label.localeCompare(b.label));
    }

    hasActiveFilters() {
        const { q, assignee, type, priority, origin } = this.state.filters;
        return Boolean(q.trim() || assignee || type || priority || origin);
    }

    stats() {
        // The flagged count replaced the remaining-points total and keeps its scope: active
        // filters, cards that paging has not loaded yet, and open lanes only. A flag left on a
        // completed card is not a call for attention. The server page applies the same lane rule.
        if (this.cardPage) return {
            cards: this.cardPage.filteredCount,
            flagged: Number(this.cardPage.flaggedCount) || 0,
            blocked: this.cardPage.blockedCount
        };
        const visible = this.filteredCards();
        const open = visible.filter(card => {
            const column = this.columnById(card.columnId);
            return column && !this.isDoneLane(column);
        });
        return {
            cards: visible.length,
            flagged: open.filter(card => card.flagged).length,
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
        this.boardSearch?.dispose();
        this.boardSearch = new BoardSearch(this.root, {
            currentBoardId: () => this.state.boardId,
            openCard: card => card.isCurrentProject === true
                ? this.openCardEditor(card.id) : this.openLocalCard(card.id)
        });
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
        bindSelect('[data-board-filter-origin]', 'origin');

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

    // Board names stay uncluttered; counts belong in the stats and lane headers.
    renderBoardPicker() {
        const select = this.query('[data-board-select]');
        if (!select) return;
        if (this.boardPicker?.select !== select) {
            this.boardPicker?.dispose();
            this.boardPicker = new BoardPicker(select, () => this.manageBoards());
        }
        this.boardPicker.update(this.state.boards, this.state.boardId);
        const settings = this.query('[data-board-action="edit-board"]');
        if (settings) settings.title = `Settings for ${this.currentBoard()?.name || 'this board'}`;
    }

    manageBoards() {
        this.boardOrderDispose?.();
        this.boardOrderDispose = openBoardOrderManager(this.app, boards => {
            this.state.boards = boards;
            this.renderBoardPicker();
            void this.refresh();
        });
    }

    renderToolbar() {
        const stats = this.stats();
        const statsHost = this.query('[data-board-stats]');
        if (statsHost) {
            statsHost.innerHTML = `
                <span class="board-stat"><strong>${stats.cards}</strong> ${stats.cards === 1 ? 'card' : 'cards'}</span>
                <span class="board-stat${stats.flagged ? ' is-flagged' : ''}" title="Cards that need your attention"><strong>${stats.flagged}</strong> flagged</span>
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

        const origin = this.query('[data-board-filter-origin]');
        if (origin) origin.value = this.state.filters.origin || '';

        const search = this.query('[data-board-search]');
        if (search && document.activeElement !== search) search.value = this.state.filters.q;
        this.boardSearch?.setQuery(this.state.filters.q);

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

    jiraBadge(card) {
        if (!card?.jiraIssueKey) return '';
        const badge = `<span class="badge text-bg-primary" title="Imported from Jira: ${escapeHtml(card.jiraIssueKey)}">${boardBrandLogo(true, '')} Jira · ${escapeHtml(card.jiraIssueKey)}</span>`;
        try {
            const url = new URL(card.jiraIssueUrl);
            if (url.protocol === 'https:' && !url.username && !url.password)
                return `<a href="${escapeHtml(url.href)}" target="_blank" rel="noopener noreferrer" aria-label="Open ${escapeHtml(card.jiraIssueKey)} in Jira">${badge}</a>`;
        } catch { /* Unlinked or older card response. */ }
        return badge;
    }

    renderCard(card) {
        const member = this.assigneeInfo(card.assignee);
        const type = cardType(card.type);
        const live = card.activeTabId
            ? `<span class="board-live-dot" title="A terminal session is working this card" aria-label="Session open"></span>`
            : '';
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
                tabindex="0" role="button" aria-label="${escapeHtml(cardDisplayId(card))}: ${escapeHtml(card.title)}${card.agentMade ? ` — ${escapeHtml(agentMadeSummary(card))}` : ''}${card.flagged ? ' — Needs your attention' : ''}">
                <span class="board-card-rail" data-priority="${escapeHtml(card.priority)}"
                    title="${escapeHtml(card.priority)} priority"></span>
                <div class="board-card-body">
                    <div class="board-card-top">
                        <span class="board-key">${card.flagged ? '<i class="fa-solid fa-flag board-attention-flag" title="Needs your attention" aria-hidden="true"></i> ' : ''}${card.agentMade ? `<i class="fa-solid fa-robot board-agent-mark" title="${escapeHtml(agentMadeSummary(card))}" aria-label="${escapeHtml(agentMadeSummary(card))}"></i> ` : ''}${escapeHtml(cardDisplayId(card))}</span>
                        <span class="board-card-top-right">
                            <span class="board-type-chip" data-type="${escapeHtml(type.value)}"
                                title="${escapeHtml(type.label)}">${escapeHtml(type.label)}</span>
                            <span class="board-priority-chip" data-priority="${escapeHtml(card.priority)}">${escapeHtml(card.priority)}</span>
                        </span>
                    </div>
                    ${card.jiraIssueKey ? `<div class="board-card-origin">${this.jiraBadge(card)}</div>` : ''}
                    <h3 class="board-card-title">${escapeHtml(card.title)}</h3>
                    ${card.hasWaitingAutomation ? this.waitingAutomationIndicator() : ''}
                    ${excerpt ? `<p class="board-card-excerpt">${escapeHtml(excerpt)}</p>` : ''}
                    <div class="board-card-meta">
                        <div class="board-card-aside">
                            ${card.hasActiveAutomation ? this.automationIndicator() : ''}
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
        if (/build|progress/i.test(column.name)) return 'Drag work in, or use New card.';
        if (/ready|next/i.test(column.name)) return 'Queue the next thing to build.';
        return 'Nothing here. Add a card or drag one in.';
    }

    renderLanes() {
        const scroll = this.captureScroll();
        const visible = this.filteredCards();

        const html = this.state.columns
            .slice()
            .sort((a, b) => a.position - b.position)
            .map((column, index) => {
                const cards = visible
                    .filter(card => card.columnId === column.id)
                    .sort((a, b) => Number(Boolean(b.flagged)) - Number(Boolean(a.flagged))
                        || a.position - b.position);
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
                        ${this.laneAgents.button(column)}
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
                    </section>`;
            })
            .join('');

        const host = this.query('[data-board-lanes]');
        if (host) host.innerHTML = html;
        if (host) {
            this.laneAgents.mount(host);
            this.laneAgents.updateActivity(this._activeAutomationColumnIds);
        }
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
        this.boardSearch?.setQuery(this.state.filters.q);
        this._boardPollAbort?.abort();
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
        this._boardPollAbort?.abort();
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
                columnId, offset: lane.nextOffset, continuationToken: lane.continuationToken, signal: request.signal
            });
            if (!isCurrent()) return;
            const next = response.lanes?.find(item => item.columnId === columnId);
            if (next?.restartRequired) {
                await this.refresh();
                return;
            }
            const cards = new Map(this.state.cards.map(card => [card.id, card]));
            for (const card of response.cards || []) cards.set(card.id, card);
            this.state.cards = [...cards.values()];
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
                onStart: () => { this._boardDragging = true; this._boardPollAbort?.abort(); },
                onMove: () => {
                    this.queryAll('.board-lane').forEach(lane => lane.classList.remove('is-drop-target'));
                },
                onChange: event => {
                    this.queryAll('.board-lane').forEach(lane => lane.classList.remove('is-drop-target'));
                    event.to.closest('.board-lane')?.classList.add('is-drop-target');
                },
                onEnd: async event => {
                    try { await this.onCardDropped(event); } finally { this._boardDragging = false; }
                }
            }));
        });

        const lanes = this.query('[data-board-lanes]');
        if (!lanes) return;
        this.sortables.push(window.Sortable.create(lanes, {
            animation,
            handle: '.board-lane-grip',
            draggable: '.board-lane',
            onStart: () => { this._boardDragging = true; this._boardPollAbort?.abort(); },
            onEnd: async () => {
                try { await this.onLanesReordered(); } finally { this._boardDragging = false; }
            }
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
        this.boardSearch?.setQuery(this.state.filters.q, { force: true });
        this._boardPollAbort?.abort();
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
            this._activeAutomationColumnIds = [];
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
        if (action !== 'lane-agents') this.laneAgents.close(false);

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
            case 'waiting-automation':
                if (cardEl) void this.openCardEditor(cardEl.dataset.cardId);
                break;
            case 'lane-agents':
                this.laneAgents.open(trigger, this.state.columns.find(column => column.id === trigger.dataset.columnId));
                break;
            case 'go-to-automation':
                event.stopPropagation();
                if (cardEl) void this.goToCardAutomation(cardEl.dataset.cardId);
                break;
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
            case 'share-board':
                this.sharingDispose?.();
                this.sharingDispose = openBoardSharing(this.app, this.state.boardId);
                break;
            case 'shared-boards':
                this.sharingDispose?.();
                this.sharingDispose = openSharedBoards(this.app, async boardId => {
                    this.state.boardId = boardId; this.persistBoardSelection(); await this.refresh();
                });
                break;
            case 'remote-boards':
                this.sharingDispose?.();
                this.sharingDispose = openRemoteBoards(this.app, () => this.refreshBoardSnapshot());
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

    async openLocalCard(cardId) {
        const generation = ++this._openCardGeneration;
        this._openCardAbort?.abort();
        const abort = this._openCardAbort = new AbortController();
        try {
            const detail = await BoardApi.getLocalBoardCardAsync(cardId, { signal: abort.signal });
            if (abort.signal.aborted || generation !== this._openCardGeneration) return;
            if (detail.isCurrentProject === true) return this.openCardEditor(detail.card.id);
            this.localCardDispose?.();
            this.localCardDispose = openLocalCardEditor(this.app, detail, {
                openCard: id => this.openLocalCard(id),
                onChanged: () => this.boardSearch?.setQuery(this.state.filters.q, { force: true })
            });
        } catch (error) {
            if (!abort.signal.aborted && generation === this._openCardGeneration)
                this.app.showToast('Board', error?.message || 'That card could not be opened.', 'error');
        }
    }

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

        this.app.showModal(card ? cardLabel(card) : 'New card', `
            <div class="board-card-editor" data-board-card-editor data-card-id="${escapeHtml(card?.id || '')}">
                <div class="board-editor-scroll">
                <div class="board-editor-main">
                    ${this.jiraBadge(card)}
                    ${card && card.boardId !== this.state.boardId ? `<div class="alert alert-info" role="note">This card is on another board: <strong>${escapeHtml(this.boardById(card.boardId)?.name || '')}</strong>. Changes are saved to that board.</div>` : ''}
                    <input type="text" class="form-control board-editor-title" id="board-card-title"
                        placeholder="What needs to happen" value="${escapeHtml(card?.title || '')}"
                        aria-label="Card title" spellcheck="true">

                    <section class="board-block">
                        <h3 class="board-block-label">Description</h3>
                        ${this.composerMarkup({
                            name: 'description',
                            value: card?.description || '',
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
                        <h3 class="board-block-label">Comments <span class="board-count" data-board-count="comments">${card?.comments?.length || 0}</span></h3>
                        ${card ? `<label class="board-editor-label" for="board-comment-filter">Show comments</label>
                        <select class="form-select form-select-sm mb-2" id="board-comment-filter" data-board-comment-filter>
                            <option value="all">All comments</option>
                            <option value="human">Hide agent comments</option>
                            <option value="agent">Agent comments</option>
                            ${AGENT_PURPOSES.map(([value, label]) => `<option value="${value}">${escapeHtml(label)} agent comments</option>`).join('')}
                        </select>
                        <p class="board-editor-muted">Attention comments always appear first.</p>` : ''}
                        <div data-board-jira-delivery-status></div>
                        <div class="board-comments" data-board-comments></div>
                        ${card?.jiraIssueKey ? '<label class="form-check mb-2"><input class="form-check-input" type="checkbox" data-board-jira-sync checked> Post this comment to Jira</label><p class="board-editor-muted">Uncheck for an internal note. Linked sessions get a public replay link posted to Jira.</p>' : ''}
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
                        ${agentProvenanceHtml(card)}
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

                    ${card ? `<section class="board-side-section board-discussion">
                        <label class="board-editor-label" for="board-chat-agent">Discuss this card</label>
                        <div class="board-chat-controls">
                            <select id="board-chat-agent" class="form-select form-select-sm" data-board-chat-agent aria-label="Agent for card discussion"></select>
                            <button type="button" class="btn btn-link btn-sm" data-board-chat aria-describedby="board-chat-help">
                                <i class="fa-solid fa-comments" aria-hidden="true"></i> Chat with agent
                            </button>
                        </div>
                        <label class="board-editor-label mt-2" for="board-chat-question">Initial question (optional)</label>
                        <textarea id="board-chat-question" class="form-control form-control-sm" data-board-chat-question maxlength="1000" rows="2" spellcheck="true"></textarea>
                        <p class="board-editor-muted mt-2" id="board-chat-help">Ask about previous work. Leave the question empty to open a discussion and wait.</p>
                    </section>` : ''}


                    ${card ? cardChecksSection() : ''}
                    <div data-board-previous-work>${previousWorkHtml(card)}</div>
                    ${card ? cardOrganizeSection() : ''}
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
                                placeholder="Link a commit sha" autocomplete="off" spellcheck="false" aria-label="Commit sha">
                            <input type="hidden" name="message" value="">
                            <button type="submit" class="board-side-add" title="Link this commit" aria-label="Link this commit">
                                <i class="fa-solid fa-plus" aria-hidden="true"></i>
                            </button>
                        </form>` : ''}
                    </section>

                    <section class="board-side-section">
                        <h3 class="board-side-label">
                            <i class="fa-solid fa-terminal" aria-hidden="true"></i>
                            Sessions <span class="board-count" data-board-count="sessions">${(card?.sessions || []).filter(session => !session.isAutomation && !session.isReview && session.origin !== 'code_review').length}</span>
                        </h3>
                        <div class="board-side-list" data-board-sessions></div>
                        <p class="board-side-empty">Link the same session to every card it is working on.</p>
                        ${card ? `
                        <form class="board-side-form" data-board-add-session>
                            <input type="text" class="form-control form-control-sm" name="displayName"
                                placeholder="Paste a session id" autocomplete="off" spellcheck="false" aria-label="Session id or name">
                            <button type="submit" class="board-side-add" title="Add this session" aria-label="Add this session">
                                <i class="fa-solid fa-plus" aria-hidden="true"></i>
                            </button>
                        </form>` : ''}
                    </section>

                    <section class="board-side-section">
                        <h3 class="board-side-label">
                            <i class="fa-solid fa-robot" aria-hidden="true"></i>
                            Automations <span class="board-count" data-board-count="automations">${(card?.sessions || []).filter(session => session.isAutomation || session.isReview || session.origin === 'code_review').length}</span>
                        </h3>
                        ${cardAutomationControls(Boolean(card))}
                        <div class="board-side-list" data-board-automations></div>
                    </section>

                    ${cardSharingSection(Boolean(card))}

                    ${card ? `<section class="board-side-section"><details data-board-advanced>
                        <summary class="board-side-label">Advanced</summary>
                        <label class="board-editor-label mt-2" for="board-card-display-id">Display ID</label>
                        <input class="form-control form-control-sm" id="board-card-display-id" maxlength="32" spellcheck="false" value="${escapeHtml(cardDisplayId(card))}">
                        <p class="board-editor-muted mt-2">Permanent ID: <code>${escapeHtml(card.key)}</code></p>
                        ${contextSectionMarkup()}${historySection()}
                    </details></section>` : ''}

                    </div>
                </aside>
                </div>

                    <div class="board-editor-actions">
                        ${card ? `<button type="button" class="btn btn-sm btn-outline-danger" data-board-delete-card>
                            <i class="fa-solid fa-trash" aria-hidden="true"></i> Delete
                        </button>` : '<span></span>'}
                        <span class="board-editor-actions-main">
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
            this.disposeComposers();
            this.cardLinksDispose?.();
            this.cardLinksDispose = null;
            this.cardContextDispose?.();
            this.cardContextDispose = null;
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

    // Cards stay on their board. A different board starts with a new card.
    laneOptionsHtml(selectedId) {
        const option = column => `<option value="${escapeHtml(column.id)}"${column.id === selectedId ? ' selected' : ''}>${escapeHtml(column.name)}</option>`;
        const board = this.state.boards.find(board => (board.columns || []).some(column => column.id === selectedId));
        return (board?.columns || this.state.columns).slice()
            .sort((a, b) => a.position - b.position).map(option).join('');
    }

    // The card id lives on the editor (data-card-id), not on controller state.
    // showModal replacement runs the previous editor's onClose, which used to
    // clear a shared editingCardId and turn the next Save into a create.
    cardIdFromEditor(editor) {
        return String(editor?.dataset?.cardId || '').trim() || null;
    }

    /**
     * True while the editor is busy or holds anything unsaved: field edits, a draft comment or
     * attachments not yet uploaded. The one "save first" rule behind organizing and sharing a card.
     */
    cardHasDraft(editor) {
        return Boolean(editor._boardSaving || editor._boardUploading || editor._boardStarting || editor._boardOrganizing
            || Object.keys(this.cardChanges(editor, this.readCardForm(editor))).length > 0
            || editor.querySelector('[data-board-composer="comment"] [data-board-composer-input]')?.value.trim()
            || editor._boardCard?.pendingAttachments?.length);
    }

    bindCardEditor(editor, card) {
        card = card || { id: null, attachments: [], pendingAttachments: [] };
        editor._boardCard = card;
        editor.addEventListener('click', event => {
            const target = event.target.closest('[data-board-ref-session], [data-board-ref-commit], [data-board-ref-card], [data-board-image]');
            if (!target) return;
            event.preventDefault();
            if (target.dataset.boardRefSession) {
                const id = canonicalSessionId(target.dataset.boardRefSession);
                const session = (card.sessions || []).find(s => canonicalSessionId(s.id) === id) || { id };
                if (session.active && session.tabId) void this.focusSessionTab(card, session);
                else this.openSessionReplay(session);
            } else if (target.dataset.boardRefCommit) void this.openCommitDiff(card, target.dataset.boardRefCommit);
            else if (target.dataset.boardRefCard) {
                void this.openLinkedCard(editor, target.dataset.boardRefCard);
            } else {
                const attachment = card.attachments?.find(a => a.id === target.dataset.boardImage);
                if (attachment) void openBoardAttachment(this.app, card.id, attachment);
            }
        });
        this.cardOrganizeDispose?.();
        this.cardOrganizeDispose = bindCardOrganization(editor, card, {
            hasDraft: () => this.cardHasDraft(editor),
            onChanged: async result => {
                this.app.closeModal();
                if (result.boardId !== this.state.boardId) await this.switchBoard(result.boardId);
                else await this.refresh();
                await this.openCardEditor(result.id);
            }
        });
        this.cardLinksDispose = bindCardLinks(editor, card, {
            openCard: id => this.openLinkedCard(editor, id),
            showError: message => this.app.showToast('Board', message, 'error'),
            onChanged: () => editor._boardContext?.refresh()
        });
        // Link navigation replaces this editor. Track form edits separately from link search.
        const trackEdits = event => {
            if (event.target.closest('.board-side-fields, [data-board-composer="description"], #board-card-title, #board-card-display-id'))
                editor._boardHasEdits = true;
        };
        editor.addEventListener('input', trackEdits);
        editor.addEventListener('change', trackEdits);
        editor._boardDiscussionImages = createBoardImagePreviews(() => editor._boardCard,
            () => this.applyCommentClamps(editor.querySelector('[data-board-comments]')));
        this.composerDisposers.push(() => editor._boardDiscussionImages.dispose());
        editor.querySelector('[data-board-comment-filter]')?.addEventListener('change', () => {
            this.renderCardDiscussion(editor, editor._boardCard);
        });
        this.renderCardDiscussion(editor, card);
        if (card) this.cardHistoryDispose = mountHistory(editor.querySelector('[data-board-history-view]'), card.boardId, card.id);
        this.renderCommitsPanel(editor, card);
        this.renderSessionsPanel(editor, card);
        this.renderAttachmentsPanel(editor, card);
        // Agent context (VB-63): what an agent launched on this card would read, measured server-side
        // when the Advanced section is opened, and again after rail changes while it stays open.
        this.cardContextDispose?.();
        const cardContext = bindCardContext(editor, card);
        editor._boardContext = cardContext;
        this.cardContextDispose = cardContext ? () => cardContext.dispose() : null;
        editor.querySelector('[data-board-add-files]')?.addEventListener('click', () => editor.querySelector('[data-board-files]')?.click());
        editor.querySelector('[data-board-files]')?.addEventListener('change', event => {
            const files = Array.from(event.target.files || []);
            event.target.value = '';
            this.attachImages(editor.querySelector('[data-board-composer="description"]'),
                editor.querySelector('[data-board-composer="description"] [data-board-composer-input]'), card, files, { inline: false });
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
        this.cardChecks = bindCardChecks(editor, card, this.app);
        this.cardSharing = bindCardSharing(editor, card, { app: this.app, hasDraft: () => this.cardHasDraft(editor) });
        bindAgentProvenance(editor.querySelector('[data-board-agent-provenance]'),
            (sessionId, seekToUtc) => this.openSessionReplay({ id: sessionId }, { seekToUtc }));
        this.cardAutomations = bindCardAutomations(editor, card, {
            app: this.app,
            onQueued: () => void this.refreshSessionActivity()
        });
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
                selectedValue: card.sessions?.filter(session => session.origin === 'chat').at(-1)?.selection || card.assignee || getEnabledLlmItems(this.app, 'sandbox')[0]?.key || ''
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

        editor._boardFormBaseline = this.readCardForm(editor);
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
            if (editor.isConnected !== false) await this.openLocalCard(cardId);
        } finally {
            editor._boardOpeningLink = false;
        }
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
                        title="Attach a file (or paste an image)"${off}>
                        <i class="fa-solid fa-paperclip" aria-hidden="true"></i><span>Attach</span>
                    </button>
                    <span class="board-composer-hint">@ file · ! card/session · # commit</span>
                </div>
                <textarea class="form-control board-composer-input" data-board-composer-input
                    placeholder="${escapeHtml(placeholder)}" rows="3" spellcheck="true"${off}>${escapeHtml(value)}</textarea>
                ${name === 'comment' ? '<div class="board-comment-body board-composer-live" data-board-composer-live aria-label="Live preview"></div>' : ''}
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
        input.addEventListener('input', autoGrow);
        this.composerDisposers.push(bindComposerPreview(composer, input, card));
        this.composerDisposers.push(bindBoardReferences(input, { app: this.app, host: composer, card,
            onLink: async item => {
                if (item.kind === 'session' && card.sessions?.some(s => canonicalSessionId(s.id) === canonicalSessionId(item.session.id))) return;
                if (item.kind === 'commit' && card.commits?.some(c => c.sha === item.commit.sha)) return;
                if (!card.id) {
                    card.pendingReferences ||= [];
                    card.pendingReferences.push(item);
                    if (item.kind === 'session') (card.sessions ||= []).push(item.session);
                    else (card.commits ||= []).push(item.commit);
                    return;
                }
                const editor = composer.closest('[data-board-card-editor]');
                if (item.kind === 'session') {
                    const session = await BoardApi.addCardSessionAsync(card.id, { id: item.session.id, displayName: item.session.displayName || item.session.sessionDisplayName });
                    item.session = session;
                    card.sessions = [...(card.sessions || []).filter(s => s.id !== session.id), session];
                    if (editor?.isConnected) this.renderSessionsPanel(editor, card);
                } else {
                    const commit = await BoardApi.addCardCommitAsync(card.id, { sha: item.commit.sha });
                    item.commit = commit;
                    card.commits = [...(card.commits || []).filter(c => c.sha !== commit.sha), commit];
                    if (editor?.isConnected) this.renderCommitsPanel(editor, card);
                }
            }
        }));
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
                    input.dispatchEvent(new Event('input', { bubbles: true }));
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
                    payload.id = `pending_${crypto.randomUUID().replaceAll('-', '')}`;
                    card.pendingAttachments.push(payload);
                    if (inline && getAttachmentPreviewKind(payload) === 'image')
                        insertAtCursor(input, `![${payload.name.replace(/[\[\]\r\n]/g, '')}](attachment:${payload.id})`);
                    if (editor) this.renderAttachmentsPanel(editor, card);
                    continue;
                }
                const attachment = await BoardApi.addCardAttachmentAsync(card.id, payload);
                card.attachments = card.attachments || [];
                card.attachments.push(attachment);
                if (inline && getAttachmentPreviewKind(attachment) === 'image')
                    insertAtCursor(input, `![${attachment.name.replace(/[\[\]\r\n]/g, '')}](attachment:${attachment.id})`);
                if (editor) this.renderAttachmentsPanel(editor, card);
                editor?._boardContext?.refresh();
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
                <button type="button" class="btn btn-sm btn-outline-danger board-attachment-remove" data-board-remove-attachment="${escapeHtml(attachment.id)}" aria-label="Delete ${escapeHtml(attachment.name)}"><i class="fa-solid fa-trash-can" aria-hidden="true"></i> Delete</button>
            </div>`).join('') + pending.map((attachment, index) => `
            <div class="board-attachment-row"><span class="board-side-copy"><span class="board-side-title">${escapeHtml(attachment.name)}</span>
                <span class="board-side-sub">${formatFileSize(attachment.bytes)} · Uploads when you save</span></span>
                <button type="button" class="btn btn-sm btn-outline-secondary board-attachment-remove" data-board-remove-pending="${index}" aria-label="Remove ${escapeHtml(attachment.name)}"><i class="fa-solid fa-xmark" aria-hidden="true"></i> Remove</button>
            </div>`).join('') || '<p class="board-editor-muted">No files attached.</p>';
        host.querySelectorAll('[data-board-view-attachment]').forEach(button => button.addEventListener('click', async () => {
            const attachment = attachments.find(item => item.id === button.dataset.boardViewAttachment);
            if (!attachment) return;
            try { await openBoardAttachment(this.app, card.id, attachment); }
            catch (error) { this.app.showToast('Board', error?.message || 'Could not open the file.', 'error'); }
        }));
        host.querySelectorAll('[data-board-remove-pending]').forEach(button => button.addEventListener('click', () => {
            if (editor._boardUploading || editor._boardSaving || editor._boardStarting) return;
            pending.splice(Number(button.dataset.boardRemovePending), 1);
            this.renderAttachmentsPanel(editor, card);
            editor.querySelectorAll('[data-board-composer]').forEach(composer => composer._refreshPreview?.());
        }));
        host.querySelectorAll('[data-board-remove-attachment]').forEach(button => button.addEventListener('click', async () => {
            if (editor._boardUploading || editor._boardSaving || editor._boardStarting) return;
            const attachment = attachments.find(item => item.id === button.dataset.boardRemoveAttachment);
            if (!attachment) return;
            // Share the attachment-mutation guard with uploads so Save and other removals
            // cannot race this operation or reintroduce a removed file from a stale array.
            editor._boardUploading = true;
            button.disabled = true;
            try {
                if (!await confirmDialog({ title: 'Delete attachment', message: `Delete ${attachment.name} from this card? This also removes its inline previews.`, confirmLabel: 'Delete', danger: true }) || !editor.isConnected) return;
                await BoardApi.deleteCardAttachmentAsync(card.id, attachment.id);
                card.attachments = card.attachments.filter(item => item.id !== attachment.id);
                if (!editor.isConnected) return;
                this.renderAttachmentsPanel(editor, card);
                editor._boardContext?.refresh();
                // Repaint from the current text, preserving every unsaved field and comment.
                editor.querySelectorAll('[data-board-composer]').forEach(composer => {
                    composer._refreshPreview?.();
                });
                this.renderCardDiscussion(editor, card);
                this.app.showToast('Board', 'Attachment deleted.', 'success');
            } catch (error) {
                if (editor.isConnected) this.app.showToast('Board', error?.message || 'Could not delete the file.', 'error');
            } finally {
                editor._boardUploading = false;
                button.disabled = false;
            }
        }));
    }

    // ============================================
    // Comments
    // ============================================

    // Legacy note rows join the shared comment stream.
    // History has an explicit settings request and never enters this response.
    renderCardDiscussion(editor, card) {
        const host = editor.querySelector('[data-board-comments]');
        if (!host) return;
        // Markdown, attachments, session and commit labels are shared with the comment composer.
        const textOptions = boardTextOptions(card);
        const deliveryStatus = editor.querySelector('[data-board-jira-delivery-status]');
        if (deliveryStatus) {
            const issues = (card?.jiraDeliveries || []).filter(d => ['failed', 'uncertain'].includes(d.status));
            const pending = (card?.jiraDeliveries || []).filter(d => ['pending', 'sending'].includes(d.status)).length;
            deliveryStatus.innerHTML = issues.map(d => `<p class="text-warning">Jira ${d.kind === 'session' ? 'session link' : 'comment'}: ${escapeHtml(d.message || d.status)}</p>`).join('')
                + (pending ? `<p class="board-editor-muted">${pending} Jira update${pending === 1 ? '' : 's'} waiting for delivery.</p>` : '');
        }
        const comments = [...new Map([...(card?.comments || []), ...(card?.notes || [])].map(entry => [entry.id, entry])).values()]
            .sort((a, b) => Number(b.isAttention === true) - Number(a.isAttention === true)
                || String(a.createdAt).localeCompare(String(b.createdAt)) || String(a.id).localeCompare(String(b.id)));
        this.updateSectionCount(editor, 'comments', comments.length);
        const filter = editor.querySelector('[data-board-comment-filter]')?.value || 'all';
        const visible = comments.filter(entry => matchesCommentFilter(entry, filter));
        host.innerHTML = visible.length
            ? visible.map(entry => this.cardLogCommentHtml(entry, textOptions)).join('')
            : `<p class="board-editor-muted">${comments.length ? 'No comments match this filter.' : 'No comments yet.'}</p>`;
        editor._boardDiscussionImages?.hydrate(host);
        host.querySelectorAll('[data-board-delete-comment]').forEach(button => {
            button.addEventListener('click', () => this.deleteComment(editor, button.dataset.boardDeleteComment));
        });

        editor.querySelectorAll('[data-board-comment-jump]').forEach(button => {
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

    // Human and agent entries share the same discussion.
    cardLogCommentHtml(entry, textOptions) {
        const author = this.authorInfo(entry.author);
        // An agent entry knows the terminal session that wrote it and when: the link replays
        // that session seeked to this moment (session-viewer.js seekToUtc).
        const sessionId = entry.author?.kind === 'agent' ? String(entry.author.sessionId || '') : '';
        const jump = sessionId
            ? `<button type="button" class="board-comment-jump" data-board-comment-jump="${escapeHtml(sessionId)}"
                data-board-comment-at="${escapeHtml(entry.createdAt || '')}"
                title="Replay the session at the moment this was written">
                <i class="fa-solid fa-clock-rotate-left" aria-hidden="true"></i> in session</button>`
            : '';
        return `
            <article class="board-comment${entry.author?.kind === 'agent' ? ' is-agent' : ''}${entry.isAttention === true ? ' is-attention' : ''}">
                ${this.avatarHtml(author, 28, { filterable: false })}
                <div class="board-comment-content">
                    <div class="board-comment-meta">
                        <span class="board-comment-author">${escapeHtml(author?.label || 'Someone')}</span>
                        ${entry.syncToJira === false ? '<span class="badge text-bg-secondary">Not sent to Jira</span>' : ''}
                        ${entry.purpose && entry.purpose !== 'work' ? `<span class="badge text-bg-secondary">${escapeHtml(agentPurposeLabel(entry.purpose))}</span>` : ''}
                        <span class="board-comment-when">${jump}${escapeHtml(this.formatDateTime(entry.createdAt))}<button type="button" class="btn btn-link btn-sm text-danger" data-board-delete-comment="${escapeHtml(entry.id)}" aria-label="Delete comment" title="Delete comment"><i class="fa-solid fa-trash" aria-hidden="true"></i></button></span>
                    </div>
                    ${entry.isAttention === true ? '<div class="board-comment-attention"><i class="fa-solid fa-flag" aria-hidden="true"></i> Needs your attention</div>' : ''}
                    <div class="board-comment-body" data-board-comment-body>${renderCommentHtml(entry.body, textOptions)}</div>
                    <button type="button" class="board-comment-more" data-board-comment-more hidden>Show more</button>
                </div>
            </article>`;
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

    async deleteComment(editor, commentId) {
        if (editor._boardDeletingComment || editor._boardSaving || editor._boardStarting) return;
        editor._boardDeletingComment = true;
        try {
            if (!await confirmDialog({ title: 'Delete comment', message: 'Delete this comment from the discussion?', confirmLabel: 'Delete', danger: true })) return;
            if (editor.isConnected === false) return;
            await BoardApi.deleteBoardCommentAsync(this.cardIdFromEditor(editor), commentId);
            const card = await this.reloadEditingCard(editor);
            if (card && editor.isConnected !== false) this.renderCardDiscussion(editor, card);
            await this.refresh();
        } catch (error) {
            this.app.showToast('Board', error?.message || 'Failed to delete the comment.', 'error');
        } finally { editor._boardDeletingComment = false; }
    }

    async postComment(editor, body) {
        const cardId = this.cardIdFromEditor(editor);
        if (!cardId) return;
        const composerInput = editor.querySelector('[data-board-composer="comment"] [data-board-composer-input]');
        try {
            await BoardApi.addBoardCommentAsync(cardId, { body, syncToJira: editor.querySelector('[data-board-jira-sync]')?.checked !== false });
            const card = await this.reloadEditingCard(editor);
            if (!card) return;
            if (composerInput) {
                composerInput.value = '';
                composerInput.style.height = 'auto';
                composerInput.dispatchEvent(new Event('input', { bubbles: true }));
            }
            this.renderCardDiscussion(editor, card);
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
        const sessions = card?.sessions || [];
        this.renderSessionList(editor, card, sessions.filter(session => !session.isAutomation && !session.isReview && session.origin !== 'code_review'), 'sessions');
        this.renderSessionList(editor, card, sessions.filter(session => session.isAutomation || session.isReview || session.origin === 'code_review'), 'automations');
    }

    renderSessionList(editor, card, sessions, section) {
        const host = editor.querySelector(`[data-board-${section}]`);
        if (!host) return;
        this.updateSectionCount(editor, section, sessions.length);
        if (!sessions.length) {
            host.innerHTML = `<p class="board-side-empty">${section === 'automations' ? 'No recordings yet.' : 'No sessions linked.'}</p>`;
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
                    <i class="fa-solid ${session.isAutomation ? 'fa-robot' : session.active ? 'fa-terminal' : 'fa-clock-rotate-left'} board-side-icon${session.isAutomation && session.active ? ' board-automation-running' : ''}" aria-hidden="true"></i>
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
            label: card?.key ? cardLabel(card, card.title || session.displayName) : session.displayName,
            title: cardLabel(card, card?.title || session.displayName),
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
        // Every rail mutation that reloads the card (comment, commit, session) changed what an
        // agent would read; the Agent context section re-measures rather than going stale.
        if (editor._boardCard) Object.assign(editor._boardCard, {
            comments: card.comments, notes: card.notes, sessions: card.sessions, commits: card.commits, attachments: card.attachments
        });
        editor._boardContext?.refresh();
        return card;
    }

    readCardForm(editor) {
        const value = selector => editor.querySelector(selector)?.value ?? '';
        return {
            ...(this.cardIdFromEditor(editor) ? { displayId: value('#board-card-display-id').trim() || cardDisplayId(editor._boardCard) } : { linkedCardIds: (editor._boardCard?.linkedCards || []).map(card => card.id) }),
            title: value('#board-card-title').trim(),
            description: value('[data-board-composer="description"] [data-board-composer-input]'),
            columnId: value('#board-card-lane'),
            type: value('#board-card-type'),
            assignee: value('#board-card-assignee'),
            baseLlmOptions: readBoardLaunchOptions(editor, value('#board-card-assignee')),
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

    cardChanges(editor, payload) {
        const baseline = editor._boardFormBaseline;
        if (!baseline) return payload;
        return Object.fromEntries(Object.entries(payload)
            .filter(([field, value]) => JSON.stringify(value) !== JSON.stringify(baseline[field])));
    }

    async saveCard(editor) {
        if (editor._boardSaving || editor._boardUploading || editor._boardStarting || editor._boardOrganizing) return;
        const payload = this.readCardForm(editor);
        if (!this.validateCardTitle(editor, payload)) return;
        editor._boardSaving = true;
        let saved = null;
        try {
            const cardId = this.cardIdFromEditor(editor);
            if (cardId) {
                saved = await BoardApi.updateBoardCardAsync(cardId, this.cardChanges(editor, payload));
                editor._boardFormBaseline = payload;
                this.app.showToast('Board', 'Card saved.', 'success');
            } else {
                saved = await BoardApi.createBoardCardAsync(payload);
                editor.dataset.cardId = saved.id;
                if (editor._boardCard) Object.assign(editor._boardCard, saved);
                this.app.showToast('Board', `Created ${cardDisplayId(saved)}.`, 'success');
            }
            const pending = editor._boardCard?.pendingAttachments || [];
            while (pending.length) {
                const attachment = await BoardApi.addCardAttachmentAsync(saved.id, pending[0]);
                const uploaded = pending.shift();
                const description = editor.querySelector('[data-board-composer="description"] [data-board-composer-input]');
                if (uploaded.id && description.value.includes(`attachment:${uploaded.id}`)) {
                    description.value = description.value.replaceAll(`attachment:${uploaded.id}`, `attachment:${attachment.id}`);
                    await BoardApi.updateBoardCardAsync(saved.id, { description: description.value });
                    editor._boardFormBaseline.description = description.value;
                }
                editor._boardCard.attachments ||= [];
                editor._boardCard.attachments.push(attachment);
                this.renderAttachmentsPanel(editor, editor._boardCard);
            }
            const references = editor._boardCard?.pendingReferences || [];
            while (references.length) {
                const item = references[0];
                if (item.kind === 'session') await BoardApi.addCardSessionAsync(saved.id, { id: item.session.id, displayName: item.session.displayName || item.session.sessionDisplayName });
                else await BoardApi.addCardCommitAsync(saved.id, { sha: item.commit.sha });
                references.shift();
            }
            if (editor.isConnected !== false) this.app.closeModal();
            await this.refresh();
        } catch (error) {
            // The card itself may already be saved and only a queued upload failed — saying the
            // card failed to save sends the user looking for a card that exists. Keep the saved
            // id and remaining upload queue so Save can retry the unfinished uploads.
            this.app.showToast('Board', saved
                ? `${cardDisplayId(saved)} was saved, but an attachment or reference did not finish. ${error?.message || ''}`.trim()
                : error?.message || 'Failed to save the card.', saved ? 'warning' : 'error');
        } finally {
            editor._boardSaving = false;
        }
    }

    // "Start work": the server opens a terminal tab for the assignee with the card
    // prepended to that environment's Initial Message, links the session to the
    // card, and hands back the tab id. Unsaved edits in the editor are saved first
    // so the LLM reads what is on screen.
    // The working agent is a live linked session that is not an Automation's. A lane Automation
    // (and the CLI it spawned) stays on the card it came from, but once the launched agent's tab
    // is gone the card is back to Start work, never "Go to agent" into the review terminal.
    isWorkingSession(session) {
        return Boolean(session?.active && !session.isAutomation && !['chat', 'code_review'].includes(session.origin));
    }

    hasRunningSession(card) {
        return Boolean(card?.activeSessionId || card?.sessions?.some(session => this.isWorkingSession(session)));
    }

    updateStartWorkButton(editor, card) {
        const chat = editor.querySelector('[data-board-chat]');
        if (chat) {
            chat.disabled = Boolean(editor._boardStarting);
            chat.title = 'Open a terminal to discuss this card with the selected LLM';
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
        let session = card.sessions?.find(item => this.isWorkingSession(item) && item.tabId);
        if (!session && card.activeTabId) {
            session = { id: card.activeSessionId, tabId: card.activeTabId, selection: card.assignee, displayName: card.title };
        }
        if (!session) {
            const fresh = await BoardApi.getBoardCardAsync(card.id);
            Object.assign(card, fresh);
            this.renderSessionsPanel(editor, card);
            session = card.sessions?.find(item => this.isWorkingSession(item) && item.tabId);
            if (!session && card.activeTabId)
                session = { id: card.activeSessionId, tabId: card.activeTabId, selection: card.assignee, displayName: card.title };
        }
        if (session) await this.focusSessionTab(card, session);
        else this.app.showToast('Board', 'The agent terminal is no longer available. You can replay its session from Sessions.', 'info');
    }

    async startWork(editor, card, intent = 'work') {
        if (!card?.id) return;
        if (editor._boardSaving || editor._boardUploading || editor._boardStarting || editor._boardOrganizing) return;
        if (intent === 'work' && this.hasRunningSession(card)) {
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
            const saved = await BoardApi.updateBoardCardAsync(card.id, this.cardChanges(editor, payload));
            editor._boardFormBaseline = payload;
            if (intent === 'work' && this.hasRunningSession(saved)) {
                Object.assign(card, saved);
                editor._boardStarting = false;
                this.updateStartWorkButton(editor, saved);
                if (intent === 'work') await this.goToAgent(editor, card);
                return;
            }
            const question = intent === 'chat' ? editor.querySelector('[data-board-chat-question]')?.value?.trim() : undefined;
            const result = await BoardApi.launchBoardCardAsync(card.id, { selection, intent, ...(question ? { question } : {}) });
            const tabId = String(result?.tabId || '').trim();
            if (!tabId) throw new Error('The launch did not return a terminal tab.');

            const info = this.assigneeInfo(result.selection || selection);
            this.app.terminalController?.rememberTabLaunch?.(tabId, {
                selection: result.selection || selection,
                label: cardLabel({ ...card, ...saved }, payload.title || card.title),
                title: cardLabel({ ...card, ...saved }, payload.title || card.title),
                taskKey: intent === 'chat' ? `${CARD_TASK_KEY(card.id)}:chat` : CARD_TASK_KEY(card.id),
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
            this.app.showToast('Board', `${cardDisplayId({ ...card, ...saved })} started with ${info?.label || 'the LLM'}. Open it from Sessions when ready.`, 'success');
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

    // ============================================
    // Board editor (new / rename / delete)
    // ============================================

    openBoardEditor(boardId) {
        const board = boardId ? this.boardById(boardId) : null;
        this.app.showModal(board ? `Board · ${board.name}` : 'New board', `
            <div class="board-lane-editor" data-board-board-editor>
                ${board ? boardSettingsNavigation() : ''}
                <section id="board-settings-general" data-board-settings-panel="general" ${board ? 'role="tabpanel" aria-labelledby="board-settings-tab-general"' : ''}>
                <label class="board-editor-label" for="board-board-name">Board name</label>
                <input type="text" class="form-control form-control-sm mb-3" id="board-board-name" maxlength="60"
                    placeholder="Sprint 12, Website, Q4 bugs…" value="${escapeHtml(board?.name || '')}">
                <label class="board-editor-label" for="board-display-prefix">Card display ID prefix</label>
                <input class="form-control form-control-sm mb-2" id="board-display-prefix" maxlength="8" placeholder="Defaults to the repository name" value="${escapeHtml(board?.displayPrefix || '')}">
                <p class="board-editor-muted">New cards use this prefix followed by a number. Existing card IDs stay as they are. Clear it to go back to the repository default.</p>
                <p class="board-editor-muted mb-3">${board
                    ? 'Create a new card to work on another board. Cards can move between lanes on this board.'
                    : 'A new board starts with the default lanes. Card keys stay unique across the whole project.'}</p>
                ${board ? boardSyncSection() : '<p class="board-editor-muted">New boards stay local until you enable viberails.ai sync in Board settings.</p>'}
                <div class="board-editor-actions mt-4">
                    ${board ? `<button type="button" class="btn btn-sm btn-outline-danger" data-board-delete-board>
                        <i class="fa-solid fa-trash" aria-hidden="true"></i> Delete board
                    </button>` : '<span></span>'}
                    <button type="button" class="btn btn-sm btn-outline-primary" data-board-save-board>${board ? 'Save board' : 'Create'}</button>
                </div>
                </section>
                ${board ? `
                    <section id="board-settings-jira" role="tabpanel" aria-labelledby="board-settings-tab-jira" data-board-settings-panel="jira" hidden>${boardJiraSection()}</section>
                    <section id="board-settings-context" role="tabpanel" aria-labelledby="board-settings-tab-context" data-board-settings-panel="context" hidden>${boardContextSection()}</section>
                    <section id="board-settings-history" role="tabpanel" aria-labelledby="board-settings-tab-history" data-board-settings-panel="history" hidden>${historySection()}</section>
                ` : '<p class="board-editor-muted">Save the board to configure Jira and agent context.</p>'}
            </div>
        `, { onClose: () => { this.boardSettingsDispose?.(); this.boardSettingsDispose = null; } });

        const container = document.getElementById('modal-container');
        const editor = container?.querySelector('[data-board-board-editor]');
        if (!editor) return;
        if (board) {
            const disposeContext = mountBoardContext(this.app, editor.querySelector('[data-board-context]'), board.id);
            const disposeHistory = mountHistory(editor.querySelector('[data-board-history-view]'), board.id);
            const jira = new BoardJiraPanel(this.app, editor.querySelector('[data-jira-panel]'), board, (action, destination) => {
                if (this.state.boardId !== board.id && this.state.boardId !== destination) return;
                if (destination && destination !== this.state.boardId) {
                    this.state.boardId = destination;
                    this.persistBoardSelection();
                }
                // Keep the modal and its other sections' unsaved drafts. Only the Jira panel
                // follows the connection; General and Agent context still edit the opened board.
                if (action === 'unlink' && destination === board.id) reloadSync();
                if (action === 'pull' || action === 'unlink' || destination !== board.id) void this.refresh().then(() => {
                    if (editor.isConnected && destination) {
                        const label = editor.querySelector('[data-jira-board]');
                        if (label) label.textContent = this.boardById(destination)?.name || 'Jira board';
                    }
                });
            });
            const jiraHeading = editor.querySelector('[data-jira-heading]');
            const header = container.querySelector('.modal-header');
            header.classList.add('board-settings-header');
            header.insertBefore(jiraHeading, header.querySelector('[data-action="close-modal"]'));
            jiraHeading.hidden = true;
            const navigation = mountBoardSettingsNavigation(editor, (tab, jiraVisible) => {
                jiraHeading.hidden = !jiraVisible;
                if (jiraVisible) void jira.activate();
                if (tab === 'history') editor.querySelector('[data-board-history-view]').open = true;
            });
            let disposeSync = () => {};
            const reloadSync = () => {
                disposeSync();
                disposeSync = mountBoardSync(this.app, editor.querySelector('[data-board-sync]'), board.id, status => {
                    const title = header.querySelector('.modal-title');
                    if (status.isJiraBoard) {
                        title.innerHTML = `Board · <span class="d-inline-block">${boardBrandLogo(true)} ${escapeHtml(board.name)}</span>`;
                        navigation.combineJira();
                    } else {
                        title.textContent = `Board · ${board.name}`;
                        navigation.separateJira();
                    }
                });
            };
            reloadSync();
            this.boardSettingsDispose = () => { navigation.dispose(); disposeSync(); jira.dispose(); disposeContext(); disposeHistory(); };
        }
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
        // The field shows the effective prefix, so only a change is sent and an untouched default
        // stays a default. An emptied field is sent as '' to return the board to that default.
        const typedPrefix = editor.querySelector('#board-display-prefix')?.value.trim() ?? '';
        const displayPrefix = !board ? typedPrefix || undefined
            : typedPrefix.toUpperCase() === String(board.displayPrefix || '').toUpperCase() ? undefined
            : typedPrefix;
        try {
            if (board) {
                // Unchanged fields are omitted, so this save cannot undo a rename made elsewhere meanwhile.
                await BoardApi.updateBoardAsync(board.id, { name: name === board.name ? undefined : name, displayPrefix });
                this.app.showToast('Board', 'Board saved.', 'success');
            } else {
                const created = await BoardApi.createBoardAsync({ name, displayPrefix });
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
        if (column) this.boardSettingsDispose = mountLaneAutomation(this.app, editor.querySelector('[data-lane-automation]'), column.id,
            settings => this.laneAgents.updateColumnCount(column.id, settings));

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
