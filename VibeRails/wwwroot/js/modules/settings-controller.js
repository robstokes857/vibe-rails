export class SettingsController {
    constructor(app) {
        this.app = app;
        this._pinIsSet = false;
        this.performanceMode = window.VibeRailsPerformance;
    }

    async loadSettings() {
        const content = document.getElementById('app-content');
        if (!content) return;

        let settings = { remoteAccess: false, apiKey: '', enablePrerelease: false, developerOptions: false, chatHistorySettings: { processingLlm: '' } };
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
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());

            const remoteAccessToggle = root.querySelector('#setting-remote-access');
            const apiKeyInput = root.querySelector('#setting-api-key');
            const performanceModeToggle = root.querySelector('#setting-performance-mode');
            const enablePrereleaseToggle = root.querySelector('#setting-enable-prerelease');
            const developerOptionsToggle = root.querySelector('#setting-developer-options');

            if (remoteAccessToggle) {
                remoteAccessToggle.checked = settings.remoteAccess || false;
                remoteAccessToggle.addEventListener('change', () => {
                    if (remoteAccessToggle.checked && !this._pinIsSet) {
                        remoteAccessToggle.checked = false;
                        this.app.showToast('Remote Access', 'A PIN must be set before enabling remote access.', 'warning');
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
            if (enablePrereleaseToggle) {
                enablePrereleaseToggle.checked = settings.enablePrerelease || false;
            }
            if (developerOptionsToggle) {
                developerOptionsToggle.checked = settings.developerOptions || false;
            }

            const form = root.querySelector('#app-settings-form');
            if (form) {
                form.addEventListener('submit', async (e) => {
                    e.preventDefault();
                    const wantsRemote = remoteAccessToggle?.checked || false;
                    if (wantsRemote && !this._pinIsSet) {
                        this.app.showToast('Remote Access', 'A PIN must be set before enabling remote access.', 'warning');
                        if (remoteAccessToggle) remoteAccessToggle.checked = false;
                        return;
                    }
                    const apiKeyValue = apiKeyInput?.value || '';
                    const apiKeyChanged = apiKeyValue !== (apiKeyInput?.dataset.originalValue || '');
                    await this.saveSettings(
                        wantsRemote,
                        apiKeyChanged ? apiKeyValue : '',
                        enablePrereleaseToggle?.checked || false,
                        developerOptionsToggle?.checked || false
                    );
                });
            }

            await this._initPinSection(root);
        }

        content.appendChild(fragment);
    }

    async saveSettings(remoteAccess, apiKey, enablePrerelease, developerOptions) {
        try {
            const savedSettings = await this.app.apiCall('/api/v1/settings', 'POST', {
                remoteAccess: remoteAccess,
                apiKey: apiKey,
                enablePrerelease: enablePrerelease,
                developerOptions: developerOptions,
                chatHistorySettings: {
                    processingLlm: this.app.appSettings?.chatHistorySettings?.processingLlm || ''
                }
            });
            this.app.setAppSettings(savedSettings);
            this.applyPrereleaseVisibility(savedSettings.enablePrerelease || false);
            this.app.showToast('Settings', 'Settings saved successfully', 'success');
        } catch (error) {
            this.app.showError('Failed to save settings: ' + error.message);
        }
    }

    applyPrereleaseVisibility(enabled) {
        const swarmBtn = document.getElementById('swarm-nav-btn');
        if (swarmBtn) {
            swarmBtn.style.display = enabled ? '' : 'none';
        }
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
