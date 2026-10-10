import { BoardApi } from './board-api.js';
import { cardLabel } from './board-card-label.js';
import { renderCardLinksSection, bindCardLinks } from './board-card-links.js';
import { escapeHtml, confirmDialog } from './utils.js';
import { mountLlmPicker } from './pickers/llm-picker.js';
import { renderCommentHtml } from './board-text.js';
import { previousWorkHtml } from './board-previous-work.js';

const TYPES = [['task', 'Task'], ['bug', 'Bug'], ['feature', 'Feature'], ['research-spike', 'Research spike'], ['chore', 'Chore / tech debt']];
const options = (items, selected) => items.map(([id, name]) => `<option value="${escapeHtml(id)}"${id === selected ? ' selected' : ''}>${escapeHtml(name)}</option>`).join('');

export function localCardEditorHtml({ card, projectPath, boardName, columns }) {
    return `<div class="board-local-card" data-local-board-card-editor>
        <div class="alert alert-warning" role="note"><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i>
            This card belongs to another project's board: <strong>${escapeHtml(boardName)}</strong>.
            Changes are saved to that board.<div class="board-local-project">${escapeHtml(projectPath)}</div></div>
        <form data-local-card-form>
            <label class="form-label" for="local-card-title">Title</label>
            <input id="local-card-title" class="form-control" name="title" required spellcheck="true" value="${escapeHtml(card.title || '')}">
            <label class="form-label mt-3" for="local-card-description">Description</label>
            <textarea id="local-card-description" class="form-control" name="description" rows="8" maxlength="100000" spellcheck="true">${escapeHtml(card.description || '')}</textarea>
            <div class="row g-3 mt-1 mx-0">
                <div class="col-sm-6"><label class="form-label" for="local-card-lane">Lane</label>
                    <select id="local-card-lane" class="form-select" name="columnId">${options((columns || []).map(column => [column.id, column.name]), card.columnId)}</select></div>
                <div class="col-sm-6"><label class="form-label" for="local-card-assignee">Assignee (LLM)</label>
                    <select id="local-card-assignee" class="form-select" name="assignee"></select></div>
                <div class="col-sm-6"><label class="form-label" for="local-card-type">Type</label>
                    <select id="local-card-type" class="form-select" name="type">${options(TYPES, card.type || 'task')}</select></div>
            </div>
            <div class="d-flex gap-3 flex-wrap my-3">
                <label class="form-check"><input class="form-check-input" type="checkbox" name="blocked"${card.blocked ? ' checked' : ''}> Blocked</label>
                <label class="form-check"><input class="form-check-input" type="checkbox" name="flagged"${card.flagged ? ' checked' : ''}> Needs your attention</label>
            </div>
            <button type="submit" class="btn btn-primary btn-sm">Save card</button>
            <span class="small ms-2" data-local-card-status role="status" aria-live="polite"></span>
        </form>
        <hr>${renderCardLinksSection(card)}${previousWorkHtml(card)}<hr>
        <h3 class="h6">Comments</h3>
        <div data-local-card-comments></div>
        <form data-local-comment-form class="mt-3">
            <label class="form-label" for="local-card-comment">Add a comment</label>
            <textarea id="local-card-comment" class="form-control" name="body" rows="3" required spellcheck="true"></textarea>
            ${card.jiraIssueKey ? '<label class="form-check mt-2"><input class="form-check-input" type="checkbox" name="syncToJira" checked> Post this comment to Jira</label>' : ''}
            <button type="submit" class="btn btn-outline-primary btn-sm mt-2">Post comment</button>
        </form>
    </div>`;
}

export function readLocalCardForm(form) {
    const field = name => form.elements.namedItem(name);
    return {
        title: field('title').value.trim(), description: field('description').value,
        columnId: field('columnId').value, assignee: field('assignee').value,
        type: field('type').value,
        blocked: field('blocked').checked, flagged: field('flagged').checked
    };
}

export function localCardChanges(baseline, values) {
    return Object.fromEntries(Object.entries(values).filter(([name, value]) => JSON.stringify(value) !== JSON.stringify(baseline[name])));
}

function commentsHtml(card, isJiraBoard) {
    const entries = [...(card.comments || []), ...(card.notes || [])].sort((a, b) =>
        Number(b.isAttention === true) - Number(a.isAttention === true) || String(a.createdAt).localeCompare(String(b.createdAt)));
    return entries.map(entry => `<article class="board-comment${entry.isAttention ? ' is-attention' : ''}">
        <div class="board-comment-content"><div class="board-comment-meta">
            <strong>${escapeHtml(entry.author?.label || 'Someone')}</strong>
            ${isJiraBoard === true && entry.syncToJira === false ? '<span class="badge text-bg-secondary">Not sent to Jira</span>' : ''}
            <time>${escapeHtml(new Date(entry.createdAt).toLocaleString())}</time></div>
            ${entry.isAttention ? '<div class="board-comment-attention">Needs your attention</div>' : ''}
            <div class="board-comment-body board-local-comment-body">${renderCommentHtml(entry.body, { interactiveReferences: false })}</div></div></article>`).join('') || '<p class="text-muted small">No comments yet.</p>';
}

