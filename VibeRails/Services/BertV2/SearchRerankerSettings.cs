using VibeRails.Utils;

namespace VibeRails.Services.BertV2;

/// <summary>
/// Where the optional search re-ranking model lives (VIBE-257): <c>model.onnx</c> and its tokenizer
/// file. Measured choice: the ms-marco TinyBERT-L2-v2 cross-encoder, which uses the embedder's uncased
/// WordPiece vocabulary. Search keeps its hybrid order while neither directory holds a model.
/// </summary>
public static class SearchRerankerSettings
{
    private const string ModelFile = "model.onnx";

    /// <summary>
    /// <c>models/reranker</c> beside the embedder. Setup and the install scripts extract the model here
    /// and replace it when its bytes change, so every <c>vb</c> process of this user shares it.
    /// </summary>
    public static string InstalledDirectory =>
        Path.Combine(PathConstants.GetInstallDirPath(), PathConstants.MODELS_SUBDIR, "reranker");

    /// <summary>
    /// <c>Models/Reranker</c> beside the executable (VIBE-258). The VS Code extension stages the extracted
    /// model there because it never writes to the install directory. In a release archive this folder
    /// holds only <c>model.onnx.gz</c>, which the installers extract to <see cref="InstalledDirectory"/>.
    /// </summary>
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "Models", "Reranker");

    /// <summary>The lazily loaded, idle-released owner registered for root and stdio MCP hosts.</summary>
    public static ISearchReranker CreateOwner() => CreateOwner(InstalledDirectory, BundledDirectory);

    /// <summary>Uses the first directory, in order, that holds <c>model.onnx</c>; re-checked on every search.</summary>
    internal static ISearchReranker CreateOwner(params string[] directories) => new IdleSearchReranker(
        () => new CrossEncoderReranker(FindModelDirectory(directories) ?? directories[0]),
        () => FindModelDirectory(directories) is not null);

    internal static string? FindModelDirectory(IEnumerable<string> directories) =>
        directories.FirstOrDefault(directory => File.Exists(Path.Combine(directory, ModelFile)));
}
