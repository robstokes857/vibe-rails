import test from 'node:test';
import assert from 'node:assert/strict';
import { SettingsKeysPanel, validateKeyPassword, normalizeKeyPassword, signingPayloadBase64, MAX_SIGNING_BYTES, MIN_PASSWORD_LENGTH } from '../../../VibeRails/wwwroot/js/modules/settings-keys.js';

function createHarness(apiCall = async () => ({})) {
    const fields = new Map([
        ['#key-create-name', { value: 'Work key' }],
        ['#key-create-password', { value: 'correct horse', password: true }],
        ['#key-create-confirm', { value: 'correct horse', password: true }],
        ['#key-use-password', { value: 'correct horse', password: true }],
        ['#key-sign-type', { value: 'message' }],
        ['#key-sign-message', { value: '' }],
        ['#key-sign-file', { files: [] }],
        ['[data-keys-api-status]', { textContent: '' }],
        ['[data-keys-list]', { innerHTML: '' }],
        ['[data-keys-feedback]', { textContent: '', classList: { toggle() {} } }]
    ]);
    const root = {
        querySelector: selector => fields.get(selector) || null,
        querySelectorAll: selector => [...fields.values()].filter(field => selector !== 'input[type="password"]' || field.password),
        setAttribute() {}
    };
    const calls = [];
    const downloads = [];
    const panel = new SettingsKeysPanel({ apiCall: async (...args) => {
        calls.push(args);
        return apiCall(...args);
    } }, root);
    panel.download = (...args) => downloads.push(args);
    return { panel, fields, calls, downloads, feedback: () => fields.get('[data-keys-feedback]').textContent };
}

const key = { id: 'local-key', name: 'Work key', publicKeyPem: 'PUBLIC PEM', fingerprint: 'sha256-fingerprint', createdUtc: '2026-09-09T12:00:00Z', cloudKeyId: 'cloud-key' };

test('key passwords count Unicode characters, preserve spaces, and reject blanks, short, digit-only and mismatches', () => {
    assert.equal(MIN_PASSWORD_LENGTH, 8);
    for (const password of ['', '        ', '\t\t\t\t\t\t\t\t', 'abc', 'abcdefg', '🔑🔑🔑🔑🔑🔑🔑', '1234', '12345678', ' 1234 5678 ', '１２３４５６７８', 'a'.repeat(129)]) {
        assert.ok(validateKeyPassword(password), `expected rejection for ${JSON.stringify(password)}`);
    }
    assert.match(validateKeyPassword('12345678'), /digits only/);
    for (const password of ['abcdefgh', '1234567a', '🔑🔑🔑🔑🔑🔑🔑🔑', '🔑'.repeat(128), ' abcdefg ', 'correct horse battery staple']) {
        assert.equal(validateKeyPassword(password), null);
    }
    assert.ok(validateKeyPassword(' abcdefgh ', 'abcdefgh'));
});

test('passwords are composed to NFC before counting, comparing and sending, so keyboards agree', async () => {
    const decomposed = 'café au lait'; // "é" typed as e + combining acute
    const composed = 'café au lait';
    assert.equal(normalizeKeyPassword(decomposed), composed);
    assert.equal(validateKeyPassword(decomposed, composed), null);
    // Fourteen code points were typed, but they compose to seven characters, which is too short.
    assert.ok(validateKeyPassword('é'.repeat(7)));
    assert.equal(validateKeyPassword('é'.repeat(8)), null);
    const { panel, fields, calls } = createHarness(async () => ({ key, syncStatus: 'not_configured' }));
    fields.get('#key-create-password').value = decomposed;
    fields.get('#key-create-confirm').value = composed;
    await panel.create();
    assert.equal(calls.length, 1);
    assert.equal(calls[0][2].password, composed);
});

test('base64 signing payload preserves exact UTF-8, whitespace, null bytes and the byte bound', () => {
    const text = '  signed text\r\n🔑\u0000';
    const bytes = new TextEncoder().encode(text);
    assert.deepEqual(Buffer.from(signingPayloadBase64(bytes), 'base64'), Buffer.from(bytes));
    assert.equal(signingPayloadBase64(new Uint8Array()), '');
    assert.equal(Buffer.from(signingPayloadBase64(new Uint8Array(MAX_SIGNING_BYTES)), 'base64').length, MAX_SIGNING_BYTES);
    assert.throws(() => signingPayloadBase64(new Uint8Array(MAX_SIGNING_BYTES + 1)), /64 KiB/);
});

