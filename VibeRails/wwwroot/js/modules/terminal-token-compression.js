// Terminal token-compression UI.
//
// This module owns both compression surfaces in the terminal: the persistent savings meter and
// the per-tab compression toggle. TerminalManager and app.js only provide lifecycle integration.
//
// The meter creates one host element and re-parents it (see relocate()) into the terminal
// controls bar, so it keeps its accumulated per-provider history across the view re-renders that
// rebuild that bar. Only `proxy_activity` app events are wired to report() (see app.js), and only
// the authenticated Claude/Codex proxy routes publish those events. Hover, focus, or click to see
// recent query-free, payload-free request metadata grouped by provider.
let nextCompressionMeterId = 0;

export class TerminalTokenCompressionMeter {
    constructor({
        mount = null,
        maxEntriesPerSource = 6,
        maxSources = 12,
        title = 'Token compression',
        enabled = false,
        tokensSaved = null
    } = {}) {
        this.sources = new Map();
        this.totalCount = 0;
        this._maxEntriesPerSource = maxEntriesPerSource;
        this._maxSources = maxSources;
        this._title = title;
        this._enabled = Boolean(enabled);
        this._savings = this._normalizeSavings(tokensSaved);
        this._popoverId = `vb-activity-blinker-popover-${++nextCompressionMeterId}`;
        this._pulseTimer = null;
        this._build(mount);
        this._syncSavingsDisplay();
        this.setEnabled(this._enabled);
    }

    // Attach the meter to the app's dedicated proxy event stream and seed its counters from the
    // persisted savings endpoint. Keeping this wiring here prevents app.js from accumulating
    // compression-specific payload mapping and fallback behavior.
    connect(app) {
        if (!app || this._connectedApp === app) return this;
        this._connectedApp = app;

        app.appEventClient?.on('proxy_activity', payload => {
            this.report({
                source: payload?.source,
                label: payload?.label,
                target: payload?.target,
                status: payload?.status
            });

            // Older servers omit the tallies; retain the current values in that case.
            if (typeof payload?.tokensSavedTotal === 'number') {
                this.setTokensSaved({
                    session: payload.tokensSavedSession,
                    month: payload.tokensSavedMonth,
                    allTime: payload.tokensSavedTotal
                });
            }
        });

        this.initialSavingsReady = (async () => {
            try {
                const savings = await app.apiCall(
                    '/api/v1/token-savings',
                    'GET',
                    null,
                    { showLoading: false });
                if (typeof savings?.tokensSaved === 'number') {
                    this.setTokensSaved({
                        session: savings.tokensSavedSession,
                        month: savings.tokensSavedMonth,
                        allTime: savings.tokensSaved
                    });
                }
            } catch {
                // Best-effort seed: the next live proxy event can still supply the counters.
            }
        })();

        return this;
    }

    setEnabled(enabled) {
        this._enabled = Boolean(enabled);
        this._host.classList.toggle('is-enabled', this._enabled);
        this._syncAccessibleStatus();
        if (this._host.classList.contains('is-open')) this._renderPopover();
    }

    // Accepts a { session, month, allTime } breakdown (or a bare number, treated as all time).
    // Unmeasured windows stay null and deliberately render as an em dash rather than implying
    // zero savings.
    setTokensSaved(tokensSaved) {
        this._savings = this._normalizeSavings(tokensSaved);
        this._syncSavingsDisplay();
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
    }

    // Re-parent the persistent host into `target` (e.g. the terminal controls bar) without dropping
    // accumulated state — appendChild moves the same node. Called from TerminalManager.initialize()
    // whenever that bar re-renders, and once from app.js on startup in case the bar mounted first.
    // A no-op when the element is already there or the target is missing (the host just stays put /
    // detached until the next render), so callers can pass a possibly-null slot safely.
    relocate(target) {
        if (!this._host || !target || typeof target.appendChild !== 'function') return;
        if (this._host.parentElement === target) return;
        target.appendChild(this._host);
    }

