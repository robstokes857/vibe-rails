import { escapeHtml, escapeHtmlWithLineBreaks } from './utils.js';

/**
 * Terminal-scoped toast overlay.
 *
 * Toasts are rendered into the ACTIVE tab's overlay layer
 * (`state.ui.toastLayer`), which is a *sibling* of the xterm host element
 * (`.vb-terminal-element`) inside the per-tab panel — deliberately NOT a child
 * of it. xterm's FitAddon measures `.vb-terminal-element` to derive cols/rows,
 * so injecting DOM there would corrupt sizing and "break the terminal". By
 * living one level up (the panel) as an absolutely-positioned overlay we:
 *   - take no layout space → never trigger a reflow / SIGWINCH;
 *   - keep `pointer-events: none` on the layer so clicks fall through to the
 *     terminal everywhere except on the toast cards themselves;
 *   - never touch focus, xterm, or the WebSocket.
 *
 * Two surfaces, same options:
 *   showToast(message, opts)      → full-width banner under the tab strip
 *   showSmallToast(message, opts) → compact card at the terminal's top-right
 *
 * opts: {
 *   type: 'info' | 'success' | 'warning' | 'error'  (default 'info')
 *   durationSec: number   how long before auto-close          (default 10)
 *   autoClose: boolean    auto-dismiss after durationSec       (default true)
 *   onClose: (reason) => void   'manual' | 'auto' | 'action'
 *   action: { label, onClick }  optional inline action button
 * }
 * Returns a { close } handle, or null when there is no active terminal to
 * render over.
 */

const TOAST_TYPES = new Set(['info', 'success', 'warning', 'error']);
const DEFAULT_DURATION_SEC = 10;
const EXIT_ANIMATION_MS = 180;

const TYPE_ICON = Object.freeze({
    info: 'fa-circle-info',
    success: 'fa-circle-check',
    warning: 'fa-triangle-exclamation',
    error: 'fa-circle-xmark'
});

function normalizeType(type) {
    return TOAST_TYPES.has(type) ? type : 'info';
}

export class TerminalToast {
    constructor(manager) {
        this.manager = manager;
        // Track outstanding removal/auto-close timers so destroy() can cancel
        // them and we never write to a torn-down DOM.
        this._timers = new Set();
        // Track the live card elements so dispose() can remove any still on
        // screen — cancelling their timers alone would orphan the DOM nodes in
        // the (possibly reused) tab panel.
        this._cards = new Set();
    }

    showToast(message, options = {}) {
        return this._show(message, options, 'banner');
    }

    showSmallToast(message, options = {}) {
        return this._show(message, options, 'small');
    }

    // The layer lives on whichever tab is active right now. If there's no active
    // tab/session there's nothing to render over — callers get null, not a throw.
    _activeLayer() {
        const active = this.manager?.getActiveTab?.();
        return active?.state?.ui?.toastLayer || null;
    }

    _show(message, options, variant) {
        const layer = this._activeLayer();
        if (!layer) {
            return null;
        }

        const {
            type = 'info',
            durationSec = DEFAULT_DURATION_SEC,
            autoClose = true,
            onClose = null,
            action = null
        } = options || {};

        const resolvedType = normalizeType(type);
        const region = this._ensureRegion(layer, variant);

        const card = document.createElement('div');
        card.className = `vb-term-toast vb-term-toast-${variant} vb-term-toast-${resolvedType}`;
        card.setAttribute('role', resolvedType === 'error' || resolvedType === 'warning' ? 'alert' : 'status');

        const safeMessage = escapeHtmlWithLineBreaks(message);
        const actionHtml = action?.label
            ? `<button type="button" class="vb-term-toast-action">${escapeHtml(action.label)}</button>`
            : '';
        card.innerHTML = `
            <i class="fa-solid ${TYPE_ICON[resolvedType]} vb-term-toast-icon" aria-hidden="true"></i>
            <span class="vb-term-toast-msg">${safeMessage}</span>
            ${actionHtml}
            <button type="button" class="vb-term-toast-close" aria-label="Dismiss">&times;</button>
        `;

        let autoTimer = null;
        let closed = false;
        const close = (reason = 'manual') => {
            if (closed) {
                return;
            }
            closed = true;
            if (autoTimer) {
                this._timers.delete(autoTimer);
                clearTimeout(autoTimer);
            }
            card.classList.add('vb-term-toast-leaving');
            card.classList.remove('vb-term-toast-in');
            const removeTimer = setTimeout(() => {
                this._timers.delete(removeTimer);
                this._cards.delete(card);
                card.remove();
                try { onClose?.(reason); } catch (_) { /* no-op */ }
            }, EXIT_ANIMATION_MS);
            this._timers.add(removeTimer);
        };

        card.querySelector('.vb-term-toast-close')
            ?.addEventListener('click', () => close('manual'));
        if (action?.onClick) {
            card.querySelector('.vb-term-toast-action')
                ?.addEventListener('click', () => {
                    try { action.onClick(); } finally { close('action'); }
                });
        }

        region.appendChild(card);
        this._cards.add(card);
        // Trigger the entry transition on the next frame (after the card is in
        // the DOM with its initial off-state styles applied).
        requestAnimationFrame(() => card.classList.add('vb-term-toast-in'));

        if (autoClose) {
            // Only a positive, finite durationSec is honored; 0 / NaN / negative
            // fall back to the default. (Plain `Number(x) || DEFAULT` would also
            // swallow an explicit 0, silently turning it into the 10s default.)
            const seconds = Number(durationSec);
            const ms = (Number.isFinite(seconds) && seconds > 0 ? seconds : DEFAULT_DURATION_SEC) * 1000;
            autoTimer = setTimeout(() => {
                this._timers.delete(autoTimer);
                close('auto');
            }, ms);
            this._timers.add(autoTimer);
        }

        return { close: () => close('manual') };
    }

    // Banner and small toasts live in separate stacking regions within the same
    // layer so they don't fight for the same corner. Created lazily on first use.
    _ensureRegion(layer, variant) {
        const cls = variant === 'small'
            ? 'vb-term-toast-region-small'
            : 'vb-term-toast-region-banner';
        let region = layer.querySelector(`.${cls}`);
        if (!region) {
            region = document.createElement('div');
            region.className = `vb-term-toast-region ${cls}`;
            layer.appendChild(region);
        }
        return region;
    }

    dispose() {
        for (const timer of this._timers) {
            clearTimeout(timer);
        }
        this._timers.clear();
        // Timers are cancelled above, so the deferred card.remove() in close()
        // will never run — pull any still-visible cards out of the DOM now so a
        // torn-down/rebound manager doesn't leave a stale toast in the panel.
        for (const card of this._cards) {
            card.remove();
        }
        this._cards.clear();
    }
}
