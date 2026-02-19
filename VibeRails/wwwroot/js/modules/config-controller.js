export class ConfigController {
    constructor(app) {
        this.app = app;
    }

    loadConfiguration() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('configuration-template');
        const root = fragment.querySelector('[data-view="config"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            this.app.bindActions(root, '[data-action="configure-cli"]', (element) => {
                const cli = element.dataset.cli;
                if (cli) {
                    this.configureCLI(cli);
                }
            });
        }

        content.appendChild(fragment);
    }

    configureCLI(cliType) {
        this.app.showModal(`Configure ${cliType.toUpperCase()}`, `
            <form id="config-form">
                <div class="mb-3">
                    <label class="form-label">API Key</label>
                    <input type="password" class="form-control" id="api-key" placeholder="Enter API key">
                </div>
                <div class="mb-3">
                    <label class="form-label">Model</label>
                    <input type="text" class="form-control" id="model" placeholder="Model name">
                </div>
                <div class="mb-3">
                    <label class="form-label">Base URL</label>
                    <input type="text" class="form-control" id="base-url" placeholder="API base URL">
                </div>
                <div class="d-flex gap-2">
                    <button type="submit" class="btn btn-primary">Save</button>
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                </div>
            </form>
        `);

        document.getElementById('config-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            alert('Fix me: Configuration API not implemented');
        });
    }
}