    _build(mount) {
        const host = document.createElement('div');
        host.className = 'vb-activity-blinker';

        const trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.className = 'vb-activity-blinker-trigger';
        trigger.setAttribute('aria-expanded', 'false');
        trigger.setAttribute('aria-controls', this._popoverId);

        const zipper = document.createElement('span');
        zipper.className = 'vb-activity-blinker-zip';
        zipper.setAttribute('aria-hidden', 'true');

        const zipperOutline = document.createElement('i');
        zipperOutline.className = 'fa-regular fa-file-zipper vb-activity-blinker-zip-outline';
        zipper.appendChild(zipperOutline);

        const zipperSolid = document.createElement('i');
        zipperSolid.className = 'fa-solid fa-file-zipper vb-activity-blinker-zip-solid';
        zipper.appendChild(zipperSolid);
        trigger.appendChild(zipper);

        const label = document.createElement('span');
        label.className = 'vb-activity-blinker-label';
        label.textContent = 'Token saver';
        trigger.appendChild(label);

        const metric = document.createElement('span');
        metric.className = 'vb-activity-blinker-metric';

        const savingsValue = document.createElement('strong');
        savingsValue.className = 'vb-activity-blinker-value';
        metric.appendChild(savingsValue);
        metric.appendChild(this._span('tokens saved', 'vb-activity-blinker-metric-label'));
        trigger.appendChild(metric);

        const dot = document.createElement('span');
        dot.className = 'vb-activity-blinker-dot';
        dot.setAttribute('aria-hidden', 'true');
        trigger.appendChild(dot);
        host.appendChild(trigger);

        const popover = document.createElement('div');
        popover.className = 'vb-activity-blinker-popover';
        popover.setAttribute('id', this._popoverId);
        popover.setAttribute('role', 'region');
        popover.setAttribute('aria-label', `${this._title} details`);
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
        this._savingsMetric = metric;
        this._savingsValue = savingsValue;
        this._popover = popover;
        // With no mount the host stays detached; app.js/TerminalManager place it via relocate().
        // report() and setEnabled() work fine on a detached node, so state still accrues meanwhile.
        if (mount) mount.appendChild(host);
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

        this._trigger.setAttribute('aria-label', `${status}; ${this._formatSavingsStatus()}`);
        this._trigger.setAttribute('title', `${status}; ${this._formatSavingsStatus()}`);
    }

