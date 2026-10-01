import { BoardApi } from './board-api.js';
import { escapeHtml, confirmDialog, isConfirmDialogOpen, getCliBrand } from './utils.js';
import { getJobCliForLlm } from './jobs-controller.js';
import { workerModelSummary } from './llm-display.js';

const selectedIds = settings => settings.jobIds ?? (settings.jobId ? [settings.jobId] : []);
const icon = name => `<i class="fa-solid fa-${name}" aria-hidden="true"></i>`;

function workerIdentity(job, environments = []) {
    const actions = Array.isArray(job?.actions) ? job.actions : [];
    const worker = actions.length
        ? actions.find(action => Number(action.kind) === 0)
        : job?.environmentId ? job : null;
    if (!worker) return { label: job ? 'Script workflow' : 'Unavailable Automation', icon: job ? 'code' : 'question' };
    const environment = environments.find(item => worker.environmentId != null && Number(item.id) === Number(worker.environmentId));
    const cli = environment?.cli || getJobCliForLlm(worker.llm || job.llm);
    return { ...getCliBrand(cli || ''), icon: 'user-gear',
        workerName: environment?.name || worker.environmentName || job.environmentName,
        modelSummary: workerModelSummary(cli, environment?.customArgs) };
}

/** Compact entry points to the existing destination-lane Automation settings. */
export class BoardLaneAgents {
    constructor(app) {
        this.app = app;
    }

    button(column) {
        return `<button type="button" class="board-lane-agents-button" data-board-action="lane-agents"
            data-column-id="${escapeHtml(column.id)}" aria-haspopup="dialog" aria-expanded="false"
            aria-label="Agents on entry to ${escapeHtml(column.name)}" title="Manage agents on entry to ${escapeHtml(column.name)}">
            ${icon('robot')}<span class="board-lane-agents-count" aria-hidden="true">…</span>
            <span class="board-lane-agents-caption">Agents</span>
        </button>`;
    }

    mount(root) {
        this.countAbort?.abort();
        if (this.root !== root) this.close(false);
        this.root = root;
        const buttons = [...root.querySelectorAll('[data-board-action="lane-agents"]')];
        // Pagination replaces the lane buttons, but the body-mounted panel owns live
        // drafts, focus and requests. Keep it open and attach it to the new button.
        if (this.panel) {
            const anchor = buttons.find(button => button.dataset.columnId === this.anchor?.dataset.columnId);
            if (anchor) {
                this.anchor = anchor;
                anchor.setAttribute('aria-expanded', 'true');
                anchor.setAttribute('aria-controls', this.panel.id);
                this.positionPanel?.();
            } else this.close(false);
        }
        this.countAbort = new AbortController();
        const signal = this.countAbort.signal;
        for (const button of buttons) {
            BoardApi.getLaneAutomationAsync(button.dataset.columnId, { signal }).then(settings => {
                if (!signal.aborted && button.isConnected) this.updateCount(button, settings);
            }).catch(error => {
                if (signal.aborted || !button.isConnected) return;
                button.querySelector('.board-lane-agents-count').textContent = '!';
                button.title = `Could not load agents. Click to retry. ${error?.message || ''}`;
            });
        }
    }

    updateCount(button, settings) {
        if (Number(button.dataset.agentsRevision) > settings.revision) return;
        button.dataset.agentsRevision = String(settings.revision ?? 0);
        const count = selectedIds(settings).length;
        button.querySelector('.board-lane-agents-count').textContent = String(count);
        button.title = `${count} ${count === 1 ? 'Automation' : 'Automations'} on entry. Click to manage.`;
    }

    updateColumnCount(columnId, settings) {
        this.root?.querySelectorAll('[data-board-action="lane-agents"]').forEach(button => {
            if (button.dataset.columnId === columnId) this.updateCount(button, settings);
        });
    }

    updateActivity(columnIds = []) {
        const running = new Set(columnIds);
        this.root?.querySelectorAll('[data-board-action="lane-agents"]').forEach(button => {
            const active = running.has(button.dataset.columnId);
            button.classList.toggle('is-running', active);
            button.querySelector('.board-lane-agents-caption').textContent = active ? 'Running' : 'Agents';
            button.setAttribute('aria-description', active ? 'An Automation from this lane is running.' : '');
        });
    }

