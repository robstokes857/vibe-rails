// ============================================
// Board API — server data layer
// ============================================
//
// Thin client over /api/v1/board/* (VibeRails/Routes/BoardRoutes.cs). Every call goes through
// app.apiCall, so the session cookie and the viberails_tab header ride along like every other
// request. Errors come back as `{ error }` bodies; preferErrorResponseMessage surfaces that text
// in the toast instead of a bare status line.
//
// Shapes (see VibeRails/DTOs/ResponseRecords.cs, "Kanban board"):
//   column     { id, name, wipLimit, position, color }
//   card       { id, key 'VB-n', columnId, position, title, description, assignee, priority,
//                points, tags[], blocked, commentCount, activeSessionId, activeTabId,
//                createdAt, updatedAt }                      (board list = these summaries)
//   card+rails { ...card, comments[], commits[], sessions[], attachments[] }  (getBoardCardAsync)
//   comment    { id, author: { kind: 'user'|'agent', label, cli }, body, createdAt }
//   session    { id, tabId, displayName, cli, selection, origin, createdAt, active }
//   attachment { id, name, url (data: URL), mimeType, bytes, createdAt }
//   commit     { sha, shortSha, author, message, committedAt }
//
// `assignee` is an LLM picker key ('base:claude' / 'env:7:codex'), never a person.
// The board is per project: the server scopes every call to the open workspace.

const BASE = '/api/v1/board';

let app = null;

/** Called once by BoardController so this module can reach app.apiCall. */
function attach(appInstance) {
    app = appInstance;
}

function call(path, method = 'GET', body = null, extra = {}) {
    if (!app) throw new Error('BoardApi.attach(app) has not been called.');
    return app.apiCall(`${BASE}${path}`, method, body, { showLoading: false, preferErrorResponseMessage: true, ...extra });
}

const enc = value => encodeURIComponent(String(value));

// ---------------------------------------------- columns

async function getBoardColumnsAsync() {
    const response = await call('/columns');
    return (response?.columns || []).sort((a, b) => a.position - b.position);
}

async function createBoardColumnAsync(payload) {
    return call('/columns', 'POST', payload);
}

async function updateBoardColumnAsync(columnId, patch) {
    return call(`/columns/${enc(columnId)}`, 'PUT', patch);
}

async function deleteBoardColumnAsync(columnId) {
    return call(`/columns/${enc(columnId)}`, 'DELETE');
}

async function reorderBoardColumnsAsync(orderedIds) {
    const response = await call('/columns/order', 'PUT', { orderedIds });
    return (response?.columns || []).sort((a, b) => a.position - b.position);
}

// ---------------------------------------------- cards

async function getBoardCardsAsync() {
    const response = await call('/cards');
    return (response?.cards || []).sort((a, b) => a.position - b.position || a.key.localeCompare(b.key));
}

async function getBoardCardAsync(cardId, extra = {}) {
    return call(`/cards/${enc(cardId)}`, 'GET', null, extra);
}

async function createBoardCardAsync(payload) {
    return call('/cards', 'POST', payload);
}

async function updateBoardCardAsync(cardId, patch) {
    return call(`/cards/${enc(cardId)}`, 'PUT', patch);
}

async function deleteBoardCardAsync(cardId) {
    await call(`/cards/${enc(cardId)}`, 'DELETE');
    return { ok: true };
}

async function moveBoardCardAsync(cardId, { columnId, position }) {
    return call(`/cards/${enc(cardId)}/move`, 'POST', { columnId, position });
}

/** Start work: the server opens a terminal tab with the card prepended to the LLM's initial message. */
async function launchBoardCardAsync(cardId, { selection } = {}) {
    return call(`/cards/${enc(cardId)}/launch`, 'POST', { selection: selection || null });
}

// ---------------------------------------------- comments

async function addBoardCommentAsync(cardId, { body }) {
    return call(`/cards/${enc(cardId)}/comments`, 'POST', { body });
}

// ---------------------------------------------- attachments
//
// The contract that matters: this returns a URL, and the caller renders that URL. Today the
// server stores the (downscaled) data: URL and hands it straight back; a served URL would be a
// body-only change here.

async function addCardAttachmentAsync(cardId, { name, dataUrl, bytes, mimeType } = {}) {
    if (!dataUrl) throw new Error('The attachment has no content.');
    return call(`/cards/${enc(cardId)}/attachments`, 'POST', { name, dataUrl, bytes, mimeType });
}

async function deleteCardAttachmentAsync(cardId, attachmentId) {
    await call(`/cards/${enc(cardId)}/attachments/${enc(attachmentId)}`, 'DELETE');
    return { ok: true };
}

// ---------------------------------------------- commits

async function getCardCommitsAsync(cardId) {
    return call(`/cards/${enc(cardId)}/commits`);
}

/** The server validates the sha against the project repo and fills in author/message/date. */
async function addCardCommitAsync(cardId, payload = {}) {
    const sha = String(payload.sha || '').trim();
    if (!/^[0-9a-f]{7,40}$/i.test(sha)) {
        throw new Error('That does not look like a commit sha.');
    }
    return call(`/cards/${enc(cardId)}/commits`, 'POST', { sha });
}

async function removeCardCommitAsync(cardId, sha) {
    await call(`/cards/${enc(cardId)}/commits/${enc(sha)}`, 'DELETE');
    return { ok: true };
}

/**
 * Files for one commit, in the exact shape the Monaco diff viewer consumes and
 * that /api/v1/sandboxes/{id}/diff already returns:
 *   { files: [{ fileName, language, originalContent, modifiedContent }], totalChanges }
 */
async function getCommitDiffAsync(cardId, sha) {
    return call(`/cards/${enc(cardId)}/commits/${enc(sha)}/diff`);
}

// ---------------------------------------------- sessions
//
// Launches link themselves (origin 'launch'); an LLM touching a card over MCP links its session
// (origin 'mcp'); this form links an arbitrary id by hand (origin 'manual').

async function getCardSessionsAsync(cardId) {
    return call(`/cards/${enc(cardId)}/sessions`);
}

async function addCardSessionAsync(cardId, { id, displayName } = {}) {
    return call(`/cards/${enc(cardId)}/sessions`, 'POST', { id: id || null, displayName });
}

async function updateCardSessionAsync(cardId, sessionId, patch = {}) {
    return call(`/cards/${enc(cardId)}/sessions/${enc(sessionId)}`, 'PUT', { displayName: patch.displayName });
}

async function removeCardSessionAsync(cardId, sessionId) {
    await call(`/cards/${enc(cardId)}/sessions/${enc(sessionId)}`, 'DELETE');
    return { ok: true };
}

export const BoardApi = {
    attach,
    getBoardColumnsAsync,
    createBoardColumnAsync,
    updateBoardColumnAsync,
    deleteBoardColumnAsync,
    reorderBoardColumnsAsync,
    getBoardCardsAsync,
    getBoardCardAsync,
    createBoardCardAsync,
    updateBoardCardAsync,
    deleteBoardCardAsync,
    moveBoardCardAsync,
    launchBoardCardAsync,
    addBoardCommentAsync,
    addCardAttachmentAsync,
    deleteCardAttachmentAsync,
    getCardCommitsAsync,
    addCardCommitAsync,
    removeCardCommitAsync,
    getCommitDiffAsync,
    getCardSessionsAsync,
    addCardSessionAsync,
    updateCardSessionAsync,
    removeCardSessionAsync
};
