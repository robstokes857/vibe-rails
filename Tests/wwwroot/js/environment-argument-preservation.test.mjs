import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// VIBE-58 / VB-8WE2S-129 F1: the managed Codex and Claude builders rebuilt CustomArgs from their
// controls alone, so editing (or merely re-saving) a Worker erased flags such as --sandbox read-only.

const modules = path.resolve('VibeRails/wwwroot/js/modules');
const { EnvironmentController } = await import(pathToFileURL(path.join(modules, 'environment-controller.js')).href);

function createController() {
    return new EnvironmentController({
        escapeHtml(value) {
            return String(value)
                .replaceAll('&', '&amp;')
                .replaceAll('<', '&lt;')
                .replaceAll('>', '&gt;')
                .replaceAll('"', '&quot;');
        }
    });
}

test('Codex flags without a control round-trip unchanged and survive a model change', () => {
    const controller = createController();
    const stored = '--model gpt-5.6-sol -c model_reasoning_effort=high --sandbox read-only --search';
    const settings = controller.mergeCodexSettingsFromCustomArgs({}, stored);
    assert.equal(settings.model, 'gpt-5.6-sol');
    assert.equal(settings.effort, 'high');
    assert.equal(settings.additionalArgs, '--sandbox read-only --search');
    assert.equal(controller.buildCodexCustomArgs(settings), stored);
    assert.equal(
        controller.buildCodexCustomArgs({ ...settings, model: 'gpt-5.6-luna' }),
        '--model gpt-5.6-luna -c model_reasoning_effort=high --sandbox read-only --search');
});

test('Codex keeps unknown config keys and features, quoted values included', () => {
    const controller = createController();
    const settings = controller.mergeCodexSettingsFromCustomArgs({},
        '-c sandbox_workspace_write.network_access=true --enable web_search -c "developer_instructions=Be brief" --model gpt-5.6-sol');
    assert.equal(settings.model, 'gpt-5.6-sol');
    assert.equal(settings.additionalArgs,
        '-c sandbox_workspace_write.network_access=true --enable web_search -c "developer_instructions=Be brief"');
    const rebuilt = controller.buildCodexCustomArgs(settings);
    assert.equal(rebuilt,
        '--model gpt-5.6-sol -c sandbox_workspace_write.network_access=true --enable web_search -c "developer_instructions=Be brief"');
    assert.deepEqual(controller.mergeCodexSettingsFromCustomArgs({}, rebuilt), settings);
});

test('Codex flags that have controls are not duplicated into Additional Arguments', () => {
    const controller = createController();
    const settings = controller.mergeCodexSettingsFromCustomArgs({},
        '--yolo --no-alt-screen -c service_tier=fast --enable fast_mode -c features.fast_mode=false');
    assert.equal(settings.additionalArgs, undefined);
    assert.equal(settings.yolo, true);
    assert.equal(settings.noAltScreen, true);
    assert.equal(settings.fastMode, true);
    assert.equal(controller.buildCodexCustomArgs(settings),
        '--dangerously-bypass-approvals-and-sandbox --no-alt-screen -c service_tier=fast -c features.fast_mode=true');
});


test('Codex Ultrafast round-trips every supported config argument spelling without duplicate tiers', () => {
    const controller = createController();
    for (const flag of ['-c service_tier=ultrafast', '--config service_tier=ultrafast',
        '--config=service_tier=ultrafast', '-c=service_tier=ultrafast', '-c \'service_tier="ultrafast"\'']) {
        const settings = controller.mergeCodexSettingsFromCustomArgs({ fastMode: true },
            `--model gpt-6-astra ${flag} --enable fast_mode --sandbox read-only`);
        assert.equal(settings.ultrafastMode, true);
        assert.equal(settings.fastMode, false);
        assert.equal(settings.additionalArgs, '--sandbox read-only');
        assert.equal(controller.buildCodexCustomArgs(settings),
            '--model gpt-6-astra -c service_tier=ultrafast -c features.fast_mode=true --sandbox read-only');
        assert.match(controller.buildCliSettingsHtml('codex', settings), /value="ultrafast" selected/);
    }
});

