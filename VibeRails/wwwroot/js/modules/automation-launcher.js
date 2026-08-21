import { escapeHtml, isConfirmDialogOpen } from './utils.js';

const PREFERENCES_ENDPOINT = '/api/v1/automation-nav/preferences';

/**
 * Shapes the server's launcher catalog into what the flyout and the customize modal
 * render: one entry per automation (`jobId`) or Python script (`status` = approved |
 * modified | unapproved), malformed entries dropped, sorted by saved order.
 */
export function normalizeLauncherItems(items) {
    if (!Array.isArray(items)) return [];
    return items
        .map((item) => ({
            key: String(item?.key || ''),
            kind: item?.kind === 'script' ? 'script' : 'automation',
            label: String(item?.label || ''),
            jobId: Number(item?.jobId),
            enabled: Boolean(item?.enabled),
            order: Number(item?.order) || 0,
            status: item?.kind === 'script' ? String(item?.status || 'unapproved') : null
        }))
        .filter((item) => item.key && (item.kind === 'script' || Number.isFinite(item.jobId)))
        .sort((a, b) => a.order - b.order);
}

/** A launcher item can be run right now: automations always, scripts only while signed. */
export function isLauncherItemRunnable(item) {
    return item?.kind !== 'script' || item?.status === 'approved';
}

function launcherItemIcon(item) {
    return item.kind === 'script' ? 'fa-brands fa-python' : 'fa-solid fa-play';
}

/**
 * Nav-bar Automation launcher: a flyout on the nav "Launch" button listing the
 * project's automations and the Python scripts (run-now on click; unsigned scripts are
 * shown disabled until they are signed on the Automation page), plus a customization
 * modal giving each entry an order and show/hide switch — the same treatment Custom
 * Environments get in the LLM launch pickers. Preferences are server-persisted per
 * install (GET/PUT/DELETE /api/v1/automation-nav/preferences) and fetched fresh on every
 * open, so there is no client cache to invalidate. Automation launches delegate to
 * JobController.launchFromNav so nav runs land in a terminal tab exactly like the
 * Automation page's own "Run now"; script runs go through PythonScriptsController.run so
 * they open the same interactive terminal surface as a run from the page.
 *
 * The customization modal intentionally reuses the llm-picker-* modal CSS classes so
 * both "order and show/hide" dialogs stay visually identical.
 */
export class AutomationNavLauncher {
    constructor(app) {
        this.app = app;
        this.items = null;
        this.flyout = null;
        this.triggerElement = null;
        this.documentHandlers = null;
        this.modalState = null;
    }

    async toggle(triggerElement) {
        if (this.flyout) {
            const sameTrigger = this.triggerElement === triggerElement;
            this.closeFlyout();
            if (sameTrigger) return;
        }
        await this.openFlyout(triggerElement);
    }

