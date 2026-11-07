import { escapeHtml } from './utils.js';
import { isRunFinished } from './launch-api.js';
import { openLaunchTerminal } from './launch-terminal.js';
import { showReplayModal } from './session-viewer.js';
import { mountLaunchEvidence } from './launch-evidence.js';

export const runHistorySection =
    () => `<section class="board-block board-activity" aria-label="Run history">
    <h3 class="board-block-label">Run history</h3>
    <p class="board-editor-muted">Runs in this project. Process status and check or review results are reported separately.</p>
    <button type="button" class="btn btn-sm btn-outline-secondary" data-run-refresh>Refresh</button>
    <div data-run-list><p class="board-editor-muted">No runs yet.</p></div>
    <p data-run-error role="alert"></p>
    <button type="button" class="btn btn-sm btn-outline-secondary" data-run-more hidden>Load earlier runs</button>
</section>`;

export function mountRunHistory(app, root, api, getId) {
    const abort = new AbortController();
    const list = root.querySelector('[data-run-list]'),
        more = root.querySelector('[data-run-more]');
    const error = root.querySelector('[data-run-error]');
    let cursor = null,
        loading = false,
        initialized = false;
    const signatures = new Map(),
        evidenceDisposers = new Map();
    const rows = new Map();
    const previews = new Set();
    const current = () => !abort.signal.aborted && root.isConnected;
    function render() {
        const open = new Set(
            [...list.querySelectorAll('details[open]')].map(
                (node) => node.dataset.runId
            )
        );
        if (rows.size && !list.querySelector('[data-history-run]'))
            list.replaceChildren();
        let position = list.firstElementChild;
        for (const run of [...rows.values()].sort(
            (a, b) => b.sequence - a.sequence
        )) {
            const signature = JSON.stringify(run);
            let node = list.querySelector(
                `[data-history-run="${CSS.escape(run.id)}"]`
            );
            if (signatures.get(run.id) !== signature || !node) {
                const config =
                    run.snapshot?.preset || run.snapshot?.action || {};
                const markup = `<article class="launch-run" data-history-run="${escapeHtml(run.id)}">
                <strong>${escapeHtml(new Date(run.queuedUtc).toLocaleString())}</strong>
                <p>Process: <strong>${escapeHtml(run.status)}</strong> · Result: <strong>${escapeHtml(run.verdict || 'Not reported')}</strong>${run.exitCode == null ? '' : ` · Exit ${Number(run.exitCode)}`}</p>
                ${run.error ? `<p class="text-danger">${escapeHtml(run.error)}</p>` : ''}
                <div class="launch-run-actions">
                    ${run.tabId && !isRunFinished(run) ? `<button class="btn btn-sm btn-outline-secondary" data-run-open="${escapeHtml(run.id)}">Open terminal</button>` : ''}
                    ${run.sessionId ? `<button class="btn btn-sm btn-outline-secondary" data-run-replay="${escapeHtml(run.id)}">Recorded terminal</button>` : ''}
                    ${run.evidenceId ? `<button class="btn btn-sm btn-outline-secondary" data-run-evidence="${escapeHtml(run.id)}">${run.snapshot?.action ? 'Check result' : 'Review result'}</button>` : ''}
                    <button class="btn btn-sm btn-outline-secondary" data-run-action="${isRunFinished(run) ? 'retry' : 'cancel'}" data-run-id="${escapeHtml(run.id)}" ${run.cancelRequested ? 'disabled' : ''}>${isRunFinished(run) ? 'Retry original configuration' : run.cancelRequested ? 'Cancellation requested' : 'Cancel run'}</button>
                </div>
                <details data-run-id="${escapeHtml(run.id)}"${open.has(run.id) ? ' open' : ''}><summary>Run configuration · revision ${Number(config.revision) || 0}</summary>
                    <pre class="board-code">${escapeHtml(JSON.stringify({ definition: config, context: run.snapshot?.context, resolvedPrompt: run.resolvedPrompt }, null, 2))}</pre>
                </details><div data-run-result="${escapeHtml(run.id)}"></div>
            </article>`;
                const template = document.createElement('template');
                template.innerHTML = markup;
                const replacement = template.content.firstElementChild;
                const active = node?.contains(document.activeElement)
                    ? document.activeElement
                    : null;
                // Keep mounted reports/viewers intact when only process status changes.
                const existingResult = node?.querySelector('[data-run-result]');
                if (existingResult)
                    replacement
                        .querySelector('[data-run-result]')
                        .replaceWith(existingResult);
                if (node) {
                    if (position === node) position = replacement;
                    node.replaceWith(replacement);
                }
                node = replacement;
                signatures.set(run.id, signature);
                if (active)
                    (node.contains(active)
                        ? active
                        : [...node.querySelectorAll('button,summary')].find(
                              (button) =>
                                  button.tagName === active.tagName &&
                                  Object.entries(active.dataset).every(
                                      ([key, value]) =>
                                          button.dataset[key] === value
                                  )
                          )
                    )?.focus({ preventScroll: true });
            }
            if (node !== position) list.insertBefore(node, position);
            position = node.nextElementSibling;
        }
        if (!rows.size)
            list.innerHTML = '<p class="board-editor-muted">No runs yet.</p>';
        more.hidden = cursor == null;
    }
    async function refresh(older = false) {
        if (!getId() || loading || !current()) return;
        loading = true;
        try {
            const hadRows = rows.size > 0;
            let page = await api.runs(getId(), older ? cursor : null, {
                signal: abort.signal
            });
            const fetched = [...page.runs];
            // A busy definition can gain more than one page between polls. Bridge
            // those pages before reconnecting to the history already displayed.
            while (
                !older &&
                hadRows &&
                page.nextCursor != null &&
                page.runs.length &&
                page.runs.every((run) => !rows.has(run.id))
            ) {
                page = await api.runs(getId(), page.nextCursor, {
                    signal: abort.signal
                });
                fetched.push(...page.runs);
            }
            if (!current()) return;
            const fresh = new Set(fetched.map((run) => run.id));
            const activeOlder = [...rows.values()].filter(
                (run) => !fresh.has(run.id) && !isRunFinished(run)
            );
            const updates = await Promise.all(
                activeOlder.map((run) =>
                    api.call(
                        `runs/${encodeURIComponent(run.id)}`,
                        'GET',
                        null,
                        { signal: abort.signal }
                    )
                )
            );
            if (!current()) return;
            [...fetched, ...updates].forEach((run) => rows.set(run.id, run));
            if (older || !initialized || !hadRows) cursor = page.nextCursor;
            initialized = true;
            error.textContent = '';
            render();
        } catch (failure) {
            if (current())
                error.textContent =
                    failure.message || 'Could not load runs. Try Refresh.';
        } finally {
            loading = false;
        }
    }
    root.addEventListener(
        'click',
        async (event) => {
            const target = event.target.closest('button');
            if (!target) return;
            if (target.hasAttribute('data-run-refresh')) return void refresh();
            if (target.hasAttribute('data-run-more')) return void refresh(true);
            const id =
                target.dataset.runId ||
                target.dataset.runReplay ||
                target.dataset.runEvidence ||
                target.dataset.runOpen;
            if (!id || !rows.has(id)) return;
            target.disabled = true;
            try {
                if (target.dataset.runOpen) {
                    await openLaunchTerminal(app, api, rows.get(id), {
                        signal: abort.signal
                    });
                } else if (target.dataset.runReplay) {
                    const viewer = await showReplayModal(
                        rows.get(id).sessionId
                    );
                    if (!current()) viewer.close();
                    else previews.add(viewer.close);
                } else if (target.dataset.runEvidence) {
                    evidenceDisposers.get(id)?.();
                    const node = root.querySelector(
                        `[data-run-result="${CSS.escape(id)}"]`
                    );
                    evidenceDisposers.set(
                        id,
                        mountLaunchEvidence(
                            app,
                            node,
                            api,
                            rows.get(id),
                            abort.signal
                        )
                    );
                } else {
                    await api.runAction(id, target.dataset.runAction);
                    await refresh();
                }
            } catch (failure) {
                if (current()) error.textContent = failure.message;
            } finally {
                if (current()) target.disabled = false;
            }
        },
        { signal: abort.signal }
    );
    const timer = setInterval(() => {
        if (!app.isUiHidden?.()) void refresh();
    }, 10000);
    const visible = app.onUiVisible?.(() => void refresh());
    void refresh();
    return {
        refresh,
        dispose() {
            abort.abort();
            clearInterval(timer);
            visible?.();
            previews.forEach((close) => close());
            evidenceDisposers.forEach((dispose) => dispose());
        }
    };
}
