export class DashboardController {
    constructor(app) {
        this.app = app;
    }

    async loadDashboard(data = {}) {
        const view = this.app.currentView;
        const loadId = this.loadId = (this.loadId || 0) + 1;
        if (view === 'code-quality') {
            // Quality shows the repository map and a scan; neither reads the rule files, launch
            // catalog or project name refreshed below. Ask for the map and mount the view now, and
            // let that refresh finish behind it instead of holding an empty page for it.
            this.app.ruleController.prefetchCodeQuality();
            this.mountCodeQuality();
            await Promise.all([this.app.refreshDashboardData(), this.refreshProjectName()]);
            return;
        }

        await Promise.all([this.app.refreshDashboardData(), this.refreshProjectName()]);

        // A navigation that happened while the fetches above were in flight owns the
        // page now; painting the dashboard over it would clobber that view.
        if (this.loadId !== loadId || this.app.currentView !== view) return;

        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        this.renderUnifiedDashboard(content, data);

        // Ensure we are at the top on load
        window.scrollTo(0, 0);
    }

    // The custom project name only depends on configs.rootPath, which is already loaded, so it is
    // fetched alongside the dashboard data refresh. app.js reads _customProjectName for the
    // window/tab title, so keep this fresh.
    async refreshProjectName() {
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
    }

    mountCodeQuality() {
        const content = document.getElementById('app-content');
        if (!content) return;
        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('code-quality-template');
        const root = fragment.querySelector('[data-view="code-quality"]');
        content.appendChild(fragment);
        if (root) this.app.ruleController.attachCodeQualityOverview(root);
        window.scrollTo(0, 0);
    }

    // Rules, validation and Git Guard share the Rules page. Agent work opens in
    // the dedicated terminal view, so this surface never mounts xterm.
    renderUnifiedDashboard(container, data = {}) {
        const fragment = this.app.cloneTemplate('dashboard-template');
        const root = fragment.querySelector('[data-dashboard]');
        // Template clones have an inert ownerDocument with no defaultView. Adopt the
        // dashboard into the page before mounting children that use its window/events.
        container.appendChild(fragment);
        if (!root) return;

        const rulesHost = root.querySelector('[data-rules-overview-host]');
        if (rulesHost) {
            this.app.agentController.mountAgentsOverview(rulesHost);
        }
    }
}
