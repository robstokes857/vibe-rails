import { mountLaunchLane } from './launch-lane-editor.js';
import { escapeHtml } from './utils.js';
import { LaunchApi, isRunFinished } from './launch-api.js';

/** Lane selection drafts and per-card progress have independent lifetimes. */
export function mountLaneAgentBody({
    app,
    panel,
    column,
    signal,
    updateCount,
    position,
    close,
    openRunningAgent
}) {
    const host = panel.querySelector('[data-lane-agents-content]');
    const disposeLane = mountLaunchLane(app, host, column.id, (settings) => {
        updateCount(settings);
        position();
    });
    const abort = new AbortController(),
        api = new LaunchApi(app);
    const activity = panel.querySelector('[data-lane-running]');
    let loading = false,
        entries = [],
        markup = '',
        busy = false;
    const current = () => !abort.signal.aborted && panel.isConnected;
    const refresh = async () => {
        if (loading || busy || !current() || app.isUiHidden?.()) return;
        loading = true;
        try {
            entries = await api.call(
                `board/columns/${encodeURIComponent(column.id)}/launches/activity`,
                'GET',
                null,
                { signal: abort.signal }
            );
            if (!current()) return;
            const next = entries.length ?
                '<h3 class="h6">Card runs</h3>' +
                (entries
                    .map(
                        ({ entry, run, cardLabel, definitionName }) => `
                <article class="board-automation-run">
                    <strong>${escapeHtml(cardLabel)} · ${escapeHtml(definitionName)}</strong>
                    <span>Process: ${escapeHtml(run?.status || entry.processStatus)} · Result: ${escapeHtml(entry.verdict || run?.verdict || 'Not reported')}</span>
                    <span>${escapeHtml(entry.summary || run?.error || '')}</span>
                    <div class="launch-run-actions">
                        ${run?.sessionId ? `<button type="button" class="btn btn-sm btn-link" data-lane-running-id="${escapeHtml(entry.id)}">Open session</button>` : ''}
                        ${!entry.skipRequested && entry.verdict !== 'passed' ? `<button type="button" class="btn btn-sm btn-outline-secondary" data-lane-skip="${escapeHtml(entry.id)}">Stop and skip</button>` : ''}
                        ${run && isRunFinished(run) && !entry.skipRequested && (run.status !== 'succeeded' || (entry.verdict !== 'passed' && entry.gate !== 'process')) ? `<button type="button" class="btn btn-sm btn-outline-secondary" data-lane-retry="${escapeHtml(entry.id)}">Retry</button>` : ''}
                    </div>
                </article>`
                    )
                    .join('') ||
                    '') : '';
            if (markup !== next) activity.innerHTML = markup = next;
        } catch (error) {
            if (current()) { activity.textContent = error.message; markup = ''; }
        } finally {
            loading = false;
        }
    };
    panel.addEventListener(
        'click',
        async (event) => {
            if (event.target.closest('[data-agent-action="close"]')) {
                close?.();
                return;
            }
            const button = event.target.closest(
                '[data-lane-running-id], [data-lane-skip], [data-lane-retry]'
            );
            if (!button || busy) return;
            const item = entries.find(
                (item) =>
                    item.entry.id ===
                    (button.dataset.laneRunningId ||
                        button.dataset.laneSkip ||
                        button.dataset.laneRetry)
            );
            if (!item) return;
            if (button.dataset.laneRunningId) {
                if (close?.(false) === false) return;
                openRunningAgent?.({
                    cardId: item.entry.cardId,
                    sessionId: item.run.sessionId
                });
                return;
            }
            busy = true;
            button.disabled = true;
            try {
                if (button.dataset.laneRetry)
                    await api.runAction(item.run.id, 'retry');
                else
                    await api.call(
                        `board/cards/${encodeURIComponent(item.entry.cardId)}/launches/${encodeURIComponent(item.entry.id)}/skip`,
                        'POST'
                    );
            } catch (error) {
                if (current()) app.showToast('Board', error.message, 'error');
            } finally {
                busy = false;
                if (current()) {
                    button.disabled = false;
                    void refresh();
                }
            }
        },
        { signal: abort.signal }
    );
    const timer = setInterval(refresh, 10000);
    const visible = app.onUiVisible?.(refresh);
    const dispose = () => {
        abort.abort();
        disposeLane();
        clearInterval(timer);
        visible?.();
    };
    dispose.beforeClose = disposeLane.beforeClose;
    signal.addEventListener('abort', dispose, { once: true });
    void refresh();
    return dispose;
}
