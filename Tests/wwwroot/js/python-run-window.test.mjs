import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/python-run-window.js');
const servicePath = path.resolve('VibeRails/Services/PythonScripts/PythonScriptService.cs');
const mcpServicePath = path.resolve('VibeRails/Services/PythonScripts/PythonScriptMcpService.cs');
const stylePath = path.resolve('VibeRails/wwwroot/style.css');

const {
    PythonRunWindow,
    formatPythonRunOutput,
    isPythonRunOk,
    prettyJson,
    quoteForDisplay,
    readRemembered,
    resolveArgv,
    returnValueOf
} = await import(pathToFileURL(modulePath).href);

const positional = (name, extra = {}) => ({
    name, type: 'string', required: false, defaultValue: null,
    argumentMode: 'positional', flag: null, description: '', ...extra
});
const option = (name, flag, extra = {}) => ({
    name, type: 'string', required: false, defaultValue: null,
    argumentMode: 'option', flag, description: '', ...extra
});

// --- argv: the payload the command line prints ---

test('argv puts positional values in order first, then named options — the MCP mapping', () => {
    const parameters = [
        option('since', '--since'),
        positional('report'),
        option('verbose', '--verbose', { type: 'boolean' }),
        positional('limit', { type: 'integer' })
    ];
    const values = { since: '2026-08-01', report: 'weekly', verbose: 'true', limit: '50' };

    assert.deepEqual(resolveArgv({ parameters, values }).argv,
        ['weekly', '50', '--since', '2026-08-01', '--verbose']);
});

test('a false boolean option is the absence of its flag, never "--flag false"', () => {
    const parameters = [option('verbose', '--verbose', { type: 'boolean' })];
    assert.deepEqual(resolveArgv({ parameters, values: { verbose: 'false' } }).argv, []);
    assert.deepEqual(resolveArgv({ parameters, values: { verbose: 'true' } }).argv, ['--verbose']);
});

test('an empty value falls back to the declared default, and only a required one blocks the run', () => {
    const parameters = [
        option('format', '--format', { defaultValue: 'json' }),
        option('out', '--out', { required: true })
    ];

    // The default fills in silently…
    assert.deepEqual(resolveArgv({ parameters, values: { out: 'x.csv' } }).argv,
        ['--format', 'json', '--out', 'x.csv']);

    // …but a required parameter with nothing behind it stops the run, named for the human.
    const blocked = resolveArgv({ parameters, values: {} });
    assert.deepEqual(blocked.argv, []);
    assert.equal(blocked.error, 'out needs a value.');

    // A non-required parameter with no value and no default is simply absent.
    assert.deepEqual(resolveArgv({ parameters: [option('tag', '--tag')], values: {} }).argv, []);
});

test('typed parameters refuse values Python would choke on, in the type the author declared', () => {
    const integer = [positional('limit', { type: 'integer' })];
    assert.equal(resolveArgv({ parameters: integer, values: { limit: '12' } }).error, null);
    assert.equal(resolveArgv({ parameters: integer, values: { limit: '1.5' } }).error,
        'limit must be a integer.');

    const number = [positional('ratio', { type: 'number' })];
    assert.equal(resolveArgv({ parameters: number, values: { ratio: '0.25' } }).error, null);
    assert.equal(resolveArgv({ parameters: number, values: { ratio: 'lots' } }).error,
        'ratio must be a number.');

    const flag = [option('dry', '--dry', { type: 'boolean' })];
    assert.equal(resolveArgv({ parameters: flag, values: { dry: 'yes' } }).error,
        'dry must be true or false.');
});

test('free argument rows append after the declared ones; blank halves drop out', () => {
    const extras = [
        { flag: '--out', value: 'report.csv' },
        { flag: '', value: 'extra-positional' },
        { flag: '--quiet', value: '' },
        { flag: '', value: '' }
    ];
    assert.deepEqual(resolveArgv({ parameters: [positional('mode')], values: { mode: 'fast' }, extras }).argv,
        ['fast', '--out', 'report.csv', 'extra-positional', '--quiet']);
});

test('a script with nothing declared still runs, with no arguments at all', () => {
    assert.deepEqual(resolveArgv({}), { argv: [], error: null });
    assert.deepEqual(resolveArgv(), { argv: [], error: null });
});

test('the command line quotes only what needs it, so one argument reads as one argument', () => {
    assert.equal(quoteForDisplay('report.csv'), 'report.csv');
    assert.equal(quoteForDisplay('two words'), '"two words"');
    assert.equal(quoteForDisplay('say "hi"'), '"say \\"hi\\""');
    assert.equal(quoteForDisplay(undefined), '');
});

// --- the return value ---

