import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/environment-steps.js');
const controllerPath = path.resolve('VibeRails/wwwroot/js/modules/environment-controller.js');
const stylePath = path.resolve('VibeRails/wwwroot/style.css');

const {
    STEP_PHASE,
    MAX_STEPS,
    DEFAULT_TIMEOUT_SECONDS,
    MIN_TIMEOUT_SECONDS,
    MAX_TIMEOUT_SECONDS,
    normalizeSteps,
    serializeSteps,
    summarizeSteps,
    renderStepsSummaryButton,
    streamStepTest,
    newStepId
} = await import(pathToFileURL(modulePath).href);

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// ---------------------------------------------------------------------------------------
//  normalizeSteps
// ---------------------------------------------------------------------------------------

test('normalizeSteps orders by phase then position, which is run order', () => {
    const steps = normalizeSteps([
        { phase: 1, position: 0, command: 'git push' },
        { phase: 0, position: 1, command: 'npm ci' },
        { phase: 0, position: 0, command: 'git pull' }
    ]);

    assert.deepEqual(steps.map(step => step.command), ['git pull', 'npm ci', 'git push']);
});

test('normalizeSteps fills in the documented defaults and drops the server position', () => {
    const [step] = normalizeSteps([{ phase: 0, command: 'npm ci', position: 3 }]);

    assert.equal(step.name, '');
    assert.equal(step.enabled, true);
    assert.equal(step.startMinimized, false);
    assert.equal(step.timeoutSeconds, DEFAULT_TIMEOUT_SECONDS);
    // Position is a server concern, restated from array order on save.
    assert.equal('position' in step, false);
    assert.match(step.clientId, /^step-\d+$/);
    // A step arriving without a server id (older shape) still gets a durable identity.
    assert.match(step.id, GUID_PATTERN);
});

test('normalizeSteps keeps the server GUID — {{step:<id>}} references depend on it', () => {
    const id = newStepId();
    const [step] = normalizeSteps([{ phase: 0, command: 'npm ci', id }]);

    assert.equal(step.id, id);
});

test('normalizeSteps clamps a stored timeout instead of trusting it', () => {
    const [tooSmall] = normalizeSteps([{ phase: 0, command: 'a', timeoutSeconds: 0 }]);
    const [tooBig] = normalizeSteps([{ phase: 0, command: 'a', timeoutSeconds: 99999 }]);
    const [garbage] = normalizeSteps([{ phase: 0, command: 'a', timeoutSeconds: 'soon' }]);

    assert.equal(tooSmall.timeoutSeconds, MIN_TIMEOUT_SECONDS);
    assert.equal(tooBig.timeoutSeconds, MAX_TIMEOUT_SECONDS);
    assert.equal(garbage.timeoutSeconds, DEFAULT_TIMEOUT_SECONDS);
});

test('normalizeSteps treats a missing or unknown phase as before-launch', () => {
    const steps = normalizeSteps([{ command: 'a' }, { phase: 9, command: 'b' }]);
    assert.deepEqual(steps.map(step => step.phase), [STEP_PHASE.PRE_LAUNCH, STEP_PHASE.PRE_LAUNCH]);
});

test('normalizeSteps survives a missing or malformed list', () => {
    assert.deepEqual(normalizeSteps(undefined), []);
    assert.deepEqual(normalizeSteps(null), []);
    assert.deepEqual(normalizeSteps('nope'), []);
    assert.deepEqual(normalizeSteps([null, 4]), []);
});

// ---------------------------------------------------------------------------------------
//  serializeSteps
// ---------------------------------------------------------------------------------------

test('serializeSteps emits pre-launch steps first so array order becomes Position', () => {
    const wire = serializeSteps([
        { phase: 1, command: 'git push' },
        { phase: 0, command: 'git pull' },
        { phase: 1, command: 'git status' },
        { phase: 0, command: 'npm ci' }
    ]);

    assert.deepEqual(wire.map(step => step.command), ['git pull', 'npm ci', 'git push', 'git status']);
    assert.deepEqual(wire.map(step => step.phase), [0, 0, 1, 1]);
});

test('serializeSteps never sends position or clientId — the server stamps position', () => {
    const [step] = serializeSteps([{ phase: 0, command: 'npm ci', position: 7, clientId: 'step-1', id: newStepId() }]);

    assert.deepEqual(Object.keys(step).sort(), [
        'command', 'enabled', 'id', 'name', 'phase', 'startMinimized', 'timeoutSeconds'
    ]);
});

