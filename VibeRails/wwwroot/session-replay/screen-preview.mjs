// The terminal parser owns ANSI state. This module only reads its public buffer
// after a completed write, then projects that screen into ordinary, safe HTML.
const palette = ['#202632', '#f392a4', '#8bd2ab', '#e8cd91', '#94bfff', '#c7adff', '#88cfd5', '#d6dbea',
    '#69758a', '#ffadbc', '#a7e9c1', '#ffe2a6', '#b4d2ff', '#dcc7ff', '#a6edf2', '#ffffff'];

function color(cell, foreground) {
    const value = foreground ? cell.getFgColor() : cell.getBgColor();
    if (foreground ? cell.isFgRGB() : cell.isBgRGB()) return `#${value.toString(16).padStart(6, '0')}`;
    if (!(foreground ? cell.isFgPalette() : cell.isBgPalette())) return null;
    if (value < 16) return palette[value];
    if (value >= 232) return `rgb(${Array(3).fill(8 + (value - 232) * 10).join(',')})`;
    const index = value - 16, levels = [0, 95, 135, 175, 215, 255];
    return `rgb(${[Math.floor(index / 36), Math.floor(index / 6) % 6, index % 6].map(i => levels[i]).join(',')})`;
}

function attributes(cell) {
    let fg = color(cell, true), bg = color(cell, false);
    if (cell.isInverse()) [fg, bg] = [bg || '#101319', fg || '#cbd3e2'];
    return { fg, bg, bold: !!cell.isBold(), italic: !!cell.isItalic(), dim: !!cell.isDim(),
        underline: !!cell.isUnderline(), strike: !!cell.isStrikethrough() };
}

function appendRun(runs, text, style) {
    const last = runs.at(-1), key = JSON.stringify(style);
    if (last?.key === key) last.text += text;
    else runs.push({ text, style, key });
}

function sliceRuns(runs, start, end) {
    let offset = 0;
    return runs.flatMap(run => {
        const text = run.text.slice(Math.max(0, start - offset), Math.max(0, end - offset));
        offset += run.text.length;
        return text ? [{ ...run, text }] : [];
    });
}

export function readScreen(terminal) {
    const buffer = terminal.buffer.active, lines = [];
    // baseY is the live screen, regardless of where the xterm viewport is scrolled.
    for (let row = 0; row < terminal.rows; row++) {
        const line = buffer.getLine(buffer.baseY + row), runs = [];
        if (line) for (let col = 0; col < terminal.cols; col++) {
            const cell = line.getCell(col);
            if (!cell || cell.getWidth() === 0) continue; // second cell of a wide glyph
            const text = cell.isInvisible() ? ' '.repeat(cell.getWidth()) : cell.getChars() || ' ';
            appendRun(runs, text, attributes(cell));
        }
        lines.push({ runs, wrapped: !!line?.isWrapped });
    }
    return { cols: terminal.cols, rows: terminal.rows, buffer: buffer.type, lines };
}

export function readingLines(screen) {
    const logical = [];
    for (const line of screen.lines) {
        if (line.wrapped && logical.length) {
            for (const run of line.runs) appendRun(logical.at(-1), run.text, run.style);
        } else logical.push(line.runs.map(run => ({ ...run })));
    }
    const result = logical.map(runs => {
        const original = runs.map(run => run.text).join('');
        let start = 0, end = original.trimEnd().length;
        // Remove only paired outer box edges; keep inner columns and code pipes.
        const boxed = original.slice(0, end).match(/^\s*[│┃║] ?(.*?) ?[│┃║]$/u);
        if (boxed) { start = original.indexOf(boxed[1], original.indexOf(original.trimStart()[0]) + 1); end = start + boxed[1].length; }
        const text = original.slice(start, end);
        let kind = 'output';
        if (!text.trim()) kind = 'blank';
        else if (/^[\s─━═┄┈╌╍┌┐└┘├┤┬┴┼╭╮╰╯╔╗╚╝╠╣╦╩╬+|\-]{4,}$/u.test(text)) kind = 'rule';
        else if (/^\s*[›❯>$]($|\s)/u.test(text)) kind = 'prompt';
        else if (/^\s*[✓✔✗✘]\s/u.test(text)) kind = 'status';
        return { kind, text, runs: sliceRuns(runs, start, end) };
    });
    while (result[0]?.kind === 'blank') result.shift();
    while (result.at(-1)?.kind === 'blank') result.pop();
    return result;
}

