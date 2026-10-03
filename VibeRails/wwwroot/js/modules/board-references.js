import { BoardApi } from './board-api.js';
import { bindFileReferencePopup, findFileReferenceToken, formatFileReference } from './board-file-refs.js';
import { cardLabel } from './board-card-label.js';

export const SESSION_ID = /^(?:[a-f\d]{32}|[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12})$/i;
export const CARD_ID = /^[a-z][a-z\d]{0,15}-(?:[a-z\d]{5}-)?\d+$/i;
const PARTIAL_CARD_ID = /^[a-z][a-z\d]*-[a-z\d-]*$/i;
const MIXED_LIMIT = 50, MIXED_CARD_ROWS = 10;
export const canonicalSessionId = id => String(id).toLowerCase().replace(/^([a-f\d]{8})([a-f\d]{4})([a-f\d]{4})([a-f\d]{4})([a-f\d]{12})$/, '$1-$2-$3-$4-$5');
const sameId = (a, b) => String(a).replaceAll('-', '').toLowerCase() === String(b).replaceAll('-', '').toLowerCase();

/** Route exact identifiers without querying unrelated catalogs. Email/code are ordinary text. */
export function findBoardReferenceToken(value, caret) {
    const before = String(value).slice(0, caret);
    if ((before.match(/```/g) || []).length % 2 || (before.split('\n').at(-1).match(/`/g) || []).length % 2) return null;
    const match = before.match(/(?:^|\s)([!#])([^\s!#\[\]()`]{0,256})$/);
    if (match) return { start: before.length - match[2].length - 1, query: match[2], kind: match[1] };
    const file = findFileReferenceToken(value, caret);
    if (!file || file.query.includes('@')) return null;
    return { ...file, kind: '@' };
}

export function referenceSearchKind(token) {
    if (SESSION_ID.test(token.query) && token.kind !== '#') return 'session';
    if (CARD_ID.test(token.query)) return 'card';
    // A partly card-shaped `@` token (`@VB-`, `@board-api`) may be either; `#`/`!` have no file search.
    if (!token.quoted && PARTIAL_CARD_ID.test(token.query)) return token.kind === '@' ? 'files-and-cards' : 'card';
    return token.kind === '#' ? 'commit' : token.kind === '!' ? 'sessions' : 'files';
}

/** Browse and the empty-state hint follow the same routing as the search itself. */
export const referenceCanBrowse = token => token?.kind === '@' && ['files', 'files-and-cards'].includes(referenceSearchKind(token));
const EMPTY_NOTES = {
    files: 'No matching files. Try a card ID or session ID.', 'files-and-cards': 'No matching files or cards.',
    card: 'No matching cards.', session: 'No session with that ID in this project.',
    sessions: 'No matching sessions. Try a session ID or card ID.', commit: 'No matching commits. Type a SHA or a card ID.'
};
export const referenceEmptyNote = token => EMPTY_NOTES[referenceSearchKind(token)];

/** Uses existing authenticated APIs. Card sessions are read only after choosing/exactly naming a card. */
export function bindBoardReferences(input, { app, host, card, onLink }) {
    let recent;
    const normalizePath = value => { const path = String(value || '').replaceAll('\\', '/').replace(/\/$/, ''); return /^[a-z]:/i.test(path) ? path.toLowerCase() : path; };
    const root = normalizePath(app.data?.configs?.rootPath);
    const sessionRow = (session, prefix = '') => ({ kind: 'session', session,
        label: `${prefix}${session.displayName || session.sessionDisplayName || session.id} · ${session.id}` });
    const cardSearchRow = (c, token) => ({ kind: 'card-search', card: c, label: `${cardLabel(c)} · show ${token.kind === '#' ? 'commits' : 'sessions'}` });
    const search = async (token, options) => {
        const kind = referenceSearchKind(token);
        const query = token.query.toLowerCase();
        if (kind === 'session') {
            const local = (card.sessions || []).find(s => sameId(s.id, query));
            if (local) return { files: [sessionRow(local)] };
            try {
                const session = await app.apiCall(`/api/v1/chatHistory/${encodeURIComponent(canonicalSessionId(query))}`, 'GET', null,
                    { showLoading: false, ...options });
                return { files: session && normalizePath(session.workingDirectory) === root ? [sessionRow(session)] : [] };
            } catch (error) { if (error?.status === 404 || /not found|404/i.test(error?.message)) return { files: [] }; throw error; }
        }
        if (kind === 'card') {
            const cards = await BoardApi.getCardLinkCandidatesAsync(null, token.query, options);
            const exact = cards.find(c => [c.key, c.displayId, c.key?.replace(/-[A-Z0-9]{5}-/i, '-')].some(id => id?.toLowerCase() === query));
            if (!exact) return { files: cards.map(c => cardSearchRow(c, token)) };
            if (token.kind === '#') {
                const commits = await BoardApi.getCardCommitsAsync(exact.id, options);
                return { files: (commits || []).map(commit => ({ kind: 'commit', commit, label: `${commit.shortSha} · ${commit.message}` })) };
            }
            const sessions = await BoardApi.getCardSessionsAsync(exact.id, options);
            return { files: [ ...(token.kind === '@' ? [{ kind: 'card', card: exact, label: cardLabel(exact) }] : []),
                ...(sessions || []).map(s => sessionRow(s, `${exact.displayId || exact.key} · `)) ] };
        }
        if (kind === 'files-and-cards') {
            // Files first (the `@` default), then cards; bounded, with room kept for a few cards.
            const [found, cards] = await Promise.all([BoardApi.searchFilesAsync(token.query, options),
                BoardApi.getCardLinkCandidatesAsync(null, token.query, options)]);
            const cardRows = cards.slice(0, Math.max(MIXED_CARD_ROWS, MIXED_LIMIT - found.files.length)).map(c => cardSearchRow(c, token));
            return { files: [...found.files.slice(0, MIXED_LIMIT - cardRows.length), ...cardRows],
                truncated: found.truncated || found.files.length + cards.length > MIXED_LIMIT };
        }
        if (kind === 'commit') {
            const rows = (card.commits || []).filter(c => `${c.sha} ${c.message}`.toLowerCase().includes(query))
                .map(commit => ({ kind: 'commit', commit, label: `${commit.shortSha} · ${commit.message}` }));
            if (/^[a-f\d]{7,40}$/i.test(query) && !rows.length) rows.push({ kind: 'commit', commit: { sha: query }, label: `Link commit ${query}` });
            return { files: rows };
        }
        if (kind === 'sessions') {
            // One bounded recent-history read per editor; identifiers above never take this path.
            recent ??= app.apiCall(`/api/v1/chatHistory?page=1&pageSize=100&preferredWorkingDirectory=${encodeURIComponent(app.data?.configs?.rootPath || '')}`,
                'GET', null, { showLoading: false, ...options }).then(response => response?.items || []).catch(error => { recent = null; throw error; });
            const history = await recent;
            const sessions = [...(card.sessions || []), ...history.filter(s => normalizePath(s.workingDirectory) === root)];
            const seen = new Set();
            return { files: sessions.filter(s => !seen.has(s.id) && seen.add(s.id) && `${s.displayName || s.sessionDisplayName || ''} ${s.id}`.toLowerCase().includes(query)).slice(0, 50).map(s => sessionRow(s)) };
        }
        return BoardApi.searchFilesAsync(token.query, options);
    };
    return bindFileReferencePopup(input, { app, host, findToken: findBoardReferenceToken, search,
        label: 'Files, cards, sessions and commits', canBrowse: referenceCanBrowse, emptyNote: referenceEmptyNote,
        onPick: async (item, token) => {
            if (typeof item === 'string') return formatFileReference(item);
            if (item.kind === 'card-search') return { searchText: `${token.kind}${item.card.displayId || item.card.key}` };
            if (item.kind === 'card') return `@[${item.card.displayId || item.card.key}](card:${item.card.id})`;
            await onLink(item);
            return item.kind === 'session' ? `!${item.session.id}` : `#${item.commit.sha}`;
        }
    });
}
