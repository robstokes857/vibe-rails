# `wwwroot/assets/board`

Vendored frontend dependencies for the kanban board's attachment viewer
(`js/modules/board-attachments.js`). Read this before upgrading anything here, and before adding
a new library to this directory.

## What is here

| File | What it is | Version |
| --- | --- | --- |
| `pdf.min.js` | PDF.js renderer (ES module build) | 5.4.149 |
| `pdf.worker.min.js` | PDF.js worker, loaded through `GlobalWorkerOptions.workerSrc` | 5.4.149 |
| `LICENSE-pdfjs.txt` | Apache-2.0, as shipped by the project | — |
| `board-attachments.css` | Our own styles for the viewer layer | — |

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

## Before adding another library here

The board renders **no Markdown and no HTML** from attachments. TXT and Markdown both reach the
DOM through `textContent`, which is why this directory ships no Markdown parser and no HTML
sanitizer — there is nothing to sanitize, because nothing is ever parsed. Rendering Markdown would
cost a parser *plus* a sanitizer to re-earn exactly the safety `textContent` gives for free, and it
would put two more dependencies on the attachment path, which handles untrusted files by
definition. `board-text.js` documents the same rule for comment and description bodies.

If a new dependency really is warranted: pin an exact version, vendor its licence next to it, add
a row to the table above, and say in this file what you chose not to vendor and why.

## Upgrade checklist

1. Replace `pdf.min.js` and `pdf.worker.min.js` from the same release; refresh `LICENSE-pdfjs.txt`.
2. Update the version column above.
3. Confirm the files are treated as binary-exact — a smudged line ending in a minified bundle is
   a silent corruption (see the fixture `autocrlf` incident in the repo history).
4. Run `UITests/tests/board-ux.spec.js`: it renders a real PDF to canvas and asserts the preview
   carries no active document layer.