test('a JSON object or array is a return value; a bare scalar is just output', () => {
    assert.equal(prettyJson('{"rows":2}'), '{\n  "rows": 2\n}');
    assert.equal(prettyJson('[1, 2]'), '[\n  1,\n  2\n]');
    assert.equal(prettyJson('42'), null, 'a number a script printed is output, not a return value');
    assert.equal(prettyJson('"done"'), null);
    assert.equal(prettyJson('true'), null);
    assert.equal(prettyJson('{oops'), null);
    assert.equal(prettyJson(''), null);
    assert.equal(prettyJson(null), null);
});

test('the return value comes from the server, or is recovered from stdout the same way', () => {
    // What the server extracted wins.
    assert.equal(returnValueOf({ returnJson: '{"ok":true}', standardOutput: 'noise\n' }),
        '{\n  "ok": true\n}');

    // An older backend sends no returnJson: the whole of stdout…
    assert.equal(returnValueOf({ standardOutput: '  {"ok": false}  ' }), '{\n  "ok": false\n}');

    // …or its last line, so a script can log freely and still return something.
    assert.equal(returnValueOf({ standardOutput: 'scanning…\ndone\n{"rows": 3}\n' }),
        '{\n  "rows": 3\n}');

    assert.equal(returnValueOf({ standardOutput: 'just a log line\n' }), null);
    assert.equal(returnValueOf({ standardOutput: '' }), null);
    assert.equal(returnValueOf(null), null);
});

test('the client convention matches the server that produces it', () => {
    const service = readFileSync(servicePath, 'utf8');
    // Whole stdout, else the last line, and objects/arrays only.
    assert.match(service, /internal static string\? ExtractReturnJson\(string\? standardOutput\)/);
    assert.match(service, /LastIndexOfAny\(\['\\n', '\\r'\]\)/);
    assert.match(service, /candidate\[0\] is not \('\{' or '\['\)/);
    assert.match(service, /JsonValueKind\.Object or JsonValueKind\.Array/);
});

test('argv and stdin are bounded on the server, since they arrive as request data', () => {
    const service = readFileSync(servicePath, 'utf8');
    assert.match(service, /MaxRunArguments = 64;/);
    assert.match(service, /MaxRunArgumentChars = 8_000;/);
    assert.match(service, /MaxStandardInputChars = 256_000;/);
    assert.match(service, /ValidateRunInputs\(arguments, standardInput\);/);
});

test('the positional-then-option order is the one the MCP tool path uses', () => {
    // If BuildArguments ever stops appending options after positionals, this window's
    // preview would quietly disagree with what an agent sends for the same script.
    const mcp = readFileSync(mcpServicePath, 'utf8');
    assert.match(mcp, /positional\.AddRange\(options\);/);
});

// --- run output shared with the row drawer ---

test('formatPythonRunOutput labels stderr and never renders an empty box', () => {
    assert.equal(formatPythonRunOutput({ standardOutput: 'hi\n', standardError: '' }), 'hi');
    assert.equal(formatPythonRunOutput({ standardOutput: 'hi\n', standardError: 'boom\n' }),
        'hi\n\n[stderr]\nboom');
    assert.equal(formatPythonRunOutput({ standardOutput: '', standardError: '' }), '(no output)');
    assert.equal(formatPythonRunOutput(null), '(no output)');
});

test('a run only "worked" when it exited 0 without timing out', () => {
    assert.equal(isPythonRunOk({ exitCode: 0, timedOut: false }), true);
    assert.equal(isPythonRunOk({ exitCode: 1, timedOut: false }), false);
    assert.equal(isPythonRunOk({ exitCode: 0, timedOut: true }), false);
    assert.equal(isPythonRunOk(null), false);
});

// --- remembered inputs ---

test('remembered inputs survive a reopen and shrug off anything unreadable', () => {
    const stored = JSON.stringify({ values: { out: 'x.csv' }, extras: [{ flag: '--v', value: '' }], stdin: 'hi' });
    assert.deepEqual(readRemembered({ getItem: () => stored }, 'report.py'), {
        values: { out: 'x.csv' }, extras: [{ flag: '--v', value: '' }], stdin: 'hi'
    });
    assert.equal(readRemembered({ getItem: () => null }, 'report.py'), null);
    assert.equal(readRemembered({ getItem: () => '{oops' }, 'report.py'), null);
    assert.equal(readRemembered({ getItem: () => '"a string"' }, 'report.py'), null);
    assert.equal(readRemembered({ getItem: () => { throw new Error('denied'); } }, 'x.py'), null);
    assert.equal(readRemembered(undefined, 'x.py'), null);
    // Shapes are coerced, never trusted.
    assert.deepEqual(readRemembered({ getItem: () => '{"values":7,"extras":"no","stdin":9}' }, 'x.py'),
        { values: {}, extras: [], stdin: '' });
});

