# Shared local search (VIBE-55)

`~/.vibe_rails/search.db` is the normal per-user search component in every build, including
Debug and stdio MCP. `state.db` remains authoritative for captured inputs, sessions and file
changes; `board.db` remains authoritative for cards. Search queries, ranking, snippets and
result metadata use only the derived search database. `BoardSearchService` is shared by the
dashboard, link candidates, `search_board_cards` and card discovery in `search_history`.

The existing root-only `BertEmbeddingBackfillJob` sweeps every source corpus and then runs bounded
inference rounds, subject to the existing resource-pressure policy. An idle root ticks every five
minutes; each corpus's durable completion time suppresses duplicate sweeps across roots for five
minutes. While a sweep or an inference backlog is unfinished the job follows up after five seconds
(VB-2GUR8-187: the earlier five-second cadence re-upserted the whole corpus around the clock in
every root). `SearchIndexMaintenanceJob` repairs derived indexes every fifteen minutes. There is no
new scheduler, daemon or OS role. Each round gives Board, input and ended-session work a turn.
Ingestion does not require the model; query embedding and background model construction are lazy
so keyword search survives missing model files or native ONNX dependencies. The singleton owner
releases the native model after two minutes without inference, including in stdio MCP processes;
the next semantic query loads it again. Inference and disposal share a lock.

SQLite's vector extension is loaded by absolute path beside the executable in published
installs (`vec0.dll`, `vec0.dylib` or `vec0.so`). Development builds also support
`runtimes/<process-rid>/native/`; resolution uses the process architecture, including an x64
process running on an ARM Mac. The system loader is a final fallback when neither bundled
layout contains the library. Linux/macOS release verification runs the NativeAOT
`tools/sqlite-vec-smoke` harness against the extracted archive's libraries from a different
working directory, creating and reopening only disposable search storage.

`ISearchIndexStore` owns full source text, FTS5, typed result metadata, sqlite-vec chunks,
reconciliation cursors, expiring claims, progress, failures and retry deadlines. Board source
access remains behind `IBoardStore`. Bounded ID pages sweep each source corpus independently;
`SearchDocumentPresence` marks seen documents in narrow rows and removes deleted source documents
when that sweep finishes. Unchanged document metadata, including session chunk counts, is not rewritten.
Generated Board launch envelopes and Automation `vb --job-run` commands are excluded by
`InputEtlFilter` from both individual input documents and session aggregates. Canonical captured
inputs remain untouched; an ordinary successful sweep removes obsolete derived entries.
VIBE-257 closed the gaps VIBE-254 left: Automation launch lines whose arguments are base64
PowerShell literals (VIBE-153), so `--job-run` is visible only after decoding; script runs
(`--run-python-script`); Automation prompts from older builds that start with the card header;
and card discussion bootstraps, which keep only the question the user typed into them. On a copy
of a real index, one sweep by a filtering build retired 669 of 2,858 input documents (2,083 of
4,511 input chunks) and 583 of 1,278 session aggregates, and re-chunked 131 mixed sessions.
`SearchRetrievalBenchmarkTests.PersonalSnapshotSweepRetiresGeneratedLaunchInputs` repeats that
check against any snapshot; it never opens the live `search.db`.

One reconciliation call sweeps each due corpus from its cursor to its end with one write transaction
per page, yielding after ten seconds on a very large corpus; the durable cursor resumes after a
restart or an interrupted call, regardless of the completion cooldown. `SearchSweepProgress` holds
the presence cursor and a unique sweep token, tied to the shared `SearchCheckpoints.Epoch` and lease.
The legacy checkpoint cursor stays empty during a presence sweep, so an older process taking over
starts from the beginning and safely applies its own `SeenEpoch` deletion rule. An older process's
page or completion changes the shared cursor or epoch; a newer process then discards its private
progress and starts a new presence sweep. The additive `shared-search/2` migration creates only
these two tables in `search.db`, without backfilling or changing canonical storage. Repeated sweeps
detect older/concurrent writers without relying on timestamps or in-process notifications. Queries
never advance these cursors, claim work, write vectors or consult canonical history metadata.

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
Expected orphan-vector pruning after text replacement is maintenance, not a corruption warning.

All release platforms use the checked-in `model.onnx.gz`; the duplicate ZIP is retired.
Installers compare the installed model/vocabulary to the packaged assets and replace changed
bytes. The Windows setup embeds SHA-256 comparisons; script installers stage model extraction
before replacement. Successful installation removes only the known gzip/ZIP/Brotli model
archives. The pinned manual download scripts verify both compressed and extracted model hashes.

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

## Re-ranking (VIBE-257)

`search_board_cards`, `search_history` card discovery, and the fused group of `search_history` and
`/api/v1/search` can rescore their top 30 hybrid candidates with a local cross-encoder
(`ISearchReranker`). The cross-encoder reads the query and each candidate's matched text together.
`SearchReranking.Apply` reorders only that pool, keeps exact card identities first, and preserves
the bounded 10% current-repository preference by scaling the relevance probability. Any failure
keeps the hybrid order. Search-as-you-type and link pickers do not re-rank.
The model lives in `models/reranker/` (`model.onnx` and `vocab.txt`; `CrossEncoderReranker` also reads
XLM-R and DeBERTa-v3 SentencePiece files for comparisons). `IdleSearchReranker` loads it on first
use and releases it after two idle minutes, like the embedder. While the directory is empty,
`IsAvailable` is false and search runs exactly as before, with the same pool sizes. The measured
choice is ms-marco TinyBERT-L2-v2: MRR@10 0.62 → 0.86 on a fixed personal query set, about +80 ms
on a 900 ms search, 17.6 MB. Larger re-rankers, including bge-reranker-base, were slower and
no better. Packaging the model into installers is a separate step. The opt-in benchmark is
`SearchRetrievalBenchmarkTests.PersonalSnapshotRetrievalQualityBySetup`. Its query sets with
known answers are personal data and live outside this repository.
