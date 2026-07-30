// ============================================ 
// VibeControl Application
// Single Page Application for Agent Management
// ============================================ 

import { AgentController } from './js/modules/agent-controller.js';
import { DashboardController } from './js/modules/dashboard-controller.js';
import { SessionController } from './js/modules/session-controller.js';
import { EnvironmentController } from './js/modules/environment-controller.js';
import { ConfigController } from './js/modules/config-controller.js';
import { RuleController } from './js/modules/rule-controller.js';
import { CliLauncher } from './js/modules/cli-launcher.js';
import { TerminalController } from './js/modules/terminal-multitab.js';
import { SandboxController } from './js/modules/sandbox-controller.js';
import { SettingsController } from './js/modules/settings-controller.js';
import { VibeRailsAiController } from './js/modules/vibe-rails-ai-controller.js';
import { McpController } from './js/modules/mcp-controller.js';
import { JobController } from './js/modules/jobs-controller.js';
import { AppEventClient } from './js/modules/app-event-client.js';
import { TerminalTokenCompressionMeter, getTokenSaverEnabledSources } from './js/modules/terminal-token-compression.js';
import { showAppToast } from './js/modules/toast-service.js';
import { getLlmName, getProjectNameFromPath, formatRelativeTime, getCliBrand, escapeHtml } from './js/modules/utils.js';

export class VibeControlApp {
    constructor() {
        this.currentView = 'terminal-focus';
        this.navigationStack = ['terminal-focus'];
        this.appSettings = this._normalizeAppSettings();
        this.data = {
            agents: [],
            environments: [],
            sandboxes: [],
            availableRulesWithDescriptions: [],
            isInGit: false,
            configs: null,
            projectDisplayName: null
        };
        this.hostUnreachableToastShown = false;
        this.brandClickTimestamps = [];
        
        // Initialize Controllers
        this.agentController = new AgentController(this);
        this.dashboardController = new DashboardController(this);
        this.sessionController = new SessionController(this);
        this.environmentController = new EnvironmentController(this);
        this.configController = new ConfigController(this);
        this.ruleController = new RuleController(this);
        this.cliLauncher = new CliLauncher(this);
        this.terminalController = new TerminalController(this);
        this.sandboxController = new SandboxController(this);
        this.settingsController = new SettingsController(this);
        this.vibeRailsAiController = new VibeRailsAiController(this);
        this.mcpController = new McpController(this);
        this.jobController = new JobController(this);
        this.appEventClient = new AppEventClient(this);
        this.lifecycleHeartbeatTimer = null;
        this.lifecycleClientId = this.getOrCreateLifecycleClientId();
        this.navbarsCollapsed = false;
        this.loadingOverlayPendingCount = 0;
        this.loadingOverlayTimer = null;
        this.loadingOverlayVisibleSince = 0;
        this.navigationGuards = new Set();

        this.init();
    }

    async init() {
        this.startLifecycleHeartbeat();
        await this.fetchConfigs();
        this.applyDocumentTitle();
        await this.applyInitialSettings();
        this.terminalController.bindSessionEvents(this.appEventClient);

        // Persistent proxy/token-compression indicator. It intentionally listens to the dedicated proxy
        // event only, so terminal output, MCP calls, and other app activity cannot trigger it.
        // The light lives in the terminal controls bar (see renderTerminalPanel); it's created
        // detached and TerminalManager.initialize() re-parents it each time that bar renders.
        this.terminalTokenCompression = new TerminalTokenCompressionMeter({
            title: 'Token saver',
            enabledSources: getTokenSaverEnabledSources(this.appSettings)
        }).connect(this);
        // If the terminal view mounted before this ran, drop the meter into its slot now.
        this.terminalTokenCompression.relocate(document.getElementById('terminal-proxy-activity-slot'));
        // Register every handler before opening the socket so the first fast proxy response cannot
        // race the activity subscription during startup.
        this.appEventClient.start();

        this.initSidebarToggle();
        this.applyNavbarsCollapsedState(false);
        // Git is optional. When not in a git repo, show a small non-blocking helper bar
        // but let the app load and run normally against the launch directory.
        if (!this.data.isInGit) {
            this.showNotInGitHelper();
        }
        const initialView = this.consumeRequestedInitialView();
        this.navigationStack = [initialView];
        this.currentView = initialView;
        this.loadView(initialView);
        this.bindGlobalActions();
        this.setupKeyboardShortcuts();
        this.setupVSCodeIntegration();
        this.startLifecycleHeartbeat();
    }

    async applyInitialSettings() {
        try {
            const settings = await this.apiCall('/api/v1/settings', 'GET');
            this.setAppSettings(settings);
        } catch (error) {
            console.error('Failed to fetch initial settings:', error);
        }
    }

