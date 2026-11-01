using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// Opt-in checks of candidate cross-encoders against their published reference outputs (VIBE-257).
/// VIBERAILS_RERANKER_CHECK: label=directory|modelFile pairs separated by ';'.
/// </summary>
public sealed class CrossEncoderRerankerTests(ITestOutputHelper output)
{
    [Fact]
    public void CandidateModelsMatchTheirReferenceTokenizationAndScores()
    {
        var setting = Environment.GetEnvironmentVariable("VIBERAILS_RERANKER_CHECK");
        if (Environment.GetEnvironmentVariable("VIBERAILS_RUN_BENCHMARKS") != "1" || string.IsNullOrEmpty(setting))
            Assert.Skip("Opt-in: set VIBERAILS_RUN_BENCHMARKS=1 and VIBERAILS_RERANKER_CHECK=label=dir|model.onnx;...");
        foreach (var entry in setting.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (label, value) = (entry.Split('=', 2)[0], entry.Split('=', 2)[1]);
            var parts = value.Split('|');
            using var model = new CrossEncoderReranker(parts[0], 256, 2, parts.Length > 1 ? parts[1] : "model.onnx");
            var ids = model.EncodePair("Hello world!", "what is panda?", out var second);
            // ms-marco MiniLM model card: 8.607138, -4.320078.
            var berlin = model.Score("How many people live in Berlin?",
                ["Berlin had a population of 3,520,031 registered inhabitants in an area of 891.82 square kilometers.",
                 "Berlin is well known for its museums."]);
            // FlagEmbedding README pair: "hi" is far below the panda definition.
            var panda = model.Score("what is panda?", ["hi",
                "The giant panda (Ailuropoda melanoleuca), sometimes called a panda bear or simply panda, is a bear species endemic to China."]);
            output.WriteLine($"{label}: ids=[{string.Join(",", ids)}] second={second} berlin=[{berlin[0]:F4}, {berlin[1]:F4}] panda=[{panda[0]:F4}, {panda[1]:F4}]");
            Assert.True(berlin[0] > berlin[1] && panda[1] > panda[0], label);
        }
    }
}