// --- posting a run ---

function windowFor({ parameters = [], run = null, fail = null } = {}) {
    const posted = [];
    const app = {
        calls: posted,
        async apiCall(url, method, body) {
            posted.push({ url, method, body });
            if (fail) throw new Error(fail);
            return run;
        },
        showError() {}
    };
    // Mirrors PythonScriptsController's run bookkeeping: the window drives busy state through
    // markRunning/clearRunning so rows, the workbench and the nav flyout all learn about it.
    const scripts = {
        runningNames: new Set(),
        recorded: [],
        runChanges: [],
        mcpConfigurationByScript: () => (parameters.length ? { parameters } : null),
        recordRun(name, result) { this.recorded.push({ name, result }); },
        markRunning(name) { this.runningNames.add(name); this.runChanges.push(['start', name]); },
        clearRunning(name) { this.runningNames.delete(name); this.runChanges.push(['end', name]); },
        async runInTerminal(name) { this.terminalLaunch = name; return { tabId: 'tab-1' }; },
        _render() {}
    };
    const view = new PythonRunWindow(app, scripts);
    // Headless: the window paints into a layer it never has here.
    view.layer = null;
    view.name = 'report.py';
    view.parameters = parameters;
    return { view, app, scripts, posted };
}

test('Run posts exactly the argv the command line shows, plus stdin when there is any', async () => {
    const { view, posted } = windowFor({
        parameters: [option('out', '--out'), positional('mode')],
        run: { name: 'report.py', exitCode: 0, timedOut: false, durationMs: 8, standardOutput: '{"rows":1}', standardError: '' }
    });
    view.values = { out: 'x.csv', mode: 'fast' };
    view.stdin = 'piped text';

    const result = await view.execute();

    assert.equal(result.exitCode, 0);
    assert.deepEqual(posted, [{
        url: '/api/v1/python-scripts/run',
        method: 'POST',
        body: { name: 'report.py', arguments: ['fast', '--out', 'x.csv'], standardInput: 'piped text' }
    }]);
    // The preview is built from the same call, so it cannot drift from the payload.
    assert.deepEqual(resolveArgv(view).argv, posted[0].body.arguments);
});

test('an empty stdin box is sent as null, not an empty pipe', async () => {
    const { view, posted } = windowFor({ run: { exitCode: 0, timedOut: false, durationMs: 1, standardOutput: '', standardError: '' } });
    await view.execute();
    assert.equal(posted[0].body.standardInput, null);
});

test('a missing required value never reaches the server, and the run stays clickable', async () => {
    const { view, posted } = windowFor({ parameters: [option('out', '--out', { required: true })] });
    assert.equal(await view.execute(), null);
    assert.deepEqual(posted, [], 'nothing is posted while the command is incomplete');
    assert.equal(view.running, false);
});

test('a run marks the script busy for every surface, and releases it however the run ends', async () => {
    const ok = { exitCode: 0, timedOut: false, durationMs: 3, standardOutput: 'x', standardError: '' };
    const { view, scripts } = windowFor({ run: ok });
    const inFlight = view.execute();
    assert.ok(scripts.runningNames.has('report.py'), 'rows and the nav flyout show it running immediately');
    await inFlight;
    assert.equal(scripts.runningNames.size, 0);
    assert.deepEqual(scripts.recorded, [{ name: 'report.py', result: ok }],
        'the row drawer keeps the result after the window closes');

    // A failed request releases the name too — a stuck "Running…" row would be worse
    // than the error the window shows.
    const broken = windowFor({ fail: 'Python could not be started.' });
    await broken.view.execute();
    assert.equal(broken.scripts.runningNames.size, 0);
    assert.equal(broken.view.running, false);
});

test('a second Run while one is in flight is ignored, not queued', async () => {
    const { view, posted } = windowFor({ run: { exitCode: 0, timedOut: false, durationMs: 1, standardOutput: '', standardError: '' } });
    const first = view.execute();
    assert.equal(await view.execute(), null);
    await first;
    assert.equal(posted.length, 1);
});

// A run outlives its window: close it mid-run and the interpreter keeps going. The window is
// one shared instance, so nothing but runningNames can tell it the script is still busy.
test('reopening a window while the script is still running never starts a second interpreter', async () => {
    const { view, posted, scripts } = windowFor({ run: { exitCode: 0, timedOut: false, durationMs: 1, standardOutput: '', standardError: '' } });
    const first = view.execute();

    // The window is closed and reopened for the same script, exactly as Escape-then-Run does.
    // Headless: open() only has to make its decision, not build the DOM it normally would.
    view.name = null;
    view._mount = () => {};
    globalThis.requestAnimationFrame ??= (fn) => fn();
    view.open('report.py');
    assert.equal(view.running, true, 'the reopened window shows the run that is still in flight');
    assert.equal(await view.execute(), null, 'and refuses to start another');

    await first;
    assert.equal(posted.length, 1, 'one POST, one interpreter');
    assert.equal(view.running, false, 'the surviving run still releases the window it landed in');
    assert.equal(scripts.runningNames.size, 0);
});