    async openFlyout(triggerElement) {
        const flyout = document.createElement('div');
        flyout.className = 'automation-launch-flyout';
        flyout.setAttribute('role', 'menu');
        flyout.setAttribute('aria-label', 'Launch an automation');
        flyout.innerHTML = `
            <div class="automation-launch-flyout-header">
                <i class="fa-solid fa-play" aria-hidden="true"></i>
                <span>Launch an automation</span>
            </div>
            <div class="automation-launch-flyout-items" data-automation-launch-items>
                <div class="automation-launch-flyout-empty text-muted">
                    <span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Loading…
                </div>
            </div>
            <div class="automation-launch-flyout-footer">
                <button type="button" class="automation-launch-footer-btn" role="menuitem"
                        data-automation-launch-action="customize"
                        title="Choose which automations appear here and arrange their order">
                    <i class="fa-solid fa-sliders" aria-hidden="true"></i><span>Customize list…</span>
                </button>
                <button type="button" class="automation-launch-footer-btn" role="menuitem"
                        data-automation-launch-action="manage"
                        title="Open the Automation page">
                    <i class="fa-solid fa-clock-rotate-left" aria-hidden="true"></i><span>Manage automations</span>
                </button>
            </div>`;

        document.body.appendChild(flyout);
        this.flyout = flyout;
        this.triggerElement = triggerElement;
        triggerElement?.classList?.add('is-flyout-open');
        triggerElement?.setAttribute?.('aria-expanded', 'true');
        this._position(triggerElement, flyout);

        flyout.querySelector('[data-automation-launch-action="customize"]')
            ?.addEventListener('click', () => {
                // Remembered so closing the modal can hand focus back to the button that
                // started it (there are two: top nav and sidebar, one usually hidden).
                const triggerElement = this.triggerElement;
                this.closeFlyout();
                this.openCustomizationModal({ triggerElement });
            });
        flyout.querySelector('[data-automation-launch-action="manage"]')
            ?.addEventListener('click', () => {
                this.closeFlyout();
                this.app.navigate('jobs');
            });

        const onDocumentClick = (event) => {
            if (!this.flyout) return;
            if (this.flyout.contains(event.target)) return;
            if (this.triggerElement && this.triggerElement.contains(event.target)) return;
            this.closeFlyout();
        };
        const onKeydown = (event) => {
            // A confirmDialog overlay owns Escape while it is up (see utils.js).
            if (isConfirmDialogOpen()) return;
            if (event.key === 'Escape') {
                event.preventDefault();
                this.closeFlyout({ restoreFocus: true });
                return;
            }
            this._handleFlyoutNavigation(event);
        };
        const onRelayout = () => {
            if (this.flyout && this.triggerElement?.isConnected) {
                this._position(this.triggerElement, this.flyout);
            } else {
                this.closeFlyout();
            }
        };
        this.documentHandlers = { onDocumentClick, onKeydown, onRelayout };
        // Deferred so the opening click itself doesn't immediately close the flyout.
        setTimeout(() => {
            if (this.documentHandlers?.onDocumentClick !== onDocumentClick) return;
            document.addEventListener('click', onDocumentClick, true);
        }, 0);
        document.addEventListener('keydown', onKeydown, true);
        window.addEventListener('resize', onRelayout);

        await this._loadPreferences();
        this._renderFlyoutItems();
        // A menu button hands focus to its menu; Escape hands it back to the trigger.
        this._focusFlyoutItem();
    }

    closeFlyout({ restoreFocus = false } = {}) {
        const { flyout, triggerElement, documentHandlers } = this;
        this.flyout = null;
        this.triggerElement = null;
        this.documentHandlers = null;
        if (documentHandlers) {
            document.removeEventListener('click', documentHandlers.onDocumentClick, true);
            document.removeEventListener('keydown', documentHandlers.onKeydown, true);
            window.removeEventListener('resize', documentHandlers.onRelayout);
        }
        triggerElement?.classList?.remove('is-flyout-open');
        triggerElement?.setAttribute?.('aria-expanded', 'false');
        flyout?.remove();
        if (restoreFocus && triggerElement?.isConnected) triggerElement.focus?.();
    }

    /** The menu items the keyboard can reach: rows first, then the footer buttons. */
    _flyoutMenuItems() {
        return Array.from(this.flyout?.querySelectorAll?.('[role="menuitem"]:not([disabled])') || []);
    }

    /** True while focus sits nowhere in particular or on the trigger that opened us. */
    _focusIsLoose() {
        const active = globalThis.document?.activeElement;
        return !active || active === globalThis.document?.body || active === this.triggerElement;
    }

    /** True while focus sits in the flyout, on its trigger, or nowhere in particular. */
    _flyoutOwnsFocus() {
        return this._focusIsLoose()
            || Boolean(this.flyout?.contains?.(globalThis.document?.activeElement));
    }

