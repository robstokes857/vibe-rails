import { confirmDialog } from './utils.js';
import { remotePasswordError } from './remote-access-password.js';
import { SettingsKeysPanel } from './settings-keys.js';
import { SetupPanel, refreshSetupStatus } from './setup-panel.js';
import { SetupInstallWindow } from './setup-install-window.js';
import { AiBoardManagerSettingsPanel } from './settings-ai-board-manager.js';
import { applyGatewaySettings, gatewayCredentialEdits, initGatewaySettings } from './settings-enterprise-gateway.js';

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
        this._settingsRoot = null;
        this._keysPanel = null;
        this._setupPanel = null;
        // Outlives the Setup panel (see _mountSetupPanel), so it is never cleared on unload.
        this._setupInstallWindow = null;
        this._selectSettingsTab = null;
        this._linkedKeyVersion = 0;
        // In-app leave-confirm (window.confirm is a silent no-op in the VS Code
        // webview). A field so tests can substitute a resolved value.
        this.confirmLeave = confirmDialog;
        this._leaveConfirmPending = false;
        this._leaveSettingsConfirmed = false;
    }

    async loadSettings(data = {}) {
        this.unload();

        const content = document.getElementById('app-content');
        if (!content) return;

        let settings = {
            remoteAccess: false,
            apiKey: '',
            useVsCodeTheme: false,
            vsCodeSpellCheck: false,
            mcpEnabled: true,
            computerName: '',
            codexEnterpriseNetworkProxy: '',
            codexLlmProxyMode: 'subscription',
            claudeEnterpriseNetworkProxy: '',
            openCodeEnterpriseNetworkProxy: '',
            grokEnterpriseNetworkProxy: '',
            // Per-LLM saver toggles independently control compression; local routing is always on.
            claudeTokenSaverEnabled: true,
            codexTokenSaverEnabled: true,
            openCodeTokenSaverEnabled: true,
            machineName: ''
        };
        try {
            settings = await this.app.apiCall('/api/v1/settings', 'GET');
            this.app.setAppSettings(settings);
        } catch (error) {
            console.error('Failed to fetch settings:', error);
        }
        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('settings-template');
        const root = fragment.querySelector('[data-view="settings"]');

        if (root) {
            this._settingsRoot = root;
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            this.app.bindAction(root, '[data-open-context]', event => {
                event.preventDefault();
                this.app.navigate('context');
            });
            this._initSettingsTabs(root);
            if (data.tab) this._selectSettingsTab?.(data.tab);
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
            const performanceModeToggle = root.querySelector('#setting-performance-mode');
            const useVsCodeThemeRow = root.querySelector('#setting-use-vscode-theme-row');
            const useVsCodeThemeToggle = root.querySelector('#setting-use-vscode-theme');
            const vsCodeSpellCheckToggle = root.querySelector('#setting-vscode-spell-check');
            const mcpEnabledToggle = root.querySelector('#setting-enable-mcp');
            const computerNameInput = root.querySelector('#setting-computer-name');
            const codexEnterpriseNetworkProxyInput = root.querySelector('#setting-codex-enterprise-network-proxy');
            const codexLlmProxyModeSubscription = root.querySelector('#setting-codex-llm-proxy-mode-subscription');
            const codexLlmProxyModeApi = root.querySelector('#setting-codex-llm-proxy-mode-api');
            const claudeEnterpriseNetworkProxyInput = root.querySelector('#setting-claude-enterprise-network-proxy');
            const opencodeEnterpriseNetworkProxyInput = root.querySelector('#setting-opencode-enterprise-network-proxy');
            const grokEnterpriseNetworkProxyInput = root.querySelector('#setting-grok-enterprise-network-proxy');
            const grokLlmProxyModeSubscription = root.querySelector('#setting-grok-llm-proxy-mode-subscription');
            const grokLlmProxyModeApi = root.querySelector('#setting-grok-llm-proxy-mode-api');
            const claudeTokenSaverToggle = root.querySelector('#setting-token-saver-claude');
            const codexTokenSaverToggle = root.querySelector('#setting-token-saver-codex');
            const opencodeTokenSaverToggle = root.querySelector('#setting-token-saver-opencode');
            const grokTokenSaverToggle = root.querySelector('#setting-token-saver-grok');

            if (remoteAccessToggle) {
                remoteAccessToggle.checked = settings.remoteAccess || false;
                remoteAccessToggle.addEventListener('change', () => {
                    if (remoteAccessToggle.checked && !this._pinIsSet) {
                        remoteAccessToggle.checked = false;
                        this.app.showToast('Remote Access', 'Create a remote access password before enabling remote access.', 'warning');
                        this._updateDirtyState(root);
                    }
                });
            }
            if (apiKeyInput) {
                apiKeyInput.value = settings.apiKey || '';
                apiKeyInput.dataset.originalValue = settings.apiKey || '';
            }
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
            if (vsCodeSpellCheckToggle) vsCodeSpellCheckToggle.checked = settings.vsCodeSpellCheck === true;
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
            if (codexEnterpriseNetworkProxyInput) {
                codexEnterpriseNetworkProxyInput.value = settings.codexEnterpriseNetworkProxy || '';
            }
            const codexLlmProxyMode = settings.codexLlmProxyMode === 'api' ? 'api' : 'subscription';
            if (codexLlmProxyModeSubscription) codexLlmProxyModeSubscription.checked = codexLlmProxyMode === 'subscription';
            if (codexLlmProxyModeApi) codexLlmProxyModeApi.checked = codexLlmProxyMode === 'api';
            if (claudeEnterpriseNetworkProxyInput) {
                claudeEnterpriseNetworkProxyInput.value = settings.claudeEnterpriseNetworkProxy || '';
            }
            if (opencodeEnterpriseNetworkProxyInput) {
                opencodeEnterpriseNetworkProxyInput.value = settings.openCodeEnterpriseNetworkProxy || '';
            }
            if (grokEnterpriseNetworkProxyInput) {
                grokEnterpriseNetworkProxyInput.value = settings.grokEnterpriseNetworkProxy || '';
            }
            const grokLlmProxyMode = settings.grokLlmProxyMode === 'api' ? 'api' : 'subscription';
            if (grokLlmProxyModeSubscription) grokLlmProxyModeSubscription.checked = grokLlmProxyMode === 'subscription';
            if (grokLlmProxyModeApi) grokLlmProxyModeApi.checked = grokLlmProxyMode === 'api';
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
            if (grokTokenSaverToggle) {
                grokTokenSaverToggle.checked = settings.grokTokenSaverEnabled !== false;
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
                        this.app.showToast('Remote Access', 'Create a remote access password before enabling remote access.', 'warning');
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
                    const linkedKeyVersion = this._linkedKeyVersion;
                    this._updateSaveBar(root);
                    try {
                        const savedSettings = await this.saveSettings(
                            wantsRemote,
                            apiKeyChanged ? apiKeyValue : '',
                            useVsCodeThemeToggle?.checked || false,
                            true,
                            computerNameInput?.value || '',
                            codexEnterpriseNetworkProxyInput?.value.trim() || '',
                            this._getCodexLlmProxyMode(root),
                            claudeEnterpriseNetworkProxyInput?.value.trim() || '',
                            opencodeEnterpriseNetworkProxyInput?.value.trim() || '',
                            grokEnterpriseNetworkProxyInput?.value.trim() || '',
                            this._getGrokLlmProxyMode(root),
                            claudeTokenSaverToggle?.checked ?? true,
                            codexTokenSaverToggle?.checked ?? true,
                            opencodeTokenSaverToggle?.checked ?? true,
                            grokTokenSaverToggle?.checked ?? true,
                            clearApiKey,
                            gatewayCredentialEdits(root),
                            vsCodeSpellCheckToggle?.checked ?? false
                        );
                        if (savedSettings) {
                            await this._reconcileSavedApiKey(savedSettings, linkedKeyVersion);
                            if (this._settingsRoot !== root) return;
                            this.app.setAppSettings({
                                apiKey: savedSettings.apiKey, remoteAccountEmail: savedSettings.remoteAccountEmail,
                                remoteAccountOrganization: savedSettings.remoteAccountOrganization ?? null
                            });
                            this._applySavedSettingsToControls(root, savedSettings);
                            this._markSettingsClean(root);
                            // A pasted key or clear can supersede the linked identity.
                            this.app.updateAccountNav?.();
                        }
                    } finally {
                        this._settingsSaving = false;
                        this._updateSaveBar(root);
                    }
                });
            }

            initGatewaySettings(root, settings);
            this._initSettingsDirtyTracking(root);
            this._initEnterpriseProxyTests(root);

        }

        // Attach before the PIN status round-trip: #app-content was blanked above,
        // and awaiting a network call before appending left the page empty for a
        // full RTT on every Settings visit. The PIN section just pops in when its
        // status lands.
        content.appendChild(fragment);
        this.app.updateAccountNav?.();

        if (root) {
            await this._initPinSection(root);
        }
    }

    async saveSettings(remoteAccess, apiKey, useVsCodeTheme, mcpEnabled, computerName, codexEnterpriseNetworkProxy, codexLlmProxyMode, claudeEnterpriseNetworkProxy, openCodeEnterpriseNetworkProxy, grokEnterpriseNetworkProxy, grokLlmProxyMode, claudeTokenSaverEnabled, codexTokenSaverEnabled, openCodeTokenSaverEnabled, grokTokenSaverEnabled, clearApiKey = false, gatewayCredentials = {}, vsCodeSpellCheck = false) {
        try {
            const previous = this.app.appSettings || {};
            // A new gateway receives that CLI's traffic and credentials; clearing one does not
            // need confirmation. The server makes the same decision on normalized values.
            const pointsGatewayElsewhere = (value, saved) => !!value && value !== (saved || '');
            const sensitive = remoteAccess !== (previous.remoteAccess ?? false) || clearApiKey
                || (apiKey && !apiKey.includes('•') && apiKey !== previous.apiKey)
                || pointsGatewayElsewhere(codexEnterpriseNetworkProxy, previous.codexEnterpriseNetworkProxy)
                || pointsGatewayElsewhere(claudeEnterpriseNetworkProxy, previous.claudeEnterpriseNetworkProxy)
                || pointsGatewayElsewhere(openCodeEnterpriseNetworkProxy, previous.openCodeEnterpriseNetworkProxy)
                || pointsGatewayElsewhere(grokEnterpriseNetworkProxy, previous.grokEnterpriseNetworkProxy);
            const presenceProof = sensitive ? await this._requestPresenceProof() : null;
            if (sensitive && presenceProof === null) return null;
            const savedSettings = await this.app.apiCall('/api/v1/settings', 'POST', {
                presenceProof,
                ...gatewayCredentials,
                remoteAccess: remoteAccess,
                apiKey: apiKey,
                useVsCodeTheme: useVsCodeTheme,
                vsCodeSpellCheck,
                mcpEnabled: mcpEnabled,
                computerName: computerName,
                codexEnterpriseNetworkProxy: codexEnterpriseNetworkProxy,
                codexLlmProxyMode: codexLlmProxyMode,
                claudeEnterpriseNetworkProxy: claudeEnterpriseNetworkProxy,
                openCodeEnterpriseNetworkProxy: openCodeEnterpriseNetworkProxy,
                grokEnterpriseNetworkProxy: grokEnterpriseNetworkProxy,
                grokLlmProxyMode: grokLlmProxyMode,
                claudeTokenSaverEnabled: claudeTokenSaverEnabled,
                codexTokenSaverEnabled: codexTokenSaverEnabled,
                openCodeTokenSaverEnabled: openCodeTokenSaverEnabled,
                grokTokenSaverEnabled: grokTokenSaverEnabled,
                clearApiKey: clearApiKey,
                dataExportOptIn: true
            }, { preferErrorResponseMessage: true });
            this.app.setAppSettings(savedSettings);
            this.app.showToast('Settings', 'Settings saved successfully', 'success');
            return savedSettings;
        } catch (error) {
            this.app.showError('Failed to save settings: ' + error.message);
            return null;
        }
    }

    unload() {
        this._aiBoardManagerPanel?.unload();
        this._aiBoardManagerPanel = null;
        this._keysPanel?.unload();
        this._keysPanel = null;
        this._setupPanel?.unload();
        this._setupPanel = null;
        this._selectSettingsTab = null;
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
        this._settingsRoot = null;
    }

    _initEnterpriseProxyTests(root) {
        root.querySelectorAll('[data-enterprise-proxy-test]').forEach((button) => {
            const provider = button.dataset.enterpriseProxyTest;
            const input = root.querySelector(`#setting-${provider}-enterprise-network-proxy`);
            const output = root.querySelector(`#setting-${provider}-enterprise-network-proxy-result`);
            if (!input || !output) return;
            // A result describes the URL it tested; editing the field retires it.
            input.addEventListener('input', () => {
                output.hidden = true;
                output.textContent = '';
            });
            button.addEventListener('click', () => this._runEnterpriseProxyTest(provider, input, output, button));
        });
    }

    /**
     * Tests the gateway URL as entered, saved or not: the server sends one request without
     * credentials through the relay's own transport and reports whether the gateway answers. When
     * it finds a corrected base (usually a missing /v1), the result offers to use it.
     */
    async _runEnterpriseProxyTest(provider, input, output, button) {
        const show = (message, state) => {
            output.hidden = false;
            output.className = `small mt-1 ${state === 'ok' ? 'text-success' : state === 'busy' ? 'text-muted' : 'text-danger'}`;
            output.textContent = message;
        };
        const url = input.value.trim();
        if (!url) {
            show("Enter a gateway URL to test. Blank uses the provider's own endpoint.", 'fail');
            return;
        }
        button.disabled = true;
        show('Testing…', 'busy');
        try {
            const result = await this.app.apiCall('/api/v1/settings/enterprise-network-proxy/test', 'POST',
                { provider, url }, { showLoading: false, preferErrorResponseMessage: true });
            if (input.value.trim() !== url) return;
            show(result.message, result.ok ? 'ok' : 'fail');
            if (result.suggestedUrl) {
                const use = document.createElement('button');
                use.type = 'button';
                use.className = 'btn btn-link btn-sm p-0 ms-1 align-baseline';
                use.textContent = `Use ${result.suggestedUrl}`;
                use.addEventListener('click', () => {
                    input.value = result.suggestedUrl;
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                    show('Updated. Save to apply it.', 'ok');
                });
                output.append(' ', use);
            }
        } catch (error) {
            if (input.value.trim() === url) show(`Test failed: ${error.message}`, 'fail');
        } finally {
            button.disabled = false;
        }
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

        this._removeNavigationGuard = this.app.registerNavigationGuard?.(
            payload => this._guardSettingsNavigation(payload)) || null;

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

    // Guards are synchronous, but the in-app confirm is not (window.confirm is a
    // silent no-op in the VS Code webview — it used to force a bypass here that
    // silently discarded webview edits). So: block the navigation now, ask, and
    // replay the exact navigation via `retry` if the user opts to leave.
    _guardSettingsNavigation({ from, retry }) {
        if (from !== 'settings' || !this._settingsDirty) {
            return true;
        }
        if (this._leaveSettingsConfirmed) {
            return true;
        }
        void this._confirmLeaveSettings(retry);
        return false;
    }

    async _confirmLeaveSettings(retry) {
        if (this._leaveConfirmPending) return;
        this._leaveConfirmPending = true;
        try {
            const leave = await this.confirmLeave({
                title: 'Leave Settings?',
                message: 'You have unsaved settings changes that will be lost.',
                confirmLabel: 'Leave without saving',
                cancelLabel: 'Stay',
                danger: true
            });
            if (!leave) return;
            this._leaveSettingsConfirmed = true;
            try { retry?.(); } finally { this._leaveSettingsConfirmed = false; }
        } finally {
            this._leaveConfirmPending = false;
        }
    }

    _trackedSettingsSelector() {
        return [
            '#setting-remote-access',
            '#setting-api-key',
            '#setting-use-vscode-theme',
            '#setting-vscode-spell-check',
            '#setting-computer-name',
            '#setting-codex-gateway-auth-mode',
            '#setting-codex-gateway-api-key-header',
            '#setting-claude-gateway-auth-mode',
            '#setting-claude-gateway-api-key-header',
            '#setting-codex-gateway-token',
            '#setting-codex-gateway-token-clear',
            '#setting-claude-gateway-token',
            '#setting-claude-gateway-token-clear',
            '#setting-claude-gateway-tool-search',
            '#setting-codex-enterprise-network-proxy',
            'input[name="setting-codex-llm-proxy-mode"]',
            '#setting-claude-enterprise-network-proxy',
            '#setting-opencode-enterprise-network-proxy',
            '#setting-grok-enterprise-network-proxy',
            'input[name="setting-grok-llm-proxy-mode"]',
            '#setting-token-saver-claude',
            '#setting-token-saver-codex',
            '#setting-token-saver-opencode',
            '#setting-token-saver-grok'
        ].join(',');
    }

    _captureSettingsSnapshot(root) {
        const isChecked = (selector) => root.querySelector(selector)?.checked === true;
        const valueOf = (selector) => root.querySelector(selector)?.value || '';

        return JSON.stringify({
            ...gatewayCredentialEdits(root),
            remoteAccess: isChecked('#setting-remote-access'),
            apiKey: valueOf('#setting-api-key'),
            useVsCodeTheme: isChecked('#setting-use-vscode-theme'),
            vsCodeSpellCheck: isChecked('#setting-vscode-spell-check'),
            computerName: valueOf('#setting-computer-name'),
            codexEnterpriseNetworkProxy: (root.querySelector('#setting-codex-enterprise-network-proxy')?.value || '').trim(),
            codexLlmProxyMode: this._getCodexLlmProxyMode(root),
            claudeEnterpriseNetworkProxy: (root.querySelector('#setting-claude-enterprise-network-proxy')?.value || '').trim(),
            openCodeEnterpriseNetworkProxy: (root.querySelector('#setting-opencode-enterprise-network-proxy')?.value || '').trim(),
            grokEnterpriseNetworkProxy: (root.querySelector('#setting-grok-enterprise-network-proxy')?.value || '').trim(),
            grokLlmProxyMode: this._getGrokLlmProxyMode(root),
            claudeTokenSaverEnabled: isChecked('#setting-token-saver-claude'),
            codexTokenSaverEnabled: isChecked('#setting-token-saver-codex'),
            openCodeTokenSaverEnabled: isChecked('#setting-token-saver-opencode'),
            grokTokenSaverEnabled: isChecked('#setting-token-saver-grok')
        });
    }

    _updateDirtyState(root) {
        this._settingsDirty = this._captureSettingsSnapshot(root) !== this._settingsSnapshot;
        this._updateSaveBar(root);
    }

    _markSettingsClean(root) {
        this._settingsSnapshot = this._captureSettingsSnapshot(root);
        this._settingsDirty = false;
        this._updateSaveBar(root);
    }

    _applyLinkedApiKey(root, keyHint) {
        if (this._settingsRoot !== root || typeof keyHint !== 'string' || !keyHint) return;
        const input = root.querySelector('#setting-api-key');
        if (!input) return;
        input.value = keyHint;
        input.dataset.originalValue = keyHint;
        this._linkedKeyVersion++;
        // Only the key was saved by sign-in. Keep every other field's draft and original
        // baseline, so the save bar still reports any unrelated unsaved edits.
        const snapshot = JSON.parse(this._settingsSnapshot || '{}');
        snapshot.apiKey = keyHint;
        this._settingsSnapshot = JSON.stringify(snapshot);
        this.app.setAppSettings({ apiKey: keyHint });
        this._updateDirtyState(root);
    }

    async _reconcileSavedApiKey(savedSettings, linkedKeyVersion) {
        if (linkedKeyVersion === this._linkedKeyVersion) return;
        // Save and sign-in can finish in either order. An event is not proof that its
        // key won: a manual clear may have committed after it. Read the saved mask.
        try {
            let latest;
            do {
                linkedKeyVersion = this._linkedKeyVersion;
                latest = await this.app.apiCall('/api/v1/settings', 'GET', null, { showLoading: false });
            } while (this._settingsRoot && linkedKeyVersion !== this._linkedKeyVersion);
            savedSettings.apiKey = latest.apiKey || '';
            savedSettings.remoteAccountEmail = latest.remoteAccountEmail;
            savedSettings.remoteAccountOrganization = latest.remoteAccountOrganization;
        } catch {
            this.app.showToast('Settings', 'Settings saved. Reopen Settings when your connection is available to refresh the account details.', 'warning');
        }
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
        const vsCodeSpellCheckToggle = root.querySelector('#setting-vscode-spell-check');
        const computerNameInput = root.querySelector('#setting-computer-name');
        const codexEnterpriseNetworkProxyInput = root.querySelector('#setting-codex-enterprise-network-proxy');
        const codexLlmProxyModeSubscription = root.querySelector('#setting-codex-llm-proxy-mode-subscription');
        const codexLlmProxyModeApi = root.querySelector('#setting-codex-llm-proxy-mode-api');
        const claudeEnterpriseNetworkProxyInput = root.querySelector('#setting-claude-enterprise-network-proxy');
        const opencodeEnterpriseNetworkProxyInput = root.querySelector('#setting-opencode-enterprise-network-proxy');
        const grokEnterpriseNetworkProxyInput = root.querySelector('#setting-grok-enterprise-network-proxy');
        const grokLlmProxyModeSubscription = root.querySelector('#setting-grok-llm-proxy-mode-subscription');
        const grokLlmProxyModeApi = root.querySelector('#setting-grok-llm-proxy-mode-api');

        if (remoteAccessToggle) remoteAccessToggle.checked = settings.remoteAccess === true;
        if (apiKeyInput) {
            apiKeyInput.value = settings.apiKey || '';
            apiKeyInput.dataset.originalValue = settings.apiKey || '';
        }
        if (useVsCodeThemeToggle) useVsCodeThemeToggle.checked = settings.useVsCodeTheme === true;
        if (vsCodeSpellCheckToggle) vsCodeSpellCheckToggle.checked = settings.vsCodeSpellCheck === true;
        if (computerNameInput) {
            computerNameInput.value = settings.computerName || '';
            if (settings.machineName) computerNameInput.placeholder = settings.machineName;
        }
        if (codexEnterpriseNetworkProxyInput) codexEnterpriseNetworkProxyInput.value = settings.codexEnterpriseNetworkProxy || '';

        const codexLlmProxyMode = settings.codexLlmProxyMode === 'api' ? 'api' : 'subscription';
        if (codexLlmProxyModeSubscription) codexLlmProxyModeSubscription.checked = codexLlmProxyMode === 'subscription';
        if (codexLlmProxyModeApi) codexLlmProxyModeApi.checked = codexLlmProxyMode === 'api';
        if (claudeEnterpriseNetworkProxyInput) claudeEnterpriseNetworkProxyInput.value = settings.claudeEnterpriseNetworkProxy || '';
        if (opencodeEnterpriseNetworkProxyInput) opencodeEnterpriseNetworkProxyInput.value = settings.openCodeEnterpriseNetworkProxy || '';
        if (grokEnterpriseNetworkProxyInput) grokEnterpriseNetworkProxyInput.value = settings.grokEnterpriseNetworkProxy || '';
        const grokLlmProxyMode = settings.grokLlmProxyMode === 'api' ? 'api' : 'subscription';
        if (grokLlmProxyModeSubscription) grokLlmProxyModeSubscription.checked = grokLlmProxyMode === 'subscription';
        if (grokLlmProxyModeApi) grokLlmProxyModeApi.checked = grokLlmProxyMode === 'api';

        const claudeTokenSaverToggle = root.querySelector('#setting-token-saver-claude');
        const codexTokenSaverToggle = root.querySelector('#setting-token-saver-codex');
        const opencodeTokenSaverToggle = root.querySelector('#setting-token-saver-opencode');
        const grokTokenSaverToggle = root.querySelector('#setting-token-saver-grok');
        if (claudeTokenSaverToggle) claudeTokenSaverToggle.checked = settings.claudeTokenSaverEnabled !== false;
        if (codexTokenSaverToggle) codexTokenSaverToggle.checked = settings.codexTokenSaverEnabled !== false;
        if (opencodeTokenSaverToggle) opencodeTokenSaverToggle.checked = settings.openCodeTokenSaverEnabled !== false;
        if (grokTokenSaverToggle) grokTokenSaverToggle.checked = settings.grokTokenSaverEnabled !== false;
        applyGatewaySettings(root, settings);
    }

    _getCodexLlmProxyMode(root) {
        const selected = root.querySelector('input[name="setting-codex-llm-proxy-mode"]:checked');
        return selected?.value === 'api' ? 'api' : 'subscription';
    }

    _getGrokLlmProxyMode(root) {
        const selected = root.querySelector('input[name="setting-grok-llm-proxy-mode"]:checked');
        return selected?.value === 'api' ? 'api' : 'subscription';
    }

    // ── Section tabs ───────────────────────────────────────────────────────

    // Panels (and every input inside them) stay in the DOM; only visibility
    // changes. Dirty tracking, the save bar, and form submission therefore keep
    // reading controls that live on a hidden tab.
    _initSettingsTabs(root) {
        const tablist = root.querySelector('.settings-tabs');
        if (!tablist) return;

        const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));
        if (tabs.length === 0) return;

        const panelFor = (tab) => root.querySelector(`#${tab.getAttribute('aria-controls')}`);

        const selectTab = (id, focusTab = false) => {
            tabs.forEach((tab) => {
                const selected = tab.dataset.settingsTab === id;
                tab.setAttribute('aria-selected', String(selected));
                tab.tabIndex = selected ? 0 : -1;
                const panel = panelFor(tab);
                if (panel) panel.hidden = !selected;
            });
            if (focusTab) {
                tabs.find((tab) => tab.dataset.settingsTab === id)?.focus();
            }
            if (id === 'keys') {
                const container = root.querySelector('[data-settings-keys]');
                if (container) {
                    this._keysPanel ??= new SettingsKeysPanel(this.app, container);
                    void this._keysPanel.activate();
                }
            } else {
                this._keysPanel?.clearSecrets();
            }
            if (id === 'setup') {
                const container = root.querySelector('[data-settings-setup]');
                if (container) {
                    this._setupPanel ??= this._mountSetupPanel(container);
                    void this._setupPanel.activate();
                }
            }
            if (id === 'ai-board-manager') {
                const container = root.querySelector('[data-settings-ai-board-manager]');
                if (container) {
                    this._aiBoardManagerPanel ??= new AiBoardManagerSettingsPanel(this.app, container);
                    void this._aiBoardManagerPanel.load();
                }
            }
        };
        this._selectSettingsTab = selectTab;

        tablist.addEventListener('click', (event) => {
            const tab = event.target.closest('[data-settings-tab]');
            if (tab) selectTab(tab.dataset.settingsTab);
        });

        tablist.addEventListener('keydown', (event) => {
            const current = tabs.indexOf(event.target);
            if (current === -1) return;
            let next = -1;
            if (event.key === 'ArrowRight') next = (current + 1) % tabs.length;
            else if (event.key === 'ArrowLeft') next = (current + tabs.length - 1) % tabs.length;
            else if (event.key === 'Home') next = 0;
            else if (event.key === 'End') next = tabs.length - 1;
            if (next === -1) return;
            event.preventDefault();
            selectTab(tabs[next].dataset.settingsTab, true);
        });
    }

    // Leaving Settings drops the panel but not an install in progress: every panel shares one
    // install window, so a second install cannot start beside it, and the re-check it runs when
    // it finishes goes to whichever panel is mounted by then (or only to the warning bar).
    _mountSetupPanel(container) {
        this._setupInstallWindow ??= new SetupInstallWindow(this.app, {
            onFinished: async () => {
                if (this._setupPanel) return this._setupPanel.refresh();
                const status = await refreshSetupStatus(this.app);
                if (status) this.app.setupAlert?.update(status);
                return status;
            }
        });
        return new SetupPanel(this.app, container, {
            installWindow: this._setupInstallWindow,
            onStatus: (status) => this.app.setupAlert?.update(status)
        });
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

        btnSet.addEventListener('click', async () => {
            const presenceProof = await this._requestPresenceProof();
            if (presenceProof === null) return;
            this._showPinModal(async (pin) => {
                try {
                    await this.app.apiCall('/api/v1/settings/pin', 'POST', { pin, presenceProof });
                    this.app.closeModal();
                    this._pinIsSet = true;
                    this._updatePinUI(badge, btnSet, btnClear, true);
                    this.app.showToast('Remote access', 'Password saved', 'success');
                } catch (error) {
                    throw error;
                }
            });
        });

        btnClear.addEventListener('click', async () => {
            try {
                const presenceProof = await this._requestPresenceProof();
                if (presenceProof === null) return;
                await this.app.apiCall('/api/v1/settings/pin', 'DELETE', { pin: '', presenceProof });
                this._pinIsSet = false;
                this._updatePinUI(badge, btnSet, btnClear, false);
                this.app.showToast('Remote access', 'Password cleared', 'success');
                // Revert remote access toggle if it was enabled
                const remoteToggle = root.querySelector('#setting-remote-access');
                if (remoteToggle?.checked) {
                    remoteToggle.checked = false;
                    this._updateDirtyState(root);
                    this.app.showToast('Remote Access', 'Remote access disabled because the password was cleared.', 'warning');
                }
            } catch (error) {
                this.app.showError('Failed to clear password: ' + error.message);
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
                Password or PIN set`;
            btnSet.textContent = '';
            btnSet.innerHTML = `
                <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                    <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                </svg>
                Change password`;
            btnClear.style.removeProperty('display');
        } else {
            badge.className = 'pin-status-badge not-set';
            badge.innerHTML = `
                <svg xmlns="http://www.w3.org/2000/svg" width="12" height="12" fill="currentColor" viewBox="0 0 16 16">
                    <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                </svg>
                Not set`;
            btnSet.innerHTML = `
                <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                    <path d="M8 1a2 2 0 0 1 2 2v4H6V3a2 2 0 0 1 2-2m3 6V3a3 3 0 0 0-6 0v4a2 2 0 0 0-2 2v5a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2"/>
                </svg>
                Create password`;
            btnClear.style.setProperty('display', 'none', 'important');
        }
    }

    async _requestPresenceProof() {
        const challenge = await this.app.apiCall('/api/v1/settings/security-challenge', 'GET');
        return new Promise(resolve => {
            let input;
            this.app.showModal('Confirm security change', `
                <form id="settings-presence-form">
                    <p>${challenge.pinRequired ? 'Enter your current remote access password or PIN.' : 'Type the confirmation code shown below to approve this change.'}</p>
                    ${challenge.pinRequired ? '' : `<p><strong>${this.app.escapeHtml(challenge.code)}</strong></p>`}
                    <label class="form-label" for="settings-presence-proof">${challenge.pinRequired ? 'Current password or PIN' : 'Confirmation code'} (required)</label>
                    <input id="settings-presence-proof" class="form-control" type="${challenge.pinRequired ? 'password' : 'text'}" maxlength="128" autocomplete="${challenge.pinRequired ? 'current-password' : 'off'}" autocapitalize="off" spellcheck="false" required>
                    <div class="mt-3 d-flex flex-wrap gap-2 justify-content-end">
                        <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                        <button type="submit" id="settings-presence-confirm" class="btn btn-primary">Continue</button>
                    </div>
                </form>`,
                { onClose: () => { if (input) input.value = ''; resolve(null); } });
            input = document.getElementById('settings-presence-proof');
            document.getElementById('settings-presence-form').addEventListener('submit', event => {
                event.preventDefault();
                const proof = input.value;
                input.value = '';
                resolve(proof);
                this.app.closeModal();
            });
            input.focus();
        });
    }

    _showPinModal(onConfirm) {
        let input;
        this.app.showModal(this._pinIsSet ? 'Change remote access password' : 'Create remote access password', `
            <form id="remote-password-form" novalidate>
                <label class="form-label" for="remote-access-password">New password (required)</label>
                <input id="remote-access-password" class="form-control" type="password" maxlength="128"
                    autocomplete="new-password" autocapitalize="off" spellcheck="false"
                    aria-describedby="remote-password-help remote-password-error" required>
                <p id="remote-password-help" class="form-text">Use at least 6 characters, including a letter and a number. Maximum 128 characters.</p>
                <p id="remote-password-error" class="text-danger" role="alert" hidden></p>
                <div class="mt-3 d-flex flex-wrap gap-2 justify-content-end">
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                    <button type="submit" class="btn btn-primary" id="btn-pin-confirm">Save password</button>
                </div>
            </form>`, { onClose: () => { if (input) input.value = ''; } });

        const container = document.getElementById('modal-container');
        input = container.querySelector('#remote-access-password');
        const form = container.querySelector('#remote-password-form');
        const confirmBtn = container.querySelector('#btn-pin-confirm');
        const errorEl = container.querySelector('#remote-password-error');
        container.querySelector('.modal-dialog')?.classList.add('pin-modal-dialog');
        let saving = false;

        input.addEventListener('input', () => {
            input.removeAttribute('aria-invalid');
            errorEl.hidden = true;
            errorEl.textContent = '';
        });
        form.addEventListener('submit', async event => {
            event.preventDefault();
            if (saving) return;
            const error = remotePasswordError(input.value);
            if (error) {
                input.setAttribute('aria-invalid', 'true');
                errorEl.textContent = error;
                errorEl.hidden = false;
                input.focus();
                return;
            }
            saving = true;
            input.disabled = true;
            confirmBtn.disabled = true;
            confirmBtn.textContent = 'Saving…';
            try {
                await onConfirm(input.value);
                input.value = '';
            } catch (error) {
                errorEl.textContent = error.message || 'Could not save the password. Try again.';
                errorEl.hidden = false;
            } finally {
                saving = false;
                input.disabled = false;
                confirmBtn.disabled = false;
                confirmBtn.textContent = 'Save password';
            }
        });
        input.focus();
    }
}
