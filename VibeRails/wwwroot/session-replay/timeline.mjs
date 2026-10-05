export function durationLabel(ms) {
    const total = Math.max(0, Math.floor(ms / 1000));
    const seconds = String(total % 60).padStart(2, '0');
    const minutes = String(Math.floor(total / 60) % 60).padStart(2, '0');
    return total >= 3600 ? `${Math.floor(total / 3600)}:${minutes}:${seconds}` : `${minutes}:${seconds}`;
}

// Resize metadata describes buffered byte ranges. Split a raw frame if a resize
// falls inside it; applying every resize before that frame changes terminal state.
export function prepareFrames(raw, geometry, source) {
    if (source === 'enriched') return raw;
    const boundaries = [];
    let offset = 0, cols = 120, rows = 30;
    for (const chunk of geometry) {
        if (boundaries.length === 0 || chunk.cols !== cols || chunk.rows !== rows) {
            boundaries.push({ offset, cols: chunk.cols, rows: chunk.rows, at: chunk.at });
            cols = chunk.cols; rows = chunk.rows;
        }
        offset += chunk.bytes;
    }
    if (!boundaries.length) boundaries.push({ offset: 0, cols: 120, rows: 30 });
    let byteOffset = 0, boundary = 0;
    cols = boundaries[0].cols; rows = boundaries[0].rows;
    const frames = [];
    for (const frame of raw) {
        let cursor = 0;
        while (cursor < frame.data.length) {
            while (boundary < boundaries.length && boundaries[boundary].offset <= byteOffset) {
                cols = boundaries[boundary].cols; rows = boundaries[boundary].rows; boundary++;
            }
            const size = Math.min(frame.data.length - cursor, (boundaries[boundary]?.offset ?? Infinity) - byteOffset);
            frames.push({ at: frame.at, data: frame.data.subarray(cursor, cursor + size), cols, rows });
            cursor += size; byteOffset += size;
        }
    }
    // A zero-byte resize after the last output still changes the visible grid.
    while (boundary < boundaries.length && boundaries[boundary].offset <= byteOffset) {
        const change = boundaries[boundary++];
        frames.push({ at: Math.max(raw.at(-1)?.at ?? 0, change.at), data: new Uint8Array(), cols: change.cols, rows: change.rows });
    }
    return frames;
}

export function upperBound(items, at) {
    let low = 0, high = items.length;
    while (low < high) { const mid = (low + high) >>> 1; if (items[mid].at <= at) low = mid + 1; else high = mid; }
    return low;
}

export function advancePosition(position, elapsed, speed, end, nextActivity, skipIdle) {
    let next = Math.min(end, position + elapsed * speed);
    if (skipIdle && nextActivity - next > 2000) next = Math.min(end, nextActivity - 400);
    return next;
}

// Max speed has no clock. Each tick writes the next bounded batch of output, so pause and
// screen samples still interleave; once the output runs out the target is the recording end.
export function maxSpeedTarget(frames, frameIndex, end, budget = 256 * 1024) {
    let index = frameIndex, bytes = 0;
    while (index < frames.length && bytes < budget) bytes += frames[index++].data.length;
    return index < frames.length ? Math.min(end, frames[index - 1].at) : end;
}

export function eventsFor(manifest, exchanges) {
    const events = [
        ...manifest.prompts.map(prompt => ({ kind: 'prompt', at: prompt.at, title: prompt.text, prompt })),
        ...manifest.changes.map(change => ({ kind: 'change', at: change.at, title: change.path, change })),
        ...exchanges.flatMap(exchange => [
            { kind: 'proxy', at: exchange.at, title: `${exchange.provider} · ${exchange.method} ${exchange.path}`, exchange },
            ...exchange.tools.map(tool => ({ kind: 'tool', at: exchange.at, title: tool.name, tool, exchange }))
        ])
    ];
    return events.sort((a, b) => a.at - b.at);
}