    showNotInGitHelper() {
        const launchDir = this.data.configs?.launchDirectory || 'Unknown directory';
        const isDangerousDir = this._isDangerousDirectory(launchDir);

        const initButtonHtml = isDangerousDir ? '' : `
            <button class="btn btn-sm btn-warning" id="git-init-btn">Initialize Git Here</button>`;

        const content = document.getElementById('app-content');
        if (!content) return;

        // Idempotent: drop any existing helper bar before re-rendering.
        document.getElementById('git-helper-bar')?.remove();

        const bar = document.createElement('div');
        bar.id = 'git-helper-bar';
        // Compact, dismissible bar that sits ABOVE #app-content (which loadView owns),
        // so it never blocks the app the way the old full-screen card did.
        bar.innerHTML = `
            <div class="alert alert-warning d-flex flex-wrap align-items-center gap-2 mb-3" role="alert">
                <span class="flex-grow-1 small mb-0">
                    <strong>No git repository here.</strong>
                    Running against <code style="word-break: break-all;">${escapeHtml(launchDir)}</code> — git-only features (sandboxes, diffs, rules) are unavailable.
                </span>
                <button class="btn btn-sm btn-outline-dark" id="git-open-dir-btn">Open a Different Directory</button>
                <input type="file" id="git-dir-picker" webkitdirectory style="display:none">
                ${initButtonHtml}
                <button type="button" class="btn-close" id="git-helper-dismiss" aria-label="Dismiss"></button>
                <div id="git-confirm-row" class="d-none w-100 mt-2">
                    <div class="input-group input-group-sm">
                        <input type="text" id="git-confirm-path" class="form-control" placeholder="Resolved path">
                        <button class="btn btn-success" id="git-confirm-go">Open</button>
                    </div>
                    <div class="form-text mt-1">Edit the path if needed, then click Open.</div>
                </div>
                <div id="git-banner-error" class="alert alert-danger d-none w-100 mt-2 mb-0 py-1 small" role="alert"></div>
            </div>`;
        content.parentNode.insertBefore(bar, content);

        document.getElementById('git-helper-dismiss')?.addEventListener('click', () => {
            document.getElementById('git-helper-bar')?.remove();
        });

        const errorEl = document.getElementById('git-banner-error');
        const showError = (msg) => {
            errorEl.textContent = msg;
            errorEl.classList.remove('d-none');
        };

        document.getElementById('git-open-dir-btn')?.addEventListener('click', () => {
            document.getElementById('git-dir-picker').click();
        });

        document.getElementById('git-dir-picker')?.addEventListener('change', (e) => {
            const files = e.target.files;
            if (!files || files.length === 0) return;
            const topFolder = files[0].webkitRelativePath.split('/')[0];
            const launchDir = this.data.configs?.launchDirectory || '';
            const fullPath = launchDir.replace(/[\\/]+$/, '') + '/../' + topFolder;
            document.getElementById('git-confirm-path').value = fullPath;
            document.getElementById('git-confirm-row').classList.remove('d-none');
            errorEl.classList.add('d-none');
        });

        document.getElementById('git-confirm-go')?.addEventListener('click', () => {
            const path = document.getElementById('git-confirm-path').value.trim();
            if (!path) return;
            this._openDirectory(path, showError);
        });

        document.getElementById('git-init-btn')?.addEventListener('click', async () => {
            const btn = document.getElementById('git-init-btn');
            if (!btn) return;
            btn.disabled = true;
            btn.textContent = 'Initializing...';
            errorEl.classList.add('d-none');
            try {
                await this.apiCall('/api/v1/git/init', 'POST');
                await this.fetchConfigs();
                if (this.data.isInGit) {
                    // Repo created — re-render the current view in git mode. Route the in-place
                    // reload through the navigation guard so a dirty settings form isn't silently
                    // discarded; if the user opts to keep their edits, leave the view as-is.
                    if (!this.canNavigateTo(this.currentView)) {
                        btn.disabled = false;
                        btn.textContent = 'Initialize Git Here';
                        return;
                    }
                    document.getElementById('git-helper-bar')?.remove();
                    this.loadView(this.currentView);
                } else {
                    showError('Git was initialized but the repository could not be detected. Please refresh.');
                    btn.disabled = false;
                    btn.textContent = 'Initialize Git Here';
                }
            } catch (err) {
                const msg = err?.message || 'Failed to initialize git.';
                showError(msg);
                btn.disabled = false;
                btn.textContent = 'Initialize Git Here';
            }
        });
    }

    async _openDirectory(directory, showError) {
        try {
            const result = await this.apiCall('/api/v1/git/open-directory', 'POST', { directory });
            if (result?.url) {
                window.open(result.url, '_blank');
            }
        } catch (err) {
            const msg = err?.message || 'Failed to open directory.';
            if (showError) showError(msg);
        }
    }

    _isDangerousDirectory(path) {
        if (!path) return true;
        const normalized = path.replace(/\\/g, '/').replace(/\/+$/, '');
        // Unix roots
        if (['/', '/home', '/root', '/usr', '/etc', '/bin', '/sbin', '/var', '/tmp'].includes(normalized)) return true;
        // Windows drive roots like C:, C:/ or C:\
        if (/^[A-Za-z]:[\\/]?$/.test(normalized)) return true;
        // Windows system dirs
        const lower = normalized.toLowerCase();
        if (lower.includes('/program files') || lower.includes('/windows') || lower === 'c:/users') return true;
        return false;
    }

    setupVSCodeIntegration() {
        // Both the top nav and the sidebar render an exit button; wire them all.
        const exitBtns = document.querySelectorAll('.nav-exit-btn-sm');
        exitBtns.forEach((exitBtn) => {
            exitBtn.addEventListener('click', () => {
                if (window.__viberails_VSCODE__ && window.__viberails_close__) {
                    window.__viberails_close__();
                } else {
                    this.apiCall('/api/v1/shutdown', 'POST').catch(() => {});
                    window.close();
                }
            });
        });

        // Apply VS Code-specific UI adjustments when in webview
        if (window.__viberails_VSCODE__) {
            // Hide "Edit in VS Code" button since we're already in VS Code
            const editVsCodeCard = document.querySelector('[data-agent-action="edit-vscode"]');
            if (editVsCodeCard) {
                editVsCodeCard.closest('.col-md-4')?.remove();
            }

            // Hide "Launch in VS Code" card from terminals section
            const launchVsCodeBtn = document.querySelector('[data-action="launch-vscode"]');
            if (launchVsCodeBtn) {
                launchVsCodeBtn.closest('.project-item, .list-group-item')?.remove();
            }
        }
    }

    async fetchConfigs() {
        try {
            const configs = await this.apiCall('/api/v1/context', 'GET');
            this.data.configs = configs;
            this.data.isInGit = configs.isInGit;
            const projectPath = configs.rootPath || configs.launchDirectory;
            this.data.projectDisplayName = projectPath ? this.getProjectNameFromPath(projectPath) : null;
        } catch (error) {
            console.error('Failed to fetch configs:', error);
            this.data.isInGit = false;
        }
    }

