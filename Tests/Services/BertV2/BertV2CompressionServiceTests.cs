using VibeRails.Utils;
using Xunit;

namespace Tests.Services.BertV2;

public class BrotliCompressionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"brotli_test_{Guid.NewGuid():N}");

    public BrotliCompressionTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task CompressModel_ProducesSmallerFile()
    {
        var inputPath = Path.Combine(_tempDir, "model.onnx");
        var compressedPath = inputPath + ".br";
        try
        {
            var content = string.Concat(Enumerable.Repeat("fake ONNX model data block\n", 20_000));
            await File.WriteAllTextAsync(inputPath, content);
            await BrotliCompression.CompressAsync(inputPath, compressedPath);

            var originalSize = new FileInfo(inputPath).Length;
            var compressedSize = new FileInfo(compressedPath).Length;
            var ratio = (double)compressedSize / originalSize * 100;
            Console.WriteLine($"model.onnx: {originalSize:N0} -> {compressedSize:N0} bytes ({ratio:F1}%)");

            Assert.True(File.Exists(compressedPath));
            Assert.True(compressedSize < originalSize, "Compressed file should be smaller");
        }
        catch (Exception ex)
        {
            Assert.Fail($"Compression threw an exception: {ex}");
        }
    }

    [Fact]
    public async Task CompressAndDecompressModel_RoundTripsExactly()
    {
        var inputPath = Path.Combine(_tempDir, "model.onnx");
        var compressedPath = inputPath + ".br";
        var restoredPath = Path.Combine(_tempDir, "model_restored.onnx");
        try
        {
            var originalBytes = Enumerable.Range(0, 65_536)
                .Select(i => (byte)(i % 251))
                .ToArray();
            await File.WriteAllBytesAsync(inputPath, originalBytes);
            await BrotliCompression.CompressAsync(inputPath, compressedPath);
            await BrotliCompression.DecompressAsync(compressedPath, restoredPath);

            var restoredBytes = await File.ReadAllBytesAsync(restoredPath);
            Assert.Equal(originalBytes, restoredBytes);
        }
        finally
        {
            if (File.Exists(compressedPath)) File.Delete(compressedPath);
            if (File.Exists(restoredPath)) File.Delete(restoredPath);
        }
    }

    [Fact]
    public async Task CompressVocab_ProducesSmallerFile()
    {
        var inputPath = Path.Combine(_tempDir, "vocab.txt");
        var compressedPath = inputPath + ".br";
        try
        {
            var content = string.Concat(Enumerable.Repeat("[CLS]\n[SEP]\n[PAD]\n[UNK]\n", 15_000));
            await File.WriteAllTextAsync(inputPath, content);
            await BrotliCompression.CompressAsync(inputPath, compressedPath);

            var originalSize = new FileInfo(inputPath).Length;
            var compressedSize = new FileInfo(compressedPath).Length;
            var ratio = (double)compressedSize / originalSize * 100;
            Console.WriteLine($"vocab.txt: {originalSize:N0} -> {compressedSize:N0} bytes ({ratio:F1}%)");

            Assert.True(compressedSize < originalSize);
        }
        finally
        {
            if (File.Exists(compressedPath)) File.Delete(compressedPath);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
