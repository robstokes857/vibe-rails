const esc = v => String(v ?? '').replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');

// Catalog kind → badge class. Lossy is the loud one on purpose: it is the only kind that
// destroys information. An unrecognised kind from a newer server falls back to neutral rather
// than disappearing.
const STAGE_KIND_CLASS = {
    lossless: 'ts-kind-lossless',
    reshaping: 'ts-kind-reshaping',
    lossy: 'ts-kind-lossy',
};

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
        // Only true once the stage toggles are actually on screen. Until then the save path
        // sends null for the selection rather than the empty array the DOM would report — see
        // _collectTokenSaverStages.
        this._tokenSaverReady = false;
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
            // null = never configured. Distinct from [] ("everything off"); the catalog's
            // defaultSelection stands in for null, exactly as CompressionCatalog.Resolve does.
            tokenSaverStages: null,
            tokenSaverCaptureEnabled: false,
            machineName: ''
        };
        try {
            settings = await this.app.apiCall('/api/v1/settings', 'GET');
            this.app.setAppSettings(settings);
        } catch (error) {
            console.error('Failed to fetch settings:', error);
        }

        // The stage/scope toggles are rendered from this, never from a hardcoded list — the
        // catalog is the single source of truth for what the saver can do.
        let catalog = null;
        try {
            catalog = await this.app.apiCall('/api/v1/compression/catalog', 'GET');
        } catch (error) {
            console.error('Failed to fetch compression catalog:', error);
        }

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('settings-template');
        const root = fragment.querySelector('[data-view="settings"]');

        if (root) {
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
            const performanceModeToggle = root.querySelector('#setting-performance-mode');
            const useVsCodeThemeRow = root.querySelector('#setting-use-vscode-theme-row');
            const useVsCodeThemeToggle = root.querySelector('#setting-use-vscode-theme');
            const mcpEnabledToggle = root.querySelector('#setting-enable-mcp');
            const computerNameInput = root.querySelector('#setting-computer-name');
            const codexLlmProxyEnabledToggle = root.querySelector('#setting-codex-llm-proxy-enabled');
            const codexLlmProxyModeSubscription = root.querySelector('#setting-codex-llm-proxy-mode-subscription');
            const codexLlmProxyModeApi = root.querySelector('#setting-codex-llm-proxy-mode-api');
            const claudeLlmProxyEnabledToggle = root.querySelector('#setting-claude-llm-proxy-enabled');
            const tokenSaverCaptureToggle = root.querySelector('#setting-token-saver-capture');

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
            if (tokenSaverCaptureToggle) {
                tokenSaverCaptureToggle.checked = settings.tokenSaverCaptureEnabled === true;
            }
            // Must happen before _initSettingsDirtyTracking below: it renders the toggles that
            // the tracked-input selector goes looking for, and listeners can only be attached to
            // inputs that already exist.
            this._renderTokenSaver(root, catalog, settings);

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
                    const apiKeyChanged = apiKeyValue !== (apiKeyInput?.dataset.originalValue || '');
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
                            this._collectTokenSaverStages(root),
                            tokenSaverCaptureToggle?.checked ?? false
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

    async saveSettings(remoteAccess, apiKey, useVsCodeTheme, mcpEnabled, computerName, codexLlmProxyEnabled, codexLlmProxyMode, claudeLlmProxyEnabled, tokenSaverStages, tokenSaverCaptureEnabled) {
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
                // An empty array is a real "everything off" choice and must reach the server as
                // [], not as null/absent — the route treats null as "stale client, leave the
                // stored selection alone". null only ever comes from the catalog having failed
                // to load, which is exactly when we want that guard.
                tokenSaverStages: tokenSaverStages,
                tokenSaverCaptureEnabled: tokenSaverCaptureEnabled
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
        // The toggles go away with the view; leaving this true would let a save from a
        // re-rendered form collect an empty selection off a DOM that has none.
        this._tokenSaverReady = false;
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
            '#setting-token-saver-capture',
            // Rendered from the catalog, so they must already be in the DOM when dirty tracking
            // initialises — see the render call in loadSettings.
            '[data-token-saver-id]'
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
            // DOM order is catalog order, so this is stable across snapshots.
            tokenSaverStages: this._collectTokenSaverStages(root),
            tokenSaverCaptureEnabled: isChecked('#setting-token-saver-capture')
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

        if (remoteAccessToggle) remoteAccessToggle.checked = settings.remoteAccess === true;
        if (apiKeyInput) {
            apiKeyInput.value = settings.apiKey || '';
            apiKeyInput.dataset.originalValue = settings.apiKey || '';
        }
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

        const tokenSaverCaptureToggle = root.querySelector('#setting-token-saver-capture');
        if (tokenSaverCaptureToggle) tokenSaverCaptureToggle.checked = settings.tokenSaverCaptureEnabled === true;
        // Re-check against what the server actually stored, so an id it dropped (or kept) shows
        // up here rather than leaving the UI asserting a selection that was never saved.
        if (this._tokenSaverReady) {
            this._applyTokenSaverSelection(root, settings.tokenSaverStages ?? this._catalog?.defaultSelection);
        }
    }

    // ── Token Saver ────────────────────────────────────────────────────────

    // Renders one switch per catalog stage and scope. Nothing here knows a stage id: the list,
    // the order, the copy and the defaults all come from GET /api/v1/compression/catalog, so a
    // stage added server-side shows up without a change to this file.
    _renderTokenSaver(root, catalog, settings) {
        const stagesHost = root.querySelector('[data-token-saver-stages]');
        const scopesHost = root.querySelector('[data-token-saver-scopes]');
        if (!stagesHost || !scopesHost) return;

        this._catalog = catalog;
        this._tokenSaverReady = false;

        if (!catalog?.stages?.length || !catalog?.scopes?.length) {
            // Without the catalog we don't know which ids exist. Rendering nothing would make
            // the DOM report an empty selection, and saving that would wipe the stored one — so
            // stay out of the way entirely and let the save path send null instead.
            const unavailable = '<div class="ts-loading">Unavailable — the compression catalog failed to load. Your saved selection is left untouched.</div>';
            stagesHost.innerHTML = unavailable;
            scopesHost.innerHTML = unavailable;
            return;
        }

        // Order is the pipeline's, not ours: the stages don't commute, so showing them out of
        // execution order would misrepresent what actually runs.
        const stages = [...catalog.stages].sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
        stagesHost.innerHTML = stages.map(stage => {
            const kindClass = STAGE_KIND_CLASS[String(stage.kind || '').toLowerCase()] || '';
            return `
                <div class="ts-toggle">
                    <div class="ts-toggle-head">
                        ${this._tokenSaverSwitch(stage)}
                        <span class="ts-kind ${kindClass}">${esc(stage.kind)}</span>
                    </div>
                    <small class="form-text text-muted d-block">${esc(stage.summary)}</small>
                </div>`;
        }).join('');

        scopesHost.innerHTML = catalog.scopes.map(scope => {
            // A scope with a warning fails as a broken edit, not as a lost saving. It gets a
            // riskier treatment than an ordinary toggle on purpose.
            const risky = !!scope.warning;
            return `
                <div class="ts-toggle ${risky ? 'is-risky' : ''}">
                    <div class="ts-toggle-head">
                        ${this._tokenSaverSwitch(scope)}
                        ${risky ? '<span class="ts-kind ts-kind-lossy">Risky</span>' : ''}
                    </div>
                    <small class="form-text text-muted d-block">${esc(scope.summary)}</small>
                    ${risky ? `<div class="ts-warning"><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i><span>${esc(scope.warning)}</span></div>` : ''}
                </div>`;
        }).join('');

        this._tokenSaverReady = true;
        // null (never configured) resolves to the catalog defaults, matching what the pipeline
        // would actually run. An empty array stays empty — it is a real "everything off" choice.
        this._applyTokenSaverSelection(root, settings.tokenSaverStages ?? catalog.defaultSelection);

        this.app.bindAction(root, '[data-action="token-saver-defaults"]', () => {
            this._applyTokenSaverSelection(root, catalog.defaultSelection);
            // Setting .checked in script fires no change event, so the tracked-input listeners
            // never run and Save would stay disabled. Recompute by hand.
            this._updateDirtyState(root);
        });
    }

    _tokenSaverSwitch(item) {
        const inputId = `setting-ts-${item.id}`;
        return `
            <div class="form-check form-switch">
                <input class="form-check-input" type="checkbox" id="${esc(inputId)}" data-token-saver-id="${esc(item.id)}">
                <label class="form-check-label" for="${esc(inputId)}">${esc(item.name)}</label>
            </div>`;
    }

    // The persisted value: checked stage ids and scope ids in one flat array. Returns null —
    // never [] — when the toggles aren't on screen, because [] means "everything off" to the
    // route and would silently overwrite a real selection with nothing.
    _collectTokenSaverStages(root) {
        if (!this._tokenSaverReady) return null;
        return Array.from(root.querySelectorAll('[data-token-saver-id]'))
            .filter(input => input.checked)
            .map(input => input.dataset.tokenSaverId);
    }

    _applyTokenSaverSelection(root, ids) {
        const enabled = new Set(ids || []);
        root.querySelectorAll('[data-token-saver-id]').forEach(input => {
            input.checked = enabled.has(input.dataset.tokenSaverId);
        });
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
