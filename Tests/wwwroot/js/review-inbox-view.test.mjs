import test from 'node:test';
import assert from 'node:assert/strict';
import {
    canAct, chosenClone, cloneChoice, cloneWarning, clonesOf, describeRefresh, describeSigning, describeStatus, emptyMessage, folderName,
    isReviewPageUrl, requestRow, statusTone, summarizeInbox
} from '../../../VibeRails/wwwroot/js/modules/review-inbox-view.js';

const reference = 'rvw_0123456789abcdef0123456789abcdef';
const now = Date.now();
const item = (extra = {}) => ({
    id: reference, repository: 'vibe-rails', requestedBy: 'dev@example.test', status: 'open', fileCount: 2, sessionCount: 1,
    cardKey: 'VIBE-208', createdUtc: new Date(now - 5 * 60_000).toISOString(), expiresUtc: new Date(now + 20 * 86_400_000).toISOString(),
    decided: false, readUrl: `https://viberails.ai/Reviews/requests/${reference}`, signing: null, ...extra
});
const view = (extra = {}) => ({
    hasApiKey: true, email: 'me@example.test', emailVerified: true, fetchedUtc: new Date(now - 3 * 60_000).toISOString(),
    nextDueUtc: new Date(now + 12 * 60_000).toISOString(), lastErrorCode: null, lastError: null, requests: [item()], more: false,
    project: 'C:\\work\\vibe-rails', popupAvailable: true, ...extra
});

test('an unanswered request can be answered while viberails.ai still takes decisions, not while its popup is open', () => {
    assert.equal(canAct(item(), view()), true);
    assert.equal(canAct(item({ decided: true }), view()), false);
    // Another reviewer's decision does not close the request to this account (F2): several approvers, or an approval after a rejection.
    assert.equal(canAct(item({ status: 'approved' }), view()), true);
    assert.equal(canAct(item({ status: 'rejected' }), view()), true);
    assert.equal(canAct(item({ status: 'cancelled' }), view()), false);
    assert.equal(canAct(item({ status: 'expired' }), view()), false);
    assert.equal(canAct(item(), view({ hasApiKey: false })), false);
    assert.equal(canAct(item({ signing: { outcome: 'signing' } }), view()), false);
});

test('status and signing wording never leak raw codes', () => {
    assert.equal(describeStatus(item()), 'Waiting for your decision');
    assert.equal(describeStatus(item({ status: 'cancelled' })), 'Cancelled by the requester');
    assert.equal(describeStatus(item({ decided: true })), 'You answered; waiting for others');
    assert.equal(describeStatus(item({ status: 'approved' })), 'Approved by another reviewer; your decision is still taken');
    assert.equal(describeStatus(item({ status: 'rejected', decided: true })), 'Rejected');
    assert.equal(statusTone(item()), 'warning');
    assert.equal(statusTone(item({ status: 'rejected' })), 'danger');
    assert.equal(describeSigning({ outcome: 'approved' }), 'Approved from this computer');
    assert.equal(describeSigning({ outcome: 'popup_unavailable' }), 'The signing popup could not open on this computer');
    assert.equal(describeSigning(null), '');
});

test('the refresh line says when the list was checked and when the next automatic check is due', () => {
    assert.match(describeRefresh(view(), now), /^Checked 3 minutes ago · next automatic check in 12 min$/);
    assert.match(describeRefresh(view({ nextDueUtc: new Date(now + 10_000).toISOString() }), now), /within a minute$/);
    assert.equal(describeRefresh(view({ hasApiKey: false })), 'Sign in to viberails.ai in Settings to see requests addressed to your account.');
    assert.equal(describeRefresh(view({ fetchedUtc: null, lastError: 'offline' })), 'Not checked yet: offline');
});

test('empty states explain the key, the verified email and a failed first check', () => {
    assert.match(emptyMessage(view({ hasApiKey: false })), /Sign in to viberails.ai/);
    assert.match(emptyMessage(view({ emailVerified: false })), /no verified email/);
    assert.match(emptyMessage(view({ fetchedUtc: null, lastError: 'Could not connect' })), /Could not connect/);
    assert.equal(emptyMessage(view()), 'Nobody has asked you to review a change yet.');
    assert.deepEqual(summarizeInbox(view({ requests: [item(), item({ decided: true, status: 'rejected' })], more: true })), { total: 2, open: 1, answered: 1, more: true });
});

