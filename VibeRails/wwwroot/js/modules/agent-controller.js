import { getFileTypeVisual } from './file-type-icons.js';
import { isConfirmDialogOpen } from './utils.js';

const ENFORCEMENT_LEVELS = [
    { value: 'WARN', icon: '⚠️', blurb: 'Warn, but let the commit through.' },
    { value: 'COMMIT', icon: '💬', blurb: 'Require an explanation in the commit message.' },
    { value: 'STOP', icon: '🛑', blurb: 'Block the commit until it is fixed.' }
];

const PATH_LOCK_RULES = [
    {
        kind: 'file',
        template: "File Lock('path to file')",
        label: 'File path',
        placeholder: 'src/config.json'
    },
    {
        kind: 'directory',
        template: "Directory Lock('path to directory')",
        label: 'Directory path',
        placeholder: 'src/generated'
    }
];

export function buildAgentFilePath(rawDirectory) {
    const directory = String(rawDirectory ?? '').trim();
    if (!directory) return null;
    return `${directory.replace(/[\\/]+$/, '')}/vc.rules.md`;
}

export class AgentController {
    constructor(app) {
        this.app = app;
        this.currentAgent = null;
        this.selectedRuleIndex = null;
        // Path of the vc.rules.md open in the Rule files view's inline editor.
        this.selectedAgentPath = null;

        // Wizard state for agent creation
        this.wizardState = {
            currentStep: 1,
            totalSteps: 4,
            directory: '',
            selectedRules: [],  // Array of { text: string, enforcement: string }
            fileReferences: []
        };
    }

    getPathLockDefinition(ruleText) {
        const text = String(ruleText || '').trim();
        return PATH_LOCK_RULES.find(definition =>
            text === definition.template
            || text.toLowerCase().startsWith(`${definition.kind === 'file' ? 'file' : 'directory'} lock(`)
            || text.toLowerCase().startsWith(`${definition.kind === 'file' ? 'file' : 'directory'} lock (`)
        ) || null;
    }

    isPathLockTemplate(ruleText) {
        return PATH_LOCK_RULES.some(definition => definition.template === ruleText);
    }

