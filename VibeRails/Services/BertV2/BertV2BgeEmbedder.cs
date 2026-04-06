using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace VibeRails.Services.BertV2;

/// <summary>
/// Generates embeddings using BGE-small-en-v1.5 via ONNX Runtime.
/// Uses CLS pooling and case-sensitive tokenization.
/// </summary>
public class BertV2BgeEmbedder : IBertV2BgeEmbedder
{
    private const int MaxSequenceLength = 512;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _requiresTokenTypeIds;

    public BertV2BgeEmbedder(string modelPath, string vocabPath)
    {
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        options.AppendExecutionProvider_CPU(1);
        _session = new InferenceSession(modelPath, options);
        _requiresTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");

        using var vocabStream = File.OpenRead(vocabPath);
        _tokenizer = BertTokenizer.Create(vocabStream, new BertOptions
        {
            LowerCaseBeforeTokenization = false,
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

        var inputIds = new long[MaxSequenceLength];
        var attentionMask = new long[MaxSequenceLength];

        for (int i = 0; i < tokenCount; i++)
        {
            inputIds[i] = encoded[i];
            attentionMask[i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, MaxSequenceLength])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [1, MaxSequenceLength])),
        };

        if (_requiresTokenTypeIds)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(new long[MaxSequenceLength], [1, MaxSequenceLength])));
        }

        using var results = _session.Run(inputs);
        var lastHiddenState = results.First().AsTensor<float>();

        // CLS pooling: the [CLS] token at position 0 captures the sentence representation
        var hiddenSize = lastHiddenState.Dimensions[2];
        var embedding = new float[hiddenSize];
        for (int j = 0; j < hiddenSize; j++)
            embedding[j] = lastHiddenState[0, 0, j];

        Normalize(embedding);
        return embedding;
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
