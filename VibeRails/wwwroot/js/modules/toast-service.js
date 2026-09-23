import { escapeHtml, escapeHtmlWithLineBreaks } from './utils.js';

const DEFAULT_THEME = 'dark';
const THEME_STORAGE_KEY = 'viberails.toast.theme';
const THEMES = new Set(['dark', 'light', 'system']);

export function getToastTheme() {
    try {
        const stored = localStorage.getItem(THEME_STORAGE_KEY);
        return THEMES.has(stored) ? stored : DEFAULT_THEME;
    } catch {
        return DEFAULT_THEME;
    }
}

export function setToastTheme(theme) {
    const resolved = THEMES.has(theme) ? theme : DEFAULT_THEME;
    try { localStorage.setItem(THEME_STORAGE_KEY, resolved); } catch { /* Storage may be disabled. */ }
    return resolved;
}
const DEFAULT_POSITION = 'top-right';
const DEFAULT_ENTRY_ANIMATION = 'fadeIn';
const DEFAULT_EXIT_ANIMATION = 'fadeOut';

const DEFAULT_DURATIONS = Object.freeze({
    info: 5000,
    success: 4000,
    warning: 6000,
    error: 7000
});

const BASE_TOAST_OPTIONS = Object.freeze({
    borderRadius: '8px',
    fontSize: '13px',
    fontFamily: '\'Inter\', -apple-system, sans-serif',
    showIcon: true,
    showCloseButton: true,
    progressBarPosition: 'bottom',
    progressBarHeight: '2px',
    iconAnimation: 'default'
});

const TONES = Object.freeze({
    info: '#60a5fa',
    success: '#34d399',
    warning: '#fbbf24',
    error: '#fb7185'
});

const TOAST_TYPES = Object.freeze(['info', 'success', 'warning', 'error']);

function normalizeToastType(type) {
    const normalizedType = (type || 'info').toLowerCase();
    return TOAST_TYPES.includes(normalizedType) ? normalizedType : 'info';
}

function formatToastMessage(title, message, { compact = false } = {}) {
    const safeTitle = escapeHtml(title || 'Notification');
    const safeMessage = escapeHtml(message || '');
    const formattedMessage = escapeHtmlWithLineBreaks(message || '');
    const modifier = compact ? ' vr-toast-compact' : '';

    if (!safeMessage) {
        return `<span class="vr-toast-title vr-toast-title-only${modifier}">${safeTitle}</span>`;
    }

    return `<span class="vr-toast-title${modifier}">${safeTitle}</span><span class="vr-toast-body${modifier}">${formattedMessage}</span>`;
}

export function showAppToast(title, message, type = 'info', options = {}) {
    const {
        icon,
        iconBackground,
        iconColor,
        theme = getToastTheme(),
        duration,
        autoClose,
        requireDismiss = false,
        animation,
        entryAnimation,
        exitAnimation,
        position,
        compact = false
    } = options;

    const toastType = normalizeToastType(type);
    const accent = TONES[toastType];
    const shouldAutoClose = autoClose === false ? false : !requireDismiss;
    const baseDuration = DEFAULT_DURATIONS[toastType];
    const compactDuration = Math.min(baseDuration, 3000);
    const resolvedDuration = shouldAutoClose
        ? (duration ?? (compact ? compactDuration : baseDuration))
        : 0;
    const resolvedPosition = position ?? (compact ? 'bottom-right' : DEFAULT_POSITION);
    const resolvedTheme = `vr-${THEMES.has(theme) ? theme : DEFAULT_THEME}`;

    const toastOptions = {
        ...BASE_TOAST_OPTIONS,
        message: formatToastMessage(title, message, { compact }),
        position: resolvedPosition,
        duration: resolvedDuration,
        autoClose: shouldAutoClose,
        theme: resolvedTheme,
        entryAnimation: entryAnimation ?? animation?.enter ?? DEFAULT_ENTRY_ANIMATION,
        exitAnimation: exitAnimation ?? animation?.exit ?? DEFAULT_EXIT_ANIMATION,
        iconType: toastType === 'warning' ? 'warn' : toastType,
        showProgressBar: false,
        showCloseButton: !compact || !shouldAutoClose,
        closeButtonColor: 'var(--vr-toast-muted)',
        iconBackground: iconBackground ?? 'transparent',
        iconColor: iconColor ?? accent,
        ...(icon != null ? { icon, showIcon: true } : {})
    };

    return toast(toastOptions);
}
