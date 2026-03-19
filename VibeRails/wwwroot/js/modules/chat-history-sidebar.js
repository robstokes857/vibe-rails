import { buildLlmSelectionOptions, formatRelativeTime, escapeHtml } from './utils.js';

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
        this.body = null;
        this.contextMenu = null;
        this.refreshButton = null;
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
                <div class="ch-sidebar-search">
                    <div class="ch-search-input-wrapper">
                        <i class="fa-solid fa-magnifying-glass ch-search-icon"></i>
                        <input type="text" class="ch-search-input" id="ch-search-input" placeholder="Search sessions..." autocomplete="off">
                    </div>
                </div>
                <div class="ch-sidebar-body" id="ch-sidebar-body"></div>
                
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
        const body = root.querySelector('#ch-sidebar-body');
        const contextMenu = root.querySelector('#ch-context-menu');
        this.sidebar = sidebar;
        this.body = body;
        this.contextMenu = contextMenu;
        this.refreshButton = root.querySelector('#ch-sidebar-refresh-btn');
        const emitToggleState = () => onToggle?.(!sidebar?.classList.contains('ch-sidebar-collapsed'));

        root.querySelector('#ch-sidebar-hide-btn')?.addEventListener('click', () => {
            this._closeContextMenu();
            sidebar?.classList.toggle('ch-sidebar-collapsed');
            emitToggleState();
        });

        this.refreshButton?.addEventListener('click', async (e) => {
            e.stopPropagation();
            if (this.isLoadingPage || this.isLoadingForSearch) {
                return;
            }

            this._closeContextMenu();
            await this._load();
        });

        root.querySelector('#ch-sidebar-settings-btn')?.addEventListener('click', (e) => {
            e.stopPropagation();
            console.log('Chat History Settings Clicked');
            // TODO: Implement settings modal or action
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
