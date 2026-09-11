import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

// Pins the board's move from the localStorage placeholder to the real backend
// (VibeRails/Routes/BoardRoutes.cs) and the "assignee is an LLM" contract.

const apiPath = path.resolve('VibeRails/wwwroot/js/modules/board-api.js');
const controllerPath = path.resolve('VibeRails/wwwroot/js/modules/board-controller.js');
const indexPath = path.resolve('VibeRails/wwwroot/index.html');

test('board-api.js is a thin client over /api/v1/board with no local placeholder left', () => {
    const source = readFileSync(apiPath, 'utf8');
    assert.doesNotMatch(source, /localStorage|createSeed|resetBoardDataAsync|getBoardMembersAsync/,
        'the placeholder data layer must be gone');
    assert.match(source, /const BASE = '\/api\/v1\/board'/);
    assert.match(source, /app\.apiCall\(`\$\{BASE\}\$\{path\}`, method, body, \{ showLoading: false, preferErrorResponseMessage: true, \.\.\.extra \}\)/,
        'every call rides app.apiCall so the session cookie + tab header and the { error } body handling apply');

    // Every export the controller consumes exists and is server-backed.
    for (const name of [
        'attach', 'getBoardColumnsAsync', 'createBoardColumnAsync', 'updateBoardColumnAsync', 'deleteBoardColumnAsync',
        'reorderBoardColumnsAsync', 'getBoardCardsAsync', 'getBoardCardAsync', 'createBoardCardAsync', 'updateBoardCardAsync',
        'deleteBoardCardAsync', 'moveBoardCardAsync', 'launchBoardCardAsync', 'addBoardCommentAsync', 'addCardAttachmentAsync',
        'deleteCardAttachmentAsync', 'getCardCommitsAsync', 'addCardCommitAsync', 'removeCardCommitAsync', 'getCommitDiffAsync',
        'getCardSessionsAsync', 'addCardSessionAsync', 'updateCardSessionAsync', 'removeCardSessionAsync'
    ]) {
        assert.match(source, new RegExp(`^\\s+${name},?$`, 'm'), `BoardApi must export ${name}`);
    }

    // The routes the server maps (BoardRoutes.cs), by the paths the client builds.
    for (const route of [
        "call('/columns')", "call('/columns', 'POST', payload)", "call('/columns/order', 'PUT', { orderedIds })",
        "call('/cards')", "call('/cards', 'POST', payload)", "/move`, 'POST'", "/launch`, 'POST'",
        "/comments`, 'POST', { body })", "/attachments`, 'POST'", "/commits`, 'POST', { sha })", "/diff`)",
        "/sessions`, 'POST'"
    ]) {
        assert.ok(source.includes(route), `expected the client to build ${route}`);
    }
});

test('the assignee is the app-wide LLM picker, not a list of people', () => {
    const source = readFileSync(controllerPath, 'utf8');
    assert.match(source, /import \{ mountLlmPicker, setLlmPickerValue, getEnabledLlmItems \} from '\.\/pickers\/llm-picker\.js'/,
        'consumers import the picker facade, never llm-picker-controller directly');
    assert.match(source, /mountLlmPicker\(this\.app, assigneeSelect, \{\s*context: 'sandbox'/,
        "the card editor mounts the picker in the 'sandbox' context (environments + CLIs, no shell, no Workers)");
    assert.doesNotMatch(source, /state\.members|memberById|assigneeId:|BoardApi\.getBoardMembersAsync/,
        'no member list remains');
    assert.match(source, /assignee: value\('#board-card-assignee'\),/, 'the form reads the picker key, including an empty string to unassign');
    assert.match(source, /getCliBrand\(cli\)/, 'avatars come from the CLI brand');
    assert.match(source, /BoardApi\.attach\(app\)/, 'the controller hands the data layer its app');
    // Every mutation the LLM can make is visible on reopen: the editor always re-reads the card.
    assert.match(source, /card = await BoardApi\.getBoardCardAsync\(cardId, \{ signal: abort\.signal \}\);/);
    assert.doesNotMatch(source, /this\.state\.cards\.find\(c => c\.id === cardId\) \|\| null/);
});

test('Start work launches through the server and adopts the tab like a Python run', () => {
    const source = readFileSync(controllerPath, 'utf8');
    assert.match(source, /data-board-start-work/);
    assert.match(source, /BoardApi\.launchBoardCardAsync\(card\.id, \{ selection: payload\.assignee \}\)/);
    assert.match(source, /rememberTabLaunch\?\.\(tabId, \{[\s\S]*taskKey: CARD_TASK_KEY\(card\.id\)/,
        'the tab is tagged with the board-card:<id> task key');
    assert.match(source, /adoptLaunchedTab\?\.\(tabId\)/);
    assert.match(source, /navigate\?\.\('terminal-focus', \{\s*preferredTabId: tabId/, 'falls back to the Terminals view');
    assert.match(source, /TODO\(board\): "Auto Launch"/, 'the Auto Launch follow-up note stays in the code');
    // Save/Delete remain siblings of the scroller (see board-card-modal.test.mjs); Start work sits with Save.
    assert.match(source, /<div class="board-editor-actions">[\s\S]*data-board-start-work[\s\S]*data-board-save-card/);
});

test('the Reset sample control is gone and the live-session dot has styles', () => {
    const html = readFileSync(indexPath, 'utf8');
    assert.doesNotMatch(html, /data-board-action="reset-data"/);
    assert.match(html, /\.board-live-dot \{/);
    assert.match(html, /\.board-avatar-logo \{/);
    const controller = readFileSync(controllerPath, 'utf8');
    assert.doesNotMatch(controller, /resetBoardData|reset-data/);
    assert.match(controller, /card\.activeTabId[\s\S]*board-live-dot/);
});
