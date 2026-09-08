// ============================================
// Board API — local placeholder data layer
// ============================================
//
// The board view is wired end to end against this module, but nothing here
// talks to the backend yet. Every function has the shape the real endpoint will
// have, so swapping in the server is a body-only change:
//
//     async function getBoardCardsAsync() {
//         return app.apiCall('/api/v1/board/cards', 'GET');
//     }
//
// Keep the names and the return shapes and BoardController needs no edits.
// Data lives in localStorage so a reload keeps the user's edits; when storage
// is blocked (some VS Code webviews) the seed stays in memory for the session.

const STORAGE_KEY = 'viberails.board.local.v2';
const SEED_VERSION = 2;

let memoryDb = null;

function copy(value) {
    return structuredClone(value);
}

function nowIso() {
    return new Date().toISOString();
}

// Cards gained collections over time. Defaulting them here (rather than in the
// seed) means a card written by an older build, or a seed entry that simply has
// nothing to declare, can never hand the UI an undefined array to iterate.
function normalizeCard(card) {
    card.comments = Array.isArray(card.comments) ? card.comments : [];
    card.commits = Array.isArray(card.commits) ? card.commits : [];
    card.sessions = Array.isArray(card.sessions) ? card.sessions : [];
    card.attachments = Array.isArray(card.attachments) ? card.attachments : [];
    return card;
}

function normalizeDb(db) {
    db.cards.forEach(normalizeCard);
    return db;
}

function loadDb() {
    if (memoryDb) return memoryDb;
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        if (raw) {
            const parsed = JSON.parse(raw);
            if (parsed?.version === SEED_VERSION) {
                memoryDb = normalizeDb(parsed);
                return memoryDb;
            }
        }
    } catch {
        // Private mode or a webview that blocks storage: fall through to the seed.
    }
    memoryDb = normalizeDb(createSeed());
    saveDb(memoryDb);
    return memoryDb;
}

function saveDb(db) {
    memoryDb = db;
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(db));
    } catch (error) {
        // The in-memory copy still holds the write, so the current view stays
        // correct — but the change will not survive a reload, and the caller has
        // to be able to say so. Silently swallowing this loses user content,
        // which images made a routine occurrence rather than a corner case.
        const quota = error?.name === 'QuotaExceededError'
            || error?.name === 'NS_ERROR_DOM_QUOTA_REACHED';
        const failure = new Error(quota
            ? 'Out of local storage space. Remove some images or reset the sample board; this change will not survive a reload.'
            : `Could not persist the board locally: ${error?.message || error}`);
        failure.code = quota ? 'QUOTA_EXCEEDED' : 'PERSIST_FAILED';
        failure.cause = error;
        throw failure;
    }
}

function notFound(kind, id) {
    const error = new Error(`${kind} not found: ${id}`);
    error.code = 'NOT_FOUND';
    throw error;
}

function nextKey(db) {
    let max = 1000;
    for (const card of db.cards) {
        const match = /^VB-(\d+)$/.exec(card.key);
        if (match) max = Math.max(max, Number(match[1]));
    }
    return `VB-${max + 1}`;
}

function newId(prefix) {
    return `${prefix}_${crypto.randomUUID().slice(0, 8)}`;
}

function renumberColumn(db, columnId) {
    db.cards
        .filter(card => card.columnId === columnId)
        .sort((a, b) => a.position - b.position)
        .forEach((card, index) => {
            card.position = index;
        });
}

