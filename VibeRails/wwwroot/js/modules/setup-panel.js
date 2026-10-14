import { escapeHtml, getCliBrand } from './utils.js';
import { SetupInstallWindow } from './setup-install-window.js';

const isInstalled = tool => tool.state === 'ready' || tool.state === 'restart';

/** Joins names as prose: "A", "A and B", "A, B and C". */
function listNames(names) {
    if (names.length <= 1) return names.join('');
    return `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`;
}

/**
 * What the Setup page and the app-load warning say, from one status response. Pure, so the
 * wording is testable without a DOM. Required (the essentials, then "at least one" AI CLI)
 * comes first; Optional, including any missing package manager, comes last.
 */
export function buildSetupModel(status) {
    const tools = status?.tools || [];
    const byId = new Map(tools.map(tool => [tool.id, tool]));
    const readyAgents = tools.filter(tool => tool.group === 'agent' && isInstalled(tool));
    const missing = tools.filter(tool => tool.group === 'required' && !isInstalled(tool)).map(tool => tool.name);
    if (readyAgents.length === 0) missing.push('an AI coding CLI');

    const decorate = tool => ({
        ...tool,
        installed: isInstalled(tool),
        expandedByDefault: !isInstalled(tool) && (tool.group === 'required' || tool.group === 'package-manager'
            || (tool.group === 'agent' && readyAgents.length === 0)),
        methods: tool.methods.map(method => {
            const needs = method.requires ? byId.get(method.requires) : null;
            return { ...method, blockedBy: needs && !isInstalled(needs) ? needs : null };
        })
    });
    const pick = (...groups) => tools.filter(tool => groups.includes(tool.group)).map(decorate);
    const sections = [
        {
            key: 'required', title: 'Required', groups: [
                { key: 'required', tools: pick('required') },
                { key: 'agent', title: 'AI coding CLI', hint: 'Install at least one.', tools: pick('agent') }
            ]
        },
        {
            key: 'optional', title: 'Optional', groups: [
                // Package managers only appear when one is missing.
                { key: 'optional', tools: pick('optional', 'package-manager').filter(tool => tool.group !== 'package-manager' || !tool.installed) }
            ]
        }
    ].map(section => ({ ...section, groups: section.groups.filter(group => group.tools.length > 0) }))
        .filter(section => section.groups.length > 0);

    return {
        complete: missing.length === 0,
        missing,
        restartNeeded: tools.filter(tool => tool.state === 'restart'),
        sections,
        alert: missing.length === 0 ? null : `Install ${listNames(missing)} to start using VibeRails.`
    };
}

/** Settings → Setup. Reads the saved check; the server probes again only while setup is incomplete. */
export class SetupPanel {
    constructor(app, container, { onStatus } = {}) {
        this.app = app;
        this.container = container;
        this.onStatus = onStatus;
        this.status = null;
        this.expanded = new Map();
        this.selectedMethod = new Map();
        this.installWindow = new SetupInstallWindow(app, { onFinished: () => this.refresh() });
        container.addEventListener('click', event => this._onClick(event));
    }

    async activate() {
        if (this.status) { this.render(); return; }
        this.container.innerHTML = '<p class="text-muted" role="status">Checking this machine…</p>';
        try {
            this._apply(await this.app.apiCall('/api/v1/setup/status', 'GET', null, { showLoading: false }));
        } catch (error) {
            this.container.innerHTML = `<p class="text-danger" role="alert">Could not check this machine: ${escapeHtml(error?.message || 'unknown error')}</p>`;
        }
    }

    /** Probes again after an install finishes, whatever the installer's exit code was. */
    async refresh() {
        try {
            const status = await this.app.apiCall('/api/v1/setup/status/refresh', 'POST', {}, { showLoading: false });
            this._apply(status);
            return status;
        } catch (error) {
            this.app.showError(`Could not check this machine again: ${error?.message || 'unknown error'}`);
            return null;
        }
    }

    unload() {
        // A running install keeps its window; closing that window is what stops it.
    }

    _apply(status) {
        this.status = status;
        this.render();
        this.onStatus?.(status);
    }

    render() {
        if (!this.status) return;
        const model = buildSetupModel(this.status);
        this.container.innerHTML = `<div class="vb-setup">${model.sections.map(section => this._section(section)).join('')}</div>`;
    }

