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

        // ProjectMetadata Table
        public const string CreateProjectMetadataTable = """
            CREATE TABLE IF NOT EXISTS ProjectMetadata (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL UNIQUE,
                CustomName TEXT NOT NULL
            )
            """;
        public const string CreateProjectMetadataPathIndex = "CREATE INDEX IF NOT EXISTS idx_project_metadata_path ON ProjectMetadata(Path)";

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
                StartedUTC TEXT NOT NULL,
                EndedUTC TEXT,
                ExitCode INTEGER,
                Processed INTEGER NOT NULL DEFAULT 0,
                ParentSessionId TEXT DEFAULT '',
                SessionDisplayName TEXT DEFAULT ''
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
            CreateProjectMetadataTable,
            CreateProjectMetadataPathIndex,
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
            CreateClaudePlansTable,
            CreateClaudePlansSessionIndex,
            CreateClaudePlansStatusIndex,
            CreateClaudePlansCreatedIndex
        ];

        public static readonly string[] MigrationStatements =
        [
            "ALTER TABLE Sandboxes ADD COLUMN RemoteUrl TEXT;",
            "ALTER TABLE Sandboxes ADD COLUMN SourceBranch TEXT;",
            "ALTER TABLE ChatSummary DROP COLUMN SummaryBy;",
            "ALTER TABLE Sessions ADD COLUMN Processed INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE Sessions ADD COLUMN ParentSessionId TEXT DEFAULT ''",
            "ALTER TABLE Sessions ADD COLUMN SessionDisplayName TEXT DEFAULT ''"
        ];

        /// <summary>
        /// Seed migration: after adding the Processed column, mark all existing sessions as processed.
        /// Keyed by the migration it depends on — only runs once when that migration succeeds.
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

        // ProjectMetadata CRUD
        public const string UpsertProjectMetadata = """
            INSERT INTO ProjectMetadata (Path, CustomName)
            VALUES ($path, $customName)
            ON CONFLICT(Path) DO UPDATE SET
                CustomName = excluded.CustomName
            RETURNING Id;
            """;

        public const string SelectProjectMetadataByPath = """
            SELECT Id, Path, CustomName
            FROM ProjectMetadata
            WHERE Path = $path;
            """;

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
            INSERT INTO Sessions (Id, Cli, EnvironmentName, WorkingDirectory, StartedUTC)
            VALUES ($id, $cli, $envName, $workDir, $startedUTC);
            """;
        public const string InsertSessionLog = """
            INSERT INTO SessionLogs (SessionId, Timestamp, Content, IsError)
            VALUES ($sessionId, $timestamp, $content, $isError);
            """;
        public const string SelectOpenSessionIds = """
            SELECT s.Id
            FROM Sessions s
            WHERE s.EndedUTC IS NULL
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
            SET Processed = 1
            WHERE Id = $sessionId;
            """;
        public const string SelectChatHistoryPage = """
            SELECT s.Id, s.Cli, s.EnvironmentName, s.WorkingDirectory, s.StartedUTC, s.EndedUTC, s.ExitCode, s.ParentSessionId, s.SessionDisplayName,
                   u.Sequence, SUBSTR(u.InputText, 1, 120)
            FROM Sessions s
            LEFT JOIN UserInputs u ON u.Id = (
                SELECT Id FROM UserInputs WHERE SessionId = s.Id ORDER BY Sequence ASC LIMIT 1
            )
            ORDER BY s.StartedUTC DESC
            LIMIT $limit OFFSET $offset;
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
            DeleteSession_ClaudePlans,
            DeleteSession_SessionOutput,
            DeleteSession_SessionLogs,
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
    }
}