    _renderPopover() {
        const popover = this._popover;
        popover.replaceChildren();

        const header = document.createElement('div');
        header.className = 'vb-activity-popover-header';

        const heading = document.createElement('span');
        heading.className = 'vb-activity-popover-heading';
        const headingIcon = document.createElement('span');
        headingIcon.className = 'vb-activity-popover-heading-icon';
        headingIcon.setAttribute('aria-hidden', 'true');
        const compressIcon = document.createElement('i');
        compressIcon.className = 'fa-solid fa-compress';
        headingIcon.appendChild(compressIcon);
        heading.appendChild(headingIcon);
        heading.appendChild(this._span(this._title));
        header.appendChild(heading);
        header.appendChild(this._span(
            `${this.totalCount} request${this.totalCount === 1 ? '' : 's'}`,
            'vb-activity-popover-total'
        ));
        popover.appendChild(header);

        const savings = document.createElement('div');
        savings.className = 'vb-activity-popover-savings';

        const savingsIcon = document.createElement('span');
        savingsIcon.className = 'vb-activity-popover-savings-icon';
        savingsIcon.setAttribute('aria-hidden', 'true');
        const savingsFile = document.createElement('i');
        savingsFile.className = 'fa-solid fa-arrow-trend-down';
        savingsIcon.appendChild(savingsFile);
        savings.appendChild(savingsIcon);

        const savingsCopy = document.createElement('span');
        savingsCopy.className = 'vb-activity-popover-savings-copy';
        savingsCopy.appendChild(this._span('Tokens saved', 'vb-activity-popover-savings-label'));
        savingsCopy.appendChild(this._span(
            this._hasSavingsMeasurement() ? 'Across proxied traffic' : 'Savings measurement not connected yet',
            'vb-activity-popover-savings-hint'
        ));
        savings.appendChild(savingsCopy);

        const breakdown = document.createElement('span');
        breakdown.className = 'vb-activity-popover-savings-breakdown';
        for (const [windowLabel, value] of [
            ['This session', this._savings.session],
            ['This month', this._savings.month],
            ['All time', this._savings.allTime]
        ]) {
            const row = document.createElement('span');
            row.className = 'vb-activity-popover-savings-row';
            row.appendChild(this._span(windowLabel, 'vb-activity-popover-savings-row-label'));
            row.appendChild(this._span(
                value == null ? '—' : this._formatCompactTokenCount(value),
                'vb-activity-popover-savings-row-value'
            ));
            breakdown.appendChild(row);
        }
        savings.appendChild(breakdown);
        popover.appendChild(savings);

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

    _normalizeTokenCount(value) {
        if (value == null || value === '') return null;
        const numericValue = Number(value);
        if (!Number.isFinite(numericValue) || numericValue < 0) return null;
        return Math.floor(numericValue);
    }

    _normalizeSavings(value) {
        if (value != null && typeof value === 'object') {
            return {
                session: this._normalizeTokenCount(value.session),
                month: this._normalizeTokenCount(value.month),
                allTime: this._normalizeTokenCount(value.allTime)
            };
        }
        return { session: null, month: null, allTime: this._normalizeTokenCount(value) };
    }

    _hasSavingsMeasurement() {
        const { session, month, allTime } = this._savings;
        return session != null || month != null || allTime != null;
    }

    _formatSavingsStatus() {
        if (!this._hasSavingsMeasurement()) return 'token savings not yet measured';
        const part = (value, windowLabel) =>
            `${value == null ? 'unmeasured' : this._formatExactTokenCount(value)} ${windowLabel}`;
        const { session, month, allTime } = this._savings;
        return 'tokens saved: '
            + `${part(session, 'this session')}, ${part(month, 'this month')}, ${part(allTime, 'all time')}`;
    }

    _formatCompactTokenCount(value) {
        return new Intl.NumberFormat('en-US', {
            notation: value >= 1000 ? 'compact' : 'standard',
            maximumFractionDigits: 1
        }).format(value);
    }

    _formatExactTokenCount(value) {
        return new Intl.NumberFormat('en-US').format(value);
    }

    _syncSavingsDisplay() {
        if (!this._savingsMetric || !this._savingsValue) return;
        // The trigger has room for one number: this session's savings, the tally that visibly
        // ticks with the pulse (all time as a fallback for a bare-number legacy update). The
        // popover carries the full session/month/all-time breakdown.
        const shown = this._savings.session ?? this._savings.allTime;
        const isPlaceholder = shown == null;
        this._savingsMetric.classList.toggle('is-placeholder', isPlaceholder);
        this._savingsValue.textContent = isPlaceholder
            ? '—'
            : this._formatCompactTokenCount(shown);
    }

    _span(text, className = '') {
        const element = document.createElement('span');
        element.textContent = text;
        if (className) element.className = className;
        return element;
    }
}

// Per-tab gate over the global Token Saver stage selection. Each Web UI tab owns a child
// VibeRails process (and therefore its own Claude/Codex proxy), so this controller always talks to
// the addressed tab endpoint. The child process is authoritative; session metadata is only used
// for an immediate paint while the quiet refresh is in flight.
export class TerminalTabTokenCompression {
    constructor(manager) {
        this.manager = manager;
    }

