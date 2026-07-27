/**
 * terminal-tab-status.js
 *
 * Self-contained tab status UI: state machine, brand logos, thinking spinner,
 * ready-flash, and responsive rendering. Consumed by terminal-multitab.js with
 * minimal wiring.
 */

import { getCliBrand } from './utils.js';

// ── Status constants ────────────────────────────────────────────────────────
export const TAB_STATUS = Object.freeze({
    CONNECTED:    'connected',
    THINKING:     'thinking',
    READY:        'ready',
    WAITING:      'waiting',
    DISCONNECTED: 'disconnected'
});

const STATUS_CLASS_PREFIX = 'tab-status-';

// Font Awesome icon classes per state. CONNECTED uses a small filled dot — a
// calm "session is live / idle" status LED. (Was fa-link; the chain-link read
// as a hyperlink, not a connection — Rob, 2026-06-12.) Kept distinct from
// READY's outlined circle-check so the two resting states don't look alike.
const STATUS_FA_ICON = {
    [TAB_STATUS.CONNECTED]:    'fa-solid fa-circle',
    [TAB_STATUS.READY]:        'fa-solid fa-circle-check',
    [TAB_STATUS.WAITING]:      'fa-solid fa-hand-point-up',
    [TAB_STATUS.DISCONNECTED]: 'fa-solid fa-plug-circle-xmark',
};

// These exact strings — "Thinking", "Ready", "Waiting for user input" — are
// deliberate product voice and a feature Rob actively reads off the tabs.
// Do NOT rename or hide them (a 2026-06-12 attempt to rename THINKING's text
// to "Working" everywhere was vetoed same day). The one sanctioned exception
// is the shell tab while THINKING — see SHELL_THINKING_TEXT below.
const STATUS_TEXT = {
    [TAB_STATUS.CONNECTED]:    'Connected',
    [TAB_STATUS.THINKING]:     'Thinking',
    [TAB_STATUS.READY]:        'Ready',
    [TAB_STATUS.WAITING]:      'Waiting for user input',
    [TAB_STATUS.DISCONNECTED]: 'Disconnected'
};

// Shell tabs alone say "Working" while THINKING — a dev server or build
// emitting output isn't "thinking", an agent is. Resolved per tab in
// _statusTextFor().
const SHELL_THINKING_TEXT = 'Working';

// ── Controller ──────────────────────────────────────────────────────────────
export class TabStatusController {
    /**
     * @param {object} tabState  - tab.state from TerminalManager
     * @param {object} ui        - { item, button, close } DOM refs
     * @param {object} options
     *   getCliKey:      () => string|null
     *   isActiveTab:    () => boolean
     *   setProgress:    (progress) => void   – calls manager.updateTabProgress
     *   notifyReady:    () => void           – fired when a *background* tab
     *                                          finishes its turn (→ READY), at
     *                                          the same point as the ready flash
     *   isPushEnabled:  () => boolean        – tab opted into push notifications
     *   notifyPush:     (statusKey) => void  – fired on READY/WAITING when opted
     *                                          in, regardless of focus. statusKey
     *                                          is 'ready' | 'waiting'.
     */
    constructor(tabState, ui, options) {
        this._tabState = tabState;
        this._ui = ui;
        this._options = options;

        this._status = null;
        this._awaitingFirstIdle = false;
        this._startAsThinking = false;
        // True while the user is typing an unsent message in a resting state
        // (CONNECTED/READY). Gates onWaitingForUserSelection so a stale
        // "waiting for user input" event can't flip the tab mid-keystroke.
        // Cleared on submit (→ THINKING). See onTerminalData.
        this._userComposing = false;

        // DOM refs (populated by render())
        this._statusTextEl = null;
        this._statusIconEl = null;
        this._brandLogoEl = null;
        this._labelEl = null;
        this._lastCliKey = undefined;
        this._lastPinned = undefined;
    }

    // ── Public API ──────────────────────────────────────────────────────

