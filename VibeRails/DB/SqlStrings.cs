namespace VibeRails.DB
{
    public static class SqlStrings
    {
        // Pragmas
        public const string PragmaWal = "PRAGMA journal_mode=WAL;";
        public const string PragmaForeignKeys = "PRAGMA foreign_keys=ON;";

        // Environments Table (global, not project-scoped)
        public const string CreateEnvironmentsTable = """
            CREATE TABLE IF NOT EXISTS Environments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CustomName TEXT NOT NULL,
                LLM INTEGER NOT NULL,
                Path TEXT NOT NULL DEFAULT '',
                CustomArgs TEXT NOT NULL DEFAULT '',
                CustomPrompt TEXT NOT NULL DEFAULT '',
                CreatedUTC TEXT NOT NULL,
                LastUsedUTC TEXT NOT NULL,
                UNIQUE(CustomName, LLM)
            )
            """;
        public const string CreateEnvironmentsIndex = "CREATE INDEX IF NOT EXISTS idx_environments_name_llm ON Environments(CustomName, LLM)";
        // Durable backstop for the create-time collision check: the env name maps to a
        // case-insensitive directory (envs/{name}/{llm}), so "Work" and "work" for the same
        // LLM would share a credential directory. Upgrades the case-sensitive UNIQUE(CustomName,
        // LLM) table constraint to be case-insensitive. Lives in MigrationStatements (not
        // InitStatements) so a legacy DB already holding case-variant duplicates logs-and-skips
        // instead of failing startup.
        public const string CreateEnvironmentsNameNoCaseUniqueIndex = "CREATE UNIQUE INDEX IF NOT EXISTS idx_environments_name_nocase_llm ON Environments(CustomName COLLATE NOCASE, LLM)";

        // AgentMetadata Table
        public const string CreateAgentMetadataTable = """
            CREATE TABLE IF NOT EXISTS AgentMetadata (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL UNIQUE,
                CustomName TEXT NOT NULL
            )
            """;
        public const string CreateAgentMetadataPathIndex = "CREATE INDEX IF NOT EXISTS idx_agent_metadata_path ON AgentMetadata(Path)";

        // ChatSummary Table
        public const string CreateChatSummaryTable = """
            CREATE TABLE IF NOT EXISTS ChatSummary (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL UNIQUE,
                SummaryText TEXT NOT NULL DEFAULT '',
                Date TEXT NOT NULL
            )
            """;

        // Sandboxes Table (project-scoped via ProjectPath)
        public const string CreateSandboxesTable = """
            CREATE TABLE IF NOT EXISTS Sandboxes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Path TEXT NOT NULL,
                ProjectPath TEXT NOT NULL,
                Branch TEXT NOT NULL DEFAULT '',
                CommitHash TEXT,
                RemoteUrl TEXT,
                SourceBranch TEXT,
                CreatedUTC TEXT NOT NULL,
                UNIQUE(Name, ProjectPath)
            )
            """;
        public const string CreateSandboxesIndex = "CREATE INDEX IF NOT EXISTS idx_sandboxes_project ON Sandboxes(ProjectPath)";

        // Sessions Table
        public const string CreateSessionsTable = """
            CREATE TABLE IF NOT EXISTS Sessions (
                Id TEXT PRIMARY KEY,
                Cli TEXT NOT NULL,
                EnvironmentName TEXT,
                WorkingDirectory TEXT NOT NULL,
                ProjectDisplayName TEXT NOT NULL DEFAULT '',
                StartedUTC TEXT NOT NULL,
                EndedUTC TEXT,
                ExitCode INTEGER,
                Processed INTEGER NOT NULL DEFAULT 0,
                ParentSessionId TEXT DEFAULT '',
                SessionDisplayName TEXT DEFAULT '',
                OwnerPid INTEGER,
                OwnershipTracked INTEGER NOT NULL DEFAULT 1,
                JobRunId TEXT
            )
            """;
        public const string CreateSessionsIndex = "CREATE INDEX IF NOT EXISTS idx_sessions_started ON Sessions(StartedUTC DESC)";

        // Session-level BERT aggregate embedding tracking — drives SessionAggregateEmbeddingBackfillJob's
        // ended-but-unembedded scan. Defined in MigrationStatements since it references the
        // AggregateEmbeddedUTC column added by ALTER TABLE.
        public const string MigrateSessionsAddAggregateEmbeddedUTC = "ALTER TABLE Sessions ADD COLUMN AggregateEmbeddedUTC TEXT";
        public const string CreateSessionsUnaggregatedIndex = "CREATE INDEX IF NOT EXISTS idx_sessions_unaggregated ON Sessions(EndedUTC) WHERE AggregateEmbeddedUTC IS NULL AND EndedUTC IS NOT NULL";

        // SessionLogs Table
        public const string CreateSessionLogsTable = """
            CREATE TABLE IF NOT EXISTS SessionLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                Content BLOB NOT NULL,
                IsError INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
            )
            """;
        public const string CreateSessionLogsIndex = "CREATE INDEX IF NOT EXISTS idx_session_logs_session ON SessionLogs(SessionId)";

        // sessionOutPut Table
        public const string CreateSessionOutputTable = """
            CREATE TABLE IF NOT EXISTS sessionOutPut (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                Text TEXT NOT NULL,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
            )
            """;
        public const string CreateSessionOutputIndex = "CREATE UNIQUE INDEX IF NOT EXISTS idx_session_output_session ON sessionOutPut(SessionId)";

        // UserInputs Table
        public const string CreateUserInputsTable = """
            CREATE TABLE IF NOT EXISTS UserInputs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                Sequence INTEGER NOT NULL,
                InputText TEXT NOT NULL,
                GitCommitHash TEXT,
                TimestampUTC TEXT NOT NULL,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
            )
            """;
        public const string CreateUserInputsIndex = "CREATE INDEX IF NOT EXISTS idx_user_inputs_session ON UserInputs(SessionId)";
        public const string CreateUserInputsSeqIndex = "CREATE INDEX IF NOT EXISTS idx_user_inputs_session_seq ON UserInputs(SessionId, Sequence)";

        // FTS5 lexical index over UserInputs.InputText. External-content table — the
        // virtual table is just the inverted index; raw text stays in UserInputs.
        // bundle_e_sqlite3 ships with FTS5 enabled.
        //
        // FTS writes are driven from C# (Repository.InsertUserInputAsync /
        // BackfillUserInputsFtsAsync) — NOT from triggers — so that
        // InputEtlFilter.Process can strip secrets before anything lands in
        // UserInputs_fts. The legacy auto-insert/auto-update triggers from earlier
        // installs are dropped below; only the delete trigger remains so deletions
        // continue to keep the index in sync (calling 'delete' against a rowid that
        // was never indexed is a harmless no-op).
        public const string CreateUserInputsFts = """
            CREATE VIRTUAL TABLE IF NOT EXISTS UserInputs_fts USING fts5(
                InputText,
                content='UserInputs',
                content_rowid='Id',
                tokenize='porter unicode61'
            );
            """;
        public const string CreateUserInputsFtsAfterDeleteTrigger = """
            CREATE TRIGGER IF NOT EXISTS UserInputs_fts_ad AFTER DELETE ON UserInputs BEGIN
                INSERT INTO UserInputs_fts(UserInputs_fts, rowid, InputText) VALUES('delete', old.Id, old.InputText);
            END;
            """;

        // Drop legacy auto-insert / auto-update triggers from earlier installs that
        // copied raw InputText into the FTS index before secret filtering existed.
        public const string DropUserInputsFtsAfterInsertTrigger = "DROP TRIGGER IF EXISTS UserInputs_fts_ai;";
        public const string DropUserInputsFtsAfterUpdateTrigger = "DROP TRIGGER IF EXISTS UserInputs_fts_au;";

        // Code-driven FTS write: only called after InputEtlFilter clears the text.
        public const string InsertUserInputsFtsRow = """
            INSERT INTO UserInputs_fts(rowid, InputText) VALUES ($rowid, $inputText);
            """;
        // Backfill scan: rowids present in UserInputs but absent from the FTS shadow
        // table. External-content FTS5 stores rowids in a sibling docsize table
        // (UserInputs_fts_docsize), which IS authoritative for "is this row indexed?"
        // even though the virtual table itself proxies UserInputs.
        public const string SelectUserInputsMissingFromFts = """
            SELECT Id, InputText
            FROM UserInputs
            WHERE Id NOT IN (SELECT id FROM UserInputs_fts_docsize);
            """;

        // BERT embedding tracking — partial index drives the BertEmbeddingBackfillJob's
        // unembedded-row scan. Defined in MigrationStatements since it references the
        // BertEmbeddedUTC column added by ALTER TABLE.
        public const string MigrateUserInputsAddBertEmbeddedUTC = "ALTER TABLE UserInputs ADD COLUMN BertEmbeddedUTC TEXT";
        public const string CreateUserInputsUnembeddedIndex = "CREATE INDEX IF NOT EXISTS idx_user_inputs_unembedded ON UserInputs(Id) WHERE BertEmbeddedUTC IS NULL";
        // Failure count column lets the backfill skip poison-pill rows (e.g. a row whose
        // text the embedder OOMs on) so a single bad row doesn't block every newer row
        // behind it forever.
        public const string MigrateUserInputsAddBertEmbedFailureCount = "ALTER TABLE UserInputs ADD COLUMN BertEmbedFailureCount INTEGER NOT NULL DEFAULT 0";
        public const string MigrateSessionsAddAggregateEmbedFailureCount = "ALTER TABLE Sessions ADD COLUMN AggregateEmbedFailureCount INTEGER NOT NULL DEFAULT 0";

        // InputFileChanges Table
        public const string CreateInputFileChangesTable = """
            CREATE TABLE IF NOT EXISTS InputFileChanges (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserInputId INTEGER NOT NULL,
                PreviousInputId INTEGER,
                FilePath TEXT NOT NULL,
                ChangeType TEXT NOT NULL,
                LinesAdded INTEGER,
                LinesDeleted INTEGER,
                DiffContent TEXT,
                FOREIGN KEY (UserInputId) REFERENCES UserInputs(Id),
                FOREIGN KEY (PreviousInputId) REFERENCES UserInputs(Id)
            )
            """;
        public const string CreateInputFileChangesInputIndex = "CREATE INDEX IF NOT EXISTS idx_input_file_changes_input ON InputFileChanges(UserInputId)";
        public const string CreateInputFileChangesPathIndex = "CREATE INDEX IF NOT EXISTS idx_input_file_changes_filepath ON InputFileChanges(FilePath)";

        // TerminalSessionLogs Table — enriched per-chunk replay data (cols, rows, alternate screen)
        public const string CreateTerminalSessionLogsTable = """
            CREATE TABLE IF NOT EXISTS TerminalSessionLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                Sequence INTEGER NOT NULL,
                IsAlternateScreen INTEGER NOT NULL DEFAULT 0,
                Data BLOB NOT NULL,
                Cols INTEGER NOT NULL DEFAULT 80,
                Rows INTEGER NOT NULL DEFAULT 24,
                Timestamp TEXT NOT NULL,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id)
            )
            """;
        public const string CreateTerminalSessionLogsIndex = "CREATE INDEX IF NOT EXISTS idx_terminal_session_logs_session ON TerminalSessionLogs(SessionId, Sequence)";

        public const string InsertTerminalSessionLog = """
            INSERT INTO TerminalSessionLogs (SessionId, Sequence, IsAlternateScreen, Data, Cols, Rows, Timestamp)
            VALUES ($sessionId, $sequence, $isAlternateScreen, $data, $cols, $rows, $timestamp);
            """;

        public const string SelectTerminalSessionLogsBySession = """
            SELECT Id, SessionId, Sequence, IsAlternateScreen, Data, Cols, Rows, Timestamp
            FROM TerminalSessionLogs
            WHERE SessionId = $sessionId
            ORDER BY Sequence ASC;
            """;

        public const string DeleteSession_TerminalSessionLogs = """
            DELETE FROM TerminalSessionLogs
            WHERE SessionId = $sessionId;
            """;

        /// <summary>
        /// LLM-proxy token-saver tally: one row per UTC day per provider, upsert-incremented per
        /// request (see <see cref="UpsertTokenSavings"/>). Byte counts are the measured wire truth;
        /// "tokens saved" is derived at display time so the heuristic never freezes into history.
        /// Also created on demand by <c>TokenSavingsStore</c> (IF NOT EXISTS), since its writer can
        /// run before the first <c>Repository</c> initializes the schema.
        /// </summary>
        public const string CreateTokenSavingsTable = """
            CREATE TABLE IF NOT EXISTS TokenSavings (
                Day               TEXT    NOT NULL,
                Provider          TEXT    NOT NULL,
                Requests          INTEGER NOT NULL DEFAULT 0,
                RewrittenRequests INTEGER NOT NULL DEFAULT 0,
                BytesBefore       INTEGER NOT NULL DEFAULT 0,
                BytesAfter        INTEGER NOT NULL DEFAULT 0,
                UpdatedUTC        TEXT    NOT NULL,
                PRIMARY KEY (Day, Provider)
            )
            """;

        public const string UpsertTokenSavings = """
            INSERT INTO TokenSavings (Day, Provider, Requests, RewrittenRequests, BytesBefore, BytesAfter, UpdatedUTC)
            VALUES ($day, $provider, 1, $rewritten, $bytesBefore, $bytesAfter, $updatedUTC)
            ON CONFLICT(Day, Provider) DO UPDATE SET
                Requests          = Requests + 1,
                RewrittenRequests = RewrittenRequests + excluded.RewrittenRequests,
                BytesBefore       = BytesBefore + excluded.BytesBefore,
                BytesAfter        = BytesAfter + excluded.BytesAfter,
                UpdatedUTC        = excluded.UpdatedUTC
            """;

        public const string SelectTokenSavingsTotals = """
            SELECT COALESCE(SUM(BytesBefore), 0), COALESCE(SUM(BytesAfter), 0)
            FROM TokenSavings
            """;

        // $month is a "yyyy-MM" prefix of the Day column ("yyyy-MM-dd").
        public const string SelectTokenSavingsMonthTotals = """
            SELECT COALESCE(SUM(BytesBefore), 0), COALESCE(SUM(BytesAfter), 0)
            FROM TokenSavings
            WHERE substr(Day, 1, 7) = $month
            """;

        /// <summary>
        /// Raw before/after compression captures: one row per tool_result the pipeline considered
        /// rewriting (see <c>TokenSaver.ICompressionCaptureSink</c>). RawText/CompressedText are
        /// verbatim tool output from the user's machine, so this table is exactly as sensitive as
        /// SessionLogs and inherits the same handling rules.
        ///
        /// UNCAPPED by explicit product decision (2026-07-15) — no retention, no row limit, no
        /// truncation. The whole value of a capture is that it is the unedited input, and any cap
        /// would preferentially destroy the pathological rows that are the reason to look.
        /// <see cref="DeleteAllCompressionCaptures"/> is the only eviction.
        ///
        /// EnabledIds and Trace are JSON arrays of catalog stage ids, which
        /// <c>CompressionCatalog</c> documents as a wire format — renaming a stage id silently
        /// re-reads every historical row as "stage disabled".
        ///
        /// Also created on demand by <c>CompressionCaptureStore</c> (IF NOT EXISTS), since its
        /// writer can run before the first <c>Repository</c> initializes the schema.
        /// </summary>
        public const string CreateCompressionCapturesTable = """
            CREATE TABLE IF NOT EXISTS CompressionCaptures (
                Id             TEXT    PRIMARY KEY,
                CreatedUTC     TEXT    NOT NULL,
                Provider       TEXT    NOT NULL,
                ToolName       TEXT    NOT NULL,
                Command        TEXT,
                RawText        TEXT    NOT NULL,
                CompressedText TEXT    NOT NULL,
                CharsBefore    INTEGER NOT NULL,
                CharsAfter     INTEGER NOT NULL,
                Changed        INTEGER NOT NULL,
                RewriteAccepted INTEGER NOT NULL DEFAULT 0,
                EnabledIds     TEXT    NOT NULL,
                Trace          TEXT    NOT NULL,
                ContentHash    TEXT    NOT NULL DEFAULT '',
                SeenCount      INTEGER NOT NULL DEFAULT 1
            )
            """;
        public const string CreateCompressionCapturesCreatedIndex = "CREATE INDEX IF NOT EXISTS idx_compression_captures_created ON CompressionCaptures(CreatedUTC DESC)";

        /// <summary>
        /// The dedupe key. The Messages API is stateless, so the CLI re-sends its whole history every
        /// turn and the proxy re-compresses — and would re-capture — every tool_result in it. Row
        /// count would be triangular in turn count, ~96% of it byte-identical duplicates. This index
        /// makes <see cref="UpsertCompressionCapture"/> collapse them to one row plus a counter.
        ///
        /// PARTIAL, on purpose: rows written before ContentHash existed carry the '' default, and a
        /// plain unique index would see them as duplicates of each other and fail to build — which
        /// the migration loop swallows as a warning, silently leaving dedupe off. Excluding '' means
        /// legacy rows keep their place and every new row is deduped.
        /// </summary>
        public const string CreateCompressionCapturesHashIndex =
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_compression_captures_hash ON CompressionCaptures(ContentHash) WHERE ContentHash != ''";

        public const string AddCompressionCaptureContentHashColumn =
            "ALTER TABLE CompressionCaptures ADD COLUMN ContentHash TEXT NOT NULL DEFAULT ''";

        public const string AddCompressionCaptureSeenCountColumn =
            "ALTER TABLE CompressionCaptures ADD COLUMN SeenCount INTEGER NOT NULL DEFAULT 1";

        public const string AddCompressionCaptureRewriteAcceptedColumn =
            "ALTER TABLE CompressionCaptures ADD COLUMN RewriteAccepted INTEGER NOT NULL DEFAULT 0";

        /// <summary>
        /// Insert-or-count. On a ContentHash collision the payload is NOT rewritten — the original
        /// row's Id stays stable (it is the handle the compress runbook cites) and CreatedUTC keeps
        /// pointing at first sight. Only SeenCount moves.
        ///
        /// SeenCount is the measurement that pays for this table: a 20KB tool_result re-sent 30 times
        /// costs 600KB of context, and until now nothing counted that. It is what tells you which
        /// outputs are actually expensive, rather than which are merely large.
        /// </summary>
        public const string UpsertCompressionCapture = """
            INSERT INTO CompressionCaptures
                (Id, CreatedUTC, Provider, ToolName, Command, RawText, CompressedText,
                 CharsBefore, CharsAfter, Changed, RewriteAccepted, EnabledIds, Trace,
                 ContentHash, SeenCount)
            VALUES
                ($id, $createdUTC, $provider, $toolName, $command, $rawText, $compressedText,
                 $charsBefore, $charsAfter, $changed, $rewriteAccepted, $enabledIds, $trace,
                 $contentHash, 1)
            ON CONFLICT(ContentHash) WHERE ContentHash != ''
            DO UPDATE SET SeenCount = SeenCount + 1;
            """;

        /// <summary>Flushes a batch of in-memory re-sight counts (see CompressionCaptureStore). No-op
        /// for a hash we have never written, which is the correct outcome for a count whose insert
        /// was dropped.</summary>
        public const string TouchCompressionCapture =
            "UPDATE CompressionCaptures SET SeenCount = SeenCount + $delta WHERE ContentHash = $contentHash;";

        // The list view's projection. RawText/CompressedText are deliberately absent: this table is
        // uncapped and a row can hold megabytes, so the two big strings only ever leave via
        // SelectCompressionCaptureById.
        //
        // rowid breaks CreatedUTC ties. DateTime.UtcNow's resolution on Windows (~15ms) is coarser
        // than the write path is fast, so same-timestamp rows are routine rather than exotic, and
        // without a tiebreak they would shuffle between pages of the same scan.
        public const string SelectCompressionCaptureSummaries = """
            SELECT Id, CreatedUTC, Provider, ToolName, Command, CharsBefore, CharsAfter, Changed,
                   RewriteAccepted
            FROM CompressionCaptures
            ORDER BY CreatedUTC DESC, rowid DESC
            LIMIT $take OFFSET $skip;
            """;

        public const string SelectCompressionCaptureById = """
            SELECT Id, CreatedUTC, Provider, ToolName, Command, RawText, CompressedText,
                   CharsBefore, CharsAfter, Changed, RewriteAccepted, EnabledIds, Trace
            FROM CompressionCaptures
            WHERE Id = $id
            LIMIT 1;
            """;

        public const string DeleteAllCompressionCaptures = "DELETE FROM CompressionCaptures;";

        // Path collation follows host filesystem case semantics: case-insensitive on Windows/macOS,
        // case-sensitive (BINARY) on Linux. Collation is fixed at DDL time, so it is chosen once per
        // machine when the table is first created (which always matches that machine's OS).
        private static readonly string CodeAnalyzerIgnorePathCollation =
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "COLLATE NOCASE" : "COLLATE BINARY";

        /// <summary>
        /// Files the user excluded from Code quality scans, keyed per repository. ReasonKind is
        /// one of "test" / "config" / "other" (or NULL when no reason was given); ReasonText is
        /// the optional free-text note. RepositoryPath stays NOCASE (a machine-local absolute path);
        /// Path collation follows the host filesystem (<c>CodeAnalyzerIgnorePathCollation</c>) so a
        /// case-sensitive Linux checkout can distinguish Foo.cs from foo.cs. Also created on demand
        /// by <c>CodeAnalyzerIgnoreStore</c> (IF NOT EXISTS), since its writer can run before the
        /// first <c>Repository</c> initializes the schema.
        /// </summary>
        public static readonly string CreateCodeAnalyzerIgnoresTable = $"""
            CREATE TABLE IF NOT EXISTS CodeAnalyzerIgnores (
                RepositoryPath TEXT NOT NULL COLLATE NOCASE,
                Path           TEXT NOT NULL {CodeAnalyzerIgnorePathCollation},
                MatchKind      TEXT NOT NULL DEFAULT 'file',
                ReasonKind     TEXT,
                ReasonText     TEXT,
                CreatedUTC     TEXT NOT NULL,
                PRIMARY KEY (RepositoryPath, Path)
            )
            """;

        /// <summary>
        /// Adds the MatchKind column to pre-existing CodeAnalyzerIgnores tables. Safe to
        /// re-run: the benign "duplicate column" error is swallowed by Repository's
        /// migration runner, and CodeAnalyzerIgnoreStore.OpenAsync creates the table
        /// fresh (with the column) when it runs first.
        /// </summary>
        public const string MigrateCodeAnalyzerIgnoresAddMatchKind =
            "ALTER TABLE CodeAnalyzerIgnores ADD COLUMN MatchKind TEXT NOT NULL DEFAULT 'file'";

        public const string UpsertCodeAnalyzerIgnore = """
            INSERT INTO CodeAnalyzerIgnores (RepositoryPath, Path, MatchKind, ReasonKind, ReasonText, CreatedUTC)
            VALUES ($repositoryPath, $path, $matchKind, $reasonKind, $reasonText, $createdUTC)
            ON CONFLICT(RepositoryPath, Path) DO UPDATE SET
                MatchKind = excluded.MatchKind,
                ReasonKind = excluded.ReasonKind,
                ReasonText = excluded.ReasonText
            """;

        public const string SelectCodeAnalyzerIgnores = """
            SELECT Path, MatchKind, ReasonKind, ReasonText, CreatedUTC
            FROM CodeAnalyzerIgnores
            WHERE RepositoryPath = $repositoryPath
            ORDER BY Path
            """;

        public const string DeleteCodeAnalyzerIgnore = """
            DELETE FROM CodeAnalyzerIgnores
            WHERE RepositoryPath = $repositoryPath AND Path = $path
            """;

        public static readonly string[] InitStatements =
        [
            CreateCodeAnalyzerIgnoresTable,
            CreateEnvironmentsTable,
            CreateEnvironmentsIndex,
            CreateAgentMetadataTable,
            CreateAgentMetadataPathIndex,
            CreateSandboxesTable,
            CreateSandboxesIndex,
            CreateChatSummaryTable,
            CreateSessionsTable,
            CreateSessionsIndex,
            CreateSessionLogsTable,
            CreateSessionLogsIndex,
            CreateSessionOutputTable,
            CreateSessionOutputIndex,
            CreateUserInputsTable,
            CreateUserInputsIndex,
            CreateUserInputsSeqIndex,
            CreateInputFileChangesTable,
            CreateInputFileChangesInputIndex,
            CreateInputFileChangesPathIndex,
            CreateTerminalSessionLogsTable,
            CreateTerminalSessionLogsIndex,
            CreateProjectCacheTable,
            CreateGlobalCacheTable,
            CreateTokenSavingsTable,
            CreateCompressionCapturesTable,
            CreateCompressionCapturesCreatedIndex
        ];

        public const string AddProcessedColumn = "ALTER TABLE Sessions ADD COLUMN Processed INTEGER NOT NULL DEFAULT 0";

        public static readonly string[] MigrationStatements =
        [
            "ALTER TABLE Sandboxes ADD COLUMN RemoteUrl TEXT;",
            "ALTER TABLE Sandboxes ADD COLUMN SourceBranch TEXT;",
            "ALTER TABLE ChatSummary DROP COLUMN SummaryBy;",
            AddProcessedColumn,
            "ALTER TABLE Sessions ADD COLUMN ParentSessionId TEXT DEFAULT ''",
            "ALTER TABLE Sessions ADD COLUMN SessionDisplayName TEXT DEFAULT ''",
            "ALTER TABLE Sessions ADD COLUMN ProjectDisplayName TEXT NOT NULL DEFAULT ''",
            "ALTER TABLE Sessions ADD COLUMN OwnerPid INTEGER",
            "ALTER TABLE Sessions ADD COLUMN OwnershipTracked INTEGER",
            // Automated Jobs record a real terminal session tagged with the JobRun id. NULL = an
            // ordinary human session; NOT NULL hides it from Chat History and links it to its run.
            "ALTER TABLE Sessions ADD COLUMN JobRunId TEXT",
            // Drop orphaned table from a previous build.
            "DROP TABLE IF EXISTS TUI_Event",
            // CompressionCaptures shipped for a few days without dedupe. On a fresh DB the CREATE
            // above already has these columns and these fail benignly ("duplicate column"); on an
            // early one they add them. The hash index is partial (see its remarks), so it builds
            // either way.
            AddCompressionCaptureContentHashColumn,
            AddCompressionCaptureSeenCountColumn,
            AddCompressionCaptureRewriteAcceptedColumn,
            CreateCompressionCapturesHashIndex,
            MigrateUserInputsAddBertEmbeddedUTC,
            CreateUserInputsUnembeddedIndex,
            MigrateUserInputsAddBertEmbedFailureCount,
            MigrateSessionsAddAggregateEmbeddedUTC,
            CreateSessionsUnaggregatedIndex,
            MigrateSessionsAddAggregateEmbedFailureCount,
            CreateUserInputsFts,
            CreateUserInputsFtsAfterDeleteTrigger,
            DropUserInputsFtsAfterInsertTrigger,
            DropUserInputsFtsAfterUpdateTrigger,
            CreateEnvironmentsNameNoCaseUniqueIndex,
            MigrateCodeAnalyzerIgnoresAddMatchKind
        ];

        /// <summary>
        /// Seed migration: after adding the Processed column, mark all existing sessions as processed.
        /// Only runs when the AddProcessedColumn migration succeeds.
        /// </summary>
        public const string SeedProcessedColumn = "UPDATE Sessions SET Processed = 1";

        // Environment CRUD (global)
        public const string InsertEnvironment = """
            INSERT INTO Environments (CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC)
            VALUES ($customName, $llm, $path, $customArgs, $customPrompt, $createdUTC, $lastUsedUTC)
            RETURNING Id;
            """;
        public const string SelectEnvironmentById = """
            SELECT Id, CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC
            FROM Environments
            WHERE Id = $id;
            """;
        public const string SelectEnvironmentByNameAndLlm = """
            SELECT Id, CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC
            FROM Environments
            WHERE CustomName = $customName AND LLM = $llm;
            """;
        public const string SelectEnvironmentByName = """
            SELECT Id, CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC
            FROM Environments
            WHERE CustomName = $customName
            ORDER BY LastUsedUTC DESC
            LIMIT 1;
            """;
        // Case-insensitive single-row lookup for the create-time collision check (the env
        // name maps to a case-insensitive directory). NOCASE folds ASCII, matching the
        // validated env-name charset.
        public const string SelectEnvironmentByNameNoCase = """
            SELECT Id, CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC
            FROM Environments
            WHERE CustomName = $customName COLLATE NOCASE
            ORDER BY LastUsedUTC DESC
            LIMIT 1;
            """;
        public const string SelectAllEnvironments = """
            SELECT Id, CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC
            FROM Environments
            ORDER BY LastUsedUTC DESC;
            """;
        public const string SelectCustomEnvironments = """
            SELECT Id, CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC
            FROM Environments
            WHERE CustomName != 'Default'
              AND NOT (
                  CustomName IN ('Claude', 'Codex', 'Antigravity', 'Copilot', 'OpenCode')
                  AND (Path IS NULL OR Path = '')
                  AND (CustomArgs IS NULL OR CustomArgs = '')
                  AND (CustomPrompt IS NULL OR CustomPrompt = '')
              )
            ORDER BY LastUsedUTC DESC;
            """;
        public const string UpdateEnvironment = """
            UPDATE Environments
            SET CustomName = $customName,
                LLM = $llm,
                Path = $path,
                CustomArgs = $customArgs,
                CustomPrompt = $customPrompt,
                LastUsedUTC = $lastUsedUTC
            WHERE Id = $id;
            """;
        public const string DeleteEnvironment = "DELETE FROM Environments WHERE Id = $id;";

        // Sandbox CRUD (project-scoped)
        public const string InsertSandbox = """
            INSERT INTO Sandboxes (Name, Path, ProjectPath, Branch, CommitHash, RemoteUrl, SourceBranch, CreatedUTC)
            VALUES ($name, $path, $projectPath, $branch, $commitHash, $remoteUrl, $sourceBranch, $createdUTC)
            RETURNING Id;
            """;
        public const string SelectSandboxesByProject = """
            SELECT Id, Name, Path, ProjectPath, Branch, CommitHash, RemoteUrl, SourceBranch, CreatedUTC
            FROM Sandboxes
            WHERE ProjectPath = $projectPath
            ORDER BY CreatedUTC DESC;
            """;
        public const string SelectSandboxById = """
            SELECT Id, Name, Path, ProjectPath, Branch, CommitHash, RemoteUrl, SourceBranch, CreatedUTC
            FROM Sandboxes
            WHERE Id = $id;
            """;
        public const string SelectSandboxByNameAndProject = """
            SELECT Id, Name, Path, ProjectPath, Branch, CommitHash, RemoteUrl, SourceBranch, CreatedUTC
            FROM Sandboxes
            WHERE Name = $name AND ProjectPath = $projectPath;
            """;
        public const string DeleteSandbox = "DELETE FROM Sandboxes WHERE Id = $id;";

        // AgentMetadata CRUD
        public const string UpsertAgentMetadata = """
            INSERT INTO AgentMetadata (Path, CustomName)
            VALUES ($path, $customName)
            ON CONFLICT(Path) DO UPDATE SET
                CustomName = excluded.CustomName
            RETURNING Id;
            """;

        public const string SelectAgentMetadataByPath = """
            SELECT Id, Path, CustomName
            FROM AgentMetadata
            WHERE Path = $path;
            """;

        public const string DeleteAgentMetadata = "DELETE FROM AgentMetadata WHERE Path = $path;";

        // ChatSummary CRUD
        public const string UpsertChatSummary = """
            INSERT INTO ChatSummary (SessionId, SummaryText, Date)
            VALUES ($sessionId, $summaryText, $date)
            ON CONFLICT(SessionId) DO UPDATE SET
                SummaryText = excluded.SummaryText,
                Date = excluded.Date
            RETURNING Id;
            """;
        public const string SelectChatSummaryById = """
            SELECT Id, SessionId, SummaryText, Date
            FROM ChatSummary
            WHERE Id = $id;
            """;
        public const string SelectChatSummariesBySession = """
            SELECT Id, SessionId, SummaryText, Date
            FROM ChatSummary
            WHERE SessionId = $sessionId
            ORDER BY Date DESC;
            """;
        public const string SelectAllChatSummaries = """
            SELECT Id, SessionId, SummaryText, Date
            FROM ChatSummary
            ORDER BY Date DESC;
            """;
        public const string DeleteChatSummary = "DELETE FROM ChatSummary WHERE Id = $id;";
        public const string DeleteChatSummaryBySession = "DELETE FROM ChatSummary WHERE SessionId = $sessionId;";

        // Session CRUD
        public const string InsertSession = """
            INSERT INTO Sessions (Id, Cli, EnvironmentName, WorkingDirectory, ProjectDisplayName, StartedUTC, OwnerPid, OwnershipTracked, JobRunId)
            VALUES ($id, $cli, $envName, $workDir, $projectDisplayName, $startedUTC, $ownerPid, 1, $jobRunId);
            """;
        public const string SelectLatestProjectDisplayNameByWorkingDirectory = """
            SELECT ProjectDisplayName
            FROM Sessions
            WHERE WorkingDirectory = $workingDirectory
              AND ProjectDisplayName IS NOT NULL
              AND ProjectDisplayName != ''
            ORDER BY StartedUTC DESC
            LIMIT 1;
            """;
        public const string UpdateLatestProjectDisplayNameByWorkingDirectory = """
            UPDATE Sessions
            SET ProjectDisplayName = $projectDisplayName
            WHERE Id = (
                SELECT Id
                FROM Sessions
                WHERE WorkingDirectory = $workingDirectory
                ORDER BY StartedUTC DESC
                LIMIT 1
            );
            """;
        public const string SetParentSessionId = """
            UPDATE Sessions SET ParentSessionId = $parentSessionId WHERE Id = $id;
            """;
        public const string SetSessionDisplayName = """
            UPDATE Sessions SET SessionDisplayName = $displayName WHERE Id = $id;
            """;
        public const string SelectSessionDisplayInfo = """
            SELECT Cli, SessionDisplayName FROM Sessions WHERE Id = $id;
            """;
        public const string InsertSessionLog = """
            INSERT INTO SessionLogs (SessionId, Timestamp, Content, IsError)
            VALUES ($sessionId, $timestamp, $content, $isError);
            """;
        public const string SelectOpenSessionCleanupCandidates = """
            SELECT s.Id, s.OwnerPid
            FROM Sessions s
            WHERE s.EndedUTC IS NULL
              AND (
                (COALESCE(s.OwnershipTracked, 0) = 1
                  AND s.StartedUTC < $trackedCutoff
                  AND MAX(
                        COALESCE((SELECT MAX(l.Timestamp) FROM SessionLogs l WHERE l.SessionId = s.Id), ''),
                        COALESCE((SELECT MAX(u.TimestampUTC) FROM UserInputs u WHERE u.SessionId = s.Id), ''),
                        s.StartedUTC
                      ) < $trackedCutoff)
                OR
                (s.OwnerPid IS NULL
                  AND s.StartedUTC < $untrackedCutoff
                  AND MAX(
                        COALESCE((SELECT MAX(l.Timestamp) FROM SessionLogs l WHERE l.SessionId = s.Id), ''),
                        COALESCE((SELECT MAX(u.TimestampUTC) FROM UserInputs u WHERE u.SessionId = s.Id), ''),
                        s.StartedUTC
                      ) < $untrackedCutoff)
              );
            """;
        public const string UpdateSessionEnd = """
            UPDATE Sessions
            SET EndedUTC = $endedUTC, ExitCode = $exitCode
            WHERE Id = $id;
            """;
        public const string SelectSessionById = """
            SELECT Id, Cli, EnvironmentName, WorkingDirectory, StartedUTC, EndedUTC, ExitCode
            FROM Sessions
            WHERE Id = $id;
            """;
        public const string SelectSessionLogsBySession = """
            SELECT Id, SessionId, Timestamp, Content, IsError
            FROM SessionLogs
            WHERE SessionId = $sessionId
            ORDER BY Id ASC;
            """;
        public const string SelectRecentSessions = """
            SELECT Id, Cli, EnvironmentName, WorkingDirectory, StartedUTC, EndedUTC, ExitCode
            FROM Sessions
            ORDER BY StartedUTC DESC
            LIMIT $limit;
            """;
        public const string SelectSessionOutput = """
            SELECT s.Id, s.Cli, s.EnvironmentName, s.WorkingDirectory, s.StartedUTC, s.EndedUTC, s.Processed,
                   COALESCE(o.Text, '')
            FROM Sessions s
            LEFT JOIN sessionOutPut o ON o.SessionId = s.Id
            WHERE s.Id = $sessionId;
            """;
        public const string SelectEndedUnprocessedSessions = """
            SELECT Id
            FROM Sessions
            WHERE EndedUTC IS NOT NULL
              AND Processed = 0
            ORDER BY EndedUTC ASC
            LIMIT $limit;
            """;
        public const string SelectSessionLogChunks = """
            SELECT Id, Timestamp, Content
            FROM SessionLogs
            WHERE SessionId = $sessionId
            ORDER BY Id ASC;
            """;
        public const string SelectUserInputsBySession = """
            SELECT Id, SessionId, Sequence, InputText, GitCommitHash, TimestampUTC
            FROM UserInputs
            WHERE SessionId = $sessionId
            ORDER BY Sequence ASC;
            """;
        public const string UpsertSessionOutput = """
            INSERT INTO sessionOutPut (SessionId, Text)
            VALUES ($sessionId, $text)
            ON CONFLICT(SessionId) DO UPDATE SET
                Text = excluded.Text;
            """;
        public const string UpdateSessionProcessed = """
            UPDATE Sessions
            SET Processed = 1,
                SessionDisplayName = CASE
                    WHEN SessionDisplayName IS NULL OR SessionDisplayName = '' THEN (
                        SELECT SUBSTR(u.InputText, 1, 120)
                        FROM UserInputs u
                        WHERE u.SessionId = $sessionId
                        ORDER BY u.Sequence ASC
                        LIMIT 1
                    )
                    ELSE SessionDisplayName
                END
            WHERE Id = $sessionId;
            """;
        public const string SelectChatHistoryBase = """
            SELECT s.Id,
                   s.Cli,
                   s.EnvironmentName,
                   s.WorkingDirectory,
                   s.ProjectDisplayName,
                   s.StartedUTC,
                   s.EndedUTC,
                   s.ExitCode,
                   s.ParentSessionId,
                   p.Cli,
                   s.SessionDisplayName,
                   u.Sequence,
                   SUBSTR(u.InputText, 1, 120),
                   (SELECT COUNT(*) FROM UserInputs WHERE SessionId = s.Id),
                   CASE WHEN s.EndedUTC IS NOT NULL THEN CAST((julianday(s.EndedUTC) - julianday(s.StartedUTC)) * 86400 AS INTEGER) ELSE NULL END
            FROM Sessions s
            LEFT JOIN UserInputs u ON u.Id = (
                SELECT Id FROM UserInputs WHERE SessionId = s.Id ORDER BY Sequence ASC LIMIT 1
            )
            LEFT JOIN Sessions p ON p.Id = NULLIF(s.ParentSessionId, '')
            WHERE s.JobRunId IS NULL
            """;
        public const string SelectChatHistoryBySessionId = """
            SELECT s.Id,
                   s.Cli,
                   s.EnvironmentName,
                   s.WorkingDirectory,
                   s.ProjectDisplayName,
                   s.StartedUTC,
                   s.EndedUTC,
                   s.ExitCode,
                   s.ParentSessionId,
                   p.Cli,
                   s.SessionDisplayName,
                   u.Sequence,
                   SUBSTR(u.InputText, 1, 120),
                   (SELECT COUNT(*) FROM UserInputs WHERE SessionId = s.Id),
                   CASE WHEN s.EndedUTC IS NOT NULL THEN CAST((julianday(s.EndedUTC) - julianday(s.StartedUTC)) * 86400 AS INTEGER) ELSE NULL END
            FROM Sessions s
            LEFT JOIN UserInputs u ON u.Id = (
                SELECT Id FROM UserInputs WHERE SessionId = s.Id ORDER BY Sequence ASC LIMIT 1
            )
            LEFT JOIN Sessions p ON p.Id = NULLIF(s.ParentSessionId, '')
            WHERE s.Id = $sessionId
            LIMIT 1;
            """;
        public const string UpdateSessionDisplayName = """
            UPDATE Sessions
            SET SessionDisplayName = $sessionDisplayName
            WHERE Id = $sessionId;
            """;

        // DeleteChatHistorySession — 7 statements executed in a transaction
        public const string DeleteSession_UnparentChildren = """
            UPDATE Sessions
            SET ParentSessionId = ''
            WHERE ParentSessionId = $sessionId;
            """;
        public const string DeleteSession_FileChanges = """
            DELETE FROM InputFileChanges
            WHERE UserInputId IN (SELECT Id FROM UserInputs WHERE SessionId = $sessionId)
               OR PreviousInputId IN (SELECT Id FROM UserInputs WHERE SessionId = $sessionId);
            """;
        public const string DeleteSession_SessionOutput = """
            DELETE FROM sessionOutPut
            WHERE SessionId = $sessionId;
            """;
        public const string DeleteSession_SessionLogs = """
            DELETE FROM SessionLogs
            WHERE SessionId = $sessionId;
            """;
        public const string DeleteSession_UserInputs = """
            DELETE FROM UserInputs
            WHERE SessionId = $sessionId;
            """;
        public const string DeleteSession_Session = """
            DELETE FROM Sessions
            WHERE Id = $sessionId;
            """;

        public static readonly string[] DeleteSessionCommands =
        [
            DeleteSession_UnparentChildren,
            DeleteSession_FileChanges,
            DeleteSession_SessionOutput,
            DeleteSession_SessionLogs,
            DeleteSession_TerminalSessionLogs,
            DeleteSession_UserInputs,
            DeleteSession_Session
        ];

        // UserInput CRUD
        public const string SelectLastUserInput = """
            SELECT Id, SessionId, Sequence, InputText, GitCommitHash, TimestampUTC
            FROM UserInputs
            WHERE SessionId = $sessionId
            ORDER BY Sequence DESC
            LIMIT 1;
            """;
        public const string InsertUserInput = """
            INSERT INTO UserInputs (SessionId, Sequence, InputText, GitCommitHash, TimestampUTC)
            VALUES ($sessionId, $sequence, $inputText, $gitCommitHash, $timestampUTC)
            RETURNING Id;
            """;
        public const string InsertFileChange = """
            INSERT INTO InputFileChanges (UserInputId, PreviousInputId, FilePath, ChangeType, LinesAdded, LinesDeleted, DiffContent)
            VALUES ($userInputId, $previousInputId, $filePath, $changeType, $linesAdded, $linesDeleted, $diffContent);
            """;
        public const string DeleteFileChangesForUserInput = """
            DELETE FROM InputFileChanges
            WHERE UserInputId = $userInputId;
            """;
        public const string SelectSessionWorkingDirectory = """
            SELECT WorkingDirectory
            FROM Sessions
            WHERE Id = $sessionId
            LIMIT 1;
            """;

        public const string SelectUserInputById = """
            SELECT Id, SessionId, Sequence, InputText, GitCommitHash, TimestampUTC
            FROM UserInputs
            WHERE Id = $id
            LIMIT 1;
            """;

        // Poison-pill threshold: after this many consecutive failures we stop trying a
        // given row so it can't block every newer row behind it. Reset by hand if the
        // underlying problem is fixed (e.g. a bug in the embedder pipeline).
        public const int BertEmbedFailureSkipThreshold = 3;

        // BERT embedding backfill — drives BertEmbeddingBackfillJob.
        public static readonly string SelectUnembeddedUserInputs = $"""
            SELECT Id, SessionId, InputText
            FROM UserInputs
            WHERE BertEmbeddedUTC IS NULL
              AND BertEmbedFailureCount < {BertEmbedFailureSkipThreshold}
            ORDER BY Id ASC
            LIMIT $batchSize;
            """;
        public const string MarkUserInputBertEmbedded = """
            UPDATE UserInputs SET BertEmbeddedUTC = $now WHERE Id = $id;
            """;
        public const string IncrementUserInputBertEmbedFailureCount = """
            UPDATE UserInputs SET BertEmbedFailureCount = BertEmbedFailureCount + 1 WHERE Id = $id;
            """;

        // Session-level BERT aggregate embedding backfill — drives SessionAggregateEmbeddingBackfillJob.
        public static readonly string SelectUnaggregatedEndedSessionIds = $"""
            SELECT Id
            FROM Sessions
            WHERE EndedUTC IS NOT NULL
              AND AggregateEmbeddedUTC IS NULL
              AND AggregateEmbedFailureCount < {BertEmbedFailureSkipThreshold}
            ORDER BY EndedUTC ASC
            LIMIT $batchSize;
            """;
        public const string MarkSessionAggregateEmbedded = """
            UPDATE Sessions SET AggregateEmbeddedUTC = $now WHERE Id = $id;
            """;
        public const string IncrementSessionAggregateEmbedFailureCount = """
            UPDATE Sessions SET AggregateEmbedFailureCount = AggregateEmbedFailureCount + 1 WHERE Id = $id;
            """;
        public const string SelectUserInputTextsForSessionInOrder = """
            SELECT InputText
            FROM UserInputs
            WHERE SessionId = $sessionId
            ORDER BY Sequence ASC;
            """;

        // FTS5 lexical search over UserInputs.InputText, joining the source table to
        // produce the same `<sessionId>:<userInputId>` document id format the existing
        // BertSearchHitResponse pipeline expects. Lower bm25 rank = better match.
        public const string SelectUserInputsFtsByQuery = """
            SELECT ui.SessionId, ui.Id, ui.InputText
            FROM UserInputs_fts fts
            JOIN UserInputs ui ON ui.Id = fts.rowid
            WHERE UserInputs_fts MATCH $query
            ORDER BY fts.rank
            LIMIT $topK;
            """;

        // User input text reads — used by IGetUserText.
        public const string SelectTextForInputIdOrRaw = """
            SELECT SUBSTR(u.InputText, 1, $maxChars)
            FROM UserInputs u
            WHERE u.Id = $inputId
            LIMIT 1;
            """;
        public const string SelectFirstInputTextForSessionOrRaw = """
            SELECT SUBSTR(u.InputText, 1, $maxChars)
            FROM UserInputs u
            WHERE u.SessionId = $sessionId
            ORDER BY u.Sequence ASC
            LIMIT 1;
            """;

        // ProjectCache Table — generic key-value store scoped per project
        public const string CreateProjectCacheTable = """
            CREATE TABLE IF NOT EXISTS ProjectCache (
                ProjectPath TEXT NOT NULL,
                Key TEXT NOT NULL,
                Value TEXT NOT NULL DEFAULT '',
                UpdatedUTC TEXT NOT NULL,
                PRIMARY KEY (ProjectPath, Key)
            )
            """;

        public const string UpsertProjectCache = """
            INSERT INTO ProjectCache (ProjectPath, Key, Value, UpdatedUTC)
            VALUES ($projectPath, $key, $value, $updatedUTC)
            ON CONFLICT(ProjectPath, Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedUTC = excluded.UpdatedUTC;
            """;
        public const string SelectProjectCacheByKey = """
            SELECT Value
            FROM ProjectCache
            WHERE ProjectPath = $projectPath AND Key = $key;
            """;
        public const string SelectAllProjectCache = """
            SELECT Key, Value
            FROM ProjectCache
            WHERE ProjectPath = $projectPath;
            """;
        public const string DeleteProjectCacheByKey = """
            DELETE FROM ProjectCache
            WHERE ProjectPath = $projectPath AND Key = $key;
            """;

        // GlobalCache Table — generic key-value store NOT scoped to a project. Use for
        // machine/user-wide flags that must persist across projects (e.g. whether the MCP
        // server has already been removed from a given CLI's user-scope registration).
        public const string CreateGlobalCacheTable = """
            CREATE TABLE IF NOT EXISTS GlobalCache (
                Key TEXT NOT NULL PRIMARY KEY,
                Value TEXT NOT NULL DEFAULT '',
                UpdatedUTC TEXT NOT NULL
            )
            """;

        public const string UpsertGlobalCache = """
            INSERT INTO GlobalCache (Key, Value, UpdatedUTC)
            VALUES ($key, $value, $updatedUTC)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedUTC = excluded.UpdatedUTC;
            """;
        public const string SelectGlobalCacheByKey = """
            SELECT Value
            FROM GlobalCache
            WHERE Key = $key;
            """;
        public const string SelectAllGlobalCache = """
            SELECT Key, Value
            FROM GlobalCache;
            """;
        public const string DeleteGlobalCacheByKey = """
            DELETE FROM GlobalCache
            WHERE Key = $key;
            """;
    }
}