    /**
     * ArrowUp/ArrowDown/Home/End move through the rows and footer buttons while the
     * flyout (or nothing / its trigger) owns focus — arrows typed elsewhere on the page
     * keep their meaning.
     */
    _handleFlyoutNavigation(event) {
        if (!this.flyout) return;
        if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
        if (!this._flyoutOwnsFocus()) return;
        const items = this._flyoutMenuItems();
        if (items.length === 0) return;
        event.preventDefault();
        const current = items.indexOf(globalThis.document?.activeElement);
        let next;
        if (event.key === 'Home') next = 0;
        else if (event.key === 'End') next = items.length - 1;
        else if (current < 0) next = event.key === 'ArrowDown' ? 0 : items.length - 1;
        else next = (current + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
        items[next].focus();
    }

    /**
     * Focuses a row by index (only that row — a rebuild that left it busy leaves focus
     * alone), or the first reachable menu item (a footer button when the list is empty).
     * Never steals focus that is already parked on something else, in the flyout or out.
     */
    _focusFlyoutItem(index = null) {
        if (!this.flyout || !this._focusIsLoose()) return;
        const items = this._flyoutMenuItems();
        const target = index === null
            ? items[0]
            : items.find((item) => Number(item.dataset?.automationLaunchIndex) === index);
        target?.focus?.();
    }

    /** Runs a Python script through the shared controller in a new interactive terminal tab. */
    async _runScript(name, index = null) {
        const scripts = this.app.jobController?.pythonScripts;
        if (!scripts || !name) return;
        // run() registers the name in runningNames before its first await, so the rows
        // drawn right after it starts already show this one busy.
        const pending = scripts.run(name);
        this._renderFlyoutItems({ focusIndex: index });
        try {
            await pending;
        } finally {
            this._renderFlyoutItems({ focusIndex: index });
        }
    }

    _position(triggerElement, flyout) {
        const rect = triggerElement?.getBoundingClientRect?.();
        if (!rect) return;
        const inSidebar = Boolean(triggerElement.closest?.('.app-sidebar'));
        // Measure after render so clamping uses the real size.
        const { offsetWidth: width, offsetHeight: height } = flyout;
        let left = inSidebar ? rect.right + 8 : rect.left;
        let top = inSidebar ? rect.top : rect.bottom + 6;
        left = Math.max(8, Math.min(left, window.innerWidth - width - 8));
        top = Math.max(8, Math.min(top, window.innerHeight - height - 8));
        flyout.style.left = `${Math.round(left)}px`;
        flyout.style.top = `${Math.round(top)}px`;
    }

    async _loadPreferences() {
        try {
            const response = await this.app.apiCall(PREFERENCES_ENDPOINT, 'GET', null, { showLoading: false });
            this.items = normalizeLauncherItems(response?.items);
        } catch (error) {
            console.error('Failed to load Automation launcher preferences.', error);
            this.items = null;
        }
    }

    _renderFlyoutItem(item, index, running) {
        const label = escapeHtml(item.label);
        const unsigned = !isLauncherItemRunnable(item);
        const disabled = unsigned || running;
        const title = unsigned
            ? 'Sign it on the Automation page first'
            : running ? `${label} is running` : `Run ${label} now`;
        return `
            <button type="button" class="automation-launch-item${unsigned ? ' is-unsigned' : ''}${running ? ' is-running' : ''}"
                    role="menuitem" data-automation-launch-index="${index}"
                    ${disabled ? 'disabled aria-disabled="true"' : ''}${running ? ' aria-busy="true"' : ''}
                    title="${title}">
                ${running
                    ? '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>'
                    : `<i class="${launcherItemIcon(item)}" aria-hidden="true"></i>`}
                <span class="automation-launch-item-label">${label}</span>
                ${unsigned ? '<span class="automation-launch-item-note">Not signed</span>' : ''}
            </button>`;
    }

    /**
     * (Re)draws the rows from this.items. `focusIndex` re-focuses that row after a
     * rebuild (a run finishing under the keyboard user's cursor).
     */
    _renderFlyoutItems({ focusIndex = null } = {}) {
        const target = this.flyout?.querySelector('[data-automation-launch-items]');
        if (!target) return;

        if (this.items === null) {
            target.innerHTML = '<div class="automation-launch-flyout-empty text-muted">Could not load automations.</div>';
            return;
        }

        const visible = this.items.filter((item) => item.enabled);
        if (visible.length === 0) {
            const hiddenCount = this.items.length - visible.length;
            target.innerHTML = hiddenCount > 0
                ? '<div class="automation-launch-flyout-empty text-muted">All automations are hidden. Use "Customize list…" to show some.</div>'
                : '<div class="automation-launch-flyout-empty text-muted">No automations yet. Create one from the Automation page.</div>';
        } else {
            const runningNames = this.app.jobController?.pythonScripts?.runningNames;
            target.innerHTML = visible.map((item, index) => this._renderFlyoutItem(
                item, index, item.kind === 'script' && Boolean(runningNames?.has?.(item.label)))).join('');
            target.querySelectorAll('[data-automation-launch-index]').forEach((button) => {
                button.addEventListener('click', () => {
                    const index = Number(button.dataset.automationLaunchIndex);
                    const item = visible[index];
                    if (!item || !isLauncherItemRunnable(item)) return;
                    if (item.kind === 'script') {
                        void this._runScript(item.label, index);
                    } else {
                        this.closeFlyout();
                        void this.app.jobController?.launchFromNav?.(item.jobId);
                    }
                });
            });
        }

        // The list was rendered after the flyout was positioned against its loading
        // placeholder; re-clamp so a long list doesn't run off-screen.
        if (this.triggerElement) this._position(this.triggerElement, this.flyout);
        if (focusIndex !== null) this._focusFlyoutItem(focusIndex);
    }

    // --- Customization modal (order + show/hide, mirroring the LLM picker modal) ---

    async openCustomizationModal({ triggerElement = null } = {}) {
        this._closeCustomizationModal({ restoreFocus: false });
        const host = document.getElementById('modal-container');
        if (!host) return;

        // The flyout's own load may have failed; one more try before giving up.
        if (this.items === null || this.items === undefined) await this._loadPreferences();
        const loadFailed = this.items === null;
        const items = (this.items || []).map((item) => ({ ...item }));

        const layer = document.createElement('div');
        layer.className = 'llm-picker-modal-layer';
        layer.innerHTML = `
            <div class="modal fade show d-block llm-picker-customization-modal" tabindex="-1"
                 role="dialog" aria-modal="true" aria-labelledby="automation-nav-modal-title">
                <div class="modal-dialog modal-lg modal-dialog-scrollable">
                    <div class="modal-content">
                        <div class="modal-header">
                            <div>
                                <h5 class="modal-title" id="automation-nav-modal-title">Customize Automation launcher</h5>
                                <p class="text-muted small mb-0">Choose which automations appear in the nav launcher and arrange their order.</p>
                            </div>
                            <button type="button" class="btn-close" data-automation-nav-action="cancel"
                                    aria-label="Close Automation launcher customization"></button>
                        </div>
                        <form data-automation-nav-form>
                            <div class="modal-body">
                                <div class="llm-picker-modal-section">
                                    <h6>Automations</h6>
                                    <div data-automation-nav-group></div>
                                </div>
                                <div class="alert alert-danger mt-3 mb-0 d-none" role="alert"
                                     data-automation-nav-error></div>
                            </div>
                            <div class="modal-footer llm-picker-modal-actions">
                                <button type="button" class="btn btn-outline-secondary me-auto"
                                        data-automation-nav-action="reset">Reset to defaults</button>
                                <button type="button" class="btn btn-secondary"
                                        data-automation-nav-action="cancel">Cancel</button>
                                <button type="submit" class="btn btn-primary"
                                        data-automation-nav-action="save" ${loadFailed ? 'disabled' : ''}>Save</button>
                            </div>
                        </form>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show llm-picker-customization-backdrop"></div>`;

        const underlying = Array.from(host.children).map((element) => ({
            element,
            inert: Boolean(element.inert),
            ariaHidden: element.getAttribute('aria-hidden')
        }));
        underlying.forEach(({ element }) => {
            element.inert = true;
            element.setAttribute('aria-hidden', 'true');
        });
        host.appendChild(layer);

        const state = {
            layer,
            host,
            items,
            loadFailed,
            triggerElement,
            pending: false,
            draggingKey: null,
            underlying,
            keydownHandler: null,
            observer: null,
            disposed: false
        };
        this.modalState = state;
        this._renderModalRows();
        this._bindModalActions();
        if (loadFailed) {
            this._showModalError('Could not load the automations and scripts. Close this dialog and try again.');
        }

        state.observer = new MutationObserver(() => {
            if (!layer.isConnected) this._disposeModalState(state);
        });
        state.observer.observe(host, { childList: true });

        requestAnimationFrame(() => layer.querySelector('.llm-picker-customization-modal')?.focus());
    }

    _renderModalRows(focusTarget = null) {
        const state = this.modalState;
        if (!state || state.disposed) return;
        const container = state.layer.querySelector('[data-automation-nav-group]');
        if (!container) return;

        container.innerHTML = state.items.length === 0
            ? `<p class="llm-picker-group-empty text-muted small mb-0">${state.loadFailed
                ? 'The list could not be loaded.'
                : 'No automations yet. Create one from the Automation page first.'}</p>`
            : state.items.map((item, index) => this._renderModalRow(item, index, state.items.length)).join('');

        this._bindModalRows();
        if (focusTarget) {
            requestAnimationFrame(() => state.layer
                .querySelector(`[data-automation-nav-key="${CSS.escape(focusTarget.key)}"] [data-automation-nav-move="${focusTarget.direction}"]`)
                ?.focus());
        }
    }

    _renderModalRow(item, index, count) {
        const key = escapeHtml(item.key);
        const label = escapeHtml(item.label);
        return `
            <div class="llm-picker-modal-row${item.enabled ? '' : ' is-hidden'}" data-automation-nav-key="${key}">
                <span class="llm-picker-drag-handle" draggable="true" role="button" tabindex="0"
                      aria-label="Drag ${label} to reorder" title="Drag to reorder">
                    <i class="fa-solid fa-grip-vertical" aria-hidden="true"></i>
                </span>
                <span class="llm-picker-row-logo llm-picker-row-logo-fallback"><i class="${launcherItemIcon(item)}" aria-hidden="true"></i></span>
                <span class="llm-picker-row-label">${label}</span>
                <div class="form-check form-switch llm-picker-visibility-switch">
                    <input class="form-check-input" type="checkbox" role="switch"
                           id="automation-nav-visible-${escapeHtml(item.key.replace(/[^a-zA-Z0-9_-]/g, '-'))}"
                           data-automation-nav-enabled ${item.enabled ? 'checked' : ''}
                           aria-label="Show ${label} in the nav launcher">
                </div>
                <div class="llm-picker-move-buttons" role="group" aria-label="Move ${label}">
                    <button type="button" class="btn btn-sm btn-outline-secondary"
                            data-automation-nav-move="up" aria-label="Move ${label} up" ${index === 0 ? 'disabled' : ''}>
                        <i class="fa-solid fa-arrow-up" aria-hidden="true"></i>
                    </button>
                    <button type="button" class="btn btn-sm btn-outline-secondary"
                            data-automation-nav-move="down" aria-label="Move ${label} down" ${index === count - 1 ? 'disabled' : ''}>
                        <i class="fa-solid fa-arrow-down" aria-hidden="true"></i>
                    </button>
                </div>
            </div>`;
    }

    _bindModalActions() {
        const state = this.modalState;
        if (!state) return;
        state.layer.querySelectorAll('[data-automation-nav-action="cancel"]')
            .forEach((button) => button.addEventListener('click', () => this._closeCustomizationModal()));
        state.layer.querySelector('[data-automation-nav-action="reset"]')
            ?.addEventListener('click', () => void this._resetPreferences());
        state.layer.querySelector('[data-automation-nav-form]')
            ?.addEventListener('submit', (event) => {
                event.preventDefault();
                void this._savePreferences();
            });

        state.keydownHandler = (event) => {
            if (this.modalState !== state) return;
            // A confirmDialog overlay owns Escape while it is up (see utils.js).
            if (isConfirmDialogOpen()) return;
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                this._closeCustomizationModal();
                return;
            }
            if (event.key === 'Tab') this._trapModalFocus(event, state.layer);
        };
        document.addEventListener('keydown', state.keydownHandler, true);
    }

