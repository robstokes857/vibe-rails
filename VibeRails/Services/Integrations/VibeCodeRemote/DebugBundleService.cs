using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Serilog;
using VibeRails.DB;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Builds an encrypted debug bundle for a single terminal session and uploads it to
/// the VibeRails-Front server so a maintainer can pull it and debug remotely.
///
/// The bundle is a self-contained minimal SQLite database (the same tables the
/// python-scripts read, so they work unmodified via their --db flag). It is:
///   1. built from the live session's rows (Sessions / SessionLogs / UserInputs / TerminalSessionLogs),
///   2. Brotli-compressed,
///   3. AES-256-GCM encrypted with a random per-upload key,
///   4. the AES key RSA-OAEP-wrapped with the bundled public key (private key lives only on the server).
/// The server unwraps the key with its private cert and decrypts the payload when the bundle is pulled.
/// </summary>
public interface IDebugBundleService
{
    Task<DebugBundleResult> SendAsync(string sessionId, string? message, CancellationToken cancellationToken);
}

public enum DebugBundleStatus
{
    Success,
    NoApiKey,
    SessionNotFound,
    UploadFailed
}

public record DebugBundleResult(DebugBundleStatus Status, string? Detail = null);

public class DebugBundleService : IDebugBundleService
{
    private readonly HttpClient _httpClient;
    private readonly IRepository _repository;

    // The public key ships beside vb (see VibeRails.csproj "Certs\**" copy rule and
    // deploy/prepare-binaries.ps1). Only the public half is ever shipped to clients.
    private static readonly string PublicKeyPath =
        Path.Combine(AppContext.BaseDirectory, "Certs", "rsa_public_key.pem");