    async applyDocumentTitle() {
        const path = this.data.configs?.rootPath || this.data.configs?.launchDirectory;
        let customName = null;
        if (path) {
            // Race the fetch against a 2s timer so a hung backend doesn't block app
            // init forever — we fall back to the folder name and update later if/when
            // the customName fetch eventually returns (we ignore the late result).
            let timeoutHandle = null;
            try {
                const fetchPromise = this.apiCall(`/api/v1/projects/name?path=${encodeURIComponent(path)}`);
                const result = await Promise.race([
                    fetchPromise,
                    new Promise(resolve => { timeoutHandle = setTimeout(() => resolve(null), 2000); }),
                ]);
                customName = result?.customName || null;
            } catch { /* ignore */ }
            finally { if (timeoutHandle) clearTimeout(timeoutHandle); }
        }
        const folderName = path ? this.getProjectNameFromPath(path) : null;
        // Browser tab + VS Code preview title only: custom project name beats the folder
        // name. The "— Vibe Rails" suffix was intentionally dropped (bare name now).
        // Terminal tab labels are separate and unaffected (they use the CLI name).
        const title = customName || folderName || 'Vibe Rails';
        this.data.projectDisplayName = title;
        document.title = title;
        // Browser tab title only; in VS Code, the panel tab is owned by the
        // extension, so notify it via postMessage.
        if (typeof window.__viberails_setTitle__ === 'function') {
            try { window.__viberails_setTitle__(title); } catch { /* ignore */ }
        }
    }

    // ============================================
    // Core Infrastructure
    // ============================================

    cloneTemplate(id) {
        const template = document.getElementById(id);
        if (!template) {
            console.warn(`Template not found: ${id}`);
            return document.createDocumentFragment();
        }
        return template.content.cloneNode(true);
    }

    bindAction(container, selector, handler) {
        const element = container.querySelector(selector);
        if (element) {
            element.addEventListener('click', handler);
        }
    }

    registerNavigationGuard(guard) {
        if (typeof guard !== 'function') {
            return () => {};
        }

        this.navigationGuards.add(guard);
        return () => this.navigationGuards.delete(guard);
    }

    canNavigateTo(view, data = {}) {
        for (const guard of Array.from(this.navigationGuards)) {
            try {
                if (guard({ from: this.currentView, to: view, data }) === false) {
                    return false;
                }
            } catch (error) {
                console.error('Navigation guard failed:', error);
            }
        }

        return true;
    }

    bindGlobalActions() {
        document.addEventListener('click', (e) => {
            const brandLogo = e.target.closest('.navbar-brand-sm');
            if (brandLogo) {
                this.handleBrandLogoClick();
                return;
            }

            const goBack = e.target.closest('[data-action="go-back"]');
            if (goBack) {
                this.goBack();
            }

            const goHome = e.target.closest('[data-action="navigate-home"]');
            if (goHome) {
                e.preventDefault();
                this.navigate('dashboard', {}, { resetStack: true });
            }

            const goSettings = e.target.closest('[data-action="navigate-settings"]');
            if (goSettings) {
                e.preventDefault();
                this.navigate('settings', {}, { resetStack: true });
            }

            const navLayoutSwitch = e.target.closest('[data-action="switch-nav-layout"]');
            if (navLayoutSwitch) {
                e.preventDefault();
                const navLayout = window.VibeRailsNavLayout;
                const nextLayout = navLayoutSwitch.dataset.navLayoutTarget === 'side'
                    ? navLayout?.SIDE
                    : navLayout?.TOP;
                navLayout?.setLayout?.(nextLayout);

                window.dispatchEvent(new Event('resize'));
                this.terminalController?.refreshLayout?.();
                setTimeout(() => {
                    window.dispatchEvent(new Event('resize'));
                    this.terminalController?.refreshLayout?.();
                }, 260);
            }

            const goNav = e.target.closest('.app-sidebar [data-action="navigate"], .app-subnav [data-action="navigate"]');
            if (goNav) {
                e.preventDefault();
                const view = goNav.dataset.view;
                if (view) this.navigate(view, {}, { resetStack: true });
            }
        });
    }

    initSidebarToggle() {
        const toggleBtn = document.getElementById('sidebar-toggle-btn');

        // The sidebar collapse signal is the <html>.sidebar-collapsed class; the
        // inline <head> script already applies it before first paint. Re-sync here
        // in case storage changed elsewhere, then keep it in sync on toggle.
        //
        // The read is guarded like the write below and like nav-layout.js: a webview or
        // browser with storage denied throws SecurityError on getItem, and this runs during
        // init, so an unguarded throw would abort app startup. Fall back to whatever the
        // pre-paint <head> script already put on <html>.
        const readCollapsed = () => {
            try {
                return localStorage.getItem('viberails_sidebar_collapsed') === 'true';
            } catch (_) {
                return document.documentElement.classList.contains('sidebar-collapsed');
            }
        };

        const syncFromStorage = () => {
            const collapsed = readCollapsed();
            document.documentElement.classList.toggle('sidebar-collapsed', collapsed);
            return collapsed;
        };

        const toggleSidebar = () => {
            if (!document.getElementById('app-sidebar')) return;
            const collapsed = syncFromStorage();
            const next = !collapsed;
            document.documentElement.classList.toggle('sidebar-collapsed', next);
            try {
                localStorage.setItem('viberails_sidebar_collapsed', String(next));
            } catch (_) { /* ignore storage errors */ }

            // Notify terminal and responsive components of the size change, both
            // immediately and after the CSS transition settles.
            window.dispatchEvent(new Event('resize'));
            this.terminalController?.refreshLayout?.();

            setTimeout(() => {
                window.dispatchEvent(new Event('resize'));
                this.terminalController?.refreshLayout?.();
            }, 260);
        };

        syncFromStorage();
        toggleBtn?.addEventListener('click', toggleSidebar);
        this.toggleSidebar = toggleSidebar;
    }

    applyNavbarsCollapsedState() {
        // Kept for compatibility; the sidebar collapse signal is now <html>.sidebar-collapsed.
        window.dispatchEvent(new Event('resize'));
        this.terminalController?.refreshLayout?.();
    }

    bindActions(container, selector, handler) {
        container.querySelectorAll(selector).forEach((element) => {
            element.addEventListener('click', () => handler(element));
        });
    }