    render() {
        const button = this._ui.button;
        if (!button) return;

        const cliKey = this._options.getCliKey?.() ?? null;
        const brand = getCliBrand(cliKey);
        const pinned = this._tabState.pinned === true;

        // Set brand accent on the tab state so applyTabAccent() preserves it
        if (brand.accentColor && !this._tabState.accentColor) {
            this._tabState.accentColor = brand.accentColor;
        }

        // Idempotent path: TerminalManager.updateUi() calls renderTabButton()
        // for every tab on every UI tick. A full DOM rebuild here would tear
        // down the in-flight spinner/shimmer animations on every tick, which
        // looked like a flicker on the Thinking tab. Update only the bits
        // that actually mutate (label) and leave status visuals — managed by
        // _transitionTo / _applyVisuals on real status changes — alone.
        if (this._labelEl && this._lastCliKey === cliKey && this._lastPinned === pinned) {
            const nextLabel = this._tabState.label || 'Terminal';
            if (this._labelEl.textContent !== nextLabel) {
                this._labelEl.textContent = nextLabel;
            }
            this._syncButtonLabel();
            return;
        }

        this._lastCliKey = cliKey;
        this._lastPinned = pinned;
        button.innerHTML = '';

        // ── Left: Identity ──────────────────────────────────────────────
        const identity = document.createElement('span');
        identity.className = 'vb-tab-identity';

        // Brand logo is always shown — pinning adds a small pin badge *beside*
        // the logo (see below) rather than replacing it, so the tab keeps its
        // CLI identity while pinned.
        if (brand.logo) {
            const img = document.createElement('img');
            img.className = 'vb-tab-brand-logo';
            img.src = brand.logo;
            img.alt = '';
            img.width = 18;
            img.height = 18;
            if (brand.logoFilter) {
                img.style.filter = brand.logoFilter;
            }
            identity.appendChild(img);
            this._brandLogoEl = img;
        } else {
            const fallback = document.createElement('span');
            fallback.className = 'vb-tab-brand-logo vb-tab-brand-fallback';
            fallback.textContent = '>_';
            identity.appendChild(fallback);
            this._brandLogoEl = fallback;
        }

        // Pin badge — sits next to the brand logo when the tab is pinned.
        if (pinned) {
            const pin = document.createElement('span');
            pin.className = 'vb-tab-pin-badge';
            pin.setAttribute('aria-hidden', 'true');
            const i = document.createElement('i');
            i.className = 'fa-solid fa-thumbtack';
            pin.appendChild(i);
            identity.appendChild(pin);
        }

        const label = document.createElement('span');
        label.className = 'vb-terminal-tab-label';
        label.textContent = this._tabState.label || 'Terminal';
        identity.appendChild(label);
        this._labelEl = label;

        button.appendChild(identity);

        // ── Right: Status ───────────────────────────────────────────────
        const section = document.createElement('span');
        section.className = 'vb-tab-status-section';

        const text = document.createElement('span');
        text.className = 'vb-tab-status-text';
        section.appendChild(text);
        this._statusTextEl = text;

        const icon = document.createElement('span');
        icon.className = 'vb-tab-status-icon';
        section.appendChild(icon);
        this._statusIconEl = icon;

        button.appendChild(section);
        this._syncButtonLabel();

        if (this._status) {
            this._applyVisuals(this._status);
        }
    }

    onSocketOpen() {
        if (this._startAsThinking) {
            this._startAsThinking = false;
            this._transitionTo(TAB_STATUS.THINKING);
        } else {
            this._transitionTo(TAB_STATUS.CONNECTED);
        }
    }

    markResumedSession() {
        this._startAsThinking = true;
    }

    markInitialPrompt() {
        // The agent receives a prompt as argv at boot, so it begins processing
        // immediately. Same shape as markResumedSession — flag the controller so
        // onSocketOpen lands directly in THINKING instead of CONNECTED.
        this._startAsThinking = true;
    }

    onSocketClose() {
        this._transitionTo(TAB_STATUS.DISCONNECTED);
    }

