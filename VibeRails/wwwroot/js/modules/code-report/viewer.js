import { mountCodeAtlas } from './vendor/atlas/code-atlas.mjs';
import { mountQualityReport } from './vendor/quality/quality-report.js';
import { escapeHtml as esc, formatNumber, healthFromConcern } from './vendor/quality/report-model.js';
import { readReportTheme, observeReportTheme } from './theme-sync.js';
import { enhanceRadar } from './radar-interactions.js';
import { isConfirmDialogOpen } from '../utils.js';
import { openDiffModal } from '../diff-modal.js';
import { reportPath, reportAnnotations } from './graph-metrics.js';
import { openFileDetailsModal } from './file-details-modal.js';

export { reportPath } from './graph-metrics.js';

let instanceId = 0;
// The repository map does not depend on the scan, so it is requested on its own and shared by every
// viewer on the page. A request belongs to the scan it is first shown with (`tag`); until then it
// is unclaimed, which is how the map asked for when Quality was opened, or when a scan started,
// becomes that scan's map. Re-entering the page replays the saved scan and therefore the same map,
// without capturing the repository again; a new scan asks again, and the server answers an
// unchanged tree with the capture it already had.
const UNCLAIMED = Symbol('unclaimed');
const PRIORITY_LIMIT = 1000;
// Omission codes that mean "an eligible file was left out by a budget", which priorities can fix.
const SELECTION_LIMITS = new Set(['file-limit', 'file-node-limit']);
let graphCache = null;

function requestGraph(app, files, tag = UNCLAIMED) {
    // Nobody follows a superseded request any more; one that is still running stops costing the server.
    if (graphCache && !graphCache.graph) graphCache.controller.abort();
    const controller = new AbortController();
    const entry = { files, tag, controller, graph: null, layout: null };
    entry.promise = app.apiCall('/api/v1/code-analyzer/graph', 'POST', { files },
        { showLoading: false, signal: controller.signal, preferErrorResponseMessage: true })
        .then(graph => { entry.graph = graph; return graph; });
    // A failed request is never replayed: the next viewer asks again.
    entry.promise.catch(() => { if (graphCache === entry) graphCache = null; });
    graphCache = entry;
    return entry;
}

// The server reuses a capture while the tree is unchanged, so an equal capture time is the same map.
const sameCapture = (shown, next) => !!shown && !!next && typeof next.capturedUtc === 'string'
    && shown.capturedUtc === next.capturedUtc && shown.nodes?.length === next.nodes?.length
    && shown.edges?.length === next.edges?.length;

/**
 * Asks for the repository map before a viewer exists, alongside the page's own first requests, so
 * opening Quality does not wait for the scan (or anything else) before the map starts loading.
 * Does nothing while a map is already known or on its way.
 */
export function prefetchCodeGraph(app) {
    if (!graphCache) requestGraph(app, []);
}
const STATUS_LABELS = { modified: 'Modified', added: 'Added', deleted: 'Deleted', renamed: 'Renamed', copied: 'Copied',
    untracked: 'Untracked', conflicted: 'Conflicted', typechange: 'Type changed' };
