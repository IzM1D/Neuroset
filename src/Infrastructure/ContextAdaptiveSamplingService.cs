using System.Text.RegularExpressions;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Infrastructure;

/// <summary>A scored context returned by the AutoTune classifier.</summary>
public sealed record AutoTuneContextScore(SamplingContext Context, int Score, int Percentage);

/// <summary>A pattern that contributed to an AutoTune decision.</summary>
public sealed record AutoTunePatternMatch(string Pattern, string Source);

/// <summary>Full, inspectable result of an AutoTune pass.</summary>
public sealed record AutoTuneResult(
    GenerationSettings Generation,
    SamplingContext DetectedContext,
    double Confidence,
    IReadOnlyList<AutoTuneContextScore> ContextScores,
    IReadOnlyList<AutoTunePatternMatch> PatternMatches,
    string Reasoning);

/// <summary>
/// Context-adaptive sampler ported from G0DM0D3's AutoTune module.
///
/// The current message has a 3x weight and the four most recent previous
/// messages have a 1x weight. Fixed strategies use a stable profile; Adaptive
/// blends a detected context profile with Balanced when the signal is weak.
/// </summary>
public sealed class ContextAdaptiveSamplingService : IContextAdaptiveSamplingService
{
    private sealed record SamplingProfile(
        double Temperature,
        double TopP,
        int TopK,
        double FrequencyPenalty,
        double PresencePenalty,
        double RepetitionPenalty);

    private sealed record ContextRule(SamplingContext Context, string Description, Regex[] Patterns);

    private static readonly IReadOnlyDictionary<AutoTuneStrategy, SamplingProfile> StrategyProfiles =
        new Dictionary<AutoTuneStrategy, SamplingProfile>
        {
            [AutoTuneStrategy.Precise] = new(.20, .85, 30, .30, .10, 1.10),
            [AutoTuneStrategy.Balanced] = new(.70, .90, 50, .10, .10, 1.00),
            [AutoTuneStrategy.Creative] = new(1.10, .95, 80, .40, .60, 1.15),
            [AutoTuneStrategy.Chaotic] = new(1.60, .98, 100, .70, .80, 1.25)
        };

    private static readonly IReadOnlyDictionary<SamplingContext, SamplingProfile> ContextProfiles =
        new Dictionary<SamplingContext, SamplingProfile>
        {
            // Keep coding defaults conservative for providers that use a
            // wider interpretation of temperature and top-p.
            [SamplingContext.Coding] = new(.20, .90, 25, .10, 0, 1.05),
            [SamplingContext.Creative] = new(1.15, .95, 85, .50, .70, 1.20),
            [SamplingContext.Analytical] = new(.40, .88, 40, .20, .15, 1.08),
            [SamplingContext.Factual] = new(.20, .85, 30, 0, 0, 1.05),
            [SamplingContext.Conversational] = new(.75, .90, 50, .10, .10, 1.00),
            [SamplingContext.Chaotic] = new(1.70, .99, 100, .80, .90, 1.30),
            [SamplingContext.General] = new(.70, .90, 50, .10, .10, 1.00)
        };

