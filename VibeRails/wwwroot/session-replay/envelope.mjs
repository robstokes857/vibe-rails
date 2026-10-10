// Uploaded envelopes expose fewer capture sources than the local recording API.
// Normalize them to the same read-only contract without inventing missing events.
import { parseResponseTools } from './tools.mjs';
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
    const proxies = (Array.isArray(envelope.proxyExchanges) ? envelope.proxyExchanges : [])
        .filter(row => row.sessionId === id);
    // Compact hosted playback retains only these sizes, without the request/response bodies.
    const savingsRows = Array.isArray(envelope.proxyExchanges) ? proxies
        : (Array.isArray(envelope.proxySavings) ? envelope.proxySavings : []).filter(row => row?.sessionId === id);
    let savedCharacters = 0, measuredRequests = 0;
    for (const row of savingsRows) {
        const before = row.charsBefore ?? (typeof row.requestBefore === 'string' ? row.requestBefore.length : null);
        const after = row.charsAfter ?? (typeof row.requestAfter === 'string' ? row.requestAfter.length : null);
        if (!Number.isSafeInteger(before) || before < 0 || !Number.isSafeInteger(after) || after < 0) continue;
        savedCharacters += before - after;
        measuredRequests++;
    }
    const tokensSaved = measuredRequests && Number.isSafeInteger(savedCharacters)
        ? Math.floor(Math.max(0, savedCharacters) / 4) : null;
    const summaries = new Map();
    const bodyLimit = 2000000;
    const bodyText = value => typeof value === 'string' ? value : '';
    const summarize = (row, index) => {
        if (summaries.has(index)) return summaries.get(index);
        let request = {};
        try { request = JSON.parse(bodyText(row.requestBefore).slice(0,bodyLimit)) || {}; } catch { /* Optional metadata. */ }
        const parsed = parseResponseTools(bodyText(row.responseBody));
        const result = { cursor:index+1, id:row.id, at:time(row.createdUtc,started), provider:row.provider || '', method:row.method || '',
            path:row.path || '', status:row.statusCode, elapsedMs:row.elapsedMs,
            truncated:!!row.responseTruncated, model:request.model || '',
            effort:request.reasoning?.effort || request.output_config?.effort || request.thinking?.type || '',
            tools:parsed.tools, parseNote:parsed.note };
        summaries.set(index,result); return result;
    };
    let end = Math.max(started, ended || 0);
    for (const row of proxies) end = Math.max(end, time(row.createdUtc, started));
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
    const notes = ['Uploaded recording. Board context is not included.',
        proxies.length ? 'Proxy captures are included; tool timestamps mark response completion.' : 'No proxy request/tool captures are included in this upload.',
        'Saved patches describe prompt windows, not complete file snapshots.'];
    const manifest = { session, cards:[], prompts:inputs.map(row => ({id:row.id,sequence:row.sequence,at:time(row.timestampUtc,started),text:row.inputText || ''})),
        changes, geometry:terminal.map(({data,...row}) => ({...row,bytes:data.length})), frameSource, frameMaxId:frames.length,
        proxyMaxId:proxies.length, frameCount:frames.length, frameBytes:frames.reduce((n,row)=>n+row.data.length,0), end, notes, tokensSaved };
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
        if (parts[3] === 'exchanges') {
            if (parts.length === 5) {
                const row = proxies.find(row=>row.id===parts[4]);
                if (!row) throw new Error('Capture not found');
                const bodies=[row.requestBefore,row.requestAfter,row.responseBody].map(bodyText);
                return {id:row.id,before:bodies[0].slice(0,bodyLimit),after:bodies[1].slice(0,bodyLimit),response:bodies[2].slice(0,bodyLimit),
                    displayTruncated:bodies.some(body=>body.length>bodyLimit),captureTruncated:!!row.responseTruncated};
            }
            const after=Math.max(0,Number(url.searchParams.get('after')) || 0);
            const page=proxies.slice(after,after+30).map((row,index)=>summarize(row,after+index));
            return {items:structuredClone(page),next:after+page.length,done:after+page.length>=proxies.length};
        }
        if (parts[3] === 'changes' && patches.has(parts[4])) return patches.get(parts[4]);
        throw new Error('Capture not found');
    };
}