    extractPathLockPath(ruleText) {
        const definition = this.getPathLockDefinition(ruleText);
        if (!definition || this.isPathLockTemplate(ruleText)) return '';
        // `s` so a hand-edited rule carrying a line break still round-trips into the editor rather
        // than silently rendering as an empty path. The writer refuses to author one (see
        // buildPathLockRuleText, and PathLockRule.TryParse server-side), but reading is not writing.
        const match = String(ruleText).match(/\(\s*(['"])(.*?)\1\s*\)\s*$/s);
        return match?.[2] || '';
    }

    buildPathLockRuleText(ruleTemplate, rawPath) {
        const definition = PATH_LOCK_RULES.find(item => item.template === ruleTemplate);
        if (!definition) return ruleTemplate;

        const enteredPath = String(rawPath || '').trim();
        if (!enteredPath) {
            throw new Error(`${definition.label} is required.`);
        }
        if (/^(?:[a-z]:[\\/]|[\\/])/i.test(enteredPath)) {
            throw new Error(`${definition.label} must be relative to the vc.rules.md directory.`);
        }
        if (enteredPath.includes("'")) {
            throw new Error(`${definition.label} cannot contain a single quote.`);
        }
        // A rule is one line of vc.rules.md. A line break here would be written back as two lines,
        // and a second line starting with '#' ends the rules section, dropping every rule below it
        // from both the hook and this page. The server refuses it too; this is the readable error.
        if (/[\r\n\0]/.test(enteredPath)) {
            throw new Error(`${definition.label} cannot contain a line break.`);
        }

        let normalizedPath = enteredPath.replaceAll('\\', '/');
        while (normalizedPath.startsWith('./')) normalizedPath = normalizedPath.slice(2);
        if (normalizedPath.split('/').includes('..')) {
            throw new Error(`${definition.label} cannot leave the vc.rules.md directory.`);
        }
        if (!normalizedPath || (definition.kind === 'file' && normalizedPath === '.')) {
            throw new Error(`${definition.label} must identify a file.`);
        }
        if (normalizedPath.length > 1) normalizedPath = normalizedPath.replace(/\/+$/, '');

        const name = definition.kind === 'file' ? 'File Lock' : 'Directory Lock';
        return `${name}('${normalizedPath}')`;
    }

    showPathLockPathPicker(agent, ruleTemplate) {
        const definition = PATH_LOCK_RULES.find(item => item.template === ruleTemplate);
        if (!definition) {
            this.showEnforcementPicker(agent, ruleTemplate);
            return;
        }

        this.app.showModal(`Configure ${definition.kind} lock`, `
            <form id="path-lock-rule-form">
                <p class="text-muted">Enter a path relative to the directory containing this vc.rules.md.</p>
                <div class="mb-3">
                    <label class="form-label" for="path-lock-rule-path">${definition.label}</label>
                    <input class="form-control" id="path-lock-rule-path" type="text"
                        placeholder="${definition.placeholder}" autocomplete="off" required>
                    <small class="form-text text-muted">
                        ${definition.kind === 'file'
                            ? 'The exact file is protected from additions, edits, deletion, and renames.'
                            : 'Every file at or below this directory is protected recursively.'}
                    </small>
                </div>
                <div class="d-flex justify-content-end gap-2">
                    <button type="button" class="btn btn-outline-secondary" data-action="close-modal">Cancel</button>
                    <button type="submit" class="btn btn-primary">Choose enforcement</button>
                </div>
            </form>`);

        document.getElementById('path-lock-rule-form')?.addEventListener('submit', event => {
            event.preventDefault();
            try {
                const path = document.getElementById('path-lock-rule-path')?.value;
                this.showEnforcementPicker(agent, this.buildPathLockRuleText(ruleTemplate, path));
            } catch (error) {
                this.app.showToast('Path lock', error.message, 'warning');
            }
        });
    }

    // ============================================
    // Rule Files & Rules View
    // ============================================

    loadAgents() {
        const content = document.getElementById('app-content');
        if (!content) {
            return;
        }

        this.mountAgentsOverview(content);
    }

    mountAgentsOverview(container) {
        if (!container) return null;

        container.innerHTML = '';
        const fragment = this.app.cloneTemplate('agents-template');
        const root = fragment.querySelector('[data-view="agents"]');

        container.appendChild(fragment);
        if (root) {
            this.app.ruleController.attachRulesOverview(root);
        }
        return root;
    }

    // The RULES page: the vc.rules.md list plus the inline rule editor, full page.
    // Heavier markdown editing still goes to 'agent-edit'.
    async loadRuleFiles() {
        const content = document.getElementById('app-content');
        if (!content) return;

        // Re-read the agent list on every entry so rules added elsewhere (the full
        // editor, another tab, the CLI) are visible without a manual refresh.
        await this.app.refreshDashboardData();

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('rule-files-template');
        const root = fragment.querySelector('[data-view="rule-files"]');
        if (root) {
            this.app.bindAction(root, '[data-action="create-agent-file"]', () => this.app.navigate('agent-create'));
            this.app.bindAction(root, '[data-action="refresh-agent-files"]', async () => {
                await this.app.refreshDashboardData();
                this.renderAgentFileTree(root);
            });
            this.renderAgentFileTree(root);
        }
        content.appendChild(fragment);
    }

    // Compact CRUD surface used by the unified Project health page. The existing
    // file-tree and inline-editor renderers remain the single implementation of rule
    // mutations; this method only gives them a focused modal host.
    openRuleManager() {
        this.app.showModal('Manage rules', `
            <div class="project-health-rule-manager" data-rule-manager-modal>
                <header class="project-health-rule-manager-header">
                    <p>Choose a <code>vc.rules.md</code> file, then add, remove, or change its enforcement.</p>
                    <div class="d-flex gap-2">
                        <button class="btn btn-sm btn-outline-secondary" type="button"
                            data-rule-manager-refresh title="Reload rule files">
                            <i class="fa-solid fa-rotate-right" aria-hidden="true"></i>
                            Refresh
                        </button>
                        <button class="btn btn-sm btn-primary" type="button" data-rule-manager-create>
                            <i class="fa-solid fa-plus" aria-hidden="true"></i>
                            New rule file
                        </button>
                    </div>
                </header>
                <div class="rules-files-split project-health-rule-manager-grid">
                    <div class="rules-files-rail" data-agent-file-tree></div>
                    <div class="rules-files-detail" data-agent-rule-editor></div>
                </div>
            </div>`);

        const modalContainer = document.getElementById('modal-container');
        const root = modalContainer?.querySelector('[data-rule-manager-modal]');
        if (!root) return;

        const dialog = modalContainer.querySelector('.modal-dialog');
        dialog?.classList.remove('modal-lg');
        dialog?.classList.add('modal-xl', 'project-health-rule-modal-dialog');

        root.querySelector('[data-rule-manager-create]')?.addEventListener('click', () => {
            this.app.navigate('agent-create');
        });
        root.querySelector('[data-rule-manager-refresh]')?.addEventListener('click', async event => {
            const button = event.currentTarget;
            button.disabled = true;
            try {
                await this.app.refreshDashboardData();
                this.renderAgentFileTree(root);
            } finally {
                button.disabled = false;
            }
        });
        this.renderAgentFileTree(root);
    }

    // Rule CRUD can open while the rule manager itself is already an app modal. Keep
    // that manager mounted underneath a focused child layer so Cancel, Escape, and a
    // successful mutation all return to the same file and scroll position.
    openRuleCrudModal(title, content, { parentRoot = null } = {}) {
        const manager = parentRoot?.matches?.('[data-rule-manager-modal]')
            ? parentRoot
            : parentRoot?.closest?.('[data-rule-manager-modal]');
        const host = document.getElementById('modal-container');
        if (!manager?.isConnected || !host?.contains?.(manager)) {
            this.app.showModal(title, content);
            return {
                root: host,
                close: () => this.app.closeModal(),
                nested: false
            };
        }

        const triggerElement = document.activeElement;
        const layer = document.createElement('div');
        layer.className = 'llm-picker-modal-layer agent-rule-modal-layer';
        layer.innerHTML = `
            <div class="modal fade show d-block agent-rule-crud-modal" tabindex="-1"
                 role="dialog" aria-modal="true" aria-labelledby="agent-rule-crud-modal-title">
                <div class="modal-dialog modal-lg modal-dialog-scrollable">
                    <div class="modal-content">
                        <div class="modal-header">
                            <h5 class="modal-title" id="agent-rule-crud-modal-title">${this.app.escapeHtml(title)}</h5>
                            <button type="button" class="btn-close" data-action="close-modal"
                                    aria-label="Close ${this.app.escapeHtml(title)}"></button>
                        </div>
                        <div class="modal-body">${content}</div>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show agent-rule-crud-backdrop"></div>`;

        const underlying = Array.from(host.children).map(element => ({
            element,
            inert: Boolean(element.inert),
            ariaHidden: element.getAttribute('aria-hidden')
        }));
        underlying.forEach(({ element }) => {
            element.inert = true;
            element.setAttribute('aria-hidden', 'true');
        });
        host.appendChild(layer);

        let closed = false;
        let observer = null;
        const restoreUnderlying = () => {
            underlying.forEach(({ element, inert, ariaHidden }) => {
                if (!element.isConnected) return;
                element.inert = inert;
                if (ariaHidden == null) element.removeAttribute('aria-hidden');
                else element.setAttribute('aria-hidden', ariaHidden);
            });
        };
        const close = ({ restoreFocus = true } = {}) => {
            if (closed) return;
            closed = true;
            observer?.disconnect();
            document.removeEventListener('keydown', keydownHandler, true);
            layer.remove();
            restoreUnderlying();
            if (restoreFocus) {
                requestAnimationFrame(() => {
                    if (triggerElement?.isConnected) triggerElement.focus?.({ preventScroll: true });
                });
            }
        };
        const trapFocus = event => {
            const focusable = Array.from(layer.querySelectorAll(
                'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'))
                .filter(element => !element.closest('[inert]'));
            if (focusable.length === 0) return;
            const first = focusable[0];
            const last = focusable[focusable.length - 1];
            if (event.shiftKey && (document.activeElement === first || !layer.contains(document.activeElement))) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && (document.activeElement === last || !layer.contains(document.activeElement))) {
                event.preventDefault();
                first.focus();
            }
        };
        const keydownHandler = event => {
            if (isConfirmDialogOpen()) return;
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                close();
                return;
            }
            if (event.key === 'Tab') trapFocus(event);
        };

        layer.querySelectorAll('[data-action="close-modal"]')
            .forEach(button => button.addEventListener('click', () => close()));
        document.addEventListener('keydown', keydownHandler, true);
        if (typeof MutationObserver === 'function') {
            observer = new MutationObserver(() => {
                if (!layer.isConnected) close({ restoreFocus: false });
            });
            observer.observe(host, { childList: true });
        }
        requestAnimationFrame(() => {
            const initialFocus = layer.querySelector('input:not([disabled]), select:not([disabled]), button:not([disabled])');
            (initialFocus || layer.querySelector('.agent-rule-crud-modal'))?.focus?.();
        });

        return { root: layer, close, nested: true };
    }

    // Paints (or repaints) the RULES page's file list plus its inline editor. A rename
    // or a rule edit must not remount the whole view — repainting these two hosts in
    // place keeps the selection and never disturbs the rest of the page.
    renderAgentFileTree(root) {
        const view = root
            || document.querySelector('[data-view="rule-files"]')
            || document.querySelector('[data-rules-workspace]');

        const fileTree = view?.querySelector('[data-agent-file-tree]');
        if (!fileTree) return;

        this.app.ruleController?.renderRuleInventorySummary?.();

        const agents = this.app.data.agents || [];
        if (agents.length > 0) {
            fileTree.innerHTML = this.app.renderLocalFileTree();
            this.bindAgentListItems(fileTree, {
                onSaved: () => this.renderAgentFileTree(view),
                onSelect: (agent) => {
                    this.selectedAgentPath = agent.path;
                    // Selecting a file only changes selection and the detail pane. Keep
                    // the clicked tree button mounted so keyboard focus is not lost.
                    this.updateAgentFileSelection(fileTree);
                    this.renderInlineRuleEditor(view);
                }
            });
        } else if (this.app.data.isInGit) {
            fileTree.innerHTML = '<p class="rules-files-rail-empty">No vc.rules.md files in this project yet.</p>';
        } else {
            fileTree.innerHTML = '<p class="rules-files-rail-empty">Rule files are only available in local project context.</p>';
        }

        this.renderInlineRuleEditor(view);
    }

    // Wire up the agent-file list rendered by app.renderLocalFileTree(): clicking a row
    // selects it for the inline editor beside the list (falling back to the full-page
    // editor when this list is rendered outside the Rules workspace); the inline rename
    // button opens the custom-name modal without changing the selection.
    bindAgentListItems(container, { onSaved = null, onSelect = null } = {}) {
        container.querySelectorAll('[data-agent-tree-index]').forEach(el => {
            const idx = parseInt(el.dataset.agentTreeIndex);
            const agent = this.app.data.agents[idx];
            if (!agent) return;
            el.addEventListener('click', () => {
                if (onSelect) onSelect(agent);
                else this.app.navigate('agent-edit', agent);
            });
        });

        container.querySelectorAll('[data-agent-rename]').forEach(el => {
            const idx = parseInt(el.dataset.agentRename);
            const agent = this.app.data.agents[idx];
            if (agent) {
                el.addEventListener('click', (e) => {
                    // Don't let the click bubble to the row (which would change selection).
                    e.stopPropagation();
                    this.showAgentCustomNameModal(agent, {
                        onSaved: onSaved || (() => this.loadAgents()),
                        parentRoot: container
                    });
                });
            }
        });
        this.updateAgentFileSelection(container);
    }

    updateAgentFileSelection(container) {
        container?.querySelectorAll('[data-agent-tree-index]').forEach(button => {
            const agent = this.app.data.agents[Number.parseInt(button.dataset.agentTreeIndex, 10)];
            const selected = Boolean(agent && this.selectedAgentPath && agent.path === this.selectedAgentPath);
            button.closest('.agent-file-tree-item')?.classList.toggle('is-selected', selected);
            if (selected) button.setAttribute('aria-current', 'true');
            else button.removeAttribute('aria-current');
        });
    }

    focusRuleManagerControl(root, selectors = []) {
        const manager = root?.matches?.('[data-rule-manager-modal]')
            ? root
            : root?.closest?.('[data-rule-manager-modal]');
        const scope = manager || root;
        if (!scope?.querySelector) return;

        requestAnimationFrame(() => {
            if (!scope.isConnected) return;
            const target = selectors
                .map(selector => scope.querySelector(selector))
                .find(Boolean)
                || scope.querySelector('[data-agent-tree-index][aria-current="true"]')
                || scope.querySelector('[data-rule-manager-refresh], [data-action="refresh-agent-files"]');
            target?.focus?.({ preventScroll: true });
        });
    }

    // ============================================
    // Inline vc.rules.md rule CRUD (Rules workspace)
    // ============================================

    // The detail half of the rule manager. Everything happens in place so the user
    // keeps the selected file and returns to the same Project health summary.
    renderInlineRuleEditor(root) {
        const view = root
            || document.querySelector('[data-view="rule-files"]')
            || document.querySelector('[data-rules-workspace]');
        const host = view?.querySelector('[data-agent-rule-editor]');
        if (!host) return;

        const agents = this.app.data.agents || [];
        const agent = agents.find(candidate => candidate.path === this.selectedAgentPath)
            || agents.find(candidate => (candidate.rules?.length || 0) > 0)
            || agents[0]
            || null;

        if (!agent) {
            host.innerHTML = `
                <div class="rules-files-detail-empty">
                    <span aria-hidden="true"><i class="fa-solid fa-scale-balanced"></i></span>
                    <strong>No rule file selected</strong>
                    <p>Create a vc.rules.md file to start enforcing rules on every commit.</p>
                </div>`;
            return;
        }

        this.selectedAgentPath = agent.path;
        const index = Math.max(agents.findIndex(candidate => candidate.path === agent.path), 0);
        const viewModel = this.app.getAgentFileViewModel(agent, index);
        const rules = agent.rules || [];
        const escape = value => this.app.escapeHtml(value);

        const ruleRows = rules.map((rule, ruleIndex) => {
            const level = ['WARN', 'COMMIT', 'STOP'].includes(String(rule.enforcement || '').toUpperCase())
                ? String(rule.enforcement).toUpperCase()
                : 'WARN';
            const options = ENFORCEMENT_LEVELS.map(option => `
                <option value="${option.value}" ${option.value === level ? 'selected' : ''}>
                    ${option.icon} ${option.value}
                </option>`).join('');
            return `
                <li class="rules-rule-row" data-rule-row="${ruleIndex}" data-level="${level}">
                    <span class="rules-rule-text">${escape(rule.text)}</span>
                    <select class="form-select form-select-sm rules-rule-enforcement"
                        data-rule-enforcement="${ruleIndex}" aria-label="Enforcement for ${escape(rule.text)}">
                        ${options}
                    </select>
                    <button class="btn btn-sm btn-outline-danger rules-icon-btn" type="button"
                        data-rule-remove="${ruleIndex}" title="Remove this rule"
                        aria-label="Remove ${escape(rule.text)}">
                        <i class="fa-solid fa-trash" aria-hidden="true"></i>
                    </button>
                </li>`;
        }).join('');

        host.innerHTML = `
            <header class="rules-files-detail-header">
                <div class="rules-files-detail-title">
                    <h3>${escape(viewModel.displayName)}</h3>
                    <code>${escape(viewModel.relativePath)}</code>
                </div>
                <div class="rules-files-detail-actions">
                    <button class="btn btn-sm btn-outline-secondary rules-icon-btn" type="button"
                        data-rule-editor-rename title="Set this rule file's display name"
                        aria-label="Set this rule file's display name">
                        <i class="fa-solid fa-pen" aria-hidden="true"></i>
                    </button>
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-rule-editor-open
                        title="Open the full editor with the files this rule file covers">
                        Full editor
                    </button>
                    <button class="btn btn-sm btn-primary d-inline-flex align-items-center gap-2" type="button"
                        data-rule-editor-add>
                        <i class="fa-solid fa-plus" aria-hidden="true"></i>Add rule
                    </button>
                </div>
            </header>
            ${rules.length === 0
                ? `<div class="rules-files-detail-empty">
                        <span aria-hidden="true"><i class="fa-regular fa-circle-check"></i></span>
                        <strong>No rules yet</strong>
                        <p>This file exists but enforces nothing. Add a rule to make validation act on it.</p>
                   </div>`
                : `<ul class="rules-rule-list">${ruleRows}</ul>`}
            <p class="rules-files-detail-foot">
                Enforcement: <strong>WARN</strong> reports it · <strong>COMMIT</strong> needs a reason in the
                commit message · <strong>STOP</strong> blocks the commit.
            </p>`;

        host.querySelector('[data-rule-editor-rename]')?.addEventListener('click',
            () => this.showAgentCustomNameModal(agent, {
                onSaved: () => this.renderAgentFileTree(view),
                parentRoot: view
            }));
        host.querySelector('[data-rule-editor-open]')?.addEventListener('click',
            () => this.app.navigate('agent-edit', agent));
        host.querySelector('[data-rule-editor-add]')?.addEventListener('click',
            () => this.showInlineAddRule(agent, view));

        host.querySelectorAll('[data-rule-enforcement]').forEach(select => {
            select.addEventListener('change', async (event) => {
                const rule = rules[Number(event.target.dataset.ruleEnforcement)];
                if (!rule) return;
                await this.updateRuleEnforcement(agent, rule.text, event.target.value, view);
            });
        });

        host.querySelectorAll('[data-rule-remove]').forEach(button => {
            button.addEventListener('click', () => {
                const rule = rules[Number(button.dataset.ruleRemove)];
                if (rule) this.confirmInlineRemoveRule(agent, rule, view);
            });
        });
    }

    async updateRuleEnforcement(agent, ruleText, enforcement, root) {
        try {
            await this.app.apiCall('/api/v1/agents/rules/enforcement', 'PUT', {
                path: agent.path,
                ruleText,
                enforcement
            });
            this.app.showToast('Rule updated', `Enforcement set to ${enforcement}.`, 'success');
            await this.app.refreshDashboardData();
            this.renderAgentFileTree(root);
            const updatedAgent = (this.app.data.agents || []).find(candidate => candidate.path === agent.path);
            const updatedRuleIndex = (updatedAgent?.rules || []).findIndex(rule => rule.text === ruleText);
            if (updatedRuleIndex >= 0) {
                requestAnimationFrame(() => root?.querySelector(
                    `[data-rule-enforcement="${updatedRuleIndex}"]`)?.focus?.({ preventScroll: true }));
            }
        } catch {
            this.app.showError('Failed to update enforcement');
            this.renderAgentFileTree(root);
        }
    }

    showInlineAddRule(agent, root) {
        const available = this.app.data.availableRulesWithDescriptions || [];
        const existing = new Set((agent.rules || []).map(rule => rule.text));
        // Parameterized locks stay available so one vc.rules.md can protect multiple paths.
        const unused = available.filter(rule =>
            this.isPathLockTemplate(rule.name) || !existing.has(rule.name));

        if (unused.length === 0) {
            this.app.showToast('Add rule', 'Every available rule is already in this file.', 'info');
            return;
        }

        const options = unused.map(rule => `
            <label class="rules-add-rule-option">
                <input type="radio" name="inline-rule-pick" value="${this.app.escapeHtml(rule.name)}">
                <span>
                    <strong>${this.app.escapeHtml(rule.name)}</strong>
                    <small>${this.app.escapeHtml(rule.description)}</small>
                </span>
            </label>`).join('');

        const levels = ENFORCEMENT_LEVELS.map((option, index) => `
            <label class="rules-add-rule-level">
                <input type="radio" name="inline-rule-level" value="${option.value}" ${index === 0 ? 'checked' : ''}>
                <span><strong>${option.icon} ${option.value}</strong><small>${option.blurb}</small></span>
            </label>`).join('');

        const modal = this.openRuleCrudModal('Add a rule', `
            <form id="inline-add-rule-form" class="rules-add-rule">
                <p class="text-muted mb-2">Adding to <code>${this.app.escapeHtml(agent.path)}</code></p>
                <div class="rules-add-rule-list">${options}</div>
                <div class="mt-3 d-none" data-inline-path-lock-fields>
                    <label class="form-label" for="inline-path-lock-path" data-inline-path-lock-label>Path</label>
                    <input class="form-control" id="inline-path-lock-path" name="inline-path-lock-path"
                        type="text" autocomplete="off">
                    <small class="form-text text-muted">
                        Relative to this vc.rules.md directory. Directory locks include every descendant.
                    </small>
                </div>
                <div class="form-label mt-3 mb-2">Enforcement</div>
                <div class="rules-add-rule-levels">${levels}</div>
                <div class="d-flex justify-content-end gap-2 mt-4">
                    <button type="button" class="btn btn-outline-secondary" data-action="close-modal">Cancel</button>
                    <button type="submit" class="btn btn-primary">Add rule</button>
                </div>
            </form>`, { parentRoot: root });

        const inlineForm = modal.root?.querySelector?.('#inline-add-rule-form') || null;
        const syncPathLockFields = () => {
            const selectedTemplate = inlineForm?.querySelector('input[name="inline-rule-pick"]:checked')?.value;
            const definition = PATH_LOCK_RULES.find(item => item.template === selectedTemplate);
            const fields = inlineForm?.querySelector('[data-inline-path-lock-fields]');
            fields?.classList.toggle('d-none', !definition);
            const label = inlineForm?.querySelector('[data-inline-path-lock-label]');
            const input = inlineForm?.querySelector('input[name="inline-path-lock-path"]');
            if (label && definition) label.textContent = definition.label;
            if (input) {
                input.placeholder = definition?.placeholder || '';
                input.required = Boolean(definition);
            }
        };
        inlineForm?.querySelectorAll('input[name="inline-rule-pick"]').forEach(input =>
            input.addEventListener('change', syncPathLockFields));

        inlineForm?.addEventListener('submit', async (event) => {
            event.preventDefault();
            const form = event.currentTarget;
            const ruleTemplate = form.querySelector('input[name="inline-rule-pick"]:checked')?.value;
            const enforcement = form.querySelector('input[name="inline-rule-level"]:checked')?.value || 'WARN';
            if (!ruleTemplate) {
                this.app.showToast('Add rule', 'Pick a rule first.', 'warning');
                return;
            }
            let ruleText;
            try {
                ruleText = this.buildPathLockRuleText(
                    ruleTemplate,
                    form.querySelector('input[name="inline-path-lock-path"]')?.value);
            } catch (error) {
                this.app.showToast('Path lock', error.message, 'warning');
                return;
            }
            try {
                await this.app.apiCall('/api/v1/agents/rules', 'POST', {
                    path: agent.path,
                    ruleText,
                    enforcement
                });
                modal.close();
                this.app.showToast('Rule added', `${ruleText} is now enforced at ${enforcement}.`, 'success');
                await this.app.refreshDashboardData();
                this.renderAgentFileTree(root);
                this.focusRuleManagerControl(root, ['[data-rule-editor-add]']);
            } catch {
                this.app.showError('Failed to add rule');
            }
        });
    }

    confirmInlineRemoveRule(agent, rule, root) {
        const modal = this.openRuleCrudModal('Remove rule', `
            <p>Remove <strong>"${this.app.escapeHtml(rule.text)}"</strong> from
                <code>${this.app.escapeHtml(agent.path)}</code>?</p>
            <p class="text-muted small">You can add it back at any time.</p>
            <div class="d-flex gap-2 justify-content-end mt-4">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-danger" id="inline-remove-rule-confirm">Remove rule</button>
            </div>`, { parentRoot: root });

        modal.root?.querySelector?.('#inline-remove-rule-confirm')?.addEventListener('click', async () => {
            modal.close();
            try {
                await this.app.apiCall('/api/v1/agents/rules', 'DELETE', {
                    path: agent.path,
                    rules: [rule.text]
                });
                this.app.showToast('Rule removed', `${rule.text} no longer applies here.`, 'success');
                await this.app.refreshDashboardData();
                this.renderAgentFileTree(root);
                this.focusRuleManagerControl(root, ['[data-rule-editor-add]']);
            } catch {
                this.app.showError('Failed to remove rule');
            }
        });
    }

    showAvailableRules() {
        // Use API-fetched rules with descriptions if available
        const rulesWithDescriptions = this.app.data.availableRulesWithDescriptions || [];

        const rules = rulesWithDescriptions.map(rule => `
            <div class="list-group-item">
                <div class="mb-1">
                    <strong>${this.app.escapeHtml(rule.name)}</strong>
                </div>
                <small class="text-muted">${this.app.escapeHtml(rule.description)}</small>
            </div>
        `).join('');

        this.app.showModal('Available VCA Rules', `
            <div class="list-group">
                ${rules || '<p class="text-muted text-center">No rules available</p>'}
            </div>
        `);
    }

    // ============================================
    // Agent Edit View
    // ============================================

    loadAgentEdit(agent) {
        this.currentAgent = agent;
        this.selectedRuleIndex = null;

        const content = document.getElementById('app-content');
        if (!content) {
            return;
        }

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('agent-edit-template');
        const root = fragment.querySelector('[data-view="agent-edit"]');

        if (root) {
            const displayName = root.querySelector('[data-agent-display-name]');
            if (displayName) {
                const agentIndex = this.app.data.agents.findIndex(candidate => candidate.path === agent.path);
                displayName.textContent = this.app.getAgentFileViewModel(agent, Math.max(agentIndex, 0)).displayName;
            }

            const path = root.querySelector('[data-agent-path]');
            if (path) {
                path.textContent = agent.path;
            }

            const rules = root.querySelector('[data-agent-rules]');
            if (rules) {
                rules.innerHTML = this.renderAgentRules(agent);
                // Bind rule selection click handlers (CSP-safe, no inline onclick)
                rules.querySelectorAll('[data-rule-select]').forEach(el => {
                    const index = parseInt(el.dataset.ruleSelect);
                    el.addEventListener('click', () => this.selectRule(el, index));
                });
            }

            const fullContent = root.querySelector('[data-agent-full-content]');
            const contentToggle = root.querySelector('[data-agent-content-toggle]');
            const contentContainer = root.querySelector('[data-agent-content-container]');

            if (fullContent && contentToggle && contentContainer) {
                let isLoaded = false;

                contentToggle.addEventListener('click', () => {
                    const isHidden = contentContainer.style.display === 'none';
                    contentContainer.style.display = isHidden ? 'block' : 'none';

                    const icon = contentToggle.querySelector('.toggle-icon');
                    if (icon) {
                        icon.classList.toggle('rotated', isHidden);
                    }

                    if (isHidden && !isLoaded) {
                        this.loadAgentFileContent(agent.path, fullContent);
                        isLoaded = true;
                    }
                });
            }

            // Files affected - load immediately (always visible)
            const filesList = root.querySelector('[data-agent-files-list]');
            if (filesList) {
                this.loadAgentFiles(agent.path, filesList, root.querySelector('[data-agent-file-count]'));
            }

            const actions = {
                'add-rule': () => this.addRule(agent),
                'edit-rule': () => this.editRule(),
                'remove-rule': () => this.removeRule(),
                'validate-agent': () => this.validateAgent(agent),
                'edit-vscode': () => this.editInVSCode(agent),
                'show-available-rules': () => this.showAvailableRules()
            };

            root.querySelectorAll('[data-agent-action]').forEach((element) => {
                const action = element.dataset.agentAction;
                const handler = actions[action];
                if (handler) {
                    element.addEventListener('click', handler);
                }

                // Dim edit/remove until a rule is selected. We deliberately keep
                // them clickable so the handler's "select a rule first" toast can
                // fire — a fully inert card just reads as a broken/dead button.
                if (action === 'edit-rule' || action === 'remove-rule') {
                    element.closest('.card').classList.add('disabled-card');
                    element.style.opacity = '0.5';
                }

                // Hide "Edit in VS Code" button when running inside VS Code
                if (action === 'edit-vscode' && window.__viberails_VSCODE__) {
                    element.closest('.col-md-4')?.remove();
                }
            });

            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            this.app.bindAction(root, '[data-action="set-custom-agent-name"]', () => this.showAgentCustomNameModal(agent));
        }

        content.appendChild(fragment);
    }

    renderAgentRules(agent) {
        if (!agent.rules || agent.rules.length === 0) {
            return '<div class="alert alert-secondary border-0"><i class="me-2">ℹ️</i>No rules configured for this rule file.</div>';
        }

        const getEnforcementBadge = (level) => {
            const normalizedLevel = ['WARN', 'COMMIT', 'STOP'].includes(String(level || '').toUpperCase())
                ? String(level).toUpperCase()
                : '';
            const className = normalizedLevel ? `badge-${normalizedLevel.toLowerCase()}` : 'bg-secondary';
            const label = normalizedLevel || String(level || 'Unknown');
            return `<span class="badge ${className} px-3 py-2">${this.app.escapeHtml(label)}</span>`;
        };

        return `
            <div class="d-flex flex-column gap-3 mb-4">
                ${agent.rules.map((rule, index) => `
                    <div class="card rule-card border-0 shadow-sm" data-rule-select="${index}" data-rule-index="${index}" style="cursor:pointer;">
                        <div class="card-body p-3 d-flex justify-content-between align-items-center">
                            <div class="pe-3 d-flex align-items-center flex-grow-1">
                                <span class="rule-icon me-3 text-muted">📜</span>
                                <span class="fw-medium text-light" style="font-size: 1.05rem;">${this.app.escapeHtml(rule.text)}</span>
                            </div>
                            <div class="d-flex align-items-center gap-3">
                                <button class="btn btn-sm btn-primary d-flex align-items-center gap-2">
                                    <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" fill="currentColor" viewBox="0 0 16 16">
                                        <path d="M12.736 3.97a.733.733 0 0 1 1.047 0c.286.289.29.756.01 1.05L7.88 12.01a.733.733 0 0 1-1.065.02L3.217 8.384a.757.757 0 0 1 0-1.06.733.733 0 0 1 1.047 0l3.052 3.093 5.42-6.447z"/>
                                    </svg>
                                    Select
                                </button>
                                <div class="flex-shrink-0">
                                    ${getEnforcementBadge(rule.enforcement)}
                                </div>
                            </div>
                        </div>
                    </div>
                `).join('')}
            </div>
        `;
    }

    async loadAgentFileContent(path, element) {
        try {
            element.textContent = 'Loading...';
            const response = await this.app.apiCall(`/api/v1/agents/content?path=${encodeURIComponent(path)}`, 'GET');
            if (response && response.content) {
                element.textContent = response.content;
            } else {
                element.textContent = 'No content available';
            }
        } catch (error) {
            console.error('Failed to load rule file content:', error);
            element.textContent = `Error loading file content: ${error.message || 'Unknown error'}`;
        }
    }

    async loadAgentFiles(path, listElement, countBadge) {
        try {
            const response = await this.app.apiCall(`/api/v1/agents/files?path=${encodeURIComponent(path)}`, 'GET');
            if (response && response.files && response.files.length > 0) {
                const totalCount = response.totalCount || response.files.length;
                if (countBadge) {
                    countBadge.textContent = `${totalCount} files`;
                }

                // Build summary of top-level directories with file counts
                const summary = this.buildDirectorySummary(response.files);
                const summaryHtml = this.renderDirectorySummary(summary, totalCount);

                listElement.innerHTML = summaryHtml;
            } else {
                if (countBadge) {
                    countBadge.textContent = '0 files';
                }
                listElement.innerHTML = '<p class="text-muted mb-0">No files found in this rule file\'s scope.</p>';
            }
        } catch (error) {
            console.error('Failed to load rule-file scope:', error);
            const errorMsg = this.app.escapeHtml(error.message || 'Unknown error');
            listElement.innerHTML = `<p class="text-danger mb-0">Error loading files: ${errorMsg}</p>`;
        }
    }

    buildDirectorySummary(files) {
        const summary = { dirs: {}, rootFiles: [] };

        files.forEach(filePath => {
            const parts = filePath.split(/[\\/]/);

            // Skip hidden files/directories (starting with .)
            if (parts[0].startsWith('.')) return;

            if (parts.length === 1) {
                // Root level file
                summary.rootFiles.push(parts[0]);
            } else {
                // File in a directory
                const topDir = parts[0];
                if (!summary.dirs[topDir]) {
                    summary.dirs[topDir] = { count: 0, subdirs: new Set() };
                }
                summary.dirs[topDir].count++;
                if (parts.length > 2) {
                    summary.dirs[topDir].subdirs.add(parts[1]);
                }
            }
        });

        return summary;
    }

    renderDirectorySummary(summary, totalCount) {
        const maxItems = 12;
        let html = '<div class="file-summary-grid">';
        let itemCount = 0;

        // Sort directories by file count (descending)
        const sortedDirs = Object.entries(summary.dirs)
            .sort((a, b) => b[1].count - a[1].count);

        // Render directories
        for (const [dirName, data] of sortedDirs) {
            if (itemCount >= maxItems) break;

            const subdirCount = data.subdirs.size;
            const subdirText = subdirCount > 0 ? `${subdirCount} subdirs` : '';

            html += `
                <div class="file-summary-item dir-item">
                    <div class="file-summary-icon dir-icon"></div>
                    <div class="file-summary-info">
                        <span class="file-summary-name">${this.app.escapeHtml(dirName)}</span>
                        <span class="file-summary-meta">${data.count} files${subdirText ? ' · ' + subdirText : ''}</span>
                    </div>
                </div>
            `;
            itemCount++;
        }

        // Render root files (if any and space remaining)
        for (const fileName of summary.rootFiles) {
            if (itemCount >= maxItems) break;
            const fileType = getFileTypeVisual(fileName);

            html += `
                <div class="file-summary-item file-item">
                    <div class="file-summary-icon file-icon" title="${this.app.escapeHtml(fileType.name)}">
                        <img src="${this.app.escapeHtml(fileType.iconPath)}" alt="${this.app.escapeHtml(fileType.name)} icon" loading="lazy">
                    </div>
                    <div class="file-summary-info">
                        <span class="file-summary-name">${this.app.escapeHtml(fileName)}</span>
                        <span class="file-summary-meta">${this.app.escapeHtml(fileType.name)}</span>
                    </div>
                </div>
            `;
            itemCount++;
        }

        html += '</div>';

        // Add overflow indicator if needed
        const totalItems = Object.keys(summary.dirs).length + summary.rootFiles.length;
        if (totalItems > maxItems) {
            html += `<div class="file-summary-overflow">+ ${totalItems - maxItems} more directories/files</div>`;
        }

        return html;
    }

    async validateAgent(agent) {
        const root = document.querySelector('[data-view="agent-edit"]');
        if (!root) return;

        const section = root.querySelector('[data-agent-validation-section]');
        const resultsContainer = root.querySelector('[data-agent-validation-results]');
        if (!section || !resultsContainer) return;

        section.style.display = 'block';
        resultsContainer.innerHTML = '<div class="text-center"><div class="spinner-border text-primary"></div><p class="mt-2">Running validation...</p></div>';

        try {
            const response = await this.app.apiCall(
                `/api/v1/agents/validate?path=${encodeURIComponent(agent.path)}`, 'POST'
            );

            resultsContainer.innerHTML = this.renderValidationResults(response);

            if (response.passed) {
                this.app.showToast('Validation Passed', response.message, 'success');
            } else {
                this.app.showToast('Validation Failed', response.message, 'error');
            }
        } catch (error) {
            console.error('Failed to validate rule file:', error);
            const errorMsg = this.app.escapeHtml(error.message || 'Unknown error');
            resultsContainer.innerHTML = `<p class="text-danger">Error: ${errorMsg}</p>`;
            this.app.showError('Validation failed');
        }
    }

    renderValidationResults(response = {}) {
        const passed = response?.passed === true;
        const message = this.app.escapeHtml(response?.message || (passed ? 'Validation passed' : 'Validation failed'));
        const results = Array.isArray(response?.results) ? response.results : [];
        const summaryTone = passed ? 'success' : 'danger';
        const resultCards = results.map(result => {
            const enforcement = String(result?.enforcement || 'Unknown').toUpperCase();
            const enforcementTone = {
                WARN: 'warning',
                COMMIT: 'info',
                STOP: 'danger'
            }[enforcement] || 'secondary';
            const affectedFiles = Array.isArray(result?.affectedFiles) ? result.affectedFiles : [];
            const filesHtml = affectedFiles.length === 0
                ? ''
                : `<ul class="mb-0 mt-2">${affectedFiles.map(file => `<li><code>${this.app.escapeHtml(file)}</code></li>`).join('')}</ul>`;

            return `
                <div class="border rounded p-3 mb-2">
                    <div class="d-flex justify-content-between align-items-start gap-3">
                        <strong>${this.app.escapeHtml(result?.ruleName || 'Rule')}</strong>
                        <span class="badge bg-${enforcementTone}">${this.app.escapeHtml(enforcement)}</span>
                    </div>
                    <p class="mb-0 mt-2 ${result?.passed === true ? 'text-success' : 'text-danger'}">${this.app.escapeHtml(result?.message || (result?.passed === true ? 'Passed' : 'Failed'))}</p>
                    ${filesHtml}
                </div>`;
        }).join('');

        return `
            <div class="alert alert-${summaryTone}" role="status">${message}</div>
            ${resultCards}`;
    }

    selectRule(element, index) {
        // Remove active class from all items
        const container = element.parentElement;
        container.querySelectorAll('.rule-card').forEach(item => {
            item.classList.remove('active');
        });

        // Add active class to clicked item
        element.classList.add('active');
        this.selectedRuleIndex = index;

        // Light up the edit/remove cards now that a rule is selected.
        const root = document.querySelector('[data-view="agent-edit"]');
        if (root) {
            root.querySelectorAll('[data-agent-action="edit-rule"], [data-agent-action="remove-rule"]').forEach(btn => {
                const card = btn.closest('.card');
                if (card) {
                    card.classList.remove('disabled-card');
                    card.style.opacity = '1';
                }
            });
        }
    }

    async addRule(agent) {
        // Get available rules with descriptions from API data
        const rulesWithDescriptions = this.app.data.availableRulesWithDescriptions || [];

        // Filter out rules already in use
        const existingRuleTexts = agent.rules.map(r => r.text);
        const unusedRulesWithDescriptions = rulesWithDescriptions.filter(r =>
            this.isPathLockTemplate(r.name) || !existingRuleTexts.includes(r.name));

        if (unusedRulesWithDescriptions.length === 0) {
            this.app.showToast('Add Rule', 'All available rules are already added', 'info');
            return;
        }

        const ruleOptions = unusedRulesWithDescriptions.map(rule => `
            <div class="list-group-item list-group-item-action" data-rule="${this.app.escapeHtml(rule.name)}" style="cursor: pointer;">
                <div class="mb-1"><strong>${this.app.escapeHtml(rule.name)}</strong></div>
                <small class="text-muted">${this.app.escapeHtml(rule.description)}</small>
            </div>
        `).join('');

        this.app.showModal('Add Rule', `
            <p class="text-muted mb-3">Select a rule to add to this rule file:</p>
            <div class="list-group">
                ${ruleOptions}
            </div>
        `);

        // Bind click handlers - show enforcement picker after selecting rule
        document.querySelectorAll('[data-rule]').forEach(el => {
            el.addEventListener('click', () => {
                const ruleText = el.dataset.rule;
                if (this.isPathLockTemplate(ruleText)) {
                    this.showPathLockPathPicker(agent, ruleText);
                } else {
                    this.showEnforcementPicker(agent, ruleText);
                }
            });
        });
    }

    showEnforcementPicker(agent, ruleText, isEdit = false) {
        const currentEnforcement = isEdit ? agent.rules.find(r => r.text === ruleText)?.enforcement : null;

        this.app.showModal('Select Enforcement Level', `
            <p class="text-muted mb-3">How should this rule be enforced?</p>
            <p class="mb-4"><strong>${this.app.escapeHtml(ruleText)}</strong></p>
            <div class="d-flex flex-column gap-3">
                <div class="card enforcement-option ${currentEnforcement === 'WARN' ? 'border-warning' : ''}" data-enforcement="WARN" style="cursor: pointer;">
                    <div class="card-body d-flex align-items-center gap-3">
                        <span class="fs-3">⚠️</span>
                        <div>
                            <h6 class="mb-1">WARN</h6>
                            <small class="text-muted">Warn the user about the violation but allow the action to proceed.</small>
                        </div>
                    </div>
                </div>
                <div class="card enforcement-option ${currentEnforcement === 'COMMIT' ? 'border-info' : ''}" data-enforcement="COMMIT" style="cursor: pointer;">
                    <div class="card-body d-flex align-items-center gap-3">
                        <span class="fs-3">💬</span>
                        <div>
                            <h6 class="mb-1">COMMIT</h6>
                            <small class="text-muted">Require an explanation in the commit or PR message about why the rule was broken.</small>
                        </div>
                    </div>
                </div>
                <div class="card enforcement-option ${currentEnforcement === 'STOP' ? 'border-danger' : ''}" data-enforcement="STOP" style="cursor: pointer;">
                    <div class="card-body d-flex align-items-center gap-3">
                        <span class="fs-3">🛑</span>
                        <div>
                            <h6 class="mb-1">STOP</h6>
                            <small class="text-muted">Block the commit or PR entirely until the violation is fixed.</small>
                        </div>
                    </div>
                </div>
            </div>
        `);

        // Bind click handlers for enforcement options
        document.querySelectorAll('[data-enforcement]').forEach(el => {
            el.addEventListener('click', async () => {
                const enforcement = el.dataset.enforcement;
                try {
                    if (isEdit) {
                        // Update existing rule's enforcement
                        await this.app.apiCall('/api/v1/agents/rules/enforcement', 'PUT', {
                            path: agent.path,
                            ruleText: ruleText,
                            enforcement: enforcement
                        });
                    } else {
                        // Add new rule with enforcement
                        await this.app.apiCall('/api/v1/agents/rules', 'POST', {
                            path: agent.path,
                            ruleText: ruleText,
                            enforcement: enforcement
                        });
                    }
                    this.app.closeModal();
                    this.app.showToast('Success', isEdit ? 'Enforcement level updated' : 'Rule added successfully', 'success');
                    await this.app.refreshDashboardData();
                    // Reload the agent edit view with updated data
                    const updatedAgent = this.app.data.agents.find(a => a.path === agent.path);
                    if (updatedAgent) {
                        this.loadAgentEdit(updatedAgent);
                    }
                } catch (error) {
                    this.app.showError(isEdit ? 'Failed to update enforcement' : 'Failed to add rule');
                }
            });
        });
    }

    editRule() {
        if (this.selectedRuleIndex === null) {
            this.app.showToast('Edit Rule', 'Please select a rule first', 'warning');
            return;
        }
        const rule = this.currentAgent.rules[this.selectedRuleIndex];
        // Show enforcement picker in edit mode
        this.showEnforcementPicker(this.currentAgent, rule.text, true);
    }

    async removeRule() {
        if (this.selectedRuleIndex === null) {
            this.app.showToast('Remove Rule', 'Please select a rule first', 'warning');
            return;
        }

        const rule = this.currentAgent.rules[this.selectedRuleIndex];

        this.app.showModal('Remove Rule', `
            <div class="text-center py-3">
                <div class="mb-3 text-danger">
                    <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M11 1.5v1h3.5a.5.5 0 0 1 0 1h-.538l-.853 10.66A2 2 0 0 1 11.115 16h-6.23a2 2 0 0 1-1.994-1.84L2.038 3.5H1.5a.5.5 0 0 1 0-1H5v-1A1.5 1.5 0 0 1 6.5 0h3A1.5 1.5 0 0 1 11 1.5m-5 0v1h4v-1a.5.5 0 0 0-.5-.5h-3a.5.5 0 0 0-.5.5M4.5 5.029l.5 8.5a.5.5 0 1 0 .998-.06l-.5-8.5a.5.5 0 1 0-.998.06m6.53-.06a.5.5 0 0 0-.515.479l-.5 8.5a.5.5 0 1 0 .998.06l.5-8.5a.5.5 0 0 0-.484-.539M8 5.5a.5.5 0 0 0-.5.5v8.5a.5.5 0 0 0 1 0V6a.5.5 0 0 0-.5-.5"/>
                    </svg>
                </div>
                <h5>Remove rule from rule file?</h5>
                <p>Are you sure you want to remove <strong>"${this.app.escapeHtml(rule.text)}"</strong>?</p>
                <p class="text-muted small px-4">This rule will be removed from the rule file. You can add it back later if needed.</p>
            </div>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-danger" id="confirm-delete-rule-btn">Remove Rule</button>
            </div>
        `);

        document.getElementById('confirm-delete-rule-btn').onclick = async () => {
            this.app.closeModal();
            try {
                await this.app.apiCall('/api/v1/agents/rules', 'DELETE', {
                    path: this.currentAgent.path,
                    rules: [rule.text]
                });
                this.app.showToast('Success', 'Rule removed', 'success');
                await this.app.refreshDashboardData();
                // Reload the agent edit view with updated data
                const updatedAgent = this.app.data.agents.find(a => a.path === this.currentAgent.path);
                if (updatedAgent) {
                    this.loadAgentEdit(updatedAgent);
                } else {
                    this.app.goBack();
                }
            } catch (error) {
                this.app.showError('Failed to remove rule');
            }
        };
    }

    async editInVSCode(agent) {
        try {
            await this.app.apiCall('/api/v1/cli/launch/vscode', 'POST', { path: agent.path });
            this.app.showToast('VS Code', `Opened ${agent.name} in VS Code`, 'success');
        } catch (error) {
            this.app.showError(`Failed to open ${agent.name} in VS Code`);
        }
    }

    showAgentCustomNameModal(agent, { onSaved = null, parentRoot = null } = {}) {
        const currentName = agent.customName || agent.name;
        const openedFromTree = parentRoot?.matches?.('[data-agent-file-tree]') === true;

        const modal = this.openRuleCrudModal('Set Rule File Display Name', `
            <form id="agent-custom-name-form">
                <div class="mb-3">
                    <label class="form-label">Custom Name</label>
                    <input type="text" class="form-control" id="agent-custom-name" value="${this.app.escapeHtml(currentName)}" placeholder="${this.app.escapeHtml(currentName)}" required>
                    <small class="form-text text-muted">Enter a friendly display name for this rule file.</small>
                </div>
                <div class="d-flex gap-2 justify-content-end">
                    <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                    <button type="submit" class="btn btn-primary d-flex align-items-center gap-2">
                        <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                            <path d="M11 2H9v3h2z"/>
                            <path d="M1.5 0h11.586a1.5 1.5 0 0 1 1.06.44l1.415 1.414A1.5 1.5 0 0 1 16 2.914V14.5a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 0 14.5v-13A1.5 1.5 0 0 1 1.5 0M1 1.5v13a.5.5 0 0 0 .5.5H2v-4.5A1.5 1.5 0 0 1 3.5 9h9a1.5 1.5 0 0 1 1.5 1.5V15h.5a.5.5 0 0 0 .5-.5V2.914a.5.5 0 0 0-.146-.353l-1.415-1.415A.5.5 0 0 0 13.086 1H13v4.5A1.5 1.5 0 0 1 11.5 7h-7A1.5 1.5 0 0 1 3 5.5V1H1.5a.5.5 0 0 0-.5.5m3 4a.5.5 0 0 0 .5.5h7a.5.5 0 0 0 .5-.5V1H4zM3 15h10v-4.5a.5.5 0 0 0-.5-.5h-9a.5.5 0 0 0-.5.5z"/>
                        </svg>
                        Save Custom Name
                    </button>
                </div>
            </form>
        `, { parentRoot });

        modal.root?.querySelector?.('#agent-custom-name-form')?.addEventListener('submit', async (e) => {
            e.preventDefault();
            const newName = modal.root.querySelector('#agent-custom-name').value;
            try {
                await this.app.apiCall('/api/v1/agents/name', 'PUT', {
                    path: agent.path,
                    customName: newName
                });
                this.app.showToast('Success', `Rule file name updated to "${newName}"`, 'success');
                modal.close();
                // Refresh data, then let the caller decide how to re-render. The
                // list passes onSaved to stay on the list; the editor view falls
                // back to reloading itself with the updated agent.
                await this.app.refreshDashboardData();
                if (onSaved) {
                    onSaved();
                    const updatedIndex = this.app.data.agents.findIndex(candidate => candidate.path === agent.path);
                    const selectors = openedFromTree && updatedIndex >= 0
                        ? [`[data-agent-rename="${updatedIndex}"]`, '[data-rule-editor-rename]']
                        : ['[data-rule-editor-rename]'];
                    this.focusRuleManagerControl(parentRoot, selectors);
                } else {
                    const updatedAgent = this.app.data.agents.find(a => a.path === agent.path);
                    if (updatedAgent) {
                        this.loadAgentEdit(updatedAgent);
                    }
                }
            } catch (error) {
                this.app.showError('Failed to update rule file name');
            }
        });
    }

    // ============================================
    // Agent Create Wizard
    // ============================================

    resetWizardState() {
        this.wizardState = {
            currentStep: 1,
            totalSteps: 4,
            directory: '',
            selectedRules: [],
            fileReferences: []
        };
    }

    loadAgentCreate() {
        this.resetWizardState();

        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('agent-create-template');
        const root = fragment.querySelector('[data-view="agent-create"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
        }

        content.appendChild(fragment);
        this.renderWizardStep();
    }

    renderWizardStep() {
        const wizardContent = document.getElementById('wizard-content');
        if (!wizardContent) return;

        // Update step indicators
        this.updateStepIndicators();

        switch (this.wizardState.currentStep) {
            case 1:
                this.renderStep1Directory(wizardContent);
                break;
            case 2:
                this.renderStep2Rules(wizardContent);
                break;
            case 3:
                this.renderStep3Enforcement(wizardContent);
                break;
            case 4:
                this.renderStep4Review(wizardContent);
                break;
        }
    }

    updateStepIndicators() {
        const steps = document.querySelectorAll('.wizard-step');
        steps.forEach((step, index) => {
            const stepNum = index + 1;
            step.classList.remove('active', 'completed');
            if (stepNum < this.wizardState.currentStep) {
                step.classList.add('completed');
            } else if (stepNum === this.wizardState.currentStep) {
                step.classList.add('active');
            }
        });
    }

    renderStep1Directory(container) {
        const rootPath = this.app.data.configs?.rootPath || '';
        const defaultPath = rootPath || '';
        const stripAgentFileName = path => String(path || '').replace(/[\\/]AGENTS\.md$/i, '');
        const directoryValue = stripAgentFileName(this.wizardState.directory || defaultPath);

        // Escape user data to prevent XSS
        const escapedDirectory = this.app.escapeHtml(directoryValue);
        const escapedRootPath = this.app.escapeHtml(rootPath);

        container.innerHTML = `
            <h5 class="mb-4">Step 1: Select Directory</h5>
            <div class="mb-4">
                <label class="form-label" for="agent-directory">Directory for vc.rules.md</label>
                <div class="input-group">
                    <input type="text" class="form-control" id="agent-directory"
                           placeholder="/path/to/directory"
                           value="${escapedDirectory}">
                    <button type="button" class="btn btn-outline-primary d-inline-flex align-items-center gap-2"
                            data-action="pick-agent-directory">
                        <i class="fa-regular fa-folder-open" aria-hidden="true"></i>
                        Browse
                    </button>
                </div>
                <small class="form-text text-muted">
                    Enter the directory where <code>vc.rules.md</code> will be created.
                </small>
            </div>
            ${rootPath ? `
                <div class="alert alert-info">
                    <strong>Current Project:</strong> ${escapedRootPath}
                </div>
            ` : ''}
            <div class="d-flex justify-content-between mt-4">
                <button class="btn btn-outline-secondary" type="button" data-action="go-back">Cancel</button>
                <button class="btn btn-primary d-flex align-items-center gap-2" type="button" id="wizard-next-btn">
                    Next
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M1 8a.5.5 0 0 1 .5-.5h11.793l-3.147-3.146a.5.5 0 0 1 .708-.708l4 4a.5.5 0 0 1 0 .708l-4 4a.5.5 0 0 1-.708-.708L13.293 8.5H1.5A.5.5 0 0 1 1 8"/>
                    </svg>
                </button>
            </div>
        `;

        const directoryInput = container.querySelector('#agent-directory');
        const browseButton = container.querySelector('[data-action="pick-agent-directory"]');
        browseButton?.addEventListener('click', async () => {
            const enteredPath = directoryInput?.value || '';
            const requestedPath = enteredPath.trim() ? stripAgentFileName(enteredPath) : rootPath;
            const result = await this.app.pickFileSystemEntry({
                mode: 'directory',
                initialPath: requestedPath,
                title: 'Choose vc.rules.md Directory',
                triggerElement: browseButton
            });
            if (result.canceled || !directoryInput?.isConnected) return;

            directoryInput.value = result.path;
            directoryInput.focus();
            directoryInput.setSelectionRange?.(directoryInput.value.length, directoryInput.value.length);
        });

        document.getElementById('wizard-next-btn').addEventListener('click', () => {
            const agentPath = buildAgentFilePath(document.getElementById('agent-directory').value);
            if (!agentPath) {
                this.app.showToast('Validation Error', 'Please enter a directory path', 'warning');
                return;
            }
            this.wizardState.directory = agentPath;
            this.wizardState.currentStep = 2;
            this.renderWizardStep();
        });
    }

    renderStep2Rules(container) {
        // Use rules with descriptions if available
        const rulesWithDescriptions = this.app.data.availableRulesWithDescriptions || [];

        const ruleCheckboxes = rulesWithDescriptions.map((rule, index) => {
            const lockDefinition = PATH_LOCK_RULES.find(item => item.template === rule.name);
            const selectedRule = this.wizardState.selectedRules.find(selected =>
                selected.text === rule.name
                || (lockDefinition && this.getPathLockDefinition(selected.text)?.kind === lockDefinition.kind));
            const isChecked = Boolean(selectedRule);
            const lockPath = lockDefinition ? this.extractPathLockPath(selectedRule?.text) : '';
            return `
                <div class="form-check mb-3 pb-2 border-bottom">
                    <input class="form-check-input" type="checkbox" id="rule-${index}"
                           data-rule="${this.app.escapeHtml(rule.name)}" ${isChecked ? 'checked' : ''}>
                    <label class="form-check-label" for="rule-${index}">
                        <strong>${this.app.escapeHtml(rule.name)}</strong>
                        <br><small class="text-muted">${this.app.escapeHtml(rule.description)}</small>
                    </label>
                    ${lockDefinition ? `
                        <div class="mt-2 ms-4">
                            <label class="form-label small" for="path-lock-${index}">${lockDefinition.label}</label>
                            <input class="form-control form-control-sm" id="path-lock-${index}"
                                data-path-lock-index="${index}" type="text"
                                value="${this.app.escapeHtml(lockPath)}"
                                placeholder="${lockDefinition.placeholder}" autocomplete="off">
                            <small class="form-text text-muted">Relative to this vc.rules.md directory.</small>
                        </div>` : ''}
                </div>
            `;
        }).join('');

        container.innerHTML = `
            <h5 class="mb-4">Step 2: Choose Rules</h5>
            <p class="text-muted mb-3">Select the rules you want to include in this rule file:</p>
            <div class="card mb-4">
                <div class="card-body" style="max-height: 400px; overflow-y: auto;">
                    ${ruleCheckboxes || '<p class="text-muted">No rules available</p>'}
                </div>
            </div>
            <div class="d-flex justify-content-between">
                <button class="btn btn-outline-secondary d-flex align-items-center gap-2" type="button" id="wizard-prev-btn">
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M15 8a.5.5 0 0 0-.5-.5H2.707l3.147-3.146a.5.5 0 1 0-.708-.708l-4 4a.5.5 0 0 0 0 .708l4 4a.5.5 0 0 0 .708-.708L2.707 8.5H14.5A.5.5 0 0 0 15 8"/>
                    </svg>
                    Back
                </button>
                <button class="btn btn-primary d-flex align-items-center gap-2" type="button" id="wizard-next-btn">
                    Next ${this.wizardState.selectedRules.length > 0 ? `(${this.wizardState.selectedRules.length} selected)` : ''}
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M1 8a.5.5 0 0 1 .5-.5h11.793l-3.147-3.146a.5.5 0 0 1 .708-.708l4 4a.5.5 0 0 1 0 .708l-4 4a.5.5 0 0 1-.708-.708L13.293 8.5H1.5A.5.5 0 0 1 1 8"/>
                    </svg>
                </button>
            </div>
        `;

        document.getElementById('wizard-prev-btn').addEventListener('click', () => {
            this.wizardState.currentStep = 1;
            this.renderWizardStep();
        });

        document.getElementById('wizard-next-btn').addEventListener('click', () => {
            // Collect selected rules (preserving any existing enforcement levels)
            const checkboxes = container.querySelectorAll('input[type="checkbox"]:checked');
            const newSelectedRules = [];

            for (const cb of checkboxes) {
                const ruleTemplate = cb.dataset.rule;
                const lockDefinition = PATH_LOCK_RULES.find(item => item.template === ruleTemplate);
                let ruleText = ruleTemplate;
                if (lockDefinition) {
                    try {
                        const index = cb.id.replace('rule-', '');
                        const path = container.querySelector(`[data-path-lock-index="${index}"]`)?.value;
                        ruleText = this.buildPathLockRuleText(ruleTemplate, path);
                    } catch (error) {
                        this.app.showToast('Path lock', error.message, 'warning');
                        return;
                    }
                }
                const existingRule = this.wizardState.selectedRules.find(r =>
                    r.text === ruleText
                    || (lockDefinition && this.getPathLockDefinition(r.text)?.kind === lockDefinition.kind));
                newSelectedRules.push({
                    text: ruleText,
                    enforcement: existingRule?.enforcement || 'WARN'
                });
            }

            this.wizardState.selectedRules = newSelectedRules;

            if (this.wizardState.selectedRules.length === 0) {
                this.app.showToast('Validation', 'Please select at least one rule', 'warning');
                return;
            }

            this.wizardState.currentStep = 3;
            this.renderWizardStep();
        });

        // Update button text dynamically as checkboxes change
        container.querySelectorAll('input[type="checkbox"]').forEach(cb => {
            cb.addEventListener('change', () => {
                const checkedCount = container.querySelectorAll('input[type="checkbox"]:checked').length;
                const btn = document.getElementById('wizard-next-btn');
                btn.textContent = checkedCount > 0 ? `Next (${checkedCount} selected)` : 'Next';
            });
        });
    }

    renderStep3Enforcement(container) {
        const enforcementOptions = ['WARN', 'COMMIT', 'STOP'];
        const enforcementDescriptions = {
            'WARN': 'Warn the user but allow the action to proceed',
            'COMMIT': 'Require an explanation in the commit/PR message',
            'STOP': 'Block the commit/PR until the violation is fixed'
        };
        const enforcementIcons = {
            'WARN': '⚠️',
            'COMMIT': '💬',
            'STOP': '🛑'
        };

        const ruleCards = this.wizardState.selectedRules.map((rule, index) => {
            const options = enforcementOptions.map(opt => `
                <option value="${opt}" ${rule.enforcement === opt ? 'selected' : ''}>
                    ${enforcementIcons[opt]} ${opt}
                </option>
            `).join('');

            return `
                <div class="card mb-3">
                    <div class="card-body">
                        <div class="d-flex justify-content-between align-items-start">
                            <div class="flex-grow-1 me-3">
                                <p class="mb-2 fw-medium">${this.app.escapeHtml(rule.text)}</p>
                            </div>
                            <select class="form-select" style="width: auto; min-width: 150px;"
                                    data-rule-index="${index}">
                                ${options}
                            </select>
                        </div>
                    </div>
                </div>
            `;
        }).join('');

        container.innerHTML = `
            <h5 class="mb-4">Step 3: Set Enforcement Levels</h5>
            <p class="text-muted mb-3">Choose how each rule should be enforced:</p>
            <div class="mb-3">
                <div class="d-flex gap-4 mb-3">
                    ${enforcementOptions.map(opt => `
                        <small class="text-muted">
                            <span class="me-1">${enforcementIcons[opt]}</span>
                            <strong>${opt}:</strong> ${enforcementDescriptions[opt]}
                        </small>
                    `).join('')}
                </div>
            </div>
            <div style="max-height: 400px; overflow-y: auto;">
                ${ruleCards}
            </div>
            <div class="d-flex justify-content-between mt-4">
                <button class="btn btn-outline-secondary d-flex align-items-center gap-2" type="button" id="wizard-prev-btn">
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M15 8a.5.5 0 0 0-.5-.5H2.707l3.147-3.146a.5.5 0 1 0-.708-.708l-4 4a.5.5 0 0 0 0 .708l4 4a.5.5 0 0 0 .708-.708L2.707 8.5H14.5A.5.5 0 0 0 15 8"/>
                    </svg>
                    Back
                </button>
                <button class="btn btn-primary d-flex align-items-center gap-2" type="button" id="wizard-next-btn">
                    Review
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M1 8a.5.5 0 0 1 .5-.5h11.793l-3.147-3.146a.5.5 0 0 1 .708-.708l4 4a.5.5 0 0 1 0 .708l-4 4a.5.5 0 0 1-.708-.708L13.293 8.5H1.5A.5.5 0 0 1 1 8"/>
                    </svg>
                </button>
            </div>
        `;

        // Handle enforcement changes
        container.querySelectorAll('select[data-rule-index]').forEach(select => {
            select.addEventListener('change', (e) => {
                const index = parseInt(e.target.dataset.ruleIndex);
                this.wizardState.selectedRules[index].enforcement = e.target.value;
            });
        });

        document.getElementById('wizard-prev-btn').addEventListener('click', () => {
            this.wizardState.currentStep = 2;
            this.renderWizardStep();
        });

        document.getElementById('wizard-next-btn').addEventListener('click', () => {
            this.wizardState.currentStep = 4;
            this.renderWizardStep();
        });
    }

    renderStep4Review(container) {
        const enforcementIcons = {
            'WARN': '⚠️',
            'COMMIT': '💬',
            'STOP': '🛑'
        };

        const ruleSummary = this.wizardState.selectedRules.map(rule => {
            const enforcement = ['WARN', 'COMMIT', 'STOP'].includes(String(rule.enforcement || '').toUpperCase())
                ? String(rule.enforcement).toUpperCase()
                : 'WARN';
            return `
                <div class="d-flex justify-content-between align-items-center py-2 border-bottom">
                    <span>${this.app.escapeHtml(rule.text)}</span>
                    <span class="badge badge-${enforcement.toLowerCase()}">
                        ${enforcementIcons[enforcement]} ${this.app.escapeHtml(enforcement)}
                    </span>
                </div>`;
        }).join('');

        container.innerHTML = `
            <h5 class="mb-4">Step 4: Review & Create</h5>
            <div class="card mb-4">
                <div class="card-header">Rule File Details</div>
                <div class="card-body">
                    <dl class="row mb-0">
                        <dt class="col-sm-3">File Path</dt>
                        <dd class="col-sm-9"><code>${this.app.escapeHtml(this.wizardState.directory)}</code></dd>
                        <dt class="col-sm-3">Rules Count</dt>
                        <dd class="col-sm-9">${this.wizardState.selectedRules.length} rules</dd>
                    </dl>
                </div>
            </div>
            <div class="card mb-4">
                <div class="card-header">Selected Rules</div>
                <div class="card-body" style="max-height: 300px; overflow-y: auto;">
                    ${ruleSummary || '<p class="text-muted">No rules selected</p>'}
                </div>
            </div>
            <div class="d-flex justify-content-between">
                <button class="btn btn-outline-secondary d-flex align-items-center gap-2" type="button" id="wizard-prev-btn">
                    <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                        <path fill-rule="evenodd" d="M15 8a.5.5 0 0 0-.5-.5H2.707l3.147-3.146a.5.5 0 1 0-.708-.708l-4 4a.5.5 0 0 0 0 .708l4 4a.5.5 0 0 0 .708-.708L2.707 8.5H14.5A.5.5 0 0 0 15 8"/>
                    </svg>
                    Back
                </button>
                <button class="btn btn-success btn-lg d-flex align-items-center gap-2" type="button" id="wizard-create-btn">
                    <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" fill="currentColor" viewBox="0 0 16 16">
                        <path d="M16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0m-3.97-3.03a.75.75 0 0 0-1.08.022L7.477 9.417 5.384 7.323a.75.75 0 0 0-1.06 1.06L6.97 11.03a.75.75 0 0 0 1.079-.02l3.992-4.99a.75.75 0 0 0-.01-1.05z"/>
                    </svg>
                    Create Rule File
                </button>
            </div>
        `;

        document.getElementById('wizard-prev-btn').addEventListener('click', () => {
            this.wizardState.currentStep = 3;
            this.renderWizardStep();
        });

        document.getElementById('wizard-create-btn').addEventListener('click', () => {
            this.createAgent();
        });
    }

    async createAgent() {
        try {
            // First create the rule file with just the rules (text only for the initial creation)
            const ruleTexts = this.wizardState.selectedRules.map(r => r.text);

            await this.app.apiCall('/api/v1/agents', 'POST', {
                path: this.wizardState.directory,
                rules: ruleTexts
            });

            // Now update enforcement levels for each rule
            for (const rule of this.wizardState.selectedRules) {
                if (rule.enforcement !== 'WARN') { // WARN is the default
                    try {
                        await this.app.apiCall('/api/v1/agents/rules/enforcement', 'PUT', {
                            path: this.wizardState.directory,
                            ruleText: rule.text,
                            enforcement: rule.enforcement
                        });
                    } catch (enfError) {
                        console.warn(`Failed to set enforcement for rule: ${rule.text}`, enfError);
                    }
                }
            }

            this.app.showToast('Success', 'Rule file created successfully!', 'success');

            // Refresh data and navigate to the new agent
            await this.app.refreshDashboardData();

            const newAgent = this.app.data.agents.find(a => a.path === this.wizardState.directory);
            if (newAgent) {
                this.app.navigate('agent-edit', newAgent);
            } else {
                this.app.navigate('dashboard', {}, { resetStack: true });
            }
        } catch (error) {
            console.error('Failed to create rule file:', error);
            this.app.showError('Failed to create rule file. ' + (error.message || ''));
        }
    }
}
