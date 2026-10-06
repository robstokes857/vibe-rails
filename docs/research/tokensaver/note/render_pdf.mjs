// Render tokens-saved-technical-note.html to PDF with Playwright's Chromium.
//
//   node render_pdf.mjs
//
// Uses the playwright-core install under UITests/node_modules (no new dependencies) and
// falls back to the installed Microsoft Edge if the bundled Chromium is unavailable.
// Regenerate the figures first with `python make_charts.py` if daily-series.csv changes.
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..', '..', '..');
const require = createRequire(import.meta.url);
const { chromium } = require(resolve(repoRoot, 'UITests', 'node_modules', 'playwright-core'));

const source = resolve(here, 'tokens-saved-technical-note.html');
const output = resolve(here, '..', 'output', 'pdf', 'tokens-saved-technical-note.pdf');

const footer = `
  <div style="font-family:'Segoe UI',sans-serif;font-size:7.5px;color:#898781;width:100%;
              padding:0 0.85in;display:flex;justify-content:space-between;">
    <span>VibeRails technical note 1.1 · What “tokens saved” means</span>
    <span><span class="pageNumber"></span> / <span class="totalPages"></span></span>
  </div>`;

async function launch() {
  try {
    return await chromium.launch();
  } catch (error) {
    console.warn(`Bundled Chromium unavailable (${error.message.split('\n')[0]}); trying Microsoft Edge.`);
    return await chromium.launch({ channel: 'msedge' });
  }
}

const browser = await launch();
try {
  const page = await browser.newPage();
  await page.goto(pathToFileURL(source).href, { waitUntil: 'load' });
  await page.emulateMedia({ media: 'print' });
  await page.pdf({
    path: output,
    format: 'Letter',
    printBackground: true,
    displayHeaderFooter: true,
    headerTemplate: '<span></span>',
    footerTemplate: footer,
    margin: { top: '0.75in', bottom: '0.8in', left: '0.85in', right: '0.85in' },
  });
  console.log(`Wrote ${output}`);
} finally {
  await browser.close();
}
