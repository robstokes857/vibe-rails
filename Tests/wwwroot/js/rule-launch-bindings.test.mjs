import test from 'node:test';
import assert from 'node:assert/strict';
import { ruleChoices, bindingTrigger } from '../../../VibeRails/wwwroot/js/modules/launch-bindings.js';

test('rule choices retain exact text and declaring file from the real agents response', () => {
    const text = "File Lock('name <two>.txt')";
    const choices = ruleChoices([
        { path:'C:\\project\\vc.rules.md', rules:[{text,enforcement:'STOP'},{text,enforcement:'WARN'}] },
        { path:'C:\\project\\nested\\vc.rules.md', rules:[{text,enforcement:'STOP'}] }
    ], 'C:\\project');
    assert.equal(choices.length, 2);
    assert.deepEqual(choices.map(item => JSON.parse(item.value)), [
        {sourcePath:'vc.rules.md',ruleText:text},
        {sourcePath:'nested/vc.rules.md',ruleText:text}
    ]);
});

test('switching from a schedule to a rule response drops stale schedule fields', () => {
    const draft = {kind:6, scheduleKind:0, intervalMinutes:60, daysOfWeekMask:62, localTime:'09:00', timeZoneId:'UTC'};
    assert.deepEqual(bindingTrigger(draft), {kind:6});
    assert.deepEqual(bindingTrigger({...draft,kind:4}), {kind:4});
    assert.deepEqual(bindingTrigger({...draft,kind:0}), {kind:0,scheduleKind:0,intervalMinutes:60});
});
