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
                CreatedUTC TEXT NOT NULL,
                UNIQUE(Name, ProjectPath)
            )
            """;
        public const string CreateSandboxesIndex = "CREATE INDEX IF NOT EXISTS idx_sandboxes_project ON Sandboxes(ProjectPath)";

        public static readonly string[] InitStatements =
        [
            CreateEnvironmentsTable,
            CreateEnvironmentsIndex,
            CreateAgentMetadataTable,
            CreateAgentMetadataPathIndex,
            CreateProjectMetadataTable,
            CreateProjectMetadataPathIndex,
            CreateSandboxesTable,
            CreateSandboxesIndex
        ];

        public static readonly string[] MigrationStatements =
        [
            "ALTER TABLE Sandboxes ADD COLUMN RemoteUrl TEXT;"
        ];

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
            INSERT INTO Sandboxes (Name, Path, ProjectPath, Branch, CommitHash, RemoteUrl, CreatedUTC)
            VALUES ($name, $path, $projectPath, $branch, $commitHash, $remoteUrl, $createdUTC)
            RETURNING Id;
            """;
        public const string SelectSandboxesByProject = """
            SELECT Id, Name, Path, ProjectPath, Branch, CommitHash, RemoteUrl, CreatedUTC
            FROM Sandboxes
            WHERE ProjectPath = $projectPath
            ORDER BY CreatedUTC DESC;
            """;
        public const string SelectSandboxById = """
            SELECT Id, Name, Path, ProjectPath, Branch, CommitHash, RemoteUrl, CreatedUTC
            FROM Sandboxes
            WHERE Id = $id;
            """;
        public const string SelectSandboxByNameAndProject = """
            SELECT Id, Name, Path, ProjectPath, Branch, CommitHash, RemoteUrl, CreatedUTC
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
    }
}
