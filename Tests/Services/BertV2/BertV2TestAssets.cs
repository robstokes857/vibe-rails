using System.IO.Compression;

namespace Tests.Services.BertV2;

internal static class BertV2TestAssets
{
    public static async Task MaterializeRuntimeFilesAsync(string runtimeDir)
    {
        var bundled = GetBundledModelDirectory();
        Directory.CreateDirectory(runtimeDir);

        await using (var source = File.OpenRead(Path.Combine(bundled, "model.onnx.gz")))
        await using (var gzip = new GZipStream(source, CompressionMode.Decompress))
        await using (var dest = File.Create(Path.Combine(runtimeDir, "model.onnx")))
        {
            await gzip.CopyToAsync(dest);
        }

        File.Copy(
            Path.Combine(bundled, "vocab.txt"),
            Path.Combine(runtimeDir, "vocab.txt"),
            overwrite: true);
    }

    public static string GetBundledModelDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var sourceCandidate = Path.Combine(current.FullName, "VibeRails", "Models", "BertV2");
            if (ContainsBundledModel(sourceCandidate))
            {
                return sourceCandidate;
            }

            var outputCandidate = Path.Combine(current.FullName, "Models", "BertV2");
            if (ContainsBundledModel(outputCandidate))
            {
                return outputCandidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the BertV2 model assets for tests.");
    }

    private static bool ContainsBundledModel(string directory)
    {
        return Directory.Exists(directory)
            && File.Exists(Path.Combine(directory, "model.onnx.gz"))
            && File.Exists(Path.Combine(directory, "vocab.txt"));
    }
}