    _section(section) {
        return `<section class="vb-setup-section" aria-labelledby="vb-setup-${section.key}">
            <h3 class="vb-setup-section-title" id="vb-setup-${section.key}">${escapeHtml(section.title)}</h3>
            ${section.groups.map(group => `
                ${group.title ? `<div class="vb-setup-subhead"><span>${escapeHtml(group.title)}</span><span>${escapeHtml(group.hint)}</span></div>` : ''}
                <ul class="vb-setup-list">${group.tools.map(tool => this._row(tool)).join('')}</ul>`).join('')}
        </section>`;
    }

    _row(tool) {
        const expanded = this.expanded.get(tool.id) ?? tool.expandedByDefault;
        const brand = tool.brand ? getCliBrand(tool.brand) : null;
        const icon = brand?.logo
            ? `<img src="${escapeHtml(brand.logo)}" alt="" width="20" height="20"${brand.logoFilter ? ` style="filter:${escapeHtml(brand.logoFilter)}"` : ''}>`
            : `<i class="fa-solid ${this._fallbackIcon(tool.id)}" aria-hidden="true"></i>`;
        const pill = tool.state === 'ready'
            ? `<span class="vb-setup-pill" data-state="ready"><i class="fa-solid fa-check" aria-hidden="true"></i> Ready</span>`
            : tool.state === 'restart'
                ? `<span class="vb-setup-pill" data-state="restart"><i class="fa-solid fa-rotate" aria-hidden="true"></i> Restart needed</span>`
                : `<span class="vb-setup-pill" data-state="missing">Not installed</span>`;
        const meta = tool.state === 'ready'
            ? `<span class="vb-setup-path" title="${escapeHtml(tool.path || '')}">${tool.version ? `v${escapeHtml(tool.version)} · ` : ''}${escapeHtml(tool.path || '')}</span>`
            : tool.state === 'restart'
                ? `<span class="vb-setup-why" title="${escapeHtml(tool.path || '')}">Installed. Restart VibeRails (and VS Code, if you use the extension) so new terminals can find it.</span>`
                : `<span class="vb-setup-why">${escapeHtml(tool.why)}</span>`;
        const toggle = !tool.installed && !expanded
            ? `<button type="button" class="btn btn-outline-primary btn-sm" data-setup-action="expand" data-tool="${escapeHtml(tool.id)}">Install…</button>`
            : '';
        return `<li class="vb-setup-row" data-state="${escapeHtml(tool.state)}" data-tool-row="${escapeHtml(tool.id)}">
            <div class="vb-setup-row-main">
                <span class="vb-setup-icon">${icon}</span>
                <div class="vb-setup-row-text">
                    <div class="vb-setup-row-title"><strong>${escapeHtml(tool.name)}</strong>${pill}</div>
                    ${meta}
                </div>
                ${toggle}
            </div>
            ${!tool.installed && expanded ? this._installer(tool) : ''}
        </li>`;
    }

    _installer(tool) {
        if (tool.methods.length === 0) {
            // winget only comes from the Microsoft Store page.
            return `<div class="vb-setup-installer">
                <div class="vb-setup-installer-actions">
                    <a class="btn btn-primary btn-sm" href="${escapeHtml(tool.docsUrl)}" target="_blank" rel="noopener noreferrer">
                        Get ${escapeHtml(tool.name)} <i class="fa-solid fa-arrow-up-right-from-square" aria-hidden="true"></i></a>
                </div>
            </div>`;
        }
        const selectedId = this.selectedMethod.get(tool.id)
            ?? (tool.methods.find(method => !method.blockedBy) || tool.methods[0]).id;
        const method = tool.methods.find(candidate => candidate.id === selectedId) || tool.methods[0];
        const choices = tool.methods.length > 1
            ? `<div class="vb-setup-methods" role="radiogroup" aria-label="Install with">
                ${tool.methods.map(candidate => `<button type="button" role="radio" class="vb-setup-method"
                    aria-checked="${candidate.id === method.id}" data-setup-action="method"
                    data-tool="${escapeHtml(tool.id)}" data-method="${escapeHtml(candidate.id)}">
                    ${escapeHtml(candidate.label)}${candidate.recommended ? ' <span class="vb-setup-recommended">Recommended</span>' : ''}
                </button>`).join('')}
            </div>`
            : '';
        const blocked = method.blockedBy
            ? `<p class="vb-setup-blocked"><i class="fa-solid fa-link" aria-hidden="true"></i>
                Needs ${escapeHtml(method.blockedBy.name)} first.
                <button type="button" class="btn btn-link btn-sm p-0" data-setup-action="expand" data-tool="${escapeHtml(method.blockedBy.id)}" data-scroll="true">Install ${escapeHtml(method.blockedBy.name)}</button></p>`
            : '';
        return `<div class="vb-setup-installer">
            ${choices}
            ${blocked}
            <div class="vb-setup-installer-actions">
                <button type="button" class="btn btn-primary btn-sm" data-setup-action="install"
                    data-tool="${escapeHtml(tool.id)}" data-method="${escapeHtml(method.id)}"${method.blockedBy ? ' disabled' : ''}>
                    <i class="fa-solid fa-download" aria-hidden="true"></i> Install ${escapeHtml(tool.name)}</button>
                <span class="vb-setup-runs" title="The command the install window runs"><code>${escapeHtml(method.command)}</code></span>
            </div>
            ${method.note ? `<p class="vb-setup-installer-note">${escapeHtml(method.note)}</p>` : ''}
        </div>`;
    }