    private static readonly ContextRule[] Rules =
    [
        new(
            SamplingContext.Coding,
            "CODE: programming and software terms",
            [
                Create(@"\b(code|coding|function|class|variable|bug|error|debug|compile|syntax|api|endpoint|regex|algorithm|refactor|typescript|javascript|python|rust|html|css|sql|json|xml|import|export|return|async|await|promise|interface|type|const|let|var|unit\s+test|lint|npm|pip|cargo|git)\b"),
                Create(@"\x60{3}[\s\S]*\x60{3}"),
                Create(@"\b(fix|implement|write|create|build|deploy|test)\b[^\r\n]{0,200}\b(code|function|app|service|component|module)\b"),
                Create(@"[{}();=><]"),
                Create(@"\b(stack\s*overflow|github|repo|pull\s+request|commit|merge)\b"),
                Create(@"\b(код|программ|функци|класс|переменн|ошибк|отлад|компил|синтаксис|api|регекс|алгоритм|рефактор|тест|репозитор)\w*\b")
            ]),
        new(
            SamplingContext.Creative,
            "CREATIVE: writing, fiction, and roleplay terms",
            [
                Create(@"\b(write|story|poem|novel|scene|character|brainstorm|creative|imagine|rewrite|fiction|narrative|plot|dialogue|metaphor|lyrics|song|artistic|fantasy|dream|inspire|muse|prose|verse|haiku)\b"),
                Create(@"\b(describe|paint|envision|portray|illustrate|craft)\b[^\r\n]{0,200}\b(world|scene|character|feeling|emotion|atmosphere)\b"),
                Create(@"\b(roleplay|role-play|pretend|act\s+as|you\s+are\s+a)\b"),
                Create(@"\b(brainstorm|ideate|come\s+up\s+with|think\s+of|generate\s+ideas)\b"),
                Create(@"\b(рассказ|стих|роман|сцен|персонаж|творчес|придум|сюжет|диалог|фантаз|ролев)\w*\b")
            ]),
        new(
            SamplingContext.Analytical,
            "ANALYTICAL: analysis and research terms",
            [
                Create(@"\b(analy[sz]e|analysis|compare|contrast|evaluate|assess|examine|investigate|research|study|review|critique|breakdown|data|statistics|metrics|benchmark|measure)\b"),
                Create(@"\b(pros\s+and\s+cons|advantages|disadvantages|trade-?offs|implications|consequences)\b"),
                Create(@"\b(why|how\s+does|what\s+causes|explain|elaborate|clarify|define|summarize|overview)\b"),
                Create(@"\b(report|document|technical|specification|architecture|diagram|whitepaper)\b"),
                Create(@"\b(анализ|сравн|оцен|исслед|причин|объясн|определ|вывод|архитектур|спецификац)\w*\b")
            ]),
        new(
            SamplingContext.Factual,
            "FACTUAL: direct fact and definition questions",
            [
                Create(@"\b(what|when|where|who|define|explain|summari[sz]e|how\s+many|how\s+much|exactly)\b"),
                Create(@"^[\s]*(what|when|where|who|which|как|что|когда|где|кто|сколько)\b"),
                Create(@"\b(что|когда|где|кто|определ|объясн|сколько|каков)\w*\b")
            ]),
        new(
            SamplingContext.Conversational,
            "CHAT: casual conversation",
            [
                Create(@"\b(hey|hi|hello|sup|what's\s+up|how\s+are\s+you|thanks|thank\s+you|cool|nice|awesome|great|lol|haha)\b"),
                Create(@"\b(chat|talk|tell\s+me\s+about|what\s+do\s+you\s+think|opinion|feel|believe)\b"),
                Create(@"^[\s\S]{0,30}$"),
                Create(@"\b(привет|здравствуй|спасибо|класс|как\s+дела|поговор)\w*\b")
            ]),
        new(
            SamplingContext.Chaotic,
            "CHAOS: experimental or deliberately noisy terms",
            [
                Create(@"\b(chaos|random|wild|crazy|absurd|surreal|glitch|corrupt|break|destroy|unleash|madness|void|entropy)\b"),
                // Match zalgo-like sequences, not any ordinary z/a/l/g/o
                // character (a character class would create false positives).
                Create(@"(?:z\u0337|a\u0338|l\u0335|g\u0336|o\u0337)"),
                Create(@"\b(gl1tch|h4ck|pwn|1337|l33t)\b"),
                Create(@"(!{3,}|\?{3,}|\.{4,})"),
                Create(@"\b(хаос|случайн|безум|абсурд|сюрреал|глитч|энтроп)\w*\b")
            ])
    ];

    public SamplingContext Classify(string input) => Classify(input, null);

    public SamplingContext Classify(string input, IReadOnlyList<string>? conversationHistory)
    {
        var detection = Detect(input ?? string.Empty, conversationHistory ?? []);
        return detection.Context;
    }

    public GenerationSettings Tune(GenerationSettings generation, string input) => Tune(generation, input, null);

