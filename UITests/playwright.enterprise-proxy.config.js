const config = require('./playwright.board-nudge.config');

module.exports = { ...config, testMatch: 'settings-enterprise-proxy.spec.js' };
