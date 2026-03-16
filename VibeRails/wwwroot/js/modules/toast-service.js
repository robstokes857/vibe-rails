import { escapeHtml } from './utils.js';

const DEFAULT_THEME = 'xrayOverlay';
const DEFAULT_POSITION = 'top-right';
const DEFAULT_ENTRY_ANIMATION = 'swingDownIn';
const DEFAULT_EXIT_ANIMATION = 'dustOut';

const DEFAULT_DURATIONS = Object.freeze({
    info: 5000,
    success: 4200,
    warning: 6500,
    error: 7000
});

const BASE_TOAST_OPTIONS = Object.freeze({
    borderRadius: '18px',
    background: 'linear-gradient(135deg, rgba(10, 18, 28, 0.96), rgba(18, 28, 44, 0.92))',
    color: '#eefbff',
    fontFamily: '\'JetBrains Mono\', \'Fira Code\', monospace',
    showIcon: true,
    progressBarPosition: 'top',
    progressBarHeight: '0.18rem',
    iconAnimation: 'pulse'
});

const TOAST_TONES = Object.freeze({
    info: Object.freeze({
        accent: '#67e8f9',
        border: 'rgba(103, 232, 249, 0.45)',
        iconBackground: 'rgba(8, 145, 178, 0.22)',
        closeButtonColor: '#a5f3fc'
    }),
    success: Object.freeze({
        accent: '#4ade80',
        border: 'rgba(74, 222, 128, 0.42)',
        iconBackground: 'rgba(22, 163, 74, 0.2)',
        closeButtonColor: '#86efac'
    }),
    warning: Object.freeze({
        accent: '#fbbf24',
        border: 'rgba(251, 191, 36, 0.45)',
        iconBackground: 'rgba(217, 119, 6, 0.2)',
        closeButtonColor: '#fde68a'
    }),
    error: Object.freeze({
        accent: '#fb7185',
        border: 'rgba(251, 113, 133, 0.48)',
        iconBackground: 'rgba(225, 29, 72, 0.2)',
        closeButtonColor: '#fda4af'
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