const STATUS_CODES = { modified: 'M', added: 'A', deleted: 'D', renamed: 'R', copied: 'C', untracked: '?', conflicted: 'U', typechange: 'T' };
// Sidebar scrollers that keep the wheel inside the panel; innermost first when nested.
const SIDEBAR_SCROLLERS = '.code-excerpt,.qr-files,.qr-changes,.details-panel';
const basename = path => reportPath(path).split('/').pop();
/** A host-owned report view. Networking uses app.apiCall; no fixture or global route state. */
export class CodeReportViewer {
    constructor(host, app, options = {}) {
        this.options = options;
        this.host = host;
        this.app = app;
        this.document = host.ownerDocument;
        this.window = this.document.defaultView;
        this.generation = 0;
        this.destroyed = false;
        this.activeList = 'report';
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
        // Structural entities without a saved file use the inline sidebar; file reports use Monaco.
        this.details = this.root.querySelector('.details-panel');
        this.root.querySelector('[data-close-details]').addEventListener('click', () => this.closeDetails(true));
        // Captured so the app's document-level Escape (Back) does not also leave Project health.
        this.onKeydown = event => {
            if (this.fileModal || isConfirmDialogOpen()) return;
            if (event.key !== 'Escape' || this.details.hidden || event.defaultPrevented) return;
            event.preventDefault();
            event.stopPropagation();
            this.closeDetails(true);
        };
        this.document.addEventListener('keydown', this.onKeydown, true);
        // Let the opening map tour yield immediately to the user's own navigation.
        // Focusing the sandboxed Atlas frame blurs the host window.
        this.onInteraction = () => this.cancelAutoFocus();
        for (const type of ['pointerdown', 'keydown', 'wheel'])
            this.document.addEventListener(type, this.onInteraction, { capture: true, passive: true });
        this.window.addEventListener('blur', this.onInteraction);
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
        // The sidebar's lists keep the wheel (VIBE-71): a short list, or a list at its end, would
        // otherwise hand the wheel to the page and scroll the map out from under the pointer. A
        // nested scroller (a captured excerpt inside the details) still scrolls first.
        this.sidebar = this.root.querySelector('.report-sidebar');
        this.onWheel = event => {
            if (event.defaultPrevented || !(event.target instanceof Element)) return;
            const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? this.window.innerHeight : 1;
            const delta = event.deltaY * unit;
            let guarded = false;
            for (let node = event.target.closest(SIDEBAR_SCROLLERS); node; node = node.parentElement?.closest(SIDEBAR_SCROLLERS)) {
                guarded = true;
                if (delta && node.scrollHeight > node.clientHeight + 1
                    && (delta < 0 ? node.scrollTop > 0 : node.scrollTop + node.clientHeight < node.scrollHeight - 1)) return;
            }
            if (guarded) event.preventDefault();
        };
        this.sidebar.addEventListener('wheel', this.onWheel, { passive: false });
        // The map fills the viewport under the page header (VIBE-71): the layout is as tall as what
        // is left after the chrome above and below it, never under 600px nor over 1400px. The chrome
        // below is the rest of the container plus whatever visible element follows it, measured
        // from edges that do not depend on the layout's own height, so the observer settles after
        // one pass. The stylesheet's clamp is the fallback until this measures (the Board card
        // ignores the variable).
        this.fitHeight = () => {
            if (this.destroyed) return;
            const layout = this.root.querySelector('.code-layout');
            const container = this.host.closest('.main-container') || this.document.body;
            const rect = layout.getBoundingClientRect();
            if (!rect.height) return;
            let below = Math.max(0, container.getBoundingClientRect().bottom - rect.bottom);
            for (let next = container.nextElementSibling; next; next = next.nextElementSibling) {
                const box = next.getBoundingClientRect();
                if (!box.height) continue; // A hidden sibling (the app's footer) still reports its margins.
                const style = this.window.getComputedStyle(next);
                below += box.height + (parseFloat(style.marginTop) || 0) + (parseFloat(style.marginBottom) || 0);
            }
            const top = rect.top + (this.window.scrollY || 0);
            const height = Math.round(Math.min(1400, Math.max(600, this.window.innerHeight - top - below)));
            if (Math.abs(height - (this.layoutHeight || 0)) <= 1) return;
            this.layoutHeight = height;
            this.root.style.setProperty('--code-report-height', `${height}px`);
        };
        this.window.addEventListener('resize', this.fitHeight);
        this.resizeObserver = typeof this.window.ResizeObserver === 'function' ? new this.window.ResizeObserver(this.fitHeight) : null;
        this.resizeObserver?.observe(this.document.body);
        this.fitHeight();
        // The map has its own lifetime beside the report's: `graphEpoch` counts the request being
        // followed, `generation` the report being shown. `ready` settles when the followed map is
        // mounted, or has failed.
        this.graphEpoch = 0;
        this.graphEntry = null;
        this.reportFiles = null;
        this.reportPaths = [];
        this.ready = Promise.resolve();
        // A map requested while the page was still loading starts mounting now, ahead of the scan.
        if (graphCache?.tag === UNCLAIMED) this.follow(graphCache);
    }

    // The report's own state: everything a new scan result replaces. The map is not part of it.
    resetReport() {
        ++this.generation;
        this.radar?.destroy();
        this.radar = null;
        this.closeDetails();
        // The pending opening selection belongs to the report being replaced. Whether the user has
        // already taken over (they can use the map while a scan runs) is kept until the next scan starts.
        this.window.clearTimeout(this.autoFocusTimer);
        this.autoFocusTimer = null;
        this.changesRequest?.abort();
        this.changesRequest = null;
        this.changes = undefined;
        this.quality.setLoading();
    }

