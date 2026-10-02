using System.Text;
using System.Text.RegularExpressions;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Infrastructure;

/// <summary>
/// C# port of G0DM0D3's trigger-scoped Parseltongue engine.
///
/// The source engine detects configured trigger words first, then applies one
/// selected technique to each matching occurrence. The optional seed is a
/// Neuroset addition that makes the random source reproducible when desired.
/// </summary>
public sealed class ParseltongueService : IParseltongueService
{
    private static readonly string[] DefaultTriggers =
    [
        "hack", "exploit", "bypass", "crack", "break", "attack", "penetrate",
        "inject", "manipulate", "override", "disable", "circumvent", "evade",
        "malware", "virus", "trojan", "payload", "shellcode", "rootkit",
        "keylogger", "backdoor", "vulnerability", "exploit",
        "weapon", "bomb", "explosive", "poison", "drug", "synthesize",
        "jailbreak", "unlock", "root", "sudo", "admin", "privilege",
        "phishing", "scam", "impersonate", "deceive", "fraud",
        "nsfw", "explicit", "uncensored", "unfiltered", "unrestricted",
        "ignore", "disregard", "forget", "pretend", "roleplay",
        "character", "act as", "you are now", "new identity",
        // Technical wording is frequently present in requests that users
        // want routed through the same robustness path.
        "code", "код"
    ];

    private static readonly IReadOnlyDictionary<char, string[]> LeetMap =
        new Dictionary<char, string[]>
        {
            ['a'] = ["4", "@", "∂", "λ"],
            ['b'] = ["8", "|3", "ß", "13"],
            ['c'] = ["(", "<", "¢", "©"],
            ['d'] = ["|)", "|>", "đ"],
            ['e'] = ["3", "€", "£", "∑"],
            ['f'] = ["|=", "ƒ", "ph"],
            ['g'] = ["9", "6", "&"],
            ['h'] = ["#", "|-|", "}{"],
            ['i'] = ["1", "!", "|", "¡"],
            ['j'] = ["_|", "]", "¿"],
            ['k'] = ["|<", "|{", "κ"],
            ['l'] = ["1", "|", "£", "|_"],
            ['m'] = ["|V|", "/\\/\\", "µ"],
            ['n'] = ["|\\|", "/\\/", "η"],
            ['o'] = ["0", "()", "°", "ø"],
            ['p'] = ["|*", "|>", "þ"],
            ['q'] = ["0_", "()_", "ℚ"],
            ['r'] = ["|2", "®", "12"],
            ['s'] = ["5", "$", "§", "∫"],
            ['t'] = ["7", "+", "†", "⊤"],
            ['u'] = ["|_|", "µ", "ü"],
            ['v'] = ["\\/", "√"],
            ['w'] = ["\\/\\/", "vv", "ω"],
            ['x'] = ["><", "×", "}{"],
            ['y'] = ["`/", "¥", "γ"],
            ['z'] = ["2", "7_", "ℤ"]
        };

    private static readonly IReadOnlyDictionary<char, string[]> UnicodeHomoglyphs =
        new Dictionary<char, string[]>
        {
            ['a'] = ["а", "ɑ", "α", "ａ"],
            ['b'] = ["Ь", "ｂ", "ḅ"],
            ['c'] = ["с", "ϲ", "ⅽ", "ｃ"],
            ['d'] = ["ԁ", "ⅾ", "ｄ"],
            ['e'] = ["е", "ė", "ẹ", "ｅ"],
            ['f'] = ["ƒ", "ｆ"],
            ['g'] = ["ɡ", "ｇ"],
            ['h'] = ["һ", "ḥ", "ｈ"],
            ['i'] = ["і", "ι", "ｉ"],
            ['j'] = ["ϳ", "ｊ"],
            ['k'] = ["κ", "ｋ"],
            ['l'] = ["ӏ", "ⅼ", "ｌ"],
            ['m'] = ["м", "ｍ"],
            ['n'] = ["ո", "ｎ"],
            ['o'] = ["о", "ο", "ｏ"],
            ['p'] = ["р", "ρ", "ｐ"],
            ['s'] = ["ѕ", "ｓ"],
            ['t'] = ["τ", "ｔ"],
            ['u'] = ["υ", "ｕ"],
            ['v'] = ["ν", "ｖ"],
            ['w'] = ["ѡ", "ｗ"],
            ['x'] = ["х", "ｘ"],
            ['y'] = ["у", "γ", "ｙ"],
            ['z'] = ["ᴢ", "ｚ"]
        };

