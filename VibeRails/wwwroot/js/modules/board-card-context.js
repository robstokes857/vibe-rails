// Agent context (VB-63): the block in a saved card's collapsed Advanced section that says how much
// the card puts in front of an agent at launch. The server measures it (GET
// /api/v1/board/cards/{id}/context) by rendering the real launch prompt and the first Board tool
// reads; this module only formats the answer. Tokens are an estimate (characters over four) and
// every number carries the ≈ to say so.
import { BoardApi } from './board-api.js';
import { escapeHtml } from './utils.js';

// 980 · 12.4k · 1.2M — the shape agents and their status lines use.
export function formatTokens(tokens) {
    const n = Math.max(0, Math.round(Number(tokens) || 0));
    // One decimal below ten units (1.5k, 9.8k), whole numbers from there (12k, 1.2M stays 1.2M under 10M).
    // Integer arithmetic first: 1450 / 1000 is 1.4499… in binary and would round down.
    const scale = (divisor, unit) =>
        (n >= divisor * 10 ? String(Math.round(n / divisor)) : String(Math.round((n * 10) / divisor) / 10)) + unit;
    if (n >= 1_000_000) return scale(1_000_000, 'M');
    if (n >= 1000) return scale(1000, 'k');
    return String(n);
}

// Only a saved card has something to measure, so only its Advanced section carries this block.
export function contextSectionMarkup() {
    return `<div class="mt-3" data-board-context-section>
        <h4 class="board-editor-label">
            <i class="fa-solid fa-gauge-high" aria-hidden="true"></i>
            Agent context <span class="board-count" data-board-context-total title="Estimated tokens an agent starts with">…</span>
        </h4>
        <div class="board-side-list board-context" data-board-context></div>
        <p class="board-side-empty" data-board-context-message role="status" aria-live="polite"></p>
    </div>`;
}

function row(part, strong = false) {
    const items = part.items === null || part.items === undefined ? '' : ` <span class="board-side-sub">(${escapeHtml(String(part.items))})</span>`;
    const note = part.note ? `<span class="board-side-sub board-context-note">${escapeHtml(part.note)}</span>` : '';
    return `<div class="board-context-row${strong ? ' board-context-row-strong' : ''}" data-context-part="${escapeHtml(part.key)}"
        title="${escapeHtml(Number(part.chars || 0).toLocaleString())} characters">
        <span class="board-side-title">${escapeHtml(part.label)}${items}</span>
        <span class="board-context-tokens">≈${escapeHtml(formatTokens(part.tokens))}</span>
        ${note}
    </div>`;
}

// Pure: renders one estimate into the section. Exported for the node tests.
export function renderContext(host, total, estimate) {
    total.textContent = `≈${formatTokens(estimate.tokens)}`;
    total.title = `${Number(estimate.chars || 0).toLocaleString()} characters · tokens ${estimate.method}`;
    const last = estimate.lastLaunch;
    const lastLine = last
        ? `Last launch ≈${escapeHtml(formatTokens(last.tokens))} tokens · ${escapeHtml(new Date(last.measuredAt).toLocaleString())}${last.cli ? ` · ${escapeHtml(last.cli)}` : ''}.`
        : 'No launch recorded yet.';
    host.innerHTML = `
        <div class="board-context-group">${(estimate.sources || []).map(part => row(part, true)).join('')}</div>
        <details class="board-context-details">
            <summary class="board-side-sub">By content</summary>
            ${(estimate.contents || []).map(part => row(part)).join('')}
        </details>
        ${(estimate.extras || []).length ? `<details class="board-context-details">
            <summary class="board-side-sub">Available, not sent unless asked for</summary>
            ${estimate.extras.map(part => row(part)).join('')}
        </details>` : ''}
        <p class="board-side-empty">${lastLine} Tokens are an estimate (${escapeHtml(estimate.method || 'chars/4')}).</p>`;
}

// refresh() is called after every rail change. While the enclosing disclosure is closed nobody can
// read the numbers, so a refresh only marks them stale and opening the disclosure measures once.
export function bindCardContext(editor, card) {
    const section = editor.querySelector('[data-board-context-section]');
    const host = section?.querySelector('[data-board-context]');
    const total = section?.querySelector('[data-board-context-total]');
    const message = section?.querySelector('[data-board-context-message]');
    if (!section || !host || !total || !card?.id) return null;
    const disclosure = section.closest?.('details') || null;
    let disposed = false;
    let generation = 0;
    let stale = true;
    let request;
    const current = () => !disposed && section.isConnected;
    const shown = () => !disclosure || disclosure.open;

    async function refresh() {
        if (!current()) return;
        if (!shown()) {
            stale = true;
            return;
        }
        stale = false;
        const version = ++generation;
        request?.abort();
        const abort = request = new AbortController();
        try {
            const estimate = await BoardApi.getCardContextAsync(card.id, { signal: abort.signal });
            if (!current() || generation !== version) return;
            renderContext(host, total, estimate);
            if (message) message.textContent = '';
        } catch (error) {
            if (!current() || generation !== version || abort.signal.aborted || error?.name === 'AbortError') return;
            // Inline only: the rail refreshes after every change, so a toast would repeat each time.
            total.textContent = '?';
            if (message) message.textContent = error?.message || 'Could not measure the agent context.';
        }
    }

    const onToggle = () => {
        if (shown() && stale) void refresh();
    };
    disclosure?.addEventListener('toggle', onToggle);
    void refresh();
    return {
        refresh,
        dispose() {
            disposed = true;
            ++generation;
            request?.abort();
            disclosure?.removeEventListener('toggle', onToggle);
        }
    };
}
