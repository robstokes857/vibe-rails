export const OPENCODE_WHEEL_CLIS = new Set(['opencode', 'glm-5.2', 'glm-5.3']);
export const OPENCODE_RIGHT_PANE_COL_RATIO = 0.62;
export const OPENCODE_INPUT_GUARD_ROWS = 8;

const WHEEL_PREFIX = '\x1b[<6';
const PAGE_UP = '\x1b[5~';
const PAGE_DOWN = '\x1b[6~';
const WHEEL_EVENT = /\x1b\[<(64|65);(\d+);(\d+)M/g;

export function translateOpenCodeMouseWheel(data, { cli, cols = 0, rows = 0 } = {}) {
    const name = (cli || '').toLowerCase();
    if (!OPENCODE_WHEEL_CLIS.has(name)) {
        return data;
    }
    if (typeof data !== 'string' || data.indexOf(WHEEL_PREFIX) === -1) {
        return data;
    }

    const width = Number(cols) || 0;
    const height = Number(rows) || 0;
    const rightPaneLeft = width > 0
        ? Math.floor(width * OPENCODE_RIGHT_PANE_COL_RATIO)
        : Number.POSITIVE_INFINITY;
    const inputTop = height > 0
        ? height - OPENCODE_INPUT_GUARD_ROWS
        : 0;

    return data.replace(WHEEL_EVENT, (match, button, col, row) => {
        const c = Number(col);
        const r = Number(row);
        if (width > 0 && c > rightPaneLeft && r <= inputTop) {
            return match;
        }
        return button === '64' ? PAGE_UP : PAGE_DOWN;
    });
}