    _bindModalRows() {
        const state = this.modalState;
        if (!state) return;
        state.layer.querySelectorAll('.llm-picker-modal-row').forEach((row) => {
            const key = row.dataset.automationNavKey;
            row.querySelector('[data-automation-nav-enabled]')?.addEventListener('change', (event) => {
                const item = state.items.find((candidate) => candidate.key === key);
                if (!item) return;
                item.enabled = Boolean(event.target.checked);
                row.classList.toggle('is-hidden', !item.enabled);
            });
            row.querySelectorAll('[data-automation-nav-move]').forEach((button) => {
                button.addEventListener('click', () => {
                    this._moveModalItem(key, button.dataset.automationNavMove);
                });
            });

            const handle = row.querySelector('.llm-picker-drag-handle');
            handle?.addEventListener('keydown', (event) => {
                if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
                event.preventDefault();
                this._moveModalItem(key, event.key === 'ArrowUp' ? 'up' : 'down');
            });
            handle?.addEventListener('dragstart', (event) => {
                state.draggingKey = key;
                row.classList.add('is-dragging');
                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData('text/plain', key);
            });
            handle?.addEventListener('dragend', () => {
                state.draggingKey = null;
                state.layer.querySelectorAll('.is-dragging, .is-drag-target')
                    .forEach((element) => element.classList.remove('is-dragging', 'is-drag-target'));
            });
            row.addEventListener('dragover', (event) => {
                if (!state.draggingKey || state.draggingKey === key) return;
                event.preventDefault();
                event.dataTransfer.dropEffect = 'move';
                row.classList.add('is-drag-target');
            });
            row.addEventListener('dragleave', () => row.classList.remove('is-drag-target'));
            row.addEventListener('drop', (event) => {
                if (!state.draggingKey || state.draggingKey === key) return;
                event.preventDefault();
                const fromIndex = state.items.findIndex((item) => item.key === state.draggingKey);
                const toIndex = state.items.findIndex((item) => item.key === key);
                if (fromIndex < 0 || toIndex < 0) return;
                const [moved] = state.items.splice(fromIndex, 1);
                state.items.splice(toIndex, 0, moved);
                state.draggingKey = null;
                this._renderModalRows();
            });
        });
    }

