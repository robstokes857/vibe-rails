const config = require('./playwright.board-nudge.config');

module.exports = { ...config, testMatch: 'settings-console-host.spec.js' };
