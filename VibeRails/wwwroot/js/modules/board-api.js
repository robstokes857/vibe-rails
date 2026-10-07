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
//   column     { id, name, position, color }
//   card       { id, key 'VB-n', columnId, position, title, description, assignee, type, priority,
//                points, tags[], blocked, commentCount, activeSessionId, activeTabId,
//                createdAt, updatedAt }                      (board list = these summaries)
//   card+rails { ...card, comments[], commits[], sessions[], attachments[], notes[], linkedCards[] }  (getBoardCardAsync)
//   linkedCard { id, key, title, boardId, boardName, columnId, columnName }
//   comment    { id, author: { kind: 'user'|'agent', label, cli }, body, createdAt }
//   note       same shape as a comment (id 'note_…'); legacy compatibility; new card reads include these in comments[]
//   session    { id, tabId, displayName, cli, selection, origin, createdAt, active }
//   attachment { id, name, url (data: URL), mimeType, bytes, createdAt }
//   commit     { sha, shortSha, author, message, committedAt, linkedAt }
//   files      { files: ['repo/relative/path', …], truncated }   (searchFilesAsync, for `@path` refs)
//
//   board      { id, name, position, createdAt, cardCount, columns[] }
//
// `assignee` is an LLM picker key ('base:claude' / 'env:7:codex'), never a person.
// Boards are per project: the server scopes every call to the open workspace, and a project can
// hold several boards (sprints, sub-projects). Lane and card lists take the board id; omitted,
// the server answers for the project's first board. Card keys are unique across the project.

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
const withBoard = (path, boardId) => boardId ? `${path}?boardId=${enc(boardId)}` : path;

// ---------------------------------------------- boards

async function getBoardsAsync(extra = {}) {
    const response = await call('/boards', 'GET', null, extra);
    return (response?.boards || []).sort((a, b) => a.position - b.position);
}

async function createBoardAsync(payload) {
    return call('/boards', 'POST', payload);
}

async function updateBoardAsync(boardId, patch) {
    return call(`/boards/${enc(boardId)}`, 'PUT', patch);
}

async function reorderBoardsAsync(orderedIds, extra = {}) {
    const response = await call('/boards/order', 'PUT', { orderedIds }, extra);
    return response?.boards || [];
}

// settings: true adds the board settings form's extras (column-to-lane map, lanes, email suggestion).
async function getJiraConnectionAsync(boardId, { settings = false } = {}) {
    return call(`/boards/${enc(boardId)}/jira${settings ? '?settings=true' : ''}`);
}

async function saveJiraConnectionAsync(boardId, payload) {
    return call(`/boards/${enc(boardId)}/jira`, 'PUT', payload);
}

async function testJiraConnectionAsync(boardId) {
    return call(`/boards/${enc(boardId)}/jira/test`, 'POST');
}

async function pullJiraAsync(boardId, dryRun = false) {
    return call(`/boards/${enc(boardId)}/jira/pull${dryRun ? '?dryRun=true' : ''}`, 'POST');
}

// ---------------------------------------------- viberails.ai sync (VB-51)
//
//   syncStatus { boardId, published, enabled, remoteBoardId, remoteUrl, cursor, unsent,
//                lastSyncUtc, lastError, configured }
// `configured` says whether an API key and endpoint exist; the key itself never comes back.

async function getBoardSyncAsync(boardId, extra = {}) {
    return call(`/boards/${enc(boardId)}/sync`, 'GET', null, extra);
}

async function setBoardSyncAsync(boardId, enabled, includeActivity = false) {
    return call(`/boards/${enc(boardId)}/sync`, 'PUT', { enabled: !!enabled, includeActivity: !!includeActivity });
}

async function syncBoardNowAsync(boardId) {
    return call(`/boards/${enc(boardId)}/sync/now`, 'POST');
}

// History (VB-51): a human-only settings view, paged by 100 from an offset, optionally for one
// card. Never part of a card read.
async function getBoardHistoryAsync(boardId, { card = null, offset = 0, signal } = {}) {
    return call(`/boards/${enc(boardId)}/history?offset=${enc(offset)}${card ? `&card=${enc(card)}` : ''}`, 'GET', null, { signal });
}

async function getBoardContextAsync(boardId, extra = {}) {
    return call(`/boards/${enc(boardId)}/context`, 'GET', null, extra);
}

async function saveBoardContextAsync(boardId, payload) {
    return call(`/boards/${enc(boardId)}/context`, 'PUT', payload);
}

async function getLaneAutomationAsync(columnId, extra = {}) {
    return call(`/columns/${enc(columnId)}/automation`, 'GET', null, extra);
}

/** The lane panel's running list only: cheap enough to poll. */
async function getLaneRunningAgentsAsync(columnId, extra = {}) {
    return call(`/columns/${enc(columnId)}/automation/running`, 'GET', null, extra);
}

async function saveLaneAutomationAsync(columnId, payload) {
    return call(`/columns/${enc(columnId)}/automation`, 'PUT', payload);
}