test('failed confirmation never calls the API and clears every password field', async () => {
    const { panel, fields, calls, feedback } = createHarness();
    fields.get('#key-create-confirm').value = 'different';
    await panel.create();
    assert.equal(calls.length, 0);
    assert.match(feedback(), /do not match/);
    assert.ok([...fields.values()].filter(field => field.password).every(field => field.value === ''));
});

test('key creation preserves the password exactly and retains a local key when cloud sync fails', async () => {
    const localKey = { ...key, cloudKeyId: null };
    const { panel, fields, calls, feedback } = createHarness(async () => ({ key: localKey, syncStatus: 'failed', syncError: 'API unavailable' }));
    fields.get('#key-create-password').value = ' abcd 1234 ';
    fields.get('#key-create-confirm').value = ' abcd 1234 ';
    await panel.create();
    assert.equal(calls.length, 1);
    assert.deepEqual(calls[0].slice(0, 3), ['/api/v1/settings/keys', 'POST', { name: 'Work key', password: ' abcd 1234 ' }]);
    assert.deepEqual(panel.keys, [localKey]);
    assert.match(feedback(), /saved locally/);
    assert.match(feedback(), /API unavailable/);
    assert.equal(fields.get('#key-create-password').value, '');
    assert.equal(fields.get('#key-create-confirm').value, '');
    assert.equal(panel.busy, false);
});

test('refresh uses server API-key status and safely escapes key metadata', async () => {
    const { panel, fields } = createHarness(async () => ({ keys: [{ ...key, name: '<img src=x onerror=alert(1)>', fingerprint: '<script>attack</script>' }], apiKeyConfigured: true }));
    await panel.refresh();
    assert.equal(panel.apiKeyConfigured, true);
    const html = fields.get('[data-keys-list]').innerHTML;
    assert.doesNotMatch(html, /<img|<script>/);
    assert.match(html, /&lt;img/);
    assert.match(html, /&lt;script&gt;/);
});

test('private-key backup rejects an incorrect password without downloading and clears it', async () => {
    const { panel, fields, downloads, feedback } = createHarness(async () => { throw new Error('Incorrect password.'); });
    panel.actionKey = key;
    await panel.useKey('backup');
    assert.equal(downloads.length, 0);
    assert.equal(fields.get('#key-use-password').value, '');
    assert.match(feedback(), /Incorrect password/);
    assert.equal(panel.busy, false);
});

test('public-key sync requires the key password and clears it after proving ownership', async () => {
    const { panel, calls, fields, feedback } = createHarness(async () => ({ key, syncStatus: 'synced' }));
    panel.actionKey = key;
    fields.get('#key-use-password').value = ' abcd 1234 ';
    await panel.useKey('sync');
    assert.equal(calls[0][0], '/api/v1/settings/keys/local-key/sync');
    assert.deepEqual(calls[0][2], { password: ' abcd 1234 ' });
    assert.equal(fields.get('#key-use-password').value, '');
    assert.match(feedback(), /Public key saved/);
});

test('private-key backup downloads only the encrypted PEM returned by the protected endpoint', async () => {
    const { panel, calls, downloads } = createHarness(async () => ({ fileName: 'key.pem', encryptedPrivateKeyPem: 'ENCRYPTED PRIVATE PEM', publicKeyPem: 'PUBLIC PEM' }));
    panel.actionKey = key;
    await panel.useKey('backup');
    assert.equal(calls[0][0], '/api/v1/settings/keys/local-key/export');
    assert.deepEqual(calls[0][2], { password: 'correct horse' });
    assert.deepEqual(downloads, [['key.pem', 'ENCRYPTED PRIVATE PEM', 'application/x-pem-file']]);
});

