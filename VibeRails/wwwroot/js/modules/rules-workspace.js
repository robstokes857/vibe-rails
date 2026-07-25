/**
 * Rules workspace shell: local navigation + the sections/terminal split.
 *
 * The Rules page used to stack every panel in one long scroll, which pushed the agent
 * terminal below ~2000px of report. Now the page is two panes: a local nav swaps ONE
 * section into the top pane, and the terminal permanently owns the bottom pane.
 *
 * Two invariants this module exists to protect:
 *   1. Switching sections only toggles `hidden`. Nothing is re-rendered and nothing is
 *      unmounted — the running terminal, the Monaco source pane, and the in-flight scans
 *      all survive a tab click. (Monaco is created with automaticLayout, so it re-measures
 *      itself when its section is shown again.)
 *   2. The nav never fetches anything. It mirrors state the controllers already publish
 *      on the panels (`data-tone`, `[data-vca-console-state]`, list contents) via a
 *      MutationObserver, so no controller needs to know the nav exists.
 */

const SECTION_STORAGE_KEY = 'viberails_rules_section';

function writeStored(key, value) {
    try {
        window.localStorage.setItem(key, String(value));
    } catch {
        // Private-mode / storage-disabled hosts still get a working (just non-persistent) split.
    }
}

const TONES = new Set(['neutral', 'success', 'warning', 'danger']);

function normalizeTone(value) {
    const tone = String(value || '');
    return TONES.has(tone) ? tone : 'neutral';
}

export class RulesWorkspace {
    constructor(root, { onLayoutChange = null } = {}) {
        this.root = root || null;
        // Called after a section switch so the host can re-fit the terminal and any
        // newly visible code-quality surface immediately.
        this.onLayoutChange = typeof onLayoutChange === 'function' ? onLayoutChange : null;
        this.nav = this.root?.querySelector('[data-rules-nav]') || null;
        this.tabs = Array.from(this.root?.querySelectorAll('[data-rules-tab]') || []);
        this.sections = new Map();
        this.observers = [];
        this.cleanups = [];
        this.activeId = null;

        for (const section of this.root?.querySelectorAll('[data-rules-section]') || []) {
            this.sections.set(section.dataset.rulesSection, section);
        }
    }

    mount() {
        if (!this.root || !this.tabs.length) return this;
        this.bindTabs();

        const stored = (() => {
            try { return window.localStorage.getItem(SECTION_STORAGE_KEY); } catch { return null; }
        })();
        const initial = this.sections.has(stored) ? stored : this.tabs[0]?.dataset.rulesTab;
        this.showSection(initial, { focus: false, persist: false });

        this.watchStatus();
        return this;
    }

    destroy() {
        this.observers.forEach(observer => observer.disconnect());
        this.observers = [];
        this.cleanups.forEach(fn => { try { fn(); } catch { /* teardown is best-effort */ } });
        this.cleanups = [];
        this.root = null;
        this.nav = null;
        this.tabs = [];
        this.sections.clear();
    }

    // ── Local navigation ────────────────────────────────────────────────────

    bindTabs() {
        this.tabs.forEach((tab, index) => {
            const onClick = () => this.showSection(tab.dataset.rulesTab);
            const onKeydown = (event) => {
                const delta = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[event.key];
                if (delta) {
                    event.preventDefault();
                    const next = this.tabs[(index + delta + this.tabs.length) % this.tabs.length];
                    this.showSection(next?.dataset.rulesTab, { focus: true });
                    return;
                }
                if (event.key === 'Home' || event.key === 'End') {
                    event.preventDefault();
                    const target = event.key === 'Home' ? this.tabs[0] : this.tabs[this.tabs.length - 1];
                    this.showSection(target?.dataset.rulesTab, { focus: true });
                }
            };
            tab.addEventListener('click', onClick);
            tab.addEventListener('keydown', onKeydown);
            this.cleanups.push(() => {
                tab.removeEventListener('click', onClick);
                tab.removeEventListener('keydown', onKeydown);
            });
        });
    }

    showSection(id, { focus = false, persist = true } = {}) {
        if (!id || !this.sections.has(id) || id === this.activeId) {
            if (focus) this.tabs.find(tab => tab.dataset.rulesTab === id)?.focus();
            return;
        }

        this.activeId = id;
        for (const [sectionId, section] of this.sections) {
            section.hidden = sectionId !== id;
        }
        for (const tab of this.tabs) {
            const selected = tab.dataset.rulesTab === id;
            tab.setAttribute('aria-selected', String(selected));
            tab.classList.toggle('is-active', selected);
            tab.tabIndex = selected ? 0 : -1;
            if (selected && focus) tab.focus();
        }
        if (persist) writeStored(SECTION_STORAGE_KEY, id);

        // Panels that were laid out while hidden (the code-quality workspace, the rule
        // editor) measure 0 wide. A resize tick after the swap lets them settle.
        window.requestAnimationFrame(() => this.notifyLayoutChanged());
    }

