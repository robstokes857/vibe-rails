using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace VibeRails.Services.BertV2;

/// <summary>
/// Generates embeddings using BGE-small-en-v1.5 via ONNX Runtime.
/// Uses CLS pooling and the model's uncased WordPiece vocabulary.
/// </summary>
public class BertV2BgeEmbedder : IBertV2BgeEmbedder
{
    private const int MaxSequenceLength = 512;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _requiresTokenTypeIds;
    private long _invocationCount;

    public BertV2BgeEmbedder(string modelPath, string vocabPath)
    {
        Serilog.Log.Information(
            "[BertEmbedder] Constructing. modelPath={ModelPath} vocabPath={VocabPath} pid={Pid}",
            modelPath, vocabPath, Environment.ProcessId);

        // This session lives in every root and in any `vb mcp` that has searched, and runs one
        // query or one backfill chunk at a time. ORT's defaults give it an intra-op pool the size
        // of the machine that spin-waits between operators, and a CPU arena that keeps peak buffers.
        // Measured on 32 logical cores (VIBE-170): the defaults spent 108 s of CPU on a 4.5 s run;
        // two non-spinning threads spent about 20 s, with 75 MB less private memory. A query
        // embed went from about 3 ms to 4 ms, a 512-token chunk from 32 ms to 132 ms. Backfill pace
        // is set by the job's batch cap and follow-up delay, so a full re-embed should take an
        // estimated 20% longer.
        // The memory pattern stays on: without the arena it holds no buffers between runs
        // (8 MB over 400 distinct lengths), and turning it off made long chunks 1.5x slower again.
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            EnableCpuMemArena = false,
            EnableMemoryPattern = true
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AppendExecutionProvider_CPU(0);

        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _session = new InferenceSession(modelPath, options);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex,
                "[BertEmbedder] InferenceSession load FAILED. modelPath={ModelPath} durationMs={DurationMs}",
                modelPath, loadSw.ElapsedMilliseconds);
            throw;
        }
        _requiresTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");

        Serilog.Log.Information(
            "[BertEmbedder] InferenceSession loaded. durationMs={DurationMs} inputs={Inputs} requiresTokenTypeIds={RequiresTokenTypeIds}",
            loadSw.ElapsedMilliseconds,
            string.Join(",", _session.InputMetadata.Keys),
            _requiresTokenTypeIds);

        using var vocabStream = File.OpenRead(vocabPath);
        _tokenizer = BertTokenizer.Create(vocabStream, new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            SeparatorToken = "[SEP]",
            ClassificationToken = "[CLS]",
            UnknownToken = "[UNK]",
            PaddingToken = "[PAD]",
        });
    }

    public float[] GenerateEmbedding(string text)
    {
        var encoded = _tokenizer.EncodeToIds(text, MaxSequenceLength, addSpecialTokens: true, out _, out _);
        var tokenCount = encoded.Count;
        // Right-sized seq dim: shape the tensor to actual token count (capped by tokenizer at MaxSequenceLength).
        // Padding to a fixed 512 for short inputs wastes ~13x attention compute (O(seq²) per layer).
        var seqLen = Math.Max(tokenCount, 1);

        var inputIds = new long[seqLen];
        var attentionMask = new long[seqLen];

        for (int i = 0; i < tokenCount; i++)
        {
            inputIds[i] = encoded[i];
            attentionMask[i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, seqLen])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [1, seqLen])),
        };

        if (_requiresTokenTypeIds)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(new long[seqLen], [1, seqLen])));
        }

        var invocation = System.Threading.Interlocked.Increment(ref _invocationCount);
        var runSw = System.Diagnostics.Stopwatch.StartNew();
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results;
        try
        {
            results = _session.Run(inputs);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex,
                "[BertEmbedder] session.Run FAILED. invocation={Invocation} tokenCount={TokenCount} durationMs={DurationMs} pid={Pid}",
                invocation, tokenCount, runSw.ElapsedMilliseconds, Environment.ProcessId);
            throw;
        }

        // Heartbeat every 100th invocation so we can see the embedder is alive without drowning the log.
        if (invocation == 1 || invocation % 100 == 0)
        {
            Serilog.Log.Information(
                "[BertEmbedder] session.Run ok. invocation={Invocation} tokenCount={TokenCount} durationMs={DurationMs}",
                invocation, tokenCount, runSw.ElapsedMilliseconds);
        }

        using (results)
        {
            var lastHiddenState = results.First().AsTensor<float>();

            // CLS pooling: the [CLS] token at position 0 captures the sentence representation
            var hiddenSize = lastHiddenState.Dimensions[2];
            var embedding = new float[hiddenSize];
            for (int j = 0; j < hiddenSize; j++)
                embedding[j] = lastHiddenState[0, 0, j];

            Normalize(embedding);
            return embedding;
        }
    }

    private static void Normalize(float[] vector)
    {
        double norm = 0;
        for (int i = 0; i < vector.Length; i++)
            norm += vector[i] * (double)vector[i];

        norm = Math.Sqrt(norm);
        if (norm < 1e-12) return;

        for (int i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }

    public void Dispose() => _session.Dispose();
}