function styledText(document, runs) {
    const fragment = document.createDocumentFragment();
    for (const { text, style } of runs) {
        const span = document.createElement('span');
        span.textContent = text; // Recorded output is never interpreted as HTML or links.
        if (style.fg) span.style.color = style.fg;
        if (style.bg) span.style.backgroundColor = style.bg;
        if (style.bold) span.style.fontWeight = '700';
        if (style.italic) span.style.fontStyle = 'italic';
        if (style.dim) span.style.opacity = '.7';
        span.style.textDecoration = [style.underline ? 'underline' : '', style.strike ? 'line-through' : ''].filter(Boolean).join(' ');
        fragment.append(span);
    }
    return fragment;
}

export function renderScreen(container, screen, mode = 'reading') {
    const document = container.ownerDocument, fragment = document.createDocumentFragment();
    container.dataset.mode = mode;
    const lines = mode === 'screen' ? screen.lines.map(line => {
        const text = line.runs.map(run => run.text).join('').trimEnd();
        return { kind: 'output', text, runs: sliceRuns(line.runs, 0, text.length) };
    }) : readingLines(screen);
    if (!lines.some(line => line.text.trim())) {
        const empty = document.createElement('p'); empty.className = 'html-empty';
        empty.textContent = 'The terminal screen is blank at this point.'; fragment.append(empty);
    } else for (const line of lines) {
        const element = document.createElement(line.kind === 'rule' ? 'hr' : 'div');
        element.className = `html-line html-${line.kind}`;
        if (line.kind !== 'rule') element.append(styledText(document, line.runs));
        fragment.append(element);
    }
    container.replaceChildren(fragment);
}

// Sampling is called only at settled replay positions, never mid-write/rebuild.
// Wall-clock time keeps a 100× replay from rebuilding the DOM 100× as often.
export class ScreenPreview {
    constructor({ content, status, time, geometry, refresh, modeButtons, phone, shell, interval = 3000 }) {
        Object.assign(this, { content, status, time, geometry, refresh, modeButtons, phone, shell, interval });
        this.mode = 'reading'; this.lastSample = -Infinity; this.screen = null; this.signature = '';
        modeButtons.forEach(button => button.addEventListener('click', () => {
            this.mode = button.dataset.mode;
            modeButtons.forEach(item => item.setAttribute('aria-pressed', String(item === button)));
            if (this.screen) renderScreen(this.content, this.screen, this.mode);
        }));
        phone?.addEventListener('click', () => {
            const active = phone.getAttribute('aria-pressed') !== 'true';
            phone.setAttribute('aria-pressed', String(active)); shell.classList.toggle('phone-preview', active);
        });
    }
    reset(message) {
        this.screen = null; this.signature = ''; this.lastSample = -Infinity;
        this.content.replaceChildren();
        const empty = this.content.ownerDocument.createElement('p'); empty.className = 'html-empty'; empty.textContent = message;
        this.content.append(empty); this.time.textContent = '—'; this.geometry.textContent = 'Current screen';
        this.status.textContent = message; this.refresh.disabled = true;
    }
    sample(terminal, { now = performance.now(), force = false, label, playing, hasFrames = true }) {
        if (!force && now - this.lastSample < this.interval) return;
        this.lastSample = now;
        const screen = readScreen(terminal), signature = JSON.stringify(screen);
        this.screen = screen;
        // Leave unchanged content alone so selection and reading position survive.
        if (signature !== this.signature) { renderScreen(this.content, screen, this.mode); this.signature = signature; }
        this.time.textContent = label;
        this.geometry.textContent = `${screen.cols} × ${screen.rows} · ${screen.buffer === 'alternate' ? 'Alternate screen' : 'Current screen'}`;
        this.status.textContent = !hasFrames ? 'No terminal output in this recording' : playing ? 'Following playback · refreshes every 3 seconds' : 'Snapshot at the playhead';
        this.refresh.disabled = false;
    }
}
