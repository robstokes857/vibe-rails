using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace VibeRails.Services.Bert;

/// <summary>
/// Generates sentence embeddings using ONNX Runtime on CPU.
/// </summary>
public sealed class BertEmbedder : IDisposable
{
    private const int MaxSequenceLength = 512;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _requiresTokenTypeIds;

    public BertEmbedder(string modelPath, string vocabPath)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        ArgumentNullException.ThrowIfNull(vocabPath);

        using var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        sessionOptions.AppendExecutionProvider_CPU(1);
        _session = new InferenceSession(modelPath, sessionOptions);
        _requiresTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");

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
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be empty", nameof(text));

        // Tokenize with special tokens ([CLS], [SEP]).
        string? normalizedText;
        int charsConsumed;
        var encoded = _tokenizer.EncodeToIds(
            text,
            MaxSequenceLength,
            addSpecialTokens: true,
            out normalizedText,
            out charsConsumed);
        var tokenCount = encoded.Count;

        var inputIds = new long[MaxSequenceLength];
        var attentionMask = new long[MaxSequenceLength];

        for (int i = 0; i < tokenCount && i < MaxSequenceLength; i++)
        {
            inputIds[i] = encoded[i];
            attentionMask[i] = 1;
        }

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, MaxSequenceLength]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, MaxSequenceLength]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
        };

        if (_requiresTokenTypeIds)
        {
            var tokenTypeIds = new long[MaxSequenceLength];
            var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, MaxSequenceLength]);
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor));
        }

        using var results = _session.Run(inputs);
        var lastHiddenState = results.First().AsTensor<float>();

        return MeanPool(lastHiddenState, attentionMask);
    }

    private static float[] MeanPool(Tensor<float> lastHiddenState, long[] attentionMask)
    {
        var seqLen = lastHiddenState.Dimensions[1];
        var hiddenSize = lastHiddenState.Dimensions[2];
        var embedding = new float[hiddenSize];

        float maskSum = 0;
        for (int i = 0; i < seqLen; i++)
            maskSum += attentionMask[i];

        if (maskSum < 1e-9f)
            maskSum = 1e-9f;

        for (int i = 0; i < seqLen; i++)
        {
            if (attentionMask[i] == 0)
                continue;

            for (int j = 0; j < hiddenSize; j++)
            {
                embedding[j] += lastHiddenState[0, i, j];
            }
        }

        for (int j = 0; j < hiddenSize; j++)
            embedding[j] /= maskSum;

        Normalize(embedding);

        return embedding;
    }

    private static void Normalize(float[] vector)
    {
        double norm = 0;
        for (int i = 0; i < vector.Length; i++)
            norm += vector[i] * (double)vector[i];

        norm = Math.Sqrt(norm);
        if (norm < 1e-12)
            return;

        for (int i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}

