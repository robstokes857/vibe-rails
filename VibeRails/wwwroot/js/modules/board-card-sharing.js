import { escapeHtml, confirmDialog } from './utils.js';

export function publicCardUrl(path) {
    return typeof path === 'string' && /^\/shared\/card#key=[a-f0-9]{64}$/.test(path)
        ? `https://viberails.ai${path}` : null;
}

export function cardSharingSection(saved) {
    if (!saved) return '<section class="board-side-section"><h3 class="board-side-label">Share card</h3><p class="board-side-empty">Save the card to create a public link.</p></section>';
    return `<section class="board-side-section"><details data-card-sharing>
        <summary class="board-side-label"><i class="fa-solid fa-share-nodes" aria-hidden="true"></i> Share card</summary>
        <p class="board-side-empty mt-2">Anyone with a link can read this card, comments, documents, saved commit diffs and linked session recordings.</p>
        <p class="board-side-empty">Saved changes, including new documents and sessions, update shared cards while VibeRails is open and signed in. Recordings become playable after they end and upload.</p>
        <label class="board-editor-label">Link name <input class="form-control form-control-sm" data-card-share-name maxlength="160"></label>
        <div class="d-flex flex-wrap gap-2 mt-2">
            <button type="button" class="btn btn-sm btn-outline-primary" data-share-action="create">Create public link</button>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-share-action="refresh">Refresh shared card</button>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-share-action="reload">Reload links</button>
        </div>
        <p class="board-side-empty mt-2" role="status" aria-live="polite" data-card-share-message></p>
        <div class="board-side-list" data-card-share-links></div>
        <button type="button" class="btn btn-sm btn-link" data-share-action="more" hidden>Load older links</button>
        <p class="board-side-empty mt-2">Links expire after one month. Revoke a link to end access through it. Deleting the local card keeps its public links until revoked or expired.</p>
        <a href="https://viberails.ai/CardSharingLinks" target="_blank" rel="noopener noreferrer" data-card-share-manage>Manage all card links</a>
    </details></section>`;
}

