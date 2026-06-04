export function renderTerminalMenuHtml() {
    return `
        <div class="vb-terminal-menu-wrap" id="vb-terminal-menu-wrap">
            <button type="button" class="vb-terminal-control-btn icon-btn" id="terminal-menu-btn" title="Terminal actions" aria-label="Terminal actions" aria-haspopup="menu" aria-expanded="false">
                <i class="fa-solid fa-ellipsis-vertical"></i>
            </button>
            <div class="vb-terminal-menu" id="vb-terminal-menu" role="menu" hidden>
                <button type="button" id="terminal-multirun-btn" role="menuitem">
                    <i class="fa-solid fa-layer-group"></i>
                    <span>Multi Run</span>
                </button>
                <button type="button" id="terminal-senddebug-btn" role="menuitem">
                    <i class="fa-solid fa-bug"></i>
                    <span>Send debug log</span>
                </button>
            </div>
        </div>
    `;
}

/**
 * Generic dropdown menu controller. Each instance owns its own outside-click
 * dismissal independently — the trigger button does NOT stopPropagation, so
 * every document click reaches every mounted menu's listener, and each menu
 * decides for itself whether the click landed inside its own button/menu
 * (ignore) or outside (close). Mount as many of these as you want; they
 * coordinate via the DOM, not via shared state.
 *
 * @param {HTMLElement} container — root element to query within
 * @param {object} config
 * @param {string} config.buttonId — DOM id of the trigger button
 * @param {string} config.menuId   — DOM id of the menu element (the [hidden] panel)
 * @param {Array<{ id: string, onClick: (event: Event) => void }>} [config.items]
 */
export class TerminalMenu {
    constructor(container, config = {}) {
        this.container = container;
        this.config = config;
        this.button = null;
        this.menu = null;
        this.items = [];
        this.handleButtonClick = this.handleButtonClick.bind(this);
        this.handleDocumentClick = this.handleDocumentClick.bind(this);
        this.handleDocumentKeydown = this.handleDocumentKeydown.bind(this);
    }

    mount() {
        this.button = this.container.querySelector(`#${this.config.buttonId}`);
        this.menu = this.container.querySelector(`#${this.config.menuId}`);

        this.button?.addEventListener('click', this.handleButtonClick);
        document.addEventListener('click', this.handleDocumentClick);
        document.addEventListener('keydown', this.handleDocumentKeydown);

        for (const itemConfig of this.config.items || []) {
            const el = this.container.querySelector(`#${itemConfig.id}`);
            if (!el) continue;
            const handler = (event) => {
                event.preventDefault();
                this.close();
                itemConfig.onClick?.(event);
            };
            el.addEventListener('click', handler);
            this.items.push({ el, handler });
        }
    }

    destroy() {
        this.button?.removeEventListener('click', this.handleButtonClick);
        document.removeEventListener('click', this.handleDocumentClick);
        document.removeEventListener('keydown', this.handleDocumentKeydown);
        for (const { el, handler } of this.items) {
            el.removeEventListener('click', handler);
        }
        this.items = [];
        this.close();
        this.button = null;
        this.menu = null;
    }

    isOpen() {
        return !!this.menu && !this.menu.hasAttribute('hidden');
    }

    open() {
        if (!this.menu || this.isOpen()) return;
        this.menu.removeAttribute('hidden');
        this.button?.setAttribute('aria-expanded', 'true');
        this.items[0]?.el.focus();
    }

    close() {
        if (!this.menu) return;
        const focusInsideMenu = this.menu.contains(document.activeElement);
        this.menu.setAttribute('hidden', '');
        this.button?.setAttribute('aria-expanded', 'false');
        // If focus was on a menu item we just hid, restore it to the trigger
        // so it doesn't fall through to <body>. If focus was elsewhere (user
        // clicked outside, Tabbed away, etc.) leave it where the user put it.
        if (focusInsideMenu) {
            this.button?.focus({ preventScroll: true });
        }
    }

    toggle() {
        if (this.isOpen()) this.close();
        else this.open();
    }

    handleButtonClick(event) {
        event.preventDefault();
        this.toggle();
    }

    handleDocumentClick(event) {
        if (!this.isOpen()) return;
        const target = event.target;
        if (this.button?.contains(target) || this.menu?.contains(target)) return;
        this.close();
    }

    handleDocumentKeydown(event) {
        if (!this.isOpen()) return;
        if (event.key === 'Escape') {
            event.preventDefault();
            this.close();
            return;
        }
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            const els = this.items.map((i) => i.el).filter(Boolean);
            if (els.length === 0) return;
            event.preventDefault();
            const currentIdx = els.indexOf(document.activeElement);
            const dir = event.key === 'ArrowDown' ? 1 : -1;
            const nextIdx = currentIdx === -1 ? 0 : (currentIdx + dir + els.length) % els.length;
            els[nextIdx]?.focus();
        }
    }
}
