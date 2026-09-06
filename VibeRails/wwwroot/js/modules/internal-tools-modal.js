import { escapeHtml } from './utils.js';

const PAGE_SIZE = 100;
const STATUS_LABELS = {
    started: 'Started',
    succeeded: 'Succeeded',
    uploaded: 'Uploaded · local update failed',
    failed: 'Failed',
    cancelled: 'Cancelled',
    skipped: 'Skipped'
};
const UPLOAD_STATUS_LABELS = { ...STATUS_LABELS, started: 'Started · outcome pending', succeeded: 'Uploaded' };
const LOG_SOURCES = {
    application: 'Application logs',
    daemon: 'VibeRails Demon logs',
    features: 'Feature journal'
};

// Add internal screens here. Each section owns its markup and lazy load function;
// shared dialog lifetime, keyboard navigation and request cleanup stay below.
const SECTIONS = [
    { id: 'about', label: 'About', icon: 'fa-circle-info', render: renderAbout, load: modal => modal.loadVersion() },
    { id: 'uploads', label: 'Data uploads', icon: 'fa-cloud-arrow-up', render: () => renderDataScreen('uploads'), load: modal => modal.loadEntries('uploads') },
    { id: 'logs', label: 'Logs', icon: 'fa-list-ul', render: () => renderDataScreen('logs'), load: modal => modal.loadEntries('logs') }
];

export function showInternalToolsModal(app) {
    const modal = new InternalToolsModal(app);
    modal.open();
    return modal;
}

class InternalToolsModal {
    constructor(app) {
        this.app = app;
        this.activeSection = null;
        this.closed = false;
        this.requests = new Map();
        this.loaded = new Set();
        this.lists = {
            uploads: { offset: 0, entries: [], hasMore: false },
            logs: { offset: 0, entries: [], hasMore: false }
        };
    }

    open() {
        this.app.showModal('Internal tools', `
            <div id="vb-internal-tools" class="vb-internal-tools">
                <div class="vb-internal-tabs" role="tablist" aria-label="Internal tools sections">
                    ${SECTIONS.map(section => `
                        <button type="button" class="vb-internal-tab" role="tab" id="vb-internal-tab-${section.id}"
                            aria-controls="vb-internal-panel-${section.id}" aria-selected="false" tabindex="-1" data-section="${section.id}">
                            <i class="fas ${section.icon}" aria-hidden="true"></i> ${section.label}
                        </button>`).join('')}
                </div>
                ${SECTIONS.map(section => `
                    <section class="vb-internal-panel" id="vb-internal-panel-${section.id}" role="tabpanel"
                        aria-labelledby="vb-internal-tab-${section.id}" tabindex="0" hidden>${section.render()}</section>`).join('')}
                <div class="vb-internal-footer">
                    <span class="text-muted small">Local diagnostics · Refresh to see new activity</span>
                    <button type="button" class="btn btn-secondary btn-sm" data-action="close-modal">Close</button>
                </div>
            </div>`, { onClose: () => this.dispose() });

        this.root = document.getElementById('vb-internal-tools');
        if (!this.root) return;
        this.root.closest('.modal')?.classList.add('vb-internal-modal');
        this.root.addEventListener('click', event => this.handleClick(event));
        this.root.querySelector('[role="tablist"]').addEventListener('keydown', event => this.handleTabKeydown(event));
        this.root.querySelectorAll('form[data-screen]').forEach(form => {
            form.addEventListener('submit', event => {
                event.preventDefault();
                this.loadEntries(form.dataset.screen, 0);
            });
            form.addEventListener('change', event => {
                if (event.target.tagName !== 'SELECT') return;
                if (event.target.name === 'source') this.setLogSource(event.target.value);
                this.loadEntries(form.dataset.screen, 0);
            });
        });
        this.selectSection('about');
    }

    dispose() {
        this.closed = true;
        for (const controller of this.requests.values()) controller.abort();
        this.requests.clear();
    }