    _fallbackIcon(id) {
        return {
            git: 'fa-code-branch', pwsh: 'fa-terminal', node: 'fa-cubes', python: 'fa-file-code',
            winget: 'fa-box-open', brew: 'fa-beer-mug-empty'
        }[id] || 'fa-toolbox';
    }

    _find(toolId, methodId) {
        const tool = this.status?.tools.find(candidate => candidate.id === toolId);
        return { tool, method: tool?.methods.find(candidate => candidate.id === methodId) };
    }

    _onClick(event) {
        const button = event.target.closest('[data-setup-action]');
        if (!button || !this.container.contains(button)) return;
        const { setupAction: action, tool: toolId, method: methodId } = button.dataset;
        if (action === 'expand') {
            this.expanded.set(toolId, true);
            this.render();
            if (button.dataset.scroll)
                this.container.querySelector(`[data-tool-row="${CSS.escape(toolId)}"]`)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
        } else if (action === 'method') {
            this.selectedMethod.set(toolId, methodId);
            this.render();
        } else if (action === 'install') {
            const { tool, method } = this._find(toolId, methodId);
            if (!tool || !method) return;
            if (this.installWindow.running) {
                this.installWindow.show();
                this.app.showToast('Setup', 'Finish or stop the current install first.', 'warning');
                return;
            }
            this.installWindow.open(tool, method);
        }
    }
}

/**
 * The warning bar the app shows on load, next to the not-in-git bar, while a required tool or
 * every AI CLI is missing. Dismissing it lasts until the next load.
 */
export class SetupAlert {
    constructor(app) {
        this.app = app;
        this.element = null;
        this.view = null;
        this.dismissed = false;
    }

    /** Settings → Setup is where the bar points, so it steps aside on Settings. */
    setView(view) {
        this.view = view;
        if (this.element) this.element.hidden = view === 'settings';
    }

    async check() {
        try {
            this.update(await this.app.apiCall('/api/v1/setup/status', 'GET', null, { showLoading: false }));
        } catch {
            // A tab-child backend has no Setup routes; there is nothing to warn about there.
        }
    }

    update(status) {
        const message = buildSetupModel(status).alert;
        if (!message || this.dismissed) { this.element?.remove(); this.element = null; return; }
        const content = document.getElementById('app-content');
        if (!content) return;
        if (!this.element) {
            this.element = document.createElement('div');
            this.element.id = 'setup-helper-bar';
            this.element.addEventListener('click', event => this._onClick(event));
            content.parentNode.insertBefore(this.element, content);
        }
        this.element.innerHTML = `
            <div class="alert alert-warning d-flex flex-wrap align-items-center gap-2 mb-3" role="alert">
                <span class="flex-grow-1 small mb-0"><strong>VibeRails isn’t set up yet.</strong> ${escapeHtml(message)}</span>
                <button type="button" class="btn btn-sm btn-warning" data-setup-alert="open">Open Setup</button>
                <button type="button" class="btn-close" data-setup-alert="dismiss" aria-label="Dismiss"></button>
            </div>`;
        this.element.hidden = this.view === 'settings';
    }

    _onClick(event) {
        const action = event.target.closest('[data-setup-alert]')?.dataset.setupAlert;
        if (action === 'open') {
            this.app.navigate('settings', { tab: 'setup' }, { resetStack: true });
        } else if (action === 'dismiss') {
            this.dismissed = true;
            this.element?.remove();
            this.element = null;
        }
    }
}
