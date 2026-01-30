export class CliLauncher {
    constructor(app) {
        this.app = app;
    }

    loadLaunchCLI() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('launch-cli-template');
        const root = fragment.querySelector('[data-view="launch-cli"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            this.app.bindActions(root, '[data-action="launch-cli"]', (element) => {
                const cli = element.dataset.cli;
                if (cli) {
                    this.launchCLI(cli);
                }
            });
        }

        content.appendChild(fragment);
    }

    async launchCLI(cliType, environmentName = null) {
        const terminal = document.getElementById('cli-terminal');
        if (terminal) {
            terminal.innerHTML = `<div class="terminal-line">Launching ${cliType.toUpperCase()} CLI...</div>`;
        }

        try {
            const requestBody = {
                environmentName: environmentName,
                args: []
            };

            const response = await this.app.apiCall(`/api/v1/cli/launch/${cliType}`, 'POST', requestBody);

            if (terminal) {
                terminal.innerHTML += `<div class="terminal-line">${response.message}</div>`;
                if (response.standardOutput) {
                    terminal.innerHTML += `<div class="terminal-line">${response.standardOutput}</div>`;
                }
                if (response.standardError) {
                    terminal.innerHTML += `<div class="terminal-line" style="color: #ff6b6b;">${response.standardError}</div>`;
                }
            }

            if (response.success) {
                this.app.showToast('CLI Launched', `${cliType} CLI session started successfully`, 'success');
            } else {
                this.app.showToast('CLI Error', response.message, 'error');
            }
        } catch (error) {
            if (terminal) {
                terminal.innerHTML += `<div class="terminal-line" style="color: #ff6b6b;">Error: ${error.message}</div>`;
            }
            this.app.showError(`Failed to launch ${cliType} CLI`);
        }
    }
}