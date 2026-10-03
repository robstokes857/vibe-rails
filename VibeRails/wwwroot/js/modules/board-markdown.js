// A small escape-first Markdown subset. Input is already escaped and code/links/images
// are parked by board-text.js. These transforms only emit fixed, inert markup.
export function styleBoardMarkdown(escaped) {
    const inline = text => text
        .replace(/\*\*([^*\n]+)\*\*|__([^_\n]+)__/g, (_, a, b) => `<strong>${a ?? b}</strong>`)
        .replace(/~~([^~\n]+)~~/g, '<del>$1</del>')
        .replace(/(?<!\*)\*([^*\n]+)\*(?!\*)|(?<![\w_])_([^_\n]+)_(?![\w_])/g, (_, a, b) => `<em>${a ?? b}</em>`);
    let list = null;
    const lines = [];
    const closeList = () => { if (list) lines.push(`</${list}>`); list = null; };
    for (const line of escaped.split(/\r?\n/)) {
        const item = line.match(/^\s{0,3}(?:([-+*])|\d+[.)])\s+(.+)$/);
        if (item) {
            const kind = item[1] ? 'ul' : 'ol';
            if (kind !== list) { closeList(); lines.push(`<${kind}>`); list = kind; }
            const task = item[2].match(/^\[([ xX])\]\s+(.*)$/);
            lines.push(`<li>${task ? `<span role="img" aria-label="${task[1] === ' ' ? 'Unchecked' : 'Checked'}">${task[1] === ' ' ? '☐' : '☑'}</span> ${inline(task[2])}` : inline(item[2])}</li>`);
            continue;
        }
        closeList();
        const heading = line.match(/^(#{1,6})\s+(.+)$/);
        if (heading) lines.push(`<h${heading[1].length}>${inline(heading[2])}</h${heading[1].length}>`);
        else if (/^&gt;\s?/.test(line)) lines.push(`<blockquote>${inline(line.replace(/^&gt;\s?/, ''))}</blockquote>`);
        else if (/^\s{0,3}(?:---+|\*\*\*+)\s*$/.test(line)) lines.push('<hr>');
        else lines.push(inline(line));
    }
    closeList();
    return lines.join('\n');
}
