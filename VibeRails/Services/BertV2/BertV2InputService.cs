using VibeRails.Services.UserInOut;

namespace VibeRails.Services.BertV2;

public class BertV2InputService : IBertV2InputService
{
    private readonly IBertV2BgeEmbedder _embedder;
    private readonly IBertV2VectorStore _store;
    private readonly Lock _writeLock = new();

    public BertV2InputService(IBertV2BgeEmbedder embedder, IBertV2VectorStore store)
    {
        _embedder = embedder;
        _store = store;
    }

    public void Capture(string sessionId, long userInputId, string inputText)
    {
        // InputEtlFilter.Process handles null/whitespace, normalizes the text, drops
        // noise commands, and — most importantly — refuses to return anything that
        // matches a known secret pattern. A null return means "do not embed."
        var captureText = InputEtlFilter.Process(inputText);
        if (string.IsNullOrWhiteSpace(captureText))
            return;

        var embedding = _embedder.GenerateEmbedding(captureText);

        lock (_writeLock)
        {
            _store.AddOrUpdate(BertDocumentId.Create(sessionId, userInputId), captureText, embedding);
        }
    }
}