    onTerminalData(data) {
        // xterm normally emits CR for Enter, but attached/remote input paths
        // can surface LF or CRLF. Treat all newline-only variants as the same
        // submit boundary so WAITING clears on the first Enter everywhere.
        const isSubmit = data === '\r' || data === '\n' || data === '\r\n';
        if (isSubmit &&
            (this._status === TAB_STATUS.CONNECTED ||
             this._status === TAB_STATUS.READY ||
             this._status === TAB_STATUS.WAITING)) {
            this._transitionTo(TAB_STATUS.THINKING);
            return;
        }
        if (data === '\x1b' && this._status === TAB_STATUS.THINKING) {
            this._transitionTo(TAB_STATUS.CONNECTED);
            return;
        }
        // Recognise a single printable byte (0x20–0x7E) as the user typing a
        // character. Narrowly scoped to avoid re-introducing the ACTIVE-state
        // false positives that motivated its retirement:
        //   - CSI sequences (arrow keys "\x1b[B", function keys, focus
        //     reports "\x1b[I/O", DSR auto-replies "\x1b[24;80R") have
        //     length > 1 and start with ESC — excluded.
        //   - Bracketed-paste wrappers ("\x1b[200~…\x1b[201~") and clipboard
        //     pastes arrive as one large multi-byte chunk — excluded.
        //
        // ASCII-only by design: xterm.js delivers user input to onData as
        // UTF-8 bytes packed into a JS string (one byte per char), so a
        // typed non-ASCII character ("é", "中", emoji) arrives as a 2–4
        // char chunk and falls out via `data.length === 1`. We accept that
        // gap. Widening beyond 0x7E would re-admit single-byte C1 controls
        // and Latin-1 glyphs that xterm.js could emit during DCS/OSC
        // responses, which is exactly the ACTIVE-state failure mode this avoids.
        const code = data.length === 1 ? data.charCodeAt(0) : -1;
        const isPrintableKeystroke = code >= 0x20 && code <= 0x7e;
        if (!isPrintableKeystroke) {
            return;
        }

        // In a resting state, a typed character means the user is composing an
        // unsent message. Mark it so a backend "waiting for user input" event
        // (which can false-fire on the composer repainting per keystroke —
        // session dd6819b6) doesn't flip the tab to WAITING mid-sentence. The
        // flag clears on submit (→ THINKING), so a genuine prompt — which only
        // appears after a submit — is never suppressed. THINKING is excluded on
        // purpose: type-ahead while the agent works must not arm the guard.
        if (this._status === TAB_STATUS.CONNECTED || this._status === TAB_STATUS.READY) {
            this._userComposing = true;
            return;
        }

        // If we were parked in WAITING, a typed character means the user is
        // already engaging — the "Codex is waiting for you" signal is stale.
        // Clear back to CONNECTED so the indicator reflects reality.
        if (this._status === TAB_STATUS.WAITING) {
            this._transitionTo(TAB_STATUS.CONNECTED);
        }
    }

    onSessionInput(kind) {
        const normalized = typeof kind === 'string' ? kind.toLowerCase() : '';

        if (normalized === 'submit' &&
            (this._status === TAB_STATUS.CONNECTED ||
             this._status === TAB_STATUS.READY ||
             this._status === TAB_STATUS.WAITING)) {
            this._transitionTo(TAB_STATUS.THINKING);
            return;
        }

        if (normalized !== 'printable') {
            return;
        }

        if (this._status === TAB_STATUS.CONNECTED || this._status === TAB_STATUS.READY) {
            this._userComposing = true;
            return;
        }

        if (this._status === TAB_STATUS.WAITING) {
            this._transitionTo(TAB_STATUS.CONNECTED);
        }
    }

    onSessionIdle() {
        // Shell tabs: backend idle (5s of no PTY output) means the command/server
        // went quiet — drop the spinner. Return to CONNECTED rather than READY so
        // we don't fire the "ready" flash/toast every time a dev server settles
        // between requests. Only act when we were actually spinning; idle pings in
        // CONNECTED (already quiet) are left alone.
        if (this._isShellTab()) {
            if (this._status === TAB_STATUS.THINKING) {
                this._transitionTo(TAB_STATUS.CONNECTED);
            }
            return;
        }

        if (this._awaitingFirstIdle) {
            this._transitionTo(TAB_STATUS.READY);
        }
        // A tab parked in WAITING stays WAITING until the user presses Enter.
        // The backend fires session_idle after only 5s of no PTY output — which
        // is the *normal* state of a menu waiting for user input — so we
        // previously popped WAITING → READY almost immediately after entering
        // it. Trust Enter as the sole signal that waiting is over.
    }

