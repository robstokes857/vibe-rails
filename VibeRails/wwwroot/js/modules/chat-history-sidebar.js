import {
    buildLlmSelectionOptions,
    buildLlmSelectionValue,
    formatRelativeTime,
    escapeHtml,
    parseLlmSelection,
    populateLlmSelectionSelect
} from './utils.js';

const DEFAULT_PAGE_SIZE = 20;
const SCROLL_LOAD_THRESHOLD_PX = 48;

export class ChatHistorySidebar {
    constructor(app) {
        this.app = app;
        this.allItems = [];
        this.filterText = '';
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
        this.settingsButton = null;
        this.settingsPanel = null;
        this.settingsSelect = null;
        this.settingsCloseButton = null;
        this.isSavingSettings = false;
    }

    static renderHtml() {
        return `
            <div class="ch-sidebar" id="ch-sidebar">
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
                        <button class="ch-sidebar-settings-btn" id="ch-sidebar-settings-btn" title="Chat Settings">
                            <i class="fa-solid fa-gear"></i>
                        </button>
                        <button class="ch-sidebar-hide-btn" id="ch-sidebar-hide-btn" title="Hide history">
                            <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" fill="currentColor" viewBox="0 0 16 16">
                                <path fill-rule="evenodd" d="M11.354 1.646a.5.5 0 0 1 0 .708L5.707 8l5.647 5.646a.5.5 0 0 1-.708.708l-6-6a.5.5 0 0 1 0-.708l6-6a.5.5 0 0 1 .708 0"/>
                            </svg>
                        </button>
                    </div>
                </div>
                <div class="ch-sidebar-main">
                    <div class="ch-sidebar-search">
                        <div class="ch-search-input-wrapper">
                            <i class="fa-solid fa-magnifying-glass ch-search-icon"></i>
                            <input type="text" class="ch-search-input" id="ch-search-input" placeholder="Search sessions..." autocomplete="off">
                        </div>
                    </div>
                    <div class="ch-sidebar-body" id="ch-sidebar-body"></div>
                    <div class="ch-sidebar-settings-panel" id="ch-sidebar-settings-panel" aria-hidden="true">
                        <div class="ch-sidebar-settings-panel-header">
                            <div>
                                <div class="ch-sidebar-settings-panel-title">Chat Settings</div>
                                <div class="ch-sidebar-settings-panel-subtitle">Changes save immediately.</div>
                            </div>
                            <button type="button" class="ch-sidebar-settings-close-btn" id="ch-sidebar-settings-close-btn" aria-label="Close chat settings">
                                <i class="fa-solid fa-xmark"></i>
                            </button>
                        </div>
                        <div class="ch-sidebar-settings-panel-body">
                            <div class="ch-sidebar-settings-card">
                                <label class="ch-sidebar-settings-label" for="ch-processing-llm-select">Select LLM for processing</label>
                                <select class="ch-sidebar-settings-select" id="ch-processing-llm-select"></select>
                                <p class="ch-sidebar-settings-help">This only stores the preferred base CLI for future chat-history processing actions.</p>
                            </div>
                        </div>
                    </div>
                </div>
                
                <!-- Floating Context Menu -->
                <div class="ch-context-menu" id="ch-context-menu">
                  <div class="ch-context-menu-item" data-action="resume">Resume</div>
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
                    <div class="ch-context-menu-item" data-action="summarize">Summarize</div>
                    <div class="ch-context-menu-item text-danger" data-action="delete">Delete</div>
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
        this.settingsButton = root.querySelector('#ch-sidebar-settings-btn');
        this.settingsPanel = root.querySelector('#ch-sidebar-settings-panel');
        this.settingsSelect = root.querySelector('#ch-processing-llm-select');
        this.settingsCloseButton = root.querySelector('#ch-sidebar-settings-close-btn');
        const emitToggleState = () => onToggle?.(!sidebar?.classList.contains('ch-sidebar-collapsed'));

        root.querySelector('#ch-sidebar-hide-btn')?.addEventListener('click', () => {
            this._closeContextMenu();
            this._toggleSettingsPanel(false);
            sidebar?.classList.toggle('ch-sidebar-collapsed');
            emitToggleState();
        });

        this.refreshButton?.addEventListener('click', async (e) => {
            e.stopPropagation();
            if (this.isLoadingPage || this.isLoadingForSearch || this.sidebar?.classList.contains('ch-sidebar-settings-open')) {
                return;
            }

            this._closeContextMenu();
            await this._load();
        });

        this.settingsButton?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._toggleSettingsPanel();
        });
        this.settingsCloseButton?.addEventListener('click', () => this._toggleSettingsPanel(false));

        this._populateSettingsSelect();
        this.settingsSelect?.addEventListener('change', async (e) => {
            const nextValue = e.target?.value || '';
            await this._saveProcessingLlm(nextValue);
        });

        contextMenu?.querySelector('[data-action="rename"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            void this._showRenameModal();
        });

        contextMenu?.querySelector('[data-action="summarize"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            void this._showSummaryModal();
        });

        contextMenu?.querySelector('[data-action="delete"]')?.addEventListener('click', (e) => {
            e.stopPropagation();
            void this._showDeleteModal();
        });

        const searchInput = root.querySelector('#ch-search-input');
        searchInput?.addEventListener('input', (e) => {
            this.filterText = e.target.value.toLowerCase().trim();
            this._renderItems();

            if (this.filterText && this.hasMore) {
                void this._loadRemainingPagesForSearch();
            }
        });

        body?.addEventListener('scroll', () => {
            if (!this._shouldLoadNextPage()) {
                return;
            }

            void this._loadNextPage();
        }, { passive: true });

        // Close menu on click outside
        document.addEventListener('click', (e) => {
            const isMenuBtn = e.target.closest('.ch-item-menu-btn');
            const isMenu = e.target.closest('.ch-context-menu');
            const isMenuItem = e.target.closest('.ch-context-menu-item') && !e.target.closest('.ch-has-submenu');

            if (!isMenuBtn && (!isMenu || isMenuItem)) {
                this._closeContextMenu();
            }
        });

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
                    this.allItems.push(...visibleItems);
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

        if (this.body.scrollHeight <= this.body.clientHeight) {
            return false;
        }

        return this.body.scrollTop + this.body.clientHeight >= this.body.scrollHeight - SCROLL_LOAD_THRESHOLD_PX;
    }

    _getLlmOptions() {
        return buildLlmSelectionOptions(this.app.data.environments || []);
    }

    _getSettingsLlmOptions() {
        return buildLlmSelectionOptions([], {
            includeGroups: false,
            includeDefaultSuffix: false
        });
    }

    _getSavedProcessingLlm() {
        return this.app.appSettings?.chatHistorySettings?.processingLlm || '';
    }

    _getSavedProcessingSelectionValue() {
        const cli = this._getSavedProcessingLlm();
        return cli ? buildLlmSelectionValue(cli) : '';
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
        if (!this.refreshButton) {
            return;
        }

        const isBusy = this.isLoadingPage || this.isLoadingForSearch;
        this.refreshButton.disabled = isBusy;
        this.refreshButton.classList.toggle('is-loading', isBusy);
    }

    _populateSettingsSelect() {
        if (!this.settingsSelect) {
            return;
        }

        const selectedValue = this._getSavedProcessingSelectionValue();
        const options = this._getSettingsLlmOptions();
        populateLlmSelectionSelect(this.settingsSelect, [], {
            placeholder: null,
            selectedValue,
            includeGroups: false,
            includeDefaultSuffix: false
        });

        const unsetOption = document.createElement('option');
        unsetOption.value = '';
        unsetOption.textContent = 'Not set';
        this.settingsSelect.insertBefore(unsetOption, this.settingsSelect.firstChild);

        if (!selectedValue) {
            this.settingsSelect.value = '';
        }

        const validValues = new Set(['', ...options.map((item) => item.value)]);
        if (!validValues.has(this.settingsSelect.value)) {
            this.settingsSelect.value = '';
        }
    }

    _toggleSettingsPanel(forceOpen) {
        if (!this.sidebar || !this.settingsPanel) {
            return;
        }

        if (this.sidebar.classList.contains('ch-sidebar-collapsed')) {
            return;
        }

        const open = typeof forceOpen === 'boolean'
            ? forceOpen
            : !this.sidebar.classList.contains('ch-sidebar-settings-open');

        if (open) {
            this._closeContextMenu();
            this._populateSettingsSelect();
        }

        this.sidebar.classList.toggle('ch-sidebar-settings-open', open);
        this.settingsButton?.classList.toggle('active', open);
        this.settingsPanel.setAttribute('aria-hidden', String(!open));
    }

    async _saveProcessingLlm(selectionValue) {
        if (!this.settingsSelect || this.isSavingSettings) {
            return;
        }

        const parsed = parseLlmSelection(selectionValue, []);
        const nextCli = parsed.kind === 'base' ? (parsed.cli || '') : '';
        const previousCli = this._getSavedProcessingLlm();

        if (nextCli === previousCli) {
            return;
        }

        this.isSavingSettings = true;
        this.settingsPanel?.classList.add('is-saving');
        this.settingsSelect.disabled = true;

        try {
            const savedSettings = await this.app.apiCall('/api/v1/settings', 'POST', {
                remoteAccess: this.app.appSettings?.remoteAccess || false,
                apiKey: '',
                enablePrerelease: this.app.appSettings?.enablePrerelease || false,
                developerOptions: this.app.appSettings?.developerOptions || false,
                chatHistorySettings: {
                    processingLlm: nextCli
                }
            });
            this.app.setAppSettings(savedSettings);
            this._populateSettingsSelect();

            const toastMessage = nextCli
                ? `Processing LLM set to ${nextCli}.`
                : 'Processing LLM cleared.';
            this.app.showToast('Chat History', toastMessage, 'success');
        } catch (error) {
            this._populateSettingsSelect();
            this.app.showError(`Failed to save chat history settings: ${error.message}`);
        } finally {
            this.isSavingSettings = false;
            this.settingsPanel?.classList.remove('is-saving');
            this.settingsSelect.disabled = false;
        }
    }

    _getDisplayName(item) {
        const sessionName = item.sessionDisplayName?.trim();
        if (sessionName) {
            return sessionName;
        }

        const inputName = item.inputText?.trim().split('\n')[0]?.trim();
        return inputName || '';
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

        const filteredItems = this.filterText
            ? this.allItems.filter(item => {
                const rawName = this._getDisplayName(item).toLowerCase();
                const brand = (this.app.getCliBrand(item.cli)?.label || '').toLowerCase();
                return rawName.includes(this.filterText) || brand.includes(this.filterText);
            })
            : this.allItems;

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

            this.body.innerHTML = `<div class="ch-empty">${this.filterText ? 'No matches found.' : 'No chat history yet.'}</div>`;
            return;
        }

        this.body.innerHTML = `${filteredItems.map(item => {
            const brand = this.app.getCliBrand(item.cli);
            const rawName = this._getDisplayName(item);
            const name = rawName.length > 52 ? rawName.slice(0, 52) + '…' : rawName;
            const time = formatRelativeTime(item.startedUTC);
            const isActive = !item.endedUTC;
            const logoHtml = brand.logo
                ? `<img src="${escapeHtml(brand.logo)}" alt="${escapeHtml(brand.label)}" class="ch-item-logo">`
                : `<span class="ch-item-logo-fallback">${escapeHtml((brand.label || '?')[0])}</span>`;
            return `
                <div class="ch-item${isActive ? ' ch-item-active' : ''}" data-id="${escapeHtml(item.id)}">
                    <div class="ch-item-icon">${logoHtml}</div>
                    <div class="ch-item-content">
                        <div class="ch-item-name" title="${escapeHtml(rawName)}">${escapeHtml(name)}</div>
                        <div class="ch-item-meta">${escapeHtml(brand.label)}${isActive ? ' · <span class="ch-item-live">live</span>' : ` · ${time}`}</div>
                    </div>
                    <button class="ch-item-menu-btn" title="Actions">
                        <i class="fa-solid fa-ellipsis-vertical"></i>
                    </button>
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

    async _showSummaryModal() {
        if (!this.activeItem) {
            return;
        }

        const sessionId = this.activeItem.id;
        const chatName = this._getDisplayName(this.activeItem);
        this._closeContextMenu();

        this.app.showModal('Chat Summary', `
            <div class="text-muted small mb-3">${escapeHtml(chatName)}</div>
            <div id="ch-summary-body">
                <div class="d-flex flex-column gap-2 py-2 text-muted">
                    <div class="d-flex align-items-center gap-2">
                        <div class="spinner-border spinner-border-sm flex-shrink-0" role="status"></div>
                        <span>Generating summary…</span>
                    </div>
                    <div class="small" style="padding-left:1.75rem;">This might take a while. You can navigate away and come back when it is done.</div>
                </div>
            </div>
            <div class="d-flex justify-content-end gap-2 mt-3">
                <button type="button" class="btn btn-outline-secondary btn-sm" id="ch-summary-regenerate-btn" disabled>
                    <i class="fa-solid fa-rotate-right me-1"></i>Regenerate
                </button>
                <button type="button" class="btn btn-secondary btn-sm" data-action="close-modal">Close</button>
            </div>
        `);

        const loadSummary = async (regenerate) => {
            const summaryBody = document.getElementById('ch-summary-body');
            const regenerateBtn = document.getElementById('ch-summary-regenerate-btn');
            if (!summaryBody) return;

            summaryBody.innerHTML = `
                <div class="d-flex flex-column gap-2 py-2 text-muted">
                    <div class="d-flex align-items-center gap-2">
                        <div class="spinner-border spinner-border-sm flex-shrink-0" role="status"></div>
                        <span>Generating summary…</span>
                    </div>
                    <div class="small" style="padding-left:1.75rem;">This might take a while. You can navigate away and come back when it is done.</div>
                </div>`;
            if (regenerateBtn) regenerateBtn.disabled = true;

            try {
                const url = `/api/v1/chatHistory/${encodeURIComponent(sessionId)}/Summary${regenerate ? '?regenerate=true' : ''}`;
                const result = await this.app.apiCall(url, 'GET', null, { showLoading: false });

                const summary = result?.summary ?? '';
                const transcript = result?.transcript ?? '';
                const cleanTranscript = this._stripAnsi(transcript);

                if (summaryBody) {
                    summaryBody.innerHTML = `
                        <div class="mb-3">
                            <div class="fw-semibold small text-muted mb-1 text-uppercase" style="letter-spacing:.05em;">Summary</div>
                            <div class="p-3 rounded" style="background:var(--bs-tertiary-bg,#f8f9fa);white-space:pre-wrap;line-height:1.6;">${escapeHtml(summary)}</div>
                        </div>
                        <details>
                            <summary class="fw-semibold small text-muted mb-1 text-uppercase" style="letter-spacing:.05em;cursor:pointer;">Raw Transcript</summary>
                            <div class="mt-2 p-2 rounded" style="background:var(--bs-body-bg,#fff);border:1px solid var(--bs-border-color,#dee2e6);max-height:260px;overflow-y:auto;">
                                <pre class="mb-0 small" style="white-space:pre-wrap;word-break:break-word;">${escapeHtml(cleanTranscript || '(empty)')}</pre>
                            </div>
                        </details>`;
                }
                if (regenerateBtn) regenerateBtn.disabled = false;
                this.app.showToast('Chat Summary', `Summary ready for "${chatName}"`, 'success');
            } catch (error) {
                if (summaryBody) {
                    summaryBody.innerHTML = `<div class="text-danger small">Failed to generate summary: ${escapeHtml(error.message)}</div>`;
                }
                if (regenerateBtn) regenerateBtn.disabled = false;
            }
        };

        document.getElementById('ch-summary-regenerate-btn')?.addEventListener('click', () => loadSummary(true));

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
                console.log(`Send session ${this.activeItem?.id} to ${value}`);
                // Implement actual send logic here
                const menu = submenu.closest('.ch-context-menu');
                menu.classList.remove('show');
                
                const sidebar = menu.closest('.ch-sidebar');
                sidebar?.querySelectorAll('.ch-item-menu-active').forEach(el => el.classList.remove('ch-item-menu-active'));
            });
        });
    }
}