    _moveModalItem(key, direction) {
        const state = this.modalState;
        if (!state) return;
        const index = state.items.findIndex((item) => item.key === key);
        if (index < 0) return;
        const target = direction === 'up' ? index - 1 : index + 1;
        if (target < 0 || target >= state.items.length) return;
        const [moved] = state.items.splice(index, 1);
        state.items.splice(target, 0, moved);
        this._renderModalRows({ key, direction });
    }

    async _savePreferences() {
        const state = this.modalState;
        if (!state || state.pending || state.loadFailed) return;
        state.pending = true;
        this._showModalError(null);
        try {
            const payload = {
                items: state.items.map((item, index) => ({
                    key: item.key,
                    kind: item.kind,
                    label: item.label,
                    jobId: item.jobId,
                    enabled: item.enabled,
                    order: index
                }))
            };
            const response = await this.app.apiCall(
                PREFERENCES_ENDPOINT, 'PUT', payload,
                { showLoading: false, preferErrorResponseMessage: true });
            this._acceptCatalog(response);
            this.app.showToast('Automation launcher updated', 'Nav launcher preferences were saved.', 'success');
            this._closeCustomizationModal();
        } catch (error) {
            const message = error?.message || 'Could not save Automation launcher preferences.';
            // The server rejects a snapshot that no longer matches its catalog (an
            // automation was added, renamed or removed meanwhile). Rather than a dead end,
            // pull the current list, keep the user's arrangement for what survived, and
            // let them look it over and save again.
            const changed = await this._refreshModalCatalog(state);
            this._showModalError(changed ? `The list changed — review and save again. ${message}` : message);
        } finally {
            state.pending = false;
        }
    }

