// Persistent, app-wide status light for the local LLM proxy/token saver.
//
// The control stays visible in an idle/disabled state so its absence cannot be confused with a
// lack of traffic. Only `proxy_activity` app events are wired to report() (see app.js), and only
// the authenticated Claude/Codex proxy routes publish those events. Hover, focus, or click to see
// recent query-free, payload-free request metadata grouped by provider.
export class ActivityBlinker {
    constructor({ mount = null, maxEntriesPerSource = 6, maxSources = 12, title = 'Proxy & token saver', enabled = false } = {}) {
        this.sources = new Map();
        this.totalCount = 0;
        this._maxEntriesPerSource = maxEntriesPerSource;
        this._maxSources = maxSources;
        this._title = title;
        this._enabled = Boolean(enabled);
        this._pulseTimer = null;
        this._build(mount);
        this.setEnabled(this._enabled);
    }

    setEnabled(enabled) {
        this._enabled = Boolean(enabled);
        this._host.classList.toggle('is-enabled', this._enabled);
        this._syncAccessibleStatus();
        if (this._host.classList.contains('is-open')) this._renderPopover();
    }

    // Announce one completed, authenticated proxy relay. The caller must supply only sanitized
    // display metadata: provider/source, method, query-free upstream path, and status code.
    report(entry = {}) {
        const source = (entry.source != null ? String(entry.source) : 'Unknown proxy') || 'Unknown proxy';
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

        // A proxy event is stronger evidence than a possibly stale settings snapshot.
        this._enabled = true;
        this._host.classList.add('is-enabled');
        this._pulse();
        this._syncAccessibleStatus();
        if (this._host.classList.contains('is-open')) this._renderPopover();
    }

    _evictOldestSource() {
        let oldestName = null;
        let oldestAt = Infinity;
        for (const [name, rec] of this.sources) {
            const time = rec.last ? rec.last.getTime() : 0;
            if (time < oldestAt) {
                oldestAt = time;
                oldestName = name;
            }
        }
        if (oldestName != null) this.sources.delete(oldestName);
    }

    _pulse() {
        if (this._pulseTimer != null) clearTimeout(this._pulseTimer);

        // Re-adding the class restarts the CSS keyframes for back-to-back requests.
        this._host.classList.remove('is-active');
        void this._host.offsetWidth;
        this._host.classList.add('is-active');
        this._pulseTimer = setTimeout(() => {
            this._host.classList.remove('is-active');
            this._pulseTimer = null;
            this._syncAccessibleStatus();
        }, 900);
        this._pulseTimer?.unref?.();
    }

    _build(mount) {
        const host = document.createElement('div');
        host.className = 'vb-activity-blinker';
        if (!mount) host.classList.add('is-floating');

        const trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.className = 'vb-activity-blinker-trigger';
        trigger.setAttribute('aria-haspopup', 'true');
        trigger.setAttribute('aria-expanded', 'false');

        const icon = document.createElement('i');
        icon.className = 'fa-solid fa-traffic-light vb-activity-blinker-icon';
        icon.setAttribute('aria-hidden', 'true');
        trigger.appendChild(icon);

        const dot = document.createElement('span');
        dot.className = 'vb-activity-blinker-dot';
        dot.setAttribute('aria-hidden', 'true');
        trigger.appendChild(dot);

        const label = document.createElement('span');
        label.className = 'vb-activity-blinker-label';
        label.textContent = 'Proxy';
        trigger.appendChild(label);
        host.appendChild(trigger);

        const popover = document.createElement('div');
        popover.className = 'vb-activity-blinker-popover';
        popover.setAttribute('role', 'tooltip');
        host.appendChild(popover);

        host.addEventListener('mouseenter', () => this._openPopover());
        host.addEventListener('mouseleave', () => {
            if (!host.contains?.(document.activeElement)) this._closePopover();
        });
        trigger.addEventListener('focus', () => this._openPopover());
        trigger.addEventListener('blur', () => {
            setTimeout(() => {
                if (!host.contains?.(document.activeElement)) this._closePopover();
            }, 0);
        });
        trigger.addEventListener('click', () => {
            if (host.classList.contains('is-open')) this._closePopover();
            else this._openPopover();
        });
        document.addEventListener?.('click', (event) => {
            if (!host.contains?.(event.target)) this._closePopover();
        });

        this._host = host;
        this._trigger = trigger;
        this._dot = dot;
        this._popover = popover;
        (mount || document.body).appendChild(host);
    }

    _openPopover() {
        this._renderPopover();
        this._host.classList.add('is-open');
        this._trigger.setAttribute('aria-expanded', 'true');
    }

    _closePopover() {
        this._host.classList.remove('is-open');
        this._trigger.setAttribute('aria-expanded', 'false');
    }

    _syncAccessibleStatus() {
        let status;
        if (!this._enabled) {
            status = `${this._title}: disabled`;
        } else if (this._host.classList.contains('is-active')) {
            status = `${this._title}: proxy traffic detected`;
        } else if (this.totalCount === 0) {
            status = `${this._title}: enabled, waiting for traffic`;
        } else {
            status = `${this._title}: ${this.totalCount} proxied request${this.totalCount === 1 ? '' : 's'}`;
        }

        this._trigger.setAttribute('aria-label', status);
        this._trigger.setAttribute('title', status);
    }

    _renderPopover() {
        const popover = this._popover;
        popover.replaceChildren();

        const header = document.createElement('div');
        header.className = 'vb-activity-popover-header';
        header.appendChild(this._span(this._title));
        header.appendChild(this._span(`${this.totalCount} total`, 'vb-activity-popover-total'));
        popover.appendChild(header);

        if (this.sources.size === 0) {
            popover.appendChild(this._span(
                this._enabled ? 'Enabled — waiting for proxied traffic.' : 'Disabled in application settings.',
                'vb-activity-popover-empty'
            ));
            return;
        }

        const groups = [...this.sources.entries()].sort(
            (left, right) => (right[1].last?.getTime() || 0) - (left[1].last?.getTime() || 0)
        );

        for (const [name, rec] of groups) {
            const group = document.createElement('div');
            group.className = 'vb-activity-popover-group';

            const groupTitle = document.createElement('div');
            groupTitle.className = 'vb-activity-popover-group-title';
            groupTitle.appendChild(this._span(name, 'vb-activity-popover-source'));
            const when = rec.last ? rec.last.toLocaleTimeString() : '';
            groupTitle.appendChild(this._span(`${rec.count} · ${when}`, 'vb-activity-popover-time'));
            group.appendChild(groupTitle);

            for (const entry of rec.entries.slice(0, 3)) {
                const parts = [];
                if (entry.label) parts.push(entry.label);
                if (entry.target) parts.push(`→ ${entry.target}`);
                if (entry.status) parts.push(`· ${entry.status}`);
                group.appendChild(this._span(parts.join(' '), 'vb-activity-popover-entry'));
            }
            popover.appendChild(group);
        }
    }

    _span(text, className = '') {
        const element = document.createElement('span');
        element.textContent = text;
        if (className) element.className = className;
        return element;
    }
}
