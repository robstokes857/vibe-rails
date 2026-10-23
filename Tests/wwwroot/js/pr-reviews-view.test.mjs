import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import {
    summarizePullRequests, retiredBlockerNote, isLive, trackedRow, openRow, nextUpCard, pendingReviewForm,
    reviewCard, detailAlerts, detailHeader, pullRequestLink, repositoryLink, shortSha
} from '../../../VibeRails/wwwroot/js/modules/pr-reviews-view.js';

const pr = (number, status, gitHubState = 'open', extra = {}) => ({
    number, status, gitHubState, title: `PR ${number}`, author: 'octocat', summary: '', completedCount: 1, ...extra
});

test('unsynced PRs are live; only merged and closed retire a PR', () => {
    assert.equal(isLive(pr(1, 'complete', 'unknown')), true);
    assert.equal(isLive(pr(1, 'complete', 'open')), true);
    assert.equal(isLive(pr(1, 'complete', 'merged')), false);
    assert.equal(isLive(pr(1, 'complete', 'closed')), false);
});

test('working lists hold live PRs only, and status counts follow the lifecycle filter', () => {
    const prs = [
        pr(1, 'blocker'), pr(2, 'blocker', 'merged'), pr(3, 'blocker', 'closed'),
        pr(4, 'in_review'), pr(5, 'in_review', 'merged'), pr(6, 'complete', 'unknown'), pr(7, 'not_started')
    ];

    const live = summarizePullRequests(prs, { lifecycle: 'live' });
    assert.deepEqual(live.blocked.map(p => p.number), [1]);
    assert.deepEqual(live.pending.map(p => p.number), [4]);
    assert.deepEqual(live.retiredBlockers, { total: 2, merged: 1, closed: 1 });
    assert.deepEqual(live.lifecycleCounts, { all: 7, live: 4, merged: 2, closed: 1 });
    // The Blocker count on the filter row matches the Blocked section above it.
    assert.equal(live.statusCounts.blocker, live.blocked.length);
    assert.equal(live.statusCounts.all, 4);

    const merged = summarizePullRequests(prs, { lifecycle: 'merged', status: 'blocker' });
    assert.deepEqual(merged.visible.map(p => p.number), [2]);
    assert.equal(merged.statusCounts.in_review, 1);
});

test('the text filter matches number, title, author and overview', () => {
    const prs = [pr(12, 'complete', 'open', { title: 'Fix imports' }), pr(34, 'complete', 'open', { summary: 'touches widgets' })];
    const find = query => summarizePullRequests(prs, { lifecycle: 'all', query }).visible.map(p => p.number);
    assert.deepEqual(find('#12'), [12]);
    assert.deepEqual(find('IMPORTS'), [12]);
    assert.deepEqual(find('widgets'), [34]);
    assert.deepEqual(find('octo'), [12, 34]);
});

test('a retired blocker is said out loud instead of dropped', () => {
    assert.equal(retiredBlockerNote({ total: 0, merged: 0, closed: 0 }), '');
    assert.match(retiredBlockerNote({ total: 1, merged: 1, closed: 0 }), /^1 more PR carries a standing blocker but 1 merged anyway\./);
    assert.match(retiredBlockerNote({ total: 3, merged: 2, closed: 1 }), /2 merged anyway and 1 closed without merging/);
});