test('serializeSteps round-trips a string id and regenerates a non-string one', () => {
    const id = newStepId();
    const wire = serializeSteps([
        { phase: 0, command: 'git pull', id },
        { phase: 0, command: 'npm ci', id: 12 }
    ]);

    assert.equal(wire[0].id, id);
    assert.match(wire[1].id, GUID_PATTERN);
});

test('manual-phase steps survive serialization and sort last', () => {
    // Phase 2 = "only when referenced" from the Initial Message. A filter that only knew
    // phases 0 and 1 would silently drop them from every save.
    const wire = serializeSteps([
        { phase: STEP_PHASE.MANUAL, command: 'git log -1' },
        { phase: 0, command: 'git pull' },
        { phase: 1, command: 'git push' }
    ]);

    assert.deepEqual(wire.map(step => step.phase), [0, 1, STEP_PHASE.MANUAL]);
});

test('serializeSteps drops empty rows but keeps the command verbatim', () => {
    const wire = serializeSteps([
        { phase: 0, command: '   ' },
        { phase: 0, command: '  git pull  ', name: '  Pull  ' }
    ]);

    assert.equal(wire.length, 1);
    // Only the label is trimmed: leading whitespace can matter in a shell script.
    assert.equal(wire[0].command, '  git pull  ');
    assert.equal(wire[0].name, 'Pull');
});

test('serializeSteps keeps a disabled step rather than silently deleting it', () => {
    const wire = serializeSteps([{ phase: 0, command: 'npm ci', enabled: false }]);
    assert.equal(wire.length, 1);
    assert.equal(wire[0].enabled, false);
});

// ---------------------------------------------------------------------------------------
//  Summary
// ---------------------------------------------------------------------------------------

test('summarizeSteps reads as a count per phase', () => {
    assert.equal(summarizeSteps([]), 'None yet');
    assert.equal(summarizeSteps([{ phase: 0 }, { phase: 0 }, { phase: 1 }]), '2 before · 1 after');
    assert.equal(summarizeSteps([{ phase: 1 }]), '1 after');
    assert.equal(summarizeSteps([{ phase: 0 }]), '1 before');
    assert.equal(
        summarizeSteps([{ phase: 0 }, { phase: STEP_PHASE.MANUAL }]),
        '1 before · 1 referenced');
});

test('the summary button carries an escaped count and the hook the form binds to', () => {
    const html = renderStepsSummaryButton([{ phase: 0 }]);

    assert.match(html, /data-env-steps-open/);
    assert.match(html, /data-env-steps-summary>1 before</);
    assert.match(html, /aria-haspopup="dialog"/);
});

// ---------------------------------------------------------------------------------------
//  Test stream
// ---------------------------------------------------------------------------------------

function streamFrom(text) {
    const encoder = new TextEncoder();
    const chunks = [encoder.encode(text)];
    let index = 0;
    return {
        getReader: () => ({
            read: async () => (index < chunks.length
                ? { value: chunks[index++], done: false }
                : { value: undefined, done: true }),
            releaseLock() { }
        })
    };
}

test('streamStepTest posts the command and parses each SSE frame', async () => {
    const calls = [];
    const events = [];

    await streamStepTest({
        url: '/api/v1/environments/steps/test',
        command: 'npm ci',
        workingDirectory: '/srv/app',
        timeoutSeconds: 120,
        headers: { viberails_tab: 'tab-1' },
        fetchImpl: async (url, init) => {
            calls.push({ url, init });
            return {
                ok: true,
                body: streamFrom(
                    'data: {"type":"line","line":"installing","isError":false}\n\n' +
                    'data: {"type":"done","exitCode":0,"durationMs":40}\n\n')
            };
        },
        onEvent: event => events.push(event)
    });

    assert.equal(calls.length, 1);
    assert.equal(calls[0].init.method, 'POST');
    assert.equal(calls[0].init.headers.viberails_tab, 'tab-1');
    assert.deepEqual(JSON.parse(calls[0].init.body), {
        command: 'npm ci',
        workingDirectory: '/srv/app',
        timeoutSeconds: 120
    });
    assert.deepEqual(events.map(event => event.type), ['line', 'done']);
    assert.equal(events[1].exitCode, 0);
});

