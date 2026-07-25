(function () {
    const STORAGE_KEY = 'viberails_nav_layout';
    const SIDE = 'side';
    const TOP = 'top';
    const subscribers = new Set();
    let layout = SIDE;

    function normalize(value) {
        return value === TOP ? TOP : SIDE;
    }

    function readStoredValue() {
        try {
            return normalize(localStorage.getItem(STORAGE_KEY));
        } catch {
            return SIDE;
        }
    }

    function applyState() {
        const root = document.documentElement;
        if (!root) return;
        root.classList.toggle('nav-layout-side', layout === SIDE);
        root.classList.toggle('nav-layout-top', layout === TOP);
    }

    function notifySubscribers() {
        const detail = { layout };
        window.dispatchEvent(new CustomEvent('navLayoutChanged', { detail }));

        subscribers.forEach((callback) => {
            try {
                callback(layout);
            } catch (error) {
                console.error('Nav layout subscriber failed:', error);
            }
        });
    }

    function syncFromStorage({ notify = false } = {}) {
        const next = readStoredValue();
        const changed = next !== layout;
        layout = next;
        applyState();

        if (notify && changed) {
            notifySubscribers();
        }

        return layout;
    }

    function setLayout(value, { notify = true } = {}) {
        const next = normalize(value);
        const changed = next !== layout;
        layout = next;

        try {
            localStorage.setItem(STORAGE_KEY, layout);
        } catch {
            // Ignore storage failures and keep the in-memory state.
        }

        applyState();

        if (notify && changed) {
            notifySubscribers();
        }

        return layout;
    }

    // Apply the stored preference as early as possible. The inline <head> script
    // already sets the class for first paint; this re-applies to stay in sync if
    // storage changed in another tab or the inline script did not run.
    syncFromStorage();

    window.VibeRailsNavLayout = {
        storageKey: STORAGE_KEY,
        SIDE,
        TOP,
        getLayout() {
            return layout;
        },
        isSide() {
            return layout === SIDE;
        },
        isTop() {
            return layout === TOP;
        },
        setLayout,
        syncFromStorage,
        subscribe(callback) {
            subscribers.add(callback);
            return () => subscribers.delete(callback);
        }
    };
})();
