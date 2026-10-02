namespace Neuroset.Domain;

public sealed class ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "OpenAI-compatible";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKeySecretId { get; set; } = string.Empty;
    public List<string> BackupApiKeySecretIds { get; set; } = [];
    public string Model { get; set; } = string.Empty;
    public string? Organization { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 4096;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AppSettings
{
    public bool FirstRunCompleted { get; set; }
    public string InterfaceLanguage { get; set; } = "ru-RU";
    public Guid? ActiveConnectionId { get; set; }
    public bool LongTermMemoryEnabled { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool FullAutonomyEnabled { get; set; }
    public int AgentMaxIterations { get; set; } = 12;
    public int AgentHardMaxIterations { get; set; } = 64;
    public int CodingAgentMaxIterations { get; set; } = 256;
    public int CodingAgentTimeoutMinutes { get; set; } = 120;
    public int AgentRetryCount { get; set; } = 2;
    public ReasoningPreference ReasoningPreference { get; set; } = ReasoningPreference.Auto;
    public GenerationSettings Generation { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
}

public sealed class GenerationSettings
{
    public string SystemPrompt { get; set; } = string.Empty;
    public SystemPromptPreset PromptPreset { get; set; }
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 1;
    /// <summary>Optional provider-specific top-k sampling limit.</summary>
    public int? TopK { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public double PresencePenalty { get; set; }
    public double FrequencyPenalty { get; set; }
    /// <summary>Optional multiplicative repetition penalty used by open models.</summary>
    public double? RepetitionPenalty { get; set; }
    public int? Seed { get; set; }
    public List<string> StopSequences { get; set; } = [];
    public bool EnableTools { get; set; } = true;
    public ParseltongueSettings Parseltongue { get; set; } = new();
    public SemanticTransformationSettings SemanticTransform { get; set; } = new();
    public AutoTuneSettings AutoTune { get; set; } = new();
    public MultiModelRacingSettings MultiModelRacing { get; set; } = new();

    public GenerationSettings Normalize() => new()
    {
        SystemPrompt = NormalizePrompt(SystemPrompt),
        PromptPreset = Enum.IsDefined(PromptPreset) ? PromptPreset : SystemPromptPreset.None,
        Temperature = Math.Clamp(Temperature, 0, 2),
        TopP = Math.Clamp(TopP, 0, 1),
        TopK = TopK is { } topK ? Math.Clamp(topK, 1, 100) : null,
        MaxTokens = Math.Clamp(MaxTokens, 1, 1_000_000),
        PresencePenalty = Math.Clamp(PresencePenalty, -2, 2),
        FrequencyPenalty = Math.Clamp(FrequencyPenalty, -2, 2),
        RepetitionPenalty = RepetitionPenalty is { } repetition ? Math.Clamp(repetition, 0, 2) : null,
        Seed = Seed,
        StopSequences = (StopSequences ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(4).Select(value => value.Length > 500 ? value[..500] : value).ToList(),
        EnableTools = EnableTools,
        Parseltongue = (Parseltongue ?? new()).Normalize(),
        SemanticTransform = (SemanticTransform ?? new()).Normalize(),
        AutoTune = (AutoTune ?? new()).Normalize(),
        MultiModelRacing = (MultiModelRacing ?? new()).Normalize()
    };

    private static string NormalizePrompt(string? value)
    {
        var prompt = (value ?? string.Empty).Trim();
        return prompt.Length > 32_768 ? prompt[..32_768] : prompt;
    }
}

public sealed class ParseltongueSettings
{
    public bool Enabled { get; set; }
    public ParseltongueIntensity Intensity { get; set; } = ParseltongueIntensity.Light;
    public ParseltongueTechnique Technique { get; set; } = ParseltongueTechnique.Leetspeak;
    public List<string> CustomTriggers { get; set; } = [];
    public int? Seed { get; set; }

    public ParseltongueSettings Normalize() => new()
    {
        Enabled = Enabled,
        Intensity = Enum.IsDefined(Intensity) ? Intensity : ParseltongueIntensity.Light,
        Technique = Enum.IsDefined(Technique) ? Technique : ParseltongueTechnique.Leetspeak,
        CustomTriggers = (CustomTriggers ?? [])
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .Select(value => value.Length > 200 ? value[..200] : value)
            .ToList(),
        Seed = Seed
    };
}

public sealed class SemanticTransformationSettings
{
    public bool HedgeReducerEnabled { get; set; }
    public bool DirectModeEnabled { get; set; }
    public bool CasualModeEnabled { get; set; }

    public SemanticTransformationSettings Normalize() => new()
    {
        HedgeReducerEnabled = HedgeReducerEnabled,
        DirectModeEnabled = DirectModeEnabled,
        CasualModeEnabled = CasualModeEnabled
    };
}

public sealed class AutoTuneSettings
{
    public bool Enabled { get; set; }
    public AutoTuneStrategy Strategy { get; set; } = AutoTuneStrategy.Adaptive;

    public AutoTuneSettings Normalize() => new()
    {
        Enabled = Enabled,
        Strategy = Enum.IsDefined(Strategy) ? Strategy : AutoTuneStrategy.Adaptive
    };
}

public sealed class MultiModelRacingSettings
{
    public bool Enabled { get; set; }
    public int ModelCount { get; set; } = 2;

    public MultiModelRacingSettings Normalize() => new()
    {
        Enabled = Enabled,
        ModelCount = Math.Clamp(ModelCount, 2, 8)
    };
}

public sealed record SystemPromptPresetDefinition(SystemPromptPreset Id, string DisplayName, string Prompt);

public sealed record ParseltongueIntensityDefinition(ParseltongueIntensity Id, string DisplayName);

public sealed record ParseltongueTechniqueDefinition(ParseltongueTechnique Id, string DisplayName);

public sealed record AutoTuneStrategyDefinition(AutoTuneStrategy Id, string DisplayName);

public static class ParseltongueIntensities
{
    public static IReadOnlyList<ParseltongueIntensityDefinition> All { get; } =
    [
        new(ParseltongueIntensity.Light, "Light intensity"),
        new(ParseltongueIntensity.Medium, "Medium intensity"),
        new(ParseltongueIntensity.Heavy, "Heavy intensity")
    ];
}

public static class ParseltongueTechniques
{
    public static IReadOnlyList<ParseltongueTechniqueDefinition> All { get; } =
    [
        new(ParseltongueTechnique.Leetspeak, "L33tspeak"),
        new(ParseltongueTechnique.Unicode, "Unicode"),
        new(ParseltongueTechnique.Zwj, "Zero-width"),
        new(ParseltongueTechnique.MixedCase, "MiXeD CaSe"),
        new(ParseltongueTechnique.Phonetic, "Phonetic"),
        new(ParseltongueTechnique.Random, "Random")
    ];
}

public static class AutoTuneStrategies
{
    public static IReadOnlyList<AutoTuneStrategyDefinition> All { get; } =
    [
        new(AutoTuneStrategy.Precise, "Precise"),
        new(AutoTuneStrategy.Balanced, "Balanced"),
        new(AutoTuneStrategy.Creative, "Creative"),
        new(AutoTuneStrategy.Chaotic, "Chaotic"),
        new(AutoTuneStrategy.Adaptive, "Adaptive")
    ];
}

public static class SystemPromptPresets
{
    public static IReadOnlyList<SystemPromptPresetDefinition> All { get; } =
    [
        new(SystemPromptPreset.None, "None", string.Empty),
        new(SystemPromptPreset.Research, "Research", """
            Work as a rigorous research assistant. Separate verified facts, reasonable inferences, and unknowns. Prefer primary evidence, state material uncertainty precisely, and produce a concise conclusion supported by the analysis.
            """),
        new(SystemPromptPreset.Direct, "Direct", """
            Answer the user's actual request immediately. Use plain, precise language, omit boilerplate and repeated caveats, and keep qualifications only when they materially change the answer.
            """),
        new(SystemPromptPreset.G0dm0d3, "G0DM0D3", GodmodePrompt.Full)
    ];

    public static string Resolve(SystemPromptPreset preset) =>
        All.FirstOrDefault(item => item.Id == preset)?.Prompt ?? string.Empty;
}

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#7C6CF2";
    public double FontSize { get; set; } = 14;
    public double MessageFontSize { get; set; } = 15;
    public double UiScale { get; set; } = 1;
    public double SidebarWidth { get; set; } = 280;
    public UiDensity Density { get; set; } = UiDensity.Comfortable;
    public double CornerRadius { get; set; } = 14;
    public double Transparency { get; set; } = 0.92;
    public AnimationLevel Animations { get; set; } = AnimationLevel.Full;
}

public sealed record ThemeDefinition(
    string Name,
    string Background,
    string Surface,
    string ElevatedSurface,
    string Primary,
    string Secondary,
    string Accent,
    string Text,
    string SecondaryText,
    string Border,
    string CodeBlock,
    string Error,
    string Warning,
    string Success,
    string Selection,
    string Hover,
    string Focus);
