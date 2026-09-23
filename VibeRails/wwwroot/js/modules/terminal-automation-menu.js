// Automation runs stay in a compact list. Merely listing a run never creates
// an xterm or opens a socket; the manager materializes only a selected terminal.
export function isAutomationTab(tab) {
    return tab?.jobRunId != null;
}

export function automationStatus(tab) {
    if (tab.statusAvailable === false) return 'Unavailable';
    if (tab.hasActiveSession) return 'Running';
    return tab.sessionId ? 'Finished' : 'Starting';
}

export function automationEntries(tabs) {
    return [...tabs.values()].sort((a, b) => {
        const active = Number(b.hasActiveSession === true) - Number(a.hasActiveSession === true);
        return active || (Date.parse(b.createdUTC) || 0) - (Date.parse(a.createdUTC) || 0);
    });
}

export class TerminalAutomationMenu {
    constructor(manager) {
        this.manager = manager;
        this.wrapper = null;
        this.button = null;
        this.count = null;
        this.popup = null;
        this.signature = null;
        this.handleClick = event => this.onClick(event);
        this.handleKey = event => this.onKey(event);
        this.handlePosition = () => this.position();
        this.handleToggle = () => this.toggle();
    }

    mount() {
        const root = this.manager.container;
        this.wrapper = root.querySelector('#vb-terminal-automations-wrapper');
        this.button = root.querySelector('#vb-terminal-automations-btn');
        this.count = root.querySelector('#vb-terminal-automations-count');
        if (!this.button) return;
        this.popup = document.createElement('div');
        this.popup.className = 'vb-terminal-automations-menu';
        this.popup.id = 'vb-terminal-automations-menu';
        this.popup.hidden = true;
        this.popup.setAttribute('role', 'dialog');
        this.popup.setAttribute('aria-label', 'Automation terminals');
        document.body.appendChild(this.popup);
        this.button.addEventListener('click', this.handleToggle);
        document.addEventListener('click', this.handleClick, true);
        document.addEventListener('keydown', this.handleKey, true);
        window.addEventListener('resize', this.handlePosition);
        document.addEventListener('scroll', this.handlePosition, true);
        this.timer = setInterval(() => {
            if (!document.hidden) void this.manager.refreshAutomationTabs();
        }, 10000);
        this.refresh();
    }

    destroy() {
        clearInterval(this.timer);
        this.button?.removeEventListener('click', this.handleToggle);
        document.removeEventListener('click', this.handleClick, true);
        document.removeEventListener('keydown', this.handleKey, true);
        window.removeEventListener('resize', this.handlePosition);
        document.removeEventListener('scroll', this.handlePosition, true);
        this.popup?.remove();
        this.popup = null;
    }