test('streamStepTest clamps the timeout it sends', async () => {
    let sent = null;
    await streamStepTest({
        url: '/x',
        command: 'a',
        timeoutSeconds: 99999,
        fetchImpl: async (_url, init) => {
            sent = JSON.parse(init.body);
            return { ok: true, body: streamFrom('data: {"type":"done","exitCode":0}\n\n') };
        }
    });

    assert.equal(sent.timeoutSeconds, MAX_TIMEOUT_SECONDS);
});

test('streamStepTest surfaces a non-OK response as an error carrying the status', async () => {
    await assert.rejects(
        () => streamStepTest({
            url: '/x',
            command: 'a',
            fetchImpl: async () => ({ ok: false, status: 400, statusText: 'Bad Request' })
        }),
        error => {
            assert.equal(error.status, 400);
            assert.match(error.message, /Step test failed: 400/);
            return true;
        });
});

// ---------------------------------------------------------------------------------------
//  House rules
// ---------------------------------------------------------------------------------------

test('the steps editor confirms in-app — window.confirm is dead in the webview', () => {
    const source = readFileSync(modulePath, 'utf8');

    assert.doesNotMatch(source, /window\.confirm\s*\(/);
    assert.match(source, /confirmDialog\(/);
    // A capture-phase Escape handler that ignores an open confirm overlay would tear this modal
    // down underneath the dialog it just opened.
    assert.match(source, /if \(isConfirmDialogOpen\(\)\) return;/);
});

test('the editor opens as its own layer, never a second app.showModal', () => {
    // app.showModal rebuilds #modal-container's innerHTML wholesale, which would destroy the
    // environment form this editor is opened from.
    const source = readFileSync(modulePath, 'utf8');

    assert.doesNotMatch(source, /showModal\(/);
    assert.match(source, /llm-picker-modal-layer/);
    assert.match(source, /element\.inert = true;/);
    assert.match(source, /setAttribute\('aria-hidden', 'true'\)/);
});

test('the environment form omits steps entirely until the editor has been used', () => {
    const source = readFileSync(controllerPath, 'utf8');

    // `null` on the wire means "leave them untouched"; sending [] from a form whose steps modal
    // was never opened would wipe a configured setup chain.
    assert.match(source, /let editedSteps = null;/);
    assert.match(source, /if \(editedSteps\) payload\.steps = serializeSteps\(editedSteps\);/);
    assert.match(source, /\.\.\.\(editedSteps \? \{ steps: serializeSteps\(editedSteps\) \} : \{\}\)/);
});

test('step rows do not exceed the server-side maximum', () => {
    const source = readFileSync(modulePath, 'utf8');
    assert.equal(MAX_STEPS, 20);
    assert.match(source, /state\.steps\.length >= MAX_STEPS/);
});

test('the editor body scrolls: the form must not break the modal flex chain', () => {
    // modal-dialog-scrollable only reaches .modal-body through .modal-content's flex column. The
    // <form> wrapping body + footer is a flex item with min-height: auto, so without this it
    // refuses to shrink and a long step list is clipped by overflow: hidden with no scrollbar.
    const css = readFileSync(stylePath, 'utf8');
    const rule = css.match(/\.env-steps-modal \[data-env-steps-form\]\s*\{[^}]*\}/);

    assert.ok(rule, 'expected a flex-chain rule for the steps editor form');
    assert.match(rule[0], /display:\s*flex/);
    assert.match(rule[0], /flex-direction:\s*column/);
    assert.match(rule[0], /min-height:\s*0/);

    const source = readFileSync(modulePath, 'utf8');
    assert.match(source, /modal-dialog-scrollable/);
});

test('every env-step colour has a fallback so the panel is never transparent', () => {
    // An undefined custom property invalidates the whole declaration, which is the documented
    // cause of the transparent-background bug.
    const css = readFileSync(stylePath, 'utf8');
    const block = css.slice(css.indexOf('Environment Steps editor'));

    assert.ok(block.includes('.env-step-row {'), 'expected the env-step CSS block to exist');
    for (const match of block.matchAll(/var\((--color-[a-z-]+)([^)]*)\)/g)) {
        assert.ok(
            match[2].includes(','),
            `${match[1]} is used without a fallback in the env-step CSS: ${match[0]}`);
    }
});
