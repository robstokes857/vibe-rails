using VibeRails.Utils;

namespace VibeRails.Services.BertV2;

/// <summary>
/// Where the optional search re-ranking model lives (VIBE-257): <c>models/reranker</c> beside the
/// embedder, holding <c>model.onnx</c> and its tokenizer file. Measured choice: the ms-marco
/// TinyBERT-L2-v2 cross-encoder, which uses the embedder's uncased WordPiece vocabulary.
/// Search keeps its hybrid order whenever the directory is empty.
/// </summary>
public static class SearchRerankerSettings
{
    public static string ModelDirectory =>
        Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.MODELS_SUBDIR, "reranker");

    /// <summary>The lazily loaded, idle-released owner registered for root and stdio MCP hosts.</summary>
    public static ISearchReranker CreateOwner() => new IdleSearchReranker(() => new CrossEncoderReranker(ModelDirectory),
        () => File.Exists(Path.Combine(ModelDirectory, "model.onnx")));
}