    createState(source = {}) {
        return {
            tokenSaverEnabled: source?.tokenSaverEnabled !== false,
            tokenSaverPending: false,
            tokenSaverRevision: 0
        };
    }

    readMetadata(metadata = {}) {
        return { tokenSaverEnabled: metadata?.tokenSaverEnabled !== false };
    }

    writeMetadata(state = {}) {
        return { tokenSaverEnabled: state?.tokenSaverEnabled !== false };
    }

    createButton(tabId) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'vb-terminal-tab-token-compression';
        button.addEventListener('click', (event) => {
            event.stopPropagation();
            void this.toggle(tabId);
        });
        return button;
    }

    async refresh(tabId) {
        const startingTab = this.manager.tabs.get(tabId);
        if (!startingTab) return;
        const revision = startingTab.state.tokenSaverRevision;

        try {
            const response = await this.manager.app.apiCall(
                this._endpoint(tabId), 'GET', null, { showLoading: false });
            const tab = this.manager.tabs.get(tabId);
            if (!tab
                || tab.state.tokenSaverRevision !== revision
                || typeof response?.enabled !== 'boolean') return;

            tab.state.tokenSaverEnabled = response.enabled;
            this._persist(tab);
            this.render(tab);
        } catch {
            // Initial synchronization is best-effort. A deliberate click surfaces failures.
        }
    }

    async toggle(tabId) {
        const tab = this.manager.tabs.get(tabId);
        if (!tab || tab.state.tokenSaverPending === true) return;

        const desired = tab.state.tokenSaverEnabled !== true;
        tab.state.tokenSaverRevision += 1;
        tab.state.tokenSaverPending = true;
        this.render(tab);

        try {
            const response = await this.manager.app.apiCall(
                this._endpoint(tabId),
                'PUT',
                { enabled: desired },
                { showLoading: false });
            if (typeof response?.enabled !== 'boolean') {
                throw new Error('The terminal returned an invalid compression state.');
            }

            tab.state.tokenSaverEnabled = response.enabled;
            this._persist(tab);
        } catch (error) {
            const reason = error instanceof Error ? error.message : String(error);
            this.manager.app.showToast(
                'Token compression',
                `Could not update this tab: ${reason}`,
                'error');
        } finally {
            tab.state.tokenSaverPending = false;
            this.render(tab);
        }
    }

    render(tab) {
        const item = tab?.state?.ui?.item;
        const button = tab?.state?.ui?.tokenCompression;
        if (!item) return;

        const enabled = tab.state.tokenSaverEnabled === true;
        const pending = tab.state.tokenSaverPending === true;
        item.classList.toggle('is-token-compression-on', enabled);
        item.dataset.tokenCompression = enabled ? 'on' : 'off';

        if (!button) return;

        button.hidden = false;
        button.disabled = pending;
        button.classList.toggle('is-on', enabled);
        button.dataset.state = pending ? 'pending' : enabled ? 'on' : 'off';
        button.innerHTML = pending
            ? '<i class="fa-solid fa-spinner fa-spin" aria-hidden="true"></i>'
            : '<i class="fa-solid fa-compress" aria-hidden="true"></i><span class="vb-terminal-tab-token-compression-dot" aria-hidden="true"></span>';

        const label = pending
            ? 'Updating token compression for this tab…'
            : enabled
                ? 'Token compression is on for this tab — click to turn it off'
                : 'Token compression is off for this tab — click to turn it on';
        button.title = label;
        button.setAttribute('aria-label', label);
        button.setAttribute('aria-pressed', String(enabled));
        button.setAttribute('aria-busy', String(pending));
        button.setAttribute('aria-hidden', 'false');
    }

    _persist(tab) {
        this.manager.saveTabMeta(
            tab.state.id,
            this.manager._tabMetaPayload(tab.state));
    }

    _endpoint(tabId) {
        return `/api/v1/terminal/tabs/${encodeURIComponent(tabId)}/token-saver`;
    }
}