async function getCardAutomationsAsync(cardId, extra = {}) {
    return call(`/cards/${enc(cardId)}/automations`, 'GET', null, extra);
}

async function skipCardAutomationAsync(cardId, jobId, eventKey) {
    return call(`/cards/${enc(cardId)}/automations/skip`, 'POST', { jobId, eventKey });
}

async function runCardAutomationAsync(cardId, jobId) {
    return call(`/cards/${enc(cardId)}/automations`, 'POST', { jobId });
}

// What an agent launched on the card now would read, in characters and estimated tokens (VB-63).
async function getCardContextAsync(cardId, extra = {}) {
    return call(`/cards/${enc(cardId)}/context`, 'GET', null, extra);
}

/** Deletes the board with its lanes and cards; the server refuses the project's last board. */
async function deleteBoardAsync(boardId) {
    return call(`/boards/${enc(boardId)}`, 'DELETE');
}

// ---------------------------------------------- columns

async function getBoardColumnsAsync(boardId = null, extra = {}) {
    const response = await call(withBoard('/columns', boardId), 'GET', null, extra);
    return (response?.columns || []).sort((a, b) => a.position - b.position);
}

/** payload.boardId picks the board; omitted, the lane lands on the project's first board. */
async function createBoardColumnAsync(payload) {
    return call('/columns', 'POST', payload);
}

async function updateBoardColumnAsync(columnId, patch) {
    return call(`/columns/${enc(columnId)}`, 'PUT', patch);
}

async function deleteBoardColumnAsync(columnId) {
    return call(`/columns/${enc(columnId)}`, 'DELETE');
}

async function reorderBoardColumnsAsync(orderedIds, boardId = null) {
    const response = await call('/columns/order', 'PUT', { orderedIds, boardId: boardId || null });
    return (response?.columns || []).sort((a, b) => a.position - b.position);
}

// ---------------------------------------------- cards

async function getBoardCardsAsync(boardId = null, extra = {}) {
    const response = await call(withBoard('/cards', boardId), 'GET', null, extra);
    return (response?.cards || []).sort((a, b) => a.position - b.position || a.key.localeCompare(b.key));
}

async function getBoardCardActivityAsync(boardId, cardIds, extra = {}) {
    const response = await call('/cards/activity', 'POST', { boardId, cardIds }, extra);
    return { cards: response?.cards || [], activeAutomationColumnIds: response?.activeAutomationColumnIds || [] };
}

async function getBoardCardPageAsync(boardId, filters = {}, { columnId, offset = 0, continuationToken, signal } = {}) {
    const params = new URLSearchParams({ pageSize: '30' });
    for (const [key, value] of Object.entries(filters)) {
        if (value) params.set(key, value);
    }
    if (columnId) {
        params.set('columnId', columnId);
        params.set('offset', String(offset));
        if (continuationToken) params.set('continuationToken', continuationToken);
    }
    if (boardId) params.set('boardId', boardId);
    return call(`/cards?${params}`, 'GET', null, { signal });
}

async function getBoardCardAsync(cardId, extra = {}) {
    return call(`/cards/${enc(cardId)}`, 'GET', null, extra);
}

/** Search every local board. Ownership labels and ordering come from the server. */
async function searchBoardCardsAsync(query, extra = {}) {
    const response = await call(`/cards/search?q=${enc(query)}`, 'GET', null, extra);
    return response?.cards || [];
}

// Explicit local discovery routes resolve the card's owning project server-side.
// Never send a project path or reuse current-project launch/file APIs for these cards.
async function getLocalBoardCardAsync(cardId, extra = {}) {
    return call(`/local-cards/${enc(cardId)}`, 'GET', null, extra);
}

async function updateLocalBoardCardAsync(cardId, patch) {
    return call(`/local-cards/${enc(cardId)}`, 'PUT', patch);
}

async function addLocalBoardCommentAsync(cardId, { body }) {
    return call(`/local-cards/${enc(cardId)}/comments`, 'POST', { body });
}

async function getLocalCardLinkCandidatesAsync(cardId, query = '', extra = {}) {
    const response = await call(`/local-cards/${enc(cardId)}/links/candidates?q=${enc(query)}`, 'GET', null, extra);
    return response?.cards || [];
}

async function linkLocalCardAsync(cardId, linkedCardId) {
    return call(`/local-cards/${enc(cardId)}/links`, 'POST', { card: linkedCardId });
}

