const base = require('./playwright.page-headers.config');
module.exports = { ...base, testMatch: 'pr-reviews.spec.js', use: { ...base.use, channel: 'chrome' } };
