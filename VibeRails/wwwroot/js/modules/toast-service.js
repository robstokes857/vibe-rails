import { escapeHtml } from './utils.js';

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

const TOAST_TONES = Object.freeze({
    info: Object.freeze({
        accent: '#3b82f6',
        border: 'rgba(59, 130, 246, 0.4)',
        iconBackground: 'rgba(59, 130, 246, 0.1)',
        closeButtonColor: '#94a3b8'
    }),
    success: Object.freeze({
        accent: '#10b981',
        border: 'rgba(20, 116, 84, 0.86)',
        iconBackground: 'rgba(16, 185, 129, 0.1)',
        closeButtonColor: '#94a3b8'
    }),
    warning: Object.freeze({
        accent: '#f59e0b',
        border: 'rgba(245, 158, 11, 0.4)',
        iconBackground: 'rgba(245, 158, 11, 0.1)',
        closeButtonColor: '#94a3b8'
    }),
    error: Object.freeze({
        accent: '#ef4444',
        border: 'rgba(239, 68, 68, 0.4)',
        iconBackground: 'rgba(239, 68, 68, 0.1)',
        closeButtonColor: '#94a3b8'
    })
});

function normalizeToastType(type) {
    const normalizedType = (type || 'info').toLowerCase();
    return Object.hasOwn(TOAST_TONES, normalizedType) ? normalizedType : 'info';
}

function formatToastMessage(title, message) {
    const safeTitle = escapeHtml(title || 'Notification');
    const safeMessage = escapeHtml(message || '');
    const formattedMessage = safeMessage.replace(/\n/g, '<br>');

    if (!safeMessage) {
        return `<span class="vr-toast-title vr-toast-title-only">${safeTitle}</span>`;
    }

    return `<span class="vr-toast-title">${safeTitle}</span><span class="vr-toast-body">${formattedMessage}</span>`;
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
        position = DEFAULT_POSITION
    } = options;

    const toastType = normalizeToastType(type);
    const tone = TOAST_TONES[toastType];
    const shouldAutoClose = autoClose === false ? false : !requireDismiss;
    const resolvedDuration = shouldAutoClose ? (duration ?? DEFAULT_DURATIONS[toastType]) : 0;

    toast({
        ...BASE_TOAST_OPTIONS,
        message: formatToastMessage(title, message),
        position,
        duration: resolvedDuration,
        autoClose: shouldAutoClose,
        theme,
        entryAnimation: entryAnimation ?? animation?.enter ?? DEFAULT_ENTRY_ANIMATION,
        exitAnimation: exitAnimation ?? animation?.exit ?? DEFAULT_EXIT_ANIMATION,
        border: `1px solid ${tone.border}`,
        iconType: toastType === 'warning' ? 'warn' : toastType,
        progressBarColor: tone.accent,
        showProgressBar: shouldAutoClose,
        closeButtonColor: tone.closeButtonColor,
        ...(icon != null ? { icon, showIcon: true } : {}),
        iconBackground: iconBackground ?? tone.iconBackground,
        ...(iconColor != null ? { iconColor } : { iconColor: tone.accent })
    });
}
