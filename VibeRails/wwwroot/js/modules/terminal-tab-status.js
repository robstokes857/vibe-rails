/**
 * terminal-tab-status.js
 *
 * Self-contained tab status UI: state machine, brand logos, thinking emoji
 * animation, ready-flash, and responsive rendering. Consumed by
 * terminal-multitab.js with minimal wiring.
 */

import { getCliBrand } from './utils.js';

// ── Status constants ────────────────────────────────────────────────────────
export const TAB_STATUS = Object.freeze({
    CONNECTED:    'connected',
    THINKING:     'thinking',
    READY:        'ready',
    DISCONNECTED: 'disconnected'
});

const STATUS_CLASS_PREFIX = 'tab-status-';

// Large pool of thinking emoji — picked at random, never repeats back-to-back
const THINKING_EMOJI = [
    '\u{1F9E0}', // brain
    '\u{1F680}', // rocket
    '\u{26A1}',  // lightning
    '\u{1F4A1}', // lightbulb
    '\u{1F52E}', // crystal ball
    '\u{2728}',  // sparkles
    '\u{1F916}', // robot
    '\u{1F3AF}', // bullseye
    '\u{1F525}', // fire
];

// Font Awesome icon classes per state
const STATUS_FA_ICON = {
    [TAB_STATUS.CONNECTED]:    'fa-solid fa-link',
    [TAB_STATUS.READY]:        'fa-solid fa-circle-check',
    [TAB_STATUS.DISCONNECTED]: 'fa-solid fa-plug-circle-xmark',
};

const STATUS_TEXT = {
    [TAB_STATUS.CONNECTED]:    'Connected',
    [TAB_STATUS.THINKING]:     'Thinking',
    [TAB_STATUS.READY]:        'Ready',
    [TAB_STATUS.DISCONNECTED]: 'Disconnected'
};

// ── Controller ──────────────────────────────────────────────────────────────
export class TabStatusController {
    /**
     * @param {object} tabState  - tab.state from TerminalManager
     * @param {object} ui        - { item, button, close } DOM refs
     * @param {object} options
     *   getCliKey:      () => string|null
     *   isActiveTab:    () => boolean
     *   setProgress:    (progress) => void   – calls manager.updateTabProgress
     */
    constructor(tabState, ui, options) {
        this._tabState = tabState;
        this._ui = ui;
        this._options = options;

        this._status = null;
        this._awaitingFirstIdle = false;

        // Thinking emoji animation state
        this._emojiTimer = null;
        this._lastEmojiIndex = -1;

        // DOM refs (populated by render())
        this._statusTextEl = null;
        this._statusIconEl = null;
        this._brandLogoEl = null;
        this._labelEl = null;
    }

    // ── Public API ──────────────────────────────────────────────────────

    render() {
        const button = this._ui.button;
        if (!button) return;

        button.innerHTML = '';

        // ── Left: Identity ──────────────────────────────────────────────
        const identity = document.createElement('span');
        identity.className = 'vb-tab-identity';

        const brand = getCliBrand(this._options.getCliKey?.());
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

        // Set brand accent on the tab state so applyTabAccent() preserves it
        if (brand.accentColor && !this._tabState.accentColor) {
            this._tabState.accentColor = brand.accentColor;
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
        button.title = this._tabState.label || 'Terminal';

        if (this._status) {
            this._applyVisuals(this._status);
        }
    }

    onSocketOpen() {
        this._transitionTo(TAB_STATUS.CONNECTED);
    }

    onSocketClose() {
        this._transitionTo(TAB_STATUS.DISCONNECTED);
    }

    onTerminalData(data) {
        if (data === '\r' &&
            (this._status === TAB_STATUS.CONNECTED || this._status === TAB_STATUS.READY)) {
            this._transitionTo(TAB_STATUS.THINKING);
        } else if (data === '\x1b' && this._status === TAB_STATUS.THINKING) {
            this._transitionTo(TAB_STATUS.CONNECTED);
        }
    }

    onSessionIdle() {
        if (this._awaitingFirstIdle) {
            this._transitionTo(TAB_STATUS.READY);
        }
    }

    onSessionBusy() {
        // No-op. Enter detection via onTerminalData is the sole trigger for
        // THINKING. The backend fires session_busy on any PTY activity (including
        // initial prompt output), so we cannot use it to infer user intent.
    }

    onSessionCompleted() {
        if (this._awaitingFirstIdle) {
            this._transitionTo(TAB_STATUS.READY);
        }
    }

    dispose() {
        this._stopEmojiCycle();
        this._statusTextEl = null;
        this._statusIconEl = null;
        this._brandLogoEl = null;
        this._labelEl = null;
    }

    // ── Internal ────────────────────────────────────────────────────────

    _transitionTo(newStatus) {
        const prev = this._status;
        if (prev === newStatus) return;

        this._status = newStatus;

        if (newStatus === TAB_STATUS.THINKING) {
            this._awaitingFirstIdle = true;
        } else if (newStatus === TAB_STATUS.READY) {
            this._awaitingFirstIdle = false;
        } else if (newStatus === TAB_STATUS.CONNECTED) {
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
            this._statusTextEl.textContent = STATUS_TEXT[status] || '';
        }

        // Icon area
        this._stopEmojiCycle();
        if (this._statusIconEl) {
            if (status === TAB_STATUS.THINKING) {
                this._statusIconEl.innerHTML = '';
                this._startEmojiCycle();
            } else {
                this._statusIconEl.innerHTML = '';
                this._statusIconEl.className = 'vb-tab-status-icon';
                const faClass = STATUS_FA_ICON[status];
                if (faClass) {
                    const i = document.createElement('i');
                    i.className = faClass;
                    this._statusIconEl.appendChild(i);
                }
            }
        }
    }

    // ── Thinking emoji cycle (JS-driven, one at a time, random) ─────────

    _startEmojiCycle() {
        this._showNextEmoji();
        this._emojiTimer = setInterval(() => this._showNextEmoji(), 2000);
    }

    _stopEmojiCycle() {
        if (this._emojiTimer) {
            clearInterval(this._emojiTimer);
            this._emojiTimer = null;
        }
    }

    _showNextEmoji() {
        const el = this._statusIconEl;
        if (!el) return;

        // Pick random, never same as last
        let idx;
        do {
            idx = Math.floor(Math.random() * THINKING_EMOJI.length);
        } while (idx === this._lastEmojiIndex && THINKING_EMOJI.length > 1);
        this._lastEmojiIndex = idx;

        const emoji = THINKING_EMOJI[idx];

        // If there's already content, animate it out then bring new one in
        if (el.textContent) {
            el.className = 'vb-tab-status-icon vb-emoji-exit';
            setTimeout(() => {
                el.textContent = emoji;
                el.className = 'vb-tab-status-icon vb-emoji-enter';
            }, 300);
        } else {
            el.textContent = emoji;
            el.className = 'vb-tab-status-icon vb-emoji-enter';
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
}
