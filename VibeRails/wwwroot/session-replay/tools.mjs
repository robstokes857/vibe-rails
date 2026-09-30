// Parse emitted responses only; request history must not duplicate tool calls.
// Match the desktop ToolParser's display limits, including in-flight SSE fragments.
const MAX_RESPONSE = 2_000_000, MAX_TOOLS = 64, MAX_LABEL = 256;
const MAX_ARGUMENT = 8_192, MAX_TOTAL_ARGUMENTS = 65_536;
const TOOL_TYPES = new Set(['function_call', 'custom_tool_call', 'tool_use', 'server_tool_use', 'function']);

export function parseResponseTools(body = '') {
    const calls = new Map(), blocks = new Map();
    let malformed = false, recognized = false, truncated = false, argumentBudget = MAX_TOTAL_ARGUMENTS;
    const object = value => {
        if (value !== null && typeof value === 'object' && !Array.isArray(value)) return true;
        malformed = true;
        return false;
    };
    const clip = (value, limit) => {
        if (value.length <= limit) return value;
        truncated = true;
        // Do not split a UTF-16 surrogate pair at the display boundary.
        if (limit > 0 && /[\uD800-\uDBFF]/.test(value[limit - 1])) limit--;
        return value.slice(0, limit);
    };
    const text = (root, key, allowJson = false) => {
        const value = root[key];
        if (value == null) return '';
        if (typeof value === 'string') return value;
        if (allowJson) return JSON.stringify(value);
        malformed = true;
        return '';
    };
    const label = (root, key) => clip(text(root, key), MAX_LABEL);
    const argumentsText = (value, remaining = MAX_ARGUMENT) => {
        const retained = clip(value, Math.min(remaining, argumentBudget));
        argumentBudget -= retained.length;
        return retained;
    };
    const array = (root, key) => {
        if (root[key] == null) return [];
        if (Array.isArray(root[key])) return root[key];
        malformed = true;
        return [];
    };
    const index = root => {
        if (Number.isInteger(root.index) && root.index >= 0 && root.index <= 2_147_483_647) return root.index;
        malformed = true;
        return null;
    };
    const room = () => {
        if (calls.size + blocks.size < MAX_TOOLS) return true;
        truncated = true;
        return false;
    };
    const add = item => {
        if (!object(item) || !TOOL_TYPES.has(text(item, 'type'))) return;
        let id = label(item, 'call_id') || label(item, 'id');
        const fn = 'function' in item ? item.function : item;
        if (!object(fn)) return;
        const name = label(fn, 'name');
        if (!name) { malformed = true; return; }
        if (!calls.has(id) && !room()) return;
        // Missing IDs must not retain a second copy of the arguments in a map key.
        if (!id) {
            let suffix = calls.size;
            do { id = 'replay-call-' + suffix++; } while (calls.has(id));
        }
        calls.set(id, {id, name, arguments: argumentsText(text(fn, 'arguments', true) || text(fn, 'input', true))});
    };
    const read = (root, depth = 0) => {
        if (!object(root)) return;
        if (depth > 16) { malformed = true; return; }
        if ('item' in root) add(root.item);
        for (const key of ['output', 'content']) for (const item of array(root, key)) add(item);
        if ('response' in root) read(root.response, depth + 1);
        const type = text(root, 'type');
        if (type === 'content_block_start') {
            const block = root.content_block;
            if (object(block) && ['tool_use', 'server_tool_use'].includes(text(block, 'type'))) {
                const key = index(root);
                if (key !== null && (blocks.has(key) || room())) {
                    const input = text(block, 'input', true);
                    blocks.set(key, {id: label(block, 'id'), name: label(block, 'name'),
                        arguments: argumentsText(input === '{}' ? '' : input)});
                }
            }
        }
        if (type === 'content_block_delta') {
            const key = index(root), delta = root.delta;
            if (object(delta) && key !== null && blocks.has(key)) {
                const part = blocks.get(key);
                part.arguments += argumentsText(text(delta, 'partial_json'), MAX_ARGUMENT - part.arguments.length);
            }
        }
        for (const choice of array(root, 'choices')) {
            if (!object(choice)) continue;
            if ('message' in choice && object(choice.message))
                for (const item of array(choice.message, 'tool_calls')) add(item);
            if (!('delta' in choice) || !object(choice.delta)) continue;
            for (const fragment of array(choice.delta, 'tool_calls')) {
                if (!object(fragment)) continue;
                const key = index(fragment);
                if (key === null) continue;
                let part = blocks.get(key);
                if (!part) {
                    if (!room()) continue;
                    part = {id: '', name: '', arguments: ''};
                    blocks.set(key, part);
                }
                const id = label(fragment, 'id');
                if (id) part.id = id;
                if (!('function' in fragment) || !object(fragment.function)) continue;
                part.name += clip(text(fragment.function, 'name'), MAX_LABEL - part.name.length);
                part.arguments += argumentsText(text(fragment.function, 'arguments'), MAX_ARGUMENT - part.arguments.length);
            }
        }
    };
    const parse = value => {
        try { const root = JSON.parse(value); recognized = true; read(root); }
        catch { malformed = true; }
    };
    if (typeof body !== 'string') { malformed = true; body = ''; }
    const hasBody = body.length > 0;
    body = clip(body, MAX_RESPONSE);
    if (/^[\s]*[\[{]/.test(body)) parse(body);
    else {
        let data = [];
        const flush = () => {
            const value = data.join('\n').trim(); data = [];
            if (value && value !== '[DONE]') parse(value);
        };
        for (const line of body.split(/\r?\n/)) {
            if (!line) flush();
            else if (line.startsWith('data:')) data.push(line.slice(5).trimStart());
        }
        flush();
    }
    for (const [key, part] of blocks) {
        if (!part.name) { malformed = true; continue; }
        const id = part.id || 'block-' + key;
        calls.set(id, {...part, id, arguments: part.arguments || argumentsText('{}')});
    }
    const notes = [];
    if (malformed) notes.push('Some response events have incomplete JSON or invalid structure; those events were skipped.');
    if (!recognized && hasBody) notes.push('Unrecognized response format; inspect the raw exchange.');
    if (truncated) notes.push('Tool summary truncated by display limits; inspect the raw exchange for more detail.');
    return {tools: [...calls.values()], note: notes.join(' ') || null};
}