using System.Text;
using System.Text.RegularExpressions;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Providers;

public sealed class SemanticResponseTransformer : ISemanticResponseTransformer
{
    private const int StreamingTailCharacters = 96;
    private const int MaximumBufferedPrefixCharacters = 512;
    private const int MaximumStreamingSentenceCharacters = 2_048;

    // These rules intentionally mirror G0DM0D3/src/stm/modules.ts.
    private static readonly Regex DirectPrefix = Create(
        @"\A(?:(?:sure,?\s*)|(?:of course,?\s*)|(?:certainly,?\s*)|(?:absolutely,?\s*)|(?:great question!?\s*)|(?:that's a great question!?\s*)|(?:i'd be happy to help(?: you)?(?: with that)?[.!]?\s*)|(?:let me help you with that[.!]?\s*)|(?:i understand[.!]?\s*)|(?:thanks for asking[.!]?\s*))+" );

    // Refusal detection is a Neuroset extension used for regeneration. STM does not remove refusal text.
    private static readonly Regex RefusalSentence = Create(
        @"(?:\b(?:i\s+(?:cannot|can't|won't|am unable to)|i(?:'|\u2019)?m\s+(?:unable to|not comfortable|sorry)|i\s+must\s+decline|i\s+have\s+to\s+refuse)\b|\b(?:не\s+(?:буду|стану|могу)|(?:писать|создавать|сделать|помогать)\s+не\s+(?:буду|стану)|отказываюсь|вынужден(?:а|ы)?\s+отказаться)\b)");

    private static readonly Regex[] HedgeMarkers =
    [
        Create(@"\bI think\s+"),
        Create(@"\bI believe\s+"),
        Create(@"\bperhaps\s+"),
        Create(@"\bmaybe\s+"),
        Create(@"\bIt seems like\s+"),
        Create(@"\bIt appears that\s+"),
        Create(@"\bprobably\s+"),
        Create(@"\bpossibly\s+"),
        Create(@"\bI would say\s+"),
        Create(@"\bIn my opinion,?\s*"),
        Create(@"\bFrom my perspective,?\s*")
    ];

    private static readonly (Regex Pattern, string Replacement)[] CasualReplacements =
    [
        (CreateSensitive(@"\bHowever\b"), "But"), (CreateSensitive(@"\bTherefore\b"), "So"),
        (CreateSensitive(@"\bFurthermore\b"), "Also"), (CreateSensitive(@"\bAdditionally\b"), "Plus"),
        (CreateSensitive(@"\bNevertheless\b"), "Still"), (CreateSensitive(@"\bConsequently\b"), "So"),
        (CreateSensitive(@"\bMoreover\b"), "Also"), (CreateSensitive(@"\bUtilize\b"), "Use"),
        (CreateSensitive(@"\butilize\b"), "use"), (CreateSensitive(@"\bPurchase\b"), "Buy"),
        (CreateSensitive(@"\bpurchase\b"), "buy"), (CreateSensitive(@"\bObtain\b"), "Get"),
        (CreateSensitive(@"\bobtain\b"), "get"), (CreateSensitive(@"\bCommence\b"), "Start"),
        (CreateSensitive(@"\bcommence\b"), "start"), (CreateSensitive(@"\bTerminate\b"), "End"),
        (CreateSensitive(@"\bterminate\b"), "end"), (Create(@"\bPrior to\b"), "Before"),
        (Create(@"\bSubsequent to\b"), "After"), (Create(@"\bIn order to\b"), "To"),
        (Create(@"\bDue to the fact that\b"), "Because"), (Create(@"\bAt this point in time\b"), "Now"),
        (Create(@"\bIn the event that\b"), "If")
    ];

    public string Transform(string content, SemanticTransformationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = (settings ?? new SemanticTransformationSettings()).Normalize();
        return TransformFragment(content, normalized, normalized.DirectModeEnabled);
    }

    public IResponseTransformSession CreateSession(SemanticTransformationSettings settings) =>
        new StreamingSession((settings ?? new SemanticTransformationSettings()).Normalize());

