using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neuroset.Storage;
using Neuroset.Tools;

namespace Neuroset.Tests;

public sealed class EmbeddingIntegrationTests
{
    [Fact]
    [Trait("Category", "Network")]
    public async Task MiniLm_Downloads_And_Produces_Semantic_Embeddings()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NEUROSET_RUN_MODEL_TESTS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var embeddings = new LocalMiniLmEmbeddingService(httpClient, LocalEmbeddingOptions.CreateDefault());

        var first = await embeddings.EmbedAsync("A Windows desktop application for chatting with an AI assistant.");
        var related = await embeddings.EmbedAsync("A local PC client that lets a user talk to a language model.");
        var unrelated = await embeddings.EmbedAsync("Fresh oranges and lemons are sold at the market.");

        first.Should().HaveCount(embeddings.Dimensions);
        first.Should().OnlyContain(value => float.IsFinite(value));
        VectorLength(first).Should().BeApproximately(1d, 0.001d);
        Cosine(first, related).Should().BeGreaterThan(Cosine(first, unrelated));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Knowledge_Pipeline_Indexes_And_Searches_A_Local_Document()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NEUROSET_RUN_MODEL_TESTS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var testDirectory = Path.Combine(Path.GetTempPath(), "NeurosetKnowledgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            var documentPath = Path.Combine(testDirectory, "manual.md");
            await File.WriteAllTextAsync(documentPath, "Neuroset is a local Windows desktop client. It uses WinUI 3 and can connect to OpenAI-compatible language model APIs.\n\nThe garden contains roses and tulips.");

            var database = new SqliteDatabase(new DatabaseOptions(Path.Combine(testDirectory, "knowledge.db")), NullLogger<SqliteDatabase>.Instance);
            await database.InitializeAsync();
            var repository = new SqliteKnowledgeRepository(database);
            var extractor = new CompositeTextExtractor([new PlainTextExtractor(), new DocxTextExtractor(), new PdfTextExtractor()]);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var embeddings = new LocalMiniLmEmbeddingService(httpClient, LocalEmbeddingOptions.CreateDefault());
            var knowledge = new KnowledgeService(extractor, embeddings, repository);

            var source = await knowledge.IndexAsync(documentPath);
            var results = await knowledge.SearchAsync("Which UI framework does the desktop AI client use?");

            source.Status.Should().Be("ready");
            source.ChunkCount.Should().BeGreaterThan(0);
            results.Should().NotBeEmpty();
            results[0].Content.Should().Contain("WinUI 3");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, true);
            }
        }
    }

    private static double VectorLength(float[] value) => Math.Sqrt(value.Sum(component => component * component));

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
        }

        return dot;
    }
}
