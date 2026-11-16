import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import {
    agentSkipNoticeMarkup, cardRunRowMarkup, laneEntryRowMarkup, stepRunState, stepSummaryText
} from '../../../VibeRails/wwwroot/js/modules/board-step-runs.js';

// VIBE-338: the card rail printed "Process: succeeded · Result: passed" and "Action completed:
// succeeded" as block text. Each step now reads as one status pill with its report below.
test('a lane step reads as one state with a tone', () => {
    assert.deepEqual(stepRunState({ processStatus: 'succeeded', verdict: 'passed' }), { label: 'Passed', tone: 'success', icon: 'circle-check' });
    assert.equal(stepRunState({ processStatus: 'failed', verdict: 'failed' }).tone, 'danger');
    assert.equal(stepRunState({ processStatus: 'running', runId: 'r' }).label, 'Running');
    assert.equal(stepRunState({ processStatus: 'queued' }).label, 'Waiting');
    assert.equal(stepRunState({ processStatus: 'queued', runId: 'r' }).label, 'Queued');
    assert.equal(stepRunState({ processStatus: 'succeeded' }).label, 'Finished');
    assert.deepEqual(stepRunState({ verdict: 'skipped', skipRequested: true, skippedByAgent: 'Claude' }),
        { label: 'Skipped by agent', tone: 'warning', icon: 'forward-step' });
    assert.equal(stepRunState({ verdict: 'skipped', skipRequested: true }).label, 'Skipped');
});

test('a report that only repeats the pill is not shown', () => {
    assert.equal(stepSummaryText({ summary: 'Action completed: succeeded' }), '');
    assert.equal(stepSummaryText({ summary: 'User skipped this lane entry.' }), '');
    assert.equal(stepSummaryText({ summary: 'No commits are linked to this card.' }), 'No commits are linked to this card.');
    assert.equal(stepSummaryText({}, { error: 'Script failed.' }), 'Script failed.');
    assert.equal(stepSummaryText({ skippedByAgent: 'Claude', summary: 'Skipped by agent: Docs only.' }), 'Docs only.');
});

test('a long report is clamped behind Show more, and text stays escaped', () => {
    const long = 'Reviewed <b>5948ae35</b>. ' + 'No blocking findings. '.repeat(12);
    const html = laneEntryRowMarkup({ id: 'e1', processStatus: 'succeeded', verdict: 'passed', summary: long }, 'Code review');
    assert.match(html, /board-step-run-summary is-clamped/);
    assert.match(html, /data-step-more="e1"/);
    assert.match(html, /&lt;b&gt;5948ae35&lt;\/b&gt;/);
    assert.doesNotMatch(html, /<b>/);
    // A passed step offers no skip; a finished one offers Skip, a queued or running one Stop and skip.
    assert.doesNotMatch(html, /data-launch-skip/);
    assert.match(laneEntryRowMarkup({ id: 'e2', processStatus: 'failed', verdict: 'failed' }, 'Check'), />Skip</);
    assert.match(laneEntryRowMarkup({ id: 'e4', processStatus: 'queued' }, 'Check'), />Stop and skip</);
    assert.match(laneEntryRowMarkup({ id: 'e3', processStatus: 'running', runId: 'r' }, 'Check'), />Stop and skip</);
});

test('a run row names the run and offers its recording', () => {
    const html = cardRunRowMarkup({ id: 'r1', status: 'failed', sessionId: 's1', snapshot: { action: { name: 'Quality' } },
        endedUtc: '2026-11-16T08:07:00Z' });
    assert.match(html, /Quality/);
    assert.match(html, /is-danger/);
    assert.match(html, /data-launch-replay="s1"/);
});

test('an open agent skip shows its step, reason and a Dismiss button', () => {
    assert.equal(agentSkipNoticeMarkup(null), '');
    const html = agentSkipNoticeMarkup({ stepName: 'Code quality', reason: 'Docs only <script>', authorLabel: 'Claude', openCount: 3 });
    assert.match(html, /Skipped by agent/);
    assert.match(html, /Code quality/);
    assert.match(html, /Docs only &lt;script&gt;/);
    assert.match(html, /2 earlier skips in Comments/);
    assert.match(html, /data-board-dismiss-agent-skip/);
});

test('the tile marks an open agent skip, and VibeRails comments wear its logo', () => {
    const controller = Object.create(BoardController.prototype);
    assert.equal(controller.agentSkipIndicator(null), '');
    const badge = controller.agentSkipIndicator({ stepName: 'Code quality', reason: 'Docs only', openCount: 1 });
    assert.match(badge, /board-agent-skip-badge/);
    assert.match(badge, /title="Skipped by agent: Code quality\. Docs only"/);
    const system = controller.authorInfo({ kind: 'system', label: 'Code quality check' });
    assert.equal(system.logo, 'assets/img/logo.png');
    assert.equal(system.label, 'Code quality check');
    assert.equal(controller.authorInfo({ kind: 'user' }).initials, 'ME');
});
