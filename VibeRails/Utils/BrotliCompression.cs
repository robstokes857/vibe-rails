using System.IO.Compression;

namespace VibeRails.Utils;

public static class BrotliCompression
{
    public static async Task CompressAsync(string inputPath, string outputPath, CompressionLevel level = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(outputPath);

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        await using var sourceStream = File.OpenRead(inputPath);
        await using var destStream = File.Create(outputPath);
        await using var brotliStream = new BrotliStream(destStream, level);

        await sourceStream.CopyToAsync(brotliStream);
    }

    public static async Task DecompressAsync(string inputPath, string outputPath)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(outputPath);


        await using var sourceStream = File.OpenRead(inputPath);
        await using var brotliStream = new BrotliStream(sourceStream, CompressionMode.Decompress);
        await using var destStream = File.Create(outputPath);

        await brotliStream.CopyToAsync(destStream);
    }
}
