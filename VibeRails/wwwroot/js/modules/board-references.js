import { BoardApi } from './board-api.js';
import { bindFileReferencePopup, findFileReferenceToken, formatFileReference } from './board-file-refs.js';
import { cardLabel } from './board-card-label.js';

export const SESSION_ID = /^(?:[a-f\d]{32}|[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12})$/i;
export const CARD_ID = /^[a-z][a-z\d]{0,15}-(?:[a-z\d]{5}-)?\d+$/i;
const PARTIAL_CARD_ID = /^[a-z][a-z\d]*-[a-z\d-]*$/i;
export const canonicalSessionId = id => String(id).toLowerCase().replace(/^([a-f\d]{8})([a-f\d]{4})([a-f\d]{4})([a-f\d]{4})([a-f\d]{12})$/, '$1-$2-$3-$4-$5');
const sameId = (a, b) => String(a).replaceAll('-', '').toLowerCase() === String(b).replaceAll('-', '').toLowerCase();

/** Route exact identifiers without querying unrelated catalogs. Email/code are ordinary text. */
export function findBoardReferenceToken(value, caret) {
    const before = String(value).slice(0, caret);
    if ((before.match(/```/g) || []).length % 2 || (before.split('\n').at(-1).match(/`/g) || []).length % 2) return null;
    const file = findFileReferenceToken(value, caret);
    if (file && !file.query.includes('@')) return { ...file, kind: '@' };
    const match = before.match(/(?:^|\s)(!)([^\r\n!#@\[\]()"`]{0,256})$/)
        || before.match(/(?:^|\s)(#)([^\s!#\[\]()`]{0,256})$/);
    if (match) {
        // Spaces belong to a title search, but a completed session reference is finished.
        if (/^\s/.test(match[2]) || SESSION_ID.test(match[2].split(/\s/)[0]) && /\s/.test(match[2])) return null;
        return { start: before.length - match[2].length - 1, query: match[2], kind: match[1] };
    }
    return null;
}

export function referenceSearchKind(token) {
    if (token.kind === '@') return 'files';
    if (token.kind === '!') return SESSION_ID.test(token.query) ? 'session' : 'card';
    if (CARD_ID.test(token.query) || PARTIAL_CARD_ID.test(token.query)) return 'card';
    return 'commit';
}

/** Browse and the empty-state hint follow the same routing as the search itself. */
export const referenceCanBrowse = token => token?.kind === '@';
const EMPTY_NOTES = {
    files: 'No matching files.', card: 'No matching cards. Search by title, keywords or card ID.',
    session: 'No session with that ID in this project.', commit: 'No matching commits. Type a SHA or a card ID.'
};
export const referenceEmptyNote = token => EMPTY_NOTES[referenceSearchKind(token)];

/** Uses existing authenticated APIs. Card sessions are read only after choosing/exactly naming a card. */
export function bindBoardReferences(input, { app, host, card, onLink }) {
    const normalizePath = value => { const path = String(value || '').replaceAll('\\', '/').replace(/\/$/, ''); return /^[a-z]:/i.test(path) ? path.toLowerCase() : path; };
    const root = normalizePath(app.data?.configs?.rootPath);
    const sessionRow = session => {
        const started = session.startedUTC || session.createdAt;
        const date = started && new Date(started);
        const when = date && !Number.isNaN(date.getTime()) ? date.toLocaleString() : '';
        const status = typeof session.active === 'boolean' ? (session.active ? 'Open' : 'Recorded')
            : session.endedUTC ? 'Ended' : '';
        return { kind: 'session', session, label: session.displayName || session.sessionDisplayName || 'Session',
            detail: [session.cli, when && `${session.startedUTC ? 'Started' : 'Linked'} ${when}`,
                session.environmentName, session.isReview ? 'Review' : session.isAutomation ? 'Automation' : '', status].filter(Boolean).join(' · '),
            title: session.id };
    };
    const cardSearchRow = (c, token) => ({ kind: 'card-search', card: c,
        label: `${cardLabel(c)} · show ${token.kind === '#' ? 'commits' : 'sessions'}` });
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
            const cards = (await BoardApi.getProjectCardCandidatesAsync(token.query, options))
                .filter(candidate => candidate.isCurrentProject !== false);
            const exact = cards.find(c => [c.key, c.displayId, c.key?.replace(/-[A-Z0-9]{5}-/i, '-')].some(id => id?.toLowerCase() === query));
            if (!exact) return { files: cards.map(c => cardSearchRow(c, token)) };
            if (token.kind === '#') {
                const commits = await BoardApi.getCardCommitsAsync(exact.id, options);
                return { files: (commits || []).map(commit => ({ kind: 'commit', commit, label: `${commit.shortSha} · ${commit.message}` })) };
            }
            const sessions = await BoardApi.getCardSessionsAsync(exact.id, options);
            return { files: [...(sessions || []).map(sessionRow),
                { kind: 'card', card: exact, label: `Reference card ${cardLabel(exact)}`,
                    detail: sessions?.length ? 'Use the card itself' : 'No sessions linked to this card.' }] };
        }
        if (kind === 'commit') {
            const rows = (card.commits || []).filter(c => `${c.sha} ${c.message}`.toLowerCase().includes(query))
                .map(commit => ({ kind: 'commit', commit, label: `${commit.shortSha} · ${commit.message}` }));
            if (/^[a-f\d]{7,40}$/i.test(query) && !rows.length) rows.push({ kind: 'commit', commit: { sha: query }, label: `Link commit ${query}` });
            return { files: rows };
        }
        return BoardApi.searchFilesAsync(token.query, options);
    };
    return bindFileReferencePopup(input, { app, host, findToken: findBoardReferenceToken, search,
        label: 'Files, cards, sessions and commits', canBrowse: referenceCanBrowse, emptyNote: referenceEmptyNote,
        onPick: async (item, token) => {
            if (typeof item === 'string') return formatFileReference(item);
            if (item.kind === 'card-search') return { searchText: `${token.kind}${item.card.key}` };
            if (item.kind === 'card') return `![${item.card.displayId || item.card.key}](card:${item.card.id})`;
            await onLink(item);
            return item.kind === 'session' ? `!${item.session.id}` : `#${item.commit.sha}`;
        }
    });
}
