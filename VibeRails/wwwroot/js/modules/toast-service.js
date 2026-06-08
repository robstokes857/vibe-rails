import { escapeHtml, escapeHtmlWithLineBreaks } from './utils.js';

const DEFAULT_THEME = 'glassmorphism';
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
    borderRadius: '12px',
    color: '#f8fafc',
    fontFamily: '\'Inter\', -apple-system, sans-serif',
    showIcon: true,
    showCloseButton: true,
    progressBarPosition: 'bottom',
    progressBarHeight: '2px',
    iconAnimation: 'pulse'
});

const DEFAULT_TONE = Object.freeze({
    accent: '#3b82f6',
    border: 'rgba(59, 130, 246, 0.4)',
    iconBackground: 'rgba(59, 130, 246, 0.1)',
    closeButtonColor: '#94a3b8'
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
        theme = DEFAULT_THEME,
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
    const tone = DEFAULT_TONE;
    const shouldAutoClose = autoClose === false ? false : !requireDismiss;
    const baseDuration = DEFAULT_DURATIONS[toastType];
    const compactDuration = Math.min(baseDuration, 3000);
    const resolvedDuration = shouldAutoClose
        ? (duration ?? (compact ? compactDuration : baseDuration))
        : 0;
    const resolvedPosition = position ?? (compact ? 'bottom-right' : DEFAULT_POSITION);
    const resolvedTheme = compact ? 'minimal' : theme;

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
        showProgressBar: shouldAutoClose && !compact,
        showCloseButton: !compact,
        ...(icon != null ? { icon, showIcon: true } : {})
    };

    if (!compact) {
        toastOptions.border = `1px solid ${tone.border}`;
        toastOptions.progressBarColor = tone.accent;
        toastOptions.closeButtonColor = tone.closeButtonColor;
        toastOptions.iconBackground = iconBackground ?? tone.iconBackground;
        toastOptions.iconColor = iconColor ?? tone.accent;
    } else {
        if (iconBackground != null) toastOptions.iconBackground = iconBackground;
        if (iconColor != null) toastOptions.iconColor = iconColor;
    }

    toast(toastOptions);
}