    setAppSettings(settings = {}) {
        this.appSettings = this._normalizeAppSettings({
            ...this.appSettings,
            ...settings
        });
        this.applyVsCodeThemePreference();
        this.terminalTokenCompression?.setEnabledSources(getTokenSaverEnabledSources(this.appSettings));
    }

    _normalizeAppSettings(settings = {}) {
        return {
            remoteAccess: false,
            apiKey: '',
            useVsCodeTheme: false,
            mcpEnabled: true,
            computerName: '',
            codexLlmProxyEnabled: false,
            codexLlmProxyMode: 'subscription',
            claudeLlmProxyEnabled: false,
            openCodeLlmProxyEnabled: false,
            claudeTokenSaverEnabled: true,
            codexTokenSaverEnabled: true,
            openCodeTokenSaverEnabled: true,
            machineName: '',
            ...settings
        };
    }

    applyVsCodeThemePreference() {
        if (!window.__viberails_VSCODE__) {
            document.documentElement.removeAttribute('data-viberails-vscode-theme');
            return;
        }

        if (this.appSettings?.useVsCodeTheme === true) {
            document.documentElement.dataset.viberailsVscodeTheme = 'enabled';
        } else {
            document.documentElement.removeAttribute('data-viberails-vscode-theme');
        }
    }

    handleBrandLogoClick() {
        const now = Date.now();
        this.brandClickTimestamps.push(now);
        this.brandClickTimestamps = this.brandClickTimestamps.filter(timestamp => now - timestamp <= 900);

        if (this.brandClickTimestamps.length < 3) {
            return;
        }

        this.brandClickTimestamps = [];
        this.showAppVersionModal();
    }

    async showAppVersionModal() {
        this.showModal('App Version', `
            <div class="d-flex flex-column gap-3">
                <div class="d-flex align-items-baseline gap-2">
                    <span class="text-muted">Version</span>
                    <span id="app-version-value" class="font-monospace fs-5">Loading…</span>
                </div>
                <div class="d-flex justify-content-end">
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Close</button>
                </div>
            </div>
        `);

        const versionOutput = document.getElementById('app-version-value');
        try {
            const response = await this.apiCall('/api/v1/update/version', 'GET', null, { showLoading: false });
            if (versionOutput) {
                versionOutput.textContent = response?.version || 'Unknown';
            }
        } catch (error) {
            console.error('Failed to fetch app version:', error);
            if (versionOutput) {
                versionOutput.textContent = 'Unavailable';
            }
        }
    }

    getDuplicateTabView() {
        return this.getDuplicateTabViewName(this.currentView);
    }

    buildDuplicateTabRedirect() {
        const url = new URL(window.location.href);
        const view = this.getDuplicateTabView();

        if (view === 'terminal-focus') {
            url.searchParams.delete('view');
        } else {
            url.searchParams.set('view', view);
        }

        return `${url.pathname}${url.search}${url.hash}`;
    }

    consumeRequestedInitialView() {
        const url = new URL(window.location.href);
        if (url.pathname.replace(/\/+$/, '') === '/git-guard') {
            return 'git-guard';
        }
        const requestedView = (url.searchParams.get('view') || '').trim();
        const initialView = this.getDuplicateTabViewName(requestedView);

        if (url.searchParams.has('view')) {
            url.searchParams.delete('view');
            try {
                window.history.replaceState({}, document.title, url.toString());
            } catch {
                // Ignore history rewrite failures.
            }
        }

        return initialView;
    }

    getDuplicateTabViewName(view) {
        const normalizedView = ['agents', 'agent-edit', 'agent-create'].includes(view)
            ? 'dashboard'
            : view;
        const duplicateableViews = new Set([
            'dashboard',
            'launch-cli',
            'agents',
            'check-violations',
            'git-guard',
            'environments',
            'config',
            'sessions',
            'settings',
            'terminal-focus',
            'sandboxes',
            'vibe-rails-ai',
            'mcp',
            'jobs'
        ]);

        return duplicateableViews.has(normalizedView) ? normalizedView : 'terminal-focus';
    }

    async openDuplicateTab() {
        try {
            const redirect = this.buildDuplicateTabRedirect();
            const response = await this.apiCall(
                `/api/v1/auth/duplicate-tab-link?redirect=${encodeURIComponent(redirect)}`,
                'POST',
                null,
                { showLoading: false }
            );

            if (!response?.url) {
                throw new Error('Duplicate tab link was not returned by the server.');
            }

            const duplicateWindow = window.open(response.url, '_blank');
            if (duplicateWindow) {
                duplicateWindow.opener = null;
                this.showToast('Duplicate Tab', 'Opened a new authenticated tab.', 'success');
                return;
            }

            const copied = await this.copyTextToClipboard(response.url);
            if (copied) {
                this.showToast('Duplicate Tab', 'Popup blocked. Copied the duplicate-tab link to the clipboard.', 'warning');
                return;
            }

            throw new Error('Popup blocked. Allow popups and try again.');
        } catch (error) {
            console.error('Failed to duplicate tab:', error);
            this.showToast('Duplicate Tab', error?.message || 'Failed to open duplicate tab.', 'error');
        }
    }

    getCurrentPortNumber() {
        const sources = [window.location.href, this.getApiBaseUrl()];

        for (const source of sources) {
            if (!source) continue;

            try {
                const url = new URL(source, window.location.origin);
                const portValue = url.port
                    || (url.protocol === 'https:' ? '443' : url.protocol === 'http:' ? '80' : '');
                if (!portValue) continue;

                const parsedPort = Number.parseInt(portValue, 10);
                if (!Number.isNaN(parsedPort)) {
                    return parsedPort;
                }
            } catch {
                // Ignore malformed URLs and continue to the next source.
            }
        }

        return null;
    }

    getSessionStorageValue(key) {
        try {
            return window.sessionStorage.getItem(key);
        } catch {
            return null;
        }
    }

    selectElementContents(element) {
        if (!element || !window.getSelection || !document.createRange) {
            return;
        }

        const selection = window.getSelection();
        if (!selection) {
            return;
        }

        const range = document.createRange();
        range.selectNodeContents(element);
        selection.removeAllRanges();
        selection.addRange(range);
    }

