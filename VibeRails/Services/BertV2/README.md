# Shared local search (VIBE-55)

`~/.vibe_rails/search.db` is the normal per-user search component in every build, including
Debug and stdio MCP. `state.db` remains authoritative for captured inputs, sessions and file
changes; `board.db` remains authoritative for cards. Search queries, ranking, snippets and
result metadata use only the derived search database. `BoardSearchService` is shared by the
dashboard, link candidates, `search_board_cards` and card discovery in `search_history`.

The existing root-only `BertEmbeddingBackfillJob` sweeps every source corpus and then runs bounded
inference rounds, subject to the existing resource-pressure policy. An idle root ticks every five
minutes; while a sweep or an inference backlog is unfinished the job follows up after five seconds
(VB-2GUR8-187: the earlier five-second cadence re-upserted the whole corpus around the clock in
every root). `SearchIndexMaintenanceJob` repairs derived indexes every fifteen minutes. There is no
new scheduler, daemon or OS role. Each round gives Board, input and ended-session work a turn.
Ingestion does not require the model; query embedding and background model construction are lazy
so keyword search survives missing model files or native ONNX dependencies.

`ISearchIndexStore` owns full source text, FTS5, typed result metadata, sqlite-vec chunks,
reconciliation cursors, expiring claims, progress, failures and retry deadlines. Board source
access remains behind `IBoardStore`. Bounded ID pages sweep each source corpus independently;
an epoch marks seen documents and removes deleted source documents when that sweep finishes.
One reconciliation call sweeps each corpus from its cursor to its end with one write transaction
per page, yielding after ten seconds on a very large corpus; the durable cursor resumes after a
restart or an interrupted call. Repeated sweeps detect older/concurrent writers without relying
on timestamps or in-process notifications. Queries never advance these cursors, claim work, write
vectors or consult canonical history metadata.

Full titles, descriptions, visible Comments/retained notes and all retained handoffs are separate
sources. History rows and deleted/hidden discussion are excluded. WordPiece chunks use the
same uncased vocabulary/normalization as inference, a 192-token retrieval target inside the actual 512-token model
window, up to 32 tokens of title context and about 24 tokens of overlap. Sentence boundaries
are preferred; dense code and surrogate pairs retain full character coverage. Full sources also
remain in FTS/literal matching so identifiers and phrases can span semantic chunk boundaries.

Each source stores its owning document, source identity and content hash. Chunks retain source
identity, order, text/hash and model version. Changing text atomically removes its old chunks
before replacement inference. A hash-and-claim check rejects completions for superseded work.
Claims expire after two minutes and renew every 15 seconds during inference. Model work and
tokenization happen outside SQLite transactions. Failure retries use bounded exponential delay;
shutdown and interrupted ingestion can replay unchanged work safely. Native sqlite-vec distance
evaluation and SQL grouping return one best chunk per document; project/exclusion eligibility
is applied before ranking or limits. Board ranking retains exact identity precedence and the
bounded 10% current-project preference.

An unavailable history database or incomplete source schema records a retryable reconciliation
failure and retains the existing search documents. It is never treated as an empty corpus.
Vector repair removes at most 256 missing/orphan entries per pass to bound writer commits.

The database is populated automatically from canonical sources. Legacy Board JSON caches,
standalone vector files and state search tables/columns remain stored but are no longer the
current application's retrieval/work stores. Legacy vectors have no compatible content/model
receipt, so new chunks are rebuilt instead of trusting them. The additive nullable
`UserInputs.SearchComponent` marker lets current captures avoid the old pending queue while
older inserts retain their existing trigger behavior. No old row is backfilled or rewritten;
startup no longer drains the retired state index. Only the derived search component is repaired.

The existing Vibe AI status UI displays document/chunk counts, completed embeddings, pending
sources and failure/retry information. No MCP tool, indexing-status field or readiness message
was added to search responses. Eventual consistency means new content appears after ingestion
and gains semantic coverage after background inference. Follow-up tools still read canonical
cards or explicitly requested captured documents.

Coverage: `SharedSearchIndexTests`, `SearchRetrievalBenchmarkTests`, `BoardSearchTests`,
`BoardRecallTests`, history retrieval tests, schema snapshots and previous-release compatibility
fixtures. All database fixtures are disposable; application debugging uses the normal databases.
