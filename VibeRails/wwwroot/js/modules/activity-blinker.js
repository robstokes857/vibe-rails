// A single, shared, app-wide "router light".
//
// Reusable and drop-in: any consumer announces work with one call —
//   blinker.report({ source: 'Claude proxy', label: 'POST', target: 'https://api.anthropic.com/v1/messages', status: '200' });
// — and the one shared LED blinks. Hovering it shows a small list of recent pings aggregated by
// `source` (counts + last time + the last few targets). Nothing here is proxy-specific; it's just
// the first consumer. Feed it directly from client code, or wire it to server 'activity'
// app-events (see app.js). Mounts itself (fixed, bottom-right) unless given a `mount` element.
export class ActivityBlinker {
    constructor({ mount = null, maxEntriesPerSource = 6, maxSources = 12, title = 'Activity' } = {}) {
        this.sources = new Map(); // name -> { count, last: Date, entries: [{ label, target, status, at }] }
        this.totalCount = 0;
        this._maxEntriesPerSource = maxEntriesPerSource;
        this._maxSources = maxSources;
        this._title = title;
        this._build(mount);
    }

    // A consumer announces it just did something. All fields optional except a stable `source`.
    report(entry = {}) {
        const source = (entry.source != null ? String(entry.source) : 'Unknown') || 'Unknown';
        const rec = this.sources.get(source) || { count: 0, last: null, entries: [] };
        rec.count += 1;
        rec.last = new Date();
        rec.entries.unshift({
            label: entry.label != null ? String(entry.label) : '',
            target: entry.target != null ? String(entry.target) : '',
            status: entry.status != null ? String(entry.status) : '',
            at: rec.last
        });
        if (rec.entries.length > this._maxEntriesPerSource) rec.entries.length = this._maxEntriesPerSource;
        this.sources.set(source, rec);
        if (this.sources.size > this._maxSources) this._evictOldestSource();
        this.totalCount += 1;

        this._host.style.display = '';
        this._host.setAttribute('aria-label', `${this._title}: ${this.totalCount} events`);
        this._pulse();
        if (this._popover.style.display !== 'none') this._renderPopover();
    }

    _evictOldestSource() {
        let oldestName = null;
        let oldestAt = Infinity;
        for (const [name, rec] of this.sources) {
            const t = rec.last ? rec.last.getTime() : 0;
            if (t < oldestAt) { oldestAt = t; oldestName = name; }
        }
        if (oldestName != null) this.sources.delete(oldestName);
    }

    _pulse() {
        const dot = this._dot;
        if (!dot || typeof dot.animate !== 'function') return;
        try {
            dot.animate(
                [
                    { opacity: 1, boxShadow: '0 0 10px 2px rgba(34, 197, 94, 0.9)', transform: 'scale(1.3)' },
                    { opacity: 0.45, boxShadow: '0 0 2px 0 rgba(34, 197, 94, 0.4)', transform: 'scale(1)' }
                ],
                { duration: 500, easing: 'ease-out' }
            );
        } catch {
            // WAAPI unavailable — the dim dot + hover popover still convey activity.
        }
    }

    _build(mount) {
        const host = document.createElement('div');
        host.className = 'vb-activity-blinker';
        Object.assign(host.style, {
            position: mount ? 'relative' : 'fixed',
            right: mount ? 'auto' : '14px',
            bottom: mount ? 'auto' : '12px',
            zIndex: '2147483000',
            display: 'none', // shown after the first report
            alignItems: 'center',
            width: '12px',
            height: '12px'
        });

        const dot = document.createElement('div');
        Object.assign(dot.style, {
            width: '10px',
            height: '10px',
            borderRadius: '50%',
            background: '#22c55e',
            opacity: '0.45',
            boxShadow: '0 0 2px 0 rgba(34, 197, 94, 0.4)',
            cursor: 'default'
        });
        host.appendChild(dot);

        const popover = document.createElement('div');
        Object.assign(popover.style, {
            position: 'absolute',
            display: 'none',
            right: '0',
            bottom: '20px',
            minWidth: '250px',
            maxWidth: '360px',
            maxHeight: '320px',
            overflowY: 'auto',
            padding: '10px 12px',
            borderRadius: '8px',
            background: 'rgba(17, 24, 39, 0.97)',
            color: '#e5e7eb',
            border: '1px solid rgba(148, 163, 184, 0.25)',
            boxShadow: '0 8px 28px rgba(0, 0, 0, 0.45)',
            font: '12px/1.5 system-ui, sans-serif',
            whiteSpace: 'normal'
        });
        host.appendChild(popover);

        host.addEventListener('mouseenter', () => { this._renderPopover(); popover.style.display = 'block'; });
        host.addEventListener('mouseleave', () => { popover.style.display = 'none'; });

        this._host = host;
        this._dot = dot;
        this._popover = popover;

        (mount || document.body).appendChild(host);
    }

    _renderPopover() {
        const p = this._popover;
        p.replaceChildren();

        const header = document.createElement('div');
        Object.assign(header.style, { display: 'flex', justifyContent: 'space-between', gap: '12px', fontWeight: '600', marginBottom: '4px' });
        header.appendChild(this._span(this._title));
        header.appendChild(this._span(`${this.totalCount} total`, { opacity: '0.6', fontWeight: '400' }));
        p.appendChild(header);

        if (this.sources.size === 0) {
            p.appendChild(this._span('No activity yet.', { opacity: '0.6' }));
            return;
        }

        // Most-recently-active source first.
        const groups = [...this.sources.entries()].sort(
            (a, b) => (b[1].last ? b[1].last.getTime() : 0) - (a[1].last ? a[1].last.getTime() : 0)
        );

        for (const [name, rec] of groups) {
            const group = document.createElement('div');
            group.style.margin = '8px 0 2px';

            const title = document.createElement('div');
            Object.assign(title.style, { display: 'flex', justifyContent: 'space-between', gap: '12px' });
            title.appendChild(this._span(name, { color: '#22c55e', fontWeight: '600' }));
            const when = rec.last ? rec.last.toLocaleTimeString() : '';
            title.appendChild(this._span(`${rec.count} · ${when}`, { opacity: '0.6' }));
            group.appendChild(title);

            for (const e of rec.entries.slice(0, 3)) {
                const parts = [];
                if (e.label) parts.push(e.label);
                if (e.target) parts.push('→ ' + e.target);
                if (e.status) parts.push('· ' + e.status);
                const line = this._span(parts.join(' '), {
                    display: 'block',
                    opacity: '0.8',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap'
                });
                group.appendChild(line);
            }
            p.appendChild(group);
        }
    }

    // textContent-only span builder — never interpolates data into HTML.
    _span(text, style = {}) {
        const el = document.createElement('span');
        el.textContent = text;
        Object.assign(el.style, style);
        return el;
    }
}
