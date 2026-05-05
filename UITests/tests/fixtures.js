// @ts-check
//
// Custom Playwright `test` that loads runtime config (baseURL + storageState) written
// by ../global-setup.js. Tests should `require('./fixtures')` instead of '@playwright/test'
// so navigation and auth Just Work against the spawned backend.
//
// Also exports shared selector helpers so a renamed dashboard data-attribute or tab
// id only has to change here, not in every spec.
const base = require('@playwright/test');
const fs = require('node:fs');
const path = require('node:path');

const RUNTIME_FILE = path.resolve(__dirname, '..', '.playwright-runtime.json');

let cachedRuntime = null;
function readRuntime() {
    if (cachedRuntime) return cachedRuntime;
    if (!fs.existsSync(RUNTIME_FILE)) {
        throw new Error(
            `Playwright runtime file missing at ${RUNTIME_FILE}. ` +
            `Did global-setup.js run? Are you running tests outside playwright?`
        );
    }
    cachedRuntime = JSON.parse(fs.readFileSync(RUNTIME_FILE, 'utf8'));
    return cachedRuntime;
}

const test = base.test.extend({
    context: async ({ browser }, use) => {
        const { storageState, baseURL } = readRuntime();
        const ctx = await browser.newContext({ storageState, baseURL });
        await use(ctx);
        await ctx.close();
    },
});

// Centralised selectors. Keep DOM hooks here so a rename surfaces as one diff.
const selectors = {
    launchWebTerminalButton: (cli) => `[data-action="launch-web-terminal"][data-cli="${cli}"]`,
    tabList: '#vb-terminal-tab-list',
    tabItems: '#vb-terminal-tab-list .vb-terminal-tab-item',
    tabItem: (tabId) => `#vb-terminal-tab-list .vb-terminal-tab-item[data-tab-id="${tabId}"]`,
    tabClose: (tabId) =>
        `#vb-terminal-tab-list .vb-terminal-tab-item[data-tab-id="${tabId}"] .vb-terminal-tab-close`,
    tabPanel: (tabId) => `.vb-terminal-tab-panel[data-tab-id="${tabId}"]`,
    tabAddBtn: '#vb-terminal-tab-add-btn',
    undoWrapper: '#vb-terminal-tab-undo-wrapper',
    undoBtn: '#vb-terminal-tab-undo-btn',
    undoDropdownKill: (tabId) => `.vb-undo-dropdown [data-kill="${tabId}"]`,
};

module.exports = { test, expect: base.expect, selectors };