    notifyLayoutChanged() {
        window.dispatchEvent(new Event('resize'));
        try {
            this.onLayoutChange?.();
        } catch {
            // A host that cannot re-fit is not a reason to break the split.
        }
    }

    // ── Live status in the nav ──────────────────────────────────────────────

    // Deliberately narrow observers. A subtree watch on a whole section would fire on
    // every Monaco frame in the code-quality pane; these watch only the four things the
    // controllers actually write: the panel tone, the state word, and the two lists.
    watchStatus() {
        const validate = this.sections.get('validate');
        const quality = this.sections.get('quality');
        const files = this.sections.get('files');
        const automate = this.sections.get('automate');

        const onValidate = () => this.syncValidate(validate);
        this.observeTone(validate, onValidate);
        this.observeContent(validate?.querySelector('[data-vca-console-state]'), onValidate);
        this.observeContent(validate?.querySelector('[data-vca-finding-list]'), onValidate, false);

        const onQuality = () => this.syncQuality(quality);
        this.observeTone(quality, onQuality);
        this.observeContent(quality?.querySelector('[data-vca-console-state]'), onQuality);
        this.observeContent(quality?.querySelector('[data-code-analyzer-report]'), onQuality, false);

        this.observeContent(files?.querySelector('[data-agent-file-tree]'),
            () => this.syncFiles(files), false);
        this.observeContent(automate?.querySelector('[data-jobs-automation-host]'),
            () => this.syncAutomate(automate), false);

        this.syncValidate(validate);
        this.syncQuality(quality);
        this.syncFiles(files);
        this.syncAutomate(automate);
    }

    /** Coalesces bursts of mutations into one handler call per frame. */
    coalesce(handler) {
        let queued = false;
        return () => {
            if (queued) return;
            queued = true;
            window.requestAnimationFrame(() => {
                queued = false;
                handler();
            });
        };
    }

    observeTone(target, handler) {
        if (!target) return;
        const observer = new MutationObserver(this.coalesce(handler));
        observer.observe(target, { attributes: true, attributeFilter: ['data-tone', 'aria-busy'] });
        this.observers.push(observer);
    }

    observeContent(target, handler, watchText = true) {
        if (!target) return;
        const observer = new MutationObserver(this.coalesce(handler));
        observer.observe(target, {
            childList: true,
            characterData: watchText,
            subtree: watchText
        });
        this.observers.push(observer);
    }

    setTabStatus(id, { tone = 'neutral', meta = '', busy = false } = {}) {
        const tab = this.tabs.find(candidate => candidate.dataset.rulesTab === id);
        if (!tab) return;
        tab.dataset.tone = normalizeTone(tone);
        tab.classList.toggle('is-busy', Boolean(busy));
        const metaElement = tab.querySelector('[data-rules-tab-meta]');
        if (metaElement && meta) metaElement.textContent = meta;
    }

    // Badge copy stays short — it rides inside a tab, not a status tile.
    syncValidate(section) {
        if (!section) return;
        const busy = section.getAttribute('aria-busy') === 'true';
        const tone = section.dataset.tone || 'neutral';
        const findings = section.querySelectorAll('[data-vca-finding-list] .vca-finding').length;
        const state = section.querySelector('[data-vca-console-state]')?.textContent?.trim() || 'Ready';
        const meta = busy
            ? 'Running'
            : findings > 0
                ? `${findings} ${findings === 1 ? 'finding' : 'findings'}`
                : state;
        this.setTabStatus('validate', { tone, meta, busy });
    }

    syncQuality(section) {
        if (!section) return;
        const busy = section.getAttribute('aria-busy') === 'true';
        const tone = section.dataset.tone || 'neutral';
        const score = section.querySelector('.code-analyzer-score-value')?.textContent?.trim();
        const state = section.querySelector('[data-vca-console-state]')?.textContent?.trim() || 'Ready';
        const meta = busy ? 'Scanning' : (score ? `Score ${score}` : state);
        this.setTabStatus('quality', { tone, meta, busy });
    }

    syncFiles(section) {
        if (!section) return;
        const files = section.querySelectorAll('[data-agent-file-tree] .agent-file-tree-item').length;
        const withRules = section.querySelectorAll(
            '[data-agent-file-tree] .agent-files-configured .agent-file-tree-item').length;
        const meta = files === 0 ? 'None' : `${files} ${files === 1 ? 'file' : 'files'}`;
        this.setTabStatus('files', { tone: withRules > 0 ? 'success' : 'neutral', meta });
    }

    syncAutomate(section) {
        if (!section) return;
        const host = section.querySelector('[data-jobs-automation-host]');
        const jobs = host?.querySelectorAll('.jobs-automation-list article').length || 0;
        const enabled = Array.from(host?.querySelectorAll('.jobs-automation-list .job-state') || [])
            .filter(state => state.dataset.tone === 'success').length;
        const meta = jobs === 0 ? 'None' : `${jobs} ${jobs === 1 ? 'job' : 'jobs'}`;
        this.setTabStatus('automate', { tone: enabled > 0 ? 'success' : 'neutral', meta });
    }
}
