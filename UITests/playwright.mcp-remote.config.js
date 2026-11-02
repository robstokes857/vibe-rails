// Static server and settings shared with the page-header suite; no backend.
const base = require('./playwright.page-headers.config');

module.exports = { ...base, testMatch: 'mcp-remote-frame.spec.js' };