test('rows escape everything that came from GitHub', () => {
    const hostile = '<img src=x onerror=alert(1)>';
    const row = trackedRow(pr(5, 'blocker', 'open', { title: hostile, author: '"><script>' }), 'danger');
    assert.doesNotMatch(row, /<img|<script/);
    assert.match(row, /&lt;img src=x onerror=alert\(1\)&gt;/);
    assert.match(row, /data-pr-open="5"/);

    const open = openRow({
        number: 6, title: hostile, author: 'a', openedUtc: '2026-08-01T00:00:00Z', headSha: '0123456789',
        url: 'javascript:alert(1)', reviewCount: 0, trackerStatus: 'not_started', reason: 'never_reviewed', why: '<b>why</b>'
    }, false);
    assert.doesNotMatch(open, /href="javascript:/);
    assert.doesNotMatch(open, /<b>why/);
    assert.match(open, /data-pr-start="6"/);
    assert.match(open, /<code>0123456<\/code>/);
});

test('only github.com pull request and repository URLs become links', () => {
    assert.match(pullRequestLink('https://github.com/octo/widgets/pull/7', 'x'), /href="https:\/\/github\.com\/octo\/widgets\/pull\/7"/);
    assert.match(pullRequestLink('https://github.com/octo/widgets/pull/7', 'x'), /rel="noopener noreferrer" data-pr-external/);
    assert.doesNotMatch(pullRequestLink('https://evil.example/octo/widgets/pull/7', 'x'), /href=/);
    assert.match(repositoryLink('https://github.com/octo/widgets', 'octo/widgets'), /href=/);
    assert.doesNotMatch(repositoryLink('https://github.com/octo/widgets" onclick="x', 'r'), /href=/);
    assert.equal(shortSha(''), '—');
});

test('next up offers resume for an open pass and start otherwise', () => {
    const counts = { open: 3, eligible: 1, drafts: 1, upToDate: 1 };
    const entry = { number: 9, title: 't', author: 'a', openedUtc: '2026-08-01T00:00:00Z', headSha: 'abc', url: 'https://github.com/o/r/pull/9' };
    assert.match(nextUpCard({ next: { ...entry, reason: 'in_review' }, counts }, false), /data-pr-open="9"[^>]*>Open review/);
    assert.match(nextUpCard({ next: { ...entry, reason: 'never_reviewed' }, counts }, false), /data-pr-start="9"/);
    assert.match(nextUpCard({ next: null, counts }, false), /All 2 non-draft open PRs are reviewed/);
});

test('the pending form says when completing will leave a blocker', () => {
    const pending = { id: 1, feedback: '</textarea><script>', verdict: 'request_changes', blocking: true, startedUtc: 'a', updatedUtc: 'a' };
    const form = pendingReviewForm(pr(3, 'in_review'), pending);
    assert.match(form, /Complete as blocker/);
    assert.doesNotMatch(form, /data-pr-blocker-note hidden/);
    assert.doesNotMatch(form, /<\/textarea><script>/);

    const clean = pendingReviewForm(pr(3, 'in_review'), { ...pending, verdict: 'comments', blocking: false });
    assert.match(clean, /Complete review/);
    assert.match(clean, /data-pr-blocker-note hidden/);
});

test('only the newest completed review offers the blocker toggle', () => {
    const review = { id: 4, feedback: 'body', verdict: 'comments', blocking: false, reviewedUtc: '2026-08-01T00:00:00Z', headSha: '' };
    assert.match(reviewCard(review, { isLatest: true }), /data-pr-toggle-blocker="4"[^>]*>Mark as blocker/);
    assert.match(reviewCard({ ...review, blocking: true }, { isLatest: true }), /Clear blocker/);
    assert.doesNotMatch(reviewCard(review, { isLatest: false }), /data-pr-toggle-blocker/);
    assert.match(reviewCard({ ...review, feedback: 'x'.repeat(901) }), /is-collapsed/);
    assert.match(reviewCard(review, { editing: true }), /data-pr-review-edit="4"/);
});

test('a merged blocker keeps its record and says GitHub moved on', () => {
    assert.match(detailAlerts(pr(1, 'blocker')), /Do not merge/);
    assert.match(detailAlerts(pr(1, 'blocker', 'merged')), /merged with the review&#39;s blocker still standing/);
    assert.equal(detailAlerts(pr(1, 'complete')), '');
    assert.doesNotMatch(detailHeader(pr(1, 'complete'), 'javascript:alert(1)'), /Open on GitHub/);
});

test('the controller stands down after its first await and app.js unloads it on every navigation', () => {
    const controller = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/pr-reviews-controller.js'), 'utf8');
    const app = readFileSync(path.resolve('VibeRails/wwwroot/app.js'), 'utf8');
    const start = controller.indexOf('async loadView(');
    const pollAt = controller.indexOf('this.pollTimer = window.setInterval', start);
    const body = controller.slice(start, pollAt);
    assert.ok(body.search(/await this\.loadList\(generation\);\s*if \(this\.root !== root\) return;/) >= 0,
        'loadView must check it is still current before starting the poll');
    assert.match(controller, /if \(this\.app\.isUiHidden\(\) \|\| this\.pollPending\) return null;/);
    const loadView = app.slice(app.indexOf('loadView(view, data = {})'));
    assert.match(loadView.slice(0, loadView.indexOf('const views')), /this\.prReviewsController\?\.unload\?\.\(\);/);
    assert.match(app, /'pr-reviews',\s*'ai-agents'/);
});
