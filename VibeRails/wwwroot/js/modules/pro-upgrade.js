// The hosted Pro features (remote Boards, remote terminals, session sharing, card sharing) and
// the one sign-in / upgrade prompt they share (VIBE-169). The hosted server decides; this module
// only reads the local backend's answer and never locks anything it cannot confirm.
import { escapeHtml } from './utils.js';

const ENTITLEMENTS_API = '/api/v1/account/entitlements';
const BILLING_PAGE = 'https://viberails.ai/Billing';
const LOCAL_NOTE = 'Everything local stays free, and nothing was deleted.';

// name starts a sentence; plural picks its verb.
const FEATURES = {
    remote_boards: { name: 'Remote Boards', plural: true },
    remote_terminals: { name: 'Remote terminals', plural: true },
    session_sharing: { name: 'Session sharing' },
    card_sharing: { name: 'Card sharing' }
};

// Only a plain page address is kept: the feature name is the one thing ever added to it.
function billingPage(value) {
    try {
        const url = new URL(value);
        if (/^https?:$/.test(url.protocol) && !url.username && !url.password && !url.search && !url.hash) {
            return url.origin + url.pathname;
        }
    } catch { /* Absent or unusable: the public page. */ }
    return BILLING_PAGE;
}

/**
 * The local backend's entitlement answer in one shape. Anything unreadable (an older backend,
 * a failed request) is "unknown", which reports every feature available and locks nothing.
 */
export function normalizeEntitlements(raw) {
    const state = ['signed_out', 'free', 'pro', 'inactive'].includes(raw?.state) ? raw.state : 'unknown';
    return {
        state,
        enforced: raw?.enforced === true,
        features: Object.fromEntries(Object.keys(FEATURES).map(feature =>
            [feature, state === 'unknown' || raw.features?.[feature] !== false])),
        billingUrl: billingPage(raw?.billingUrl),
        checkedUtc: raw?.checkedUtc ?? null
    };
}

export const unknownEntitlements = () => normalizeEntitlements(null);

/** Reads the entitlement state; force asks the backend to check the hosted server again. */
export async function readEntitlements(app, force = false) {
    try {
        return normalizeEntitlements(await app.apiCall(force ? `${ENTITLEMENTS_API}/refresh` : ENTITLEMENTS_API,
            force ? 'POST' : 'GET', null, { showLoading: false }));
    } catch {
        return unknownEntitlements();
    }
}

export function isProFeatureAvailable(entitlements, feature) {
    return entitlements?.features?.[feature] !== false;
}

/** The Billing page for one feature. The query names the feature and carries nothing else. */
export function billingUrl(entitlements, feature) {
    return `${billingPage(entitlements?.billingUrl)}?feature=${feature.replaceAll('_', '-')}`;
}

/** A "Pro" badge beside a locked control. app.updateProBadges() follows later plan changes. */
export function proBadge(app, feature) {
    return `<span class="badge pro-badge" data-pro-badge="${feature}" title="Part of Vibe Rails Pro"${
        isProFeatureAvailable(app?.entitlements, feature) ? ' hidden' : ''}>Pro</span>`;
}

export function updateProBadges(app, root = globalThis.document) {
    for (const badge of root?.querySelectorAll?.('[data-pro-badge]') || []) {
        badge.hidden = isProFeatureAvailable(app.entitlements, badge.dataset.proBadge);
    }
}

/**
 * What the prompt says for one feature: signed out, free, an inactive subscription, or a Board
 * whose owner pays. A denial that contradicts the local copy reads as free for a saved account.
 */
export function proUpgradeView(app, feature, { payer } = {}) {
    const { name, plural } = FEATURES[feature];
    const lower = name[0].toLowerCase() + name.slice(1);
    const are = plural ? 'are' : 'is';
    if (payer === 'board_owner') {
        return {
            state: 'board_owner', title: "This Board's owner needs Vibe Rails Pro",
            body: 'Syncing and sharing this Board are paused until its owner upgrades. You do not need to upgrade.'
        };
    }
    const state = app.entitlements?.state;
    const url = billingUrl(app.entitlements, feature);
    if (state === 'inactive') {
        return {
            state, title: 'Your Vibe Rails Pro subscription is inactive', primary: 'Manage billing', url,
            body: `${name} ${are} paused until the subscription is active again. Billing opens on viberails.ai in your browser.`
        };
    }
    if (state === 'signed_out' || (state !== 'free' && !app.appSettings?.apiKey)) {
        return {
            state: 'signed_out', title: `Sign in to use ${lower}`, primary: 'Sign in',
            body: `${name} ${are} part of Vibe Rails Pro. Sign in to continue; the same page also creates an account.`
        };
    }
    return {
        state: 'free', title: `${name} ${plural ? 'need' : 'needs'} Vibe Rails Pro`, primary: 'Upgrade to Pro', url,
        body: `Upgrade your account to use ${lower}. Billing opens on viberails.ai in your browser.`
    };
}