test('Codex speed overrides replace copied settings and the last tier wins', () => {
    const controller = createController();
    for (const tier of ['fast', 'default']) {
        const settings = controller.mergeCodexSettingsFromCustomArgs({ model: 'gpt-6-astra', ultrafastMode: true },
            `--enable fast_mode -c service_tier=ultrafast -c service_tier=${tier}`);
        assert.equal(settings.ultrafastMode, false);
        assert.equal(settings.fastMode, tier === 'fast');
        assert.doesNotMatch(controller.buildCodexCustomArgs(settings), /ultrafast/);
    }
});

test('Codex refuses to emit Ultrafast for non-Astra models and preserves unmanaged tiers', () => {
    const controller = createController();
    for (const model of ['', 'gpt-6.1-sol', 'gpt-6-sol', 'gpt-5.6-sol']) {
        assert.doesNotMatch(controller.buildCodexCustomArgs({ model, ultrafastMode: true }), /service_tier|fast_mode/);
        assert.match(controller.buildCliSettingsHtml('codex', { model, ultrafastMode: true }), /value="ultrafast"\s+disabled/);
    }
    const settings = controller.mergeCodexSettingsFromCustomArgs({}, '--config=service_tier=flex --search');
    assert.equal(settings.additionalArgs, '--config=service_tier=flex --search');
    assert.equal(controller.buildCodexCustomArgs(settings), settings.additionalArgs);
});

test('Claude flags without a control round-trip unchanged and survive a model change', () => {
    const controller = createController();
    const stored = '--model claude-opus-5-5[1m] --permission-mode plan --add-dir "/work/my repo" --dangerously-skip-permissions --debug';
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, stored);
    assert.equal(settings.model, 'claude-opus-5-5[1m]');
    assert.equal(settings.dangerouslySkipPermissions, true);
    assert.equal(settings.debug, true);
    assert.equal(settings.additionalArgs, '--permission-mode plan --add-dir "/work/my repo"');
    assert.equal(controller.buildClaudeCustomArgs(settings),
        '--model claude-opus-5-5[1m] --dangerously-skip-permissions --debug --permission-mode plan --add-dir "/work/my repo"');
    assert.equal(
        controller.buildClaudeCustomArgs({ ...settings, model: 'claude-fable-5-1[1m]', dangerouslySkipPermissions: false }),
        '--model claude-fable-5-1[1m] --debug --permission-mode plan --add-dir "/work/my repo"');
});

test('Codex and Claude forms show and read back Additional Arguments and unlisted efforts', () => {
    const controller = createController();
    for (const [cli, effortId, effort] of [['codex', 'codex-effort', 'none'], ['claude', 'claude-effort', 'turbo']]) {
        const html = controller.buildCliSettingsHtml(cli, { effort, additionalArgs: '--flag "a b"' });
        assert.match(html, new RegExp(`id="${cli}-additional-args" value="--flag &quot;a b&quot;"`));
        const effortSelect = html.slice(html.indexOf(`id="${effortId}"`), html.indexOf('</select>', html.indexOf(`id="${effortId}"`)));
        assert.match(effortSelect, new RegExp(`<option value="${effort}" selected>${effort} \\(custom\\)</option>`));
        assert.doesNotMatch(controller.buildCliSettingsHtml(cli, { effort: 'high' }), /\(custom\)<\/option>/);
    }

    const fields = {
        'env-initial-message': { value: 'Review.' },
        'codex-model': { value: 'gpt-5.6-sol' },
        'codex-effort': { value: 'high' },
        'codex-yolo': { checked: false },
        'codex-no-alt-screen': { checked: false },
        'codex-speed': { value: '' },
        'codex-additional-args': { value: '--sandbox read-only --search' }
    };
    const root = { getElementById: id => fields[id] ?? null };
    const payload = controller.buildEnvironmentSavePayload('codex', null, root);
    assert.equal(payload.customArgs, '--model gpt-5.6-sol -c model_reasoning_effort=high --sandbox read-only --search');
});