    selectSection(id) {
        const section = SECTIONS.find(item => item.id === id);
        if (!section || this.closed) return;
        for (const [key, controller] of this.requests) {
            if (key !== id) {
                controller.abort();
                this.requests.delete(key);
                // An aborted refresh must be retried when this tab is revisited.
                this.loaded.delete(key);
            }
        }
        this.activeSection = id;
        this.root.querySelectorAll('[role="tab"]').forEach(tab => {
            const selected = tab.dataset.section === id;
            tab.setAttribute('aria-selected', String(selected));
            tab.tabIndex = selected ? 0 : -1;
        });
        for (const item of SECTIONS) this.panel(item.id).hidden = item.id !== id;
        if (!this.loaded.has(id)) section.load(this);
    }

    handleTabKeydown(event) {
        const tabs = [...this.root.querySelectorAll('[role="tab"]')];
        const current = tabs.indexOf(event.target);
        if (current < 0) return;
        let next;
        if (event.key === 'ArrowRight') next = (current + 1) % tabs.length;
        else if (event.key === 'ArrowLeft') next = (current + tabs.length - 1) % tabs.length;
        else if (event.key === 'Home') next = 0;
        else if (event.key === 'End') next = tabs.length - 1;
        else return;
        event.preventDefault();
        this.selectSection(tabs[next].dataset.section);
        tabs[next].focus();
    }

    handleClick(event) {
        const tab = event.target.closest('[data-section]');
        if (tab) return this.selectSection(tab.dataset.section);
        const button = event.target.closest('[data-internal-action]');
        if (!button || button.disabled) return;
        const action = button.dataset.internalAction;
        const id = this.activeSection;
        if (action === 'refresh') return this.lists[id]
            ? this.loadEntries(id, 0)
            : SECTIONS.find(section => section.id === id)?.load(this);
        if (action === 'reset') {
            this.panel(id).querySelector('form').reset();
            if (id === 'logs') this.setLogSource('application');
            return this.loadEntries(id, 0);
        }
        if (action === 'previous') return this.loadEntries(id, Math.max(0, this.lists[id].offset - PAGE_SIZE));
        if (action === 'next') return this.loadEntries(id, this.lists[id].offset + PAGE_SIZE);
        if (action === 'close-detail') {
            this.panel(id).querySelector('[data-detail]').hidden = true;
            this.panel(id).querySelector(`[data-internal-action="details"][data-entry="${this.detailIndex}"]`)?.focus();
            return;
        }
        const entry = this.lists[id]?.entries[Number(button.dataset.entry)];
        if (!entry) return;
        if (action === 'details') this.showDetails(id, entry, button.dataset.entry);
        if (action === 'view-logs' && entry.operationId) {
            const form = this.panel('logs').querySelector('form');
            form.reset();
            this.setLogSource('features');
            this.setFeatures([entry.feature || 'data-upload']);
            form.elements.namedItem('feature').value = entry.feature || 'data-upload';
            form.elements.namedItem('operationId').value = entry.operationId;
            this.lists.logs.offset = 0;
            this.loaded.delete('logs');
            this.selectSection('logs');
            this.root.querySelector('#vb-internal-tab-logs').focus();
        }
    }

    panel(id) {
        return this.root.querySelector(`#vb-internal-panel-${id}`);
    }

    setLogSource(source) {
        const panel = this.panel('logs');
        const form = panel.querySelector('form');
        form.elements.namedItem('source').value = source;
        form.elements.namedItem('feature').value = '';
        for (const name of ['status', 'operationId']) {
            const control = form.elements.namedItem(name);
            control.value = '';
            control.disabled = source !== 'features';
        }
        form.elements.namedItem('operationId').placeholder = source === 'features' ? 'Any operation' : 'Feature journal only';
        this.setFeatures([]);
        this.lists.logs = { offset: 0, entries: [], hasMore: false };
        this.loaded.delete('logs');
        panel.querySelector('[data-warning]').hidden = true;
        this.renderEntries('logs');
    }

    beginRequest(id) {
        this.requests.get(id)?.abort();
        const controller = new AbortController();
        this.requests.set(id, controller);
        return controller;
    }

    isCurrent(id, controller) {
        return !this.closed && this.root.isConnected && this.requests.get(id) === controller && !controller.signal.aborted;
    }

