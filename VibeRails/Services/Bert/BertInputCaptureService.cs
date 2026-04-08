using VibeRails.Services.BertV2;
using VibeRails.Utils;

namespace VibeRails.Services.Bert;

/// <summary>
/// Captures the user's prose into a BERT vector index. Only the user's
/// input text is embedded — session/user-input/git/file metadata lives in
/// state.db and is joined back at query time by BertExplorerService.
/// </summary>
public sealed class BertInputCaptureService : IBertInputCaptureService, IDisposable
{
    private readonly ILogger<BertInputCaptureService> _logger;
    private readonly bool _enabled;
    private readonly string _modelDirectory;
    private readonly string _dataDirectory;

    private readonly Lock _stateLock = new();
    private bool _initAttempted;
    private BertV2BgeEmbedder? _embedder;
    private BertSqliteVectorStore? _store;

    public BertInputCaptureService(IConfiguration configuration, ILogger<BertInputCaptureService> logger)
    {
        _logger = logger;

        var section = configuration.GetSection("VibeRails:BertCapture");
        _enabled = section.GetValue<bool?>("Enabled") ?? true;

        var installRoot = PathConstants.GetInstallDirPath();

        var configuredModelDirectory = section["ModelDirectory"];
        _modelDirectory = string.IsNullOrWhiteSpace(configuredModelDirectory)
            ? Path.Combine(installRoot, PathConstants.MODELS_SUBDIR, "bertv2")
            : configuredModelDirectory;

        var configuredDataDirectory = section["DataDirectory"];
        _dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? Path.Combine(installRoot, PathConstants.VECTOR_SUBDIR, "bert")
            : configuredDataDirectory;

        if (!_enabled)
        {
            _logger.LogInformation("[BERT] Input capture disabled via configuration.");
        }
    }

    public Task CaptureAsync(
        string sessionId,
        long userInputId,
        string inputText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_enabled || string.IsNullOrWhiteSpace(inputText))
            return Task.CompletedTask;

        if (!EnsureInitialized())
            return Task.CompletedTask;

        var captureText = SanitizeText(inputText);
        if (string.IsNullOrWhiteSpace(captureText))
            return Task.CompletedTask;

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

                var dbPath = Path.Combine(_dataDirectory, "bert_user_text_vectors.db");
                _embedder = new BertV2BgeEmbedder(modelPath, vocabPath);
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

    private static string SanitizeText(string value)
    {
        var normalized = value.Replace("\0", string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();

        return normalized.Length == 0 ? string.Empty : normalized;
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
