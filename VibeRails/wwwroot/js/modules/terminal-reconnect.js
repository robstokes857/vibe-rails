// Auto-reconnect policy for terminal tab WebSockets.
//
// Two situations leave a tab with a live PTY but no browser socket:
//
//   1. Navigation. app.loadView() destroys the TerminalManager (every xterm +
//      socket). On re-entry the restored manager reconnects the active tab at
//      once; every other restored tab is reconnected in the background by
//      TerminalManager.scheduleBackgroundReconnect() — one at a time, after the
//      active tab's replay has landed, with its xterm pinned to the active tab's
//      geometry (see resolveHiddenConnectGeometry) so the snapshot paints at the
//      width the PTY actually has.
//   2. A socket that closes on its own (proxy idle timeout, child hiccup,
//      sleep/wake). TerminalAutoReconnect retries with backoff, but never after a
//      takeover close: two viewers auto-reconnecting would steal the session from
//      each other forever, so a "taken over" close always waits for the user.
//
// Reconnect stays a plain snapshot replay (SubscribeWithSnapshot + the serializer's
// reset prologue). Nothing here pokes the PTY: no resize on a hidden tab beyond
// what the visible tab already reported, no Ctrl+L, no font bump.
//
// Kill switch: localStorage `viberails_terminal_autoReconnect` = 'off' restores
// the explicit Connect-button behaviour everywhere (Terminal settings → Rendering).

export const AUTO_RECONNECT_STORAGE_KEY = 'viberails_terminal_autoReconnect';

// First retry after an unexpected close; doubles per attempt, capped.
export const AUTO_RECONNECT_FIRST_DELAY_MS = 2000;
export const AUTO_RECONNECT_MAX_DELAY_MS = 30000;
// 2 + 4 + 8 + 16 + 30 s ≈ one minute of trying before the Connect button is the
// only way back. Bounded so a dead tab child (root answers 404) does not retry
// forever.
export const AUTO_RECONNECT_MAX_ATTEMPTS = 5;

// Background tabs after navigation: wait for the active tab's first replay flush
// (or this long), then settle, then connect the rest one by one with a gap between
// opens so the webview never parses several 20k-line snapshots in one burst.
export const BACKGROUND_RECONNECT_REPLAY_WAIT_MS = 4000;
export const BACKGROUND_RECONNECT_SETTLE_MS = 1000;
export const BACKGROUND_RECONNECT_GAP_MS = 250;

// Same sanity floor as the pre-connect fit check in terminal-tab.js: FitAddon can
// report tiny pre-layout grids; never pin a hidden tab to one of those.
export const MIN_TRUSTED_COLS = 32;
export const MIN_TRUSTED_ROWS = 8;

export const CLOSE_KIND = Object.freeze({
    RETRY: 'retry',
    TAKEOVER: 'takeover',
    SESSION_ENDED: 'session-ended',
    REJECTED: 'rejected',
    NO_SESSION: 'no-session'
});

const SESSION_ENDED_REASON = /session (exited|stopped|ended)|no active terminal session/i;
const TAKEOVER_REASON = /taken over/i;

export function isAutoReconnectEnabled(storage = undefined) {
    try {
        const store = storage === undefined ? globalThis.localStorage : storage;
        return store?.getItem?.(AUTO_RECONNECT_STORAGE_KEY) !== 'off';
    } catch {
        return true;
    }
}

// Decide what a socket close means for the tab. Close frames from the tab child
// are forwarded by the root proxy with their status and reason intact
// (TerminalTabHostService.ForwardCloseFrameAsync); a relay that ends without a
// child close frame arrives as NormalClosure "Done", and a rejected upgrade or a
// dropped connection as 1006 with no reason. All of those are worth a retry.
export function classifySocketClose({ code, reason, hasActiveSession } = {}) {
    if (hasActiveSession === false) {
        return CLOSE_KIND.NO_SESSION;
    }

    const text = typeof reason === 'string' ? reason : '';
    if (TAKEOVER_REASON.test(text)) {
        return CLOSE_KIND.TAKEOVER;
    }
    if (SESSION_ENDED_REASON.test(text)) {
        return CLOSE_KIND.SESSION_ENDED;
    }

    // 1007 InvalidPayloadData is the child's "No active terminal session" close;
    // 1008 PolicyViolation is the remote PIN lockout. Neither heals by retrying.
    if (code === 1007) {
        return CLOSE_KIND.SESSION_ENDED;
    }
    if (code === 1008) {
        return CLOSE_KIND.REJECTED;
    }

    return CLOSE_KIND.RETRY;
}

export function computeReconnectDelayMs(attempt, {
    firstDelayMs = AUTO_RECONNECT_FIRST_DELAY_MS,
    maxDelayMs = AUTO_RECONNECT_MAX_DELAY_MS
} = {}) {
    const step = Math.max(0, Math.min(Number.isFinite(attempt) ? Math.floor(attempt) : 0, 16));
    return Math.min(firstDelayMs * (2 ** step), maxDelayMs);
}

// The geometry a hidden tab must be pinned to before it connects. A display:none
// panel cannot be measured, so xterm would stay at its constructor default and
// the pre-connect fit path would ship 120x40 to the server — resizing the PTY to
// a size nothing on screen has. Every tab panel shares the same container and
// font, so the visible tab's last reported PTY geometry is exactly what fit()
// will compute once the hidden tab is shown. No trustworthy geometry ⇒ null,
// and the caller leaves the tab to connect on activation instead.
export function resolveHiddenConnectGeometry(activeInstance) {
    if (!activeInstance || typeof activeInstance.getLastResizeGeometry !== 'function') {
        return null;
    }
    if (typeof activeInstance.hasOpenSocket === 'function' && !activeInstance.hasOpenSocket()) {
        return null;
    }

    const geometry = activeInstance.getLastResizeGeometry();
    if (!geometry) {
        return null;
    }

    const cols = Number(geometry.cols);
    const rows = Number(geometry.rows);
    if (!Number.isFinite(cols) || !Number.isFinite(rows)
        || cols < MIN_TRUSTED_COLS || rows < MIN_TRUSTED_ROWS) {
        return null;
    }

    return { cols, rows };
}

