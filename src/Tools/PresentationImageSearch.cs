using System.Globalization;
using System.Text.RegularExpressions;

namespace Neuroset.Tools;

// Commons search requires every word. Authoring prompts often contain descriptions,
// so progressively relax those descriptions while retaining the subject and image kind.
internal static class PresentationImageSearch
{
    private static readonly string[] MapKinds = ["linguistic", "languages", "language", "administrative", "cantons", "physical", "locator", "location"];
    private static readonly HashSet<string> Descriptors = new([
        "photograph", "photography", "photo", "photos", "image", "images", "picture", "pictures", "panorama", "panoramic",
        "aerial", "exterior", "interior", "simple", "official", "national", "square", "svg", "png", "jpg", "jpeg",
        "wikimedia", "commons", "high", "resolution", "wide", "view", "city", "summer", "traditional", "realistic", "real",
        "president", "confederation", "portrait", "the", "and", "in", "of", "with", "alpine", "precision", "mechanical", "yellow", "four"
    ], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Countries = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(culture => new RegionInfo(culture.Name).EnglishName).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(country => country.Length).ToArray();

    public static string? Country(string query)
    {
        string? selected = null;
        var first = int.MaxValue;
        foreach (var country in Countries)
        {
            var match = Regex.Match(query, @"(?<!\p{L})" + Regex.Escape(country) + @"(?!\p{L})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success && match.Index < first) { first = match.Index; selected = country; }
        }
        return selected;
    }

    public static string? SymbolTitle(string query)
    {
        var country = Country(query);
        if (country is null) return null;
        if (Regex.IsMatch(query, @"\bcoat\s+of\s+arms\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Coat of arms of " + country;
        return Regex.IsMatch(query, @"\bflag\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "Flag of " + country : null;
    }

    public static string[] Queries(string query)
    {
        var results = new List<string>();
        void Add(string value)
        {
            value = value.Trim();
            if (value.Length > 0 && !results.Contains(value, StringComparer.OrdinalIgnoreCase)) results.Add(value);
        }
        Add(query);
        var words = Regex.Matches(query, @"[\p{L}\p{N}]+(?:['’][\p{L}]+)?", RegexOptions.CultureInvariant)
            .Select(match => match.Value).Where(word => !Descriptors.Contains(word) && !int.TryParse(word, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var country = Country(query);
        if (SymbolTitle(query) is { } symbol)
        {
            Add(symbol);
            return results.ToArray();
        }
        Add(string.Join(' ', words));
        // A language/administrative/physical map must remain that type of map.
        if (country is not null && words.Contains("map", StringComparer.OrdinalIgnoreCase))
        {
            var kind = words.FirstOrDefault(word => MapKinds.Contains(word, StringComparer.OrdinalIgnoreCase));
            Add(string.Join(' ', new[] { country, kind, "map" }.Where(word => !string.IsNullOrWhiteSpace(word))));
            if (kind is "locator") Add(country + " location map");
            return results.ToArray();
        }
        foreach (var length in new[] { 6, 4, 3 })
            if (words.Length > length) Add(string.Join(' ', words.Take(length)));
        if (country is not null && !query.StartsWith(country, StringComparison.OrdinalIgnoreCase))
        {
            // Keep the named city/landmark and country to avoid similarly named places elsewhere.
            var countryIndex = query.IndexOf(country, StringComparison.OrdinalIgnoreCase);
            var subject = Regex.Matches(query[..countryIndex], @"[\p{L}]+(?:['’][\p{L}]+)?", RegexOptions.CultureInvariant)
                .Select(match => match.Value).Where(word => !Descriptors.Contains(word)).ToArray();
            Add(string.Join(' ', subject) + " " + country);
        }
        else if (words.Length > 2) Add(string.Join(' ', words.Take(2)));
        return results.Take(7).ToArray();
    }

    public static bool MatchesKind(string query, string title)
    {
        static bool Has(string value, string pattern) => Regex.IsMatch(value.Replace('_', ' '), pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (Has(query, @"\bmap\b"))
        {
            if (Country(query) is { } expected && Country(title.Replace('_', ' ')) is { } actual && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
            if (Has(query, @"\b(?:linguistic|languages?|sprach|langue)\b") && !Has(title, @"linguist|language|sprach|langue")) return false;
            if (Has(query, @"\b(?:physical|relief)\b") && !Has(title, @"physical|relief|topograph|physisch")) return false;
            if (Has(query, @"\b(?:cantons|administrative)\b") && !Has(title, @"canton|kanton|administrative")) return false;
            return Has(title, @"\b(?:map|maps|karte|carte|mappa|locator|location|locationmap)\b");
        }
        if (Has(query, @"\bcoat\s+of\s+arms\b")) return Has(title, @"\b(?:coat\s+of\s+arms|coa|wappen|armoiries|stemma)\b");
        if (Has(query, @"\bflag\b")) return Has(title, @"\b(?:flag|flagge|drapeau|bandiera)\b");
        if (!Has(query, @"\b(?:satellite|space|aerial)\b") && Has(title, @"\b(?:ISS\d*|satellite)\b")) return false;
        return Has(query, @"\b(?:diagram|chart|logo)\b") || !Has(title, @"\.(?:svg|pdf|djvu|tif|tiff)$");
    }

    public static bool MatchesSubject(string query, string text)
    {
        var country = Country(query);
        var countryIndex = country is null ? -1 : query.IndexOf(country, StringComparison.OrdinalIgnoreCase);
        var subject = countryIndex > 0 ? query[..countryIndex] : countryIndex == 0 ? query[country!.Length..] : query;
        var words = Regex.Matches(subject, @"[\p{L}]+", RegexOptions.CultureInvariant).Select(match => match.Value)
            .Where(word => word.Length > 2 && !Descriptors.Contains(word) && word is not ("des" or "aux"))
            .Take(countryIndex == 0 ? 1 : 2).ToArray();
        return words.Length > 0 && words.All(word => text.Contains(
            char.IsLower(word[0]) && word.Length > 6 && word.EndsWith("ing", StringComparison.Ordinal) ? word[..^3] : word,
            StringComparison.OrdinalIgnoreCase));
    }

    public static string SubjectQuery(string query)
    {
        if (SymbolTitle(query) is { } symbol) return symbol;
        if (query.Contains(" map", StringComparison.OrdinalIgnoreCase)) return Queries(query)[^1];
        var country = Country(query);
        var index = country is null ? -1 : query.IndexOf(country, StringComparison.OrdinalIgnoreCase);
        var subject = index > 0 ? query[..index] : index == 0 ? query[country!.Length..] : query;
        return string.Join(' ', Regex.Matches(subject, @"[\p{L}]+", RegexOptions.CultureInvariant).Select(match => match.Value)
            .Where(word => word.Length > 2 && !Descriptors.Contains(word) && word is not ("des" or "aux")).Take(index == 0 ? 1 : 2));
    }
}
