using System.Text.RegularExpressions;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Agent;

/// <summary>
/// Sends the same request to several configured connections concurrently and
/// chooses the most useful successful response.
/// </summary>
public sealed class MultiModelRacingService(
    ILLMProviderFactory providerFactory,
    IConnectionRepository connections,
    ISemanticResponseTransformer responseTransformer) : IMultiModelRacingService
{
    private static readonly Regex HeaderPattern = Create(@"(?m)^#{1,3}\s");
    private static readonly Regex ListPattern = Create(@"(?m)^[\s]*[-*•]\s");
    private static readonly Regex CodeBlockPattern = Create(@"\x60{3}");
    private static readonly Regex RefusalPattern = Create(
        @"(?:i\s+(?:cannot|can't|won't|am unable to)|i(?:'|\u2019)m unable to|cannot assist|i\s+must\s+decline|i\s+have\s+to\s+refuse|i(?:'|\u2019)m sorry|i\s+apolog(?:ize|ise)|as an ai|as a language model|it(?:'|\u2019)s important to note|instead,\s+i\s+can|\u043d\u0435\s+\u043c\u043e\u0433\u0443|\u043d\u0435\s+\u0431\u0443\u0434\u0443|\u043d\u0435\s+\u0441\u0442\u0430\u043d\u0443|\u043f\u0438\u0441\u0430\u0442\u044c\s+\u043d\u0435\s+(?:\u0431\u0443\u0434\u0443|\u0441\u0442\u0430\u043d\u0443)|\u043e\u0442\u043a\u0430\u0437\u044b\u0432\u0430\u044e\u0441\u044c|\u0432\u044b\u043d\u0443\u0436\u0434\u0435\u043d\s+\u043e\u0442\u043a\u0430\u0437\u0430\u0442\u044c)");
    private static readonly Regex PreamblePattern = Create(
        @"^\s*(?:sure|of course|certainly|absolutely|great question|that's a great question|i(?:'d| would) be happy to help|let me help you|thanks for asking)\b");

    public async Task<ModelRaceResult> RaceAsync(
        LlmRequest request,
        int modelCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var candidates = await SelectCandidatesAsync(request.Connection, modelCount, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
            throw new InvalidOperationException("No valid model connections are available for the parallel race.");

        var rawGeneration = request.Generation?.Normalize();
        var semanticSettings = rawGeneration?.SemanticTransform?.Normalize() ?? new SemanticTransformationSettings();
        var candidateGeneration = WithoutSemanticTransform(rawGeneration);
        var userQuery = request.Messages
            .LastOrDefault(message => message.Role == MessageRole.User)?.Content ?? string.Empty;

        var tasks = candidates
            .Select(connection => CompleteCandidateAsync(
                request with { Connection = connection, Generation = candidateGeneration },
                userQuery,
                cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var winner = results
            .Where(result => result.Response is not null)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Connection.Id == request.Connection.Id ? 0 : 1)
            .FirstOrDefault();

        if (winner?.Response is null)
        {
            var detail = string.Join("; ", results.Select(result => $"{result.Connection.Name}: {result.Error}"));
            throw new InvalidOperationException($"Every model in the parallel race failed. {detail}");
        }

        // Transform only the selected response. Scoring raw output preserves
        // the distinction between a genuinely useful answer and a refusal
        // whose markers would otherwise have been removed.
        var transformed = winner.Response with
        {
            Content = responseTransformer.Transform(winner.Response.Content, semanticSettings)
        };
        return new ModelRaceResult(winner.Connection, transformed, results);
    }

    private async Task<IReadOnlyList<ConnectionProfile>> SelectCandidatesAsync(
        ConnectionProfile primary,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        var count = Math.Clamp(requestedCount, 2, 8);
        var saved = await connections.ListAsync(cancellationToken).ConfigureAwait(false);
        var ordered = new[] { primary }
            .Concat(saved
                .Where(item => item.Id != primary.Id)
                .OrderByDescending(item => item.IsActive)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ConnectionProfile>(count);
        foreach (var connection in ordered)
        {
            if (string.IsNullOrWhiteSpace(connection.BaseUrl) || string.IsNullOrWhiteSpace(connection.Model))
                continue;
            var key = $"{connection.BaseUrl.TrimEnd('/')}\n{connection.Model.Trim()}";
            if (!keys.Add(key))
                continue;
            result.Add(connection);
            if (result.Count == count)
                break;
        }
        return result;
    }

    private async Task<ModelRaceCandidateResult> CompleteCandidateAsync(
        LlmRequest request,
        string userQuery,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await providerFactory.Create(request.Connection)
                .CompleteAsync(request with { Stream = false }, cancellationToken)
                .ConfigureAwait(false);
            return new(request.Connection, response, Score(response, userQuery));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(request.Connection, null, double.NegativeInfinity, exception.Message);
        }
    }

    /// <summary>
    /// Scores a response on substance, structure, directness and relevance.
    /// The public helper is useful for diagnostics and deterministic tests.
    /// </summary>
    public static int ScoreResponse(string? content, string? userQuery)
    {
        var value = content?.Trim() ?? string.Empty;
        if (value.Length < 10)
            return 0;

        // Length (0..25), with diminishing returns.
        var score = Math.Min(value.Length / 40d, 25d);

        // Structure (0..20): headings, lists and complete fenced blocks.
        var headers = HeaderPattern.Matches(value).Count;
        var listItems = ListPattern.Matches(value).Count;
        var codeBlocks = CodeBlockPattern.Matches(value).Count / 2d;
        score += Math.Min(headers * 3 + listItems * 1.5 + codeBlocks * 5, 20);

        // Anti-refusal (0..25).
        var refusalCount = RefusalPattern.Matches(value).Count;
        score += Math.Max(25 - refusalCount * 8, 0);

        // Directness (0..15).
        score += PreamblePattern.IsMatch(value) ? 8 : 15;

        // Relevance (0..15). Match complete query words to avoid rewarding
        // arbitrary character-level overlap.
        var queryWords = ExtractQueryWords(userQuery);
        if (queryWords.Count == 0)
        {
            score += 7.5;
        }
        else
        {
            var lowerContent = value.ToLowerInvariant();
            var matched = queryWords.Count(word => lowerContent.Contains(word, StringComparison.Ordinal));
            score += matched / (double)queryWords.Count * 15;
        }

        return (int)Math.Clamp(Math.Round(score), 0, 100);
    }

    private static double Score(LlmResponse response, string userQuery)
    {
        var score = ScoreResponse(response.Content, userQuery);
        if (response.ToolCalls.Count > 0)
        {
            score = Math.Min(100, score + 20 + response.ToolCalls.Count(call =>
                !string.IsNullOrWhiteSpace(call.Name) &&
                !string.IsNullOrWhiteSpace(call.ArgumentsJson)) * 3);
        }
        if (string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            score = Math.Max(0, score - 8);
        return score;
    }

    private static HashSet<string> ExtractQueryWords(string? query)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
                     query?.ToLowerInvariant() ?? string.Empty,
                     @"[\p{L}\p{N}]{4,}"))
            result.Add(match.Value);
        return result;
    }

    private static GenerationSettings? WithoutSemanticTransform(GenerationSettings? source)
    {
        if (source is null) return null;
        return new GenerationSettings
        {
            SystemPrompt = source.SystemPrompt,
            PromptPreset = source.PromptPreset,
            Temperature = source.Temperature,
            TopP = source.TopP,
            TopK = source.TopK,
            MaxTokens = source.MaxTokens,
            PresencePenalty = source.PresencePenalty,
            FrequencyPenalty = source.FrequencyPenalty,
            RepetitionPenalty = source.RepetitionPenalty,
            Seed = source.Seed,
            StopSequences = [.. source.StopSequences],
            EnableTools = source.EnableTools,
            Parseltongue = source.Parseltongue.Normalize(),
            SemanticTransform = new SemanticTransformationSettings(),
            AutoTune = source.AutoTune.Normalize(),
            MultiModelRacing = source.MultiModelRacing.Normalize()
        }.Normalize();
    }

    private static Regex Create(string pattern) => new(
        pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
}
