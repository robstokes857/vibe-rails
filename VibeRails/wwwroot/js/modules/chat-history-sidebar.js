import {
    buildLlmSelectionOptions,
    parseLlmSelection,
    formatRelativeTime,
    escapeHtml
} from './utils.js';
import * as SessionDebug from './session-viewer.js';

const DEFAULT_PAGE_SIZE = 20;
const SCROLL_LOAD_THRESHOLD_PX = 48;

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
        this.sidebar = null;
        this.search = null;
        this.body = null;
        this.contextMenu = null;
        this.refreshButton = null;
        this.llmFilterContainer = null;
    }

    static renderHtml() {
        return `
            <div class="ch-sidebar" id="ch-sidebar">
                <div class="ch-sidebar-collapsed-icon" title="Open chat history"><i class="fa-solid fa-clock-rotate-left"></i></div>
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
                            <input type="text" class="ch-search-input" id="ch-search-input" placeholder="Search sessions..." autocomplete="off">
                        </div>
                        <div class="ch-sidebar-controls">
                            <label class="ch-filter-label">LLM</label>
                            <div class="ch-llm-filter-group" id="ch-llm-filter-group"></div>
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
                    <div class="ch-context-menu-item" data-action="get-transcript">Get Transcript</div>
                    <div class="ch-context-menu-item" data-action="get-session">Get Session</div>
                </div>
            </div>`;
    }

    mount(root, { onToggle } = {}) {
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
                icon.className = isOpen
                    ? 'fa-solid fa-chevron-left'
                    : 'fa-solid fa-chevron-right';
            }
        };
        const emitToggleState = () => onToggle?.(!sidebar?.classList.contains('ch-sidebar-collapsed'));

        // Toggle button stays visible in both states.
        this.closeButton?.addEventListener('click', (e) => {
            e.stopPropagation();
            const willOpen = sidebar?.classList.contains('ch-sidebar-collapsed') ?? false;
            onToggle?.(willOpen);
        });

        // Clicking the collapsed sidebar peek strip re-opens it
        sidebar?.addEventListener('click', (e) => {
            if (!sidebar.classList.contains('ch-sidebar-collapsed')) return;
            // Only respond to clicks on the sidebar itself or its header when collapsed
            const clickedInteractive = e.target.closest('button, input, a, .ch-item');
            if (clickedInteractive) return;
            onToggle?.(true);
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

        contextMenu?.querySelector('[data-action="get-transcript"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeContextMenu();
            if (this.activeItem) void SessionDebug.showTranscriptModal(this.activeItem.id);
        });
        contextMenu?.querySelector('[data-action="get-session"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeContextMenu();
            if (this.activeItem) void SessionDebug.showReplayModal(this.activeItem.id);
        });

        const searchInput = root.querySelector('#ch-search-input');
        searchInput?.addEventListener('input', (e) => {
            this.filterText = e.target.value.toLowerCase().trim();
            this._renderItems();

            if (this.filterText && this.hasMore) {
                void this._loadRemainingPagesForSearch();
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
            this._closeContextMenu();
            this._renderItems();
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
            new ResizeObserver(() => {
                if (this._shouldLoadNextPage()) {
                    void this._loadNextPage();
                }
            }).observe(body);
        }

        // Close menu on click outside
        document.addEventListener('click', (e) => {
            const isMenuBtn = e.target.closest('.ch-item-menu-btn');
            const isMenu = e.target.closest('.ch-context-menu');
            const isMenuItem = e.target.closest('.ch-context-menu-item') && !e.target.closest('.ch-has-submenu');

            if (!isMenuBtn && (!isMenu || isMenuItem)) {
                this._closeContextMenu();
            }
        });

        if (sidebar && this.closeButton && typeof MutationObserver === 'function') {
            new MutationObserver(() => syncCloseButtonState())
                .observe(sidebar, { attributes: true, attributeFilter: ['class'] });
        }

        syncCloseButtonState();
        this._syncLlmFilterControls();
        emitToggleState();
        void this._load();
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
        this.body.innerHTML = '<div class="ch-loading"><div class="spinner-border spinner-border-sm text-primary" role="status"></div></div>';
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
                const nextPage = this.currentPage + 1;
                const params = new URLSearchParams({
                    page: String(nextPage),
                    pageSize: String(this.pageSize)
                });
                const preferredWorkingDirectory = this._getPreferredWorkingDirectory();
                if (preferredWorkingDirectory) {
                    params.set('preferredWorkingDirectory', preferredWorkingDirectory);
                }
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

            if (this.filterText && this.hasMore && !this.isLoadingForSearch) {
                void this._loadRemainingPagesForSearch();
            }

            // If visible items don't fill the container, keep loading
            if (this._shouldLoadNextPage()) {
                void this._loadNextPage();
            }
        }
    }

    async _loadRemainingPagesForSearch() {
        if (this.isLoadingForSearch || !this.filterText || !this.hasMore) {
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
    }

    _syncLlmFilterControls() {
        if (!this.llmFilterContainer) {
            return;
        }

        const options = this._getLlmFilterOptions();
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
        if (!item?.parentSessionId || !rawName) {
            return rawName;
        }

        return this._stripResumePrefix(rawName, item.parentCli, item.cli);
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

        const itemRect = itemEl.getBoundingClientRect();
        const anchorRect = anchorEl.getBoundingClientRect();
        const sidebarRect = this.sidebar.getBoundingClientRect();

        this.contextMenu.style.top = `${itemRect.top - sidebarRect.top}px`;
        this.contextMenu.style.left = `${anchorRect.right - sidebarRect.left + 6}px`;
        this.sidebar.classList.add('ch-sidebar-menu-open');
        this.contextMenu.classList.add('show');

        itemEl.classList.add('ch-item-menu-active');

        const jumpToParentItem = this.contextMenu.querySelector('[data-action="jump-to-parent"]');
        if (jumpToParentItem) {
            jumpToParentItem.style.display = this.activeItem?.parentSessionId ? '' : 'none';
        }

        this._populateLlmSubmenu(submenu);
    }

    _renderItems() {
        if (!this.body) {
            return;
        }

        if (this.loadFailed && this.allItems.length === 0) {
            this.body.innerHTML = '<div class="ch-empty">Failed to load history.</div>';
            return;
        }

        const filteredItems = this._getFilteredItems();

        if (!filteredItems.length) {
            if (this.filterText && (this.isLoadingPage || this.isLoadingForSearch)) {
                this.body.innerHTML = `
                    <div class="ch-loading ch-loading-inline">
                        <div class="spinner-border spinner-border-sm text-primary" role="status"></div>
                        <span class="ch-loading-label">Searching older sessions...</span>
                    </div>
                `;
                return;
            }

            this.body.innerHTML = `<div class="ch-empty">${(this.filterText || this.llmFilters.size > 0) ? 'No matches found.' : 'No chat history yet.'}</div>`;
            return;
        }

        this.body.innerHTML = `${filteredItems.map(item => {
            const brand = this.app.getCliBrand(item.cli);
            const rawName = this._getDisplayName(item);
            const name = rawName.length > 52 ? rawName.slice(0, 52) + '…' : rawName;
            const time = formatRelativeTime(item.startedUTC);
            const isActive = !item.endedUTC;
            const logoHtml = this._renderBrandLogo(brand, 'ch-item-logo');
            const projectDisplayName = this._getProjectDisplayName(item);
            const metaParts = [
                `<span class="ch-meta-label">Project:</span> ${escapeHtml(projectDisplayName)}`,
                `<span class="ch-meta-label">CLI:</span> ${escapeHtml(brand.label)}`
            ];
            if (item.environmentName?.trim()) {
                metaParts.push(`<span class="ch-meta-label">Env:</span> ${escapeHtml(item.environmentName.trim())}`);
            }
            const metaHtml = isActive
                ? `${metaParts.join(' <span class="ch-meta-separator">·</span> ')} <span class="ch-meta-separator">·</span> <span class="ch-item-live">live</span>`
                : `${metaParts.join(' <span class="ch-meta-separator">·</span> ')} <span class="ch-meta-separator">·</span> ${escapeHtml(time)}`;
            const relationshipHtml = item.parentSessionId
                ? this._renderResumeRelationship(item, brand)
                : '';
            const parentJumpButton = item.parentSessionId
                ? `<button class="ch-item-parent-btn" type="button" title="Jump to parent chat" aria-label="Jump to parent chat">
                        <i class="fa-solid fa-turn-up"></i>
                   </button>`
                : '';
            return `
                <div class="ch-item${isActive ? ' ch-item-active' : ''}" data-id="${escapeHtml(item.id)}">
                    <div class="ch-item-icon">${logoHtml}</div>
                    <div class="ch-item-content">
                        <div class="ch-item-name" title="${escapeHtml(rawName)}">${escapeHtml(name)}</div>
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
            ? 'Searching older sessions...'
            : 'Loading more sessions...';

        return `
            <div class="ch-loading ch-loading-inline">
                <div class="spinner-border spinner-border-sm text-primary" role="status"></div>
                <span class="ch-loading-label">${label}</span>
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

        this.app.showModal(`Sending to ${escapeHtml(llmDisplayLabel)}`, `
            <div class="text-muted small mb-3">${escapeHtml(sessionId)}</div>
            <div id="ch-resume-body">
                <div class="d-flex flex-column gap-2 py-2 text-muted">
                    <div class="d-flex align-items-center gap-2">
                        <div class="spinner-border spinner-border-sm flex-shrink-0" role="status"></div>
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
                        <div class="spinner-border spinner-border-sm flex-shrink-0" role="status"></div>
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
                const url = `/api/v1/chatHistory/${encodeURIComponent(sessionId)}/Summary${regenerate ? '?regenerate=true' : ''}`;
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

    _getProjectDisplayName(item) {
        const projectDisplayName = item?.projectDisplayName?.trim();
        if (projectDisplayName) {
            return projectDisplayName;
        }

        return this.app.getProjectNameFromPath(item?.workingDirectory || '');
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

    _renderBrandLogo(brand, className) {
        const logoClass = className || 'ch-item-logo';
        const logoStyle = brand.logoFilter ? ` style="filter: ${brand.logoFilter}"` : '';
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
        return this.allItems.filter(item => {
            if (this.llmFilters.size > 0 && !this.llmFilters.has((item?.cli || '').toLowerCase())) {
                return false;
            }

            if (!this.filterText) {
                return true;
            }

            const searchParts = [
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
        const preferredWorkingDirectory = this._getPreferredWorkingDirectory();

        this.allItems.sort((left, right) => {
            if (preferredWorkingDirectory) {
                const leftPreferred = left.workingDirectory === preferredWorkingDirectory ? 0 : 1;
                const rightPreferred = right.workingDirectory === preferredWorkingDirectory ? 0 : 1;
                if (leftPreferred !== rightPreferred) {
                    return leftPreferred - rightPreferred;
                }
            }

            const recencyCompare = new Date(right.startedUTC).getTime() - new Date(left.startedUTC).getTime();
            if (recencyCompare !== 0) {
                return recencyCompare;
            }

            return (right.id || '').localeCompare(left.id || '', undefined, { sensitivity: 'base' });
        });
    }

    _getLlmFilterOptions() {
        return [
            { value: 'reset', label: 'Reset', logoHtml: '<i class="fa-solid fa-rotate-left"></i>' },
            ...['claude', 'codex', 'gemini', 'copilot'].map((cli) => {
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
