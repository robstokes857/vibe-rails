import test from 'node:test';
import assert from 'node:assert/strict';
import { backupStatusHtml, BackupStatusPanel } from '../../../VibeRails/wwwroot/js/modules/backup-status.js';

test('backup status distinguishes a verified older version from incomplete pending content and escapes details', () => {
    const html = backupStatusHtml({ configured: true, datasets: [{ dataset: 'board', state: {
        status: 'failed', error: '<img src=x onerror=alert(1)>', coverageIssues: ['missing original'],
        receipt: { receivedUtc: '2026-10-01T00:00:00Z', sourceStartedUtc: '2026-09-30T00:00:00Z' }
    } }], exclusions: ['external scripts <not included>'] });
    assert.match(html, /Incomplete/); assert.match(html, /Last successful backup/);
    assert.doesNotMatch(html, /<img/); assert.match(html, /&lt;img/);
    assert.match(backupStatusHtml({ configured: false }), /Sign in/);
});

test('closing Settings suppresses a late backup status response and stops refresh', async () => {
    let finish; const root = { innerHTML: 'untouched' };
    const panel = new BackupStatusPanel({ apiCall: () => new Promise(resolve => { finish = resolve; }) }, root);
    panel.dispose(); finish({ configured: false });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(root.innerHTML, 'untouched'); assert.equal(panel.timer, undefined);
});