test('signing downloads a request with cloud key ID, fingerprint, public key and exact message bytes, without the password', async () => {
    const { panel, fields, calls, downloads } = createHarness(async (_url, _method, body) => ({
        keyId: 'cloud-key', fingerprint: 'sha256-fingerprint', publicKeyPem: 'PUBLIC PEM', algorithm: 'RSA-PSS-SHA256',
        payloadBase64: body.payloadBase64, signatureBase64: 'signature'
    }));
    panel.actionKey = key;
    const message = '  first line\r\n🔑 '; // Preserve all whitespace and Unicode.
    fields.get('#key-sign-message').value = message;
    await panel.useKey('sign');
    assert.equal(calls[0][0], '/api/v1/settings/keys/local-key/sign');
    assert.equal(Buffer.from(calls[0][2].payloadBase64, 'base64').toString('utf8'), message);
    // Exactly the shape the cloud verify endpoint accepts verbatim (it disallows unknown members).
    assert.deepEqual(JSON.parse(downloads[0][1]), {
        keyId: 'cloud-key', fingerprint: 'sha256-fingerprint', publicKeyPem: 'PUBLIC PEM', algorithm: 'RSA-PSS-SHA256',
        payloadBase64: calls[0][2].payloadBase64, signatureBase64: 'signature'
    });
    assert.doesNotMatch(downloads[0][1], /password|correct horse/);
    assert.equal(fields.get('#key-use-password').value, '');
});

test('file signing reads exact bytes and rejects oversized files before reading or calling the API', async () => {
    const { panel, fields, calls, feedback } = createHarness();
    fields.get('#key-sign-type').value = 'file';
    const bytes = new Uint8Array([0, 1, 128, 255]);
    fields.get('#key-sign-file').files = [{ size: bytes.length, arrayBuffer: async () => bytes.buffer }];
    assert.deepEqual(await panel.readSigningBytes(), bytes);
    let read = false;
    fields.get('#key-sign-file').files = [{ size: MAX_SIGNING_BYTES + 1, arrayBuffer: async () => { read = true; return new ArrayBuffer(0); } }];
    panel.actionKey = key;
    await panel.useKey('sign');
    assert.equal(read, false);
    assert.equal(calls.length, 0);
    assert.match(feedback(), /64 KiB/);
    assert.equal(fields.get('#key-use-password').value, '');
});

test('oversized multibyte text is rejected by byte count before the signing API request', async () => {
    const { panel, fields, calls, feedback } = createHarness();
    panel.actionKey = key;
    fields.get('#key-sign-message').value = '🔑'.repeat(16385);
    await panel.useKey('sign');
    assert.equal(calls.length, 0);
    assert.match(feedback(), /64 KiB/);
});

test('unload aborts pending requests, clears passwords and prevents late private-key downloads', async () => {
    const previousWindow = globalThis.window;
    globalThis.window = { removeEventListener() {} };
    try {
        let complete;
        const response = new Promise(resolve => { complete = resolve; });
        const { panel, fields, calls, downloads } = createHarness(() => response);
        panel.actionKey = key;
        const pending = panel.useKey('backup');
        panel.unload();
        assert.equal(calls[0][3].signal.aborted, true);
        assert.ok([...fields.values()].filter(field => field.password).every(field => field.value === ''));
        complete({ fileName: 'private.pem', encryptedPrivateKeyPem: 'ENCRYPTED PEM' });
        await pending;
        assert.equal(downloads.length, 0);
        assert.equal(panel.actionKey, null);
    } finally {
        globalThis.window = previousWindow;
    }
});

test('duplicate submissions cannot create two keys while the first request is pending', async () => {
    let complete;
    const response = new Promise(resolve => { complete = resolve; });
    const { panel, calls } = createHarness(() => response);
    const pending = panel.create();
    await panel.create();
    assert.equal(calls.length, 1);
    complete({ key, syncStatus: 'synced' });
    await pending;
    assert.equal(panel.busy, false);
});

test('refresh shows skipped-file warnings from the server as text and hides them when there are none', async () => {
    let warnings = ["Skipped 'notes.json' in the signing-keys folder: not a signing-key file.", "Skipped '<b>x</b>.json' in the signing-keys folder: the file is damaged or unreadable."];
    const { panel, fields } = createHarness(async () => ({ keys: [key], apiKeyConfigured: false, warnings }));
    const element = { textContent: '', hidden: true };
    fields.set('[data-keys-warnings]', element);
    await panel.refresh();
    assert.equal(element.hidden, false);
    assert.equal(element.textContent, warnings.join(' ')); // textContent, so the markup in a file name stays inert
    warnings = [];
    await panel.refresh();
    assert.equal(element.hidden, true);
    assert.equal(element.textContent, '');
});
