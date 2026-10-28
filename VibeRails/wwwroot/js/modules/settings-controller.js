import { confirmDialog } from './utils.js';
import { SettingsKeysPanel } from './settings-keys.js';
import { SetupPanel, refreshSetupStatus } from './setup-panel.js';
import { SetupInstallWindow } from './setup-install-window.js';
import { getToastTheme, setToastTheme } from './toast-service.js';
import { PrReviewerSettingsPanel } from './settings-pr-reviewer.js';
import { BoardApi } from './board-api.js';
import { contextEditorSection, mountContextEditor } from './board-settings.js';

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
        this._disposeContextEditor = null;
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
            createVibeStoryTracking: true,
            createVibeStoryTrackingCustomEnvs: false,
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
            this._initSettingsTabs(root);
            if (data.tab) this._selectSettingsTab?.(data.tab);
            const toastTheme = root.querySelector('#setting-toast-theme');
            if (toastTheme) {
                toastTheme.value = getToastTheme();
                toastTheme.addEventListener('change', () => {
                    const theme = setToastTheme(toastTheme.value);
                    this.app.showToast('Notifications', 'Your notification style is ready.', 'success', { theme });
                });
            }

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
            const createVibeStoryTrackingToggle = root.querySelector('#setting-create-vibe-story-tracking');
            const createVibeStoryTrackingCustomEnvsToggle = root.querySelector('#setting-create-vibe-story-tracking-custom-envs');
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
                        this.app.showToast('Remote Access', 'A PIN must be set before enabling remote access.', 'warning');
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
            if (createVibeStoryTrackingToggle) {
                createVibeStoryTrackingToggle.checked = settings.createVibeStoryTracking !== false;
            }
            if (createVibeStoryTrackingCustomEnvsToggle) {
                createVibeStoryTrackingCustomEnvsToggle.checked = settings.createVibeStoryTrackingCustomEnvs === true;
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
                            createVibeStoryTrackingToggle?.checked ?? true,
                            createVibeStoryTrackingCustomEnvsToggle?.checked ?? false
                        );
                        if (savedSettings) {
                            await this._reconcileSavedApiKey(savedSettings, linkedKeyVersion);
                            if (this._settingsRoot !== root) return;
                            this.app.setAppSettings({ apiKey: savedSettings.apiKey, remoteAccountEmail: savedSettings.remoteAccountEmail });
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

    async saveSettings(remoteAccess, apiKey, useVsCodeTheme, mcpEnabled, computerName, codexEnterpriseNetworkProxy, codexLlmProxyMode, claudeEnterpriseNetworkProxy, openCodeEnterpriseNetworkProxy, grokEnterpriseNetworkProxy, grokLlmProxyMode, claudeTokenSaverEnabled, codexTokenSaverEnabled, openCodeTokenSaverEnabled, grokTokenSaverEnabled, clearApiKey = false, createVibeStoryTracking = true, createVibeStoryTrackingCustomEnvs = false) {
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
                remoteAccess: remoteAccess,
                apiKey: apiKey,
                useVsCodeTheme: useVsCodeTheme,
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
                createVibeStoryTracking: createVibeStoryTracking,
                createVibeStoryTrackingCustomEnvs: createVibeStoryTrackingCustomEnvs,
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
        this._disposeContextEditor?.();
        this._disposeContextEditor = null;
        this._prReviewerPanel?.unload();
        this._prReviewerPanel = null;
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
            '#setting-create-vibe-story-tracking',
            '#setting-create-vibe-story-tracking-custom-envs',
            '#setting-computer-name',
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
            remoteAccess: isChecked('#setting-remote-access'),
            apiKey: valueOf('#setting-api-key'),
            useVsCodeTheme: isChecked('#setting-use-vscode-theme'),
            createVibeStoryTracking: isChecked('#setting-create-vibe-story-tracking'),
            createVibeStoryTrackingCustomEnvs: isChecked('#setting-create-vibe-story-tracking-custom-envs'),
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
        const createVibeStoryTrackingToggle = root.querySelector('#setting-create-vibe-story-tracking');
        const createVibeStoryTrackingCustomEnvsToggle = root.querySelector('#setting-create-vibe-story-tracking-custom-envs');
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
        if (createVibeStoryTrackingToggle) createVibeStoryTrackingToggle.checked = settings.createVibeStoryTracking !== false;
        if (createVibeStoryTrackingCustomEnvsToggle) createVibeStoryTrackingCustomEnvsToggle.checked = settings.createVibeStoryTrackingCustomEnvs === true;
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
            // App context (VIBE-229) uses the same editor as a board's Context tab, with only its own level.
            if (id === 'context' && !this._disposeContextEditor) {
                const container = root.querySelector('[data-settings-context-editor]');
                if (container) {
                    BoardApi.attach(this.app);
                    container.innerHTML = contextEditorSection({ board: false });
                    this._disposeContextEditor = mountContextEditor(this.app, container.querySelector('[data-context-editor]'), { toastTitle: 'Settings' });
                }
            }
            if (id === 'pr-reviewer') {
                const container = root.querySelector('[data-settings-pr-reviewer]');
                if (container) {
                    this._prReviewerPanel ??= new PrReviewerSettingsPanel(this.app, container);
                    void this._prReviewerPanel.load();
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
                    this.app.showToast('PIN Lock', 'PIN set successfully', 'success');
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

    async _requestPresenceProof() {
        const challenge = await this.app.apiCall('/api/v1/settings/security-challenge', 'GET');
        return new Promise(resolve => {
            this.app.showModal('Confirm security change', `
                <p>${challenge.pinRequired ? 'Enter your current remote access PIN.' : 'Type the confirmation code shown below to approve this change.'}</p>
                ${challenge.pinRequired ? '' : `<p><strong>${this.app.escapeHtml(challenge.code)}</strong></p>`}
                <input id="settings-presence-proof" class="form-control" type="${challenge.pinRequired ? 'password' : 'text'}" maxlength="128" autocomplete="off" aria-label="Confirmation">
                <div class="mt-3"><button class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button id="settings-presence-confirm" class="btn btn-primary">Continue</button></div>`,
                { onClose: () => resolve(null) });
            const input = document.getElementById('settings-presence-proof');
            document.getElementById('settings-presence-confirm').addEventListener('click', () => {
                const proof = input.value;
                input.value = '';
                resolve(proof);
                this.app.closeModal();
            });
            input.focus();
        });
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