test('a run that outlived its window records its result but does not repaint a newer one', async () => {
    const slow = { exitCode: 0, timedOut: false, durationMs: 9, standardOutput: 'slow', standardError: '' };
    const { view, scripts } = windowFor({ run: slow });
    const first = view.execute();

    // The window moves on to a different script while the first POST is still in flight.
    view.name = 'other.py';
    view.running = false;
    view._runToken = {};

    await first;
    assert.deepEqual(scripts.recorded, [{ name: 'report.py', result: slow }],
        'the row drawer and the workbench still get the result');
    assert.equal(view.lastRun, null, 'but the window now showing other.py is left alone');
    assert.equal(view.running, false);
    assert.ok(!scripts.runningNames.has('report.py'));
});

test('Run in terminal refuses while a captured run is going, instead of closing onto nothing', async () => {
    const { view, scripts } = windowFor({ run: { exitCode: 0, timedOut: false, durationMs: 1, standardOutput: '', standardError: '' } });
    let closed = false;
    view.close = () => { closed = true; };

    const inFlight = view.execute();
    await view.runInTerminal();
    assert.equal(closed, false, 'the window stays put and says why');
    assert.equal(scripts.terminalLaunch, undefined);

    await inFlight;
    await view.runInTerminal();
    assert.equal(closed, true);
    assert.equal(scripts.terminalLaunch, 'report.py');
});

// --- the design contract ---

test('the MCP tool prints the return value, which survives a truncated stdout', () => {
    const service = readFileSync(servicePath, 'utf8');
    const mcp = readFileSync(mcpServicePath, 'utf8');

    // ReturnJson is extracted from the FULL stdout while the captured copy is capped, so the
    // return value can be the one thing truncation removes. The tool has to print it itself.
    assert.match(service, /ExtractReturnJson\(result\.StandardOutput\)\);/);
    assert.match(service, /Truncate\(result\.StandardOutput\),/);
    assert.match(mcp, /if \(!string\.IsNullOrWhiteSpace\(run\.ReturnJson\)\)/);
    const format = mcp.slice(mcp.indexOf('static string FormatRunResult'));
    assert.ok(format.indexOf('run.ReturnJson') < format.indexOf('run.StandardOutput'),
        'the return value is printed ahead of the transcript it may have been cut from');
});

test('the PTY escape hatch says it drops the inputs, because /run/interactive takes only a name', () => {
    const source = readFileSync(modulePath, 'utf8');
    assert.match(source, /_paintTerminalLink\(argv\.length > 0 \|\| Boolean\(this\.stdin\)\)/);
    assert.match(source, /without these inputs/);
    const routes = readFileSync(path.resolve('VibeRails/Routes/PythonScriptRoutes.cs'), 'utf8');
    assert.match(routes, /RunPythonScriptInteractive/);
});

test('the run window CSS keeps a fallback on every colour token and spends cyan only on the command line', () => {
    const css = readFileSync(stylePath, 'utf8');
    const block = css.slice(css.indexOf('The run window (python-run-window.js)'),
        css.indexOf('.python-mcp-config-body'));
    assert.ok(block.length > 0, 'the run window has its own CSS block');
    // The slice starts inside the block's header comment; drop what is left of it so the
    // prose below (which names --color-accent) is not read as a rule.
    const rules = block.slice(block.indexOf('*/') + 2).replace(/\/\*[\s\S]*?\*\//g, '');

    for (const match of block.matchAll(/var\((--color-[a-z-]+)([^)]*)\)/g)) {
        assert.ok(match[2].includes(','), `${match[1]} is used without a fallback: ${match[0]}`);
    }

    // The command line is the one accent in the window: the values you supplied, and the
    // rail beside them. Anything else reaching for --color-accent dilutes it.
    const accented = rules.split('}')
        .filter((rule) => rule.includes('--color-accent') && rule.includes('{'))
        .map((rule) => rule.slice(0, rule.indexOf('{')).trim().split(/\r?\n/).pop().trim());
    assert.deepEqual(accented, ['.vb-run-command', '.vb-run-value']);

    // Reduced motion stops the only animation in the window.
    assert.match(block, /@media \(prefers-reduced-motion: reduce\) \{\s*\.vb-run-status\.is-waiting \.vb-run-dot \{\s*animation: none;/);
});