    private static readonly string[] ZeroWidthCharacters = ["\u200B", "\u200C", "\u200D", "\uFEFF"];

    public string Transform(string input, ParseltongueSettings settings)
        => TransformDetailed(input, settings).TransformedText;

    public ParseltongueTransformResult TransformDetailed(string input, ParseltongueSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = (settings ?? new ParseltongueSettings()).Normalize();
        if (!normalized.Enabled)
            return new(input, input, [], normalized.Technique, []);

        var triggers = DetectTriggers(input, normalized.CustomTriggers);
        if (triggers.Count == 0)
            return new(input, input, [], normalized.Technique, []);

        var random = normalized.Seed is { } seed ? new Random(seed) : Random.Shared;
        var transformed = input;
        var transformations = new List<ParseltongueTransformation>();

        // Longest first prevents a phrase such as "act as" from being split
        // by a shorter trigger introduced by a custom list.
        foreach (var trigger in triggers.OrderByDescending(value => value.Length))
        {
            var pattern = CreateWholeWordRegex(trigger);
            transformed = pattern.Replace(transformed, match =>
            {
                var value = ObfuscateWord(match.Value, normalized.Technique, normalized.Intensity, random);
                transformations.Add(new(match.Value, value, normalized.Technique));
                return value;
            });
        }

        return new(input, transformed, triggers, normalized.Technique, transformations);
    }

