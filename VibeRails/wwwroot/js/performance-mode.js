(function () {
    const STORAGE_KEY = 'performanceMode';
    const subscribers = new Set();
    let enabled = false;

    function normalize(value) {
        return value === true || value === 'true';
    }

    function readStoredValue() {
        try {
            return normalize(localStorage.getItem(STORAGE_KEY));
        } catch {
            return false;
        }
    }

    function applyBodyState() {
        document.body?.classList.toggle('performance-mode', enabled);
    }

    function notifySubscribers() {
        const detail = { enabled };
        window.dispatchEvent(new CustomEvent('performanceModeChanged', { detail }));

        subscribers.forEach((callback) => {
            try {
                callback(enabled);
            } catch (error) {
                console.error('Performance mode subscriber failed:', error);
            }
        });
    }

    function syncFromStorage({ notify = false } = {}) {
        const nextEnabled = readStoredValue();
        const changed = nextEnabled !== enabled;
        enabled = nextEnabled;
        applyBodyState();

        if (notify && changed) {
            notifySubscribers();
        }

        return enabled;
    }

    function setEnabled(value, { notify = true } = {}) {
        const nextEnabled = normalize(value);
        const changed = nextEnabled !== enabled;
        enabled = nextEnabled;

        try {
            localStorage.setItem(STORAGE_KEY, String(enabled));
        } catch {
            // Ignore storage failures and keep the in-memory state.
        }

        applyBodyState();

        if (notify && changed) {
            notifySubscribers();
        }

        return enabled;
    }

    syncFromStorage();

    window.VibeRailsPerformance = {
        storageKey: STORAGE_KEY,
        isEnabled() {
            return enabled;
        },
        setEnabled,
        syncFromStorage,
        subscribe(callback) {
            subscribers.add(callback);
            return () => subscribers.delete(callback);
        }
    };
})();
