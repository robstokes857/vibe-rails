import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const { accountSummary, accountChipLabel, ACCOUNT_SITE_URL } =
    await import(pathToFileURL(path.resolve('VibeRails/wwwroot/js/modules/account-nav.js')).href);
const index = readFileSync('VibeRails/wwwroot/index.html', 'utf8');

test('no saved key is signed out, whatever account details linger', () => {
    assert.equal(accountSummary({ apiKey: '', remoteAccountEmail: 'rob@example.com' }).state, 'signed-out');
    assert.equal(accountSummary(null).state, 'signed-out');
});

test('a key with an approved email is a personal account showing only the masked ending', () => {
    assert.deepEqual(accountSummary({ apiKey: '••••••••abcd', remoteAccountEmail: ' rob@example.com ' }), {
        state: 'personal', email: 'rob@example.com', organization: null, initial: 'R', keyEnding: 'abcd'
    });
    // A short key is masked completely, so there is no ending to show.
    assert.equal(accountSummary({ apiKey: '••••', remoteAccountEmail: 'rob@example.com' }).keyEnding, '');
});

test('a manual key without an email never shows an organization', () => {
    const summary = accountSummary({ apiKey: '••••wxyz', remoteAccountEmail: null, remoteAccountOrganization: 'Contoso' });
    assert.equal(summary.state, 'key-only');
    assert.equal(summary.organization, null);
    assert.equal(accountChipLabel(summary), 'Account menu. API key configured');
});

test('an organization on an approved account makes it managed; a blank one does not', () => {
    const managed = accountSummary({ apiKey: '••••abcd', remoteAccountEmail: 'rob@example.com', remoteAccountOrganization: 'Contoso' });
    assert.equal(managed.state, 'managed');
    assert.equal(accountChipLabel(managed), 'Account menu. Signed in as rob@example.com, managed by Contoso');
    const blank = accountSummary({ apiKey: '••••abcd', remoteAccountEmail: 'rob@example.com', remoteAccountOrganization: '  ' });
    assert.equal(blank.state, 'personal');
    assert.equal(accountChipLabel(blank), 'Account menu. Signed in as rob@example.com');
});

test('the initial is the first character, not half of a surrogate pair', () => {
    assert.equal(accountSummary({ apiKey: '••••abcd', remoteAccountEmail: '😀@example.com' }).initial, '😀');
});

test('the top bar starts hidden and wires its popover accessibly', () => {
    assert.match(index, /<div class="app-account" data-account-menu-host hidden>/);
    assert.match(index, /data-action="account-menu" aria-haspopup="dialog"\s+aria-expanded="false" aria-controls="app-account-menu"/);
    assert.match(index, /id="app-account-menu" role="dialog" aria-label="viberails\.ai account" data-account-menu hidden/);
    assert.match(index, new RegExp(`href="${ACCOUNT_SITE_URL}" target="_blank" rel="noopener noreferrer"`));
    // Signed-in controls never reuse the signed-out prompt's hook (VIBE-243 locators count it).
    const host = index.slice(index.indexOf('data-account-menu-host'), index.indexOf('class="nav-exit-btn-sm"'));
    assert.doesNotMatch(host, /data-account-nav|data-action="remote-account"/);
});