export function bindCardSharing(editor, card, { app, hasDraft = () => false }) {
    const host = editor.querySelector('[data-card-sharing]');
    if (!host || !card?.id) return null;
    const message = host.querySelector('[data-card-share-message]');
    const list = host.querySelector('[data-card-share-links]');
    const name = host.querySelector('[data-card-share-name]');
    const more = host.querySelector('[data-share-action="more"]');
    name.value = String(card.title || card.displayId || card.key || 'Shared card').slice(0, 160);
    const base = `/api/v1/board/cards/${encodeURIComponent(card.id)}/sharing-links`;
    let disposed = false, busy = false, loaded = false, nextBefore = null, generation = 0, request;
    let links = [];
    const current = () => !disposed && host.isConnected;
    const call = (suffix = '', method = 'GET', body = null, extra = {}) => app.apiCall(base + suffix, method, body,
        { showLoading: false, preferErrorResponseMessage: true, ...extra });
    const setBusy = value => {
        busy = value;
        if (current()) host.querySelectorAll('button, input').forEach(node => { node.disabled = value; });
    };
    function render() {
        list.innerHTML = links.length ? links.map(link => {
            const url = publicCardUrl(link.sharePath);
            const status = ['active', 'revoked', 'expired', 'key_unavailable'].includes(link.status) ? link.status.replaceAll('_', ' ') : 'unavailable';
            return `<div class="board-card-share-link" data-card-share-id="${Number(link.id)}">
                <label class="board-editor-label">Link name<input class="form-control form-control-sm" data-card-share-rename maxlength="160" value="${escapeHtml(link.displayName)}"></label>
                <span class="board-side-sub">${escapeHtml(status)} · Expires ${escapeHtml(new Date(link.expiresUtc).toLocaleString())}</span>
                <span class="board-side-sub">Last published ${escapeHtml(new Date(link.updatedUtc).toLocaleString())}</span>
                ${url ? `<input class="form-control form-control-sm mt-1" aria-label="Public card URL" readonly value="${escapeHtml(url)}" data-card-share-url>` : ''}
                <div class="d-flex flex-wrap gap-2 mt-2">
                    ${url && link.status === 'active' ? `<button type="button" class="btn btn-sm btn-outline-secondary" data-share-action="copy">Copy link</button>
                    <a class="btn btn-sm btn-outline-secondary" href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer" data-card-share-open>Open</a>` : ''}
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-share-action="rename">Rename</button>
                    ${link.status === 'revoked' ? '' : '<button type="button" class="btn btn-sm btn-outline-danger" data-share-action="revoke">Revoke</button>'}
                </div>
            </div>`;
        }).join('') : '<p class="board-side-empty">No public links for this card.</p>';
        more.hidden = !nextBefore;
    }
    async function load(append = false, keepMessage = false) {
        if (!current() || busy) return;
        const version = ++generation;
        request?.abort();
        request = new AbortController();
        setBusy(true);
        if (!keepMessage) message.textContent = 'Loading sharing links…';
        try {
            const result = await call(append && nextBefore ? `?before=${nextBefore}` : '', 'GET', null, { signal: request.signal });
            if (!current() || version !== generation) return;
            if (!result?.success) { message.textContent = result?.message || 'Could not load sharing links.'; return; }
            links = append ? [...links, ...(result.links || [])].filter((link, i, all) => all.findIndex(item => item.id === link.id) === i) : result.links || [];
            nextBefore = result.nextBefore;
            loaded = true;
            render();
            if (!keepMessage) message.textContent = '';
        } catch (error) {
            if (current() && version === generation && error?.name !== 'AbortError') message.textContent = 'Could not load sharing links. Try Reload links.';
        } finally { if (current() && version === generation) setBusy(false); }
    }
    async function click(event) {
        const external = event.target.closest('[data-card-share-open], [data-card-share-manage]');
        if (external && typeof window.__viberails_openExternal__ === 'function') {
            event.preventDefault(); window.__viberails_openExternal__(external.href); return;
        }
        const button = event.target.closest('[data-share-action]');
        if (!button || !current() || busy) return;
        const action = button.dataset.shareAction;
        if (action === 'reload' || action === 'more') { await load(action === 'more'); return; }
        const row = button.closest('[data-card-share-id]');
        const id = Number(row?.dataset.cardShareId);
        if (action === 'copy') {
            const input = row.querySelector('[data-card-share-url]');
            try { await navigator.clipboard.writeText(input.value); if (current()) message.textContent = 'Link copied.'; }
            catch { if (current()) { input.focus(); input.select(); message.textContent = 'Select and copy the public URL.'; } }
            return;
        }
        if (['create', 'refresh'].includes(action) && hasDraft()) {
            message.textContent = 'Save the card and post any draft comments before sharing. Your drafts are still here.'; return;
        }
        const displayName = (action === 'rename' ? row?.querySelector('[data-card-share-rename]')?.value : name.value)?.trim();
        if (['create', 'rename'].includes(action) && (!displayName || displayName.length > 160)) {
            message.textContent = 'Enter a link name of 1 to 160 characters.'; return;
        }
        setBusy(true);
        ++generation;
        request?.abort();
        if (action === 'revoke') {
            const confirmed = await confirmDialog({ title: 'Revoke card link', message: 'End access to this card, its documents and recordings through this link? Other links keep working.', confirmLabel: 'Revoke', danger: true });
            if (!current()) return;
            if (!confirmed) { setBusy(false); return; }
        }
        message.textContent = action === 'create' ? 'Preparing the complete saved card and creating its public link…' : 'Updating sharing…';
        try {
            // Submitted mutations are not aborted on close: the server may have committed.
            const result = await call(action === 'create' ? '' : action === 'refresh' ? '/refresh' : `/${id}`,
                action === 'rename' ? 'PATCH' : action === 'revoke' ? 'DELETE' : 'POST',
                ['create', 'rename'].includes(action) ? { displayName } : null);
            if (!current()) return;
            message.textContent = result?.message || 'Sharing request finished. Reload links to see its result.';
            setBusy(false);
            // Always reconcile after creation, including an uncertain response. Never repeat POST.
            if (result?.success || action === 'create') await load(false, true);
        } catch {
            if (current()) {
                message.textContent = 'Could not confirm the request. Reload links before creating another; a link may already exist.';
                setBusy(false);
                await load(false, true);
            }
        } finally { if (current()) setBusy(false); }
    }
    const toggle = () => { if (host.open && !loaded) void load(); };
    host.addEventListener('toggle', toggle);
    host.addEventListener('click', click);
    return { dispose() { disposed = true; ++generation; request?.abort(); host.removeEventListener('toggle', toggle); host.removeEventListener('click', click); } };
}