    public GenerationSettings Tune(
        GenerationSettings generation,
        string input,
        IReadOnlyList<string>? conversationHistory)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var normalized = generation.Normalize();
        if (!normalized.AutoTune.Enabled) return normalized;
        return Analyze(normalized, input, conversationHistory).Generation;
    }

    /// <summary>Runs AutoTune and returns both tuned settings and diagnostics.</summary>
    public AutoTuneResult Analyze(
        GenerationSettings generation,
        string input,
        IReadOnlyList<string>? conversationHistory = null)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var normalized = generation.Normalize();
        var history = conversationHistory ?? [];
        var detection = Detect(input ?? string.Empty, history);
        var strategy = normalized.AutoTune.Strategy;

        if (!normalized.AutoTune.Enabled)
        {
            return new AutoTuneResult(
                normalized,
                detection.Context,
                detection.Confidence,
                detection.Scores,
                detection.PatternMatches,
                "AutoTune disabled; original generation profile preserved");
        }

        var baseProfile = strategy == AutoTuneStrategy.Adaptive
            ? ResolveAdaptiveProfile(detection.Context, detection.Confidence)
            : StrategyProfiles.GetValueOrDefault(strategy, StrategyProfiles[AutoTuneStrategy.Balanced]);
        var tuned = ApplyProfile(normalized, baseProfile);

        var conversationLength = history.Count;
        if (conversationLength > 10)
        {
            var boost = Math.Min((conversationLength - 10) * .01, .15);
            tuned.RepetitionPenalty = Math.Min((tuned.RepetitionPenalty ?? 1) + boost, 2);
            tuned.FrequencyPenalty = Math.Clamp(tuned.FrequencyPenalty + boost * .5, -2, 2);
        }

        tuned = tuned.Normalize();
        var reasoning = strategy == AutoTuneStrategy.Adaptive
            ? $"Detected {ContextLabel(detection.Context)} context ({Math.Round(detection.Confidence * 100):0}% confidence)"
            : $"Strategy {strategy.ToString().ToUpperInvariant()} fixed profile applied";
        if (conversationLength > 10) reasoning += $"; long conversation factor ({conversationLength} messages) applied";

        return new AutoTuneResult(
            tuned,
            detection.Context,
            detection.Confidence,
            detection.Scores,
            detection.PatternMatches,
            reasoning);
    }

    private static GenerationSettings ApplyProfile(GenerationSettings source, SamplingProfile profile) => new()
    {
        SystemPrompt = source.SystemPrompt,
        PromptPreset = source.PromptPreset,
        Temperature = profile.Temperature,
        TopP = profile.TopP,
        TopK = profile.TopK,
        MaxTokens = source.MaxTokens,
        PresencePenalty = profile.PresencePenalty,
        FrequencyPenalty = profile.FrequencyPenalty,
        RepetitionPenalty = profile.RepetitionPenalty,
        Seed = source.Seed,
        StopSequences = [.. source.StopSequences],
        EnableTools = source.EnableTools,
        Parseltongue = source.Parseltongue.Normalize(),
        SemanticTransform = source.SemanticTransform.Normalize(),
        AutoTune = source.AutoTune.Normalize(),
        MultiModelRacing = source.MultiModelRacing.Normalize()
    };

    private static SamplingProfile ResolveAdaptiveProfile(SamplingContext context, double confidence)
    {
        var contextual = ContextProfiles.GetValueOrDefault(context, ContextProfiles[SamplingContext.General]);
        if (confidence >= .6) return contextual;
        return Blend(contextual, StrategyProfiles[AutoTuneStrategy.Balanced], 1 - confidence);
    }

    private static SamplingProfile Blend(SamplingProfile a, SamplingProfile b, double weight)
    {
        var w = Math.Clamp(weight, 0, 1);
        var inverse = 1 - w;
        return new(
            a.Temperature * inverse + b.Temperature * w,
            a.TopP * inverse + b.TopP * w,
            (int)Math.Round(a.TopK * inverse + b.TopK * w),
            a.FrequencyPenalty * inverse + b.FrequencyPenalty * w,
            a.PresencePenalty * inverse + b.PresencePenalty * w,
            a.RepetitionPenalty * inverse + b.RepetitionPenalty * w);
    }

    private sealed record Detection(
        SamplingContext Context,
        double Confidence,
        IReadOnlyList<AutoTuneContextScore> Scores,
        IReadOnlyList<AutoTunePatternMatch> PatternMatches);

    private static Detection Detect(string message, IReadOnlyList<string> history)
    {
        var scores = Enum.GetValues<SamplingContext>().ToDictionary(context => context, _ => 0);
        var matches = new List<AutoTunePatternMatch>();

        ScoreText(message, "current", 3, scores, matches);
        foreach (var previous in history.TakeLast(4))
            ScoreText(previous ?? string.Empty, "history", 1, scores, matches);

        var total = scores.Values.Sum();
        if (total == 0)
        {
            var fallback = Enum.GetValues<SamplingContext>()
                .Select(context => new AutoTuneContextScore(context, context == SamplingContext.Conversational ? 1 : 0, context == SamplingContext.Conversational ? 100 : 0))
                .OrderByDescending(item => item.Score)
                .ToArray();
            return new(SamplingContext.Conversational, .5, fallback,
                [new("DEFAULT: no patterns matched", "current")]);
        }

        var ordered = scores
            .Select(pair => new AutoTuneContextScore(pair.Key, pair.Value, (int)Math.Round(pair.Value * 100d / total)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => ContextPriority(item.Context))
            .ToArray();
        var best = ordered[0];
        return new(best.Context, Math.Min(best.Score / (double)total, 1), ordered, matches);
    }

    private static void ScoreText(
        string text,
        string source,
        int weight,
        IDictionary<SamplingContext, int> scores,
        ICollection<AutoTunePatternMatch> matches)
    {
        foreach (var rule in Rules)
        {
            foreach (var pattern in rule.Patterns)
            {
                if (!pattern.IsMatch(text)) continue;
                // A short-message hint is useful only when no stronger
                // domain signal was found. Without this guard, a concise
                // coding request would be blended with the chat profile.
                if (rule.Context == SamplingContext.Conversational &&
                    pattern.ToString().Contains("0,30", StringComparison.Ordinal) &&
                    scores.Any(pair => pair.Key != SamplingContext.Conversational && pair.Value > 0))
                    continue;
                scores[rule.Context] += weight;
                // Keep diagnostics compact while retaining the first useful
                // current match and a few history matches.
                if (matches.Count(item => item.Source == source) < (source == "current" ? 8 : 4))
                    matches.Add(new($"{rule.Description}: {Describe(pattern)}", source));
            }
        }
    }

    private static int ContextPriority(SamplingContext context) => context switch
    {
        SamplingContext.Coding => 0,
        SamplingContext.Creative => 1,
        SamplingContext.Analytical => 2,
        SamplingContext.Factual => 3,
        SamplingContext.Chaotic => 4,
        SamplingContext.Conversational => 5,
        _ => 6
    };

    private static string ContextLabel(SamplingContext context) => context switch
    {
        SamplingContext.Coding => "coding",
        SamplingContext.Creative => "creative",
        SamplingContext.Analytical => "analytical",
        SamplingContext.Factual => "factual",
        SamplingContext.Chaotic => "chaotic",
        SamplingContext.Conversational => "conversational",
        _ => "general"
    };

    private static string Describe(Regex pattern)
    {
        var source = pattern.ToString();
        if (source.Contains("\\x60{3}", StringComparison.Ordinal)) return "code block";
        if (source.Contains("[{}", StringComparison.Ordinal)) return "code syntax";
        if (source.Contains("0,30", StringComparison.Ordinal)) return "short message";
        if (source.Contains("!{3", StringComparison.Ordinal)) return "excessive punctuation";
        var match = Regex.Match(source, @"\\b\(([^)]+)\)", RegexOptions.CultureInvariant);
        if (match.Success)
            return string.Join(", ", match.Groups[1].Value.Split('|').Take(3));
        return source.Length > 28 ? source[..28] + "..." : source;
    }

    private static Regex Create(string pattern) => new(
        pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
}