    public bool ContainsRefusal(string content, SemanticTransformationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var normalized = (settings ?? new SemanticTransformationSettings()).Normalize();
        return (normalized.HedgeReducerEnabled || normalized.DirectModeEnabled || normalized.CasualModeEnabled) && RefusalSentence.IsMatch(content);
    }

    private static string TransformFragment(string content, SemanticTransformationSettings settings, bool applyDirectMode, bool trimStart = false)
    {
        var result = content;
        if (settings.HedgeReducerEnabled) result = ApplyHedgeReducer(result);
        if (applyDirectMode) result = ApplyDirectMode(result);
        if (settings.CasualModeEnabled) result = ApplyCasualMode(result);
        return trimStart ? result.TrimStart() : result;
    }

    private static string ApplyHedgeReducer(string value)
    {
        foreach (var marker in HedgeMarkers) value = marker.Replace(value, string.Empty);
        // Capitalize each line without consuming the newline separating paragraphs.
        return Regex.Replace(value, @"^[^\S\r\n]*([a-z])", match => match.Groups[1].Value.ToUpperInvariant(), RegexOptions.Multiline);
    }

    private static string ApplyDirectMode(string value)
    {
        value = DirectPrefix.Replace(value, string.Empty, 1);
        return Regex.Replace(value, @"^\s*([a-z])", match => match.Groups[1].Value.ToUpperInvariant());
    }

    private static string ApplyCasualMode(string value)
    {
        foreach (var (pattern, replacement) in CasualReplacements) value = pattern.Replace(value, replacement);
        return value;
    }

    private static Regex Create(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static Regex CreateSensitive(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private sealed class StreamingSession(SemanticTransformationSettings settings) : IResponseTransformSession
    {
        private readonly StringBuilder _pending = new();
        private readonly bool _enabled = settings.DirectModeEnabled || settings.HedgeReducerEnabled || settings.CasualModeEnabled;
        private bool _prefixReleased = !settings.DirectModeEnabled;

        public string Push(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return string.Empty;
            if (!_enabled) return delta;
            _pending.Append(delta);
            if (!_prefixReleased)
            {
                var buffered = _pending.ToString();
                if (!CanResolvePrefix(buffered)) return string.Empty;
                _pending.Clear();
                _pending.Append(ApplyDirectMode(buffered));
                settings.DirectModeEnabled = false;
                _prefixReleased = true;
            }
            if (_pending.Length <= StreamingTailCharacters) return string.Empty;
            var cut = FindStableCut(_pending, _pending.Length - StreamingTailCharacters);
            if (cut <= 0) return string.Empty;
            var source = _pending.ToString(0, cut);
            _pending.Remove(0, cut);
            return TransformFragment(source, settings, false, false);
        }

        public string Complete()
        {
            if (!_enabled || _pending.Length == 0) return string.Empty;
            var source = _pending.ToString();
            _pending.Clear();
            var result = TransformFragment(source, settings, !_prefixReleased, !_prefixReleased);
            settings.DirectModeEnabled = false;
            _prefixReleased = true;
            return result;
        }

        private static int FindStableCut(StringBuilder value, int preferred)
        {
            for (var index = preferred; index > 0; index--)
            {
                var character = value[index - 1];
                if (character == '\n') return index;
                if ((character is '.' or '!' or '?') && (index == value.Length || char.IsWhiteSpace(value[index]))) return index;
            }
            if (value.Length <= MaximumStreamingSentenceCharacters) return 0;
            for (var index = preferred; index > 0; index--)
                if (char.IsWhiteSpace(value[index - 1])) return index;
            return preferred;
        }

        private static bool CanResolvePrefix(string value)
        {
            if (value.Length >= MaximumBufferedPrefixCharacters || value.Contains("\n\n", StringComparison.Ordinal) || value.Contains("\r\n\r\n", StringComparison.Ordinal)) return true;
            var trimmed = value.TrimStart();
            if (trimmed.Length < 12) return false;
            return !(trimmed.StartsWith("sure", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("of course", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("certainly", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("absolutely", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("great question", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("that's a great question", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("i'd be happy to help", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("let me help you with that", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("i understand", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("thanks for asking", StringComparison.OrdinalIgnoreCase));
        }
    }
}