function defaultIsPageVisible() {
    try {
        return typeof document === 'undefined' || document.visibilityState !== 'hidden';
    } catch {
        return true;
    }
}

function defaultSubscribeVisible(callback) {
    if (typeof document === 'undefined' || typeof document.addEventListener !== 'function') {
        return () => {};
    }
    const handler = () => {
        if (document.visibilityState !== 'hidden') {
            callback();
        }
    };
    document.addEventListener('visibilitychange', handler);
    return () => document.removeEventListener('visibilitychange', handler);
}

// Per-tab retry state. The owner reports every socket open/close it did not cause
// itself (terminal-tab.js nulls `this.socket` before a deliberate close, so those
// never reach handleClose). A retry only runs `attempt()`; success is observed
// through handleOpen(), failure through the next handleClose() — so a failed
// attempt naturally schedules the following one with a longer delay.
export class TerminalAutoReconnect {
    constructor({
        attempt,
        canAttempt = () => true,
        isEnabled = () => isAutoReconnectEnabled(),
        isPageVisible = defaultIsPageVisible,
        subscribeVisible = defaultSubscribeVisible,
        setTimeout: setTimeoutFn = (fn, ms) => globalThis.setTimeout(fn, ms),
        clearTimeout: clearTimeoutFn = (id) => globalThis.clearTimeout(id),
        maxAttempts = AUTO_RECONNECT_MAX_ATTEMPTS,
        firstDelayMs = AUTO_RECONNECT_FIRST_DELAY_MS,
        maxDelayMs = AUTO_RECONNECT_MAX_DELAY_MS
    } = {}) {
        if (typeof attempt !== 'function') {
            throw new TypeError('TerminalAutoReconnect requires an attempt() callback');
        }
        this._attempt = attempt;
        this._canAttempt = canAttempt;
        this._isEnabled = isEnabled;
        this._isPageVisible = isPageVisible;
        this._subscribeVisible = subscribeVisible;
        this._setTimeout = setTimeoutFn;
        this._clearTimeout = clearTimeoutFn;
        this._maxAttempts = maxAttempts;
        this._firstDelayMs = firstDelayMs;
        this._maxDelayMs = maxDelayMs;

        this._attempts = 0;
        this._timer = null;
        this._pendingDelayMs = 0;
        this._unsubscribeVisible = null;
    }

    get attempts() {
        return this._attempts;
    }

    isPending() {
        return this._timer !== null || this._unsubscribeVisible !== null;
    }

    // The socket opened — which is not yet proof that anything works. The root accepts a viewer
    // socket before it has connected to the tab child (TerminalTabsRoutes → TerminalTabHostService),
    // so a dead child still gives us an open socket that closes again a moment later. Zeroing the
    // budget here made that loop unbounded: every failure reset to attempt zero and retried
    // forever, despite the five-attempt limit. Cancel the armed retry, keep the count.
    handleOpen() {
        this.cancel();
    }

    // First bytes from the session (snapshot replay or live output). Now the whole path — root,
    // child, PTY — is proven, so the budget starts over.
    handleHealthy() {
        this._attempts = 0;
    }

    // Cancel any armed retry but keep the attempt count: a manual connect that
    // fails should not restart an unbounded loop.
    cancel() {
        if (this._timer !== null) {
            this._clearTimeout(this._timer);
            this._timer = null;
        }
        if (this._unsubscribeVisible) {
            try { this._unsubscribeVisible(); } catch { /* no-op */ }
            this._unsubscribeVisible = null;
        }
        this._pendingDelayMs = 0;
    }

    // Explicit user action (Connect / Reconnect button, a fresh session): cancel
    // and start the budget over.
    reset() {
        this.cancel();
        this._attempts = 0;
    }

    handleClose({ code, reason, hasActiveSession } = {}) {
        const kind = classifySocketClose({ code, reason, hasActiveSession });
        const result = { kind, scheduled: false, delayMs: 0, exhausted: false };
        if (kind !== CLOSE_KIND.RETRY) {
            return result;
        }
        if (!this._isEnabled()) {
            return result;
        }
        if (this.isPending()) {
            result.delayMs = this._pendingDelayMs;
            return result;
        }
        if (this._attempts >= this._maxAttempts) {
            result.exhausted = true;
            return result;
        }

        const delayMs = computeReconnectDelayMs(this._attempts, {
            firstDelayMs: this._firstDelayMs,
            maxDelayMs: this._maxDelayMs
        });
        this._attempts += 1;
        this._pendingDelayMs = delayMs;
        this._timer = this._setTimeout(() => {
            this._timer = null;
            this._pendingDelayMs = 0;
            this._fire();
        }, delayMs);

        result.scheduled = true;
        result.delayMs = delayMs;
        return result;
    }

    _fire() {
        if (!this._isEnabled() || !this._canAttempt()) {
            return;
        }
        if (!this._isPageVisible()) {
            // A hidden webview gets 1 s timer clamping and nobody is looking anyway;
            // try once the page is visible again.
            this._unsubscribeVisible = this._subscribeVisible(() => {
                const unsubscribe = this._unsubscribeVisible;
                this._unsubscribeVisible = null;
                try { unsubscribe?.(); } catch { /* no-op */ }
                if (this._isEnabled() && this._canAttempt()) {
                    this._attempt();
                }
            });
            return;
        }
        this._attempt();
    }
}
