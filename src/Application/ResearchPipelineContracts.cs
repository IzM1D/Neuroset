using Neuroset.Domain;

namespace Neuroset.Application;

public interface IParseltongueService
{
    string Transform(string input, ParseltongueSettings settings);
}

public interface IContextAdaptiveSamplingService
{
    SamplingContext Classify(string input);
    GenerationSettings Tune(GenerationSettings generation, string input);

    /// <summary>
    /// Tunes a request using the most recent conversation turns as weak context.
    /// The default implementation keeps third-party implementations source-compatible.
    /// </summary>
    GenerationSettings Tune(GenerationSettings generation, string input, IReadOnlyList<string>? conversationHistory) =>
        Tune(generation, input);

    /// <summary>Classifies a message while optionally considering recent history.</summary>
    SamplingContext Classify(string input, IReadOnlyList<string>? conversationHistory) =>
        Classify(input);
}

public interface IResponseTransformSession
{
    string Push(string delta);
    string Complete();
}

public interface ISemanticResponseTransformer
{
    string Transform(string content, SemanticTransformationSettings settings);
    IResponseTransformSession CreateSession(SemanticTransformationSettings settings);
    bool ContainsRefusal(string content, SemanticTransformationSettings settings);
}

public sealed record ModelRaceCandidateResult(
    ConnectionProfile Connection,
    LlmResponse? Response,
    double Score,
    string? Error = null);

public sealed record ModelRaceResult(
    ConnectionProfile Winner,
    LlmResponse Response,
    IReadOnlyList<ModelRaceCandidateResult> Candidates);

public interface IMultiModelRacingService
{
    Task<ModelRaceResult> RaceAsync(LlmRequest request, int modelCount, CancellationToken cancellationToken = default);
}
