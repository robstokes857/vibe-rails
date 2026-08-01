export class SettingsController {
    constructor(app) {
        this.app = app;
        this._pinIsSet = false;
        this.performanceMode = window.VibeRailsPerformance;
        this._settingsDirty = false;
        this._settingsSaving = false;
        this._settingsSnapshot = '';
        this._removeNavigationGuard = null;
        this._beforeUnloadHandler = null;
        this._dataExportInProgress = false;
        this._dataExportConfigured = false;
        this._dataExportSizeBytes = null;
        this._settingsRoot = null;
    }

    async loadSettings() {
        this.unload();

        const content = document.getElementById('app-content');
        if (!content) return;

        let settings = {
            remoteAccess: false,
            apiKey: '',
            useVsCodeTheme: false,
            mcpEnabled: true,
            computerName: '',
            codexLlmProxyEnabled: false,
            codexLlmProxyMode: 'subscription',
            claudeLlmProxyEnabled: false,
            openCodeLlmProxyEnabled: false,
            // Per-LLM saver toggles — the whole saver surface. Default on: the saver only acts
            // when that LLM's proxy is also enabled.
            claudeTokenSaverEnabled: true,
            codexTokenSaverEnabled: true,
            openCodeTokenSaverEnabled: true,
            tokenSaverCaptureEnabled: false,
            removeCoAuthorTrailers: true,
            machineName: ''
        };
        const dataExportSizePromise = this._loadDataExportSize();
        try {
            settings = await this.app.apiCall('/api/v1/settings', 'GET');
            this.app.setAppSettings(settings);
        } catch (error) {
            console.error('Failed to fetch settings:', error);
        }
        await dataExportSizePromise;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('settings-template');
        const root = fragment.querySelector('[data-view="settings"]');

        if (root) {
            this._settingsRoot = root;
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());

            const projectIdentityCard = root.querySelector('[data-project-identity-card]');
            if (projectIdentityCard && this.app.data.isInGit) {
                projectIdentityCard.hidden = false;
                const projectName = projectIdentityCard.querySelector('[data-project-display-name]');
                const projectPath = projectIdentityCard.querySelector('[data-project-root-path]');
                const renderProjectName = (name) => {
                    if (projectName) projectName.textContent = name || this.app.getCurrentProjectDisplayName();
                };
                renderProjectName(this.app.getCurrentProjectDisplayName());
                if (projectPath) projectPath.textContent = this.app.data.configs?.rootPath || '';
                this.app.bindAction(projectIdentityCard, '[data-action="rename-project"]', () => {
                    this.app.showCustomNameModal({ onSaved: renderProjectName });
                });
            }

            const remoteAccessToggle = root.querySelector('#setting-remote-access');
            const apiKeyInput = root.querySelector('#setting-api-key');
            const exportDataButton = root.querySelector('#settings-export-data-button');
            const performanceModeToggle = root.querySelector('#setting-performance-mode');
            const useVsCodeThemeRow = root.querySelector('#setting-use-vscode-theme-row');
            const useVsCodeThemeToggle = root.querySelector('#setting-use-vscode-theme');
            const mcpEnabledToggle = root.querySelector('#setting-enable-mcp');
            const computerNameInput = root.querySelector('#setting-computer-name');
            const codexLlmProxyEnabledToggle = root.querySelector('#setting-codex-llm-proxy-enabled');
            const codexLlmProxyModeSubscription = root.querySelector('#setting-codex-llm-proxy-mode-subscription');
            const codexLlmProxyModeApi = root.querySelector('#setting-codex-llm-proxy-mode-api');
            const claudeLlmProxyEnabledToggle = root.querySelector('#setting-claude-llm-proxy-enabled');
            const opencodeLlmProxyEnabledToggle = root.querySelector('#setting-opencode-llm-proxy-enabled');
            const claudeTokenSaverToggle = root.querySelector('#setting-token-saver-claude');
            const codexTokenSaverToggle = root.querySelector('#setting-token-saver-codex');
            const opencodeTokenSaverToggle = root.querySelector('#setting-token-saver-opencode');
            const tokenSaverCaptureToggle = root.querySelector('#setting-token-saver-capture');
            const removeCoAuthorTrailersToggle = root.querySelector('#setting-remove-co-author-trailers');

            if (remoteAccessToggle) {
                remoteAccessToggle.checked = settings.remoteAccess || false;
                remoteAccessToggle.addEventListener('change', () => {
                    if (remoteAccessToggle.checked && !this._pinIsSet) {
                        remoteAccessToggle.checked = false;
                        this.app.showToast('Remote Access', 'A PIN must be set before enabling remote access.', 'warning');
                        this._updateDirtyState(root);
                    }
                });
            }
            if (apiKeyInput) {
                apiKeyInput.value = settings.apiKey || '';
                apiKeyInput.dataset.originalValue = settings.apiKey || '';
            }
            this._dataExportConfigured = settings.dataExportConfigured === true;
            if (exportDataButton) {
                exportDataButton.addEventListener('click', () => this._exportData(root));
            }
            this._updateDataExportAvailability(root);
            if (performanceModeToggle) {
                performanceModeToggle.checked = this.performanceMode?.isEnabled?.() === true;
                performanceModeToggle.addEventListener('change', () => {
                    if (this.performanceMode?.setEnabled) {
                        this.performanceMode.setEnabled(performanceModeToggle.checked);
                    }
                });
            }
            if (useVsCodeThemeRow && !window.__viberails_VSCODE__) {
                useVsCodeThemeRow.style.display = 'none';
            }
            if (useVsCodeThemeToggle) {
                useVsCodeThemeToggle.checked = settings.useVsCodeTheme || false;
            }
            if (mcpEnabledToggle) {
                mcpEnabledToggle.checked = true;
                mcpEnabledToggle.disabled = true;
            }
            if (computerNameInput) {
                computerNameInput.value = settings.computerName || '';
                // Blank field defaults to the live machine name in push notifications —
                // surface it as the placeholder so the default is visible.
                if (settings.machineName) computerNameInput.placeholder = settings.machineName;
            }
            if (codexLlmProxyEnabledToggle) {
                codexLlmProxyEnabledToggle.checked = settings.codexLlmProxyEnabled === true;
            }
            const codexLlmProxyMode = settings.codexLlmProxyMode === 'api' ? 'api' : 'subscription';
            if (codexLlmProxyModeSubscription) codexLlmProxyModeSubscription.checked = codexLlmProxyMode === 'subscription';
            if (codexLlmProxyModeApi) codexLlmProxyModeApi.checked = codexLlmProxyMode === 'api';
            if (claudeLlmProxyEnabledToggle) {
                claudeLlmProxyEnabledToggle.checked = settings.claudeLlmProxyEnabled === true;
            }
            if (opencodeLlmProxyEnabledToggle) {
                opencodeLlmProxyEnabledToggle.checked = settings.openCodeLlmProxyEnabled === true;
            }
            // `!== false` so a server that predates the per-LLM split (key absent) renders the
            // default-on state the proxy actually runs with.
            if (claudeTokenSaverToggle) {
                claudeTokenSaverToggle.checked = settings.claudeTokenSaverEnabled !== false;
            }
            if (codexTokenSaverToggle) {
                codexTokenSaverToggle.checked = settings.codexTokenSaverEnabled !== false;
            }
            if (opencodeTokenSaverToggle) {
                opencodeTokenSaverToggle.checked = settings.openCodeTokenSaverEnabled !== false;
            }
            if (tokenSaverCaptureToggle) {
                tokenSaverCaptureToggle.checked = settings.tokenSaverCaptureEnabled === true;
            }
            if (removeCoAuthorTrailersToggle) {
                // Missing on older servers/settings files means the documented default: enabled.
                removeCoAuthorTrailersToggle.checked = settings.removeCoAuthorTrailers !== false;
            }

            const form = root.querySelector('#app-settings-form');
            if (form) {
                form.addEventListener('submit', async (e) => {
                    e.preventDefault();
                    if (!this._settingsDirty || this._settingsSaving) {
                        return;
                    }

                    const wantsRemote = remoteAccessToggle?.checked || false;
                    if (wantsRemote && !this._pinIsSet) {
                        this.app.showToast('Remote Access', 'A PIN must be set before enabling remote access.', 'warning');
                        if (remoteAccessToggle) remoteAccessToggle.checked = false;
                        this._updateDirtyState(root);
                        return;
                    }
                    const apiKeyValue = apiKeyInput?.value || '';
                    const savedApiKey = apiKeyInput?.dataset.originalValue || '';
                    const apiKeyChanged = apiKeyValue !== savedApiKey;
                    // Emptying a populated box is the "remove my key" gesture. The backend reads a
                    // blank apiKey as "unchanged" (the masked value wasn't edited), so this flag is
                    // the only way a saved key can be cleared from the UI.
                    const clearApiKey = apiKeyChanged
                        && apiKeyValue.trim().length === 0
                        && savedApiKey.length > 0;
                    this._settingsSaving = true;
                    this._updateSaveBar(root);
                    try {
                        const savedSettings = await this.saveSettings(
                            wantsRemote,
                            apiKeyChanged ? apiKeyValue : '',
                            useVsCodeThemeToggle?.checked || false,
                            true,
                            computerNameInput?.value || '',
                            codexLlmProxyEnabledToggle?.checked || false,
                            this._getCodexLlmProxyMode(root),
                            claudeLlmProxyEnabledToggle?.checked || false,
                            opencodeLlmProxyEnabledToggle?.checked || false,
                            claudeTokenSaverToggle?.checked ?? true,
                            codexTokenSaverToggle?.checked ?? true,
                            opencodeTokenSaverToggle?.checked ?? true,
                            tokenSaverCaptureToggle?.checked ?? false,
                            removeCoAuthorTrailersToggle?.checked ?? true,
                            clearApiKey
                        );
                        if (savedSettings) {
                            this._applySavedSettingsToControls(root, savedSettings);
                            this._markSettingsClean(root);
                        }
                    } finally {
                        this._settingsSaving = false;
                        this._updateSaveBar(root);
                    }
                });
            }

            this._initSettingsDirtyTracking(root);
            await this._initPinSection(root);
        }

        content.appendChild(fragment);
    }

    async saveSettings(remoteAccess, apiKey, useVsCodeTheme, mcpEnabled, computerName, codexLlmProxyEnabled, codexLlmProxyMode, claudeLlmProxyEnabled, openCodeLlmProxyEnabled, claudeTokenSaverEnabled, codexTokenSaverEnabled, openCodeTokenSaverEnabled, tokenSaverCaptureEnabled, removeCoAuthorTrailers, clearApiKey = false) {
        try {
            const savedSettings = await this.app.apiCall('/api/v1/settings', 'POST', {
                remoteAccess: remoteAccess,
                apiKey: apiKey,
                useVsCodeTheme: useVsCodeTheme,
                mcpEnabled: mcpEnabled,
                computerName: computerName,
                codexLlmProxyEnabled: codexLlmProxyEnabled,
                codexLlmProxyMode: codexLlmProxyMode,
                claudeLlmProxyEnabled: claudeLlmProxyEnabled,
                openCodeLlmProxyEnabled: openCodeLlmProxyEnabled,
                claudeTokenSaverEnabled: claudeTokenSaverEnabled,
                codexTokenSaverEnabled: codexTokenSaverEnabled,
                openCodeTokenSaverEnabled: openCodeTokenSaverEnabled,
                tokenSaverCaptureEnabled: tokenSaverCaptureEnabled,
                removeCoAuthorTrailers: removeCoAuthorTrailers,
                clearApiKey: clearApiKey
            });
            this.app.setAppSettings(savedSettings);
            this.app.showToast('Settings', 'Settings saved successfully', 'success');
            return savedSettings;
        } catch (error) {
            this.app.showError('Failed to save settings: ' + error.message);
            return null;
        }
    }

    unload() {
        if (this._removeNavigationGuard) {
            this._removeNavigationGuard();
            this._removeNavigationGuard = null;
        }

        if (this._beforeUnloadHandler) {
            window.removeEventListener('beforeunload', this._beforeUnloadHandler);
            this._beforeUnloadHandler = null;
        }

        this._settingsDirty = false;
        this._settingsSaving = false;
        this._settingsSnapshot = '';
        this._dataExportSizeBytes = null;
        this._settingsRoot = null;
    }

    _initSettingsDirtyTracking(root) {
        this._settingsSnapshot = this._captureSettingsSnapshot(root);
        this._settingsDirty = false;
        this._settingsSaving = false;
        this._updateSaveBar(root);

        root.querySelectorAll(this._trackedSettingsSelector()).forEach((input) => {
            input.addEventListener('input', () => this._updateDirtyState(root));
            input.addEventListener('change', () => this._updateDirtyState(root));
        });

        this._removeNavigationGuard = this.app.registerNavigationGuard?.(({ from }) => {
            if (from !== 'settings' || !this._settingsDirty) {
                return true;
            }

            // window.confirm is suppressed (returns false) inside the sandboxed VS Code webview
            // iframe, which would trap the user on a dirty form with no way to leave. Only block
            // where confirm actually prompts; in the webview, allow navigation instead of trapping.
            if (window.__viberails_VSCODE__) {
                return true;
            }

            return window.confirm('You have unsaved settings changes. Leave without saving?');
        }) || null;

        this._beforeUnloadHandler = (event) => {
            if (!this._settingsDirty) {
                return undefined;
            }

            event.preventDefault();
            event.returnValue = '';
            return '';
        };
        window.addEventListener('beforeunload', this._beforeUnloadHandler);
    }

    _trackedSettingsSelector() {
        return [
            '#setting-remote-access',
            '#setting-api-key',
            '#setting-use-vscode-theme',
            '#setting-computer-name',
            '#setting-codex-llm-proxy-enabled',
            'input[name="setting-codex-llm-proxy-mode"]',
            '#setting-claude-llm-proxy-enabled',
            '#setting-opencode-llm-proxy-enabled',
            '#setting-token-saver-claude',
            '#setting-token-saver-codex',
            '#setting-token-saver-opencode',
            '#setting-token-saver-capture',
            '#setting-remove-co-author-trailers'
        ].join(',');
    }

    _captureSettingsSnapshot(root) {
        const isChecked = (selector) => root.querySelector(selector)?.checked === true;
        const valueOf = (selector) => root.querySelector(selector)?.value || '';

        return JSON.stringify({
            remoteAccess: isChecked('#setting-remote-access'),
            apiKey: valueOf('#setting-api-key'),
            useVsCodeTheme: isChecked('#setting-use-vscode-theme'),
            computerName: valueOf('#setting-computer-name'),
            codexLlmProxyEnabled: isChecked('#setting-codex-llm-proxy-enabled'),
            codexLlmProxyMode: this._getCodexLlmProxyMode(root),
            claudeLlmProxyEnabled: isChecked('#setting-claude-llm-proxy-enabled'),
            openCodeLlmProxyEnabled: isChecked('#setting-opencode-llm-proxy-enabled'),
            claudeTokenSaverEnabled: isChecked('#setting-token-saver-claude'),
            codexTokenSaverEnabled: isChecked('#setting-token-saver-codex'),
            openCodeTokenSaverEnabled: isChecked('#setting-token-saver-opencode'),
            tokenSaverCaptureEnabled: isChecked('#setting-token-saver-capture'),
            removeCoAuthorTrailers: isChecked('#setting-remove-co-author-trailers')
        });
    }

    _updateDirtyState(root) {
        this._settingsDirty = this._captureSettingsSnapshot(root) !== this._settingsSnapshot;
        this._updateSaveBar(root);
        this._updateDataExportAvailability(root);
    }

    _markSettingsClean(root) {
        this._settingsSnapshot = this._captureSettingsSnapshot(root);
        this._settingsDirty = false;
        this._updateSaveBar(root);
    }

    _updateSaveBar(root) {
        const saveBar = root.querySelector('[data-settings-save-bar]');
        const saveButton = root.querySelector('#settings-save-button');
        const saveState = root.querySelector('#settings-save-state');

        if (saveBar) {
            saveBar.classList.toggle('is-dirty', this._settingsDirty);
        }

        if (saveState) {
            saveState.classList.toggle('is-dirty', this._settingsDirty);
            saveState.textContent = this._settingsSaving
                ? 'Saving changes...'
                : this._settingsDirty
                    ? 'Unsaved changes'
                    : 'All changes saved';
        }

        if (saveButton) {
            saveButton.disabled = this._settingsSaving || !this._settingsDirty;
        }
    }

    _applySavedSettingsToControls(root, settings) {
        if (!settings) return;

        const remoteAccessToggle = root.querySelector('#setting-remote-access');
        const apiKeyInput = root.querySelector('#setting-api-key');
        const useVsCodeThemeToggle = root.querySelector('#setting-use-vscode-theme');
        const computerNameInput = root.querySelector('#setting-computer-name');
        const codexLlmProxyEnabledToggle = root.querySelector('#setting-codex-llm-proxy-enabled');
        const codexLlmProxyModeSubscription = root.querySelector('#setting-codex-llm-proxy-mode-subscription');
        const codexLlmProxyModeApi = root.querySelector('#setting-codex-llm-proxy-mode-api');
        const claudeLlmProxyEnabledToggle = root.querySelector('#setting-claude-llm-proxy-enabled');
        const opencodeLlmProxyEnabledToggle = root.querySelector('#setting-opencode-llm-proxy-enabled');

        if (remoteAccessToggle) remoteAccessToggle.checked = settings.remoteAccess === true;
        if (apiKeyInput) {
            apiKeyInput.value = settings.apiKey || '';
            apiKeyInput.dataset.originalValue = settings.apiKey || '';
        }
        this._dataExportConfigured = settings.dataExportConfigured === true;
        if (useVsCodeThemeToggle) useVsCodeThemeToggle.checked = settings.useVsCodeTheme === true;
        if (computerNameInput) {
            computerNameInput.value = settings.computerName || '';
            if (settings.machineName) computerNameInput.placeholder = settings.machineName;
        }
        if (codexLlmProxyEnabledToggle) codexLlmProxyEnabledToggle.checked = settings.codexLlmProxyEnabled === true;

        const codexLlmProxyMode = settings.codexLlmProxyMode === 'api' ? 'api' : 'subscription';
        if (codexLlmProxyModeSubscription) codexLlmProxyModeSubscription.checked = codexLlmProxyMode === 'subscription';
        if (codexLlmProxyModeApi) codexLlmProxyModeApi.checked = codexLlmProxyMode === 'api';
        if (claudeLlmProxyEnabledToggle) claudeLlmProxyEnabledToggle.checked = settings.claudeLlmProxyEnabled === true;
        if (opencodeLlmProxyEnabledToggle) opencodeLlmProxyEnabledToggle.checked = settings.openCodeLlmProxyEnabled === true;

        const claudeTokenSaverToggle = root.querySelector('#setting-token-saver-claude');
        const codexTokenSaverToggle = root.querySelector('#setting-token-saver-codex');
        const opencodeTokenSaverToggle = root.querySelector('#setting-token-saver-opencode');
        if (claudeTokenSaverToggle) claudeTokenSaverToggle.checked = settings.claudeTokenSaverEnabled !== false;
        if (codexTokenSaverToggle) codexTokenSaverToggle.checked = settings.codexTokenSaverEnabled !== false;
        if (opencodeTokenSaverToggle) opencodeTokenSaverToggle.checked = settings.openCodeTokenSaverEnabled !== false;

        const tokenSaverCaptureToggle = root.querySelector('#setting-token-saver-capture');
        if (tokenSaverCaptureToggle) tokenSaverCaptureToggle.checked = settings.tokenSaverCaptureEnabled === true;

        const removeCoAuthorTrailersToggle = root.querySelector('#setting-remove-co-author-trailers');
        if (removeCoAuthorTrailersToggle) removeCoAuthorTrailersToggle.checked = settings.removeCoAuthorTrailers !== false;

        this._updateDataExportAvailability(root);
    }

    _updateDataExportAvailability(root) {
        const wrapper = root.querySelector('#settings-export-data-wrapper');
        const button = root.querySelector('#settings-export-data-button');
        const apiKeyInput = root.querySelector('#setting-api-key');
        const savedValue = apiKeyInput?.dataset.originalValue || '';
        // Also requires a configured export URL: the shipped placeholder is rejected server-side,
        // so without this the button would be visible and enabled for every user with a key and
        // could only ever return "Data export is not configured."
        const available = this._dataExportConfigured === true
            && savedValue.trim().length > 0
            && apiKeyInput?.value === savedValue;

        if (wrapper) {
            wrapper.hidden = !available;
        }
        if (button) {
            button.disabled = !available || this._dataExportInProgress;
            button.textContent = this._getDataExportButtonText();
        }
    }

    async _loadDataExportSize() {
        this._dataExportSizeBytes = null;
        try {
            const response = await this.app.apiCall(
                '/api/v1/settings/db-size',
                'GET',
                null,
                { showLoading: false }
            );
            const bytes = Number(response?.bytes);
            if (Number.isFinite(bytes) && bytes >= 0) {
                this._dataExportSizeBytes = bytes;
            }
        } catch (error) {
            // Size is supplemental display information. Keep export available if an older
            // backend or a transient file-system problem cannot provide it.
            console.error('Failed to fetch state database size:', error);
        }
    }

    _getDataExportButtonText() {
        const action = this._dataExportInProgress ? 'Exporting…' : 'Export Data';
        const size = this._formatBytes(this._dataExportSizeBytes);
        return size ? `${action} (${size})` : action;
    }

    _formatBytes(bytes) {
        if (!Number.isFinite(bytes) || bytes < 0) {
            return '';
        }

        const units = ['B', 'KB', 'MB', 'GB', 'TB'];
        let value = bytes;
        let unitIndex = 0;
        while (value >= 1024 && unitIndex < units.length - 1) {
            value /= 1024;
            unitIndex++;
        }

        const decimals = value >= 100 || unitIndex === 0
            ? 0
            : value >= 10
                ? 1
                : 2;
        return `${value.toFixed(decimals)} ${units[unitIndex]}`;
    }

    async _exportData(root) {
        if (this._dataExportInProgress) {
            return;
        }

        const apiKeyInput = root.querySelector('#setting-api-key');
        const savedValue = apiKeyInput?.dataset.originalValue || '';
        if (this._dataExportConfigured !== true
            || savedValue.trim().length === 0
            || apiKeyInput?.value !== savedValue) {
            this._updateDataExportAvailability(root);
            return;
        }

        this._dataExportInProgress = true;
        this._updateDataExportAvailability(root);
        try {
            const result = await this.app.apiCall(
                '/api/v1/settings/export-data',
                'POST',
                null,
                { showLoading: false }
            );
            this.app.showToast(
                'Data Export',
                result?.message || (result?.success ? 'Data exported successfully.' : 'Failed to export data.'),
                result?.success ? 'success' : 'error'
            );
        } catch (error) {
            this.app.showToast(
                'Data Export',
                error?.message || 'Failed to export data.',
                'error'
            );
        } finally {
            this._dataExportInProgress = false;
            this._updateDataExportAvailability(root);
            if (this._settingsRoot && this._settingsRoot !== root) {
                this._updateDataExportAvailability(this._settingsRoot);
            }
        }
    }

    _getCodexLlmProxyMode(root) {
        const selected = root.querySelector('input[name="setting-codex-llm-proxy-mode"]:checked');
        return selected?.value === 'api' ? 'api' : 'subscription';
    }

    // ── PIN section ────────────────────────────────────────────────────────

    async _initPinSection(root) {
        const badge = root.querySelector('#pin-status-badge');
        const btnSet = root.querySelector('#btn-set-pin');
        const btnClear = root.querySelector('#btn-clear-pin');

        let pinIsSet = false;
        try {
            const status = await this.app.apiCall('/api/v1/settings/pin/status', 'GET');
            pinIsSet = status.isSet;
        } catch (error) {
            console.error('Failed to fetch PIN status:', error);
        }

        this._pinIsSet = pinIsSet;
        this._updatePinUI(badge, btnSet, btnClear, pinIsSet);

        btnSet.addEventListener('click', () => {
            this._showPinModal(async (pin) => {
                try {
                    await this.app.apiCall('/api/v1/settings/pin', 'POST', { pin });
                    this.app.closeModal();
                    this._pinIsSet = true;
                    this._updatePinUI(badge, btnSet, btnClear, true);
                    this.app.showToast('PIN Lock', 'PIN set successfully', 'success');
                } catch (error) {
                    throw error;
                }
            });
        });

        btnClear.addEventListener('click', async () => {
            try {
                await this.app.apiCall('/api/v1/settings/pin', 'DELETE');
                this._pinIsSet = false;
                this._updatePinUI(badge, btnSet, btnClear, false);
                this.app.showToast('PIN Lock', 'PIN cleared', 'success');
                // Revert remote access toggle if it was enabled
                const remoteToggle = root.querySelector('#setting-remote-access');
                if (remoteToggle?.checked) {
                    remoteToggle.checked = false;
                    this._updateDirtyState(root);
                    this.app.showToast('Remote Access', 'Remote access disabled because PIN was cleared.', 'warning');
                }
            } catch (error) {
                this.app.showError('Failed to clear PIN: ' + error.message);
            }
        });
    }

    _updatePinUI(badge, btnSet, btnClear, isSet) {
        if (isSet) {
            badge.className = 'pin-status-badge set';
            badge.innerHTML = `
                <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" fill="currentColor" viewBox="0 0 16 16">
                    <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                </svg>
                PIN Set`;
            btnSet.textContent = '';
            btnSet.innerHTML = `
                <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                    <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                </svg>
                Change PIN`;
            btnClear.style.removeProperty('display');
        } else {
            badge.className = 'pin-status-badge not-set';
            badge.innerHTML = `
                <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" fill="currentColor" viewBox="0 0 16 16">
                    <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                </svg>
                Not Set`;
            btnSet.innerHTML = `
                <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                    <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                </svg>
                Set PIN`;
            btnClear.style.setProperty('display', 'none', 'important');
        }
    }

    _showPinModal(onConfirm) {
        const MAX_DIGITS = 6;
        const MIN_DIGITS = 4;

        const modalHtml = `
            <div class="text-center">
                <p class="pin-modal-subtitle">Enter a ${MIN_DIGITS}–${MAX_DIGITS} digit PIN to protect remote viewer access</p>
                <div class="pin-digit-row" id="pin-digit-row">
                    ${Array.from({ length: MAX_DIGITS }, (_, i) => `
                        <input
                            class="pin-digit"
                            type="password"
                            inputmode="numeric"
                            maxlength="1"
                            pattern="[0-9]"
                            autocomplete="off"
                            data-pin-index="${i}"
                            aria-label="PIN digit ${i + 1}"
                        >`).join('')}
                </div>
                <div class="pin-length-hint" id="pin-length-hint">Enter ${MIN_DIGITS}–${MAX_DIGITS} digits</div>
                <div class="pin-actions">
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                    <button type="button" class="btn btn-primary" id="btn-pin-confirm" disabled>
                        <svg xmlns="http://www.w3.org/2000/svg" width="15" height="15" fill="currentColor" class="me-1" viewBox="0 0 16 16">
                            <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                        </svg>
                        Set PIN
                    </button>
                </div>
                <div id="pin-error-msg" class="text-danger mt-2" style="min-height:1.2em;font-size:0.82rem;"></div>
            </div>
        `;

        const titleHtml = `
            <span class="pin-modal-title">
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" fill="currentColor" viewBox="0 0 16 16">
                    <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                </svg>
                Set Remote PIN
            </span>`;

        this.app.showModal(titleHtml, modalHtml);

        // After modal is in DOM, wire up the digit inputs
        const container = document.getElementById('modal-container');
        const digits = Array.from(container.querySelectorAll('.pin-digit'));
        const confirmBtn = container.querySelector('#btn-pin-confirm');
        const hintEl = container.querySelector('#pin-length-hint');
        const errorEl = container.querySelector('#pin-error-msg');

        // Override modal title with raw HTML (showModal escapes, so patch it)
        const titleEl = container.querySelector('.modal-title');
        if (titleEl) titleEl.innerHTML = titleHtml;

        // Narrow the modal dialog
        const dialogEl = container.querySelector('.modal-dialog');
        if (dialogEl) dialogEl.classList.add('pin-modal-dialog');

        const getPin = () => digits.map(d => d.value).join('').replace(/\D/g, '');

        const updateState = () => {
            const pin = getPin();
            const len = pin.length;

            digits.forEach((d, i) => {
                d.classList.toggle('filled', d.value !== '');
            });

            if (len === 0) {
                hintEl.textContent = `Enter ${MIN_DIGITS}–${MAX_DIGITS} digits`;
            } else if (len < MIN_DIGITS) {
                hintEl.textContent = `${MIN_DIGITS - len} more digit${MIN_DIGITS - len > 1 ? 's' : ''} needed`;
            } else {
                hintEl.textContent = `${len} digit${len > 1 ? 's' : ''} entered — looks good!`;
            }

            confirmBtn.disabled = len < MIN_DIGITS;
            errorEl.textContent = '';
        };

        digits.forEach((input, idx) => {
            input.addEventListener('keydown', (e) => {
                if (e.key === 'Backspace') {
                    if (input.value === '' && idx > 0) {
                        digits[idx - 1].value = '';
                        digits[idx - 1].focus();
                        updateState();
                        e.preventDefault();
                    }
                } else if (e.key === 'ArrowLeft' && idx > 0) {
                    digits[idx - 1].focus();
                    e.preventDefault();
                } else if (e.key === 'ArrowRight' && idx < digits.length - 1) {
                    digits[idx + 1].focus();
                    e.preventDefault();
                } else if (e.key === 'Enter') {
                    const pin = getPin();
                    if (pin.length >= MIN_DIGITS) confirmBtn.click();
                }
            });

            input.addEventListener('input', (e) => {
                const raw = input.value.replace(/\D/g, '');
                if (raw.length === 0) {
                    input.value = '';
                    updateState();
                    return;
                }

                // Handle paste across multiple digits
                if (raw.length > 1) {
                    const chars = raw.split('');
                    chars.forEach((ch, offset) => {
                        if (idx + offset < digits.length) {
                            digits[idx + offset].value = ch;
                        }
                    });
                    const nextIdx = Math.min(idx + chars.length, digits.length - 1);
                    digits[nextIdx].focus();
                    updateState();
                    return;
                }

                input.value = raw;
                updateState();

                if (idx < digits.length - 1) {
                    digits[idx + 1].focus();
                }
            });
        });

        confirmBtn.addEventListener('click', async () => {
            const pin = getPin();
            if (pin.length < MIN_DIGITS) return;

            confirmBtn.disabled = true;
            confirmBtn.innerHTML = `<span class="spinner-border spinner-border-sm me-1"></span> Saving…`;

            try {
                await onConfirm(pin);
            } catch (error) {
                // Flash error state on all filled digits
                digits.forEach(d => { if (d.value) d.classList.add('error'); });
                setTimeout(() => digits.forEach(d => d.classList.remove('error')), 600);
                errorEl.textContent = error.message || 'Failed to set PIN';
                confirmBtn.disabled = false;
                confirmBtn.innerHTML = `
                    <svg xmlns="http://www.w3.org/2000/svg" width="15" height="15" fill="currentColor" class="me-1" viewBox="0 0 16 16">
                        <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                    </svg>
                    Set PIN`;
            }
        });

        // Focus first digit after render
        requestAnimationFrame(() => digits[0]?.focus());
    }
}