/** Limited to the explicit local-card API; repository/terminal actions stay on their own project. */
export function openLocalCardEditor(app, detail, { openCard, onChanged }) {
    const card = detail.card;
    let disposed = false;
    let busy = false;
    let linksDispose = null;
    let pickerDispose = null;
    const lifetime = new AbortController();
    const dispose = () => {
        if (disposed) return;
        disposed = true;
        lifetime.abort();
        linksDispose?.();
        pickerDispose?.();
    };
    app.showModal(cardLabel(card), localCardEditorHtml(detail), { onClose: dispose });
    const editor = document.getElementById('modal-container')?.querySelector('[data-local-board-card-editor]');
    if (!editor) return dispose;
    const form = editor.querySelector('[data-local-card-form]');
    const commentForm = editor.querySelector('[data-local-comment-form]');
    const comments = editor.querySelector('[data-local-card-comments]');
    const status = editor.querySelector('[data-local-card-status]');
    pickerDispose = mountLlmPicker(app, form.elements.namedItem('assignee'), {
        context: 'sandbox', placeholder: 'Unassigned', selectedValue: card.assignee || '',
        selectedFallback: value => ({ value, label: `Unavailable selection (${value})` })
    });
    let baseline = readLocalCardForm(form);
    const alive = () => !disposed && editor.isConnected !== false;
    const lock = value => {
        busy = value;
        editor.querySelectorAll('button, input, select, textarea').forEach(control => { control.disabled = value; });
    };
    const changed = () => {
        if (alive()) onChanged?.();
    };
    const showError = message => { if (alive()) app.showToast('Board', message, 'error'); };
    comments.innerHTML = commentsHtml(card, detail.isJiraBoard);
    linksDispose = bindCardLinks(editor, card, {
        api: {
            getCardLinkCandidatesAsync: BoardApi.getLocalCardLinkCandidatesAsync,
            linkCardAsync: BoardApi.linkLocalCardAsync,
            unlinkCardAsync: BoardApi.unlinkLocalCardAsync
        },
        openCard: async id => {
            if (busy || !alive()) return;
            const dirty = Object.keys(localCardChanges(baseline, readLocalCardForm(form))).length
                || commentForm.elements.namedItem('body').value.trim();
            if (dirty && !await confirmDialog({ title: 'Open linked card',
                message: 'You have unsaved edits on this card. Discard them and open the linked card?',
                confirmLabel: 'Discard and open', danger: true })) return;
            if (alive()) await openCard(id);
        },
        showError, onChanged: changed
    });
    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (busy || !alive()) return;
        const values = readLocalCardForm(form);
        if (!values.title) { form.elements.namedItem('title').focus(); return; }
        const patch = localCardChanges(baseline, values);
        if (!Object.keys(patch).length) { status.textContent = 'No changes to save.'; return; }
        lock(true);
        status.textContent = 'Saving…';
        try {
            const saved = await BoardApi.updateLocalBoardCardAsync(card.id, patch);
            if (!alive()) return;
            Object.assign(card, saved);
            baseline = values;
            status.textContent = 'Saved to this card’s board.';
            changed();
        } catch (error) { showError(error?.message || 'Could not save the card.'); status.textContent = ''; }
        finally { if (alive()) lock(false); }
    }, { signal: lifetime.signal });
    commentForm.addEventListener('submit', async event => {
        event.preventDefault();
        if (busy || !alive()) return;
        const body = commentForm.elements.namedItem('body');
        if (!body.value.trim()) return;
        lock(true);
        try {
            const entry = await BoardApi.addLocalBoardCommentAsync(card.id, { body: body.value.trim(), syncToJira: commentForm.elements.namedItem('syncToJira')?.checked !== false });
            if (!alive()) return;
            card.comments = [...(card.comments || []), entry];
            comments.innerHTML = commentsHtml(card, detail.isJiraBoard);
            body.value = '';
            changed();
        } catch (error) { showError(error?.message || 'Could not post the comment.'); }
        finally { if (alive()) lock(false); }
    }, { signal: lifetime.signal });
    form.elements.namedItem('title').focus();
    return dispose;
}