    /**
     * Reloads the catalog into the open modal, keeping the draft's order and show/hide
     * choices for entries that still exist. Resolves true when the list actually differed
     * from the draft (false when it matched, or could not be loaded — the draft is kept).
     */
    async _refreshModalCatalog(state) {
        await this._loadPreferences();
        if (this.modalState !== state || state.disposed || !Array.isArray(this.items)) return false;
        const identity = (item) => JSON.stringify([item.key, item.kind, item.label, item.jobId]);
        const fresh = new Map(this.items.map((item) => [item.key, item]));
        const draftIdentities = state.items.map(identity).sort();
        const freshIdentities = this.items.map(identity).sort();
        if (draftIdentities.length === freshIdentities.length
            && draftIdentities.every((value, index) => value === freshIdentities[index])) {
            return false;
        }
        const merged = state.items
            .filter((item) => fresh.has(item.key))
            .map((item) => ({ ...fresh.get(item.key), enabled: item.enabled }));
        const kept = new Set(merged.map((item) => item.key));
        merged.push(...this.items.filter((item) => !kept.has(item.key)).map((item) => ({ ...item })));
        state.items = merged;
        this._renderModalRows();
        return true;
    }

    async _resetPreferences() {
        const state = this.modalState;
        if (!state || state.pending) return;
        state.pending = true;
        this._showModalError(null);
        try {
            const response = await this.app.apiCall(
                PREFERENCES_ENDPOINT, 'DELETE', null, { showLoading: false });
            this._acceptCatalog(response);
            state.items = (this.items || []).map((item) => ({ ...item }));
            state.loadFailed = this.items === null;
            state.layer.querySelector('[data-automation-nav-action="save"]')?.toggleAttribute('disabled', state.loadFailed);
            this._renderModalRows();
        } catch (error) {
            this._showModalError(error?.message || 'Could not reset Automation launcher preferences.');
        } finally {
            state.pending = false;
        }
    }

