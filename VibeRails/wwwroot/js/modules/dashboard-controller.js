export class DashboardController {
    constructor(app) {
        this.app = app;
    }

    async loadDashboard(data = {}) {
        await this.app.refreshDashboardData();

        // Fetch custom project name if in local context. app.js reads
        // _customProjectName for the window/tab title, so keep this fresh.
        if (this.app.data.isInGit) {
            const path = this.app.data.configs?.rootPath;
            if (path) {
                try {
                    const result = await this.app.apiCall(`/api/v1/projects/name?path=${encodeURIComponent(path)}`);
                    this._customProjectName = result.customName || null;
                    if (this._customProjectName) {
                        this.app.data.projectDisplayName = this._customProjectName;
                    }
                } catch {
                    this._customProjectName = null;
                }
            }
        }

        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        content.appendChild(this.renderUnifiedDashboard(data));

        // Ensure we are at the top on load
        window.scrollTo(0, 0);
    }

    // The dashboard is the Rules workspace plus the terminal mount below it.
    renderUnifiedDashboard(data = {}) {
        const fragment = this.app.cloneTemplate('dashboard-template');
        const root = fragment.querySelector('[data-dashboard]');
        if (!root) return fragment;

        const rulesHost = root.querySelector('[data-rules-overview-host]');
        if (rulesHost) {
            this.app.agentController.mountAgentsOverview(rulesHost);
        }

        // Terminal section
        const terminalContent = root.querySelector('[data-terminal-content]');
        if (terminalContent) {
            terminalContent.innerHTML = this.app.terminalController.renderTerminalPanel();
            // Pass preselected environment ID if navigating from environments page
            this.app.terminalController.bindTerminalActions(
                terminalContent,
                data.preselectedEnvId || null
            );
        }

        return fragment;
    }
}
