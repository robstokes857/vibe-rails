const base = require('./playwright.board.config');
module.exports = { ...base, testMatch: ['board-ux.spec.js'], grep: /Public card links/,
    use: { ...base.use, channel: 'chrome' } };
