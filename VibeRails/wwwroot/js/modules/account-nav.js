import { isConfirmDialogOpen } from './utils.js';

export const ACCOUNT_SITE_URL = 'https://viberails.ai/Account';

function displayText(value) {
    return typeof value === 'string' && value.trim() ? value.trim() : null;
}

/**
 * What the top bar shows for the saved viberails.ai credential. `apiKey` is the
 * settings mask, so the key ending is only what the mask already shows. An organization
 * belongs to an approved account: a key without an email never shows one.
 */
export function accountSummary(settings) {
    const apiKey = typeof settings?.apiKey === 'string' ? settings.apiKey : '';
    if (!apiKey) return { state: 'signed-out', email: null, organization: null, initial: '', keyEnding: '' };
    const email = displayText(settings.remoteAccountEmail);
    const organization = email ? displayText(settings.remoteAccountOrganization) : null;
    return {
        state: !email ? 'key-only' : organization ? 'managed' : 'personal',
        email,
        organization,
        initial: email ? Array.from(email)[0].toUpperCase() : '',
        keyEnding: /[^•]{1,4}$/u.exec(apiKey)?.[0] ?? ''
    };
}

/** The chip's accessible name, which also says who manages the account. */
export function accountChipLabel(summary) {
    if (summary.state === 'key-only') return 'Account menu. API key configured';
    const label = `Account menu. Signed in as ${summary.email}`;
    return summary.organization ? `${label}, managed by ${summary.organization}` : label;
}

/**
 * The signed-in account in the top bar: a chip with a popover. Signed out, it stays hidden
 * and the existing Sign in / Sign up pill is the only prompt (VIBE-243).
 * The popover holds ordinary buttons, not ARIA menu items: Tab moves through it, Escape or
 * an outside click closes it, and Escape hands focus back to the chip.
 */
export class AccountNavMenu {
    constructor(app, doc = globalThis.document) {
        this.app = app;
        this.doc = doc;
        this.isOpen = false;
        this.documentHandlers = null;
        this.bound = false;
    }

    get host() { return this.doc?.querySelector?.('[data-account-menu-host]') ?? null; }
    get trigger() { return this.host?.querySelector('[data-action="account-menu"]') ?? null; }
    get panel() { return this.host?.querySelector('[data-account-menu]') ?? null; }

    bind() {
        const panel = this.panel;
        if (this.bound || !panel) return;
        this.bound = true;
        panel.addEventListener('click', event => {
            const action = event.target.closest('[data-account-menu-action]');
            if (!action || action.disabled) return;
            const name = action.dataset.accountMenuAction;
            if (name === 'site') {
                this.close();
                return;
            }
            event.preventDefault();
            if (name === 'settings') {
                this.close();
                // Already on Settings: switch tabs in place rather than reload over a draft.
                if (this.app.currentView === 'settings' && this.app.settingsController?._selectSettingsTab) {
                    this.app.settingsController._selectSettingsTab('general');
                } else {
                    this.app.navigate('settings', { tab: 'general' }, { resetStack: true });
                }
            } else if (name === 'sign-in' || name === 'switch') {
                // The sign-in modal records the focused element; the chip gets it back on close.
                this.close({ restoreFocus: true });
                this.app.openRemoteAccount();
            }
        });
        panel.addEventListener('focusout', event => {
            if (this.isOpen && event.relatedTarget && !this.host.contains(event.relatedTarget)) this.close();
        });
    }

    render(summary) {
        const doc = this.doc;
        if (!doc?.querySelectorAll) return;
        this.bind();
        const signedIn = summary.state !== 'signed-out';
        const keyOnly = summary.state === 'key-only';
        const managed = summary.state === 'managed';
        const host = this.host;
        if (host) host.hidden = !signedIn;
        if (!signedIn && this.isOpen) this.close();

        for (const avatar of doc.querySelectorAll('[data-account-avatar]')) {
            avatar.dataset.kind = keyOnly ? 'key' : 'person';
            const initial = avatar.querySelector('[data-account-initial]');
            if (initial) initial.textContent = summary.initial;
        }
        for (const name of doc.querySelectorAll('[data-account-org-name]')) name.textContent = summary.organization ?? '';

        const trigger = this.trigger;
        if (trigger && signedIn) {
            trigger.setAttribute('aria-label', accountChipLabel(summary));
            trigger.title = keyOnly ? 'API key configured' : summary.email;
            trigger.querySelector('[data-account-chip-label]').textContent = keyOnly ? 'API key' : summary.email;
            trigger.querySelector('[data-account-chip-org]').hidden = !managed;
        }

        const panel = this.panel;
        if (panel && signedIn) {
            const show = (selector, visible) => {
                for (const element of panel.querySelectorAll(selector)) element.hidden = !visible;
            };
            panel.querySelector('[data-account-menu-title]').textContent = keyOnly ? 'API key configured' : summary.email;
            show('[data-account-key-only]', keyOnly);
            show('[data-account-signed-in]', !keyOnly);
            show('[data-account-badge="personal"]', summary.state === 'personal');
            show('[data-account-badge="managed"]', managed);
            show('[data-account-org-block]', managed);
            panel.querySelector('[data-account-key]').textContent = summary.keyEnding ? `••••${summary.keyEnding}` : 'Saved';
            // A managed account's sign-in belongs to its organization, so switching is off.
            const switchAccount = panel.querySelector('[data-account-menu-action="switch"]');
            switchAccount.disabled = managed;
            if (managed) switchAccount.setAttribute('aria-describedby', 'app-account-switch-help');
            else switchAccount.removeAttribute('aria-describedby');
            show('[data-account-switch-help]', managed);
        }
    }

    toggle() {
        if (this.isOpen) this.close({ restoreFocus: true });
        else this.open();
    }

    open() {
        const { host, trigger, panel } = this;
        if (!panel || !trigger || host.hidden) return;
        this.isOpen = true;
        panel.hidden = false;
        trigger.setAttribute('aria-expanded', 'true');
        const onDocumentClick = event => {
            if (!this.host?.contains(event.target)) this.close();
        };
        const onKeydown = event => {
            // A confirmDialog overlay owns Escape while it is up (see utils.js).
            if (event.key !== 'Escape' || isConfirmDialogOpen()) return;
            const active = this.doc.activeElement;
            // Escape typed elsewhere, such as a focused terminal, keeps its meaning.
            if (active && active !== this.doc.body && !this.host?.contains(active)) {
                this.close();
                return;
            }
            event.preventDefault();
            this.close({ restoreFocus: true });
        };
        this.documentHandlers = { onDocumentClick, onKeydown };
        // Deferred so the opening click does not immediately close the popover.
        setTimeout(() => {
            if (this.documentHandlers?.onDocumentClick !== onDocumentClick) return;
            this.doc.addEventListener('click', onDocumentClick, true);
        }, 0);
        this.doc.addEventListener('keydown', onKeydown, true);
        panel.querySelector('button:not([disabled]):not([hidden]), a[href]:not([hidden])')?.focus();
    }

    close({ restoreFocus = false } = {}) {
        const { trigger, panel, documentHandlers } = this;
        this.isOpen = false;
        this.documentHandlers = null;
        if (documentHandlers) {
            this.doc.removeEventListener('click', documentHandlers.onDocumentClick, true);
            this.doc.removeEventListener('keydown', documentHandlers.onKeydown, true);
        }
        if (panel) panel.hidden = true;
        trigger?.setAttribute('aria-expanded', 'false');
        if (restoreFocus && trigger?.isConnected && !trigger.closest('[hidden]')) trigger.focus();
    }
}
