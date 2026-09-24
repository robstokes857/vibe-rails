import { mountCodeAtlas } from './vendor/atlas/code-atlas.mjs';
import { mountQualityReport } from './vendor/quality/quality-report.js';
import { escapeHtml as esc, formatNumber, healthFromConcern } from './vendor/quality/report-model.js';
import { readReportTheme, observeReportTheme } from './theme-sync.js';
import { enhanceRadar } from './radar-interactions.js';

let instanceId = 0;
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
        const titleId = `code-report-details-${++instanceId}`;
        host.innerHTML = `<div class="code-report">
            <main aria-label="Interactive code report">
                <div class="code-layout">
                    <section class="graph-panel" aria-label="Interactive code explorer">
                        <div data-code-map><p class="load-error" role="status">Preparing repository map…</p></div>
                        <div class="graph-note" data-graph-note>Hover a domain to trace its connections. Select a report file to explore it in the map.</div>
                    </section>
                    <section class="report-sidebar" aria-label="Code health and report files"><div data-quality-report></div></section>
                </div>
            </main>
            <dialog class="details-dialog" aria-labelledby="${titleId}">
                <div class="dialog-header"><div><span class="eyebrow" data-details-kicker>CODE DETAILS</span><h2 id="${titleId}"></h2></div>
                    <button type="button" class="icon-button" data-close-details aria-label="Close details">✕</button></div>
                <div class="dialog-body" data-details-body></div><div class="dialog-actions" data-details-actions></div>
            </dialog>
        </div>`;
        this.root = host.querySelector('.code-report');
        this.mapHost = this.root.querySelector('[data-code-map]');
        this.qualityHost = this.root.querySelector('[data-quality-report]');
        this.dialog = this.root.querySelector('dialog');
        this.root.querySelector('[data-close-details]').addEventListener('click', () => this.dialog.close());
        // The app has a document-level Escape shortcut. Let this dialog own its Escape.
        this.dialog.addEventListener('keydown', event => {
            if (event.key === 'Escape') event.stopPropagation();
        });
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
        this.dialog.close();
        this.disposeGraph();
        this.quality.setLoading();
        this.mapHost.innerHTML = '<p class="load-error" role="status">Preparing repository map…</p>';
    }

    setError(message) {
        if (this.destroyed) return;
        this.setLoading();
        this.quality.setError(message || 'The code report could not be loaded.');
        // The graph remains useful when analysis fails; its failure is independent.
        this.ready = this.loadGraph([], this.generation);
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
        const request = this.request = new AbortController();
        try {
            const graph = await this.app.apiCall('/api/v1/code-analyzer/graph', 'POST',
                { files: files.slice(0, 1000) }, { showLoading: false, signal: request.signal, preferErrorResponseMessage: true });
            if (!this.isCurrent(generation)) return;
            this.graph = graph;
            this.mapHost.replaceChildren();
            const atlas = this.atlas = mountCodeAtlas(this.mapHost, {
                graph, theme: readReportTheme(this.root), cspNonce: this.window.__viberails_NONCE__,
                changedFiles: files, highlightChanges: false,
                onOpenDetails: details => { if (this.isCurrent(generation)) this.showEntity(details); },
                onError: error => { if (this.isCurrent(generation)) this.notify(error.message); }
            });
            await atlas.ready;
            if (!this.isCurrent(generation)) return;
            this.themes = observeReportTheme(this.root, theme => atlas.setTheme(theme), error => this.notify(error.message));
            const note = this.root.querySelector('[data-graph-note]');
            note.textContent = `${graph.truncated ? 'Bounded map · ' : ''}${graph.fileCount} source files. Hover a domain to trace source references. Select a report file to inspect it.`;
            note.title = graph.description || '';
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
        const heading = this.qualityHost.querySelector('.qr-files-section h3');
        heading.textContent = 'Report files';
        const count = this.document.createElement('span');
        count.className = 'file-count';
        count.textContent = String(rows.length);
        heading.append(count);
        if (!rows.length) files.querySelector('.qr-empty').textContent = 'No source files in this report.';
    }

    findFile(path) {
        const files = this.response?.report?.files;
        return Array.isArray(files) ? files.find(file => reportPath(file?.file) === reportPath(path)) : undefined;
    }

    async focusFile(path, nodeId) {
        const generation = this.generation;
        this.dialog.close();
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
        this.dialog.querySelector('h2').textContent = title;
        this.dialog.querySelector('[data-details-kicker]').textContent = kicker;
        this.dialog.querySelector('[data-details-body]').innerHTML = body;
        const actions = this.dialog.querySelector('[data-details-actions]');
        actions.replaceChildren();
        if (action) {
            const button = this.document.createElement('button');
            button.type = 'button';
            button.className = 'button primary';
            button.textContent = 'Show in code explorer';
            button.addEventListener('click', action);
            actions.append(button);
        }
        if (!this.dialog.open) this.dialog.showModal();
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
        this.dialog.querySelectorAll('[data-report-metric]').forEach(button => button.addEventListener('click', () => {
            this.showFile(file, metrics[Number(button.dataset.reportMetric)].name);
            this.dialog.querySelector(`[data-report-metric="${button.dataset.reportMetric}"]`)?.focus();
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
        this.dialog.close();
        this.window.removeEventListener('pagehide', this.pagehide);
        this.root.remove();
        this.response = null;
    }
}
