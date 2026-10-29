import { escapeHtml, confirmDialog } from './utils.js';

function sharingModal(app, title, intro) {
    const abort = new AbortController();
    app.showModal(title, `<section data-sharing-modal><p>${intro}</p>
        <p data-sharing-status role="status" aria-live="polite">Loading…</p><div data-sharing-content></div></section>`,
    { onClose: () => abort.abort() });
    const host = document.querySelector('#modal-container [data-sharing-modal]');
    const active = () => host?.isConnected && !abort.signal.aborted;
    const status = text => { if (active()) host.querySelector('[data-sharing-status]').textContent = text; };
    const request = (url, method = 'GET', data = null) => app.apiCall(url, method, data,
        { signal: abort.signal, showLoading: false, preferErrorResponseMessage: true });
    return { host, active, status, request, dispose: () => abort.abort() };
}

export function openBoardSharing(app, boardId) {
    const view = sharingModal(app, 'Share board', 'Invite up to three collaborators by email. Pending invitations use a slot. Collaborators view it read-only on viberails.ai.');
    const { host, active, request, status } = view;
    const base = `/api/v1/board/boards/${encodeURIComponent(boardId)}/sharing`;
    const content = host?.querySelector('[data-sharing-content]');
    async function load(message = '') {
        const state = await request(base); if (!active()) return;
        if (!state.configured) { status('Sign in using Account in the navigation bar to share this board.'); return; }
        if (!state.isOwner) { status('This board is shared with you and is read-only. Only its owner can manage collaborators.'); return; }
        const sharing = state.sharing;
        status(message || `${sharing.invitations.length} of ${sharing.limit} collaborator slots used`);
        content.innerHTML = `<p class="small text-muted">Recipients review invitations on <a href="https://viberails.ai/Boards/invitations" target="_blank" rel="noopener noreferrer">viberails.ai</a>. No email is sent. Registration, declines and blocks are private.</p>
            ${sharing.invitations.map(item => `<form class="border rounded p-3 mb-2" data-sharing-edit="${escapeHtml(item.id)}">
                <p class="small mb-2">${item.status === 'accepted' ? 'Collaborator' : 'Invitation pending'}</p>
                <div class="d-flex gap-2 flex-wrap"><input class="form-control form-control-sm" style="flex:1;min-width:180px" type="email" maxlength="320" required aria-label="Email address" value="${escapeHtml(item.email)}">
                <button type="submit" class="btn btn-sm btn-outline-primary">Save address</button>
                <button type="button" class="btn btn-sm btn-outline-danger" data-sharing-remove>Remove</button></div></form>`).join('')}
            ${sharing.invitations.length < sharing.limit ? `<form class="d-flex gap-2 mt-3" data-sharing-new><input class="form-control form-control-sm" type="email" maxlength="320" required placeholder="person@example.com" aria-label="Invite email"><button class="btn btn-sm btn-primary" type="submit">Invite</button></form>` : ''}`;
        content.querySelectorAll('form').forEach(form => {
            form.addEventListener('submit', async event => {
                event.preventDefault(); if (!form.reportValidity() || form.dataset.busy) return;
                form.dataset.busy = 'true'; const submit = form.querySelector('[type="submit"]'); submit.disabled = true;
                try {
                    await request(base + (form.dataset.sharingEdit ? '/' + encodeURIComponent(form.dataset.sharingEdit) : ''),
                        form.dataset.sharingEdit ? 'PUT' : 'POST', { email: form.querySelector('input').value });
                    if (active()) await load('Invitation saved. The recipient can review it in Boards if eligible.');
                } catch (error) { if (error.name !== 'AbortError') status(error.message || 'Could not save the invitation.'); }
                finally { delete form.dataset.busy; submit.disabled = false; }
            });
            form.querySelector('[data-sharing-remove]')?.addEventListener('click', async event => {
                const button = event.currentTarget; button.disabled = true;
                try {
                    if (!await confirmDialog({ title: 'Remove collaborator', message: 'Remove this invitation or collaborator’s access?', confirmLabel: 'Remove', danger: true }) || !active()) return;
                    await request(base + '/' + encodeURIComponent(form.dataset.sharingEdit), 'DELETE');
                    if (active()) await load('Access removed.');
                } catch (error) { if (error.name !== 'AbortError') status(error.message || 'Could not remove access.'); }
                finally { button.disabled = false; }
            });
        });
    }
    if (host) load().catch(error => { if (error.name !== 'AbortError') status(error.message || 'Could not load sharing.'); });
    return view.dispose;
}
