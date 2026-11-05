# `wwwroot/assets/board`

Vendored frontend dependencies for the kanban board's attachment viewer
(`js/modules/board-attachments.js`). Read this before upgrading anything here, and before adding
a new library to this directory.

## What is here

| File | What it is | Version |
| --- | --- | --- |
| `pdf.min.js` | PDF.js renderer (ES module build) | 6.3.289 |
| `pdf.worker.min.js` | PDF.js worker, loaded through `GlobalWorkerOptions.workerSrc` | 6.3.289 |
| `LICENSE-pdfjs.txt` | Apache-2.0, as shipped by the project | — |
| `board-attachments.css` | Our own styles for the viewer layer | — |
| `markdown-it.min.js` | Self-contained browser ES module for read-only Markdown | 15.0.3 |
| `LICENSE-markdown-it.txt`, `LICENSE-*.txt` | Parser and bundled dependency notices | — |

Upstream: <https://github.com/mozilla/pdf.js/releases>. Both JS files come from the `pdfjs-dist`
build of the same release and **must be upgraded together** — a worker that does not match its
renderer fails at the API-version check, which the viewer reports as a plain "cannot be previewed"
and nothing louder.

## What is deliberately NOT here

PDF.js also publishes CMaps, standard fonts and Wasm decoders. We ship none of them: 194 files and
about 6.4 MB, against 1.7 MB for the renderer itself. The cost of leaving them out is that CJK
text and PDFs that omit the base-14 fonts fall back to system faces, which `useSystemFonts: true`
already approximates on the desktops this runs on. If a PDF ever renders with missing glyphs, that
trade is the reason — reconsider it deliberately, do not quietly add 6.4 MB.

Wasm stays off (`useWasm: false`) and so does eval (`isEvalSupported: false`) because the
production CSP forbids both. PDF.js's bundled JavaScript decoders keep that boundary intact. XFA
is off (`enableXfa: false`): it is a scripting-capable form layer with no place in a preview.

## Markdown safety and upgrades

Markdown attachments use `board-attachment-markdown.js`, loaded only when opening a Markdown
file. The parser is [markdown-it](https://github.com/markdown-it/markdown-it), MIT, with bundled
entities (BSD-2-Clause), linkify-it, mdurl, punycode.js and uc.micro (MIT). Its browser module is
about 125 KB uncompressed. Source maps, CLI, plugins, syntax highlighters and editor packages are
not shipped. No network, worker, eval or stylesheet injection is required.

Use the upstream [HTML-disabled safety strategy](https://github.com/markdown-it/markdown-it/blob/master/docs/safety.md):
raw HTML is escaped, no plugins can generate additional markup, all images become text, and
links are restricted to absolute HTTP(S) URLs without credentials. Links use a new tab with
`noopener noreferrer`. Relative URLs cannot reach authenticated application routes. This
configuration does not need an HTML sanitizer; **enabling HTML or adding plugins changes that
decision and requires a new security review**. Comments still use the separate escape-first
`board-text.js` renderer. TXT and Markdown Source use `textContent`.

Preview/Source never edits the attachment. Preserve the existing 1 MiB / 200,000-character
preview bounds, truncation notice, original download, cancellation and parent draft/focus.

To update the parser, install into a temporary directory, without install scripts:

```powershell
npm install --prefix <temporary-directory> --ignore-scripts --no-audit --no-fund markdown-it@15.0.3 entities@8.1.0 linkify-it@6.1.0 mdurl@2.1.0 punycode.js@2.3.1 uc.micro@3.0.0
node VibeRails/wwwroot/assets/board/update-markdown.mjs <temporary-directory>/node_modules
```

The updater checks exact versions, copies the official browser build and license notices,
and strips only the development source-map pointer. Keep its output byte-exact via
`.gitattributes`. After upgrading, run the Markdown Node regressions, attachment browser
security tests and real VS Code attachment webview test.

## Before adding another library here

If a new dependency really is warranted: pin an exact version, vendor its licence next to it, add
a row to the table above, and say in this file what you chose not to vendor and why.

## Upgrade checklist

1. Replace `pdf.min.js` and `pdf.worker.min.js` from the same release; refresh `LICENSE-pdfjs.txt`.
2. Update the version column above.
3. Confirm the files are treated as binary-exact — a smudged line ending in a minified bundle is
   a silent corruption (see the fixture `autocrlf` incident in the repo history).
4. Run `UITests/tests/board-ux.spec.js`: it renders a real PDF to canvas and asserts the preview
   carries no active document layer.
