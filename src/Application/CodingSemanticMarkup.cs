using System.Text;
using System.Text.RegularExpressions;

namespace Neuroset.Application;

public sealed record CodingSemanticSegment(string Text, string? Color)
{
    public bool HasColor => !string.IsNullOrWhiteSpace(Color);
}

public static class CodingSemanticMarkup
{
    private static readonly Regex TokenRegex = new(
        @"\[\[(?<color>cyan|blue|green|yellow|orange|red|purple|gray)\|(?<text>[^\]\r\n]{1,500})\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<CodingSemanticSegment> Parse(string value)
    {
        if (string.IsNullOrEmpty(value)) return [new(string.Empty, null)];
        var result = new List<CodingSemanticSegment>();
        var position = 0;
        foreach (Match match in TokenRegex.Matches(value))
        {
            if (match.Index > position) result.Add(new(value[position..match.Index], null));
            result.Add(new(match.Groups["text"].Value, match.Groups["color"].Value.ToLowerInvariant()));
            position = match.Index + match.Length;
        }
        if (position < value.Length) result.Add(new(value[position..], null));
        return result.Count == 0 ? [new(value, null)] : result;
    }

    public static string Strip(string value)
    {
        var result = new StringBuilder(value?.Length ?? 0);
        foreach (var segment in Parse(value ?? string.Empty)) result.Append(segment.Text);
        return result.ToString();
    }
}
