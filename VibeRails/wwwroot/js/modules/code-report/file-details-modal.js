import { escapeHtml as esc } from './vendor/quality/report-model.js';
import { fileFindings } from './findings.js';
import { QualitySourceEditor } from './source-editor.js';
import { buildFileQualityFixPrompt } from './file-fix-prompt.js';
import { launchProjectHealthFix, mountProjectHealthFixPickers } from '../project-health-fix-launcher.js';

const findingBody = item => [
    item.meaning ? `<p class="finding-meaning">${esc(item.meaning)}</p>` : '',
    item.level !== 'ok' && item.fix ? `<p class="finding-fix"><strong>How to fix.</strong> ${esc(item.fix)}</p>` : '',
    `<p class="finding-limits">${esc(item.limitsText)}</p>`,
    item.name === 'npath_complexity' && item.value >= 1_000_000_000
        ? '<p class="finding-limits">NPath is an estimate capped at 1 billion paths, not an exact count.</p>' : ''
].join('');
let nextId = 0;

/** A nested native dialog: the map and any parent Board editor retain their state. */
export function openFileDetailsModal({ app, file, capturedUtc, preferredName, sourceScope = null,
    workingDirectory = null, onIgnore = null, onLocate, onClose, launchRequest, onReturn }) {
    const host = document.getElementById('modal-container');
    if (!host) return null;
    const findings = fileFindings(file);
    const items = findings.measurements;
    let selected = items.find(item => item.name === preferredName) || findings.problems[0] || items[0];
    const prefix = `quality-file-${++nextId}`;
    const row = (item, index) => `<div class="finding" data-level="${item.level}" data-metric="${esc(item.name)}">
        <button type="button" class="finding-toggle" data-finding="${index}" aria-expanded="false" aria-controls="${prefix}-${index}">
            <span class="finding-head">${item.level === 'ok' ? '' : `<span class="finding-level">${esc(item.levelLabel)}</span>`}
                <span class="finding-name">${esc(item.label)}</span><strong class="finding-value">${esc(item.valueText)}</strong></span>
            <small class="finding-where">${esc(item.where)} · ${esc(item.limitText)}</small>
        </button><div class="finding-body" id="${prefix}-${index}" hidden></div></div>`;
    const date = new Date(capturedUtc);
    const layer = document.createElement('div');
    layer.className = 'code-report quality-file-layer';
    layer.innerHTML = `<dialog class="quality-file-modal" role="dialog" aria-modal="true" aria-labelledby="${prefix}-title">
        <header class="quality-file-header">
            <div><h2 id="${prefix}-title">File quality · ${esc(String(file.file).split(/[\\/]/).pop())}</h2>
                <div class="details-path">${esc(file.file)}</div></div>
            <button type="button" class="btn btn-secondary" data-quality-close autofocus aria-label="Close file quality">Close</button>
        </header>
        <div class="quality-file-workspace">
            <section class="quality-source" aria-label="Source code">
                <div class="quality-source-toolbar"><div role="group" aria-label="Source version">
                    ${sourceScope ? '<button type="button" class="btn btn-sm btn-secondary" data-source-mode="file">Current file</button>' : ''}
                    <button type="button" class="btn btn-sm btn-secondary" data-source-mode="excerpt">Captured excerpt</button>
                </div><span data-source-status role="status"></span></div>
                <div class="quality-source-editor" data-source-editor></div>
                <p class="quality-source-note">${sourceScope ? 'Saved finding locations may differ from current source. Select a finding to jump to its line.' : 'Captured source from the saved scan. Select a finding to view its excerpt.'}</p>
            </section>
            <aside class="quality-file-sidebar" aria-label="Findings and actions">
                <div class="quality-file-findings">
                    <div class="file-verdict" data-tone="${findings.tone}"><span class="verdict-rating">${esc(findings.ratingLabel)}</span>
                        <span class="verdict-summary"><strong>${esc(findings.headline)}</strong><span>Health ${esc(findings.healthText)} / 100</span></span></div>
                    ${findings.hotspot ? `<p class="verdict-hotspot">${esc(findings.hotspot)}</p>` : ''}
                    ${findings.problems.length ? `<h3 class="findings-heading">What to fix first</h3><div class="finding-list">${findings.problems.map(row).join('')}</div>`
                        : `<p class="detail-note">${items.length ? 'Every measurement is within its limits.' : 'No measurements were saved for this file.'}</p>`}
                    ${findings.within.length ? `<details class="within-limits"${selected?.level === 'ok' ? ' open' : ''}><summary>Within limits <span class="file-count">${findings.within.length}</span></summary>
                        <div class="finding-list">${findings.within.map((item, i) => row(item, findings.problems.length + i)).join('')}</div></details>` : ''}
                    <p class="detail-note details-provenance">${Number.isNaN(date.valueOf()) ? 'Saved report' : `Report captured ${esc(date.toLocaleString())}`}.</p>
                </div>
                <div class="quality-file-actions">
                    ${workingDirectory ? `<label class="form-label" for="${prefix}-agent">Fix with</label>
                        <select id="${prefix}-agent" data-project-health-fix-agent></select>
                        <button type="button" class="btn btn-primary" data-quality-fix>Open fix terminal</button>
                        <p class="quality-action-help">Sends this file’s saved findings to the selected agent.</p>` : ''}
                    ${onIgnore ? '<button type="button" class="btn btn-secondary" data-quality-ignore>Ignore file</button>' : ''}
                    <button type="button" class="btn btn-link" data-quality-locate>Show in code explorer</button>
                    <p data-quality-action-status role="status" hidden></p>
                </div>
            </aside>
        </div>
    </dialog>`;
    const previousFocus = document.activeElement;
    host.append(layer);
    const dialog = layer.querySelector('dialog');
    let closed = false;
    let replaced = false;
    let busy = false;
    let source;
    let disposePicker;
    const observer = new MutationObserver(() => { if (!layer.isConnected) close(false, true); });
    function close(restoreFocus = true, replacement = false) {
        if (closed) return;
        closed = true;
        replaced = replacement;
        // A preset's prompt-values dialog may replace this reader while the launch
        // is still being prepared. Cancel/failure returns here; ordinary close cancels polling.
        if (!replacement) launchRequest?.abort();
        observer.disconnect();
        source?.destroy();
        disposePicker?.();
        dialog.close();
        layer.remove();
        if (restoreFocus && previousFocus?.isConnected) previousFocus.focus({ preventScroll: true });
        onClose?.();
    }
    dialog.addEventListener('cancel', event => { event.preventDefault(); close(); });
    // Bubble phase gives Monaco Find and the shared picker first use of Escape.
    dialog.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || event.defaultPrevented || event.target.closest('.monaco-editor')) return;
        event.preventDefault();
        event.stopPropagation();
        close();
    });
    layer.querySelector('[data-quality-close]').addEventListener('click', () => close());
    layer.querySelector('[data-quality-locate]').addEventListener('click', () => { close(); onLocate?.(); });
    layer.querySelector('[data-quality-ignore]')?.addEventListener('click', () => { close(); onIgnore(); });
    layer.querySelector('[data-quality-fix]')?.addEventListener('click', async event => {
        if (busy) return;
        busy = true;
        const button = event.currentTarget;
        button.disabled = true;
        button.textContent = 'Opening…';
        const status = layer.querySelector('[data-quality-action-status]');
        status.hidden = true;
        const opened = await launchProjectHealthFix(app, {
            title: `Fix quality · ${String(file.file).split(/[\\/]/).pop()}`,
            prompt: buildFileQualityFixPrompt(file, capturedUtc, selected), workingDirectory,
            signal: launchRequest?.signal,
            selectionValue: layer.querySelector('[data-project-health-fix-agent]').value
        });
        if (closed) {
            if (replaced && !opened && !launchRequest?.signal.aborted) {
                // The prompt dialog settles in its capture-phase close handler.
                // Let its remaining click handlers finish clearing the modal host
                // before restoring the reader. Navigation/new selections still cancel it.
                setTimeout(() => {
                    if (!launchRequest?.signal.aborted) onReturn?.();
                }, 0);
            }
            return;
        }
        if (opened) { close(false); return; }
        busy = false;
        button.disabled = false;
        button.textContent = 'Open fix terminal';
        status.textContent = 'The terminal did not open. Check the selected agent and try again.';
        status.hidden = false;
    });
    dialog.showModal();
    observer.observe(host, { childList: true });
    if (workingDirectory) disposePicker = mountProjectHealthFixPickers(app, layer);
    source = new QualitySourceEditor(layer, app, file.file, sourceScope, () => close());
    const toggles = [...layer.querySelectorAll('[data-finding]')];
    function select(button) {
        selected = items[Number(button.dataset.finding)];
        const wasOpen = button.getAttribute('aria-expanded') === 'true';
        for (const toggle of toggles) {
            const expanded = toggle === button && !wasOpen;
            toggle.setAttribute('aria-expanded', String(expanded));
            const body = layer.querySelector(`#${toggle.getAttribute('aria-controls')}`);
            body.hidden = !expanded;
            body.innerHTML = expanded ? findingBody(selected) : '';
        }
        source.select(selected);
    }
    toggles.forEach(button => button.addEventListener('click', () => select(button)));
    if (selected) {
        const button = toggles[items.indexOf(selected)];
        select(button);
        if (preferredName) button.scrollIntoView({ block: 'nearest' });
    }
    return { close, ready: source.ready };
}
