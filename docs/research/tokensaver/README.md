# TokenSaver compression savings research

Research draft 0.2, October 5, 2026, prepared for Robert Stokes. This folder holds the paper and evidence package for independent review. It covers measured request-body compression, conditional mathematical guarantees, actual usage accounting, and the experiment still needed to establish net task savings with preserved quality.

Board review card: **VIBE-63 — Review TokenSaver compression savings research paper**
(permanent key `VB-NY2MM-135`) on the VibeRails Board. The card links to these repository
files and retains Markdown snapshots of the manuscript and review brief.

## Read or edit the paper

- [Editable Word paper](output/docx/tokensaver-research-editable.docx) — editable prose, tables and six native equations; Track Changes is enabled for future edits.
- [Markdown manuscript](tokensaver-research-review.md) — preferred for agent edits and diffs.
- [PDF reading copy](output/pdf/tokensaver-research-draft.pdf) — 16 pages and nine charts.
- [LaTeX source](tokensaver-research-draft.tex) — standalone mathematical source.
- [Complete agent review package](tokensaver-agent-review-package.zip) — portable snapshot with the manuscript, review instructions, figures and aggregate evidence.

## Review and return findings

Start with [REVIEW.md](REVIEW.md). Use the stable claim identifiers TS-C01 through TS-C10, cite evidence for every finding, and return an edited manuscript or unified diff together with `review-notes-<reviewer>.md`. Keep separate reviewer copies, then reconcile accepted changes into the next manuscript version. The scientific claims are the same across formats; the Word and Markdown reproduction tables also identify the new review files.

The Word and Markdown copies do not synchronize automatically. Do not rerun the builders over edited copies: they regenerate exports from their source blocks and can overwrite revisions. The ZIP and Board attachments are versioned review snapshots; update them deliberately after reconciliation.

## Evidence and provenance

- [Evidence aggregates](evidence.json) and [daily series](daily-series.csv)
- [Source manifest](source-manifest.json) and [research discovery record](research-discovery.txt)
- [Existing product verification record](validation.json)
- [Editable document checks](editable-validation.json) and [review baseline hashes](review-baseline.json)
- [Figures](figures/) in PNG, SVG and PDF

Underlying research remains in the sibling `vibe-books` repository. This package contains aggregates, not raw captured request bodies. Some cited private source files are not included; reviewers should state unavailable sources. The paper demonstrates measured transport reduction and conditional guarantees. Net billed-token savings and preserved task quality remain open empirical questions.

The packaged builders are preserved for provenance. They assume the original local research and product checkouts and optional authoring dependencies; opening or reviewing the documents requires none of those tools. The PDF was checked separately, and the Word document was rendered and inspected in Microsoft Word. Native LaTeX compilation was unavailable in the authoring environment.

## Proposed web placement

The proposed public home is `/research/tokensaver`, with an HTML article and downloads linked beneath the homepage Tokens Saved counter. In the local app, add “How savings are measured” to the savings popover and “Research and methodology” beside Settings → LLMs → Token Saver. These are proposals for later implementation after review. The public aggregate meter and this workstation study have different populations and must be labeled accordingly.
