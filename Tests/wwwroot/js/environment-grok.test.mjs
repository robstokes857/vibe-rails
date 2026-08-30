import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const environmentModule = path.resolve('VibeRails/wwwroot/js/modules/environment-controller.js');
const { EnvironmentController } = await import(pathToFileURL(environmentModule).href);

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

const CANONICAL_EFFORTS = ['', 'none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];

test('buildGrokCustomArgs pins the model and omits effort when unset', () => {
    const args = createController().buildGrokCustomArgs({});
    assert.equal(args, '-m grok-4.6');
});

test('buildGrokCustomArgs emits --effort and --yolo', () => {
    const args = createController().buildGrokCustomArgs({
        effort: 'XHigh',
        yoloMode: true
    });
    assert.equal(args, '-m grok-4.6 --effort xhigh --yolo');
});

test('mergeGrokSettingsFromCustomArgs reads --effort and --reasoning-effort forms', () => {
    const controller = createController();

    assert.equal(
        controller.mergeGrokSettingsFromCustomArgs({}, '-m grok-4.6 --effort xhigh').effort,
        'xhigh'
    );
    assert.equal(
        controller.mergeGrokSettingsFromCustomArgs({}, '--effort=high').effort,
        'high'
    );
    assert.equal(
        controller.mergeGrokSettingsFromCustomArgs({}, '--reasoning-effort medium').effort,
        'medium'
    );
    assert.equal(
        controller.mergeGrokSettingsFromCustomArgs({}, '--reasoning-effort=max').effort,
        'max'
    );
});

test('mergeGrokSettingsFromCustomArgs does not leave effort flags in additional args', () => {
    const settings = createController().mergeGrokSettingsFromCustomArgs(
        {},
        '-m grok-4.6 --effort xhigh --yolo --debug'
    );

    assert.equal(settings.effort, 'xhigh');
    assert.equal(settings.yoloMode, true);
    assert.equal(settings.additionalArgs, '--debug');
});

test('merge then build rewrites --reasoning-effort to --effort', () => {
    const controller = createController();
    const settings = controller.mergeGrokSettingsFromCustomArgs(
        {},
        '-m grok-4.6 --reasoning-effort xhigh --yolo'
    );
    assert.equal(
        controller.buildGrokCustomArgs(settings),
        '-m grok-4.6 --effort xhigh --yolo'
    );
});

test('Grok effort dropdown lists the canonical thinking levels', () => {
    const html = createController().buildCliSettingsHtml('grok-4.6', {});
    const values = [...html.matchAll(/<option value="([^"]*)"/g)].map(match => match[1]);
    assert.deepEqual(values, CANONICAL_EFFORTS);
    assert.match(html, /id="grok-effort"/);
    assert.match(html, /--reasoning-effort/);
});

test('unknown Grok effort values round-trip as a custom option', () => {
    const html = createController().buildCliSettingsHtml('grok-4.6', { effort: 'Deep' });
    assert.match(html, /value="deep" selected>deep \(custom\)/);
    assert.equal(createController().buildGrokCustomArgs({ effort: 'Deep' }), '-m grok-4.6 --effort deep');
});

test('mergeGrokSettingsFromCustomArgs still drops leftover OpenCode flags', () => {
    const settings = createController().mergeGrokSettingsFromCustomArgs(
        {},
        '-m grok-4.6 --auto --pure --agent build --effort xhigh'
    );

    assert.equal(settings.effort, 'xhigh');
    assert.equal(settings.yoloMode, true);
    assert.equal(settings.additionalArgs, undefined);
});