    _acceptCatalog(response) {
        if (Array.isArray(response?.items)) this.items = normalizeLauncherItems(response.items);
    }

    _showModalError(message) {
        const alert = this.modalState?.layer.querySelector('[data-automation-nav-error]');
        if (!alert) return;
        if (message) {
            alert.textContent = message;
            alert.classList.remove('d-none');
        } else {
            alert.textContent = '';
            alert.classList.add('d-none');
        }
    }

    _closeCustomizationModal({ restoreFocus = true } = {}) {
        const state = this.modalState;
        if (!state) return;
        this._disposeModalState(state, { restoreFocus });
    }

    _disposeModalState(state, { restoreFocus = false } = {}) {
        if (state.disposed) return;
        state.disposed = true;
        if (this.modalState === state) this.modalState = null;
        state.observer?.disconnect();
        if (state.keydownHandler) {
            document.removeEventListener('keydown', state.keydownHandler, true);
        }
        state.underlying.forEach(({ element, inert, ariaHidden }) => {
            if (!element.isConnected) return;
            element.inert = inert;
            if (ariaHidden === null) element.removeAttribute('aria-hidden');
            else element.setAttribute('aria-hidden', ariaHidden);
        });
        state.layer.remove();
        if (restoreFocus) this._restoreTriggerFocus(state.triggerElement);
    }

    /**
     * Focus goes back to the button that opened the modal. Without one (or after a
     * re-render dropped it), fall back to whichever Launch button is actually visible —
     * index.html has one in the top nav and one in the sidebar, and only one is shown.
     */
    _restoreTriggerFocus(triggerElement) {
        if (triggerElement?.isConnected) {
            triggerElement.focus?.();
            return;
        }
        const buttons = Array.from(document.querySelectorAll('[data-action="automation-launcher"]'));
        const visible = buttons.find((button) => (button.getClientRects?.().length ?? 1) > 0);
        (visible || buttons[0])?.focus?.();
    }

    _trapModalFocus(event, layer) {
        const focusable = Array.from(layer.querySelectorAll(
            'button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])'))
            .filter((element) => !element.closest('[inert]'));
        if (focusable.length === 0) return;
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    }
}