async function unlinkLocalCardAsync(cardId, linkedCardId) {
    return call(`/local-cards/${enc(cardId)}/links/${enc(linkedCardId)}`, 'DELETE');
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
async function launchBoardCardAsync(cardId, { selection, intent = 'work', review, question } = {}) {
    return call(`/cards/${enc(cardId)}/launch`, 'POST', { selection: selection || null, intent, ...(review ? { review } : {}), ...(question ? { question } : {}) });
}

// ---------------------------------------------- linked cards

async function getCardLinkCandidatesAsync(cardId, query = '', extra = {}) {
    const response = await call(`${cardId ? `/cards/${enc(cardId)}/links/candidates` : '/cards/link-candidates'}?q=${enc(query)}`, 'GET', null, extra);
    return response?.cards || [];
}

/** Merge and session/commit references stay in the open project, before the server result limit. */
async function getProjectCardCandidatesAsync(query = '', extra = {}) {
    const response = await call(`/cards/link-candidates?q=${enc(query)}&currentProjectOnly=true`, 'GET', null, extra);
    return response?.cards || [];
}

async function linkCardAsync(cardId, linkedCardId) {
    return call(`/cards/${enc(cardId)}/links`, 'POST', { card: linkedCardId });
}

// ---------------------------------------------- repository files (the composer's @ typeahead)

/** Repo-relative paths matching `query` (server-ranked, capped at 50) plus whether more matched. */
async function searchFilesAsync(query = '', extra = {}) {
    const response = await call(`/files?q=${enc(query)}`, 'GET', null, extra);
    return {
        files: Array.isArray(response?.files) ? response.files.filter(item => typeof item === 'string') : [],
        truncated: Boolean(response?.truncated)
    };
}

async function unlinkCardAsync(cardId, linkedCardId) {
    return call(`/cards/${enc(cardId)}/links/${enc(linkedCardId)}`, 'DELETE');
}

// ---------------------------------------------- comments

async function addBoardCommentAsync(cardId, { body }) {
    return call(`/cards/${enc(cardId)}/comments`, 'POST', { body });
}

// ---------------------------------------------- agent notes
//
// The scratchpad agents write over MCP (append_board_note). The card response already carries
// `notes`; these exist for a refresh and for a user-written note.

async function getCardNotesAsync(cardId) {
    return call(`/cards/${enc(cardId)}/notes`);
}

async function addCardNoteAsync(cardId, body) {
    return call(`/cards/${enc(cardId)}/notes`, 'POST', { body });
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

async function getCardAttachmentContentAsync(cardId, attachmentId, extra = {}) {
    return call(`/cards/${enc(cardId)}/attachments/${enc(attachmentId)}/content`, 'GET', null,
        { ...extra, responseType: 'blob' });
}


// ---------------------------------------------- commits

async function getCardCommitsAsync(cardId, extra = {}) {
    return call(`/cards/${enc(cardId)}/commits`, 'GET', null, extra);
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

async function getCardSessionsAsync(cardId, extra = {}) {
    return call(`/cards/${enc(cardId)}/sessions`, 'GET', null, extra);
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

async function deleteBoardCommentAsync(cardId, commentId) {
    return call(`/cards/${enc(cardId)}/comments/${enc(commentId)}`, 'DELETE');
}

async function mergeBoardCardsAsync(cardId, targetCard) {
    return call(`/cards/${enc(cardId)}/merge`, 'POST', { targetCard });
}

export const BoardApi = {
    deleteBoardCommentAsync,
    mergeBoardCardsAsync,
    attach,
    getBoardHistoryAsync,
    getBoardsAsync,
    createBoardAsync,
    updateBoardAsync,
    reorderBoardsAsync,
    getJiraConnectionAsync,
    saveJiraConnectionAsync,
    testJiraConnectionAsync,
    pullJiraAsync,
    getBoardSyncAsync,
    setBoardSyncAsync,
    syncBoardNowAsync,
    getBoardContextAsync,
    saveBoardContextAsync,
    getLaneAutomationAsync,
    getLaneRunningAgentsAsync,
    saveLaneAutomationAsync,
    getCardAutomationsAsync,
    skipCardAutomationAsync,
    runCardAutomationAsync,
    getCardContextAsync,
    deleteBoardAsync,
    getBoardColumnsAsync,
    createBoardColumnAsync,
    updateBoardColumnAsync,
    deleteBoardColumnAsync,
    reorderBoardColumnsAsync,
    getBoardCardsAsync,
    getBoardCardActivityAsync,
    getBoardCardPageAsync,
    getBoardCardAsync,
    searchBoardCardsAsync,
    getLocalBoardCardAsync,
    updateLocalBoardCardAsync,
    addLocalBoardCommentAsync,
    getLocalCardLinkCandidatesAsync,
    linkLocalCardAsync,
    unlinkLocalCardAsync,
    createBoardCardAsync,
    updateBoardCardAsync,
    deleteBoardCardAsync,
    moveBoardCardAsync,
    launchBoardCardAsync,
    getCardLinkCandidatesAsync,
    getProjectCardCandidatesAsync,
    linkCardAsync,
    unlinkCardAsync,
    searchFilesAsync,
    addBoardCommentAsync,
    getCardNotesAsync,
    addCardNoteAsync,
    addCardAttachmentAsync,
    deleteCardAttachmentAsync,
    getCardAttachmentContentAsync,
    getCardCommitsAsync,
    addCardCommitAsync,
    removeCardCommitAsync,
    getCommitDiffAsync,
    getCardSessionsAsync,
    addCardSessionAsync,
    updateCardSessionAsync,
    removeCardSessionAsync
};