    async loadVersion() {
        const controller = this.beginRequest('about');
        const output = this.panel('about').querySelector('[data-version]');
        output.textContent = 'Loading…';
        try {
            const response = await this.app.apiCall('/api/v1/update/version', 'GET', null,
                { showLoading: false, signal: controller.signal });
            if (!this.isCurrent('about', controller)) return;
            output.textContent = response?.version || 'Unknown';
            this.loaded.add('about');
        } catch (error) {
            if (this.isCurrent('about', controller)) output.textContent = 'Unavailable — use Refresh to try again.';
        } finally {
            if (this.requests.get('about') === controller) this.requests.delete('about');
        }
    }

    async loadEntries(id, offset = this.lists[id].offset) {
        const panel = this.panel(id);
        const state = this.lists[id];
        const controller = this.beginRequest(id);
        const query = new URLSearchParams(new FormData(panel.querySelector('form')));
        query.set('offset', String(offset));
        query.set('limit', String(PAGE_SIZE));
        panel.querySelector('[data-notice]').textContent = 'Loading…';
        panel.querySelector('[data-error]').hidden = true;
        panel.querySelector('[data-detail]').hidden = true;
        panel.querySelector('[data-results]').setAttribute('aria-busy', 'true');
        panel.querySelectorAll('[data-page-button]').forEach(button => { button.disabled = true; });
        try {
            const response = await this.app.apiCall(`/api/v1/internal/${id}?${query}`, 'GET', null,
                { showLoading: false, signal: controller.signal, preferErrorResponseMessage: true });
            if (!this.isCurrent(id, controller)) return;
            state.offset = offset;
            state.entries = Array.isArray(response?.entries) ? response.entries.slice(0, PAGE_SIZE) : [];
            state.hasMore = Boolean(response?.hasMore);
            panel.querySelector('[data-detail]').hidden = true;
            this.loaded.add(id);
            if (id === 'logs') this.setFeatures(response?.features || []);
            this.renderEntries(id);
            const issues = [];
            if (response?.droppedCount > 0) issues.push(`${response.droppedCount} events dropped while the logger was busy`);
            if (response?.writeFailures > 0) issues.push(`${response.writeFailures} events could not be written to disk`);
            if (response?.readFailures > 0) issues.push(`${response.readFailures} log files or records could not be read in this snapshot`);
            const notices = [];
            if (response?.truncated) notices.push('Only a bounded window of recent log files and entries is shown; older history may be omitted.');
            if (issues.length) notices.push(`History may be incomplete: ${issues.join('; ')}.`);
            const warning = panel.querySelector('[data-warning]');
            warning.hidden = notices.length === 0;
            warning.textContent = notices.join(' ');
        } catch (error) {
            if (!this.isCurrent(id, controller)) return;
            const alert = panel.querySelector('[data-error]');
            alert.textContent = `Unable to load ${id === 'uploads' ? 'upload history' : 'logs'}. ${error.message || 'Please try again.'}`;
            alert.hidden = false;
            panel.querySelector('[data-notice]').textContent = state.entries.length ? 'Showing the last loaded page.' : 'No results loaded.';
        } finally {
            if (this.isCurrent(id, controller)) {
                panel.querySelector('[data-results]').setAttribute('aria-busy', 'false');
                panel.querySelector('[data-internal-action="previous"]').disabled = state.offset === 0;
                panel.querySelector('[data-internal-action="next"]').disabled = !state.hasMore;
                this.requests.delete(id);
            }
        }
    }

    setFeatures(features) {
        const select = this.panel('logs').querySelector('[name="feature"]');
        const selected = select.value;
        const values = [...new Set([selected, ...features].filter(value => typeof value === 'string' && value))].sort();
        select.innerHTML = '<option value="">All features</option>' + values.map(value => `<option value="${escapeHtml(value)}">${escapeHtml(value)}</option>`).join('');
        select.value = selected;
    }