    disposeMap() {
        this.themes?.destroy();
        this.themes = null;
        this.atlas?.destroy();
        this.atlas = null;
        this.graph = null;
        this.overlayApplied = false;
        this.root.querySelector('[data-map-diagnostics]').hidden = true;
        this.root.querySelector('[data-graph-options]').hidden = true;
    }

    /** A scan is starting. The map on screen stays; the tree is asked for again alongside the scan. */
    setLoading() {
        if (this.destroyed) return;
        this.resetReport();
        this.autoFocusCancelled = false;
        // A request nobody has been shown yet (asked for with the page, or still on its way) already
        // describes the tree as this scan will see it.
        const known = graphCache;
        if (known && !known.graph) known.tag = UNCLAIMED;
        this.follow(known?.tag === UNCLAIMED ? known : requestGraph(this.app, known?.files ?? []));
    }

    setError(message) {
        if (this.destroyed) return;
        this.resetReport();
        // A failed scan has no results: nothing of an earlier report may read as current.
        this.response = null;
        this.reportFiles = null;
        this.reportPaths = [];
        this.quality.setError(message || 'The code report could not be loaded.');
        // The graph remains useful when analysis fails; its failure is independent.
        this.claimGraph(null, []);
        this.annotate();
        void this.loadChanges(this.generation);
    }

    async setResponse(response) {
        if (this.destroyed) return;
        this.resetReport();
        const generation = this.generation;
        this.response = structuredClone(response);
        // A successful empty changeset has no report. Preserve its missing score.
        if (response?.success !== false && !response?.report && response?.analyzedFileCount === 0)
            this.response.report = { files: [], overview: [], scorecard: [] };
        const reportFiles = this.response?.report?.files;
        this.reportPaths = Array.isArray(reportFiles) ? reportFiles.map(file => reportPath(file?.file)).filter(Boolean) : [];
        // Only a report accepted by the quality viewer can supply map measurements.
        const validReport = this.quality.setResponse(this.response);
        this.reportFiles = validReport ? this.response.report.files : null;
        this.claimGraph(this.response?.startedUtc ?? null, this.reportPaths);
        void this.loadChanges(generation);
        if (validReport) {
            this.composeFiles();
            this.radar = enhanceRadar(this.qualityHost.querySelector('.qr'), this.response, {
                onSelect: category => this.showCategory(category)
            });
        }
        // A map that is already up takes the results now; one still on its way takes them when it mounts.
        this.annotate();
        this.syncChangedFiles();
        this.prioritizeReportFiles();
        // Asking again with priorities replaces `ready`; the opening selection waits for the map that stays.
        for (let ready; ready !== this.ready && this.isCurrent(generation);) {
            ready = this.ready;
            await ready;
        }
        if (this.isCurrent(generation)) this.scheduleAutoFocus(generation);
    }

    isCurrent(generation) { return !this.destroyed && generation === this.generation; }

    // The map a report is shown with: the one asked for alongside it, the one it was shown with
    // before, or a new capture that puts the report's own files first.
    claimGraph(tag, files) {
        const known = graphCache;
        if (known && (known.tag === UNCLAIMED || known.tag === tag)) {
            known.tag = tag;
            this.follow(known);
        } else this.follow(requestGraph(this.app, files.slice(0, PRIORITY_LIMIT), tag));
    }

    // Follows one request to a mounted map. A later follow, or teardown, supersedes it.
    follow(entry) {
        if (this.graphEntry === entry) return this.ready;
        this.graphEntry = entry;
        const epoch = ++this.graphEpoch;
        const current = () => !this.destroyed && epoch === this.graphEpoch;
        return this.ready = (async () => {
            let graph;
            try { graph = await entry.promise; }
            catch (error) {
                if (current() && !entry.controller.signal.aborted) this.showMapError(error);
                return;
            }
            if (current()) await this.showGraph(entry, graph);
        })();
    }

    showMapError(error) {
        this.disposeMap();
        this.mapHost.innerHTML = `<p class="load-error" role="alert">Code map unavailable: ${esc(error.message)} Saved report details are still available.</p>`;
    }

