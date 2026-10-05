import { mountCodeAtlas } from './vendor/atlas/code-atlas.mjs';
import { mountQualityReport } from './vendor/quality/quality-report.js';
import { escapeHtml as esc, formatNumber, healthFromConcern } from './vendor/quality/report-model.js';
import { readReportTheme, observeReportTheme } from './theme-sync.js';
import { enhanceRadar } from './radar-interactions.js';
import { isConfirmDialogOpen } from '../utils.js';
import { openDiffModal } from '../diff-modal.js';

let instanceId = 0;
// The last graph a viewer fetched, keyed by its request and the report it belongs to. Re-entering the
// page replays the saved report without a rescan; the map follows the same lifetime instead of capturing
// the repository again (seconds on a large tree). A new scan response refreshes both.
let graphCache = null;
const STATUS_LABELS = { modified: 'Modified', added: 'Added', deleted: 'Deleted', renamed: 'Renamed', copied: 'Copied',
    untracked: 'Untracked', conflicted: 'Conflicted', typechange: 'Type changed' };
const STATUS_CODES = { modified: 'M', added: 'A', deleted: 'D', renamed: 'R', copied: 'C', untracked: '?', conflicted: 'U', typechange: 'T' };
export const reportPath = path => String(path || '').replace(/\\/g, '/').replace(/:\d+(?::\d+)?$/, '').replace(/^\.\//, '');
const basename = path => reportPath(path).split('/').pop();
const metricName = name => String(name || '').replaceAll('_', ' ').replace(/^./, c => c.toUpperCase());
const cappedNPath = metric => metric?.name === 'npath_complexity' && metric.value >= 1_000_000_000;
const metricValue = metric => cappedNPath(metric) ? '1B (cap)' : formatNumber(metric.value, 2);
const allMetrics = file => (file?.categories || []).flatMap(category =>
    (category.metrics || []).map(metric => ({ ...metric, category: category.name })));

/** A host-owned report view. Networking uses app.apiCall; no fixture or global route state. */
export class CodeReportViewer {
    constructor(host, app) {
        this.host = host;
        this.app = app;
        this.document = host.ownerDocument;
        this.window = this.document.defaultView;
        this.generation = 0;
        this.destroyed = false;
        this.activeList = 'changes'; // What changed comes first; the report list is one click away.
        const titleId = `code-report-details-${++instanceId}`;
        host.innerHTML = `<div class="code-report code-report-compact">
            <main aria-label="Interactive code report">
                <div class="code-layout">
                    <section class="graph-panel" aria-label="Interactive code explorer">
                        <div data-code-map><p class="load-error" role="status">Preparing repository map…</p></div>
                        <div class="graph-options" data-graph-options hidden>
                            <details data-map-diagnostics hidden><summary>Map coverage and filters</summary><div data-map-diagnostics-body></div></details>
                        </div>
                    </section>
                    <section class="report-sidebar" aria-label="Code health and report files"><div data-quality-report></div>
                        <section class="details-panel" role="region" aria-labelledby="${titleId}" hidden>
                            <div class="details-header"><div><span class="eyebrow" data-details-kicker>CODE DETAILS</span><h2 id="${titleId}" tabindex="-1"></h2></div>
                                <button type="button" class="icon-button" data-close-details aria-label="Back to code health">✕</button></div>
                            <div class="details-body" data-details-body></div><div class="details-actions" data-details-actions></div>
                        </section>
                    </section>
                </div>
            </main>
        </div>`;
        this.root = host.querySelector('.code-report');
        this.mapHost = this.root.querySelector('[data-code-map]');
        this.qualityHost = this.root.querySelector('[data-quality-report]');
        // Details replace the health summary in the sidebar: the report stays inline, never a modal.
        this.details = this.root.querySelector('.details-panel');
        this.root.querySelector('[data-close-details]').addEventListener('click', () => this.closeDetails(true));
        // Captured so the app's document-level Escape (Back) does not also leave Project health.
        this.onKeydown = event => {
            if (isConfirmDialogOpen()) return;
            if (event.key !== 'Escape' || this.details.hidden || event.defaultPrevented) return;
            event.preventDefault();
            event.stopPropagation();
            this.closeDetails(true);
        };
        this.document.addEventListener('keydown', this.onKeydown, true);
        this.quality = mountQualityReport(this.qualityHost, {
            onFileClick: file => this.focusFile(file.file),
            onMetricClick: metric => this.showFile(this.findFile(metric.file), metric.metricName),
            onCategoryClick: category => this.showCategory(category)
        });
        const loading = this.document.createElement('div');
        loading.className = 'report-files-loading';
        loading.setAttribute('aria-hidden', 'true');
        loading.innerHTML = '<span>Report files</span><i></i><i></i><i></i>';
        this.qualityHost.querySelector('.qr').append(loading);
        this.qualityHost.querySelector('.qr-title').textContent = 'Code health';
        this.pagehide = () => this.destroy();
        this.window.addEventListener('pagehide', this.pagehide);
    }

    disposeGraph() {
        this.request?.abort();
        this.request = null;
        this.changesRequest?.abort();
        this.changesRequest = null;
        this.themes?.destroy();
        this.themes = null;
        this.atlas?.destroy();
        this.atlas = null;
        this.graph = null;
    }

    setLoading() {
        if (this.destroyed) return;
        ++this.generation;
        this.radar?.destroy();
        this.radar = null;
        this.closeDetails();
        this.disposeGraph();
        this.changes = undefined;
        this.root.querySelector('[data-map-diagnostics]').hidden = true;
        this.root.querySelector('[data-graph-options]').hidden = true;
        this.quality.setLoading();
        this.mapHost.innerHTML = '<p class="load-error" role="status">Preparing repository map…</p>';
    }

    setError(message) {
        if (this.destroyed) return;
        this.setLoading();
        this.quality.setError(message || 'The code report could not be loaded.');
        // The graph remains useful when analysis fails; its failure is independent.
        this.ready = this.loadGraph([], this.generation);
        void this.loadChanges(this.generation);
    }

    async setResponse(response) {
        if (this.destroyed) return;
        this.setLoading();
        const generation = this.generation;
        this.response = structuredClone(response);
        // A successful empty changeset has no report. Preserve its missing score.
        if (response?.success !== false && !response?.report && response?.analyzedFileCount === 0)
            this.response.report = { files: [], overview: [], scorecard: [] };
        const reportFiles = this.response?.report?.files;
        const files = Array.isArray(reportFiles) ? reportFiles.map(file => reportPath(file?.file)).filter(Boolean) : [];
        // Start the map, then paint the report we already hold in memory: the quality panel needs no
        // network data, and focusFile awaits this.ready before it touches the map.
        this.ready = this.loadGraph(files, generation);
        void this.loadChanges(generation);
        if (this.quality.setResponse(this.response)) {
            this.composeFiles();
            this.radar = enhanceRadar(this.qualityHost.querySelector('.qr'), this.response, {
                onSelect: category => this.showCategory(category)
            });
        }
        await this.ready;
    }

    isCurrent(generation) { return !this.destroyed && generation === this.generation; }

    async loadGraph(files, generation) {
        this.root.querySelector('[data-map-diagnostics]').hidden = true;
        const request = this.request = new AbortController();
        const cacheKey = JSON.stringify([this.response?.startedUtc ?? null, files.slice(0, 1000)]);
        try {
            const graph = graphCache?.key === cacheKey ? graphCache.graph
                : await this.app.apiCall('/api/v1/code-analyzer/graph', 'POST',
                    { files: files.slice(0, 1000) },
                    { showLoading: false, signal: request.signal, preferErrorResponseMessage: true });
            if (!this.isCurrent(generation)) return;
            graphCache = { key: cacheKey, graph };
            this.graph = graph;
            this.mapHost.replaceChildren();
            const atlas = this.atlas = mountCodeAtlas(this.mapHost, {
                graph, theme: readReportTheme(this.root), cspNonce: this.window.__viberails_NONCE__,
                changedFiles: this.changedPaths() ?? files, highlightChanges: true,
                onOpenDetails: details => { if (this.isCurrent(generation)) this.showEntity(details); },
                onError: error => { if (this.isCurrent(generation)) this.notify(error.message); }
            });
            await atlas.ready;
            if (!this.isCurrent(generation)) return;
            this.themes = observeReportTheme(this.root, theme => atlas.setTheme(theme), error => this.notify(error.message));
            this.syncChangedFiles();
            // The change rows' locate buttons depend on the graph; the list often answers first on a large repository.
            this.composeChanges();
            const diagnostics = graph.diagnostics;
            if (diagnostics) {
                const omissions = diagnostics.omissions || [];
                const body = this.root.querySelector('[data-map-diagnostics-body]');
                body.innerHTML = `${graph.truncated ? '<p>Partial map: eligible files were left out of this snapshot; the counts below say which.</p>' : ''}
                    <p>${esc(graph.fileCount)} source files mapped. ${esc(diagnostics.supportedFiles)} supported source files in the Git catalog.
                    ${esc(diagnostics.excludedDependencyFiles)} vendor/node_modules/assets files and
                    ${esc(diagnostics.excludedBuildOutputFiles)} C# bin/obj files excluded, including report paths.</p>
                    <p>Non-C# bin/obj sources remain eligible. The map draws every entity of this snapshot and a bounded sample of
                    its links; hover or select an entity to see all of its links. Search covers the whole snapshot.</p>
                    ${omissions.length ? `<ul>${omissions.map(item => `<li>${esc(item.count)} ${esc(item.detail)}</li>`).join('')}</ul>`
                        : '<p>No omissions from the eligible source files.</p>'}`;
                this.root.querySelector('[data-map-diagnostics]').hidden = false;
            }
        } catch (error) {
            if (!this.isCurrent(generation) || request.signal.aborted) return;
            this.atlas?.destroy();
            this.atlas = null;
            this.mapHost.innerHTML = `<p class="load-error" role="alert">Code map unavailable: ${esc(error.message)} Saved report details are still available.</p>`;
        }
    }

    composeFiles() {
        this.qualityHost.querySelector('.qr-title').textContent = 'Code health';
        const files = this.qualityHost.querySelector('.qr-files');
        files.setAttribute('role', 'group');
        files.setAttribute('aria-label', 'Report files. Select a file to focus the code map.');
        files.tabIndex = 0;
        const rows = [...files.querySelectorAll('.qr-file')];
        for (const button of rows) {
            const name = button.querySelector('b'), path = name.textContent;
            const rating = button.querySelector('small');
            button.dataset.path = reportPath(path);
            button.title = path;
            button.setAttribute('aria-pressed', 'false');
            button.setAttribute('aria-label', `${path}. ${rating.textContent}. Concern ${button.children[1].textContent} out of 100. Focus in code map.`);
            name.textContent = basename(path);
            rating.textContent = path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : rating.textContent;
        }
        this.composeChanges();
        const count = this.qualityHost.querySelector('[data-report-count]');
        if (count) count.textContent = String(rows.length);
        this.showList(this.activeList || 'report');
        if (!rows.length) files.querySelector('.qr-empty').textContent = 'No source files in this report.';
    }

    changedPaths() { return Array.isArray(this.changes?.files) ? this.changes.files.map(file => file.path) : null; }

    // The working tree's changes against HEAD: every file git reports, not only the scanned sources.
    async loadChanges(generation) {
        this.changes = undefined;
        this.changesRequest?.abort();
        const request = this.changesRequest = new AbortController();
        this.composeChanges();
        try {
            const changes = await this.app.apiCall('/api/v1/code-analyzer/changes', 'GET', null,
                { showLoading: false, signal: request.signal, preferErrorResponseMessage: true });
            if (!this.isCurrent(generation)) return;
            this.changes = Array.isArray(changes?.files) ? changes
                : { error: 'The change list could not be read.', files: [], count: 0, additions: 0, deletions: 0 };
        } catch (error) {
            if (!this.isCurrent(generation) || request.signal.aborted) return;
            this.changes = { error: error.message, files: [], count: 0, additions: 0, deletions: 0 };
        }
        this.composeChanges();
        this.syncChangedFiles();
    }

    syncChangedFiles() {
        const paths = this.changedPaths();
        if (!paths || !this.atlas) return;
        this.atlas.setChangedFiles(paths.slice(0, 10000)).catch(error => this.notify(error.message));
    }

    mappedFiles() {
        return new Set((this.graph?.nodes || []).filter(node => node.kind === 'file').map(node => reportPath(node.path)));
    }

    // Map coverage opens from the card's menu and stays out of the way until asked for.
    toggleDiagnostics() {
        const options = this.root.querySelector('[data-graph-options]');
        const details = this.root.querySelector('[data-map-diagnostics]');
        if (!options || !details || this.destroyed) return false;
        const show = options.hidden;
        options.hidden = !show;
        if (show) {
            const body = details.querySelector('[data-map-diagnostics-body]');
            if (!body.innerHTML.trim()) body.innerHTML = '<p>Coverage details arrive with the repository map.</p>';
            details.hidden = false;
            details.open = true;
        }
        return show;
    }

    // Report files and Git changes share the sidebar list area behind one switch; both counts stay visible.
    composeChanges() {
        const section = this.qualityHost.querySelector('.qr-files-section');
        if (!section) return;
        let switcher = section.querySelector('.qr-list-switch');
        if (!switcher) {
            const heading = section.querySelector('h3');
            heading.replaceChildren();
            switcher = this.document.createElement('div');
            switcher.className = 'qr-list-switch';
            switcher.setAttribute('role', 'group');
            switcher.setAttribute('aria-label', 'Sidebar list');
            switcher.innerHTML = `<button type="button" data-list="report" aria-pressed="true">Report files <span class="file-count" data-report-count>0</span></button>`
                + `<button type="button" data-list="changes" aria-pressed="false">Git changes <span class="file-count" data-changes-count>…</span></button>`;
            heading.append(switcher);
            switcher.addEventListener('click', event => {
                const button = event.target.closest('[data-list]');
                if (button) this.showList(button.dataset.list);
            });
        }
        let list = section.querySelector('[data-changes-list]');
        if (!list) {
            list = this.document.createElement('div');
            list.className = 'qr-changes';
            list.setAttribute('data-changes-list', '');
            list.setAttribute('role', 'group');
            list.setAttribute('aria-label', 'Changed files in Git. Select a file to see its diff.');
            list.hidden = this.activeList !== 'changes';
            section.append(list);
        }
        const changes = this.changes;
        switcher.querySelector('[data-changes-count]').textContent = changes === undefined ? '…' : changes.error ? '—' : String(changes.count);
        switcher.querySelector('[data-list="changes"]').title = changes?.error ? `Changes unavailable: ${changes.error}` : 'Working-tree changes against HEAD';
        if (changes === undefined) { list.innerHTML = '<p class="qr-empty">Reading the working tree…</p>'; return; }
        if (changes.error) { list.innerHTML = `<p class="qr-empty">Changes unavailable: ${esc(changes.error)}</p>`; return; }
        if (!changes.files.length) { list.innerHTML = '<p class="qr-empty">No changes in the working tree.</p>'; return; }
        const mapped = this.mappedFiles();
        const plural = count => count === 1 ? 'file' : 'files';
        const stat = file => file.binary ? '<span class="change-stat">binary</span>'
            : file.additions === undefined && file.deletions === undefined ? ''
            : `<span class="change-stat"><ins>+${esc(file.additions ?? 0)}</ins><del>−${esc(file.deletions ?? 0)}</del></span>`;
        list.innerHTML = `<div class="qr-file-head change-head"><span>${esc(changes.count)} changed ${plural(changes.count)} against HEAD${changes.truncated ? ` (first ${esc(changes.files.length)} listed)` : ''}</span>`
            + `<span class="change-stat"><ins>+${esc(changes.additions)}</ins><del>−${esc(changes.deletions)}</del></span></div>`
            + changes.files.map((file, index) => {
                const path = file.path, slash = path.lastIndexOf('/');
                const name = slash >= 0 ? path.slice(slash + 1) : path, dir = slash >= 0 ? path.slice(0, slash) : '';
                const status = STATUS_LABELS[file.status] || file.status;
                const staging = file.status === 'untracked' ? 'Untracked' : file.staged && file.unstaged ? 'Staged and unstaged edits' : file.staged ? 'Staged' : 'Unstaged';
                return `<div class="change-row" data-path="${esc(path)}">`
                    + `<button type="button" class="change-open" data-change-index="${index}" title="${esc(path)}" aria-label="${esc(path)}. ${esc(status)}, ${esc(staging)}. Open the diff.">`
                    + `<span class="change-status change-status-${esc(file.status)}" aria-hidden="true">${esc(STATUS_CODES[file.status] || '·')}</span>`
                    + `<span class="change-name"><b>${esc(name)}</b><small>${esc(dir || staging)}</small></span>${stat(file)}</button>`
                    + (mapped.has(path) ? `<button type="button" class="change-locate" data-locate="${esc(path)}" title="Show on the map" aria-label="Show ${esc(name)} on the map">⌖</button>` : '')
                    + '</div>';
            }).join('');
        list.querySelectorAll('[data-change-index]').forEach(button =>
            button.addEventListener('click', () => this.openChangeDiff(Number(button.dataset.changeIndex))));
        list.querySelectorAll('[data-locate]').forEach(button =>
            button.addEventListener('click', () => this.focusFile(button.dataset.locate)));
    }

    showList(name) {
        this.activeList = name;
        const section = this.qualityHost.querySelector('.qr-files-section');
        if (!section) return;
        const files = section.querySelector('.qr-files'), changes = section.querySelector('[data-changes-list]');
        if (files) files.hidden = name === 'changes';
        if (changes) changes.hidden = name !== 'changes';
        section.querySelectorAll('.qr-list-switch [data-list]').forEach(button =>
            button.setAttribute('aria-pressed', String(button.dataset.list === name)));
    }

    // One diff viewer for the whole app: the changed files fill its rail and each diff loads on demand.
    openChangeDiff(index) {
        const changes = this.changes;
        if (!Array.isArray(changes?.files) || !changes.files.length) return;
        const files = changes.files.map(file => ({ fileName: file.path, status: file.status, load: () => this.loadDiff(file.path, file.originalPath) }));
        openDiffModal({ title: `Working tree changes · ${changes.count} ${changes.count === 1 ? 'file' : 'files'} against HEAD`, files, initialIndex: index });
    }

    async loadDiff(path, originalPath) {
        // A staged rename's "before" text lives at its original path in HEAD; the list reports that path.
        const query = `path=${encodeURIComponent(path)}${originalPath ? `&original=${encodeURIComponent(originalPath)}` : ''}`;
        const diff = await this.app.apiCall(`/api/v1/code-analyzer/changes/diff?${query}`, 'GET', null,
            { showLoading: false, preferErrorResponseMessage: true });
        return { originalContent: diff?.originalContent || '', modifiedContent: diff?.modifiedContent || '', language: diff?.language || 'plaintext',
            notice: diff?.binary ? 'Binary file: no text diff.' : diff?.truncated ? 'Large file: each side shows its first 1,000,000 characters.' : '' };
    }

    findFile(path) {
        const files = this.response?.report?.files;
        return Array.isArray(files) ? files.find(file => reportPath(file?.file) === reportPath(path)) : undefined;
    }

    async focusFile(path, nodeId) {
        const generation = this.generation;
        this.closeDetails();
        await this.ready;
        if (!this.isCurrent(generation)) return;
        const id = nodeId || this.graph?.nodes?.find(node => node.kind === 'file' && reportPath(node.path) === reportPath(path))?.id;
        if (!id || !this.atlas) {
            this.showFile(this.findFile(path));
            this.notify('This file is outside the current map. Its saved details remain available.');
            return;
        }
        try { await this.atlas.focusNode(id); }
        catch (error) { if (this.isCurrent(generation)) this.notify(error.message); return; }
        if (!this.isCurrent(generation)) return;
        this.qualityHost.querySelectorAll('.qr-file').forEach(button =>
            button.setAttribute('aria-pressed', String(button.dataset.path === reportPath(path))));
        const bounds = this.mapHost.getBoundingClientRect();
        if (bounds.bottom < 100 || bounds.top > this.window.innerHeight - 100)
            this.mapHost.scrollIntoView({ behavior: this.window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth', block: 'start' });
    }

    openDetails(title, kicker, body, action) {
        if (this.destroyed) return;
        // Details replace the summary in the sidebar; Escape or close returns focus to the source.
        if (this.details.hidden) this.detailsReturn = this.document.activeElement;
        const heading = this.details.querySelector('h2');
        heading.textContent = title;
        this.details.querySelector('[data-details-kicker]').textContent = kicker;
        this.details.querySelector('[data-details-body]').innerHTML = body;
        const actions = this.details.querySelector('[data-details-actions]');
        actions.replaceChildren();
        if (action) {
            const button = this.document.createElement('button');
            button.type = 'button';
            button.className = 'button primary';
            button.textContent = 'Show in code explorer';
            button.addEventListener('click', action);
            actions.append(button);
        }
        actions.hidden = !action;
        this.qualityHost.hidden = true;
        this.details.hidden = false;
        this.details.scrollTop = 0;
        heading.focus({ preventScroll: true });
    }

    closeDetails(restoreFocus) {
        if (this.details.hidden) return;
        this.details.hidden = true;
        this.qualityHost.hidden = false;
        if (restoreFocus && this.detailsReturn?.isConnected) this.detailsReturn.focus({ preventScroll: true });
        this.detailsReturn = null;
    }

    showFile(file, preferredName) {
        if (!file) return;
        const metrics = allMetrics(file);
        const selected = metrics.find(metric => metric.name === preferredName)
            || [...metrics].sort((a, b) => (b.score || 0) - (a.score || 0)).find(metric => metric.snippet) || metrics[0];
        const captured = new Date(this.response.startedUtc);
        const date = Number.isNaN(captured.valueOf()) ? 'Saved report' : `Report captured ${captured.toLocaleString()}`;
        const body = `<div class="details-path">${esc(file.file)}</div>
            <div class="detail-summary"><div><strong>${formatNumber(healthFromConcern(file.score), 1)}</strong><span>File health / 100</span></div>
                <div><strong>${formatNumber(file.score, 1)}</strong><span>Concern / 100</span></div><div><strong>${esc(file.rating)}</strong><span>Saved analysis</span></div></div>
            <h3>Measured signals</h3><div class="metric-list">${metrics.map((metric, index) =>
                `<button type="button" class="metric-detail" data-report-metric="${index}" aria-pressed="${metric === selected}"><span>${esc(metricName(metric.name))}</span><strong>${metricValue(metric)}</strong></button>`).join('')}</div>
            ${selected ? `<h3>${esc(metricName(selected.name))}</h3><p class="detail-note">Concern ${formatNumber(selected.score, 1)} / 100 · ${selected.direction === 'HB' || selected.higherIsBetter === true ? 'Higher measured values are better' : selected.direction === 'LB' || selected.higherIsBetter === false ? 'Lower measured values are better' : 'Measured value'} · Warning ${formatNumber(selected.warn, 2)} · Critical ${formatNumber(selected.critical, 2)}</p>` : ''}
            ${cappedNPath(selected) ? '<p class="detail-note">NPath is an estimate capped at 1 billion paths, not an exact count.</p>' : ''}
            ${selected?.snippet ? `<div class="snippet-heading">Captured excerpt · ${esc(selected.source || basename(file.file))}${selected.line ? ` · line ${esc(selected.line)}` : ''}</div><pre class="code-excerpt">${esc(selected.snippet)}</pre>`
                : '<p class="detail-note">No source excerpt was included for this metric.</p>'}
            <p class="detail-note">${esc(date)}. Excerpts may differ from the current working tree.</p>`;
        this.openDetails(basename(file.file), 'FILE QUALITY', body, () => this.focusFile(file.file));
        this.details.querySelectorAll('[data-report-metric]').forEach(button => button.addEventListener('click', () => {
            this.showFile(file, metrics[Number(button.dataset.reportMetric)].name);
            this.details.querySelector(`[data-report-metric="${button.dataset.reportMetric}"]`)?.focus();
        }));
    }

    showCategory(category) {
        const file = this.findFile(category.worstMetricFile);
        if (file) this.showFile(file, category.worstMetricName);
        else this.openDetails(category.category, 'CATEGORY', `<div class="detail-summary"><div><strong>${formatNumber(healthFromConcern(category.concern), 1)}</strong><span>Category health / 100</span></div></div><p class="detail-note">No file-level source was included for this category.</p>`);
    }

    showEntity(details) {
        const file = this.findFile(details.node.path);
        if (file) { this.showFile(file); return; }
        const children = details.children || [];
        const evidence = (details.relationships || []).filter(relationship => relationship.edge.evidence);
        this.openDetails(details.node.name, String(details.node.kind).toUpperCase(),
            `<div class="details-path">${esc(details.node.path || 'Repository structure')}</div><p class="detail-note">${esc(details.node.summary || '')}</p>
            ${children.length ? `<h3>Contains</h3><div class="metric-list">${children.map(node => `<div class="metric-detail"><span>${esc(node.name)}</span><strong>${esc(node.kind)}</strong></div>`).join('')}</div>` : ''}
            ${evidence.length ? `<h3>Source references</h3>${evidence.map(relationship => `<p class="detail-note">${esc(relationship.edge.evidence)}</p>`).join('')}` : ''}
            <p class="detail-note">This entity has no file-level result in the saved report.</p>`,
            () => this.focusFile(details.node.path, details.node.id));
    }

    notify(message) { if (!this.destroyed) this.app.showToast('Code report', message, 'info'); }

    destroy() {
        if (this.destroyed) return;
        this.destroyed = true;
        ++this.generation;
        this.radar?.destroy();
        this.disposeGraph();
        this.quality.destroy();
        this.closeDetails();
        this.document.removeEventListener('keydown', this.onKeydown, true);
        this.window.removeEventListener('pagehide', this.pagehide);
        this.root.remove();
        this.response = null;
    }
}
