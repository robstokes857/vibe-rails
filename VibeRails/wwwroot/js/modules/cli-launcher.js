export class CliLauncher {
    constructor(app) {
        this.app = app;
    }

    loadLaunchCLI() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.replaceChildren();
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
            terminal.replaceChildren();
            this.appendTerminalLine(terminal, `Launching ${cliType.toUpperCase()} CLI...`);
        }

        try {
            const requestBody = {
                environmentName: environmentName,
                args: []
            };

            const response = await this.app.apiCall(`/api/v1/cli/launch/${cliType}`, 'POST', requestBody);

            if (terminal) {
                this.appendTerminalLine(terminal, response.message);
                if (response.standardOutput) {
                    this.appendTerminalLine(terminal, response.standardOutput);
                }
                if (response.standardError) {
                    this.appendTerminalLine(terminal, response.standardError, { isError: true });
                }
            }

            if (response.success) {
                this.app.showToast('CLI Launched', `${cliType} CLI session started successfully`, 'success');
            } else {
                this.app.showToast('CLI Error', response.message, 'error');
            }
        } catch (error) {
            if (terminal) {
                this.appendTerminalLine(terminal, `Error: ${error.message}`, { isError: true });
            }
            this.app.showError(`Failed to launch ${cliType} CLI`);
        }
    }

    appendTerminalLine(terminal, value, { isError = false } = {}) {
        const line = document.createElement('div');
        line.className = 'vb-terminal-line';
        line.textContent = value == null ? '' : String(value);
        if (isError) {
            line.style.color = '#ff6b6b';
        }
        terminal.appendChild(line);
    }
}