    // The scan's results for the files on a mounted map: their measurements and the inspector's
    // summary. An empty or failed report clears whatever an earlier one put there.
    annotate() {
        const atlas = this.atlas;
        if (!atlas || !this.graph) return;
        const entries = this.reportFiles ? reportAnnotations(this.graph, this.reportFiles, this.response?.startedUtc) : [];
        if (!entries.length && !this.overlayApplied) return;
        this.overlayApplied = entries.length > 0;
        atlas.setAnnotations(entries).catch(error => { if (this.atlas === atlas) this.notify(error.message); });
    }

    // The first request cannot name the report's files: the scan is still running when it is sent.
    // When a budget left some of them off the map, ask once more with them as priorities.
    prioritizeReportFiles() {
        const entry = this.graphEntry, graph = this.graph;
        if (!entry || !graph || entry.graph !== graph || !this.reportPaths.length) return;
        if (!(graph.diagnostics?.omissions || []).some(item => SELECTION_LIMITS.has(item?.code) && item.count > 0)) return;
        const mapped = this.mappedFiles();
        if (!this.reportPaths.some(path => !mapped.has(path) && !entry.files.includes(path))) return;
        this.follow(requestGraph(this.app, this.reportPaths.slice(0, PRIORITY_LIMIT), entry.tag));
    }

    cancelAutoFocus() {
        this.window.clearTimeout(this.autoFocusTimer);
        this.autoFocusTimer = null;
        this.autoFocusCancelled = true;
    }

    scheduleAutoFocus(generation) {
        if (this.autoFocusCancelled || !this.atlas || this.activeList !== 'report') return;
        // Atlas readiness follows layout and the loader fade. Leave the overview visible
        // for 1.5 seconds before using the same animated selection as a Report files click.
        this.autoFocusTimer = this.window.setTimeout(() => {
            this.autoFocusTimer = null;
            if (!this.isCurrent(generation) || this.autoFocusCancelled || this.document.hidden
                || !this.root.isConnected || this.fileModal || !this.details.hidden || this.activeList !== 'report') return;
            const first = this.qualityHost.querySelector('.qr-file');
            const path = first?.dataset.path;
            // Saved files may no longer exist in the map. Don't open a details panel or
            // show an unsolicited missing-file toast in place of the opening animation.
            if (path && !first.disabled && this.mappedFiles().has(path)) void this.focusFile(path);
        }, 1500);
    }

    async showGraph(entry, graph) {
        // An unchanged tree answers a new scan with the capture already on screen. Keep that map, with
        // its camera and selection; its settled layout belongs to the new request as well.
        if (this.atlas && sameCapture(this.graph, graph)) {
            const kept = this.atlas;
            entry.layout ??= this.mapEntry?.layout ?? null;
            this.graph = graph;
            this.mapEntry = entry;
            // `ready` promises a usable map. A kept map can still be starting: the same capture
            // answers a second request while the first one's frame is laying itself out.
            try { await kept.ready; } catch { return; }
            if (this.destroyed || this.atlas !== kept) return;
            this.prioritizeReportFiles();
            return;
        }
        this.disposeMap();
        this.graph = graph;
        this.mapEntry = entry;
        this.mapHost.replaceChildren();
        let atlas;
        try {
            // The graph is mounted as captured; the scan's results arrive through annotate(), whether the
            // report is already here or still on its way. A capture this page has laid out before
            // brings its positions, so returning to the page does not run the layout solver again.
            atlas = this.atlas = mountCodeAtlas(this.mapHost, {
                graph, theme: readReportTheme(this.root), cspNonce: this.window.__viberails_NONCE__,
                changedFiles: this.highlightedPaths(), highlightChanges: true,
                ...(entry.layout ? { layout: entry.layout } : {}),
                onOpenDetails: details => { if (this.atlas === atlas) this.showEntity(details); },
                onError: error => { if (this.atlas === atlas) this.notify(error.message); }
            });
            await atlas.ready;
        } catch (error) {
            // A map replaced or torn down while it was starting is not a failure to report.
            if (!this.destroyed && this.atlas === atlas) this.showMapError(error);
            return;
        }
        if (this.destroyed || this.atlas !== atlas) return;
        this.themes = observeReportTheme(this.root, theme => atlas.setTheme(theme), error => this.notify(error.message));
        this.syncChangedFiles();
        this.annotate();
        // The change rows' locate buttons depend on the graph; the list often answers first on a large repository.
        this.composeChanges();
        this.renderDiagnostics(graph);
        if (!this.mapEntry.layout) {
            atlas.getLayout().then(layout => { if (layout && this.atlas === atlas) this.mapEntry.layout = layout; }, () => {});
        }
        this.prioritizeReportFiles();
    }

