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

const CANONICAL_EFFORTS = ['', 'low', 'medium', 'high', 'xhigh'];

function optionValues(html, id) {
    const start = html.indexOf(`id="${id}"`);
    const slice = html.slice(start, html.indexOf('</select>', start));
    return [...slice.matchAll(/<option value="([^"]*)"/g)].map(match => match[1]);
}

test('Grok model dropdown lists 4.7 then 4.6 and omits -m when left default', () => {
    const html = createController().buildCliSettingsHtml('grok', {});
    assert.deepEqual(optionValues(html, 'grok-model'), ['', 'grok-4.7', 'grok-4.6']);
    assert.equal(createController().buildGrokCustomArgs({}), '');
    assert.equal(createController().buildGrokCustomArgs({ model: 'grok-4.7' }), '-m grok-4.7');
    assert.equal(createController().buildGrokCustomArgs({ model: 'grok-4.6' }), '-m grok-4.6');
});

test('buildGrokCustomArgs emits --effort and --yolo', () => {
    const args = createController().buildGrokCustomArgs({
        model: 'grok-4.6',
        effort: 'XHigh',
        yoloMode: true
    });
    assert.equal(args, '-m grok-4.6 --effort xhigh --yolo');
});

test('mergeGrokSettingsFromCustomArgs reads the model and both effort forms', () => {
    const controller = createController();

    const saved = controller.mergeGrokSettingsFromCustomArgs({}, '-m grok-4.6 --effort xhigh');
    assert.equal(saved.model, 'grok-4.6');
    assert.equal(saved.effort, 'xhigh');
    assert.equal(
        controller.mergeGrokSettingsFromCustomArgs({}, '--model=grok-4.7').model,
        'grok-4.7'
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
        controller.mergeGrokSettingsFromCustomArgs({}, '--reasoning-effort=low').effort,
        'low'
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
    const html = createController().buildCliSettingsHtml('grok', {});
    assert.deepEqual(optionValues(html, 'grok-effort'), CANONICAL_EFFORTS);
    assert.match(html, /id="grok-effort"/);
    assert.match(html, /--reasoning-effort/);
});

test('unknown Grok effort values round-trip as a custom option', () => {
    const html = createController().buildCliSettingsHtml('grok-4.6', { effort: 'Deep' });
    assert.match(html, /value="deep" selected>deep \(custom\)/);
    assert.equal(createController().buildGrokCustomArgs({ effort: 'Deep' }), '--effort deep');
});

test('retired none/max Grok effort values render as custom, not pinned', () => {
    const noneHtml = createController().buildCliSettingsHtml('grok-4.6', { effort: 'none' });
    const maxHtml = createController().buildCliSettingsHtml('grok-4.6', { effort: 'max' });
    assert.match(noneHtml, /value="none" selected>none \(custom\)/);
    assert.match(maxHtml, /value="max" selected>max \(custom\)/);
    assert.equal((noneHtml.match(/value="none"/g) || []).length, 1);
    assert.equal((maxHtml.match(/value="max"/g) || []).length, 1);
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