    async copyTextToClipboard(text) {
        try {
            if (navigator.clipboard?.writeText) {
                await navigator.clipboard.writeText(text);
                return true;
            }
        } catch {
            // Fall through to legacy copy support.
        }

        try {
            const tempTextArea = document.createElement('textarea');
            tempTextArea.value = text;
            tempTextArea.setAttribute('readonly', 'readonly');
            tempTextArea.style.position = 'fixed';
            tempTextArea.style.opacity = '0';
            document.body.appendChild(tempTextArea);
            tempTextArea.focus();
            tempTextArea.select();
            const copied = document.execCommand('copy');
            tempTextArea.remove();
            return copied;
        } catch {
            return false;
        }
    }

    // ============================================ 
    // Utils Wrappers
    // ============================================ 

    getLlmName(llmEnum) { return getLlmName(llmEnum); }
    getProjectNameFromPath(path) { return getProjectNameFromPath(path); }
    formatRelativeTime(dateString) { return formatRelativeTime(dateString); }
    getCliBrand(cli) { return getCliBrand(cli); }
    escapeHtml(text) { return escapeHtml(text); }

    // ============================================
    // Navigation & Routing
    // ============================================ 

    navigate(view, data = {}, { resetStack = false } = {}) {
        if (!this.canNavigateTo(view, data)) {
            return false;
        }

        this.closeModal();

        if (resetStack) {
            // Top-level tab switches start a fresh history so "Back" from a detail
            // view returns to its parent tab instead of replaying every tab the
            // user has ever clicked. Detail views (agent-edit, check-violations…)
            // omit this flag, so they push and keep a working Back.
            this.navigationStack = [{ view, data }];
        } else {
            const currentStackItem = this.navigationStack[this.navigationStack.length - 1];
            const currentViewName = typeof currentStackItem === 'string' ? currentStackItem : currentStackItem.view;

            if (currentViewName === view) {
                this.navigationStack[this.navigationStack.length - 1] = { view, data };
            } else {
                this.navigationStack.push({ view, data });
            }
        }

        this.currentView = view;
        this.loadView(view, data);
        return true;
    }

    updateActiveSubNav(view) {
        document.querySelectorAll('.app-subnav-link').forEach(link => {
            const linkView = link.getAttribute('data-view') || (link.getAttribute('data-action') === 'navigate-home' ? 'dashboard' : (link.getAttribute('data-action') === 'navigate-settings' ? 'settings' : ''));
            
            if (linkView === view) {
                link.classList.add('active');
            } else {
                link.classList.remove('active');
            }
        });
    }

    goBack() {
        if (this.navigationStack.length > 1) {
            const previous = this.navigationStack[this.navigationStack.length - 2];
            const previousView = previous.view || previous;
            const previousData = previous.data || {};

            if (!this.canNavigateTo(previousView, previousData)) {
                return false;
            }

            this.navigationStack.pop();
            this.currentView = previous.view || previous;
            this.loadView(this.currentView, previous.data);
            return true;
        }

        return false;
    }

    scrollPageToTop() {
        window.scrollTo(0, 0);
        document.documentElement.scrollTop = 0;
        document.body.scrollTop = 0;
    }

    queueScrollPageToTop() {
        this.scrollPageToTop();
        requestAnimationFrame(() => {
            this.scrollPageToTop();
            requestAnimationFrame(() => this.scrollPageToTop());
        });
    }

    loadView(view, data = {}) {
        this.settingsController?.unload?.();
        this.ruleController?.unload?.();
        this.jobController?.unload?.();
        this.environmentController?.unload?.();
        // Tears down the Rules workspace's local-nav observers and listeners.
        // mountAgentsOverview re-creates them, so this is safe when re-entering Rules.
        this.agentController?.unload?.();
        this.updateActiveSubNav(view);
        this.terminalController?.resetLayoutStateForNavigation();
        // Tear down the MCP Explorer's global listener / xterm instances when navigating away
        // (and before re-entering, which rebuilds them). Idempotent.
        this.mcpController?.unload?.();
        this.applyViewLayoutState(view);
        this.queueScrollPageToTop();
        const views = {
            'dashboard': () => this.dashboardController.loadDashboard(data),
            'launch-cli': () => this.cliLauncher.loadLaunchCLI(),
            'agents': () => this.dashboardController.loadDashboard(data),
            'agent-edit': () => this.agentController.loadAgentEdit(data),
            'agent-create': () => this.agentController.loadAgentCreate(),
            'check-violations': () => this.ruleController.loadCheckViolations(),
            'git-guard': () => this.ruleController.loadFocusedGitGuard(),
            'environments': () => this.environmentController.loadEnvironments(),
            'config': () => this.configController.loadConfiguration(),
            'sessions': () => this.sessionController.loadSessions(),
            'settings': () => this.settingsController.loadSettings(),
            'terminal-focus': () => this.terminalController.loadTerminalFocusView(data),
            'sandboxes': () => this.sandboxController.loadSandboxes(),
            'vibe-rails-ai': () => this.vibeRailsAiController.loadView(),
            'mcp': () => this.mcpController.loadView(),
            'jobs': () => this.jobController.loadView(data)
        };

        const loadFunc = views[view];
        if (loadFunc) {
            const loadResult = loadFunc();
            Promise.resolve(loadResult).finally(() => {
                this.queueScrollPageToTop();
            });
        } else {
            this.showError('View not found: ' + view);
        }
    }

    applyViewLayoutState(view) {
        const isTerminalFocus = view === 'terminal-focus';
        const isGitGuardFocus = view === 'git-guard';
        // The Rules workspace is a fixed two-pane split (sections over terminal), so it
        // owns the viewport the same way the terminal focus view does: no page scroll,
        // no footer, tight gutters. 'dashboard' and 'agents' both render it.
        const isRulesWorkspace = view === 'dashboard' || view === 'agents';
        const layoutRoots = [document.documentElement, document.body];

        layoutRoots.forEach((element) => {
            element.classList.toggle('terminal-focus-active', isTerminalFocus);
            element.classList.toggle('vb-terminal-focus-active', isTerminalFocus);
            element.classList.toggle('git-guard-focus-active', isGitGuardFocus);
            element.classList.toggle('vb-rules-workspace-active', isRulesWorkspace);
        });

        // Removed automatic collapsing of navbars in terminal focus mode
    }