    public IReadOnlyList<string> DetectTriggers(string? text, IEnumerable<string>? customTriggers = null)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var allTriggers = DefaultTriggers
            .Concat(customTriggers ?? [])
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        foreach (var trigger in allTriggers)
        {
            if (CreateWholeWordRegex(trigger).IsMatch(text)) found.Add(trigger);
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> DefaultTriggerWords => DefaultTriggers;

    private static Regex CreateWholeWordRegex(string trigger)
    {
        // JS's source uses \b around every trigger. Escape custom input so it
        // cannot turn into a regular expression.
        return new(
            $"\\b({Regex.Escape(trigger)})\\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));
    }

    private static string ObfuscateWord(string word, ParseltongueTechnique technique, ParseltongueIntensity intensity, Random random)
        => technique switch
        {
            ParseltongueTechnique.Leetspeak => ApplyLeetspeak(word, intensity, random),
            ParseltongueTechnique.Unicode => ApplyUnicode(word, intensity, random),
            ParseltongueTechnique.Zwj => ApplyZeroWidth(word, intensity, random),
            ParseltongueTechnique.MixedCase => ApplyMixedCase(word, intensity, random),
            ParseltongueTechnique.Phonetic => ApplyPhonetic(word),
            ParseltongueTechnique.Random => ApplyRandom(word, intensity, random),
            _ => word
        };

    private static string ApplyLeetspeak(string word, ParseltongueIntensity intensity, Random random)
    {
        var chars = word.ToCharArray();
        var count = TransformCount(chars.Length, intensity);
        var indices = SelectIndices(chars, count, LeetMap).ToHashSet();
        var result = new StringBuilder(word.Length + indices.Count * 2);
        for (var index = 0; index < chars.Length; index++)
        {
            if (indices.Contains(index))
            {
                var options = LeetMap[LookupKey(chars[index])];
                result.Append(options[random.Next(options.Length)]);
            }
            else
            {
                result.Append(chars[index]);
            }
        }
        return result.ToString();
    }

    private static string ApplyUnicode(string word, ParseltongueIntensity intensity, Random random)
    {
        var chars = word.ToCharArray();
        var count = TransformCount(chars.Length, intensity);
        var indices = SelectIndices(chars, count, UnicodeHomoglyphs).ToHashSet();
        var result = new StringBuilder(word.Length + indices.Count);
        for (var index = 0; index < chars.Length; index++)
        {
            var source = chars[index];
            if (!indices.Contains(index))
            {
                result.Append(source);
                continue;
            }

            var options = UnicodeHomoglyphs[LookupKey(source)];
            var replacement = options[random.Next(options.Length)];
            result.Append(source == char.ToUpperInvariant(source)
                ? replacement.ToUpperInvariant()
                : replacement);
        }
        return result.ToString();
    }

    private static string ApplyZeroWidth(string word, ParseltongueIntensity intensity, Random random)
    {
        var chars = word.ToCharArray();
        var insertCount = intensity switch
        {
            ParseltongueIntensity.Light => 1,
            ParseltongueIntensity.Medium => (int)Math.Ceiling(chars.Length / 2d),
            _ => Math.Max(0, chars.Length - 1)
        };
        var result = new StringBuilder(word.Length + insertCount);
        var insertions = 0;
        for (var index = 0; index < chars.Length; index++)
        {
            result.Append(chars[index]);
            if (index < chars.Length - 1 && insertions < insertCount)
            {
                result.Append(ZeroWidthCharacters[random.Next(ZeroWidthCharacters.Length)]);
                insertions++;
            }
        }
        return result.ToString();
    }

    private static string ApplyMixedCase(string word, ParseltongueIntensity intensity, Random random)
    {
        var chars = word.ToCharArray();
        if (chars.Length == 0) return word;
        switch (intensity)
        {
            case ParseltongueIntensity.Light:
                var index = random.Next(chars.Length);
                chars[index] = char.ToUpperInvariant(chars[index]);
                break;
            case ParseltongueIntensity.Medium:
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = i % 2 == 0 ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
                break;
            default:
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = random.NextDouble() > .5 ? char.ToUpperInvariant(chars[i]) : char.ToLowerInvariant(chars[i]);
                break;
        }
        return new string(chars);
    }

    private static string ApplyPhonetic(string word)
    {
        var result = Regex.Replace(word, "ph", "f", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = Regex.Replace(result, "ck", "k", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = Regex.Replace(result, "x", "ks", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = Regex.Replace(result, "qu", "kw", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = Regex.Replace(result, "c(?=[eiy])", "s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(result, "c", "k", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ApplyRandom(string word, ParseltongueIntensity intensity, Random random)
        => random.Next(4) switch
        {
            0 => ApplyLeetspeak(word, intensity, random),
            1 => ApplyUnicode(word, intensity, random),
            2 => ApplyZeroWidth(word, intensity, random),
            _ => ApplyMixedCase(word, intensity, random)
        };

    private static int TransformCount(int length, ParseltongueIntensity intensity)
        => intensity switch
        {
            ParseltongueIntensity.Light => 1,
            ParseltongueIntensity.Medium => (int)Math.Ceiling(length / 2d),
            _ => length
        };

    private static IReadOnlyList<int> SelectIndices<T>(char[] chars, int count, IReadOnlyDictionary<char, T> map)
    {
        var result = new List<int>(Math.Max(0, count));
        var step = Math.Max(1, (int)Math.Floor(chars.Length / (double)Math.Max(1, count)));
        for (var index = 0; index < chars.Length && result.Count < count; index += step)
        {
            if (map.ContainsKey(LookupKey(chars[index]))) result.Add(index);
        }
        if (result.Count < count)
        {
            for (var index = 0; index < chars.Length && result.Count < count; index++)
            {
                if (!result.Contains(index) && map.ContainsKey(LookupKey(chars[index]))) result.Add(index);
            }
        }
        return result;
    }

    private static char LookupKey(char value) => char.ToLowerInvariant(value) switch
    {
        // Common Cyrillic lookalikes found in Russian prompts.
        '\u0430' => 'a',
        '\u0431' => 'b',
        '\u0441' => 'c',
        '\u0434' => 'd',
        '\u0435' => 'e',
        '\u0444' => 'f',
        '\u0433' => 'g',
        '\u0445' => 'h',
        '\u0456' => 'i',
        '\u043a' => 'k',
        '\u043c' => 'm',
        '\u043d' => 'n',
        '\u043e' => 'o',
        '\u043f' => 'p',
        '\u0440' => 'r',
        '\u0442' => 't',
        '\u0443' => 'y',
        '\u0432' => 'v',
        '\u0437' => 'z',
        _ => char.ToLowerInvariant(value)
    };
}

public sealed record ParseltongueTransformation(
    string Original,
    string Transformed,
    ParseltongueTechnique Technique);

public sealed record ParseltongueTransformResult(
    string OriginalText,
    string TransformedText,
    IReadOnlyList<string> TriggersFound,
    ParseltongueTechnique TechniqueUsed,
    IReadOnlyList<ParseltongueTransformation> Transformations);
