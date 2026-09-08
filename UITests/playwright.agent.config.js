// The rule UI fixture owns its API responses, so this needs no database or backend process.
const qualityConfig = require('./playwright.quality.config');

module.exports = { ...qualityConfig, testMatch: 'agent-edit.spec.js' };