    // ============================================ 
    // Shared Logic (Used by multiple controllers)
    // ============================================ 

    getCurrentProjectDisplayName() {
        const projectPath = this.data.configs?.rootPath || this.data.configs?.launchDirectory;
        return this.dashboardController?._customProjectName ||
            this.data.projectDisplayName ||
            (projectPath ? this.getProjectNameFromPath(projectPath) : null) ||
            'Project';
    }

    getAgentFileViewModel(agent, index) {
        const normalizePath = value => String(value || '').replace(/\\/g, '/').replace(/\/+$/, '');
        const rootPath = normalizePath(this.data.configs?.rootPath || this.data.configs?.launchDirectory);
        const agentPath = normalizePath(agent.path);
        const rootPrefix = rootPath ? `${rootPath}/` : '';
        const relativePath = rootPrefix && agentPath.toLowerCase().startsWith(rootPrefix.toLowerCase())
            ? agentPath.slice(rootPrefix.length)
            : agentPath.split('/').pop();
        const pathParts = String(relativePath || '').split('/').filter(Boolean);
        const fileName = pathParts.pop() || agent.name || 'AGENTS.md';
        const projectName = this.getCurrentProjectDisplayName();
        const scopeLeaf = agent.customName || 'Agent';
        const scopeName = [projectName, ...pathParts, scopeLeaf].filter(Boolean).join('/');
        const ruleCount = Number(agent.ruleCount) || agent.rules?.length || 0;

        return {
            agent,
            index,
            displayName: scopeName,
            relativePath: [...pathParts, fileName].join('/'),
            ruleCount,
            hasRules: ruleCount > 0
        };
    }

    renderAgentFileItem(item, { empty = false } = {}) {
        const ruleLabel = `${item.ruleCount} ${item.ruleCount === 1 ? 'rule' : 'rules'}`;
        return `
            <div class="agent-file-tree-item${empty ? ' agent-file-tree-item--empty' : ''}">
                <button type="button" class="agent-file-tree-open" data-agent-tree-index="${item.index}"
                    aria-label="Open ${this.escapeHtml(item.displayName)}">
                    <span class="agent-file-tree-icon" aria-hidden="true">
                        <i class="fa-regular fa-file-lines"></i>
                    </span>
                    <span class="agent-file-tree-info">
                        <span class="agent-file-tree-name text-truncate">${this.escapeHtml(item.displayName)}</span>
                        <span class="agent-file-tree-path">${this.escapeHtml(item.relativePath)}</span>
                    </span>
                </button>
                <button type="button" class="btn btn-sm btn-link text-muted p-1 agent-file-tree-rename"
                    data-agent-rename="${item.index}" title="Set a custom name">
                    <i class="fa-solid fa-pen" aria-hidden="true"></i>
                    <span class="visually-hidden">Rename ${this.escapeHtml(item.displayName)}</span>
                </button>
                <span class="agent-file-tree-badge${item.hasRules ? ' agent-file-tree-badge--active' : ''}">${ruleLabel}</span>
            </div>`;
    }

    // Pure renderer for the agent-file list. Returns HTML only; callers are
    // responsible for binding handlers (see AgentController.bindAgentListItems).
    renderLocalFileTree() {
        if (this.data.agents.length === 0) {
            return `
                <div class="agent-files-empty py-4">
                    <div class="mb-3 opacity-25">
                        <svg xmlns="http://www.w3.org/2000/svg" width="40" height="40" fill="currentColor" viewBox="0 0 16 16">
                            <path d="M14 4.5V14a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V2a2 2 0 0 1 2-2h5.5zm-3 0V1H4a1 1 0 0 0-1 1v12a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1V4.5z"/>
                        </svg>
                    </div>
                    <p class="text-muted small">No agent files found in this project.</p>
                </div>
            `;
        }

        const files = this.data.agents.map((agent, index) => this.getAgentFileViewModel(agent, index));
        const configured = files.filter(file => file.hasRules);
        const empty = files.filter(file => !file.hasRules);

        return `
            <div class="agent-files-tree">
                <section class="agent-files-group agent-files-configured" aria-labelledby="configured-agent-files-title">
                    <div class="agent-files-group-heading">
                        <div>
                            <h3 id="configured-agent-files-title">Configured</h3>
                            <p>Agent files currently enforcing one or more rules.</p>
                        </div>
                        <span>${configured.length}</span>
                    </div>
                    <div class="agent-files-list">
                        ${configured.length > 0
                            ? configured.map(item => this.renderAgentFileItem(item)).join('')
                            : '<p class="agent-files-group-empty">No agent files have rules yet.</p>'}
                    </div>
                </section>

                ${empty.length > 0 ? `
                    <details class="agent-files-group agent-files-unconfigured" data-agent-empty-group>
                        <summary>
                            <span class="agent-files-disclosure-icon" aria-hidden="true">
                                <i class="fa-solid fa-chevron-right"></i>
                            </span>
                            <span class="agent-files-disclosure-copy">
                                <strong>Without rules</strong>
                                <small>Available agent files that are not currently enforcing anything.</small>
                            </span>
                            <span class="agent-files-disclosure-count">${empty.length}</span>
                        </summary>
                        <div class="agent-files-list">
                            ${empty.map(item => this.renderAgentFileItem(item, { empty: true })).join('')}
                        </div>
                    </details>` : ''}
            </div>`;
    }

    async launchCliForProject(projectPath, cliName) {
        const cliType = cliName.toLowerCase();
        try {
            const requestBody = {
                workingDirectory: projectPath,
                environmentName: null,
                args: []
            };

            const response = await this.apiCall(`/api/v1/cli/launch/${cliType}`, 'POST', requestBody);

            if (response.success) {
                this.showToast('CLI Launched', `${cliName} launched for ${this.getProjectNameFromPath(projectPath)}`, 'success');
            } else {
                this.showToast('CLI Error', response.message, 'error');
            }
        } catch (error) {
            this.showError(`Failed to launch ${cliName} CLI`);
        }
    }

