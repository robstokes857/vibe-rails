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
        // Send the raw pieces — tab label, the user's (possibly blank) computer name,
        // and the status key. The server composes the title/body and fills a blank
        // computer name with Environment.MachineName before forwarding to
        // VibeRails-Front, so that fallback is authoritative even if this client never
        // learned the machine name (a fresh tab, a remote viewer).
        const settings = this.manager.app?.appSettings || {};
        const computerName = settings.computerName || '';
        const label = state?.label || state?.title || 'Terminal';
        const tab = this.manager.tabs.get(state.id);
        let imageBase64 = null;
        try { imageBase64 = tab?.instance?.captureImage?.() || null; } catch { /* no-op */ }

        try {
            void this.manager.app.apiCall('/api/v1/push/send', 'POST', {
                label,
                computerName,
                statusKey,
                tag: state.sessionId || state.id,
                imageBase64
            }, { showLoading: false }).catch(() => { /* fire-and-forget */ });
        } catch { /* no-op */ }
    }
}
