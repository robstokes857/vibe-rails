import { escapeHtml, confirmDialog } from './utils.js';

// Independent of the selected local board: remote-only and other-project copies belong here too.
export function openRemoteBoards(app, onChanged = () => {}) {
    const abort = new AbortController();
    let rows = [], context = null, nextOffset = null, busy = false, generation = 0;
    let edit = null;
    app.showModal('Remote boards', `<section data-remote-boards>
        <p class="text-muted">Boards stored on viberails.ai. Review old copies here, including boards no longer on this computer.</p>
        <div class="d-flex gap-2 flex-wrap mb-3">
            <input type="search" class="form-control" style="flex:1;min-width:160px" data-remote-search aria-label="Search loaded remote boards" placeholder="Search loaded boards…">
            <select class="form-select" style="width:auto;max-width:100%" data-remote-filter aria-label="Filter remote boards">
                <option value="all">All boards</option><option value="unlinked">No local copy</option>
                <option value="owned">Owned by me</option><option value="shared">Shared with me</option>
            </select>
            <button type="button" class="btn btn-outline-secondary" data-remote-refresh>Refresh</button>
            <button type="button" class="btn btn-primary" data-remote-new disabled>New remote board</button>
        </div>
        <form data-remote-editor class="border rounded p-3 mb-3" hidden>
            <label class="form-label" for="remote-board-name" data-remote-editor-title>Board name</label>
            <input id="remote-board-name" class="form-control mb-2" maxlength="120" required autocomplete="off">
            <div class="d-flex gap-2"><button type="submit" class="btn btn-primary" data-remote-save>Save</button>
                <button type="button" class="btn btn-outline-secondary" data-remote-cancel>Cancel</button></div>
        </form>
        <p role="status" aria-live="polite" data-remote-status>Loading remote boards…</p>
        <div data-remote-list></div>
        <button type="button" class="btn btn-outline-secondary mt-3" data-remote-more hidden>Load more boards</button>
    </section>`, { onClose: () => abort.abort() });
    const host = document.querySelector('#modal-container [data-remote-boards]');
    if (!host) return () => abort.abort();
    host.closest('.modal-dialog')?.classList.add('modal-xl');
    const active = () => host.isConnected && !abort.signal.aborted;
    const get = selector => host.querySelector(selector);
    const status = message => { if (active()) get('[data-remote-status]').textContent = message; };
    const request = (path, method = 'GET', data = null) => app.apiCall(`/api/v1/board/remote${path}`, method, data,
        { ...(method === 'GET' ? { signal: abort.signal } : {}), showLoading: false, preferErrorResponseMessage: true });
    function setBusy(value) {
        busy = value;
        if (!active()) return;
        host.querySelectorAll('button').forEach(button => { button.disabled = value; });
        get('#remote-board-name').disabled = value;
        get('[data-remote-new]').disabled = value || !context || !!edit;
        host.querySelectorAll('[data-remote-rename], [data-remote-delete]').forEach(button => { button.disabled = value || !!edit; });
    }
    function render() {
        if (!active()) return;
        const search = get('[data-remote-search]').value.trim().toLocaleLowerCase();
        const filter = get('[data-remote-filter]').value;
        const visible = rows.filter(row => (!search || [row.name, ...row.localCopies.map(copy => copy.projectName)].join(' ').toLocaleLowerCase().includes(search))
            && (filter !== 'unlinked' || row.localCopies.length === 0)
            && (filter !== 'owned' || row.isOwner) && (filter !== 'shared' || !row.isOwner));
        get('[data-remote-list]').innerHTML = visible.length ? visible.map(row => {
            const copies = row.localCopies.length
                ? row.localCopies.map(copy => `${escapeHtml(copy.projectName)} · ${copy.syncEnabled ? 'Sync enabled' : 'Sync paused'}`).join('<br>')
                : 'No local copy on this computer';
            // Links are built by the local service; reject executable URLs and escape all labels.
            let url = null;
            try { const candidate = new URL(row.url); if (candidate.protocol === 'https:' && !candidate.username && !candidate.password) url = candidate.href; } catch { /* No link. */ }
            return `<article class="border rounded p-3 mb-2" data-remote-row="${escapeHtml(row.id)}">
                <div class="d-flex justify-content-between gap-3 flex-wrap">
                    <div style="min-width:0;overflow-wrap:anywhere"><h3 class="h6 mb-1">${escapeHtml(row.name)}</h3>
                        <p class="small text-muted mb-1">${row.isOwner ? 'Owned by you' : 'Shared with you'} · ${row.lanes.length} lanes</p>
                        <p class="small mb-1">${copies}</p>
                        <details class="small"><summary>View lanes</summary><p class="text-muted mb-0">${row.lanes.map(escapeHtml).join(' → ')}</p></details>
                    </div>
                    <div class="d-flex align-items-start gap-2 flex-wrap">
                        ${url ? `<a class="btn btn-sm btn-outline-primary" href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer">Open board</a>` : ''}
                        ${row.isOwner ? '<button type="button" class="btn btn-sm btn-outline-secondary" data-remote-rename>Rename</button><button type="button" class="btn btn-sm btn-outline-danger" data-remote-delete>Delete</button>' : ''}
                    </div>
                </div>
            </article>`;
        }).join('') : '<p class="text-muted p-3 border rounded">No remote boards match this view.</p>';
        get('[data-remote-more]').hidden = nextOffset === null;
        setBusy(busy);
    }
    async function load(append = false, message = '') {
        const current = ++generation;
        setBusy(true);
        try {
            const page = await request(append ? `?offset=${nextOffset}` : '');
            if (!active() || current !== generation) return;
            if (append && page.context !== context) throw new Error('The account changed. Refresh remote boards before continuing.');
            const added = page.boards.filter(row => !append || !rows.some(old => old.id === row.id));
            if (append && page.boards.length && !added.length) throw new Error('The server could not load the next page. Update viberails.ai and refresh.');
            rows = append ? [...rows, ...added] : page.boards;
            context = page.context;
            nextOffset = page.nextOffset ?? null;
            render();
            status(message || `${rows.length} remote board${rows.length === 1 ? '' : 's'} loaded${nextOffset !== null ? ' · More available' : ''}.`);
        } catch (error) { if (active() && error.name !== 'AbortError') status(error.message || 'Could not load remote boards.'); }
        finally { if (current === generation) setBusy(false); }
    }
    function openEditor(row = null) {
        if (busy || !context) return;
        edit = { id: row?.id, context, requestId: Array.from(crypto.getRandomValues(new Uint8Array(16)), value => value.toString(16).padStart(2, '0')).join('') };
        get('[data-remote-editor]').hidden = false;
        get('[data-remote-editor-title]').textContent = row ? 'Rename remote board' : 'New remote board name';
        get('#remote-board-name').value = row?.name || '';
        setBusy(false);
        get('#remote-board-name').focus();
    }
    async function changed() {
        try { await onChanged(); }
        catch { status('Remote change saved. Refresh the local Board to see its current state.'); }
    }
    get('[data-remote-editor]').addEventListener('submit', async event => {
        event.preventDefault();
        if (busy || !edit || !event.currentTarget.reportValidity()) return;
        setBusy(true);
        try {
            const result = await request(edit.id ? `/${encodeURIComponent(edit.id)}` : '', edit.id ? 'PUT' : 'POST',
                { context: edit.context, requestId: edit.requestId, name: get('#remote-board-name').value });
            if (!active()) return;
            edit = null; get('[data-remote-editor]').hidden = true;
            await load(false, result.message); await changed();
        } catch (error) { status(error.message || 'Could not save the remote board. Your name is still here to retry.'); }
        finally { setBusy(false); }
    });
    get('[data-remote-list]').addEventListener('click', async event => {
        const article = event.target.closest('[data-remote-row]');
        const row = rows.find(item => item.id === article?.dataset.remoteRow);
        if (!row || busy) return;
        if (event.target.closest('[data-remote-rename]')) { openEditor(row); return; }
        if (!event.target.closest('[data-remote-delete]')) return;
        setBusy(true);
        const selectedContext = context;
        try {
            const confirmed = await confirmDialog({ title: 'Delete remote board', danger: true, confirmLabel: 'Delete remote board',
                message: `Delete “${row.name}” and its cards from viberails.ai? Collaborators will lose access. Local copies on this computer are kept with sync paused.` });
            if (!confirmed || !active()) return;
            const result = await request(`/${encodeURIComponent(row.id)}/delete`, 'POST', { context: selectedContext });
            if (active()) { await load(false, result.message); await changed(); }
        } catch (error) { if (active()) await load(false, error.message || 'Deletion was not confirmed. Refresh before retrying.'); }
        finally { setBusy(false); }
    });
    get('[data-remote-search]').addEventListener('input', render);
    get('[data-remote-filter]').addEventListener('change', render);
    get('[data-remote-refresh]').addEventListener('click', () => load());
    get('[data-remote-more]').addEventListener('click', () => load(true));
    get('[data-remote-new]').addEventListener('click', () => openEditor());
    get('[data-remote-cancel]').addEventListener('click', () => { edit = null; get('[data-remote-editor]').hidden = true; setBusy(false); });
    load();
    return () => abort.abort();
}