    renderEntries(id) {
        const panel = this.panel(id);
        const { entries, offset, hasMore } = this.lists[id];
        const isUpload = id === 'uploads';
        const columns = isUpload ? ['Last activity', 'Data', 'Status', 'Actions'] : ['Time', 'Feature / category', 'Level', 'Message', 'Actions'];
        panel.querySelector('[data-results]').innerHTML = entries.length ? `
            <table class="vb-internal-table">
                <caption class="visually-hidden">${isUpload ? 'Retained upload attempts' : 'Available log events'}, newest first</caption>
                <thead><tr>${columns.map(label => `<th scope="col">${label}</th>`).join('')}</tr></thead>
                <tbody>${entries.map((entry, index) => `
                    <tr>
                        <td class="vb-internal-time">${escapeHtml(formatTime(entry.timestampUtc))}</td>
                        <td class="vb-internal-subject">${escapeHtml(isUpload ? entry.subject || 'Upload' : entry.feature || 'Unknown')}
                            <span class="vb-internal-secondary">${escapeHtml(isUpload ? entry.message || '' : [entry.eventName, entry.sourceFile].filter(Boolean).join(' · '))}</span></td>
                        <td>${renderBadge(isUpload ? entry.status : entry.level, isUpload)}</td>
                        ${isUpload ? '' : `<td class="vb-internal-message">${escapeHtml(entry.message || '')}${entry.status ? `<span class="vb-internal-secondary">${escapeHtml(STATUS_LABELS[entry.status] || entry.status)}</span>` : ''}</td>`}
                        <td><div class="vb-internal-row-actions">
                            <button type="button" class="btn btn-sm btn-outline-secondary" data-internal-action="details" data-entry="${index}">Details</button>
                            ${isUpload && entry.operationId ? `<button type="button" class="btn btn-sm btn-outline-primary" data-internal-action="view-logs" data-entry="${index}">View logs</button>` : ''}
                        </div></td>
                    </tr>`).join('')}</tbody>
            </table>` : `
            <div class="vb-internal-empty">
                <i class="fas ${isUpload ? 'fa-cloud-arrow-up' : 'fa-list-ul'}" aria-hidden="true"></i>
                <strong>No ${isUpload ? 'upload attempts' : 'log events'} found</strong>
                <span>Try another filter or refresh after new activity.${isUpload ? ' Earlier upload activity is not backfilled.' : ''}</span>
            </div>`;
        panel.querySelector('[data-notice]').textContent = entries.length
            ? `${offset + 1}–${offset + entries.length}${hasMore ? ' · more available' : ''} · newest first`
            : offset ? 'No more matching entries. Use Previous or Refresh.' : '0 matching entries';
    }

    showDetails(id, entry, index) {
        const detail = this.panel(id).querySelector('[data-detail]');
        this.detailIndex = index;
        const fields = [
            ['Source', LOG_SOURCES[entry.source] || entry.source || 'Feature journal'], ['Log file', entry.sourceFile],
            ['Time (UTC)', entry.timestampUtc], ['Feature', entry.feature], ['Event', entry.eventName],
            ['Level', entry.level], ['Status', (id === 'uploads' ? UPLOAD_STATUS_LABELS : STATUS_LABELS)[entry.status] || entry.status],
            ['Subject', entry.subject], ['Operation ID', entry.operationId], ['Event ID', entry.id]
        ];
        detail.innerHTML = `
            <div class="d-flex justify-content-between align-items-center gap-2 mb-3">
                <h6 class="mb-0">${id === 'uploads' ? 'Upload attempt' : 'Log event'} details</h6>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-internal-action="close-detail">Close details</button>
            </div>
            <dl class="vb-internal-detail-fields">${fields.filter(([, value]) => value != null && value !== '').map(([label, value]) => `<dt>${label}</dt><dd>${escapeHtml(String(value))}</dd>`).join('')}</dl>
            <pre class="vb-internal-detail-message">${escapeHtml(entry.message || '')}</pre>
            ${id === 'uploads' && entry.status === 'started' ? '<p class="text-muted small mb-0">No final outcome was recorded. The upload may still be running, or the app may have stopped before recording its result.</p>' : ''}`;
        detail.hidden = false;
        detail.focus();
        detail.scrollIntoView({ block: 'nearest' });
    }
}

function renderAbout() {
    return `<div class="vb-internal-about">
        <span class="vb-internal-eyebrow">VibeRails</span>
        <h3>About this installation</h3>
        <p class="text-muted">Internal diagnostics and data tools for this app.</p>
        <dl class="vb-internal-version"><dt>Version</dt><dd id="app-version-value" class="font-monospace" data-version aria-live="polite">Loading…</dd></dl>
        <button type="button" class="btn btn-sm btn-outline-secondary" data-internal-action="refresh"><i class="fas fa-rotate-right" aria-hidden="true"></i> Refresh version</button>
    </div>`;
}

