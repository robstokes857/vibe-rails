using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace VibeRails.Services.BertV2;

/// <summary>
/// A local ONNX cross-encoder (VIBE-257). The tokenizer file next to the model selects the input
/// layout: <c>vocab.txt</c> is uncased BERT WordPiece (the ms-marco MiniLM family),
/// <c>sentencepiece.bpe.model</c> is XLM-RoBERTa (bge-reranker) and <c>spm.model</c> is DeBERTa-v3.
/// Each pair runs at its own length; padding a batch to its longest passage costs more than it saves
/// on two CPU threads.
/// </summary>
public sealed class CrossEncoderReranker : ISearchReranker
{
    /// <summary>Queries rarely need more; a longer one is cut so the passage keeps its share of the window.</summary>
    private const int MaxQueryTokens = 64;

    private readonly InferenceSession session;
    private readonly Func<string, int, IReadOnlyList<int>> encode;
    private readonly int cls, sep;
    private readonly bool xlmRoberta;
    private readonly bool wantsTokenTypes;
    private readonly int maxLength;

    /// <param name="directory">Holds <c>model.onnx</c> and one of the tokenizer files listed on the class.</param>
    /// <param name="maxLength">Tokens per query-passage pair, special tokens included.</param>
    /// <param name="threads">ONNX intra-op threads; the embedder uses two (VIBE-170).</param>
    /// <param name="modelFile">The model file name inside <paramref name="directory"/>.</param>
    public CrossEncoderReranker(string directory, int maxLength = 256, int threads = 2, string modelFile = "model.onnx")
    {
        if (maxLength is < 32 or > 512) throw new ArgumentOutOfRangeException(nameof(maxLength));
        this.maxLength = maxLength;
        var modelPath = Path.Combine(directory, modelFile);
        // A missing model is the normal "not installed" case: report it before any native load.
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The search re-ranking model is not installed.", modelPath);
        var vocab = Path.Combine(directory, "vocab.txt");
        var xlmr = Path.Combine(directory, "sentencepiece.bpe.model");
        var deberta = Path.Combine(directory, "spm.model");
        if (File.Exists(vocab))
        {
            using var stream = File.OpenRead(vocab);
            var bert = BertTokenizer.Create(stream, new BertOptions
            {
                LowerCaseBeforeTokenization = true, SeparatorToken = "[SEP]", ClassificationToken = "[CLS]",
                UnknownToken = "[UNK]", PaddingToken = "[PAD]"
            });
            encode = (text, max) => bert.EncodeToIds(text, max, addSpecialTokens: false, out _, out _);
            (cls, sep) = (bert.ClassificationTokenId, bert.SeparatorTokenId);
        }
        else if (File.Exists(xlmr) || File.Exists(deberta))
        {
            xlmRoberta = File.Exists(xlmr);
            using var stream = File.OpenRead(xlmRoberta ? xlmr : deberta);
            var spm = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
            var unknown = spm.UnknownId;
            // Hugging Face's XLM-R ids are fairseq ids: <s>=0, <pad>=1, </s>=2, <unk>=3, and every other
            // SentencePiece id shifted up by one. DeBERTa-v3 uses the SentencePiece ids as they are.
            encode = xlmRoberta
                ? (text, max) => spm.EncodeToIds(text, false, false, max, out _, out _).Select(id => id == unknown ? 3 : id + 1).ToArray()
                : (text, max) => spm.EncodeToIds(text, false, false, max, out _, out _);
            (cls, sep) = xlmRoberta ? (0, 2) : (1, 2);
        }
        else throw new FileNotFoundException("No cross-encoder tokenizer file was found.", vocab);

        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(threads, 1, 8),
            InterOpNumThreads = 1,
            EnableCpuMemArena = false,
            EnableMemoryPattern = true
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AppendExecutionProvider_CPU(0);
        session = new InferenceSession(modelPath, options);
        wantsTokenTypes = session.InputMetadata.ContainsKey("token_type_ids");
    }

    public bool IsAvailable => true;

    public float[] Score(string query, IReadOnlyList<string> passages)
    {
        var scores = new float[passages.Count];
        if (passages.Count == 0) return scores;
        var question = encode(query, MaxQueryTokens);
        for (var i = 0; i < passages.Count; i++)
            scores[i] = Run(Pair(question, passages[i] ?? "", out var second), second);
        return scores;
    }

    /// <summary>The model input for one pair, exposed so tests can compare it with the reference tokenizer.</summary>
    internal long[] EncodePair(string query, string passage, out int secondSegment) =>
        Pair(encode(query, MaxQueryTokens), passage, out secondSegment);

    private long[] Pair(IReadOnlyList<int> question, string passage, out int secondSegment)
    {
        // BERT: [CLS] q [SEP] p [SEP]. XLM-R: <s> q </s></s> p </s>.
        var separators = xlmRoberta ? 4 : 3;
        var tokens = encode(passage, Math.Max(1, maxLength - question.Count - separators));
        var ids = new List<long>(question.Count + tokens.Count + separators) { cls };
        ids.AddRange(question.Select(id => (long)id));
        ids.Add(sep);
        if (xlmRoberta) ids.Add(sep);
        secondSegment = ids.Count;
        ids.AddRange(tokens.Select(id => (long)id));
        ids.Add(sep);
        return ids.ToArray();
    }

    private float Run(long[] ids, int secondSegment)
    {
        var length = ids.Length;
        var mask = new long[length];
        Array.Fill(mask, 1L);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, length]))
        };
        if (wantsTokenTypes)
        {
            var types = new long[length];
            // XLM-R has a single segment type; BERT marks the passage as segment 1.
            if (!xlmRoberta) for (var i = secondSegment; i < length; i++) types[i] = 1;
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types, [1, length])));
        }
        using var results = session.Run(inputs);
        var logits = results.First().AsTensor<float>();
        // One relevance logit, or two-class logits whose difference orders the same way.
        return logits.Length == 1 ? logits.GetValue(0) : logits.GetValue(1) - logits.GetValue(0);
    }

    public void Dispose() => session.Dispose();
}