    onSessionBusy() {
        // Shell tabs only: trust backend PTY activity as the busy signal. A plain
        // shell has no agent "turn" to misread, so output (a dev server logging a
        // request, a build step) is exactly what the user wants the spinner to
        // track. Skip if already THINKING (no churn), DISCONNECTED, or WAITING — an
        // attention prompt must outlive incidental output (mirrors the agent rule;
        // shells don't enter WAITING today, so the WAITING guard is defensive).
        if (this._isShellTab() &&
            this._status !== TAB_STATUS.DISCONNECTED &&
            this._status !== TAB_STATUS.THINKING &&
            this._status !== TAB_STATUS.WAITING) {
            this._transitionTo(TAB_STATUS.THINKING);
            return;
        }
        // CLI/agent tabs: no-op. Enter detection via onTerminalData is the sole
        // trigger for entering THINKING. The backend fires session_busy on any PTY
        // activity (including the agent's own prompt redraws), so we cannot use it
        // to infer user intent there.
    }

    onWaitingForUserSelection() {
        // Don't flip to WAITING while the user is actively composing an unsent
        // message — they're plainly already engaged, and the backend detector
        // can fire on the composer repainting per keystroke (session dd6819b6).
        // _userComposing is set on a resting-state keystroke and cleared on
        // submit (→ THINKING), so a genuine prompt — which only appears after a
        // submit — is never suppressed.
        if (this._status !== TAB_STATUS.DISCONNECTED && !this._userComposing) {
            this._transitionTo(TAB_STATUS.WAITING);
        }
    }

    onSessionCompleted() {
        // A completed session means the process *exited* (the event carries an exit
        // code, and the dispatcher already pops a global "session completed" toast).
        // That's distinct from an agent "finished its turn and is waiting", so:
        //   • Shell tabs mirror their idle carve-out — settle THINKING → CONNECTED,
        //     never flashing/toasting READY (see onSessionIdle / onSessionBusy).
        //   • Agent tabs still flash READY, but we suppress the in-panel "is ready"
        //     toast (notify:false): completion is already surfaced globally, and
        //     "ready" misreads a process that just exited.
        if (this._isShellTab()) {
            if (this._status === TAB_STATUS.THINKING) {
                this._transitionTo(TAB_STATUS.CONNECTED);
            }
            return;
        }
        if (this._awaitingFirstIdle) {
            this._transitionTo(TAB_STATUS.READY, { notify: false });
        }
    }

    dispose() {
        this._statusTextEl = null;
        this._statusIconEl = null;
        this._brandLogoEl = null;
        this._labelEl = null;
        this._lastCliKey = undefined;
    }

    // ── Internal ────────────────────────────────────────────────────────

    // The plain shell tab (CLI key 'shell') is the one place we trust the
    // backend busy/idle signals to drive the spinner — see onSessionBusy /
    // onSessionIdle. Agent tabs keep the Enter-driven heuristic.
    _isShellTab() {
        return this._options.getCliKey?.() === 'shell';
    }

    _statusTextFor(status) {
        if (status === TAB_STATUS.THINKING && this._isShellTab()) {
            return SHELL_THINKING_TEXT;
        }
        return STATUS_TEXT[status] || '';
    }

