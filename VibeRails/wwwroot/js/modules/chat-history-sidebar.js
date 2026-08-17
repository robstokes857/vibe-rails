import {
    buildLlmSelectionOptions,
    parseLlmSelection,
    formatRelativeTime,
    escapeHtml
} from './utils.js';
import * as SessionDebug from './session-viewer.js';
import { showSendDebugLogModal } from './debug-bundle.js';

const DEFAULT_PAGE_SIZE = 20;
const SCROLL_LOAD_THRESHOLD_PX = 48;
const CONTEXT_MENU_VIEWPORT_MARGIN_PX = 8;
const HISTORY_OPEN_STORAGE_KEY = 'viberails_history_sidebar_open';
const HISTORY_SEEN_STORAGE_KEY = 'viberails_history_sidebar_seen';
// Standard dashed GUID (8-4-4-4-12) or "N" format (32 hex chars), case-insensitive.
const SESSION_ID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$|^[0-9a-f]{32}$/i;
// Keep inline filter values to CSS filter syntax. Brand metadata is currently hardcoded,
// but validating at the HTML boundary prevents future metadata sources from injecting CSS.
const SAFE_CSS_FILTER = /^[a-z0-9\s().,%#-]+$/i;

export class ChatHistorySidebar {
    constructor(app) {
        this.app = app;
        this.allItems = [];
        this.filterText = '';
        this.llmFilters = new Set();
        this.activeItem = null;
        this.pageSize = DEFAULT_PAGE_SIZE;
        this.currentPage = 0;
        this.hasMore = true;
        this.isLoadingPage = false;
        this.isLoadingForSearch = false;
        this.loadFailed = false;
        this.currentDirOnly = false;
        this.sidebar = null;
        this.search = null;
        this.body = null;
        this.contextMenu = null;
        this.refreshButton = null;
        this.llmFilterContainer = null;
        this.currentDirToggle = null;
        this._documentClickHandler = null;
        this._documentKeydownHandler = null;
        this._bodyResizeObserver = null;
        this._sidebarMutationObserver = null;
    }

    static renderHtml() {
        return `
            <div class="ch-sidebar" id="ch-sidebar">
                <span class="ch-sidebar-rail-label" aria-hidden="true">History</span>
                <div class="ch-sidebar-header">
                    <span class="ch-sidebar-title">
                        <svg xmlns="http://www.w3.org/2000/svg" width="13" height="13" fill="currentColor" viewBox="0 0 16 16" style="opacity:0.7">
                            <path d="M8 3.5a.5.5 0 0 0-1 0V9a.5.5 0 0 0 .252.434l3.5 2a.5.5 0 0 0 .496-.868L8 8.71V3.5z"/>
                            <path d="M8 16A8 8 0 1 0 8 0a8 8 0 0 0 0 16m7-8A7 7 0 1 1 1 8a7 7 0 0 1 14 0"/>
                        </svg>
                        Chat History
                    </span>
                    <div class="ch-sidebar-actions">
                        <button class="ch-sidebar-refresh-btn" id="ch-sidebar-refresh-btn" title="Refresh history" aria-label="Refresh history">
                            <i class="fa-solid fa-arrows-rotate"></i>
                        </button>
                        <button class="ch-sidebar-close-btn" id="ch-sidebar-close-btn" title="Close" aria-label="Close chat history">
                            <i class="fa-solid fa-xmark"></i>
                        </button>
                    </div>
                </div>
                <div class="ch-sidebar-main">
                    <div class="ch-sidebar-search">
                        <div class="ch-search-input-wrapper">
                            <i class="fa-solid fa-magnifying-glass ch-search-icon"></i>
                            <input type="text" class="ch-search-input" id="ch-search-input" placeholder="search sessions, ids, projects" autocomplete="off" spellcheck="false">
                            <kbd class="ch-search-kbd" aria-hidden="true">/</kbd>
                        </div>
                        <div class="ch-sidebar-controls">
                            <button class="ch-filter-drawer-toggle" id="ch-filter-drawer-toggle" type="button" aria-expanded="false">
                                <i class="fa-solid fa-filter ch-filter-icon"></i>
                                <span class="ch-filter-drawer-label">Filters</span>
                                <i class="fa-solid fa-chevron-down ch-filter-drawer-chevron"></i>
                            </button>
                            <div class="ch-filter-drawer-content" id="ch-filter-drawer-content">
                                <button class="ch-current-dir-toggle" id="ch-current-dir-toggle" type="button" aria-pressed="false" title="Only show chats started from the current working directory">
                                    <i class="fa-solid fa-folder-open"></i>
                                    <span class="ch-current-dir-toggle-label">This folder</span>
                                </button>
                                <div class="ch-llm-filter-group" id="ch-llm-filter-group"></div>
                            </div>
                        </div>
                    </div>
                    <div class="ch-sidebar-body" id="ch-sidebar-body"></div>
                </div>
                
                <!-- Floating Context Menu -->
                <div class="ch-context-menu" id="ch-context-menu">
                  <div class="ch-context-menu-item" data-action="jump-to-parent"><i class="fa-solid fa-turn-up me-1"></i>Jump to parent</div>
                   <div class="ch-context-menu-divider"></div>
                    <div class="ch-context-menu-item ch-has-submenu" id="ch-menu-send-to">
                        <span>Send to...</span>
                        <i class="fa-solid fa-chevron-right ms-auto" style="font-size: 0.6rem; opacity: 0.5;"></i>
                        <div class="ch-context-submenu" id="ch-send-to-submenu">
                            <!-- Populated dynamically -->
                        </div>
                    </div>
                    <div class="ch-context-menu-divider"></div>
                    <div class="ch-context-menu-item" data-action="rename">Rename</div>
                    <div class="ch-context-menu-item text-danger" data-action="delete">Delete</div>
                    <div class="ch-context-menu-divider"></div>
                    <div class="ch-context-menu-item" data-action="get-session">Replay Session</div>
                    <div class="ch-context-menu-item" data-action="get-raw-session">Session Data Dump</div>
                    <div class="ch-context-menu-divider"></div>
                    <div class="ch-context-menu-item" data-action="send-debug-log"><i class="fa-solid fa-bug me-1"></i>Send debug log</div>
                </div>
            </div>`;
    }

    mount(root, { onToggle } = {}) {
        this.destroy();
        const sidebar = root.querySelector('#ch-sidebar');
        const search = root.querySelector('.ch-sidebar-search');
        const body = root.querySelector('#ch-sidebar-body');
        const contextMenu = root.querySelector('#ch-context-menu');
        this.sidebar = sidebar;
        this.search = search;
        this.body = body;
        this.contextMenu = contextMenu;
        this.refreshButton = root.querySelector('#ch-sidebar-refresh-btn');
        this.closeButton = root.querySelector('#ch-sidebar-close-btn');
        this.llmFilterContainer = root.querySelector('#ch-llm-filter-group');
        this.currentDirToggle = root.querySelector('#ch-current-dir-toggle');
        this.filterDrawerToggle = root.querySelector('#ch-filter-drawer-toggle');
        this.filterDrawerContent = root.querySelector('#ch-filter-drawer-content');
        this.filterDrawerToggle?.addEventListener('click', () => {
            const isOpen = this.filterDrawerContent.classList.toggle('is-open');
            this.filterDrawerToggle.setAttribute('aria-expanded', String(isOpen));
        });
        const syncCloseButtonState = () => {
            if (!sidebar || !this.closeButton) {
                return;
            }

            const isOpen = !sidebar.classList.contains('ch-sidebar-collapsed');
            const icon = this.closeButton.querySelector('i');
            const label = isOpen ? 'Collapse chat history' : 'Expand chat history';

            this.closeButton.setAttribute('title', label);
            this.closeButton.setAttribute('aria-label', label);
            this.closeButton.setAttribute('aria-expanded', String(isOpen));

            if (icon) {
                // Expanded: a collapse chevron. Collapsed: the history clock —
                // the button is then the rail's identity AND its open control
                // (it replaced the old separate non-focusable collapsed-icon div).
                icon.className = isOpen ? 'fa-solid fa-chevron-left' : 'fa-solid fa-clock-rotate-left';
            }
        };
        // First-time visitors: cue the peek icon with a one-time pulse so they
        // discover the sidebar even though we default it to collapsed.
        const isFirstVisit = (() => {
            try { return localStorage.getItem(HISTORY_SEEN_STORAGE_KEY) !== '1'; } catch { return true; }
        })();
        if (isFirstVisit) {
            sidebar?.querySelector('.ch-sidebar-close-btn')?.classList.add('ch-sidebar-pulse-hint');
        }

        const persistToggle = (open) => {
            try {
                localStorage.setItem(HISTORY_OPEN_STORAGE_KEY, open ? '1' : '0');
                localStorage.setItem(HISTORY_SEEN_STORAGE_KEY, '1');
            } catch {}
            sidebar?.querySelector('.ch-sidebar-close-btn')?.classList.remove('ch-sidebar-pulse-hint');
            onToggle?.(open);
        };

        // Toggle button stays visible in both states.
        this.closeButton?.addEventListener('click', (e) => {
            e.stopPropagation();
            const willOpen = sidebar?.classList.contains('ch-sidebar-collapsed') ?? false;
            persistToggle(willOpen);
        });

        // Clicking the collapsed sidebar peek strip re-opens it
        sidebar?.addEventListener('click', (e) => {
            if (!sidebar.classList.contains('ch-sidebar-collapsed')) return;
            // Only respond to clicks on the sidebar itself or its header when collapsed
            const clickedInteractive = e.target.closest('button, input, a, .ch-item');
            if (clickedInteractive) return;
            persistToggle(true);
        });

        this.refreshButton?.addEventListener('click', async (e) => {
            e.stopPropagation();
            if (this.isLoadingPage || this.isLoadingForSearch) {
                return;
            }

            this._closeContextMenu();
            await this._load();
        });

        contextMenu?.querySelector('[data-action="jump-to-parent"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeContextMenu();
            if (this.activeItem) void this._jumpToParent(this.activeItem);
        });

        contextMenu?.querySelector('[data-action="rename"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            void this._showRenameModal();
        });

        contextMenu?.querySelector('[data-action="delete"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            void this._showDeleteModal();
        });

        contextMenu?.querySelector('[data-action="get-session"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeContextMenu();
            if (this.activeItem) void SessionDebug.showReplayModal(this.activeItem.id);
        });
        contextMenu?.querySelector('[data-action="get-raw-session"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeContextMenu();
            if (this.activeItem) {
                void SessionDebug.downloadRawSession(this.activeItem.id).catch(error => {
                    const message = error?.message || 'Unknown error';
                    this.app.showError(`Failed to download raw session: ${message}`);
                });
            }
        });
        contextMenu?.querySelector('[data-action="send-debug-log"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeContextMenu();
            if (this.activeItem) showSendDebugLogModal(this.app, this.activeItem.id);
        });
        const searchInput = root.querySelector('#ch-search-input');
        searchInput?.addEventListener('input', (e) => {
            this.filterText = e.target.value.toLowerCase().trim();
            this._renderItems();

            // If the user typed/pasted a full session id, look it up directly
            // (faster than scanning every page) and merge the result so the
            // existing filter pipeline highlights it.
            if (this._looksLikeSessionId(this.filterText)
                && !this.allItems.some(item => (item.id || '').toLowerCase() === this.filterText)) {
                void this._fetchSessionByIdIntoList(this.filterText);
                return;
            }

            if (this.filterText && this.hasMore) {
                void this._loadRemainingPagesForFilters();
            }
        });

        this.llmFilterContainer?.addEventListener('click', (e) => {
            const badge = e.target.closest('[data-llm-filter]');
            if (!badge || badge.disabled) {
                return;
            }

            e.stopPropagation();
            const nextFilter = (badge.dataset.llmFilter || 'all').trim().toLowerCase();
            if (!nextFilter) {
                return;
            }

            if (nextFilter === 'reset') {
                if (this.llmFilters.size === 0) {
                    return;
                }
                this.llmFilters.clear();
            } else if (this.llmFilters.has(nextFilter)) {
                this.llmFilters.delete(nextFilter);
            } else {
                this.llmFilters.add(nextFilter);
            }

            this._syncLlmFilterControls();
            this._syncFilterCountBadge();
            this._closeContextMenu();
            this._renderItems();

            if (this._hasActiveFilters() && this.hasMore) {
                void this._loadRemainingPagesForFilters();
            }
        });

        this.currentDirToggle?.addEventListener('click', (e) => {
            if (this.currentDirToggle.disabled) {
                return;
            }

            e.stopPropagation();
            this.currentDirOnly = !this.currentDirOnly;
            this._syncCurrentDirToggle();
            this._closeContextMenu();
            this._renderItems();

            if (this.currentDirOnly && this.hasMore && this._shouldLoadNextPage()) {
                void this._loadNextPage();
            }
        });

        body?.addEventListener('scroll', () => {
            if (!this._shouldLoadNextPage()) {
                return;
            }

            void this._loadNextPage();
        }, { passive: true });

        // When the sidebar expands (or the window/screen changes), the body
        // dimensions change.  Items that filled the collapsed peek strip may
        // leave empty space at full width — trigger a load check.
        if (body) {
            this._bodyResizeObserver = new ResizeObserver(() => {
                if (this._shouldLoadNextPage()) {
                    void this._loadNextPage();
                }
            });
            this._bodyResizeObserver.observe(body);
        }

        // Close menu on click outside
        this._documentClickHandler = (e) => {
            const isMenuBtn = e.target.closest('.ch-item-menu-btn');
            const isMenu = e.target.closest('.ch-context-menu');
            const isMenuItem = e.target.closest('.ch-context-menu-item') && !e.target.closest('.ch-has-submenu');

            if (!isMenuBtn && (!isMenu || isMenuItem)) {
                this._closeContextMenu();
            }
        };
        document.addEventListener('click', this._documentClickHandler);

        // "/" focuses the search input when the sidebar is open and the user
        // is not already typing somewhere else.
        this._documentKeydownHandler = (e) => {
            if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return;
            if (!sidebar || sidebar.classList.contains('ch-sidebar-collapsed')) return;
            const active = document.activeElement;
            if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.isContentEditable)) {
                return;
            }
            if (!searchInput || !searchInput.isConnected) return;
            e.preventDefault();
            searchInput.focus();
            searchInput.select?.();
        };
        document.addEventListener('keydown', this._documentKeydownHandler);

        if (sidebar && this.closeButton && typeof MutationObserver === 'function') {
            this._sidebarMutationObserver = new MutationObserver(() => syncCloseButtonState());
            this._sidebarMutationObserver.observe(sidebar, { attributes: true, attributeFilter: ['class'] });
        }

        syncCloseButtonState();
        this._syncLlmFilterControls();
        this._syncCurrentDirToggle();

        // Initial state: first-time visitors get a collapsed sidebar (so the
        // terminal gets full width); returning users get whatever they last
        // had open. We propagate via onToggle without writing to storage —
        // only actual user toggles persist.
        let initialOpen = false;
        if (!isFirstVisit) {
            try { initialOpen = localStorage.getItem(HISTORY_OPEN_STORAGE_KEY) === '1'; } catch {}
        }
        onToggle?.(initialOpen);
        void this._load();
    }

    destroy() {
        if (this._documentClickHandler) {
            document.removeEventListener('click', this._documentClickHandler);
            this._documentClickHandler = null;
        }
        if (this._documentKeydownHandler) {
            document.removeEventListener('keydown', this._documentKeydownHandler);
            this._documentKeydownHandler = null;
        }
        this._bodyResizeObserver?.disconnect();
        this._bodyResizeObserver = null;
        this._sidebarMutationObserver?.disconnect();
        this._sidebarMutationObserver = null;
        this.sidebar = null;
        this.search = null;
        this.body = null;
        this.contextMenu = null;
        this.refreshButton = null;
        this.closeButton = null;
        this.llmFilterContainer = null;
        this.currentDirToggle = null;
        this.filterDrawerToggle = null;
        this.filterDrawerContent = null;
    }

    async _load() {
        if (!this.body) {
            return;
        }

        this.activeItem = null;
        this.allItems = [];
        this.currentPage = 0;
        this.hasMore = true;
        this.loadFailed = false;
        this._closeContextMenu();
        this._setRefreshButtonState();
        this.body.innerHTML = '<div class="ch-loading"><span class="vb-spinner vb-spinner-lg" role="status" aria-label="Loading chat history"></span></div>';
        await this._loadNextPage({ initial: true });
    }

    async _loadNextPage({ initial = false } = {}) {
        if (this.isLoadingPage || !this.hasMore) {
            return false;
        }

        this.isLoadingPage = true;
        this._setRefreshButtonState();
        if (!initial) {
            this._renderItems();
        }

        try {
            let didLoadPage = false;

            while (this.hasMore) {
                // If the sidebar has been torn down (view switched away),
                // stop walking pages mid-loop.
                if (!this.body?.isConnected) {
                    return didLoadPage;
                }

                const nextPage = this.currentPage + 1;
                const params = new URLSearchParams({
                    page: String(nextPage),
                    pageSize: String(this.pageSize)
                });
                const data = await this.app.apiCall(`/api/v1/chatHistory?${params.toString()}`, 'GET', null, { showLoading: false });
                const fetchedItems = Array.isArray(data?.items) ? data.items : [];

                if (fetchedItems.length === 0) {
                    this.hasMore = false;
                    return didLoadPage;
                }

                didLoadPage = true;
                this.currentPage = nextPage;

                const visibleItems = fetchedItems.filter(item => this._shouldDisplayItem(item));
                if (visibleItems.length > 0) {
                    this._mergeItems(visibleItems);
                    break;
                }
            }

            this.loadFailed = false;
            return didLoadPage;
        } catch (error) {
            this.loadFailed = true;
            this.hasMore = false;

            if (!initial && this.allItems.length > 0) {
                const message = error?.message || 'Unknown error';
                this.app.showError(`Failed to load more chat history: ${message}`);
            }

            return false;
        } finally {
            this.isLoadingPage = false;
            this._setRefreshButtonState();
            this._renderItems();

            if (this._hasActiveFilters() && this.hasMore && !this.isLoadingForSearch) {
                void this._loadRemainingPagesForFilters();
            }

            // If visible items don't fill the container, keep loading
            if (this._shouldLoadNextPage()) {
                void this._loadNextPage();
            }
        }
    }

    async _loadRemainingPagesForFilters() {
        if (this.isLoadingForSearch || !this._hasActiveFilters() || !this.hasMore) {
            return;
        }

        this.isLoadingForSearch = true;
        this._setRefreshButtonState();
        this._renderItems();

        try {
            while (this.filterText && this.hasMore) {
                const didLoad = await this._loadNextPage();
                if (!didLoad) {
                    break;
                }
            }
        } finally {
            this.isLoadingForSearch = false;
            this._setRefreshButtonState();
            this._renderItems();
        }
    }

    _shouldLoadNextPage() {
        if (!this.body || this.filterText || this.isLoadingPage || !this.hasMore) {
            return false;
        }

        // Detached from the DOM (view switched away). scrollHeight/clientHeight
        // both read 0, which otherwise looks like "container not full" and
        // triggers unbounded auto-pagination.
        if (!this.body.isConnected) {
            return false;
        }

        // If content doesn't fill the container, load more to fill it
        if (this.body.scrollHeight <= this.body.clientHeight) {
            return true;
        }

        return this.body.scrollTop + this.body.clientHeight >= this.body.scrollHeight - SCROLL_LOAD_THRESHOLD_PX;
    }

    _getLlmOptions() {
        return buildLlmSelectionOptions(this.app.data.environments || []);
    }

    _closeContextMenu() {
        if (!this.contextMenu?.classList.contains('show')) {
            return;
        }

        this.contextMenu.classList.remove('show');
        this.sidebar?.classList.remove('ch-sidebar-menu-open');
        this.sidebar?.querySelectorAll('.ch-item-menu-active').forEach(el => el.classList.remove('ch-item-menu-active'));
    }

    _setRefreshButtonState() {
        const isBusy = this.isLoadingPage || this.isLoadingForSearch;
        if (this.refreshButton) {
            this.refreshButton.disabled = isBusy;
            this.refreshButton.classList.toggle('is-loading', isBusy);
        }
        if (this.llmFilterContainer) {
            this.llmFilterContainer.querySelectorAll('[data-llm-filter]').forEach((button) => {
                button.disabled = isBusy;
            });
        }
        if (this.currentDirToggle) {
            const hasPreferredDir = Boolean((this._getPreferredWorkingDirectory() || '').trim());
            this.currentDirToggle.disabled = isBusy || !hasPreferredDir;
        }
    }

    _syncLlmFilterControls() {
        if (!this.llmFilterContainer) {
            return;
        }

        const options = this._getLlmFilterOptions();
        this._syncFilterCountBadge();

        this.llmFilterContainer.innerHTML = options.map((option) => {
            const isReset = option.value === 'reset';
            const isActive = isReset ? false : this.llmFilters.has(option.value);
            const isDisabled = isReset && this.llmFilters.size === 0;
            return `
                <button
                    type="button"
                    class="ch-llm-filter-badge${isActive ? ' is-active' : ''}${isReset ? ' is-reset' : ''}"
                    data-llm-filter="${escapeHtml(option.value)}"
                    aria-pressed="${isActive ? 'true' : 'false'}"
                    title="${escapeHtml(option.label)}"
                    ${isDisabled ? 'disabled' : ''}
                >
                    ${option.logoHtml}
                    <span>${escapeHtml(option.label)}</span>
                </button>
            `;
        }).join('');
    }

    _syncFilterCountBadge() {
        if (!this.filterDrawerToggle) {
            return;
        }

        let badge = this.filterDrawerToggle.querySelector('.ch-filter-count-badge');
        const totalActive = this.llmFilters.size;
        if (totalActive > 0) {
            if (!badge) {
                badge = document.createElement('span');
                badge.className = 'ch-filter-count-badge';
                this.filterDrawerToggle.appendChild(badge);
            }
            badge.textContent = totalActive;
        } else if (badge) {
            badge.remove();
        }
    }

    _syncCurrentDirToggle() {
        if (!this.currentDirToggle) {
            return;
        }

        const preferredDir = (this._getPreferredWorkingDirectory() || '').trim();
        const hasPreferredDir = Boolean(preferredDir);

        // Can't filter to "current" if we don't know what current is.
        if (!hasPreferredDir && this.currentDirOnly) {
            this.currentDirOnly = false;
        }

        this.currentDirToggle.classList.toggle('is-active', this.currentDirOnly);
        this.currentDirToggle.setAttribute('aria-pressed', this.currentDirOnly ? 'true' : 'false');
        this.currentDirToggle.disabled = !hasPreferredDir || this.isLoadingPage || this.isLoadingForSearch;

        const tooltip = hasPreferredDir
            ? `Only show chats from ${preferredDir}`
            : 'Current directory unknown';
        this.currentDirToggle.setAttribute('title', tooltip);
    }

    _getRawDisplayName(item) {
        const sessionName = item.sessionDisplayName?.trim();
        if (sessionName) {
            return sessionName;
        }

        const inputName = item.inputText?.trim().split('\n')[0]?.trim();
        return inputName || '';
    }

    _getDisplayName(item) {
        const rawName = this._getRawDisplayName(item);
        if (!rawName) {
            return rawName;
        }

        const stripped = this._stripResumePrefix(rawName, item.parentCli, item.cli);
        return stripped || rawName;
    }

    _shouldDisplayItem(item) {
        const displayName = this._getDisplayName(item);
        return displayName.length > 0 && displayName.toLowerCase() !== 'untitled';
    }

    async _showRenameModal() {
        if (!this.activeItem) {
            return;
        }

        const sessionId = this.activeItem.id;
        const currentName = this._getDisplayName(this.activeItem);
        this._closeContextMenu();

        this.app.showModal('Rename Chat', `
            <form id="chat-history-rename-form">
                <div class="mb-3">
                    <label class="form-label">Chat Name</label>
                    <input type="text" class="form-control" id="chat-history-rename-input" value="${escapeHtml(currentName)}" maxlength="200" required>
                    <small class="form-text text-muted">Choose a friendlier label for this chat history entry.</small>
                </div>
                <div class="d-flex gap-2 justify-content-end">
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                    <button type="submit" class="btn btn-primary">Save Name</button>
                </div>
            </form>
        `);

        document.getElementById('chat-history-rename-form')?.addEventListener('submit', async (e) => {
            e.preventDefault();
            const input = document.getElementById('chat-history-rename-input');
            const nextName = input?.value?.trim() || '';

            if (!nextName) {
                this.app.showError('Chat name is required.');
                return;
            }

            try {
                await this.app.apiCall(`/api/v1/chatHistory/${encodeURIComponent(sessionId)}`, 'PATCH', {
                    sessionDisplayName: nextName
                });
                this.app.showToast('Chat Renamed', `Updated chat name to "${nextName}"`, 'success');
                this.app.closeModal();
                await this._load();
            } catch (error) {
                this.app.showError(`Failed to rename chat: ${error.message}`);
            }
        });
    }

    async _showDeleteModal() {
        if (!this.activeItem) {
            return;
        }

        const sessionId = this.activeItem.id;
        const currentName = this._getDisplayName(this.activeItem);
        this._closeContextMenu();

        this.app.showModal('Delete Chat', `
            <div class="text-center py-3">
                <div class="mb-3 text-danger">
                    <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M11 1.5v1h3.5a.5.5 0 0 1 0 1h-.538l-.853 10.66A2 2 0 0 1 11.115 16h-6.23a2 2 0 0 1-1.994-1.84L2.038 3.5H1.5a.5.5 0 0 1 0-1H5v-1A1.5 1.5 0 0 1 6.5 0h3A1.5 1.5 0 0 1 11 1.5m-5 0v1h4v-1a.5.5 0 0 0-.5-.5h-3a.5.5 0 0 0-.5.5M4.5 5.029l.5 8.5a.5.5 0 1 0 .998-.06l-.5-8.5a.5.5 0 1 0-.998.06m6.53-.06a.5.5 0 0 0-.515.479l-.5 8.5a.5.5 0 1 0 .998.06l.5-8.5a.5.5 0 0 0-.484-.539M8 5.5a.5.5 0 0 0-.5.5v8.5a.5.5 0 0 0 1 0V6a.5.5 0 0 0-.5-.5"/>
                    </svg>
                </div>
                <h5>Delete "${escapeHtml(currentName)}"?</h5>
                <p class="text-muted small px-4">This removes the chat history entry and its recorded session data. This action cannot be undone.</p>
            </div>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-danger" id="confirm-delete-chat-history-btn">Delete Chat</button>
            </div>
        `);

        document.getElementById('confirm-delete-chat-history-btn')?.addEventListener('click', async () => {
            try {
                await this.app.apiCall(`/api/v1/chatHistory/${encodeURIComponent(sessionId)}`, 'DELETE');
                this.app.showToast('Chat Deleted', `Deleted "${currentName}"`, 'info');
                this.app.closeModal();
                await this._load();
            } catch (error) {
                this.app.showError(`Failed to delete chat: ${error.message}`);
            }
        });
    }

    _openContextMenu(itemEl, anchorEl, submenu) {
        if (!this.sidebar || !this.contextMenu) {
            return;
        }

        this.sidebar.querySelectorAll('.ch-item-menu-active').forEach(el => el.classList.remove('ch-item-menu-active'));

        this.activeItem = this.allItems.find(i => i.id === itemEl.dataset.id) || null;

        this.sidebar.classList.add('ch-sidebar-menu-open');

        itemEl.classList.add('ch-item-menu-active');

        const jumpToParentItem = this.contextMenu.querySelector('[data-action="jump-to-parent"]');
        if (jumpToParentItem) {
            const show = !!this.activeItem?.parentSessionId;
            jumpToParentItem.style.display = show ? '' : 'none';
            const dividerAfter = jumpToParentItem.nextElementSibling;
            if (dividerAfter?.classList.contains('ch-context-menu-divider')) {
                dividerAfter.style.display = show ? '' : 'none';
            }
        }

        this._populateLlmSubmenu(submenu);
        this._positionContextMenu(itemEl, anchorEl);
    }

    _positionContextMenu(itemEl, anchorEl) {
        if (!this.sidebar || !this.contextMenu) {
            return;
        }

        const itemRect = itemEl.getBoundingClientRect();
        const anchorRect = anchorEl.getBoundingClientRect();
        const sidebarRect = this.sidebar.getBoundingClientRect();
        const margin = CONTEXT_MENU_VIEWPORT_MARGIN_PX;

        this.contextMenu.style.visibility = 'hidden';
        this.contextMenu.style.top = '0px';
        this.contextMenu.style.left = `${anchorRect.right - sidebarRect.left + 6}px`;
        this.contextMenu.classList.add('show');

        const menuHeight = this.contextMenu.offsetHeight;
        const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
        const visibleTop = Math.max(sidebarRect.top, margin);
        const visibleBottom = Math.min(viewportHeight, sidebarRect.bottom) - margin;
        const spaceBelow = visibleBottom - itemRect.top;
        const spaceAbove = itemRect.bottom - visibleTop;
        const opensUp = menuHeight > spaceBelow && spaceAbove > spaceBelow;
        const preferredTop = opensUp
            ? itemRect.bottom - sidebarRect.top - menuHeight
            : itemRect.top - sidebarRect.top;
        const minTop = visibleTop - sidebarRect.top;
        const maxTop = Math.max(minTop, visibleBottom - sidebarRect.top - menuHeight);
        const top = Math.min(Math.max(preferredTop, minTop), maxTop);

        this.contextMenu.style.top = `${top}px`;
        this.contextMenu.style.visibility = '';
    }

    _renderItems() {
        if (!this.body) {
            return;
        }

        this._syncCurrentDirToggle();

        if (this.loadFailed && this.allItems.length === 0) {
            this.body.innerHTML = `
                <div class="ch-empty">
                    <span class="ch-empty-glyph"><i class="fa-solid fa-triangle-exclamation"></i></span>
                    <span class="ch-empty-title">Couldn't load history</span>
                    <span class="ch-empty-hint">Hit refresh to try again. If it keeps failing, check the VibeRails logs.</span>
                </div>`;
            return;
        }

        const filteredItems = this._getFilteredItems();

        if (!filteredItems.length) {
            if (this.filterText && (this.isLoadingPage || this.isLoadingForSearch)) {
                this.body.innerHTML = `
                    <div class="ch-loading ch-loading-inline">
                        <span class="vb-spinner vb-spinner-sm" role="status" aria-label="Searching"></span>
                        <span class="ch-loading-label">Searching older sessions</span>
                    </div>
                `;
                return;
            }

            const filtered = this.filterText || this.llmFilters.size > 0 || this.currentDirOnly;
            const title = filtered ? 'No matches' : 'No chats yet';
            const hint = filtered
                ? 'Try a different search or clear your filters to widen the net.'
                : 'New CLI sessions show up here as soon as they start.';
            this.body.innerHTML = `
                <div class="ch-empty">
                    <span class="ch-empty-glyph"><i class="fa-solid fa-clock-rotate-left"></i></span>
                    <span class="ch-empty-title">${escapeHtml(title)}</span>
                    <span class="ch-empty-hint">${escapeHtml(hint)}</span>
                </div>`;
            return;
        }

        this.body.innerHTML = `${filteredItems.map(item => {
            const brand = this.app.getCliBrand(item.cli);
            const rawName = this._getDisplayName(item);
            const name = rawName.length > 80 ? rawName.slice(0, 80) + '…' : rawName;
            const time = formatRelativeTime(item.startedUTC);
            const isActive = !item.endedUTC;
            const logoHtml = this._renderBrandLogo(brand, 'ch-item-logo');
            const projectDisplayName = this._getProjectDisplayName(item);
            const accent = brand.accentColor || 'var(--color-accent, #06b6d4)';
            const itemStyle = ` style="--ch-item-accent: ${escapeHtml(accent)}"`;
            const metaLines = [];
            if (item.environmentName?.trim()) {
                metaLines.push(`<div class="ch-meta-row"><span class="ch-meta-label">Env</span> ${escapeHtml(item.environmentName.trim())}</div>`);
            }
            if (isActive) {
                metaLines.push(`<div class="ch-meta-row"><span class="ch-live-dot">Live</span> <span class="ch-meta-divider">·</span> <span>${escapeHtml(time)}</span></div>`);
            } else {
                metaLines.push(`<div class="ch-meta-row"><span class="ch-meta-label">Ended</span> ${escapeHtml(time)}</div>`);
            }
            const metaHtml = metaLines.join('');
            const relationshipHtml = item.parentSessionId
                ? this._renderResumeRelationship(item, brand)
                : '';
            const parentJumpButton = item.parentSessionId
                ? `<button class="ch-item-parent-btn" type="button" title="Jump to parent chat" aria-label="Jump to parent chat">
                        <i class="fa-solid fa-turn-up"></i>
                   </button>`
                : '';
            return `
                <div class="ch-item${isActive ? ' ch-item-active' : ''}" data-id="${escapeHtml(item.id)}"${itemStyle}>
                    <div class="ch-item-icon">${logoHtml}</div>
                    <div class="ch-item-content">
                        <div class="ch-item-header">
                            <div class="ch-item-name" title="${escapeHtml(rawName)}">${escapeHtml(name)}</div>
                            <span class="ch-project-badge" title="${escapeHtml(item.workingDirectory || '')}"><i class="fa-solid fa-folder-open"></i> ${escapeHtml(projectDisplayName)}</span>
                        </div>
                        ${relationshipHtml}
                        <div class="ch-item-meta">${metaHtml}</div>
                    </div>
                    <div class="ch-item-actions">
                        ${parentJumpButton}
                        <button class="ch-item-menu-btn" title="Actions" aria-label="Actions">
                            <i class="fa-solid fa-ellipsis-vertical"></i>
                        </button>
                    </div>
                </div>`;
        }).join('')}${this._renderFooter()}`;

        // Bind menu buttons
        const itemElements = this.body.querySelectorAll('.ch-item');
        if (!this.contextMenu) {
            return;
        }
        const submenu = this.contextMenu.querySelector('#ch-send-to-submenu');
        if (!submenu) {
            return;
        }

        itemElements.forEach(itemEl => {
            itemEl.querySelector('.ch-item-parent-btn')?.addEventListener('click', (e) => {
                e.stopPropagation();
                const item = this.allItems.find(i => i.id === itemEl.dataset.id);
                if (item) {
                    void this._jumpToParent(item);
                }
            });
            itemEl.querySelector('.ch-item-menu-btn')?.addEventListener('click', (e) => {
                e.stopPropagation();
                this._openContextMenu(itemEl, e.currentTarget, submenu);
            });
        });
    }

    _renderFooter() {
        if (!this.isLoadingPage && !this.isLoadingForSearch) {
            return '';
        }

        const label = this.filterText
            || this.llmFilters.size > 0
            || this.currentDirOnly
            ? 'Searching older sessions'
            : 'Loading more sessions';

        return `
            <div class="ch-loading ch-loading-inline">
                <span class="vb-spinner vb-spinner-sm" role="status" aria-label="${escapeHtml(label)}"></span>
                <span class="ch-loading-label">${escapeHtml(label)}</span>
            </div>
        `;
    }

    async _showResumeModal(parsedLlm, llmDisplayLabel) {
        if (!this.activeItem) return;

        const CHAR_LIMIT = 6000;
        const sessionId = this.activeItem.id;
        const chatName = this._getDisplayName(this.activeItem);
        const activeItemSnapshot = this.activeItem;
        this._closeContextMenu();

        this.app.showModal(`Sending to ${llmDisplayLabel}`, `
            <div class="text-muted small mb-3">${escapeHtml(sessionId)}</div>
            <div id="ch-resume-body">
                <div class="d-flex flex-column gap-2 py-2 text-muted">
                    <div class="d-flex align-items-center gap-2">
                        <span class="vb-spinner vb-spinner-sm flex-shrink-0" role="status" aria-label="Generating summary"></span>
                        <span>Generating summary&hellip;</span>
                    </div>
                    <div class="small" style="padding-left:1.75rem;">This might take a moment.</div>
                </div>
            </div>
            <div class="d-flex justify-content-between align-items-center mt-3" id="ch-resume-actions" style="display:none !important;">
                <button type="button" class="btn btn-outline-secondary btn-sm" id="ch-resume-regenerate-btn" disabled>
                    <i class="fa-solid fa-rotate-right me-1"></i>Regenerate
                </button>
                <div class="d-flex gap-2">
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                    <button type="button" class="btn btn-primary" id="ch-resume-launch-btn" disabled>
                        <i class="fa-solid fa-terminal me-1"></i>Launch Terminal
                    </button>
                </div>
            </div>
        `);

        const showSpinner = (container) => {
            container.innerHTML = `
                <div class="d-flex flex-column gap-2 py-2 text-muted">
                    <div class="d-flex align-items-center gap-2">
                        <span class="vb-spinner vb-spinner-sm flex-shrink-0" role="status" aria-label="Generating summary"></span>
                        <span>Generating summary&hellip;</span>
                    </div>
                    <div class="small" style="padding-left:1.75rem;">This might take a moment.</div>
                </div>`;
        };

        const loadSummary = async (regenerate) => {
            const resumeBody = document.getElementById('ch-resume-body');
            const actionsBar = document.getElementById('ch-resume-actions');
            const regenerateBtn = document.getElementById('ch-resume-regenerate-btn');
            const launchBtn = document.getElementById('ch-resume-launch-btn');
            if (!resumeBody) return;

            showSpinner(resumeBody);
            if (actionsBar) actionsBar.style.cssText = 'display:none !important;';
            if (regenerateBtn) regenerateBtn.disabled = true;
            if (launchBtn) launchBtn.disabled = true;

            try {
                const url = `/api/v1/chatHistory/${encodeURIComponent(sessionId)}/summary${regenerate ? '?regenerate=true' : ''}`;
                const result = await this.app.apiCall(url, 'GET', null, { showLoading: false });

                const summary = result?.summary ?? '';
                const transcript = this._stripAnsi(result?.transcript ?? '');

                resumeBody.innerHTML = `
                    <div class="mb-2">
                        <label class="form-label fw-semibold small text-muted text-uppercase mb-1" style="letter-spacing:.05em;">Summary</label>
                        <textarea class="form-control" id="ch-resume-summary" rows="8"
                            style="font-size:0.85rem;resize:vertical;">${escapeHtml(summary)}</textarea>
                        <div class="d-flex justify-content-end mt-1">
                            <small id="ch-resume-char-count" class="${summary.length > CHAR_LIMIT ? 'text-danger' : 'text-muted'}">${summary.length} / ${CHAR_LIMIT}</small>
                        </div>
                    </div>
                    <details class="mb-2">
                        <summary class="fw-semibold small text-muted text-uppercase" style="letter-spacing:.05em;cursor:pointer;">
                            Raw Transcript
                        </summary>
                        <textarea class="form-control mt-2" id="ch-resume-transcript" rows="6"
                            style="font-size:0.8rem;resize:vertical;">${escapeHtml(transcript || '(empty)')}</textarea>
                    </details>`;

                // Wire character counter + validation
                const summaryTextarea = document.getElementById('ch-resume-summary');
                const charCount = document.getElementById('ch-resume-char-count');
                const updateCharCount = () => {
                    const len = summaryTextarea.value.length;
                    charCount.textContent = `${len} / ${CHAR_LIMIT}`;
                    const overLimit = len > CHAR_LIMIT;
                    charCount.classList.toggle('text-danger', overLimit);
                    charCount.classList.toggle('text-muted', !overLimit);
                    if (launchBtn) launchBtn.disabled = len === 0 || overLimit;
                };
                summaryTextarea?.addEventListener('input', updateCharCount);

                if (actionsBar) actionsBar.style.cssText = '';
                if (regenerateBtn) regenerateBtn.disabled = false;
                if (launchBtn) launchBtn.disabled = summary.length === 0 || summary.length > CHAR_LIMIT;
            } catch (error) {
                resumeBody.innerHTML = `<div class="text-danger small">Failed to generate summary: ${escapeHtml(error.message)}</div>`;
                if (actionsBar) actionsBar.style.cssText = '';
                if (regenerateBtn) regenerateBtn.disabled = false;
            }
        };

        // Wire buttons
        document.getElementById('ch-resume-regenerate-btn')?.addEventListener('click', () => loadSummary(true));
        document.getElementById('ch-resume-launch-btn')?.addEventListener('click', () => {
            const summaryText = document.getElementById('ch-resume-summary')?.value?.trim() || '';
            if (!summaryText || summaryText.length > CHAR_LIMIT) return;

            this.app.closeModal();

            const manager = this.app.terminalController.manager;
            if (!manager || manager.isDestroyed()) return;

            manager.startWithOptions({
                cli: parsedLlm.cli,
                environmentName: parsedLlm.environmentName || null,
                workingDirectory: activeItemSnapshot.workingDirectory || null,
                resumeSummary: summaryText,
                resumeSessionId: sessionId,
                title: this._getDisplayName(activeItemSnapshot),
                forceNewTab: true
            });
        });

        await loadSummary(false);
    }

    _stripAnsi(text) {
        if (!text) return text;
        // Strip ANSI escape sequences and other common control chars
        return text
            .replace(/\x1b\[[0-9;]*[A-Za-z]/g, '')
            .replace(/\x1b[()][A-Z0-9]/g, '')
            .replace(/\x1b[^[\]]/g, '')
            .replace(/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/g, '');
    }

    _populateLlmSubmenu(submenu) {
        const options = this._getLlmOptions();
        const groups = {};
        options.forEach(opt => {
            if (!groups[opt.group]) groups[opt.group] = [];
            groups[opt.group].push(opt);
        });

        submenu.innerHTML = Object.keys(groups).map(groupName => `
            <div class="ch-submenu-group-label">${escapeHtml(groupName)}</div>
            ${groups[groupName].map(opt => `
                <div class="ch-context-menu-item" data-value="${escapeHtml(opt.value)}">
                    ${escapeHtml(opt.label)}
                </div>
            `).join('')}
        `).join('<div class="ch-context-menu-divider"></div>');

        // Bind submenu item clicks
        submenu.querySelectorAll('.ch-context-menu-item').forEach(item => {
            item.addEventListener('click', (e) => {
                e.stopPropagation();
                const value = item.dataset.value;
                if (!this.activeItem || !value) return;

                const parsed = parseLlmSelection(value, this.app.data.environments || []);
                if (!parsed.cli) return;

                const label = item.textContent.trim();
                this._showResumeModal(parsed, label);
            });
        });
    }

    _getPreferredWorkingDirectory() {
        return this.app.data?.configs?.rootPath || this.app.data?.configs?.launchDirectory || '';
    }

    _normalizeDirectoryKey(path) {
        // Git (rev-parse --show-toplevel) returns forward slashes on Windows,
        // but Repository.NormalizeWorkingDirectory stores sessions using
        // Path.GetFullPath which yields backslashes. Fold both to a common
        // form so the "This folder" filter actually matches.
        if (!path) {
            return '';
        }
        return path
            .trim()
            .replace(/\\/g, '/')
            .replace(/\/+$/, '')
            .toLowerCase();
    }

    _hasActiveFilters() {
        return Boolean(this.filterText)
            || this.llmFilters.size > 0
            || this.currentDirOnly;
    }

    _looksLikeSessionId(text) {
        return SESSION_ID_REGEX.test((text || '').trim());
    }

    async _fetchSessionByIdIntoList(sessionId) {
        if (!sessionId) {
            return;
        }

        // Show the searching spinner while we hit the backend.
        this.isLoadingForSearch = true;
        this._setRefreshButtonState();
        this._renderItems();

        try {
            const fetched = await this.app.apiCall(
                `/api/v1/chatHistory/${encodeURIComponent(sessionId)}`,
                'GET',
                null,
                { showLoading: false }
            );
            if (fetched) {
                this._mergeItems([fetched]);
            }
        } catch (error) {
            // 404 just means no such session — leave the empty state alone.
            if (!/not\s*found/i.test(error?.message || '')) {
                this.app.showError(`Failed to look up session: ${error.message}`);
            }
        } finally {
            this.isLoadingForSearch = false;
            this._setRefreshButtonState();
            this._renderItems();
        }
    }

    _getProjectDisplayName(item) {
        const projectDisplayName = item?.projectDisplayName?.trim();
        if (projectDisplayName) {
            return projectDisplayName;
        }

        return this.app.getProjectNameFromPath(item?.workingDirectory || '');
    }

    _getItemWorkingDirectoryKey(item) {
        return (item?.workingDirectory || '').trim();
    }

    _stripResumePrefix(name, parentCli, childCli) {
        const rawName = (name || '').trim();
        if (!rawName) {
            return rawName;
        }

        const escapePattern = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        if (parentCli && childCli) {
            const exactPattern = new RegExp(`^Chat from\\s+${escapePattern(parentCli)}\\s*->\\s*${escapePattern(childCli)}\\s+`, 'i');
            if (exactPattern.test(rawName)) {
                return rawName.replace(exactPattern, '').trim();
            }
        }

        return rawName.replace(/^Chat from\s+.+?\s*->\s*.+?\s+/i, '').trim();
    }

    _isShellBrand(brand) {
        if (!brand) {
            return false;
        }
        // The shell brand from getCliBrand carries this className. Legacy sessions
        // recorded before the shell brand existed fall through to the default
        // brand, where label is the raw cli string ("Shell"/"shell"/"terminal").
        const label = (brand.label || '').toLowerCase();
        return brand.className === 'badge-cli-shell'
            || label === 'shell'
            || label === 'terminal';
    }

    _renderBrandLogo(brand, className) {
        const logoClass = className || 'ch-item-logo';
        // Plain shell / terminal sessions: render a crisp terminal glyph tinted
        // with the brand accent instead of the flat white-filtered SVG (or the
        // bare "S" letter fallback for pre-shell-brand sessions).
        if (this._isShellBrand(brand)) {
            return `<span class="${logoClass} ch-brand-terminal-glyph"><i class="fa-solid fa-terminal" aria-hidden="true"></i></span>`;
        }
        const filterValue = typeof brand.logoFilter === 'string' && SAFE_CSS_FILTER.test(brand.logoFilter)
            ? brand.logoFilter
            : '';
        const logoStyle = filterValue ? ` style="filter: ${filterValue}"` : '';
        return brand.logo
            ? `<img src="${escapeHtml(brand.logo)}" alt="${escapeHtml(brand.label)}" class="${logoClass}"${logoStyle}>`
            : `<span class="${logoClass} ch-item-logo-fallback">${escapeHtml((brand.label || '?')[0])}</span>`;
    }

    _renderResumeRelationship(item, brand) {
        const parentBrand = this.app.getCliBrand(item.parentCli || '');
        const resumeTitle = item.parentCli
            ? `Resumed from ${parentBrand.label} into ${brand.label}`
            : `Resumed into ${brand.label}`;
        return `
            <div class="ch-item-relationship" title="${escapeHtml(resumeTitle)}">
                <span class="ch-item-relationship-flow">
                    ${this._renderBrandLogo(parentBrand, 'ch-item-inline-logo')}
                    <i class="fa-solid fa-arrow-right"></i>
                    ${this._renderBrandLogo(brand, 'ch-item-inline-logo')}
                </span>
                <span class="ch-item-relationship-label">Resume</span>
            </div>
        `;
    }

    _getFilteredItems() {
        const preferredDir = this.currentDirOnly
            ? this._normalizeDirectoryKey(this._getPreferredWorkingDirectory())
            : '';

        return this.allItems.filter(item => {
            if (this.llmFilters.size > 0 && !this.llmFilters.has((item?.cli || '').toLowerCase())) {
                return false;
            }

            if (this.currentDirOnly && preferredDir) {
                const itemDir = this._normalizeDirectoryKey(this._getItemWorkingDirectoryKey(item));
                if (itemDir !== preferredDir) {
                    return false;
                }
            }

            if (!this.filterText) {
                return true;
            }

            const searchParts = [
                item.id || '',
                this._getDisplayName(item),
                this._getProjectDisplayName(item),
                item.environmentName || '',
                this.app.getCliBrand(item.cli)?.label || '',
                this.app.getCliBrand(item.parentCli || '')?.label || ''
            ];

            return searchParts.join('\n').toLowerCase().includes(this.filterText);
        });
    }

    _mergeItems(items) {
        const merged = new Map(this.allItems.map(item => [item.id, item]));
        items.forEach((item) => {
            if (!item?.id) {
                return;
            }

            merged.set(item.id, {
                ...(merged.get(item.id) || {}),
                ...item
            });
        });

        this.allItems = Array.from(merged.values());
        this._sortItemsInMemory();
    }

    _sortItemsInMemory() {
        const getTimestamp = (item) => {
            const parsed = Date.parse(item?.startedUTC || '');
            return Number.isFinite(parsed) ? parsed : 0;
        };

        this.allItems.sort((left, right) => {
            const recencyCompare = getTimestamp(right) - getTimestamp(left);
            if (recencyCompare !== 0) {
                return recencyCompare;
            }

            const leftDir = (left.workingDirectory || '').toLowerCase();
            const rightDir = (right.workingDirectory || '').toLowerCase();
            if (leftDir !== rightDir) {
                return leftDir.localeCompare(rightDir, undefined, { sensitivity: 'base' });
            }

            return (right.id || '').localeCompare(left.id || '', undefined, { sensitivity: 'base' });
        });
    }

    _getLlmFilterOptions() {
        return [
            { value: 'reset', label: 'Reset', logoHtml: '<i class="fa-solid fa-rotate-left"></i>' },
            ...['claude', 'codex', 'opencode', 'glm-5.2', 'glm-5.3', 'grok-4.6', 'antigravity', 'copilot'].map((cli) => {
                const brand = this.app.getCliBrand(cli);
                return {
                    value: cli,
                    label: brand.label,
                    logoHtml: this._renderBrandLogo(brand, 'ch-llm-filter-logo')
                };
            })
        ];
    }

    async _jumpToParent(item) {
        const parentSessionId = item?.parentSessionId?.trim();
        if (!parentSessionId || !this.body) {
            return;
        }

        let parentItem = this.allItems.find(entry => entry.id === parentSessionId) || null;
        if (!parentItem) {
            try {
                const fetched = await this.app.apiCall(`/api/v1/chatHistory/${encodeURIComponent(parentSessionId)}`, 'GET', null, { showLoading: false });
                if (fetched) {
                    this._mergeItems([fetched]);
                    this._renderItems();
                    parentItem = this.allItems.find(entry => entry.id === parentSessionId) || fetched;
                }
            } catch (error) {
                this.app.showError(`Failed to load parent chat: ${error.message}`);
                return;
            }
        }

        const target = this.body.querySelector(`.ch-item[data-id="${CSS.escape(parentSessionId)}"]`);
        if (!target) {
            if (this.filterText) {
                this.app.showToast('Parent Loaded', 'Parent chat is hidden by the current search filter.', 'info');
            } else {
                this.app.showError('Parent chat is not visible in the current list.');
            }
            return;
        }

        target.scrollIntoView({ behavior: 'smooth', block: 'center' });
        target.classList.remove('ch-item-flash');
        requestAnimationFrame(() => {
            target.classList.add('ch-item-flash');
            window.setTimeout(() => target.classList.remove('ch-item-flash'), 3500);
        });
    }
}