    // The public key ships beside vb and never changes at runtime, so read + parse the PEM
    // once instead of on every upload. A fresh RSA is created per call from these cached
    // parameters (RSA instances aren't guaranteed thread-safe for concurrent use).
    private static readonly Lazy<RSAParameters> PublicKeyParameters = new(() =>
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(PublicKeyPath));
        return rsa.ExportParameters(includePrivateParameters: false);
    });

    public DebugBundleService(HttpClient httpClient, IRepository repository)
    {
        _httpClient = httpClient;
        _repository = repository;
    }

    public async Task<DebugBundleResult> SendAsync(string sessionId, string? message, CancellationToken cancellationToken)
    {
        var apiKey = ParserConfigs.GetApiKey();
        var frontendUrl = ParserConfigs.GetFrontendUrl();

        if (string.IsNullOrWhiteSpace(apiKey))
            return new DebugBundleResult(DebugBundleStatus.NoApiKey, "No API key configured.");
        if (string.IsNullOrWhiteSpace(frontendUrl))
            return new DebugBundleResult(DebugBundleStatus.UploadFailed, "No frontend URL configured.");

        // 1. Build the minimal SQLite bundle for this session.
        byte[] dbBytes;
        string cli;
        try
        {
            var built = await BuildSessionDbAsync(sessionId, cancellationToken);
            if (built is null)
                return new DebugBundleResult(DebugBundleStatus.SessionNotFound, $"Session not found: {sessionId}");
            (dbBytes, cli) = built.Value;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DebugBundle] Failed to build bundle for session {SessionId}", sessionId);
            return new DebugBundleResult(DebugBundleStatus.UploadFailed, "Failed to build the debug bundle.");
        }

        // 2. Brotli-compress (SQLite + ANSI logs compress hugely).
        var compressed = BrotliCompress(dbBytes);

        // 3. AES-256-GCM encrypt with a random per-upload key.
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize); // 12 bytes
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];                            // 16 bytes
        var ciphertext = new byte[compressed.Length];
        try
        {
            using var aes = new AesGcm(aesKey, tag.Length);
            aes.Encrypt(nonce, compressed, ciphertext, tag);

            // 4. RSA-OAEP-SHA256 wrap the AES key with the bundled public key.
            byte[] wrappedKey;
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(PublicKeyParameters.Value);
                wrappedKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
            }

            // 5. POST the envelope as multipart/form-data.
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(ciphertext);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "bundle", $"{sessionId}.db.br.enc");
            content.Add(new StringContent(Convert.ToBase64String(wrappedKey)), "encryptedAesKey");
            content.Add(new StringContent(Convert.ToBase64String(nonce)), "nonce");
            content.Add(new StringContent(Convert.ToBase64String(tag)), "tag");
            content.Add(new StringContent(sessionId), "sessionId");
            content.Add(new StringContent(cli), "cli");
            content.Add(new StringContent(message ?? string.Empty), "message");
            content.Add(new StringContent(VersionInfo.Version), "clientVersion");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{frontendUrl.TrimEnd('/')}/api/v1/debug-bundles");
            request.Headers.Add("X-Api-Key", apiKey);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new DebugBundleResult(DebugBundleStatus.NoApiKey, "The server rejected the API key.");
            if (!response.IsSuccessStatusCode)
                return new DebugBundleResult(DebugBundleStatus.UploadFailed, $"Server returned {(int)response.StatusCode}.");

            return new DebugBundleResult(DebugBundleStatus.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DebugBundle] Upload failed for session {SessionId}", sessionId);
            return new DebugBundleResult(DebugBundleStatus.UploadFailed, ex.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aesKey);
        }
    }

    /// <summary>
    /// Builds a fresh minimal SQLite database containing only this session's rows, using the
    /// same CREATE TABLE definitions as the real state.db (SqlStrings) so the python-scripts
    /// read it identically via --db. Returns the .db bytes and the session's CLI, or null if
    /// the session does not exist.
    /// </summary>
    private async Task<(byte[] dbBytes, string cli)?> BuildSessionDbAsync(string sessionId, CancellationToken cancellationToken)
    {
        // Session-only lookup: GetSessionWithLogsAsync would also read and base64-encode
        // every SessionLog row, which we then re-read below via GetSessionLogChunksAsync —
        // a full duplicate read of the largest table. We only need the session header here.
        var session = await _repository.GetSessionByIdAsync(sessionId, cancellationToken);
        if (session is null)
            return null;
        // These three reads are independent (each opens its own SQLite connection), so run
        // them concurrently instead of serially.
        var chunksTask = _repository.GetSessionLogChunksAsync(sessionId, cancellationToken);
        var userInputsTask = _repository.GetUserInputsForSessionAsync(sessionId, cancellationToken);
        var terminalLogsTask = _repository.GetTerminalSessionLogsAsync(sessionId, cancellationToken);
        await Task.WhenAll(chunksTask, userInputsTask, terminalLogsTask);
        var chunks = await chunksTask;
        var userInputs = await userInputsTask;
        var terminalLogs = await terminalLogsTask;

        var tempPath = Path.Combine(Path.GetTempPath(), $"vb-debug-{Guid.NewGuid():N}.db");
        try
        {
            // Pooling=false so disposing the connection fully releases the file
            // handle — otherwise the pooled handle keeps the temp .db open and the
            // File.ReadAllBytes/Delete below fails on Windows. Avoids a global
            // SqliteConnection.ClearAllPools(), which would also tear down the live
            // state.db pool.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Pooling = false
            }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);

                foreach (var createStmt in new[]
                {
                    SqlStrings.CreateSessionsTable,
                    SqlStrings.CreateSessionLogsTable,
                    SqlStrings.CreateUserInputsTable,
                    SqlStrings.CreateTerminalSessionLogsTable
                })
                {
                    await using var create = connection.CreateCommand();
                    create.CommandText = createStmt;
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

                await using (var insertSession = connection.CreateCommand())
                {
                    // List every column the Sessions schema (SqlStrings.CreateSessionsTable)
                    // defines so the bundle is a complete, schema-faithful copy. The six
                    // columns SessionResponse doesn't carry (display names + ownership/processed
                    // bookkeeping) are written with the schema's own defaults — explicit here so
                    // a future schema column forces this code to be revisited rather than
                    // silently dropping data or failing on a new NOT NULL column.
                    insertSession.CommandText = """
                        INSERT INTO Sessions
                            (Id, Cli, EnvironmentName, WorkingDirectory, ProjectDisplayName,
                             StartedUTC, EndedUTC, ExitCode, Processed, ParentSessionId,
                             SessionDisplayName, OwnerPid, OwnershipTracked)
                        VALUES
                            ($id, $cli, $env, $wd, '',
                             $started, $ended, $exit, 0, '',
                             '', NULL, 1);
                        """;
                    insertSession.Parameters.AddWithValue("$id", session.Id);
                    insertSession.Parameters.AddWithValue("$cli", session.Cli);
                    insertSession.Parameters.AddWithValue("$env", (object?)session.EnvironmentName ?? DBNull.Value);
                    insertSession.Parameters.AddWithValue("$wd", session.WorkingDirectory);
                    insertSession.Parameters.AddWithValue("$started", session.StartedUTC.ToString("O"));
                    insertSession.Parameters.AddWithValue("$ended", (object?)session.EndedUTC?.ToString("O") ?? DBNull.Value);
                    insertSession.Parameters.AddWithValue("$exit", (object?)session.ExitCode ?? DBNull.Value);
                    await insertSession.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var insertLog = connection.CreateCommand())
                {
                    insertLog.CommandText = """
                        INSERT INTO SessionLogs (Id, SessionId, Timestamp, Content, IsError)
                        VALUES ($id, $sessionId, $timestamp, $content, 0);
                        """;
                    var pId = insertLog.Parameters.Add("$id", SqliteType.Integer);
                    var pTs = insertLog.Parameters.Add("$timestamp", SqliteType.Text);
                    var pContent = insertLog.Parameters.Add("$content", SqliteType.Blob);
                    insertLog.Parameters.AddWithValue("$sessionId", session.Id);
                    foreach (var chunk in chunks)
                    {
                        pId.Value = chunk.Id;
                        pTs.Value = chunk.TimestampUtc.ToString("O");
                        pContent.Value = chunk.Content;
                        await insertLog.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await using (var insertInput = connection.CreateCommand())
                {
                    insertInput.CommandText = """
                        INSERT INTO UserInputs (Id, SessionId, Sequence, InputText, GitCommitHash, TimestampUTC)
                        VALUES ($id, $sessionId, $sequence, $inputText, $gitHash, $timestamp);
                        """;
                    var pId = insertInput.Parameters.Add("$id", SqliteType.Integer);
                    var pSeq = insertInput.Parameters.Add("$sequence", SqliteType.Integer);
                    var pText = insertInput.Parameters.Add("$inputText", SqliteType.Text);
                    var pHash = insertInput.Parameters.Add("$gitHash", SqliteType.Text);
                    var pTs = insertInput.Parameters.Add("$timestamp", SqliteType.Text);
                    insertInput.Parameters.AddWithValue("$sessionId", session.Id);
                    foreach (var input in userInputs)
                    {
                        pId.Value = input.Id;
                        pSeq.Value = input.Sequence;
                        pText.Value = input.InputText;
                        pHash.Value = (object?)input.GitCommitHash ?? DBNull.Value;
                        pTs.Value = input.TimestampUTC.ToString("O");
                        await insertInput.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await using (var insertTerm = connection.CreateCommand())
                {
                    insertTerm.CommandText = """
                        INSERT INTO TerminalSessionLogs (Id, SessionId, Sequence, IsAlternateScreen, Data, Cols, Rows, Timestamp)
                        VALUES ($id, $sessionId, $sequence, $alt, $data, $cols, $rows, $timestamp);
                        """;
                    var pId = insertTerm.Parameters.Add("$id", SqliteType.Integer);
                    var pSeq = insertTerm.Parameters.Add("$sequence", SqliteType.Integer);
                    var pAlt = insertTerm.Parameters.Add("$alt", SqliteType.Integer);
                    var pData = insertTerm.Parameters.Add("$data", SqliteType.Blob);
                    var pCols = insertTerm.Parameters.Add("$cols", SqliteType.Integer);
                    var pRows = insertTerm.Parameters.Add("$rows", SqliteType.Integer);
                    var pTs = insertTerm.Parameters.Add("$timestamp", SqliteType.Text);
                    insertTerm.Parameters.AddWithValue("$sessionId", session.Id);
                    foreach (var log in terminalLogs)
                    {
                        pId.Value = log.Id;
                        pSeq.Value = log.Sequence;
                        pAlt.Value = log.IsAlternateScreen ? 1 : 0;
                        pData.Value = log.Data;
                        pCols.Value = log.Cols;
                        pRows.Value = log.Rows;
                        pTs.Value = log.TimestampUtc.ToString("O");
                        await insertTerm.ExecuteNonQueryAsync(cancellationToken);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
            }

            var bytes = await File.ReadAllBytesAsync(tempPath, cancellationToken);
            return (bytes, session.Cli);
        }
        finally
        {
            // Pooling=false means sidecars don't normally linger, but delete -wal/-shm
            // defensively in case journaling ever leaves them beside the temp db.
            foreach (var path in new[] { tempPath, tempPath + "-wal", tempPath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[DebugBundle] Failed to delete temp bundle file {Path}", path);
                }
            }
        }
    }

    private static byte[] BrotliCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }
}
