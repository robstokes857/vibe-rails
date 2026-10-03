import test from 'node:test';
import assert from 'node:assert/strict';
import { previousWorkHtml } from '../../../VibeRails/wwwroot/js/modules/board-previous-work.js';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

test('previous work escapes task data and shows file reasons, roles and provenance', () => {
    const html = previousWorkHtml({ previousWork: { outcome: '<script>bad</script>', author: { label: 'Codex' },
        files: [{ path: 'editor.js', reason: '<img src=x>', role: 'implementation', commit: 'abc1234', status: 'missing or renamed' }] } });
    assert.ok(html.includes('&lt;script&gt;'));
    assert.ok(!html.includes('<script>'));
    assert.ok(!html.includes('<img'));
    for (const value of ['editor.js', 'implementation', 'abc1234', 'missing or renamed', 'Codex']) assert.ok(html.includes(value));
    assert.equal(previousWorkHtml({}), '');
});

test('discussion sessions never become the working agent', () => {
    const isWorking = BoardController.prototype.isWorkingSession;
    assert.equal(isWorking({ active: true, origin: 'chat' }), false);
    assert.equal(isWorking({ active: true, origin: 'code_review' }), false);
    assert.equal(isWorking({ active: true, origin: 'launch' }), true);
});
