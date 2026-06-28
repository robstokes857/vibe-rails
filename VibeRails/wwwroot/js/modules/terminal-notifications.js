function cleanString(value) {
    if (typeof value !== 'string') return null;
    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
}

function pushMessage(label, computerName, statusKey) {
    const safeLabel = cleanString(label) || 'Terminal';
    const safeComputerName = cleanString(computerName);
    const target = safeComputerName ? `${safeLabel} on ${safeComputerName}` : safeLabel;

    if (statusKey === 'waiting') {
        return {
            title: `${target} needs input`,
            body: 'Waiting for user input'
        };
    }

    return {
        title: `${target} is ready`,
        body: 'Ready'
    };
}

export class TerminalNotifications {
    constructor(manager) {
        this.manager = manager;
    }

    notifyTabReady(state) {
        const label = state?.label || state?.title || 'Terminal';

        const active = this.manager.getActiveTab();
        const handle = active?.state?.hasActiveSession
            ? this.manager.toast.showSmallToast(`${label} is ready`, {
                type: 'success',
                durationSec: 6,
                action: {
                    label: 'View',
                    onClick: () => { void this.manager.activateTab(state.id); }
                }
            })
            : null;

        if (!handle) {
            this.manager.app?.showToast?.(label, 'Ready', 'success', { compact: true });
        }
    }

    notifyTabPush(state, statusKey) {
        // Blank computerName falls back to the live machine name (resolved server-side
        // and surfaced via appSettings.machineName) so multi-machine pushes stay
        // disambiguated out of the box. pushMessage defaults the label itself.
        const settings = this.manager.app?.appSettings || {};
        const computerName = settings.computerName || settings.machineName;
        const { title, body } = pushMessage(state?.label || state?.title, computerName, statusKey);
        const tab = this.manager.tabs.get(state.id);
        let imageBase64 = null;
        try { imageBase64 = tab?.instance?.captureImage?.() || null; } catch { /* no-op */ }

        try {
            void this.manager.app.apiCall('/api/v1/push/send', 'POST', {
                title,
                body,
                tag: state.sessionId || state.id,
                imageBase64
            }, { showLoading: false }).catch(() => { /* fire-and-forget */ });
        } catch { /* no-op */ }
    }
}
