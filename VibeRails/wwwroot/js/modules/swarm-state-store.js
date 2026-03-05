const SWARM_STATE_PREFIX = 'viberails_swarm_state_v1';

function safeProjectKey(path) {
    if (typeof path !== 'string' || path.trim().length === 0) {
        return 'global';
    }

    return path.trim().toLowerCase();
}

export class SwarmStateStore {
    constructor(app) {
        this.app = app;
    }

    get storageKey() {
        const projectPath = this.app?.data?.configs?.rootPath || '';
        return `${SWARM_STATE_PREFIX}:${safeProjectKey(projectPath)}`;
    }

    load() {
        try {
            const raw = window.localStorage.getItem(this.storageKey);
            if (!raw) return null;
            const parsed = JSON.parse(raw);
            if (!parsed || typeof parsed !== 'object') {
                return null;
            }
            return parsed;
        } catch {
            return null;
        }
    }

    save(state) {
        try {
            window.localStorage.setItem(this.storageKey, JSON.stringify(state));
        } catch {
            // Ignore storage quota and browser privacy mode failures.
        }
    }

    clear() {
        try {
            window.localStorage.removeItem(this.storageKey);
        } catch {
            // no-op
        }
    }
}
