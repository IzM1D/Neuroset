using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Neuroset.Application;

namespace Neuroset.Tools;

public sealed record LocalEmbeddingOptions(string ModelDirectory)
{
    public const string ModelUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    public const string VocabularyUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";
    public static LocalEmbeddingOptions CreateDefault() => new(Path.Combine(Neuroset.Application.ApplicationPaths.DataDirectory, "Models", "all-MiniLM-L6-v2"));
}

public sealed class LocalMiniLmEmbeddingService(HttpClient httpClient, LocalEmbeddingOptions options) : IEmbeddingService, IDisposable
{
    private readonly Lazy<Task<ModelState>> _state = new(() => InitializeAsync(httpClient, options));
    public int Dimensions => 384;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var state = await _state.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Embed(state, text, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static float[] Embed(ModelState state, string text, CancellationToken cancellationToken)
    {
        var ids = state.Tokenizer.EncodeToIds(text, true, true, true).Take(256).Select(x => (long)x).ToArray();
        if (ids.Length == 0) ids = [101, 102];
        var attention = Enumerable.Repeat(1L, ids.Length).ToArray(); var tokenTypes = new long[ids.Length];
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attention, [1, ids.Length]))
        };
        if (state.Session.InputMetadata.ContainsKey("token_type_ids")) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypes, [1, ids.Length])));
        cancellationToken.ThrowIfCancellationRequested();
        using var results = state.Session.Run(inputs);
        var output = results[0].AsTensor<float>();
        var dimensions = output.Dimensions[^1]; var embedding = new float[dimensions];
        for (var token = 0; token < ids.Length; token++) for (var dimension = 0; dimension < dimensions; dimension++) embedding[dimension] += output[0, token, dimension];
        var norm = 0d;
        for (var i = 0; i < embedding.Length; i++) { embedding[i] /= ids.Length; norm += embedding[i] * embedding[i]; }
        norm = Math.Sqrt(norm); if (norm > 0) for (var i = 0; i < embedding.Length; i++) embedding[i] = (float)(embedding[i] / norm);
        return embedding;
    }

    private static async Task<ModelState> InitializeAsync(HttpClient client, LocalEmbeddingOptions options)
    {
        Directory.CreateDirectory(options.ModelDirectory);
        var modelPath = Path.Combine(options.ModelDirectory, "model.onnx"); var vocabularyPath = Path.Combine(options.ModelDirectory, "vocab.txt");
        await DownloadIfMissingAsync(client, new Uri(LocalEmbeddingOptions.ModelUrl), modelPath).ConfigureAwait(false);
        await DownloadIfMissingAsync(client, new Uri(LocalEmbeddingOptions.VocabularyUrl), vocabularyPath).ConfigureAwait(false);
        var tokenizer = BertTokenizer.Create(vocabularyPath, new BertOptions { LowerCaseBeforeTokenization = true, ApplyBasicTokenization = true });
        var session = new InferenceSession(modelPath, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        return new(session, tokenizer);
    }

    private static async Task DownloadIfMissingAsync(HttpClient client, Uri uri, string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 1024) return;
        var temporary = path + $".{Guid.NewGuid():N}.download";
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target).ConfigureAwait(false);
                await target.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() { if (_state.IsValueCreated && _state.Value.IsCompletedSuccessfully) _state.Value.Result.Dispose(); }
    private sealed record ModelState(InferenceSession Session, BertTokenizer Tokenizer) : IDisposable { public void Dispose() => Session.Dispose(); }
}
