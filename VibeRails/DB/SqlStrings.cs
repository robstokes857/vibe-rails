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

        public static readonly string[] InitStatements =
        [
            CreateEnvironmentsTable,
            CreateEnvironmentsIndex,
            CreateAgentMetadataTable,
            CreateAgentMetadataPathIndex,
            CreateProjectMetadataTable,
            CreateProjectMetadataPathIndex
        ];

        public static readonly string[] MigrationStatements = [];

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
