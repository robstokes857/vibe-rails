// Parse saved CLI arguments for display and editing; never execute shell text.
export function parseCliArguments(value) {
    const args = [];
    let current = '';
    let inQuote = false;
    let quoteChar = '';
    let escaping = false;
    let hasToken = false;

    for (const ch of value || '') {
        if (escaping) {
            current += ch;
            escaping = false;
            hasToken = true;
            continue;
        }

        if (inQuote) {
            if (ch === '\\') {
                escaping = true;
                continue;
            }
            if (ch === quoteChar) {
                inQuote = false;
                continue;
            }
            current += ch;
            hasToken = true;
            continue;
        }

        if (ch === '"' || ch === "'") {
            inQuote = true;
            quoteChar = ch;
            hasToken = true;
            continue;
        }

        if (/\s/.test(ch)) {
            if (hasToken) {
                args.push(current);
                current = '';
                hasToken = false;
            }
            continue;
        }

        current += ch;
        hasToken = true;
    }

    if (escaping) current += '\\';
    if (hasToken) args.push(current);
    return args;
}
