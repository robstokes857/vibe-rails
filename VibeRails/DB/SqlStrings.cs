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
                OwnershipTracked INTEGER NOT NULL DEFAULT 1
            )
            """;
        public const string CreateSessionsIndex = "CREATE INDEX IF NOT EXISTS idx_sessions_started ON Sessions(StartedUTC DESC)";

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

        // TUI_Event Table
        public const string CreateTuiEventTable = """
            CREATE TABLE IF NOT EXISTS TUI_Event (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                TimestampUTC TEXT NOT NULL,
                TriggerString TEXT NOT NULL,
                EventType TEXT NOT NULL,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
            )
            """;
        public const string CreateTuiEventSessionTimestampIndex = "CREATE INDEX IF NOT EXISTS idx_tui_event_session_timestamp ON TUI_Event(SessionId, TimestampUTC)";

        // CleanedUserInput Table — ETL-filtered, normalized version of UserInputs.InputText.
        // Strict 1:1 with UserInputs via dual FKs: UserInputs.CleanedId → CleanedUserInput.Id
        // and CleanedUserInput.UserInputId → UserInputs.Id.
        public const string CreateCleanedUserInputTable = """
            CREATE TABLE IF NOT EXISTS CleanedUserInput (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                UserInputId INTEGER NOT NULL UNIQUE,
                CleanedText TEXT NOT NULL,
                CreatedUTC TEXT NOT NULL,
                BertEmbeddedUTC TEXT,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id),
                FOREIGN KEY (UserInputId) REFERENCES UserInputs(Id) ON DELETE CASCADE
            )
            """;
        public const string CreateCleanedUserInputSessionIndex = "CREATE INDEX IF NOT EXISTS idx_cleaned_user_input_session ON CleanedUserInput(SessionId)";
        public const string CreateCleanedUserInputUserInputIdIndex = "CREATE UNIQUE INDEX IF NOT EXISTS idx_cleaned_user_input_user_input_id ON CleanedUserInput(UserInputId)";
        public const string CreateCleanedUserInputUnembeddedIndex = "CREATE INDEX IF NOT EXISTS idx_cleaned_user_input_unembedded ON CleanedUserInput(Id) WHERE BertEmbeddedUTC IS NULL AND CleanedText != ''";
        // Migration: add CleanedId FK column to UserInputs (nullable — null means uncleaned).
        public const string MigrateUserInputsAddCleanedId = "ALTER TABLE UserInputs ADD COLUMN CleanedId INTEGER REFERENCES CleanedUserInput(Id)";
        public const string CreateUserInputsCleanedIdIndex = "CREATE INDEX IF NOT EXISTS idx_user_inputs_cleaned_id ON UserInputs(CleanedId)";
        // Partial index for fast uncleaned scans.
        public const string CreateUserInputsUncleanedIndex = "CREATE INDEX IF NOT EXISTS idx_user_inputs_uncleaned ON UserInputs(SessionId, Id) WHERE CleanedId IS NULL";

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

        // ClaudePlans Table
        public const string CreateClaudePlansTable = """
            CREATE TABLE IF NOT EXISTS ClaudePlans (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                UserInputId INTEGER,
                PlanFilePath TEXT,
                PlanContent TEXT NOT NULL,
                PlanSummary TEXT,
                Status TEXT NOT NULL DEFAULT 'created',
                CreatedUTC TEXT NOT NULL,
                CompletedUTC TEXT,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id),
                FOREIGN KEY (UserInputId) REFERENCES UserInputs(Id)
            )
            """;
        public const string CreateClaudePlansSessionIndex = "CREATE INDEX IF NOT EXISTS idx_claude_plans_session ON ClaudePlans(SessionId)";
        public const string CreateClaudePlansStatusIndex = "CREATE INDEX IF NOT EXISTS idx_claude_plans_status ON ClaudePlans(Status)";
        public const string CreateClaudePlansCreatedIndex = "CREATE INDEX IF NOT EXISTS idx_claude_plans_created ON ClaudePlans(CreatedUTC DESC)";

        public static readonly string[] InitStatements =
        [
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
            CreateTuiEventTable,
            CreateTuiEventSessionTimestampIndex,
            CreateCleanedUserInputTable,
            CreateCleanedUserInputSessionIndex,
            CreateInputFileChangesTable,
            CreateInputFileChangesInputIndex,
            CreateInputFileChangesPathIndex,
            CreateClaudePlansTable,
            CreateClaudePlansSessionIndex,
            CreateClaudePlansStatusIndex,
            CreateClaudePlansCreatedIndex,
            CreateTerminalSessionLogsTable,
            CreateTerminalSessionLogsIndex,
            CreateProjectCacheTable
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
            MigrateUserInputsAddCleanedId,
            "ALTER TABLE CleanedUserInput ADD COLUMN UserInputId INTEGER REFERENCES UserInputs(Id)",
            CreateCleanedUserInputUserInputIdIndex,
            CreateUserInputsCleanedIdIndex,
            CreateUserInputsUncleanedIndex,
            "ALTER TABLE CleanedUserInput ADD COLUMN BertEmbeddedUTC TEXT",
            CreateCleanedUserInputUnembeddedIndex
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
                  CustomName IN ('Claude', 'Codex', 'Gemini')
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
            INSERT INTO Sessions (Id, Cli, EnvironmentName, WorkingDirectory, ProjectDisplayName, StartedUTC, OwnerPid, OwnershipTracked)
            VALUES ($id, $cli, $envName, $workDir, $projectDisplayName, $startedUTC, $ownerPid, 1);
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
              AND COALESCE(s.OwnershipTracked, 0) = 1
              AND s.StartedUTC < $cutoff
              AND MAX(
                    COALESCE((SELECT MAX(l.Timestamp) FROM SessionLogs l WHERE l.SessionId = s.Id), ''),
                    COALESCE((SELECT MAX(u.TimestampUTC) FROM UserInputs u WHERE u.SessionId = s.Id), ''),
                    s.StartedUTC
                  ) < $cutoff;
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
        public const string DeleteSession_ClaudePlans = """
            DELETE FROM ClaudePlans
            WHERE SessionId = $sessionId;
            """;
        public const string DeleteSession_SessionOutput = """
            DELETE FROM sessionOutPut
            WHERE SessionId = $sessionId;
            """;
        public const string DeleteSession_SessionLogs = """
            DELETE FROM SessionLogs
            WHERE SessionId = $sessionId;
            """;
        public const string DeleteSession_TuiEvents = """
            DELETE FROM TUI_Event
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

        public const string DeleteSession_NullOutCleanedIds = """
            UPDATE UserInputs SET CleanedId = NULL WHERE SessionId = $sessionId;
            """;

        public static readonly string[] DeleteSessionCommands =
        [
            DeleteSession_UnparentChildren,
            DeleteSession_FileChanges,
            DeleteSession_ClaudePlans,
            DeleteSession_SessionOutput,
            DeleteSession_SessionLogs,
            DeleteSession_TerminalSessionLogs,
            DeleteSession_TuiEvents,
            DeleteSession_NullOutCleanedIds,
            DeleteSession_CleanedUserInput,
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

        // TUI event tracking
        public const string InsertTuiEvent = """
            INSERT INTO TUI_Event (SessionId, TimestampUTC, TriggerString, EventType)
            VALUES ($sessionId, $timestampUTC, $triggerString, $eventType);
            """;

        // CleanedUserInput CRUD — 1:1 with UserInputs via dual FKs.
        public const string InsertCleanedUserInputAndLink = """
            INSERT INTO CleanedUserInput (SessionId, UserInputId, CleanedText, CreatedUTC)
            VALUES ($sessionId, $userInputId, $cleanedText, $createdUTC)
            RETURNING Id;
            """;
        public const string UpdateUserInputCleanedId = """
            UPDATE UserInputs SET CleanedId = $cleanedId WHERE Id = $userInputId;
            """;
        public const string SelectCleanedTextForInputId = """
            SELECT c.CleanedText
            FROM UserInputs u
            INNER JOIN CleanedUserInput c ON u.CleanedId = c.Id
            WHERE u.Id = $inputId
            LIMIT 1;
            """;
        public const string SelectSessionCleanedTextOrdered = """
            SELECT c.CleanedText
            FROM UserInputs u
            INNER JOIN CleanedUserInput c ON u.CleanedId = c.Id
            WHERE u.SessionId = $sessionId
            ORDER BY u.Sequence ASC;
            """;
        public const string SelectUncleanedInputsForSession = """
            SELECT u.Id, u.SessionId, u.Sequence, u.InputText, u.GitCommitHash, u.TimestampUTC
            FROM UserInputs u
            WHERE u.SessionId = $sessionId
              AND u.CleanedId IS NULL
            ORDER BY u.Id ASC;
            """;
        public const string SelectUncleanedInputsForClosedSessions = """
            SELECT u.Id, u.SessionId, u.Sequence, u.InputText, u.GitCommitHash, u.TimestampUTC
            FROM UserInputs u
            INNER JOIN Sessions s ON u.SessionId = s.Id
            WHERE u.CleanedId IS NULL
              AND s.EndedUTC IS NOT NULL
              AND datetime(s.EndedUTC) < datetime('now', '-5 minutes')
            ORDER BY u.Id ASC
            LIMIT $batchSize;
            """;
        public const string SelectIsInputCleaned = """
            SELECT CleanedId FROM UserInputs WHERE Id = $inputId LIMIT 1;
            """;
        // BERT embedding tracking
        public const string SelectUnembeddedCleanedInputs = """
            SELECT c.Id, c.SessionId, c.UserInputId, c.CleanedText
            FROM CleanedUserInput c
            WHERE c.BertEmbeddedUTC IS NULL
              AND c.UserInputId IS NOT NULL
              AND c.CleanedText != ''
            ORDER BY c.Id ASC
            LIMIT $batchSize;
            """;
        public const string MarkCleanedInputEmbedded = """
            UPDATE CleanedUserInput SET BertEmbeddedUTC = $embeddedUTC WHERE Id = $cleanedId;
            """;

        public const string DeleteSession_CleanedUserInput = """
            DELETE FROM CleanedUserInput
            WHERE SessionId = $sessionId;
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
    }
}