function createSeed() {
    const createdAt = '2026-09-01T14:00:00.000Z';
    const members = [
        { id: 'm_maya', name: 'Maya Chen', initials: 'MC', role: 'Frontend', color: '#3b82f6' },
        { id: 'm_jordan', name: 'Jordan Hale', initials: 'JH', role: 'Backend', color: '#06b6d4' },
        { id: 'm_priya', name: 'Priya Nair', initials: 'PN', role: 'Full stack', color: '#f59e0b' },
        { id: 'm_sam', name: 'Sam Okonkwo', initials: 'SO', role: 'Design', color: '#a855f7' },
        { id: 'm_alex', name: 'Alex Rivera', initials: 'AR', role: 'QA', color: '#ec4899' }
    ];

    const columns = [
        { id: 'col_backlog', name: 'Backlog', wipLimit: null, position: 0, color: '#64748b' },
        { id: 'col_ready', name: 'Ready', wipLimit: 8, position: 1, color: '#3b82f6' },
        { id: 'col_build', name: 'Build', wipLimit: 4, position: 2, color: '#06b6d4' },
        { id: 'col_review', name: 'Review', wipLimit: 3, position: 3, color: '#f59e0b' },
        { id: 'col_shipped', name: 'Shipped', wipLimit: null, position: 4, color: '#10b981' }
    ];

    const comment = (id, authorId, body, at) => ({ id, authorId, body, createdAt: at });

    const cards = [
        {
            id: 'card_1018',
            key: 'VB-1018',
            columnId: 'col_build',
            position: 0,
            title: 'Fix refresh-token race on 401',
            description: 'Two overlapping 401s both try to rotate the refresh token. Second writer wins and the first request retries with a dead token.\n\nRepro: throttle the auth endpoint, fire two GETs in parallel after expiry.',
            assigneeId: 'm_jordan',
            priority: 'critical',
            points: 5,
            tags: ['auth', 'bug'],
            blocked: false,
            comments: [
                comment('cm_1', 'm_maya', 'I can reproduce this from the board when two card saves fire at once.', '2026-09-04T16:12:00.000Z'),
                comment('cm_2', 'm_jordan', 'Traced it. Both callers reach `rotate()` before either has written the new token, so the second write wins and the first request retries with a token that is already dead.\n\n```csharp\n// before: nothing serialises the rotate\nvar token = await _client.RotateAsync(refreshToken);\n_store.Write(token);\n```\n\nA single-flight gate fixes it locally, but the real answer is a jti on the server so a replayed rotate is rejected outright.', '2026-09-07T11:40:00.000Z'),
                comment('cm_5', 'm_alex', 'Confirmed against the throttled endpoint. Ran the parallel-GET repro 200x with no dead-token retries:\n\n```\n$ dotnet test --filter RefreshToken\nPassed!  - Failed: 0, Passed: 14, Skipped: 0\n```', '2026-09-08T09:05:00.000Z')
            ],
            commits: [
                {
                    sha: 'a3f91c27b4e8d5106f2a9c33be7714d0a8e5b291',
                    shortSha: 'a3f91c2',
                    author: 'Jordan Hale',
                    message: 'Serialise refresh-token rotation behind a single-flight gate',
                    committedAt: '2026-09-07T11:38:00.000Z',
                    files: [
                        {
                            fileName: 'src/Auth/TokenClient.cs',
                            language: 'csharp',
                            originalContent: 'public class TokenClient\n{\n    private readonly ITokenStore _store;\n\n    public async Task<Token> RefreshAsync(string refreshToken)\n    {\n        var token = await _client.RotateAsync(refreshToken);\n        _store.Write(token);\n        return token;\n    }\n}\n',
                            modifiedContent: 'public class TokenClient\n{\n    private readonly ITokenStore _store;\n    private readonly SemaphoreSlim _rotateGate = new(1, 1);\n    private Task<Token>? _inFlight;\n\n    public async Task<Token> RefreshAsync(string refreshToken)\n    {\n        // Two overlapping 401s must not both rotate: the second write would\n        // win and the first caller would retry with a dead token.\n        await _rotateGate.WaitAsync();\n        try\n        {\n            _inFlight ??= RotateAndStoreAsync(refreshToken);\n        }\n        finally\n        {\n            _rotateGate.Release();\n        }\n\n        return await _inFlight;\n    }\n\n    private async Task<Token> RotateAndStoreAsync(string refreshToken)\n    {\n        var token = await _client.RotateAsync(refreshToken);\n        _store.Write(token);\n        _inFlight = null;\n        return token;\n    }\n}\n'
                        },
                        {
                            fileName: 'tests/Auth/TokenClientTests.cs',
                            language: 'csharp',
                            originalContent: '',
                            modifiedContent: '[Fact]\npublic async Task ConcurrentRefreshRotatesOnce()\n{\n    var client = new TokenClient(store, rotator);\n\n    var results = await Task.WhenAll(\n        client.RefreshAsync("stale"),\n        client.RefreshAsync("stale"));\n\n    Assert.Equal(1, rotator.RotateCallCount);\n    Assert.Equal(results[0].Value, results[1].Value);\n}\n'
                        }
                    ]
                },
                {
                    sha: '5c02be914aa7f36d81b0e4527cc9a1d3f6802b7e',
                    shortSha: '5c02be9',
                    author: 'Jordan Hale',
                    message: 'Log the rotate outcome so the race is visible in a session replay',
                    committedAt: '2026-09-07T09:12:00.000Z',
                    files: [
                        {
                            fileName: 'src/Auth/TokenClient.cs',
                            language: 'csharp',
                            originalContent: '        var token = await _client.RotateAsync(refreshToken);\n        _store.Write(token);\n        return token;\n',
                            modifiedContent: '        var token = await _client.RotateAsync(refreshToken);\n        _logger.LogDebug("Rotated refresh token, jti={Jti}", token.Jti);\n        _store.Write(token);\n        return token;\n'
                        }
                    ]
                }
            ],
            sessions: [
                { id: '8dd5fe21-4c2b-4f9a-9a31-2b7c6e015f44', displayName: 'Repro + single-flight fix', createdAt: '2026-09-07T09:00:00.000Z' },
                { id: 'b41c7a90-2f66-4d18-8e05-7a3d9c62118b', displayName: 'Verification run', createdAt: '2026-09-08T09:00:00.000Z' }
            ],
            createdAt,
            updatedAt: '2026-09-08T09:05:00.000Z'
        },
        {
            id: 'card_1020',
            key: 'VB-1020',
            columnId: 'col_build',
            position: 1,
            title: 'Honour lane WIP limits on drop',
            description: 'Allow the drop, but paint the lane and toast when it is over its limit. Product still deciding whether to block the drop.',
            assigneeId: 'm_maya',
            priority: 'high',
            points: 3,
            tags: ['board', 'ux'],
            blocked: true,
            comments: [
                comment('cm_3', 'm_sam', 'Warn, do not block. People dump a whole review lane at once on ship day.', '2026-09-05T09:02:00.000Z')
            ],
            createdAt,
            updatedAt: '2026-09-06T18:20:00.000Z'
        },
        {
            id: 'card_1021',
            key: 'VB-1021',
            columnId: 'col_build',
            position: 2,
            title: 'Keyboard the assignee picker',
            description: 'Arrow keys through the list, Enter assigns, Esc closes. Typeahead by name or initials.',
            assigneeId: 'm_maya',
            priority: 'medium',
            points: 2,
            tags: ['a11y', 'ui'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: createdAt
        },
        {
            id: 'card_1014',
            key: 'VB-1014',
            columnId: 'col_review',
            position: 0,
            title: 'Card editor: save on Ctrl+Enter',
            description: 'Title and description already debounce. Add an explicit shortcut and a saved indicator.',
            assigneeId: 'm_priya',
            priority: 'medium',
            points: 2,
            tags: ['ui'],
            blocked: false,
            comments: [
                comment('cm_4', 'm_alex', 'Works in Chrome. Check the webview keybinding collision with VS Code.', '2026-09-08T08:15:00.000Z')
            ],
            createdAt,
            updatedAt: '2026-09-08T08:15:00.000Z'
        },
        {
            id: 'card_1016',
            key: 'VB-1016',
            columnId: 'col_review',
            position: 1,
            title: 'Drag ghost contrast in high-contrast themes',
            description: 'Ghost opacity 0.35 disappears on vscode-high-contrast. Use an outline instead of fading the card.',
            assigneeId: 'm_sam',
            priority: 'high',
            points: 3,
            tags: ['a11y', 'theme'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: '2026-09-07T19:00:00.000Z'
        },
        {
            id: 'card_1024',
            key: 'VB-1024',
            columnId: 'col_ready',
            position: 0,
            title: 'Remember board filters across reloads',
            description: 'Persist the search, assignee, priority and tag filters so a reload restores the same slice of the board.',
            assigneeId: 'm_priya',
            priority: 'medium',
            points: 3,
            tags: ['ux'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: createdAt
        },
        {
            id: 'card_1026',
            key: 'VB-1026',
            columnId: 'col_ready',
            position: 1,
            title: 'Billing CSV export for EU orgs',
            description: 'Include the VAT line and IBAN. Encode UTF-8 with a BOM so Excel on Windows does not smash names.',
            assigneeId: 'm_jordan',
            priority: 'high',
            points: 8,
            tags: ['billing', 'export'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: createdAt
        },
        {
            id: 'card_1027',
            key: 'VB-1027',
            columnId: 'col_ready',
            position: 2,
            title: 'Empty-lane copy pass',
            description: 'Each lane should say what to do next, not "No items".',
            assigneeId: 'm_sam',
            priority: 'low',
            points: 1,
            tags: ['copy', 'ux'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: createdAt
        },
        {
            id: 'card_1028',
            key: 'VB-1028',
            columnId: 'col_ready',
            position: 3,
            title: 'Rate-limit headers on the board client',
            description: 'Return Retry-After so the UI can show a cooldown toast. Placeholder until the real API exists.',
            assigneeId: 'm_jordan',
            priority: 'low',
            points: 2,
            tags: ['api'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: createdAt
        },
        {
            id: 'card_1031',
            key: 'VB-1031',
            columnId: 'col_backlog',
            position: 0,
            title: 'Spike: board events over the app event socket',
            description: 'If the existing AppEventClient can carry board changes, a second tab can follow along without polling.',
            assigneeId: null,
            priority: 'low',
            points: 3,
            tags: ['spike'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: createdAt
        },
        {
            id: 'card_1030',
            key: 'VB-1030',
            columnId: 'col_backlog',
            position: 1,
            title: 'Docs: board API contract',
            description: 'One page that lists every board endpoint, payload, and response shape.',
            assigneeId: 'm_priya',
            priority: 'medium',
            points: 2,
            tags: ['docs'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: createdAt
        },
        {
            id: 'card_1029',
            key: 'VB-1029',
            columnId: 'col_backlog',
            position: 2,
            title: 'Priority rail contrast in light VS Code themes',
            description: 'The medium-priority rail is too close to the card edge on a light editor background.',
            assigneeId: 'm_sam',
            priority: 'medium',
            points: 1,
            tags: ['theme'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: createdAt
        },
        {
            id: 'card_1009',
            key: 'VB-1009',
            columnId: 'col_shipped',
            position: 0,
            title: 'Seed data loader with localStorage fallback',
            description: 'If storage is blocked (some webviews), keep the board in memory for the session.',
            assigneeId: 'm_priya',
            priority: 'high',
            points: 5,
            tags: ['api'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: '2026-09-04T12:10:00.000Z'
        },
        {
            id: 'card_1011',
            key: 'VB-1011',
            columnId: 'col_shipped',
            position: 1,
            title: 'Avatar initials from member colour',
            description: 'No image pipeline. Initials plus a stable colour is enough for five people.',
            assigneeId: 'm_sam',
            priority: 'low',
            points: 1,
            tags: ['ui'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: '2026-09-02T15:44:00.000Z'
        },
        {
            id: 'card_1012',
            key: 'VB-1012',
            columnId: 'col_shipped',
            position: 2,
            title: 'Surface board failures as toasts',
            description: 'The API throws with a message, the UI surfaces it. No silent catch.',
            assigneeId: 'm_alex',
            priority: 'medium',
            points: 2,
            tags: ['ux'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: '2026-09-03T10:22:00.000Z'
        },
        {
            id: 'card_1004',
            key: 'VB-1004',
            columnId: 'col_shipped',
            position: 3,
            title: 'Board: horizontal lane scroll',
            description: 'Lanes keep their width, the board scrolls sideways and the page does not.',
            assigneeId: 'm_maya',
            priority: 'high',
            points: 3,
            tags: ['board'],
            blocked: false,
            comments: [],
            createdAt,
            updatedAt: '2026-09-02T09:18:00.000Z'
        }
    ];

    return { version: SEED_VERSION, members, columns, cards: cards.map(normalizeCard) };
}

async function getBoardMembersAsync() {
    return copy(loadDb().members);
}

async function getBoardColumnsAsync() {
    return copy(loadDb().columns).sort((a, b) => a.position - b.position);
}

async function createBoardColumnAsync(payload) {
    const db = loadDb();
    const column = {
        id: newId('col'),
        name: String(payload.name || 'New lane').trim() || 'New lane',
        wipLimit: payload.wipLimit ?? null,
        position: db.columns.length,
        color: payload.color || '#3b82f6'
    };
    db.columns.push(column);
    saveDb(db);
    return copy(column);
}

async function updateBoardColumnAsync(columnId, patch) {
    const db = loadDb();
    const column = db.columns.find(c => c.id === columnId);
    if (!column) notFound('Lane', columnId);
    if (patch.name != null) column.name = String(patch.name).trim() || column.name;
    if ('wipLimit' in patch) {
        column.wipLimit = patch.wipLimit === '' || patch.wipLimit == null ? null : Number(patch.wipLimit);
    }
    if (patch.color) column.color = patch.color;
    saveDb(db);
    return copy(column);
}

async function deleteBoardColumnAsync(columnId) {
    const db = loadDb();
    const column = db.columns.find(c => c.id === columnId);
    if (!column) notFound('Lane', columnId);
    const remaining = db.columns.filter(c => c.id !== columnId);
    if (remaining.length === 0) {
        throw new Error('The board needs at least one lane.');
    }
    const fallback = remaining.sort((a, b) => a.position - b.position)[0];
    db.cards.forEach(card => {
        if (card.columnId === columnId) {
            card.columnId = fallback.id;
            card.updatedAt = nowIso();
        }
    });
    db.columns = remaining;
    remaining.forEach((c, index) => {
        c.position = index;
    });
    renumberColumn(db, fallback.id);
    saveDb(db);
    return { ok: true, movedToColumnId: fallback.id };
}

async function reorderBoardColumnsAsync(orderedIds) {
    const db = loadDb();
    orderedIds.forEach((id, index) => {
        const column = db.columns.find(c => c.id === id);
        if (column) column.position = index;
    });
    saveDb(db);
    return copy(db.columns.sort((a, b) => a.position - b.position));
}

async function getBoardCardsAsync() {
    return copy(
        loadDb().cards.sort((a, b) => a.position - b.position || a.key.localeCompare(b.key))
    );
}

async function getBoardCardAsync(cardId) {
    const card = loadDb().cards.find(c => c.id === cardId);
    if (!card) notFound('Card', cardId);
    return copy(card);
}

async function createBoardCardAsync(payload) {
    const db = loadDb();
    const columnId = payload.columnId || [...db.columns].sort((a, b) => a.position - b.position)[0]?.id;
    if (!columnId) throw new Error('No lane available.');
    const siblings = db.cards.filter(c => c.columnId === columnId);
    const card = {
        id: newId('card'),
        key: nextKey(db),
        columnId,
        position: siblings.length,
        title: String(payload.title || 'Untitled').trim() || 'Untitled',
        description: String(payload.description || ''),
        assigneeId: payload.assigneeId || null,
        priority: payload.priority || 'medium',
        points: payload.points == null || payload.points === '' ? null : Number(payload.points),
        tags: Array.isArray(payload.tags) ? payload.tags.map(String) : [],
        blocked: Boolean(payload.blocked),
        comments: [],
        commits: [],
        sessions: [],
        attachments: [],
        createdAt: nowIso(),
        updatedAt: nowIso()
    };
    db.cards.push(card);
    saveDb(db);
    return copy(card);
}

async function updateBoardCardAsync(cardId, patch) {
    const db = loadDb();
    const card = db.cards.find(c => c.id === cardId);
    if (!card) notFound('Card', cardId);

    const nextColumnId = patch.columnId;
    const moving = nextColumnId && nextColumnId !== card.columnId;

    const fields = ['title', 'description', 'assigneeId', 'priority', 'points', 'tags', 'blocked'];
    for (const field of fields) {
        if (!(field in patch)) continue;
        if (field === 'title') card.title = String(patch.title || '').trim() || card.title;
        else if (field === 'points') card.points = patch.points === '' || patch.points == null ? null : Number(patch.points);
        else if (field === 'assigneeId') card.assigneeId = patch.assigneeId || null;
        else if (field === 'tags') card.tags = Array.isArray(patch.tags) ? patch.tags.map(String) : card.tags;
        else if (field === 'blocked') card.blocked = Boolean(patch.blocked);
        else card[field] = patch[field];
    }

    if (moving) {
        const oldColumn = card.columnId;
        card.columnId = nextColumnId;
        card.position = db.cards.filter(c => c.columnId === nextColumnId && c.id !== card.id).length;
        renumberColumn(db, oldColumn);
        renumberColumn(db, nextColumnId);
    }

    card.updatedAt = nowIso();
    saveDb(db);
    return copy(card);
}

async function deleteBoardCardAsync(cardId) {
    const db = loadDb();
    const card = db.cards.find(c => c.id === cardId);
    if (!card) notFound('Card', cardId);
    const columnId = card.columnId;
    db.cards = db.cards.filter(c => c.id !== cardId);
    renumberColumn(db, columnId);
    saveDb(db);
    return { ok: true };
}

async function moveBoardCardAsync(cardId, { columnId, position }) {
    const db = loadDb();
    const card = db.cards.find(c => c.id === cardId);
    if (!card) notFound('Card', cardId);
    const target = db.columns.find(c => c.id === columnId);
    if (!target) notFound('Lane', columnId);

    const fromColumn = card.columnId;
    card.columnId = columnId;
    card.updatedAt = nowIso();

    const others = db.cards
        .filter(c => c.columnId === columnId && c.id !== cardId)
        .sort((a, b) => a.position - b.position);
    const index = Math.max(0, Math.min(Number(position) || 0, others.length));
    others.splice(index, 0, card);
    others.forEach((c, i) => {
        c.position = i;
    });
    if (fromColumn !== columnId) renumberColumn(db, fromColumn);
    saveDb(db);
    return copy(card);
}

async function addBoardCommentAsync(cardId, { body, authorId }) {
    const db = loadDb();
    const card = db.cards.find(c => c.id === cardId);
    if (!card) notFound('Card', cardId);
    const text = String(body || '').trim();
    if (!text) throw new Error('Comment is empty.');
    const comment = {
        id: newId('cm'),
        authorId: authorId || null,
        body: text,
        createdAt: nowIso()
    };
    card.comments.push(comment);
    card.updatedAt = nowIso();
    saveDb(db);
    return copy(comment);
}

function findCard(db, cardId) {
    const card = db.cards.find(c => c.id === cardId);
    if (!card) notFound('Card', cardId);
    return normalizeCard(card);
}

// ============================================
// Attachments
// ============================================
//
// The contract that matters: this returns a URL, and the caller renders that URL.
// The placeholder stores a data: URL inline; the real backend will store the bytes
// and return a served URL instead. Because the caller never sees which, swapping
// the body below changes nothing above this layer.

async function addCardAttachmentAsync(cardId, { name, dataUrl, bytes, mimeType } = {}) {
    if (!dataUrl) throw new Error('The attachment has no content.');
    const db = loadDb();
    const card = findCard(db, cardId);
    const attachment = {
        id: newId('att'),
        name: String(name || 'image').slice(0, 120),
        url: dataUrl,
        mimeType: mimeType || 'image/png',
        bytes: Number(bytes) || dataUrl.length,
        createdAt: nowIso()
    };
    card.attachments.push(attachment);
    card.updatedAt = nowIso();
    saveDb(db);
    return copy(attachment);
}

async function deleteCardAttachmentAsync(cardId, attachmentId) {
    const db = loadDb();
    const card = findCard(db, cardId);
    card.attachments = card.attachments.filter(a => a.id !== attachmentId);
    card.updatedAt = nowIso();
    saveDb(db);
    return { ok: true };
}

// ============================================
// Commits
// ============================================

async function getCardCommitsAsync(cardId) {
    const db = loadDb();
    const card = findCard(db, cardId);
    // Diff payloads stay out of the list; the viewer asks for one by sha.
    return copy(card.commits.map(({ files, ...meta }) => meta))
        .sort((a, b) => String(b.committedAt).localeCompare(String(a.committedAt)));
}

async function addCardCommitAsync(cardId, payload = {}) {
    const sha = String(payload.sha || '').trim();
    if (!/^[0-9a-f]{7,40}$/i.test(sha)) {
        throw new Error('That does not look like a commit sha.');
    }
    const db = loadDb();
    const card = findCard(db, cardId);
    if (card.commits.some(c => c.sha === sha)) {
        throw new Error(`${sha.slice(0, 7)} is already on this card.`);
    }
    const commit = {
        sha,
        shortSha: sha.slice(0, 7),
        author: String(payload.author || 'Unknown').slice(0, 120),
        message: String(payload.message || '(no message)').slice(0, 500),
        committedAt: payload.committedAt || nowIso(),
        files: Array.isArray(payload.files) ? payload.files : []
    };
    card.commits.push(commit);
    card.updatedAt = nowIso();
    saveDb(db);
    const { files, ...meta } = commit;
    return copy(meta);
}

async function removeCardCommitAsync(cardId, sha) {
    const db = loadDb();
    const card = findCard(db, cardId);
    card.commits = card.commits.filter(c => c.sha !== sha);
    card.updatedAt = nowIso();
    saveDb(db);
    return { ok: true };
}

/**
 * Files for one commit, in the exact shape the Monaco diff viewer consumes and
 * that /api/v1/sandboxes/{id}/diff already returns:
 *   { fileName, language, originalContent, modifiedContent }
 */
async function getCommitDiffAsync(cardId, sha) {
    const db = loadDb();
    const card = findCard(db, cardId);
    const commit = card.commits.find(c => c.sha === sha);
    if (!commit) notFound('Commit', sha);
    return { files: copy(commit.files || []), totalChanges: (commit.files || []).length };
}

// ============================================
// Sessions
// ============================================
//
// A session is just an id and a display name for now; clicking one opens a stub.

async function getCardSessionsAsync(cardId) {
    const db = loadDb();
    return copy(findCard(db, cardId).sessions);
}

async function addCardSessionAsync(cardId, { id, displayName } = {}) {
    const db = loadDb();
    const card = findCard(db, cardId);
    const sessionId = String(id || crypto.randomUUID());
    if (card.sessions.some(session => session.id === sessionId)) {
        throw new Error('That session is already on this card.');
    }
    const session = {
        id: sessionId,
        displayName: String(displayName || '').trim() || 'Working session',
        createdAt: nowIso()
    };
    card.sessions.push(session);
    card.updatedAt = nowIso();
    saveDb(db);
    return copy(session);
}

async function updateCardSessionAsync(cardId, sessionId, patch = {}) {
    const db = loadDb();
    const card = findCard(db, cardId);
    const session = card.sessions.find(item => item.id === sessionId);
    if (!session) notFound('Session', sessionId);
    if (patch.displayName != null) {
        session.displayName = String(patch.displayName).trim() || session.displayName;
    }
    card.updatedAt = nowIso();
    saveDb(db);
    return copy(session);
}

async function removeCardSessionAsync(cardId, sessionId) {
    const db = loadDb();
    const card = findCard(db, cardId);
    card.sessions = card.sessions.filter(session => session.id !== sessionId);
    card.updatedAt = nowIso();
    saveDb(db);
    return { ok: true };
}

async function resetBoardDataAsync() {
    memoryDb = createSeed();
    saveDb(memoryDb);
    return copy(memoryDb);
}

export const BoardApi = {
    getBoardMembersAsync,
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
    removeCardSessionAsync,
    resetBoardDataAsync
};