    // ============================================
    // Helper Methods & Data
    // ============================================

    showCustomNameModal({ onSaved = null } = {}) {
        const path = this.data.configs?.rootPath || '';
        const currentName = this.data.projectDisplayName || this.getProjectNameFromPath(path);

        this.showModal('Set Custom Project Name', `
            <form id="custom-name-form">
                <div class="mb-3">
                    <label class="form-label">Custom Name</label>
                    <input type="text" class="form-control" id="project-custom-name" value="${this.escapeHtml(currentName)}" placeholder="${this.escapeHtml(currentName)}" required>
                    <small class="form-text text-muted">Enter a friendly name to identify this project in your history.</small>
                </div>
                <div class="d-flex gap-2 justify-content-end">
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                    <button type="submit" class="btn btn-primary">Save Custom Name</button>
                </div>
            </form>
        `);

        document.getElementById('custom-name-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            const newName = document.getElementById('project-custom-name').value.trim();
            const path = this.data.configs?.rootPath;

            if (!path) {
                this.showError('Cannot determine project path');
                return;
            }

            try {
                await this.apiCall('/api/v1/projects/name', 'PUT', {
                    path: path,
                    customName: newName
                });

                this.showToast('Success', `Project name updated to "${newName}"`, 'success');
                this.data.projectDisplayName = newName;
                this.dashboardController._customProjectName = newName;
                document.title = newName;
                if (typeof window.__viberails_setTitle__ === 'function') {
                    try { window.__viberails_setTitle__(newName); } catch { /* ignore */ }
                }
                this.closeModal();
                if (typeof onSaved === 'function') onSaved(newName);
            } catch (error) {
                this.showError('Failed to update project name: ' + error.message);
            }
        });
    }

    // ============================================
    // UI Helpers
    // ============================================

    showModal(title, content) {
        const modalContainer = document.getElementById('modal-container');
        // Escape title to prevent XSS, content is trusted HTML template
        const escapedTitle = this.escapeHtml(title);
        modalContainer.innerHTML = `
            <div class="modal fade show d-block" tabindex="-1">
                <div class="modal-dialog modal-lg modal-dialog-scrollable">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title">${escapedTitle}</h5>
                            <button type="button" class="btn-close" data-action="close-modal"></button>
                        </div>
                        <div class="modal-body">
                            ${content}
                        </div>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show"></div>
        `;
        // Bind all close buttons without inline handlers (CSP-safe)
        modalContainer.querySelectorAll('[data-action="close-modal"]')
            .forEach(btn => btn.addEventListener('click', () => this.closeModal()));
    }

    closeModal() {
        const modalContainer = document.getElementById('modal-container');
        modalContainer.innerHTML = '';
    }

    showToast(title, message, type = 'info', options = {}) {
        showAppToast(title, message, type, options);
    }

    showError(message) {
        this.showToast('Error', message, 'error');
    }

    showLoading(show = true) {
        const overlay = document.getElementById('loading-overlay');
        if (!overlay) return;
        if (show) {
            overlay.classList.remove('d-none');
        } else {
            overlay.classList.add('d-none');
        }
    }

    beginLoadingOverlay() {
        this.loadingOverlayPendingCount += 1;
        if (this.loadingOverlayPendingCount > 1) {
            return;
        }

        if (this.loadingOverlayTimer) {
            clearTimeout(this.loadingOverlayTimer);
        }

        this.loadingOverlayTimer = setTimeout(() => {
            this.loadingOverlayTimer = null;
            if (this.loadingOverlayPendingCount <= 0) {
                return;
            }

            this.loadingOverlayVisibleSince = Date.now();
            this.showLoading(true);
        }, 250);
    }

    endLoadingOverlay() {
        this.loadingOverlayPendingCount = Math.max(0, this.loadingOverlayPendingCount - 1);
        if (this.loadingOverlayPendingCount > 0) {
            return;
        }

        if (this.loadingOverlayTimer) {
            clearTimeout(this.loadingOverlayTimer);
            this.loadingOverlayTimer = null;
        }

        const visibleFor = this.loadingOverlayVisibleSince > 0
            ? Date.now() - this.loadingOverlayVisibleSince
            : 0;
        const hideDelay = this.loadingOverlayVisibleSince > 0
            ? Math.max(0, 180 - visibleFor)
            : 0;

        setTimeout(() => {
            if (this.loadingOverlayPendingCount > 0) {
                return;
            }
            this.loadingOverlayVisibleSince = 0;
            this.showLoading(false);
        }, hideDelay);
    }

    getHostUnreachableMessage() {
        return 'Cannot reach the VibeRails host. It may have stopped. Relaunch VibeRails, then refresh this page.';
    }

    isHostUnreachableError(error) {
        if (!error) return false;
        if (error.name === 'HostUnreachableError') return true;
        if (!(error instanceof TypeError)) return false;

        const message = (error.message || '').toLowerCase();
        return message.includes('failed to fetch')
            || message.includes('load failed')
            || message.includes('networkerror')
            || message.includes('network error');
    }

    notifyHostUnreachable() {
        if (this.hostUnreachableToastShown) return;
        this.hostUnreachableToastShown = true;
        this.showToast('Host Unreachable', this.getHostUnreachableMessage(), 'error');
    }

    // ============================================
    // Lifecycle Heartbeat
    // ============================================

    getApiBaseUrl() {
        return window.__viberails_API_BASE__ || '';
    }

    getOrCreateLifecycleClientId() {
        return (window.crypto && typeof window.crypto.randomUUID === 'function')
            ? window.crypto.randomUUID()
            : `client-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
    }

    getLifecycleUrl(path) {
        const baseUrl = this.getApiBaseUrl();
        const clientId = encodeURIComponent(this.lifecycleClientId);
        return `${baseUrl}${path}?clientId=${clientId}`;
    }

    getLifecycleHeaders() {
        const tabToken = sessionStorage.getItem('viberails_tab');
        return {
            ...(tabToken ? { 'viberails_tab': tabToken } : {})
        };
    }

    async sendLifecyclePing() {
        const url = this.getLifecycleUrl('/api/v1/lifecycle/ping');
        try {
            await fetch(url, {
                method: 'POST',
                headers: this.getLifecycleHeaders(),
                credentials: 'include',
                cache: 'no-store'
            });
        } catch (error) {
            // Best-effort only.
        }
    }

    sendLifecycleDisconnect() {
        const url = this.getLifecycleUrl('/api/v1/lifecycle/disconnect');

        const headers = this.getLifecycleHeaders();
        if (!window.__viberails_VSCODE__ && typeof navigator.sendBeacon === 'function' && Object.keys(headers).length === 0) {
            try {
                navigator.sendBeacon(url);
                return;
            } catch (error) {
                // Fallback to fetch below.
            }
        }

        fetch(url, {
            method: 'POST',
            headers,
            credentials: 'include',
            keepalive: true
        }).catch(() => {
            // Best-effort only.
        });
    }

    startLifecycleHeartbeat() {
        if (this.lifecycleHeartbeatTimer) {
            return;
        }

        this.sendLifecyclePing();
        this.lifecycleHeartbeatTimer = setInterval(() => {
            this.sendLifecyclePing();
        }, 30000);

        const onDisconnect = () => {
            this.appEventClient?.stop?.();
            this.sendLifecycleDisconnect();
        };
        window.addEventListener('beforeunload', onDisconnect);
        window.addEventListener('pagehide', onDisconnect);
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible') {
                this.sendLifecyclePing();
            }
        });
    }

    // ============================================ 
    // API Calls
    // ============================================ 

    async apiCall(endpoint, method = 'GET', data = null, requestOptions = {}) {
        const showLoadingOverlay = requestOptions?.showLoading !== false;
        if (showLoadingOverlay) {
            this.beginLoadingOverlay();
        }
        try {
            const tabToken = sessionStorage.getItem('viberails_tab');
            const options = {
                method,
                headers: {
                    'Content-Type': 'application/json',
                    ...(tabToken ? { 'viberails_tab': tabToken } : {})
                },
                credentials: 'include'  // Send cookies with requests
            };
            if (data) options.body = JSON.stringify(data);
            const baseUrl = window.__viberails_API_BASE__ || '';
            const response = await fetch(baseUrl + endpoint, options);
            this.hostUnreachableToastShown = false;

            if (response.status === 401) {
                if (window.__viberails_VSCODE__) {
                    // In VS Code webview, can't redirect - show error
                    throw new Error('Session expired. Close and reopen the VibeRails panel to re-authenticate.');
                }
                window.location.href = (baseUrl || '') + '/auth/bootstrap';
                throw new Error('Unauthorized');
            }

            if (!response.ok) throw new Error(`API call failed: ${response.statusText}`);
            return await response.json();
        } catch (error) {
            if (this.isHostUnreachableError(error)) {
                this.notifyHostUnreachable();
                const hostError = new Error(this.getHostUnreachableMessage());
                hostError.name = 'HostUnreachableError';
                throw hostError;
            }
            console.error('API Error:', error);
            throw error;
        } finally {
            if (showLoadingOverlay) {
                this.endLoadingOverlay();
            }
        }
    }

    // ============================================ 
    // Data Refresh
    // ============================================ 

    async refreshDashboardData() {
        try {
            // Fetch environments (global, always available)
            try {
                const envResponse = await this.apiCall('/api/v1/environments', 'GET', null, { showLoading: false });
                // Keep one normalization path for environment records. In particular, this
                // preserves the `hidden` flag used by every LLM/environment selector.
                this.environmentController.setEnvironments(envResponse.environments || []);
            } catch (error) {
                console.error('Failed to fetch environments:', error);
                this.data.environments = [];
            }

            if (this.data.isInGit) {
                try {
                    const agentsResponse = await this.apiCall('/api/v1/agents', 'GET', null, { showLoading: false });
                    this.data.agents = (agentsResponse.agents || []).map((agent, index) => ({
                        id: index + 1,
                        name: agent.name,
                        customName: agent.customName || null,
                        path: agent.path,
                        ruleCount: agent.ruleCount,
                        rules: agent.rules.map(rule => ({
                            text: rule.text,
                            enforcement: rule.enforcement || 'WARN'
                        }))
                    }));
                } catch (error) {
                    console.error('Failed to fetch agents:', error);
                    this.data.agents = [];
                }

                try {
                    const rulesResponse = await this.apiCall('/api/v1/rules/details', 'GET', null, { showLoading: false });
                    this.data.availableRulesWithDescriptions = rulesResponse.rules || [];
                } catch (error) {
                    console.error('Failed to fetch available rules:', error);
                    this.data.availableRulesWithDescriptions = [];
                }

                // Fetch sandboxes (local context only)
                try {
                    await this.sandboxController.refreshSandboxes();
                } catch (error) {
                    console.error('Failed to fetch sandboxes:', error);
                    this.data.sandboxes = [];
                }
            } else {
                this.data.agents = [];
                this.data.sandboxes = [];
            }

        } catch (error) {
            console.error('Failed to refresh dashboard data:', error);
        }
    }

    setupKeyboardShortcuts() {
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                const modalContainer = document.getElementById('modal-container');
                const hadOpenModal = Boolean(modalContainer?.firstElementChild);
                this.closeModal();
                if (!hadOpenModal && this.navigationStack.length > 1) {
                    this.goBack();
                }
            } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'b') {
                const active = document.activeElement;
                if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.isContentEditable || active.classList.contains('inputarea'))) {
                    return;
                }
                e.preventDefault();
                this.toggleSidebar?.();
            }
        });
    }
}

// Initialize the app — DOM is already ready when this module is executed
// (dynamic import via `await import()` in index.html resolves after DOMContentLoaded)
window.app = new VibeControlApp();
