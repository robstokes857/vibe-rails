// Uploaded envelopes expose fewer capture sources than the local recording API.
// Normalize them to the same read-only contract without inventing missing events.
function time(value, fallback = 0) {
    if (typeof value !== 'string' || !value) return fallback;
    const stamp = value.replace(/(\.\d{3})\d+/, '$1');
    const parsed = Date.parse(/[zZ]|[+-]\d\d:?\d\d$/.test(stamp) ? stamp : stamp + 'Z');
    return Number.isFinite(parsed) ? parsed : fallback;
}
function bytes(value) {
    if (!value) return new Uint8Array();
    if (typeof value !== 'string' || value.length > 16 * 1024 * 1024) throw new RangeError('Invalid or oversized terminal frame');
    return Uint8Array.from(atob(value), c => c.charCodeAt(0));
}
function dimension(value, maximum, fallback) {
    return Number.isInteger(value) && value > 0 && value <= maximum ? value : fallback;
}
export function createEnvelopeSource(envelope) {
    const source = envelope?.session;
    if (!source?.id) throw new Error('The envelope is missing its session identity');
    const id = source.id;
    const raw = [...(envelope.sessionLogs || [])].sort((a,b) => a.id-b.id);
    const enriched = [...(envelope.terminalSessionLogs || [])].sort((a,b) => a.sequence-b.sequence || a.id-b.id);
    const started = time(source.startedUtc ?? source.startedUTC);
    const ended = time(source.endedUtc ?? source.endedUTC, null);
    const frameSource = raw.length ? 'raw' : 'enriched';
    const terminal = enriched.map((row, i) => ({ id: i+1, sequence: row.sequence,
        at: time(row.timestampUtc, started), data: bytes(row.rawBytes),
        cols: dimension(row.cols, 500, 120), rows: dimension(row.rows, 300, 30) }));
    const frames = frameSource === 'raw' ? raw.map((row,i) => ({ id:i+1, at:time(row.timestampUtc,started), data:bytes(row.rawBytes), cols:0, rows:0 })) : terminal;
    const inputs = [...(envelope.userInputs || [])].sort((a,b) => a.sequence-b.sequence || a.id-b.id);
    let end = Math.max(started, ended || 0);
    for (const row of inputs) end = Math.max(end, time(row.timestampUtc, started));
    for (const frame of frames) end = Math.max(end, frame.at);
    const changes = [], patches = new Map();
    for (let index=0; index<inputs.length; index++) {
        const input = inputs[index];
        for (const change of input.fileChanges || []) {
            const legacy = change.previousInputId != null;
            const at = legacy ? time(input.timestampUtc, started) : index+1<inputs.length ? time(inputs[index+1].timestampUtc, end) : end;
            const key = changes.length+1;
            changes.push({ id:key, inputId:input.id, path:change.filePath || '', type:change.changeType || '',
                added:change.linesAdded, deleted:change.linesDeleted, hasDiff:!!change.diffContent, at,
                timing:legacy ? 'Previous prompt window' : index+1<inputs.length ? 'At next prompt boundary' : 'At end of recording snapshot' });
            patches.set(String(key), { id:key, path:change.filePath, diff:change.diffContent || null });
        }
    }
    const session = { id, cli:source.cli || '', environment:source.environmentName || '', directory:source.workingDirectory || '',
        project:source.projectDisplayName || '', title:source.sessionDisplayName || '', started, ended,
        exitCode:source.exitCode, hasTerminal:frames.length>0, changes:changes.length };
    const notes = ['Uploaded recording. Board context and proxy request/tool details are not available in this viewer.',
        'Saved patches describe prompt windows, not complete file snapshots.'];
    const manifest = { session, cards:[], prompts:inputs.map(row => ({id:row.id,sequence:row.sequence,at:time(row.timestampUtc,started),text:row.inputText || ''})),
        changes, geometry:terminal.map(({data,...row}) => ({...row,bytes:data.length})), frameSource, frameMaxId:frames.length,
        proxyMaxId:0, frameCount:frames.length, frameBytes:frames.reduce((n,row)=>n+row.data.length,0), end, notes };
    if (raw.length && terminal.reduce((n,row)=>n+row.data.length,0) !== manifest.frameBytes)
        notes.push('Raw and buffered terminal byte totals differ. Resize alignment may be incomplete.');
    return async (path, { signal } = {}) => {
        signal?.throwIfAborted();
        const url = new URL(path, 'https://replay.invalid');
        if (url.pathname === '/api/status') return { sources:[{name:'Uploaded recording',available:true}] };
        if (url.pathname === '/api/sessions') return { items:[session], nextOffset:-1 };
        const parts = url.pathname.split('/').filter(Boolean).map(decodeURIComponent);
        if (parts[0] !== 'api' || parts[1] !== 'sessions' || parts[2] !== id) throw new Error('Session not found');
        if (parts.length === 3) return structuredClone(manifest);
        if (parts[3] === 'frames') {
            const after = Number(url.searchParams.get('after')) || 0;
            const page = frames.slice(after, after+2000);
            return { items:structuredClone(page), next:after+page.length, done:after+page.length>=frames.length };
        }
        if (parts[3] === 'exchanges') return {items:[],next:0,done:true};
        if (parts[3] === 'changes' && patches.has(parts[4])) return patches.get(parts[4]);
        throw new Error('Capture not found');
    };
}