test('rows are escape-first and only link the hosted reviewer page for a well-formed reference', () => {
    const hostile = item({ repository: '<img src=x onerror=alert(1)>', requestedBy: 'dev@example.test"><b>', cardKey: '<b>x</b>' });
    const markup = requestRow(hostile, view());
    assert.doesNotMatch(markup, /<img src=x/);
    assert.match(markup, /&lt;img src=x onerror=alert\(1\)&gt;/);
    assert.match(markup, /&lt;b&gt;x&lt;\/b&gt;/);
    assert.match(markup, new RegExp(`href="https://viberails.ai/Reviews/requests/${reference}"[^>]*data-ri-external`));
    assert.match(markup, /data-ri-action="approve"(?![^>]*disabled)/);
    assert.match(markup, /data-ri-action="reject"(?![^>]*disabled)/);

    const phishing = requestRow(item({ readUrl: 'https://evil.example/Reviews/requests/' + reference }), view());
    assert.doesNotMatch(phishing, /evil\.example/);
    assert.equal(isReviewPageUrl('https://viberails.ai/Reviews/requests/rvw_short'), false);
    assert.equal(isReviewPageUrl(`https://viberails.ai/Reviews/requests/${reference}`), true);

    const noPopup = requestRow(item(), view({ popupAvailable: false }));
    assert.match(noPopup, /data-ri-action="approve" disabled/);
    assert.match(noPopup, /data-ri-action="reject"(?![^>]*disabled)/);
    const answered = requestRow(item({ decided: true, status: 'rejected' }), view());
    assert.match(answered, /data-ri-action="approve" disabled/);
    assert.match(answered, /data-ri-action="reject" disabled/);
    assert.match(requestRow(item({ signing: { outcome: 'signing' } }), view()), /Signing popup open on this computer/);
});

// VIBE-213 item 7 (Rob, 2026-10-25): warn when the requester's repository name differs from the chosen
// clone's folder, never refuse; with more than one root open, pick the clone beside the requester's name.
const here = { path: 'C:\\work\\vibe-rails', name: 'vibe-rails', current: true };
const other = { path: 'D:\\src\\VibeRails-Front', name: 'VibeRails-Front', current: false };
const review = { path: 'D:\\src\\vibe-rails-review', name: 'vibe-rails-review', current: false };

test('the clone defaults to one named like the requester\'s folder, this window first, and keeps the reviewer\'s pick', () => {
    assert.equal(folderName('C:\\work\\vibe-rails\\'), 'vibe-rails');
    assert.equal(folderName('/home/dev/vibe-rails'), 'vibe-rails');
    assert.deepEqual(clonesOf(view()), [here]);
    const several = view({ clones: [here, other, review] });
    assert.equal(chosenClone(item(), several).path, here.path);
    assert.equal(chosenClone(item({ repository: 'VIBERAILS-FRONT' }), several).path, other.path);
    assert.equal(chosenClone(item({ repository: 'unknown' }), several).path, here.path);
    assert.equal(chosenClone(item(), several, review.path).path, review.path);
    assert.equal(chosenClone(item(), several, 'C:\\gone').path, here.path);
});

test('a different folder name is a warning before the launch, and the picker shows only with several roots open', () => {
    assert.equal(cloneWarning(item(), here), '');
    assert.match(cloneWarning(item({ repository: 'payments' }), here), /made in a folder named "payments".*your "vibe-rails" folder \(C:\\work\\vibe-rails\)/);

    const single = requestRow(item({ repository: 'payments' }), view());
    assert.doesNotMatch(single, /data-ri-clone[ >]/);
    assert.match(single, /role="note">This request was made in a folder named &quot;payments&quot;/);
    assert.match(single, /data-ri-action="approve"(?![^>]*disabled)/);

    const picked = requestRow(item(), view({ clones: [here, other, review] }), review.path);
    assert.match(picked, /<select class="form-select form-select-sm" data-ri-clone>/);
    assert.match(picked, /requested from vibe-rails/);
    assert.match(picked, /<option value="D:\\src\\vibe-rails-review"[^>]*selected>vibe-rails-review<\/option>/);
    assert.match(picked, /vibe-rails \(this window\)/);
    assert.match(picked, /folder named &quot;vibe-rails&quot;.*your &quot;vibe-rails-review&quot; folder/);

    // Only a row that can still be approved offers a choice.
    assert.doesNotMatch(requestRow(item({ decided: true }), view({ clones: [here, other] })), /ri-clone-choice/);
    assert.doesNotMatch(requestRow(item(), view({ clones: [here, other], popupAvailable: false })), /ri-clone-choice/);
});

test('the requester\'s repository and local paths are escaped in the picker and the warning', () => {
    const hostile = item({ repository: '<img src=x onerror=alert(1)>' });
    const markup = cloneChoice(hostile, view({ clones: [here, { path: 'D:\\a"<b>', name: 'a"<b>', current: false }] }));
    assert.doesNotMatch(markup, /<img src=x|<b>/);
    assert.match(markup, /requested from &lt;img src=x onerror=alert\(1\)&gt;/);
    assert.match(markup, /value="D:\\a&quot;&lt;b&gt;"/);
});
