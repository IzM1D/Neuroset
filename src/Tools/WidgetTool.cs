using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Tools;

public sealed class WidgetTool(HttpClient httpClient) : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "operation":{"type":"string","enum":["calculate","convert_units","convert_currency","weather","time","sports"]},
            "expression":{"type":"string","minLength":1,"maxLength":500},
            "value":{"type":"number"},
            "from":{"type":"string","minLength":1,"maxLength":32},
            "to":{"type":"string","minLength":1,"maxLength":32},
            "location":{"type":"string","minLength":1,"maxLength":120},
            "timezone":{"type":"string","minLength":1,"maxLength":120},
            "sport":{"type":"string","minLength":1,"maxLength":40},
            "league":{"type":"string","minLength":1,"maxLength":40},
            "date":{"type":"string","format":"date"}
          },
          "required":["operation"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    private static readonly Dictionary<string, UnitDefinition> Units = CreateUnits();

    public ToolDescriptor Descriptor { get; } = new(
        "widgets",
        "Show a native result widget. Supports safe calculations, unit and currency conversion, current weather, world time, and live sports scores/schedules. Use this instead of estimating these results.",
        Schema,
        ToolRisk.NetworkRead,
        ["widgets.use", "internet.widgets"]);

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var operation = arguments.GetProperty("operation").GetString();
            var widget = operation switch
            {
                "calculate" => Calculate(arguments),
                "convert_units" => ConvertUnits(arguments),
                "convert_currency" => await ConvertCurrencyAsync(arguments, cancellationToken).ConfigureAwait(false),
                "weather" => await GetWeatherAsync(arguments, cancellationToken).ConfigureAwait(false),
                "time" => await GetTimeAsync(arguments, cancellationToken).ConfigureAwait(false),
                "sports" => await GetSportsAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unsupported widget operation.")
            };
            return new(true, JsonSerializer.Serialize(widget, DomainJson.Options), Metadata: new Dictionary<string, string>
            {
                ["widget_kind"] = widget.Kind,
                ["widget_title"] = widget.Title
            });
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException or FormatException or InvalidOperationException or JsonException or HttpRequestException)
        {
            return new(false, string.Empty, exception.Message);
        }
    }

    private static WidgetPayload Calculate(JsonElement arguments)
    {
        var expression = RequireString(arguments, "expression");
        var result = new ExpressionParser(expression).Parse();
        if (!double.IsFinite(result)) throw new ArithmeticException("The calculation did not produce a finite number.");
        return new("calculator", "Calculator", expression, FormatNumber(result), null,
            [new("Expression", expression)], null, DateTimeOffset.UtcNow);
    }

    private static WidgetPayload ConvertUnits(JsonElement arguments)
    {
        var value = RequireNumber(arguments, "value");
        var fromText = RequireString(arguments, "from");
        var toText = RequireString(arguments, "to");
        var fromKey = NormalizeUnit(fromText);
        var toKey = NormalizeUnit(toText);
        if (!Units.TryGetValue(fromKey, out var from)) throw new InvalidOperationException($"Unsupported source unit: {fromText}.");
        if (!Units.TryGetValue(toKey, out var to)) throw new InvalidOperationException($"Unsupported target unit: {toText}.");
        if (!from.Category.Equals(to.Category, StringComparison.Ordinal)) throw new InvalidOperationException($"Cannot convert {fromText} to {toText}: the units measure different quantities.");
        var result = from.Category == "temperature"
            ? ConvertTemperature(value, from.Symbol, to.Symbol)
            : value * from.ToBase / to.ToBase;
        return new("units", "Unit conversion", $"{FormatNumber(value)} {from.Symbol} → {to.Symbol}", $"{FormatNumber(result)} {to.Symbol}", null,
            [new("From", $"{FormatNumber(value)} {from.Symbol}"), new("To", $"{FormatNumber(result)} {to.Symbol}")], null, DateTimeOffset.UtcNow);
    }

    private async Task<WidgetPayload> ConvertCurrencyAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var amount = RequireNumber(arguments, "value");
        var from = RequireCurrency(arguments, "from");
        var to = RequireCurrency(arguments, "to");
        if (from == to)
            return new("currency", "Currency converter", $"{FormatNumber(amount)} {from} → {to}", $"{FormatNumber(amount)} {to}", "Same currency",
                [new("Rate", $"1 {from} = 1 {to}")], "Local", DateTimeOffset.UtcNow);

        using var document = await GetJsonAsync($"https://open.er-api.com/v6/latest/{Uri.EscapeDataString(from)}", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("result", out var resultElement) || !resultElement.GetString()!.Equals("success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The currency service did not return a usable rate.");
        if (!root.GetProperty("rates").TryGetProperty(to, out var rateElement)) throw new InvalidOperationException($"No exchange rate is available for {to}.");
        var rate = rateElement.GetDouble();
        var converted = amount * rate;
        var updated = root.TryGetProperty("time_last_update_utc", out var updatedElement) && DateTimeOffset.TryParse(updatedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : DateTimeOffset.UtcNow;
        return new("currency", "Currency converter", $"{FormatNumber(amount)} {from} → {to}", $"{FormatNumber(converted)} {to}", $"1 {from} = {FormatNumber(rate)} {to}",
            [new("Amount", $"{FormatNumber(amount)} {from}"), new("Rate", FormatNumber(rate))], "ExchangeRate-API", updated);
    }

    private async Task<WidgetPayload> GetWeatherAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var location = RequireString(arguments, "location");
        var place = await GeocodeAsync(location, cancellationToken).ConfigureAwait(false);
        var url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={place.Latitude}&longitude={place.Longitude}&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset&forecast_days=1&timezone=auto");
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        var temperature = current.GetProperty("temperature_2m").GetDouble();
        var feels = current.GetProperty("apparent_temperature").GetDouble();
        var humidity = current.GetProperty("relative_humidity_2m").GetDouble();
        var wind = current.GetProperty("wind_speed_10m").GetDouble();
        var code = current.GetProperty("weather_code").GetInt32();
        var high = FirstDouble(daily, "temperature_2m_max");
        var low = FirstDouble(daily, "temperature_2m_min");
        var updated = current.TryGetProperty("time", out var timeElement) && DateTime.TryParse(timeElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.FromSeconds(root.TryGetProperty("utc_offset_seconds", out var offsetElement) ? offsetElement.GetInt32() : 0)) : DateTimeOffset.UtcNow;
        return new("weather", "Current weather", place.DisplayName, $"{FormatNumber(temperature)} °C", $"{WeatherDescription(code)} · feels like {FormatNumber(feels)} °C",
            [new("High / low", $"{FormatNumber(high)}° / {FormatNumber(low)}°"), new("Humidity", $"{FormatNumber(humidity)}%"), new("Wind", $"{FormatNumber(wind)} km/h"), new("Timezone", place.Timezone)], "Open-Meteo", updated);
    }

    private async Task<WidgetPayload> GetTimeAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string timezone;
        string title;
        if (arguments.TryGetProperty("timezone", out var timezoneElement) && !string.IsNullOrWhiteSpace(timezoneElement.GetString()))
        {
            timezone = timezoneElement.GetString()!.Trim();
            title = timezone;
        }
        else if (arguments.TryGetProperty("location", out var locationElement) && !string.IsNullOrWhiteSpace(locationElement.GetString()))
        {
            var place = await GeocodeAsync(locationElement.GetString()!, cancellationToken).ConfigureAwait(false);
            timezone = place.Timezone;
            title = place.DisplayName;
        }
        else
        {
            timezone = TimeZoneInfo.Local.Id;
            title = "Local time";
        }

        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(NormalizeTimeZone(timezone)); }
        catch (TimeZoneNotFoundException) { throw new InvalidOperationException($"Unknown time zone: {timezone}."); }
        catch (InvalidTimeZoneException) { throw new InvalidOperationException($"Invalid time zone: {timezone}."); }
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var offset = zone.GetUtcOffset(now);
        return new("time", "World clock", title, now.ToString("HH:mm", CultureInfo.CurrentCulture), now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture),
            [new("Time zone", zone.Id), new("UTC offset", $"UTC{FormatOffset(offset)}")], "System clock", now);
    }

    private async Task<WidgetPayload> GetSportsAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var sportInput = arguments.TryGetProperty("sport", out var sportElement) ? sportElement.GetString() : null;
        var leagueInput = arguments.TryGetProperty("league", out var leagueElement) ? leagueElement.GetString() : null;
        var league = ResolveLeague(sportInput, leagueInput);
        var date = arguments.TryGetProperty("date", out var dateElement) && DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
            ? parsedDate : DateOnly.FromDateTime(DateTime.Today);
        using var document = await GetJsonAsync($"https://site.web.api.espn.com/apis/site/v2/sports/{league.Sport}/{league.League}/scoreboard?dates={date:yyyyMMdd}", cancellationToken).ConfigureAwait(false);
        var rows = new List<WidgetRow>();
        if (document.RootElement.TryGetProperty("events", out var events))
        {
            foreach (var game in events.EnumerateArray().Take(10))
            {
                var name = game.TryGetProperty("shortName", out var shortName) ? shortName.GetString() : game.GetProperty("name").GetString();
                var competition = game.GetProperty("competitions")[0];
                var status = competition.GetProperty("status").GetProperty("type");
                var statusText = status.TryGetProperty("shortDetail", out var detail) ? detail.GetString() : status.GetProperty("description").GetString();
                var state = status.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
                var scores = competition.GetProperty("competitors").EnumerateArray()
                    .Select(x => new { Name = x.GetProperty("team").GetProperty("abbreviation").GetString(), Score = x.GetProperty("score").GetString() })
                    .ToArray();
                var value = state != "pre" && scores.Length >= 2 && scores.Any(x => !string.IsNullOrWhiteSpace(x.Score))
                    ? string.Join(" · ", scores.Select(x => $"{x.Name} {x.Score}")) + $" · {statusText}"
                    : statusText ?? "Scheduled";
                rows.Add(new(name ?? "Game", value));
            }
        }
        var primary = rows.Count == 0 ? "No games" : rows.Count == 1 ? "1 game" : $"{rows.Count} games";
        return new("sports", "Sports", league.Title, primary, date.ToString("dddd, d MMMM", CultureInfo.CurrentCulture), rows, "ESPN", DateTimeOffset.UtcNow);
    }

    private async Task<Place> GeocodeAsync(string query, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=1&language=en&format=json", cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) throw new InvalidOperationException($"Location not found: {query}.");
        var result = results[0];
        var name = result.GetProperty("name").GetString() ?? query;
        var country = result.TryGetProperty("country", out var countryElement) ? countryElement.GetString() : null;
        return new(result.GetProperty("latitude").GetDouble(), result.GetProperty("longitude").GetDouble(),
            result.TryGetProperty("timezone", out var timezone) ? timezone.GetString() ?? "UTC" : "UTC",
            string.IsNullOrWhiteSpace(country) ? name : $"{name}, {country}");
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Neuroset/1.0 widgets");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string RequireString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) && !string.IsNullOrWhiteSpace(element.GetString()) ? element.GetString()!.Trim() : throw new InvalidOperationException($"{name} is required for this widget.");

    private static double RequireNumber(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number ? element.GetDouble() : throw new InvalidOperationException($"{name} is required for this widget.");

    private static string RequireCurrency(JsonElement arguments, string name)
    {
        var value = RequireString(arguments, name).ToUpperInvariant();
        if (!Regex.IsMatch(value, "^[A-Z]{3}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException($"{name} must be a three-letter ISO currency code.");
        return value;
    }

    private static string FormatNumber(double value) => Math.Abs(value) is >= 1e12 or (< 1e-7 and > 0)
        ? value.ToString("0.########E+0", CultureInfo.CurrentCulture)
        : value.ToString("0.########", CultureInfo.CurrentCulture);

    private static double FirstDouble(JsonElement parent, string name) => parent.GetProperty(name)[0].GetDouble();

    private static string WeatherDescription(int code) => code switch
    {
        0 => "Clear sky", 1 => "Mainly clear", 2 => "Partly cloudy", 3 => "Overcast", 45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle", 56 or 57 => "Freezing drizzle", 61 or 63 or 65 => "Rain", 66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 => "Snow", 80 or 81 or 82 => "Rain showers", 85 or 86 => "Snow showers", 95 or 96 or 99 => "Thunderstorm", _ => "Current conditions"
    };

    private static string FormatOffset(TimeSpan offset) => $"{(offset < TimeSpan.Zero ? "−" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";

    private static string NormalizeTimeZone(string value) => value.Trim().ToLowerInvariant() switch
    {
        "utc" or "gmt" => "UTC",
        "moscow" or "москва" => "Russian Standard Time",
        "london" => "GMT Standard Time",
        "new york" => "Eastern Standard Time",
        "los angeles" => "Pacific Standard Time",
        "tokyo" => "Tokyo Standard Time",
        _ => value.Trim()
    };

    private static LeagueDefinition ResolveLeague(string? sport, string? league)
    {
        var value = (league ?? sport ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "nfl" or "american football" => new("football", "nfl", "NFL"),
            "nba" or "basketball" => new("basketball", "nba", "NBA"),
            "wnba" => new("basketball", "wnba", "WNBA"),
            "nhl" or "hockey" => new("hockey", "nhl", "NHL"),
            "mlb" or "baseball" => new("baseball", "mlb", "MLB"),
            "epl" or "premier league" or "football" or "soccer" => new("soccer", "eng.1", "Premier League"),
            "champions league" or "ucl" => new("soccer", "uefa.champions", "UEFA Champions League"),
            "la liga" => new("soccer", "esp.1", "La Liga"),
            "serie a" => new("soccer", "ita.1", "Serie A"),
            "bundesliga" => new("soccer", "ger.1", "Bundesliga"),
            "ligue 1" => new("soccer", "fra.1", "Ligue 1"),
            "rpl" or "russian premier league" or "российская премьер-лига" or "рпл" => new("soccer", "rus.1", "Russian Premier League"),
            _ => throw new InvalidOperationException("Specify a supported league: NFL, NBA, WNBA, NHL, MLB, Premier League, Champions League, La Liga, Serie A, Bundesliga, Ligue 1, or Russian Premier League.")
        };
    }

    private static string NormalizeUnit(string value) => value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static double ConvertTemperature(double value, string from, string to)
    {
        var celsius = from switch { "°C" => value, "°F" => (value - 32) * 5 / 9, "K" => value - 273.15, _ => throw new InvalidOperationException("Unknown temperature unit.") };
        return to switch { "°C" => celsius, "°F" => celsius * 9 / 5 + 32, "K" => celsius + 273.15, _ => throw new InvalidOperationException("Unknown temperature unit.") };
    }

    private static Dictionary<string, UnitDefinition> CreateUnits()
    {
        var result = new Dictionary<string, UnitDefinition>(StringComparer.OrdinalIgnoreCase);
        void Add(string category, string symbol, double factor, params string[] aliases)
        {
            var definition = new UnitDefinition(category, symbol, factor);
            foreach (var alias in aliases.Append(symbol)) result[NormalizeUnit(alias)] = definition;
        }
        Add("length", "mm", .001, "millimeter", "millimeters", "мм"); Add("length", "cm", .01, "centimeter", "centimeters", "см");
        Add("length", "m", 1, "meter", "meters", "metre", "метр", "метры", "м"); Add("length", "km", 1000, "kilometer", "kilometers", "км");
        Add("length", "in", .0254, "inch", "inches", "\""); Add("length", "ft", .3048, "foot", "feet"); Add("length", "yd", .9144, "yard", "yards"); Add("length", "mi", 1609.344, "mile", "miles");
        Add("mass", "mg", .000001, "milligram", "milligrams", "мг"); Add("mass", "g", .001, "gram", "grams", "г"); Add("mass", "kg", 1, "kilogram", "kilograms", "кг");
        Add("mass", "oz", .028349523125, "ounce", "ounces"); Add("mass", "lb", .45359237, "pound", "pounds", "lbs"); Add("mass", "t", 1000, "tonne", "tonnes", "тонна");
        Add("temperature", "°C", 1, "c", "celsius", "цельсий"); Add("temperature", "°F", 1, "f", "fahrenheit", "фаренгейт"); Add("temperature", "K", 1, "kelvin", "кельвин");
        Add("volume", "ml", .001, "milliliter", "milliliters", "мл"); Add("volume", "L", 1, "liter", "liters", "litre", "л"); Add("volume", "gal", 3.785411784, "gallon", "gallons");
        Add("volume", "qt", .946352946, "quart", "quarts"); Add("volume", "pt", .473176473, "pint", "pints"); Add("volume", "cup", .2365882365, "cups");
        Add("area", "m²", 1, "m2", "sqm"); Add("area", "km²", 1_000_000, "km2", "sqkm"); Add("area", "ft²", .09290304, "ft2", "sqft"); Add("area", "acre", 4046.8564224, "acres"); Add("area", "ha", 10_000, "hectare", "hectares");
        Add("speed", "m/s", 1, "mps"); Add("speed", "km/h", 1 / 3.6, "kph", "kmh", "км/ч"); Add("speed", "mph", .44704); Add("speed", "kn", .514444444, "knot", "knots");
        Add("duration", "ms", .001, "millisecond", "milliseconds"); Add("duration", "s", 1, "sec", "second", "seconds", "сек"); Add("duration", "min", 60, "minute", "minutes", "мин"); Add("duration", "h", 3600, "hour", "hours", "ч"); Add("duration", "d", 86400, "day", "days", "день"); Add("duration", "wk", 604800, "week", "weeks");
        Add("data", "B", 1, "byte", "bytes"); Add("data", "KB", 1000, "kilobyte"); Add("data", "MB", 1_000_000, "megabyte"); Add("data", "GB", 1_000_000_000, "gigabyte"); Add("data", "TB", 1_000_000_000_000, "terabyte");
        Add("data", "KiB", 1024, "kibibyte"); Add("data", "MiB", 1_048_576, "mebibyte"); Add("data", "GiB", 1_073_741_824, "gibibyte");
        return result;
    }

    private sealed class ExpressionParser(string expression)
    {
        private int _position;

        public double Parse()
        {
            var value = ParseExpression();
            SkipWhitespace();
            if (_position != expression.Length) throw new FormatException($"Unexpected character at position {_position + 1}.");
            return value;
        }

        private double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (Take('+')) value += ParseTerm(); else if (Take('-')) value -= ParseTerm(); else return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParsePower();
            while (true)
            {
                SkipWhitespace();
                if (Take('*')) value *= ParsePower();
                else if (Take('/')) { var divisor = ParsePower(); if (divisor == 0) throw new DivideByZeroException("Division by zero."); value /= divisor; }
                else if (Take('%')) { var divisor = ParsePower(); if (divisor == 0) throw new DivideByZeroException("Division by zero."); value %= divisor; }
                else return value;
            }
        }

        private double ParsePower()
        {
            var value = ParseUnary();
            SkipWhitespace();
            return Take('^') ? Math.Pow(value, ParsePower()) : value;
        }

        private double ParseUnary()
        {
            SkipWhitespace();
            if (Take('+')) return ParseUnary();
            if (Take('-')) return -ParseUnary();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhitespace();
            if (Take('(')) { var value = ParseExpression(); SkipWhitespace(); if (!Take(')')) throw new FormatException("A closing parenthesis is missing."); return value; }
            if (_position < expression.Length && (char.IsLetter(expression[_position]) || expression[_position] == 'π'))
            {
                var name = ParseIdentifier().ToLowerInvariant();
                if (name is "pi" or "π") return Math.PI;
                if (name == "e") return Math.E;
                SkipWhitespace();
                if (!Take('(')) throw new FormatException($"Function '{name}' requires parentheses.");
                var arguments = new List<double> { ParseExpression() };
                SkipWhitespace();
                while (Take(',')) arguments.Add(ParseExpression());
                SkipWhitespace();
                if (!Take(')')) throw new FormatException("A closing parenthesis is missing.");
                return EvaluateFunction(name, arguments);
            }
            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipWhitespace();
            var start = _position;
            while (_position < expression.Length && (char.IsDigit(expression[_position]) || expression[_position] == '.')) _position++;
            if (_position < expression.Length && expression[_position] is 'e' or 'E')
            {
                _position++;
                if (_position < expression.Length && expression[_position] is '+' or '-') _position++;
                var exponentStart = _position;
                while (_position < expression.Length && char.IsDigit(expression[_position])) _position++;
                if (exponentStart == _position) throw new FormatException("Invalid scientific notation.");
            }
            if (start == _position) throw new FormatException($"A number was expected at position {_position + 1}.");
            var token = expression[start.._position];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw new FormatException($"Invalid number: {token}.");
            return value;
        }

        private string ParseIdentifier()
        {
            var start = _position;
            while (_position < expression.Length && (char.IsLetter(expression[_position]) || expression[_position] == 'π')) _position++;
            return expression[start.._position];
        }

        private static double EvaluateFunction(string name, List<double> values) => name switch
        {
            "sqrt" when values.Count == 1 => Math.Sqrt(values[0]), "abs" when values.Count == 1 => Math.Abs(values[0]),
            "sin" when values.Count == 1 => Math.Sin(values[0]), "cos" when values.Count == 1 => Math.Cos(values[0]), "tan" when values.Count == 1 => Math.Tan(values[0]),
            "ln" when values.Count == 1 => Math.Log(values[0]), "log" when values.Count == 1 => Math.Log10(values[0]),
            "round" when values.Count == 1 => Math.Round(values[0]), "round" when values.Count == 2 => Math.Round(values[0], Math.Clamp((int)values[1], 0, 15)),
            "min" when values.Count >= 1 => values.Min(), "max" when values.Count >= 1 => values.Max(), "pow" when values.Count == 2 => Math.Pow(values[0], values[1]),
            _ => throw new FormatException($"Unknown function or invalid arguments: {name}.")
        };

        private bool Take(char value)
        {
            if (_position >= expression.Length || expression[_position] != value) return false;
            _position++;
            return true;
        }

        private void SkipWhitespace() { while (_position < expression.Length && char.IsWhiteSpace(expression[_position])) _position++; }
    }

    private sealed record WidgetPayload(string Kind, string Eyebrow, string Title, string Primary, string? Secondary, IReadOnlyList<WidgetRow> Rows, string? Source, DateTimeOffset UpdatedAt);
    private sealed record WidgetRow(string Label, string Value);
    private sealed record UnitDefinition(string Category, string Symbol, double ToBase);
    private sealed record Place(double Latitude, double Longitude, string Timezone, string DisplayName);
    private sealed record LeagueDefinition(string Sport, string League, string Title);
}