function dialogHtml(feature, view) {
    const primary = view.url
        ? `<a class="btn btn-primary" href="${escapeHtml(view.url)}" target="_blank" rel="noopener noreferrer" data-pro-upgrade-billing>${view.primary}</a>`
        : view.primary ? `<button type="button" class="btn btn-primary" data-pro-upgrade-sign-in>${view.primary}</button>` : '';
    return `<div data-pro-upgrade="${feature}" data-pro-upgrade-state="${view.state}">
        <p class="text-muted">${view.body}</p>
        <p class="small text-muted">${LOCAL_NOTE}</p>
        <p class="small mb-0" role="status" aria-live="polite" data-pro-upgrade-status></p>
        <div class="d-flex flex-wrap gap-2 justify-content-end mt-3">
            <button type="button" class="btn ${primary ? 'btn-outline-secondary' : 'btn-secondary'}" data-action="close-modal">${primary ? 'Not now' : 'Close'}</button>
            ${view.url ? '<button type="button" class="btn btn-secondary" data-pro-upgrade-check hidden>Check again</button>' : ''}
            ${primary}
        </div>
    </div>`;
}

/**
 * Opens the prompt for one feature. It replaces any open app.showModal dialog, so callers inside
 * an editor ask only once nothing unsaved would be lost.
 */
export function openProUpgrade(app, feature, { payer } = {}) {
    if (!FEATURES[feature]) return;
    const view = proUpgradeView(app, feature, { payer });
    if (app.showModal(view.title, dialogHtml(feature, view)) === false) {
        // A dialog with unsaved work stayed open; still say why the action did not run.
        app.showToast?.(view.title, `${view.body} ${LOCAL_NOTE}`, 'warning');
        return;
    }
    const host = globalThis.document?.querySelector('#modal-container [data-pro-upgrade]');
    if (!host) return;
    host.closest?.('.modal-dialog')?.classList.add('pro-upgrade-dialog');
    const status = host.querySelector('[data-pro-upgrade-status]');
    const check = host.querySelector('[data-pro-upgrade-check]');

    // Only the backend's fresh answer counts; nothing in a URL or a return visit proves payment.
    async function checkAgain() {
        if (check.disabled) return;
        check.disabled = true;
        status.textContent = 'Checking your plan…';
        const entitlements = await app.refreshEntitlements({ force: true });
        if (!host.isConnected) return;
        check.disabled = false;
        if (entitlements.state === 'unknown') {
            status.textContent = 'Could not check your plan. Check your connection and try again.';
        } else if (!isProFeatureAvailable(entitlements, feature)) {
            status.textContent = 'Vibe Rails Pro is not active on this account yet. Finish in your browser, then check again.';
        } else {
            app.closeModal();
            const { name, plural } = FEATURES[feature];
            app.showToast('Vibe Rails Pro', `${name} ${plural ? 'are' : 'is'} ready to use.`, 'success');
        }
    }

    host.addEventListener('click', event => {
        if (event.target.closest('[data-pro-upgrade-sign-in]')) {
            app.openRemoteAccount();
        } else if (event.target.closest('[data-pro-upgrade-billing]')) {
            // The anchor is the browser fallback; the VS Code webview opens pages through its host.
            if (typeof globalThis.window?.__viberails_openExternal__ === 'function') {
                try {
                    window.__viberails_openExternal__(view.url);
                    event.preventDefault();
                } catch { /* Let the same click follow the anchor. */ }
            }
            check.hidden = false;
        } else if (event.target.closest('[data-pro-upgrade-check]')) {
            void checkAgain();
        }
    });
}

/** True when the feature may run; otherwise opens the prompt and returns false. */
export function requirePro(app, feature) {
    if (isProFeatureAvailable(app.entitlements, feature)) return true;
    openProUpgrade(app, feature);
    return false;
}

/**
 * Opens the prompt when a response body or a thrown apiCall error is the server's pro_required
 * denial, and returns whether it was one.
 */
export function showProRequired(app, feature, denial) {
    if (denial?.code !== 'pro_required' && denial?.status !== 'pro_required') return false;
    // The server knows better than a stale local copy, and the backend has noted the refusal:
    // read it again so badges and requirePro agree.
    if (denial.payer !== 'board_owner' && isProFeatureAvailable(app.entitlements, feature)) {
        void app.refreshEntitlements?.();
    }
    openProUpgrade(app, feature, { payer: denial.payer });
    return true;
}