    refresh() {
        if (!this.button) return;
        const entries = automationEntries(this.manager.automationTabs);
        const running = entries.filter(tab => tab.hasActiveSession).length;
        this.wrapper.hidden = entries.length === 0;
        this.count.textContent = String(entries.length);
        this.button.title = `Automations: ${running} running · ${entries.length} terminals`;
        this.button.setAttribute('aria-label', this.button.title);
        this.button.classList.toggle('is-active', this.manager.automationTabs.has(this.manager.activeTabId));
        const signature = JSON.stringify(entries.map(tab => [tab.tabId, tab.automationName, tab.jobRunId,
            tab.hasActiveSession, tab.sessionId, tab.statusAvailable, this.manager.activeTabId === tab.tabId]));
        if (signature === this.signature) return;
        this.signature = signature;
        const focused = document.activeElement;
        const focusId = this.popup?.contains(focused) ? focused.dataset.automationOpen || focused.dataset.automationClose : null;
        const focusAction = focused?.dataset?.automationClose ? 'automationClose' : 'automationOpen';
        this.popup.replaceChildren();
        const heading = document.createElement('div');
        heading.className = 'vb-terminal-automations-heading';
        heading.textContent = `Automations · ${running} running`;
        this.popup.appendChild(heading);
        const list = document.createElement('div');
        list.className = 'vb-terminal-automations-list';
        for (const tab of entries) {
            const row = document.createElement('div');
            row.className = 'vb-terminal-automation-row';
            row.classList.toggle('is-active', this.manager.activeTabId === tab.tabId);
            const open = document.createElement('button');
            open.type = 'button';
            open.className = 'vb-terminal-automation-open';
            open.dataset.automationOpen = tab.tabId;
            const icon = document.createElement('i');
            icon.className = 'fa-solid fa-robot';
            icon.setAttribute('aria-hidden', 'true');
            const main = document.createElement('span');
            main.className = 'vb-terminal-automation-main';
            const name = document.createElement('span');
            name.className = 'vb-terminal-automation-name';
            name.textContent = tab.automationName || 'Automation';
            name.title = name.textContent;
            const status = automationStatus(tab);
            const detail = document.createElement('span');
            detail.className = 'vb-terminal-automation-detail';
            detail.dataset.status = status.toLowerCase();
            const runId = String(tab.jobRunId);
            detail.textContent = `${status} · ${runId.length > 12 ? runId.slice(0, 8) : runId}${status === 'Finished' ? ' · Replay' : ''}`;
            detail.title = `Run ${runId}`;
            main.append(name, detail);
            open.append(icon, main);
            open.title = tab.sessionId ? `Session ${tab.sessionId}` : `Run #${tab.jobRunId}`;
            const close = document.createElement('button');
            close.type = 'button';
            close.className = 'vb-terminal-automation-close';
            close.dataset.automationClose = tab.tabId;
            close.innerHTML = '<i class="fa-solid fa-xmark" aria-hidden="true"></i>';
            close.title = tab.hasActiveSession ? 'Close Automation terminal and stop its run' : 'Dismiss Automation terminal';
            close.setAttribute('aria-label', `${close.title}: ${name.textContent}`);
            row.append(open, close);
            list.appendChild(row);
        }
        this.popup.appendChild(list);
        const footer = document.createElement('div');
        footer.className = 'vb-terminal-automations-footer';
        footer.textContent = 'Finished runs open replay. Recordings stay in History.';
        this.popup.appendChild(footer);
        if (!entries.length) this.close();
        else if (focusId) {
            [...this.popup.querySelectorAll('button')].find(button => button.dataset[focusAction] === focusId)?.focus();
        }
        this.position();
    }

    position() {
        if (!this.popup || this.popup.hidden) return;
        const anchor = this.button.getBoundingClientRect();
        const width = Math.min(360, window.innerWidth - 16);
        this.popup.style.width = `${width}px`;
        this.popup.style.left = `${Math.max(8, Math.min(anchor.right - width, window.innerWidth - width - 8))}px`;
        this.popup.style.top = `${anchor.bottom + 6}px`;
        this.popup.style.maxHeight = `${Math.max(100, window.innerHeight - anchor.bottom - 14)}px`;
    }

    toggle() {
        if (!this.popup?.hidden) return this.close();
        this.popup.hidden = false;
        this.button.setAttribute('aria-expanded', 'true');
        this.position();
        this.popup.querySelector('button')?.focus();
        void this.manager.refreshAutomationTabs();
    }

    close() {
        if (!this.popup) return;
        if (this.popup.contains(document.activeElement)) this.button.focus({ preventScroll: true });
        this.popup.hidden = true;
        this.button.setAttribute('aria-expanded', 'false');
    }

    onClick(event) {
        if (!this.popup || this.popup.hidden) return;
        const open = event.target.closest('[data-automation-open]');
        const close = event.target.closest('[data-automation-close]');
        if (open && this.popup.contains(open)) {
            this.close();
            void this.manager.openAutomationTab(open.dataset.automationOpen).catch(error => {
                this.manager.app.showError(`Failed to open Automation terminal: ${error.message}`);
            });
        } else if (close && this.popup.contains(close)) {
            void this.manager.closeAutomationTab(close.dataset.automationClose);
        } else if (!this.popup.contains(event.target) && !this.button.contains(event.target)) {
            this.close();
        }
    }

    onKey(event) {
        if (!this.popup || this.popup.hidden) return;
        if (event.key === 'Escape') {
            event.preventDefault();
            event.stopPropagation();
            this.close();
        } else if ((event.key === 'ArrowDown' || event.key === 'ArrowUp') && this.popup.contains(document.activeElement)) {
            event.preventDefault();
            const buttons = [...this.popup.querySelectorAll('button')];
            const next = (buttons.indexOf(document.activeElement) + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length;
            buttons[next]?.focus();
        }
    }
}
