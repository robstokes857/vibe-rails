import { formatRelativeTime, escapeHtml } from './utils.js';


export class ChatHistorySidebar {
    constructor(app) {
        this.app = app;
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
                    <button class="ch-sidebar-hide-btn" id="ch-sidebar-hide-btn" title="Hide history">
                        <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" fill="currentColor" viewBox="0 0 16 16">
                            <path fill-rule="evenodd" d="M11.354 1.646a.5.5 0 0 1 0 .708L5.707 8l5.647 5.646a.5.5 0 0 1-.708.708l-6-6a.5.5 0 0 1 0-.708l6-6a.5.5 0 0 1 .708 0"/>
                        </svg>
                    </button>
                </div>
                <div class="ch-sidebar-body" id="ch-sidebar-body"></div>
            </div>`;
    }

    mount(root, { onToggle } = {}) {
        const sidebar = root.querySelector('#ch-sidebar');
        root.querySelector('#ch-sidebar-hide-btn')?.addEventListener('click', () => {
            sidebar?.classList.toggle('ch-sidebar-collapsed');
            onToggle?.();
        });
        this._load(root.querySelector('#ch-sidebar-body'));
    }

    async _load(body) {
        if (!body) return;
        body.innerHTML = '<div class="ch-loading"><div class="spinner-border spinner-border-sm text-primary" role="status"></div></div>';
        try {
            const data = await this.app.apiCall('/api/v1/chatHistory', 'GET', null, { showLoading: false });
            this._renderItems(body, data?.items || []);
        } catch {
            body.innerHTML = '<div class="ch-empty">Failed to load history.</div>';
        }
    }

    _renderItems(body, items) {
        if (!items.length) {
            body.innerHTML = '<div class="ch-empty">No chat history yet.</div>';
            return;
        }
        body.innerHTML = items.map(item => {
            const brand = this.app.getCliBrand(item.cli);
            const rawName = item.sessionDisplayName?.trim() || item.inputText?.trim().split('\n')[0] || 'Untitled';
            const name = rawName.length > 52 ? rawName.slice(0, 52) + '…' : rawName;
            const time = formatRelativeTime(item.startedUTC);
            const isActive = !item.endedUTC;
            const logoHtml = brand.logo
                ? `<img src="${escapeHtml(brand.logo)}" alt="${escapeHtml(brand.label)}" class="ch-item-logo">`
                : `<span class="ch-item-logo-fallback">${escapeHtml((brand.label || '?')[0])}</span>`;
            return `
                <div class="ch-item${isActive ? ' ch-item-active' : ''}">
                    <div class="ch-item-icon">${logoHtml}</div>
                    <div class="ch-item-content">
                        <div class="ch-item-name" title="${escapeHtml(rawName)}">${escapeHtml(name)}</div>
                        <div class="ch-item-meta">${escapeHtml(brand.label)}${isActive ? ' · <span class="ch-item-live">live</span>' : ` · ${time}`}</div>
                    </div>
                </div>`;
        }).join('');
    }
}
