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
            </div>
        </div>
    `;
}

/**
 * Generic dropdown menu controller. Owns open/close state, aria-expanded,
 * outside-click and Escape dismissal, and binds a list of menu-item buttons
 * whose clicks run a callback and then close the menu.
 *
 * @param {HTMLElement} container — root element to query within
 * @param {object} config
 * @param {string} config.buttonId — DOM id of the trigger button
 * @param {string} config.menuId   — DOM id of the menu element (the [hidden] panel)
 * @param {Array<{ id: string, onClick: (event: Event) => void }>} [config.items]
 * @param {() => void} [config.onBeforeOpen] — fires just before the menu opens
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
                event.stopPropagation();
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
        this.config.onBeforeOpen?.();
        this.menu.removeAttribute('hidden');
        this.button?.setAttribute('aria-expanded', 'true');
    }

    close() {
        this.menu?.setAttribute('hidden', '');
        this.button?.setAttribute('aria-expanded', 'false');
    }

    toggle() {
        if (this.isOpen()) this.close();
        else this.open();
    }

    handleButtonClick(event) {
        event.preventDefault();
        event.stopPropagation();
        this.toggle();
    }

    handleDocumentClick() {
        this.close();
    }

    handleDocumentKeydown(event) {
        if (event.key === 'Escape') {
            this.close();
        }
    }
}
