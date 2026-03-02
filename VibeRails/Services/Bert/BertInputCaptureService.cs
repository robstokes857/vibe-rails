using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.Bert;

/// <summary>
/// Captures user inputs and file-change context into a BERT vector index.
/// </summary>
public sealed class BertInputCaptureService : IBertInputCaptureService, IDisposable
{
    private const int DefaultMaxDiffCharsPerFile = 2000;
    private const int MaxFilesPerCapture = 40;

    private readonly ILogger<BertInputCaptureService> _logger;
    private readonly bool _enabled;
    private readonly string _modelDirectory;
    private readonly string _dataDirectory;
    private readonly int _maxDiffCharsPerFile;

    private readonly Lock _stateLock = new();
    private bool _initAttempted;
    private BertEmbedder? _embedder;
    private BertSqliteVectorStore? _store;

    public BertInputCaptureService(IConfiguration configuration, ILogger<BertInputCaptureService> logger)
    {
        _logger = logger;

        var section = configuration.GetSection("VibeRails:BertCapture");
        _enabled = section.GetValue<bool?>("Enabled") ?? true;

        var installRoot = PathConstants.GetInstallDirPath();

        var configuredModelDirectory = section["ModelDirectory"];
        var bundledModelDirectory = Path.Combine(AppContext.BaseDirectory, "Models");
        _modelDirectory = string.IsNullOrWhiteSpace(configuredModelDirectory)
            ? (Directory.Exists(bundledModelDirectory)
                ? bundledModelDirectory
                : Path.Combine(installRoot, "models", "bert"))
            : configuredModelDirectory;

        var configuredDataDirectory = section["DataDirectory"];
        _dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? Path.Combine(installRoot, PathConstants.VECTOR_SUBDIR, "bert")
            : configuredDataDirectory;

        _maxDiffCharsPerFile = section.GetValue<int?>("MaxDiffCharsPerFile") ?? DefaultMaxDiffCharsPerFile;
        if (_maxDiffCharsPerFile < 256)
            _maxDiffCharsPerFile = 256;

        if (!_enabled)
        {
            _logger.LogInformation("[BERT] Input capture disabled via configuration.");
        }
    }

    public Task CaptureAsync(
        string sessionId,
        long userInputId,
        string inputText,
        string? gitCommitHash,
        IReadOnlyList<FileChangeInfo> fileChanges,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_enabled || string.IsNullOrWhiteSpace(inputText))
            return Task.CompletedTask;

        if (!EnsureInitialized())
            return Task.CompletedTask;

        var captureText = BuildCaptureText(sessionId, userInputId, inputText, gitCommitHash, fileChanges);
        var documentId = $"{sessionId}:{userInputId}";

        try
        {
            lock (_stateLock)
            {
                if (_embedder is null || _store is null)
                    return Task.CompletedTask;

                var embedding = _embedder.GenerateEmbedding(captureText);
                _store.AddOrUpdate(documentId, captureText, embedding);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BERT] Failed to capture user input for session {SessionId}, input {UserInputId}", sessionId, userInputId);
        }

        return Task.CompletedTask;
    }

    private bool EnsureInitialized()
    {
        lock (_stateLock)
        {
            if (_embedder != null && _store != null)
                return true;

            if (_initAttempted)
                return false;

            _initAttempted = true;

            try
            {
                var modelPath = Path.Combine(_modelDirectory, "model.onnx");
                var vocabPath = Path.Combine(_modelDirectory, "vocab.txt");

                if (!File.Exists(modelPath) || !File.Exists(vocabPath))
                {
                    _logger.LogWarning(
                        "[BERT] Model files not found. Capture disabled. Expected model at {ModelPath} and vocab at {VocabPath}.",
                        modelPath,
                        vocabPath);
                    return false;
                }

                Directory.CreateDirectory(_dataDirectory);

                var dbPath = Path.Combine(_dataDirectory, "bert_input_vectors.db");
                _embedder = new BertEmbedder(modelPath, vocabPath);
                _store = new BertSqliteVectorStore(dbPath);

                _logger.LogInformation("[BERT] Input capture initialized. ModelDir={ModelDir}, DataDir={DataDir}", _modelDirectory, _dataDirectory);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BERT] Failed to initialize BERT input capture. Capture disabled for this process.");
                _embedder?.Dispose();
                _store?.Dispose();
                _embedder = null;
                _store = null;
                return false;
            }
        }
    }

    private string BuildCaptureText(
        string sessionId,
        long userInputId,
        string inputText,
        string? gitCommitHash,
        IReadOnlyList<FileChangeInfo> fileChanges)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"session_id: {sessionId}");
        sb.AppendLine($"user_input_id: {userInputId}");

        if (!string.IsNullOrWhiteSpace(gitCommitHash))
            sb.AppendLine($"git_commit: {gitCommitHash}");

        sb.AppendLine("user_text:");
        sb.AppendLine(SanitizeText(inputText));

        if (fileChanges.Count == 0)
        {
            sb.AppendLine("file_changes: none");
            return sb.ToString();
        }

        sb.AppendLine("file_changes:");

        var changeCount = Math.Min(fileChanges.Count, MaxFilesPerCapture);
        for (int i = 0; i < changeCount; i++)
        {
            var change = fileChanges[i];
            sb.Append("- ").Append(change.FilePath)
                .Append(" | type=").Append(change.ChangeType);

            if (change.LinesAdded.HasValue || change.LinesDeleted.HasValue)
            {
                sb.Append(" | +").Append(change.LinesAdded ?? 0)
                    .Append(" -").Append(change.LinesDeleted ?? 0);
            }

            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(change.DiffContent))
            {
                sb.AppendLine("diff:");
                sb.AppendLine(Truncate(change.DiffContent, _maxDiffCharsPerFile));
            }
        }

        if (fileChanges.Count > MaxFilesPerCapture)
        {
            sb.AppendLine($"... {fileChanges.Count - MaxFilesPerCapture} additional file changes omitted");
        }

        return sb.ToString();
    }

    private static string SanitizeText(string value)
    {
        var normalized = value.Replace("\0", string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();

        return normalized.Length == 0 ? string.Empty : normalized;
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;

        return value[..maxChars] + "\n... [truncated]";
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _embedder?.Dispose();
            _store?.Dispose();
            _embedder = null;
            _store = null;
        }
    }
}



