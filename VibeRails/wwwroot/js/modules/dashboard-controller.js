export class DashboardController {
    constructor(app) {
        this.app = app;
    }

    async loadDashboard(data = {}) {
        // Fetch custom project name concurrently with the dashboard data refresh —
        // it only depends on configs.rootPath, which is already loaded. app.js reads
        // _customProjectName for the window/tab title, so keep this fresh.
        const nameTask = (async () => {
            if (!this.app.data.isInGit) return;
            const path = this.app.data.configs?.rootPath;
            if (!path) return;
            try {
                const result = await this.app.apiCall(`/api/v1/projects/name?path=${encodeURIComponent(path)}`, 'GET', null, { showLoading: false });
                this._customProjectName = result.customName || null;
                if (this._customProjectName) {
                    this.app.data.projectDisplayName = this._customProjectName;
                }
            } catch {
                this._customProjectName = null;
            }
        })();

        await Promise.all([this.app.refreshDashboardData(), nameTask]);

        // A navigation that happened while the fetches above were in flight owns the
        // page now; painting the dashboard over it would clobber that view.
        if (!['dashboard', 'agents'].includes(this.app.currentView)) return;

        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        content.appendChild(this.renderUnifiedDashboard(data));

        // Ensure we are at the top on load
        window.scrollTo(0, 0);
    }

    // Rules, validation, Git Guard, and Code quality share one dashboard. Agent work
    // opens in the dedicated terminal view, so this surface never mounts xterm.
    renderUnifiedDashboard(data = {}) {
        const fragment = this.app.cloneTemplate('dashboard-template');
        const root = fragment.querySelector('[data-dashboard]');
        if (!root) return fragment;

        const rulesHost = root.querySelector('[data-rules-overview-host]');
        if (rulesHost) {
            this.app.agentController.mountAgentsOverview(rulesHost);
        }

        return fragment;
    }
}
