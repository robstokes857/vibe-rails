import { escapeHtml } from './utils.js';

const labels = { board: 'Board and attachments', configuration: 'Settings, scripts and CLI history', state: 'Sessions, environments and Automations', proxy: 'All retained proxy history' };
const date = value => value ? new Date(value).toLocaleString() : 'No verified backup yet';

export function backupStatusHtml(coverage) {
    if (!coverage?.configured) return '<p class="mb-0">Sign in to enable automatic backups while VibeRails is open.</p>';
    const rows = (coverage.datasets || []).map(({ dataset, state = {} }) => {
        const status = state.coverageIssues?.length ? 'Incomplete' : ({ current: 'Backed up', absent: 'Not present locally', preparing: 'Preparing', uploading: 'Uploading', failed: 'Retry pending', pending: 'Update pending' }[state.status] || 'Pending');
        const details = [state.error, ...(state.coverageIssues || [])].filter(Boolean);
        return `<li class="mb-3"><strong>${escapeHtml(labels[dataset] || dataset)}</strong><span class="d-block">${escapeHtml(status)}</span>
            <small class="d-block text-muted">Last successful backup: ${escapeHtml(date(state.receipt?.receivedUtc))}</small>
            ${state.receipt ? `<small class="d-block text-muted">Source captured: ${escapeHtml(date(state.receipt.sourceStartedUtc))}</small>` : ''}
            ${details.length ? `<details><summary>Coverage details (${details.length})</summary><ul>${details.map(d => `<li>${escapeHtml(d)}</li>`).join('')}</ul></details>` : ''}</li>`;
    }).join('');
    return `<ul class="list-unstyled mb-0">${rows}</ul><details><summary>What is included and excluded</summary><ul>${(coverage.exclusions || []).map(x => `<li>${escapeHtml(x)}</li>`).join('')}</ul></details>`;
}

export class BackupStatusPanel {
    constructor(app, root) { this.app = app; this.root = root; this.disposed = false; this.refresh(); }
    async refresh() {
        try {
            const data = await this.app.apiCall('/api/v1/settings/backups', 'GET');
            if (!this.disposed) this.root.innerHTML = backupStatusHtml(data);
        } catch {
            if (!this.disposed) this.root.textContent = 'Backup status is unavailable. It will refresh automatically.';
        } finally {
            if (!this.disposed) this.timer = setTimeout(() => this.refresh(), 15000);
        }
    }
    dispose() { this.disposed = true; clearTimeout(this.timer); }
}