function renderDataScreen(id) {
    const isUpload = id === 'uploads';
    const select = (name, label, options, disabled = false) => `
        <label class="vb-internal-filter"><span>${label}</span>
            <select name="${name}" class="form-select form-select-sm"${disabled ? ' disabled title="Available in the feature journal"' : ''}><option value="">All ${name === 'status' ? 'statuses' : name + 's'}</option>${options.map(([value, text]) => `<option value="${value}">${text}</option>`).join('')}</select>
        </label>`;
    return `<div class="vb-internal-screen-heading">
            <div><h6>${isUpload ? 'Data upload history' : 'Logs'}</h6>
                <p class="text-muted small mb-0">${isUpload
                    ? 'Latest recorded outcome for each upload attempt. Only retained activity recorded from this update onward appears here; this is not a complete inventory of your sessions.'
                    : 'Existing application and VibeRails Demon logs, plus the new feature journal. Select a source and feature or category to follow its activity.'}</p></div>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-internal-action="refresh"><i class="fas fa-rotate-right" aria-hidden="true"></i> Refresh</button>
        </div>
        ${isUpload ? `<div class="vb-internal-crud">
            <div class="d-flex gap-2"><button type="button" class="btn btn-sm btn-outline-primary" disabled>Create</button><button type="button" class="btn btn-sm btn-outline-secondary" disabled>Edit</button><button type="button" class="btn btn-sm btn-outline-danger" disabled>Delete</button></div>
            <span class="text-muted small">Create, edit, and delete are placeholders.</span>
        </div>` : ''}
        <form class="vb-internal-filters" data-screen="${id}">
            ${isUpload ? '' : `<label class="vb-internal-filter"><span>Source</span><select name="source" class="form-select form-select-sm">${Object.entries(LOG_SOURCES).map(([value, label]) => `<option value="${value}">${label}</option>`).join('')}</select></label>`}
            ${isUpload ? '' : select('feature', 'Feature / category', []) + select('level', 'Level', ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'].map(value => [value, value]))}
            ${select('status', 'Status', Object.entries(isUpload ? UPLOAD_STATUS_LABELS : STATUS_LABELS), !isUpload)}
            ${isUpload ? '' : '<label class="vb-internal-filter"><span>Operation ID</span><input class="form-control form-control-sm" name="operationId" type="text" maxlength="128" placeholder="Feature journal only" disabled></label>'}
            <label class="vb-internal-filter vb-internal-filter-search"><span>Search</span><input class="form-control form-control-sm" name="search" type="search" maxlength="200" placeholder="${isUpload ? 'Session ID or message' : 'Message, event or file'}"></label>
            <div class="vb-internal-filter-actions"><button type="submit" class="btn btn-sm btn-primary">Apply filters</button><button type="button" class="btn btn-sm btn-outline-secondary" data-internal-action="reset">Reset</button></div>
        </form>
        <div class="alert alert-warning small mt-3 mb-0" data-warning role="status" hidden></div>
        <div class="alert alert-danger small mt-3 mb-0" data-error role="alert" hidden></div>
        <div class="vb-internal-results" data-results aria-busy="false"></div>
        <div class="vb-internal-pagination">
            <span class="text-muted small" data-notice role="status" aria-live="polite">Select this tab to load activity.</span>
            <div class="d-flex gap-2"><button type="button" class="btn btn-sm btn-outline-secondary" data-internal-action="previous" data-page-button disabled>Previous</button><button type="button" class="btn btn-sm btn-outline-secondary" data-internal-action="next" data-page-button disabled>Next</button></div>
        </div>
        <section class="vb-internal-detail" data-detail aria-label="Selected entry details" tabindex="-1" hidden></section>`;
}

function renderBadge(value, status) {
    const tone = { succeeded: 'success', uploaded: 'warning', failed: 'danger', cancelled: 'warning', Error: 'danger', Critical: 'danger', Warning: 'warning' }[value] || 'neutral';
    return `<span class="vb-internal-badge" data-tone="${tone}">${escapeHtml((status ? UPLOAD_STATUS_LABELS[value] : null) || value || 'Unknown')}</span>`;
}

function formatTime(value) {
    if (!value) return 'Unknown';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString();
}
