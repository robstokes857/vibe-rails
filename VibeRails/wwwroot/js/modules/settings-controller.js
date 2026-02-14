export class SettingsController {
    constructor(app) {
        this.app = app;
    }

    async loadSettings() {
        const content = document.getElementById('app-content');
        if (!content) return;

        let settings = { remoteAccess: false, apiKey: '' };
        try {
            settings = await this.app.apiCall('/api/v1/settings', 'GET');
        } catch (error) {
            console.error('Failed to fetch settings:', error);
        }

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('settings-template');
        const root = fragment.querySelector('[data-view="settings"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());

            const remoteAccessToggle = root.querySelector('#setting-remote-access');
            const apiKeyInput = root.querySelector('#setting-api-key');

            if (remoteAccessToggle) {
                remoteAccessToggle.checked = settings.remoteAccess || false;
            }
            if (apiKeyInput) {
                apiKeyInput.value = settings.apiKey || '';
            }

            const form = root.querySelector('#app-settings-form');
            if (form) {
                form.addEventListener('submit', async (e) => {
                    e.preventDefault();
                    await this.saveSettings(
                        remoteAccessToggle?.checked || false,
                        apiKeyInput?.value || ''
                    );
                });
            }
        }

        content.appendChild(fragment);
    }

    async saveSettings(remoteAccess, apiKey) {
        try {
            await this.app.apiCall('/api/v1/settings', 'POST', {
                remoteAccess: remoteAccess,
                apiKey: apiKey
            });
            this.app.showToast('Settings', 'Settings saved successfully', 'success');
        } catch (error) {
            this.app.showError('Failed to save settings: ' + error.message);
        }
    }
}