    _transitionTo(newStatus, { notify = true } = {}) {
        const prev = this._status;
        if (prev === newStatus) {
            if (newStatus === TAB_STATUS.WAITING) {
                this._applyVisuals(newStatus);
                if (!this._options.isActiveTab?.()) {
                    this._flashWaiting();
                }
            }
            return;
        }

        this._status = newStatus;

        if (newStatus === TAB_STATUS.THINKING) {
            this._awaitingFirstIdle = true;
            // Submitting (the path into THINKING) ends a compose session, so a
            // prompt during the upcoming turn is a genuine wait, not stale.
            this._userComposing = false;
        } else if (newStatus === TAB_STATUS.READY) {
            this._awaitingFirstIdle = false;
        } else if (newStatus === TAB_STATUS.CONNECTED) {
            this._awaitingFirstIdle = false;
        } else if (newStatus === TAB_STATUS.WAITING) {
            // The user (not a backend idle ping) drives the next transition.
            this._awaitingFirstIdle = false;
        }

        this._applyVisuals(newStatus);

        // Progress bar: indeterminate during thinking, off otherwise
        if (newStatus === TAB_STATUS.THINKING) {
            this._options.setProgress?.({ state: 3, value: 0 });
        } else if (prev === TAB_STATUS.THINKING) {
            this._options.setProgress?.({ state: 0, value: 0 });
        }

        // Ready flash — only for background tabs
        if (newStatus === TAB_STATUS.READY && !this._options.isActiveTab?.()) {
            this._flashReady();
            // The completion path passes notify:false (see onSessionCompleted) so
            // an exited process doesn't also pop the "is ready" toast.
            if (notify) {
                this._options.notifyReady?.();
            }
        }

        // Waiting flash — only for background tabs (active tab gets the
        // pulsing icon instead, which is enough in-view signal).
        if (newStatus === TAB_STATUS.WAITING && !this._options.isActiveTab?.()) {
            this._flashWaiting();
        }

        // Opt-in push notification (fires regardless of focus — the per-tab watch
        // toggle (eye) is the gate). READY respects the `notify` flag so a process *exit* (which passes
        // notify:false via onSessionCompleted) doesn't push a misleading "Ready".
        if (this._options.isPushEnabled?.()) {
            if (newStatus === TAB_STATUS.READY && notify) {
                this._options.notifyPush?.('ready');
            } else if (newStatus === TAB_STATUS.WAITING) {
                this._options.notifyPush?.('waiting');
            }
        }
    }

    _applyVisuals(status) {
        const item = this._ui.item;
        if (!item) return;

        for (const s of Object.values(TAB_STATUS)) {
            item.classList.toggle(STATUS_CLASS_PREFIX + s, s === status);
        }
        item.classList.remove('tab-idle', 'tab-busy', 'tab-completed', 'is-connected', 'is-disconnected');

        if (this._statusTextEl) {
            this._statusTextEl.textContent = this._statusTextFor(status);
        }
        this._syncButtonLabel(status);

        // Icon area
        if (this._statusIconEl) {
            this._statusIconEl.innerHTML = '';
            this._statusIconEl.className = 'vb-tab-status-icon';

            if (status === TAB_STATUS.THINKING) {
                const spinner = document.createElement('span');
                spinner.className = 'vb-spinner vb-spinner-sm';
                spinner.setAttribute('role', 'status');
                spinner.setAttribute('aria-label', this._statusTextFor(TAB_STATUS.THINKING));
                this._statusIconEl.appendChild(spinner);
            } else {
                const faClass = STATUS_FA_ICON[status];
                if (faClass) {
                    const i = document.createElement('i');
                    i.className = faClass;
                    this._statusIconEl.appendChild(i);
                }
            }
        }
    }

    _syncButtonLabel(status = this._status) {
        const button = this._ui.button;
        if (!button) return;

        const label = this._tabState.label || 'Terminal';
        const statusText = status ? this._statusTextFor(status) : '';
        const titleParts = [label];
        if (this._tabState.pinned === true) {
            titleParts.push('Pinned');
        }
        if (statusText) {
            titleParts.push(statusText);
        }
        const title = titleParts.join(' - ');
        button.title = title;
        if (typeof button.setAttribute === 'function') {
            button.setAttribute('aria-label', titleParts.join(', '));
        }
    }

    _flashReady() {
        const item = this._ui.item;
        if (!item) return;

        item.classList.remove('tab-status-ready-flash');
        void item.offsetHeight;
        item.classList.add('tab-status-ready-flash');
        item.addEventListener('animationend', () => {
            item.classList.remove('tab-status-ready-flash');
        }, { once: true });
    }

    _flashWaiting() {
        const item = this._ui.item;
        if (!item) return;

        item.classList.remove('tab-status-waiting-flash');
        void item.offsetHeight;
        item.classList.add('tab-status-waiting-flash');
        item.addEventListener('animationend', () => {
            item.classList.remove('tab-status-waiting-flash');
        }, { once: true });
    }
}