    open(button, column) {
        if (this.anchor === button) { this.close(); return; }
        this.close(false);
        if (!column) return;
        const abort = new AbortController();
        const panel = document.createElement('section');
        panel.className = 'board-lane-agents-panel';
        panel.id = 'board-lane-agents-panel';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-labelledby', 'board-lane-agents-title');
        panel.innerHTML = `<header class="board-lane-agents-heading">
            <div><h2 id="board-lane-agents-title">Lane agents</h2><p>On entry to <strong>${escapeHtml(column.name)}</strong></p></div>
            <button type="button" class="board-lane-agents-action" data-agent-action="close" aria-label="Close lane agents">${icon('xmark')}</button>
        </header>
        <p class="board-lane-agents-help">Move cards freely. Enabled Automations run after a card stays here for 60 seconds, from any lane or when newly created.</p>
        <div data-lane-agents-content role="status">Loading agents…</div>`;
        this.anchor = button;
        this.panel = panel;
        button.setAttribute('aria-expanded', 'true');
        button.setAttribute('aria-controls', panel.id);
        document.body.append(panel);
        const alive = () => !abort.signal.aborted && panel.isConnected;
        const content = panel.querySelector('[data-lane-agents-content]');
        let settings;
        let jobs = [];
        let environments = [];
        let busy = false;
        let adding = false;
        const descriptionDrafts = new Map();
        const editingDescriptions = new Set();
        const savedDescriptions = new Set();
        const options = { showLoading: false, preferErrorResponseMessage: true, signal: abort.signal };
        const projectPath = this.app.data.configs?.rootPath || '';

        const position = () => {
            if (!alive() || !this.anchor?.isConnected) return;
            const anchor = this.anchor.getBoundingClientRect();
            const bounds = panel.getBoundingClientRect();
            const left = Math.max(8, Math.min(anchor.left + anchor.width / 2 - bounds.width / 2, window.innerWidth - bounds.width - 8));
            panel.style.left = `${left}px`;
            panel.style.top = `${Math.max(8, Math.min(anchor.bottom + 22, window.innerHeight - bounds.height - 8))}px`;
        };
        this.positionPanel = position;

        const render = () => {
            const ids = selectedIds(settings);
            const choices = settings.jobs || [];
            content.removeAttribute('role');
            content.innerHTML = `<div class="board-lane-agents-list">${ids.map(id => {
                const option = choices.find(job => job.id === id);
                const job = jobs.find(job => job.id === id);
                const name = option?.name || `Unavailable Automation (${id})`;
                const description = descriptionDrafts.get(id) ?? job?.description ?? '';
                const worker = workerIdentity(job, environments);
                const environmentId = job?.actions?.find(a => Number(a.kind) === 0)?.environmentId ?? job?.environmentId;
                const purpose = environments.find(env => Number(env.id) === Number(environmentId))?.purpose;
                const workerLogo = worker.logo
                    ? `<img src="${escapeHtml(worker.logo)}" alt="${escapeHtml(worker.label)}"${worker.logoFilter ? ` style="filter:${escapeHtml(worker.logoFilter)}"` : ''}>`
                    : icon(worker.icon);
                const workerLabel = [worker.label, worker.workerName].filter(Boolean).join(' · ');
                return `<form class="board-lane-agent" data-agent-id="${Number(id)}" data-agent-description-form>
                    <div class="board-lane-agent-heading">
                    <span class="board-lane-agent-icon" title="${escapeHtml(workerLabel)}">${workerLogo}</span>
                    <div class="board-lane-agent-copy"><strong>${escapeHtml(name)}</strong>
                        <small>${escapeHtml(workerLabel)}</small>
                        <small>${purpose === 'code_review' ? 'Code review · Saves a card report; the agent follows Board workflow for the next action.' : 'Work / custom · Produces the configured output and a handoff.'}</small>
                        ${worker.modelSummary ? `<small class="board-lane-agent-model">${escapeHtml(worker.modelSummary)}</small>` : ''}
                        ${option && !option.enabled ? '<small>Disabled · manage in the Automation editor</small>' : ''}</div>
                    <button type="button" class="board-lane-agents-action" data-agent-action="edit" aria-label="Edit ${escapeHtml(name)}"
                        title="Edit Automation" ${option ? '' : 'disabled'}>${icon('pen')}</button>
                    <button type="button" class="board-lane-agents-action" data-agent-action="remove" aria-label="Remove ${escapeHtml(name)} from this lane"
                        title="Remove from this lane">${icon('trash-can')}</button>
                    </div>
                    ${job ? editingDescriptions.has(id) ? `<label class="board-lane-agent-description-label" for="lane-agent-description-${Number(id)}">Agent description</label>
                        <textarea id="lane-agent-description-${Number(id)}" class="form-control board-lane-agent-description"
                            data-agent-description rows="3" maxlength="2000" aria-label="Agent description for ${escapeHtml(name)}"
                            placeholder="Describe what this agent does when a card enters the lane.">${escapeHtml(description)}</textarea>
                        <div class="board-lane-agent-description-footer">
                            <span data-agent-description-status role="status">${descriptionDrafts.has(id) ? 'Unsaved changes' : savedDescriptions.has(id) ? 'Description saved' : 'Up to 2,000 characters'}</span>
                            <div class="board-lane-agent-description-actions">
                                <button type="button" class="btn btn-sm board-lane-agent-description-button" data-agent-action="cancel-description"
                                    aria-label="Cancel description for ${escapeHtml(name)}">Cancel</button>
                                <button type="submit" class="btn btn-sm btn-outline-secondary" data-agent-save-description
                                    aria-label="Save description for ${escapeHtml(name)}" ${descriptionDrafts.has(id) ? '' : 'disabled'}>Save description</button>
                            </div>
                        </div>` : `<p class="board-lane-agent-description-preview">${escapeHtml(description) || 'No description yet.'}</p>
                        <div class="board-lane-agent-description-footer">
                            <span data-agent-description-status role="status">${savedDescriptions.has(id) ? 'Description saved' : ''}</span>
                            <button type="button" class="btn btn-sm board-lane-agent-description-button" data-agent-action="edit-description"
                                aria-label="Edit description for ${escapeHtml(name)}">${icon('pen')} Edit description</button>
                        </div>` : '<p class="board-lane-agents-empty">Description unavailable.</p>'}
                </form>`;
            }).join('') || '<p class="board-lane-agents-empty">No agents on entry to this lane. Add an existing Automation to get started.</p>'}</div>
            <p class="board-lane-agents-scope">Descriptions are shared with agents using the Board and apply wherever this Automation is used. Remove unlinks it from this lane and cancels the lane’s pending triggers.</p>
            <div data-agent-error role="alert"></div>
            ${adding ? `<form class="board-lane-agents-add">
                <label for="board-lane-agent-choice">Existing Automation</label>
                <select id="board-lane-agent-choice" class="form-select form-select-sm" required>
                    <option value="">Choose an Automation…</option>
                    ${choices.filter(job => !ids.includes(job.id)).map(job => `<option value="${Number(job.id)}" ${job.enabled ? '' : 'disabled'}>${escapeHtml(job.name)}${job.enabled ? '' : ' (disabled)'}</option>`).join('')}
                </select>
                <div class="board-lane-agents-footer"><button type="submit" class="btn btn-sm btn-outline-primary">Add to lane</button>
                    <button type="button" class="btn btn-sm btn-link" data-agent-action="create">Create Automation…</button></div>
                <small>Create and edit Automations on the Automations page, then select them here.</small>
            </form>` : `<button type="button" class="btn btn-sm btn-outline-secondary" data-agent-action="add">${icon('plus')} Add agent</button>`}`;
            position();
        };

        const load = async () => {
            const [lane, catalog, profiles] = await Promise.all([
                BoardApi.getLaneAutomationAsync(column.id, { signal: abort.signal }),
                this.app.apiCall(`/api/v1/jobs?projectPath=${encodeURIComponent(projectPath)}`, 'GET', null, options),
                this.app.apiCall('/api/v1/environments', 'GET', null, options)
            ]);
            if (!alive()) return;
            settings = lane;
            jobs = catalog?.jobs || [];
            environments = profiles?.environments || [];
            this.updateColumnCount(column.id, settings);
            render();
        };
        const reload = async () => {
            if (busy) return;
            busy = true;
            content.textContent = 'Loading agents…';
            try { await load(); }
            catch (error) {
                if (!alive()) return;
                content.innerHTML = `<p role="alert">${escapeHtml(error?.message || 'Could not load agents.')}</p>
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-agent-action="retry">Retry</button>`;
                position();
            } finally { busy = false; }
        };
        const saveSelection = async ids => {
            const saved = await BoardApi.saveLaneAutomationAsync(column.id, { jobIds: ids, expectedRevision: settings.revision });
            if (!alive()) return;
            // Keep a successful write visible even if the follow-up catalog read fails.
            settings = { ...settings, ...saved, jobIds: ids };
            this.updateColumnCount(column.id, settings);
            await load();
        };
        const run = async (operation, focusSelector) => {
            if (busy) return;
            busy = true;
            content.querySelectorAll('button, select, textarea').forEach(control => { control.disabled = true; });
            try { await operation(); }
            catch (error) {
                if (!alive()) return;
                render();
                const target = content.querySelector('[data-agent-error]');
                target.textContent = error?.message || 'Could not update lane agents.';
                const retry = document.createElement('button');
                retry.type = 'button';
                retry.className = 'btn btn-sm btn-link';
                retry.dataset.agentAction = 'retry';
                retry.textContent = 'Reload';
                target.append(retry);
            } finally {
                busy = false;
                if (alive()) {
                    // render() reflects the saved state, including unavailable rows.
                    content.querySelector(focusSelector)?.focus();
                    position();
                }
            }
        };
        panel.addEventListener('click', async event => {
            const action = event.target.closest('[data-agent-action]')?.dataset.agentAction;
            if (action === 'close') { this.close(); return; }
            if (!action || busy) return;
            const id = Number(event.target.closest('[data-agent-id]')?.dataset.agentId);
            if (action === 'retry') { void reload(); return; }
            if (action === 'add') { adding = true; render(); content.querySelector('select')?.focus(); return; }
            if (action === 'edit-description') {
                editingDescriptions.add(id);
                render();
                content.querySelector(`[data-agent-id="${id}"] [data-agent-description]`)?.focus();
                return;
            }
            if (action === 'cancel-description') {
                descriptionDrafts.delete(id);
                editingDescriptions.delete(id);
                render();
                content.querySelector(`[data-agent-id="${id}"] [data-agent-action="edit-description"]`)?.focus();
                return;
            }
            if (action === 'edit' || action === 'create') {
                this.close(false);
                this.app.navigate('jobs', action === 'edit' ? { editJobId: id } : { newJob: true, triggerKind: 3 });
                return;
            }
            if (action === 'remove') {
                const name = settings.jobs?.find(job => job.id === id)?.name || `Automation ${id}`;
                // The existing endpoint accepts enabled selections only. Explain any other
                // unavailable entries that must be cleared in the same revision-checked save.
                const retained = selectedIds(settings).filter(value => value !== id && settings.jobs?.some(job => job.id === value && job.enabled));
                const alsoRemoved = selectedIds(settings).filter(value => value !== id && !retained.includes(value));
                const extra = alsoRemoved.length ? ` Also remove these disabled or unavailable selections so the lane can be saved: ${alsoRemoved.map(value => settings.jobs?.find(job => job.id === value)?.name || `Automation ${value}`).join(', ')}.` : '';
                await run(async () => {
                    const confirmed = await confirmDialog({ title: `Remove “${name}” from ${column.name}?`,
                        message: `The Automation and its recordings are kept. Saving the lane selection cancels pending triggers for this lane.${extra}`,
                        confirmLabel: 'Remove', danger: true });
                    if (!alive()) return;
                    if (confirmed) await saveSelection(retained);
                    else render();
                }, '[data-agent-action="add"]');
            }
        });
        panel.addEventListener('input', event => {
            if (!event.target.matches('[data-agent-description]') || busy) return;
            const row = event.target.closest('[data-agent-id]');
            const id = Number(row.dataset.agentId);
            const value = event.target.value;
            if (value === (jobs.find(job => job.id === id)?.description ?? '')) descriptionDrafts.delete(id);
            else descriptionDrafts.set(id, value);
            savedDescriptions.delete(id);
            row.querySelector('[data-agent-save-description]').disabled = !descriptionDrafts.has(id);
            row.querySelector('[data-agent-description-status]').textContent = descriptionDrafts.has(id) ? 'Unsaved changes' : 'Up to 2,000 characters';
        });
        panel.addEventListener('submit', event => {
            if (event.target.matches('[data-agent-description-form]')) {
                event.preventDefault();
                const id = Number(event.target.dataset.agentId);
                if (busy || !descriptionDrafts.has(id)) return;
                const description = descriptionDrafts.get(id);
                void run(async () => {
                    // Update the existing description using a fresh definition. Leaving out
                    // actions preserves the workflow and its script approvals on the server.
                    const job = await this.app.apiCall(`/api/v1/jobs/${id}`, 'GET', null, options);
                    if (!alive()) return;
                    const { name, projectPath, llm, environmentId, prompt, timeoutMinutes, triggers, launchMinimized, enabled } = job;
                    const saved = await this.app.apiCall(`/api/v1/jobs/${id}`, 'PUT',
                        { name, projectPath, llm, environmentId, prompt, timeoutMinutes, triggers, launchMinimized, enabled, description },
                        { showLoading: false, preferErrorResponseMessage: true });
                    if (!alive()) return;
                    jobs = jobs.map(item => item.id === id ? saved : item);
                    settings.jobs = settings.jobs.map(item => item.id === id ? { id, name: saved.name, enabled: saved.enabled } : item);
                    descriptionDrafts.delete(id);
                    editingDescriptions.delete(id);
                    savedDescriptions.add(id);
                    render();
                }, `[data-agent-id="${id}"] [data-agent-description], [data-agent-id="${id}"] [data-agent-action="edit-description"]`);
                return;
            }
            if (!event.target.matches('.board-lane-agents-add')) return;
            event.preventDefault();
            const id = Number(content.querySelector('select').value);
            if (!id || busy) return;
            if (selectedIds(settings).some(value => !settings.jobs?.some(job => job.id === value && job.enabled))) {
                content.querySelector('[data-agent-error]').textContent = 'Enable disabled Automations in the Automation editor, or remove disabled or unavailable selections before adding another.';
                position();
                return;
            }
            void run(async () => {
                await saveSelection([...selectedIds(settings), id]);
                if (alive()) { adding = false; render(); }
            }, `[data-agent-id="${id}"] [data-agent-action="edit"]`);
        });
        const outside = event => {
            if (!panel.contains(event.target) && !this.anchor?.contains(event.target) && !isConfirmDialogOpen()) this.close(false);
        };
        const keydown = event => {
            if (event.key !== 'Escape' || isConfirmDialogOpen()) return;
            event.preventDefault();
            event.stopImmediatePropagation();
            this.close();
        };
        const scroll = event => { if (!panel.contains(event.target)) position(); };
        document.addEventListener('pointerdown', outside);
        document.addEventListener('keydown', keydown, true);
        window.addEventListener('resize', position);
        document.addEventListener('scroll', scroll, true);
        const resizeObserver = new ResizeObserver(position);
        resizeObserver.observe(panel);
        this.cleanup = () => {
            abort.abort();
            resizeObserver.disconnect();
            document.removeEventListener('pointerdown', outside);
            document.removeEventListener('keydown', keydown, true);
            window.removeEventListener('resize', position);
            document.removeEventListener('scroll', scroll, true);
        };
        position();
        panel.querySelector('[data-agent-action="close"]').focus();
        void reload();
    }

    close(restoreFocus = true) {
        this.cleanup?.();
        this.cleanup = null;
        this.positionPanel = null;
        this.panel?.remove();
        this.panel = null;
        this.anchor?.setAttribute('aria-expanded', 'false');
        this.anchor?.removeAttribute('aria-controls');
        if (restoreFocus && this.anchor?.isConnected) this.anchor.focus();
        this.anchor = null;
    }

    dispose() {
        this.countAbort?.abort();
        this.close(false);
        this.root = null;
    }
}