    renderDiagnostics(graph) {
        const diagnostics = graph.diagnostics;
        if (!diagnostics) return;
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

    // Until git's change list answers, the report's own files stand in for it.
    highlightedPaths() { return this.changedPaths() ?? this.reportPaths; }

    syncChangedFiles() {
        const atlas = this.atlas;
        if (!atlas) return;
        atlas.setChangedFiles(this.highlightedPaths().slice(0, 10000))
            .catch(error => { if (this.atlas === atlas) this.notify(error.message); });
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
        this.cancelAutoFocus();
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

    closeDetails(restoreFocus = false) {
        this.fileFixRequest?.abort();
        this.fileFixRequest = null;
        this.fileModal?.close(restoreFocus);
        this.fileModal = null;
        if (this.details.hidden) return;
        this.details.hidden = true;
        this.qualityHost.hidden = false;
        if (restoreFocus && this.detailsReturn?.isConnected) this.detailsReturn.focus({ preventScroll: true });
        this.detailsReturn = null;
    }

    showFile(file, preferredName) {
        if (!file || this.destroyed) return;
        this.closeDetails();
        this.fileFixRequest = new AbortController();
        const generation = this.generation;
        const reopen = () => {
            if (this.isCurrent(generation)) this.showFile(file, preferredName);
        };
        this.fileModal = openFileDetailsModal({
            app: this.app, file, preferredName, capturedUtc: this.response?.startedUtc,
            sourceScope: this.options.getSourceScope?.(),
            workingDirectory: this.options.getWorkingDirectory?.(),
            launchRequest: this.fileFixRequest, onReturn: reopen,
            onIgnore: this.options.onIgnoreFile ? () => this.options.onIgnoreFile(file, reopen) : null,
            onLocate: () => this.focusFile(file.file),
            onClose: () => { this.fileModal = null; }
        });
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
        const isFile = details.node.kind === 'file';
        this.openDetails(details.node.name, String(details.node.kind).toUpperCase(),
            `<div class="details-path">${esc(details.node.path || 'Repository structure')}</div>
            <p class="detail-note">${isFile ? 'Not in this scan, so it has no quality results.' : 'Quality is measured per file. Select a file to see its results.'}</p>
            ${children.length ? `<h3>Contains</h3><div class="metric-list">${children.map(node => `<div class="metric-detail"><span>${esc(node.name)}</span><strong>${esc(node.kind)}</strong></div>`).join('')}</div>` : ''}
            ${evidence.length ? `<h3>Source references</h3>${evidence.map(relationship => `<p class="detail-note">${esc(relationship.edge.evidence)}</p>`).join('')}` : ''}`,
            () => this.focusFile(details.node.path, details.node.id));
    }

    notify(message) { if (!this.destroyed) this.app.showToast('Code report', message, 'info'); }

    destroy() {
        if (this.destroyed) return;
        this.destroyed = true;
        ++this.generation;
        ++this.graphEpoch;
        this.radar?.destroy();
        this.cancelAutoFocus();
        this.changesRequest?.abort();
        this.changesRequest = null;
        // The shared map request is left running: it is this page's, not this viewer's, and coming
        // back to Quality picks it up instead of capturing the repository a second time.
        this.disposeMap();
        this.graphEntry = null;
        this.mapEntry = null;
        this.quality.destroy();
        this.closeDetails();
        this.document.removeEventListener('keydown', this.onKeydown, true);
        for (const type of ['pointerdown', 'keydown', 'wheel'])
            this.document.removeEventListener(type, this.onInteraction, true);
        this.window.removeEventListener('blur', this.onInteraction);
        this.window.removeEventListener('pagehide', this.pagehide);
        this.window.removeEventListener('resize', this.fitHeight);
        this.resizeObserver?.disconnect();
        this.sidebar?.removeEventListener('wheel', this.onWheel);
        this.root.remove();
        this.response = null;
    }
}
