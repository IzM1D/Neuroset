using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuroset.App.Services;
using Neuroset.App.Localization;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.App.ViewModels;

public partial class SettingsViewModel(
    IConnectionRepository connections,
    ISecretStore secrets,
    ILLMProviderFactory providers,
    IModelReliabilityEvaluator reliabilityEvaluator,
    ISettingsRepository settingsRepository,
    IMemoryStore memory,
    IKnowledgeRepository knowledge,
    IScheduledTaskRepository scheduled,
    IToolPermissionRepository permissions,
    ThemeService themes) : ObservableObject, IDisposable
{
    public ObservableCollection<ConnectionProfile> Connections { get; } = [];
    public ObservableCollection<BackupApiKeyViewModel> BackupApiKeys { get; } = [];
    public ObservableCollection<string> AvailableModels { get; } = [];
    public ObservableCollection<MemoryRecord> Memories { get; } = [];
    public ObservableCollection<KnowledgeSource> KnowledgeSources { get; } = [];
    public ObservableCollection<ScheduledTaskDefinition> ScheduledTasks { get; } = [];
    public ObservableCollection<ToolPermissionRule> PermissionRules { get; } = [];
    public ObservableCollection<ModelReliabilityProbeResult> ReliabilityResults { get; } = [];
    public IReadOnlyList<string> ThemeNames => themes.Themes.Select(item => item.Name).ToArray();
    public IReadOnlyList<UiDensity> DensityOptions { get; } = Enum.GetValues<UiDensity>();
    public IReadOnlyList<AnimationLevel> AnimationOptions { get; } = Enum.GetValues<AnimationLevel>();
    public IReadOnlyList<ReasoningPreference> ReasoningOptions { get; } = Enum.GetValues<ReasoningPreference>();
    public IReadOnlyList<InterfaceLanguageOption> LanguageOptions { get; } = LocalizationService.Languages;
    public IReadOnlyList<SystemPromptPresetDefinition> PromptPresetOptions { get; } = SystemPromptPresets.All;
    public IReadOnlyList<ParseltongueIntensityDefinition> ParseltongueIntensityOptions { get; } = ParseltongueIntensities.All;
    public IReadOnlyList<ParseltongueTechniqueDefinition> ParseltongueTechniqueOptions { get; } = ParseltongueTechniques.All;
    public IReadOnlyList<AutoTuneStrategyDefinition> AutoTuneStrategyOptions { get; } = AutoTuneStrategies.All;
    public SystemPromptPresetDefinition? SelectedPromptPreset
    {
        get => PromptPresetOptions.FirstOrDefault(option => option.Id == DefaultPromptPreset);
        set { if (value is not null) DefaultPromptPreset = value.Id; }
    }
    public ParseltongueIntensityDefinition? SelectedPerturbationIntensity
    {
        get => ParseltongueIntensityOptions.FirstOrDefault(option => option.Id == DefaultParseltongueIntensity);
        set { if (value is not null) DefaultParseltongueIntensity = value.Id; }
    }
    public ParseltongueTechniqueDefinition? SelectedPerturbationTechnique
    {
        get => ParseltongueTechniqueOptions.FirstOrDefault(option => option.Id == DefaultParseltongueTechnique);
        set { if (value is not null) DefaultParseltongueTechnique = value.Id; }
    }
    public AutoTuneStrategyDefinition? SelectedSamplingStrategy
    {
        get => AutoTuneStrategyOptions.FirstOrDefault(option => option.Id == DefaultAutoTuneStrategy);
        set { if (value is not null) DefaultAutoTuneStrategy = value.Id; }
    }

    public event EventHandler<AppearanceSettings>? AppearanceApplied;
    public event EventHandler? ConnectionsChanged;

    [ObservableProperty] public partial ConnectionProfile? SelectedConnection { get; set; }
    [ObservableProperty] public partial string ConnectionName { get; set; } = "OpenAI-compatible";
    [ObservableProperty] public partial string BaseUrl { get; set; } = "https://api.openai.com/v1";
    [ObservableProperty] public partial string ApiKey { get; set; } = string.Empty;
    [ObservableProperty] public partial string Model { get; set; } = string.Empty;
    [ObservableProperty] public partial string Organization { get; set; } = string.Empty;
    [ObservableProperty] public partial int TimeoutSeconds { get; set; } = 120;
    [ObservableProperty] public partial double Temperature { get; set; } = 0.7;
    [ObservableProperty] public partial int MaxTokens { get; set; } = 4096;
    [ObservableProperty] public partial string ConnectionStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsConnectionBusy { get; set; }

    [ObservableProperty] public partial string Theme { get; set; } = "Dark";
    [ObservableProperty] public partial string AccentColor { get; set; } = "#8B7CF6";
    [ObservableProperty] public partial double FontSize { get; set; } = 14;
    [ObservableProperty] public partial double MessageFontSize { get; set; } = 15;
    [ObservableProperty] public partial double UiScale { get; set; } = 1;
    [ObservableProperty] public partial double SidebarWidth { get; set; } = 280;
    [ObservableProperty] public partial UiDensity Density { get; set; } = UiDensity.Comfortable;
    [ObservableProperty] public partial double CornerRadius { get; set; } = 14;
    [ObservableProperty] public partial double Transparency { get; set; } = 0.92;
    [ObservableProperty] public partial AnimationLevel Animations { get; set; } = AnimationLevel.Full;
    [ObservableProperty] public partial ReasoningPreference Reasoning { get; set; } = ReasoningPreference.Auto;
    [ObservableProperty] public partial bool LongTermMemoryEnabled { get; set; } = true;
    [ObservableProperty] public partial bool NotificationsEnabled { get; set; } = true;
    [ObservableProperty] public partial bool FullAutonomyEnabled { get; set; }
    [ObservableProperty] public partial string PermissionsStatus { get; set; } = LocalizationService.Get("Changes are saved automatically.");
    [ObservableProperty] public partial string InterfaceLanguage { get; set; } = LocalizationService.CurrentLanguage;
    [ObservableProperty] public partial string LanguageStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial int AgentMaxIterations { get; set; } = 12;
    [ObservableProperty] public partial int AgentHardMaxIterations { get; set; } = 64;
    [ObservableProperty] public partial int CodingAgentMaxIterations { get; set; } = 256;
    [ObservableProperty] public partial int CodingAgentTimeoutMinutes { get; set; } = 120;
    [ObservableProperty] public partial int AgentRetryCount { get; set; } = 2;
    [ObservableProperty] public partial string DefaultSystemPrompt { get; set; } = string.Empty;
    [ObservableProperty] public partial SystemPromptPreset DefaultPromptPreset { get; set; }
    [ObservableProperty] public partial double DefaultTemperature { get; set; } = 0.7;
    [ObservableProperty] public partial double DefaultTopP { get; set; } = 1;
    [ObservableProperty] public partial double DefaultTopKValue { get; set; } = double.NaN;
    [ObservableProperty] public partial int DefaultMaxTokens { get; set; } = 4096;
    [ObservableProperty] public partial double DefaultPresencePenalty { get; set; }
    [ObservableProperty] public partial double DefaultFrequencyPenalty { get; set; }
    [ObservableProperty] public partial double DefaultRepetitionPenaltyValue { get; set; } = double.NaN;
    [ObservableProperty] public partial double DefaultSeedValue { get; set; } = double.NaN;
    [ObservableProperty] public partial string DefaultStopSequences { get; set; } = string.Empty;
    [ObservableProperty] public partial bool DefaultEnableTools { get; set; } = true;
    [ObservableProperty] public partial bool DefaultParseltongueEnabled { get; set; }
    [ObservableProperty] public partial ParseltongueIntensity DefaultParseltongueIntensity { get; set; } = ParseltongueIntensity.Light;
    [ObservableProperty] public partial ParseltongueTechnique DefaultParseltongueTechnique { get; set; } = ParseltongueTechnique.Leetspeak;
    [ObservableProperty] public partial string DefaultParseltongueCustomTriggers { get; set; } = string.Empty;
    [ObservableProperty] public partial bool DefaultHedgeReducerEnabled { get; set; }
    [ObservableProperty] public partial bool DefaultDirectModeEnabled { get; set; }
    [ObservableProperty] public partial bool DefaultCasualModeEnabled { get; set; }
    [ObservableProperty] public partial bool DefaultAutoTuneEnabled { get; set; }
    [ObservableProperty] public partial AutoTuneStrategy DefaultAutoTuneStrategy { get; set; } = AutoTuneStrategy.Adaptive;
    [ObservableProperty] public partial bool DefaultMultiModelRacingEnabled { get; set; }
    [ObservableProperty] public partial int DefaultRacingModelCount { get; set; } = 2;
    [ObservableProperty] public partial string GenerationStatus { get; set; } = LocalizationService.Get("Changes are saved automatically.");
    [ObservableProperty] public partial string AppearanceStatus { get; set; } = LocalizationService.Get("Changes are saved automatically.");
    [ObservableProperty] public partial bool IsReliabilityRunning { get; set; }
    [ObservableProperty] public partial bool TestInstructionHierarchy { get; set; } = true;
    [ObservableProperty] public partial bool TestObfuscationRobustness { get; set; } = true;
    [ObservableProperty] public partial bool TestRefusalCalibration { get; set; } = true;
    [ObservableProperty] public partial bool TestAssistantPrefill { get; set; } = true;
    [ObservableProperty] public partial string ReliabilityStatus { get; set; } = LocalizationService.Get("Select an active connection, then run the reliability suite.");
    [ObservableProperty] public partial string ReliabilityReport { get; set; } = string.Empty;

    public AppSettings AppSettings { get; private set; } = new();
    public void RefreshSelectionBindings()
    {
        // The settings surface is initially collapsed. Re-evaluate selections
        // after its option containers are realized, including enum defaults.
        foreach (var property in new[] { nameof(InterfaceLanguage), nameof(Theme), nameof(Density), nameof(Animations),
            nameof(Reasoning), nameof(SelectedPromptPreset), nameof(SelectedPerturbationIntensity),
            nameof(SelectedPerturbationTechnique), nameof(SelectedSamplingStrategy), nameof(Model) })
            OnPropertyChanged(property);
    }
    public bool HasReliabilityReport => !string.IsNullOrWhiteSpace(ReliabilityReport);
    public string ReliabilityConnectionLabel
    {
        get
        {
            var active = Connections.FirstOrDefault(item => item.IsActive);
            return active is null ? LocalizationService.Get("No active connection") : $"{active.Name} · {active.Model} · {active.BaseUrl}";
        }
    }
    private bool CanRunReliability() => !IsReliabilityRunning && Connections.Any(item => item.IsActive);
    private bool _appearanceReady;
    private int _appearanceChangeVersion;
    private int _generationChangeVersion;
    private int _permissionsChangeVersion;
    private CancellationTokenSource? _reliabilityRun;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _appearanceReady = false;
        AppSettings = await settingsRepository.LoadAsync(cancellationToken);
        InterfaceLanguage = LocalizationService.CurrentLanguage;
        AppSettings.InterfaceLanguage = InterfaceLanguage;
        Theme = AppSettings.Appearance.Theme;
        AccentColor = AppSettings.Appearance.AccentColor;
        FontSize = AppSettings.Appearance.FontSize;
        MessageFontSize = AppSettings.Appearance.MessageFontSize;
        UiScale = AppSettings.Appearance.UiScale;
        SidebarWidth = AppSettings.Appearance.SidebarWidth;
        Density = AppSettings.Appearance.Density;
        CornerRadius = AppSettings.Appearance.CornerRadius;
        Transparency = AppSettings.Appearance.Transparency;
        Animations = AppSettings.Appearance.Animations;
        Reasoning = Enum.IsDefined(AppSettings.ReasoningPreference) ? AppSettings.ReasoningPreference : ReasoningPreference.Auto;
        LongTermMemoryEnabled = AppSettings.LongTermMemoryEnabled;
        NotificationsEnabled = AppSettings.NotificationsEnabled;
        FullAutonomyEnabled = AppSettings.FullAutonomyEnabled;
        AgentMaxIterations = AppSettings.AgentMaxIterations;
        AgentHardMaxIterations = AppSettings.AgentHardMaxIterations;
        CodingAgentMaxIterations = AppSettings.CodingAgentMaxIterations;
        CodingAgentTimeoutMinutes = AppSettings.CodingAgentTimeoutMinutes;
        AgentRetryCount = AppSettings.AgentRetryCount;
        AppSettings.Generation = (AppSettings.Generation ?? new GenerationSettings()).Normalize();
        DefaultSystemPrompt = AppSettings.Generation.SystemPrompt;
        DefaultPromptPreset = AppSettings.Generation.PromptPreset;
        DefaultTemperature = AppSettings.Generation.Temperature;
        DefaultTopP = AppSettings.Generation.TopP;
        DefaultTopKValue = AppSettings.Generation.TopK is { } topK ? topK : double.NaN;
        DefaultMaxTokens = AppSettings.Generation.MaxTokens;
        DefaultPresencePenalty = AppSettings.Generation.PresencePenalty;
        DefaultFrequencyPenalty = AppSettings.Generation.FrequencyPenalty;
        DefaultRepetitionPenaltyValue = AppSettings.Generation.RepetitionPenalty is { } repetitionPenalty ? repetitionPenalty : double.NaN;
        DefaultSeedValue = AppSettings.Generation.Seed is { } seed ? seed : double.NaN;
        DefaultStopSequences = string.Join(Environment.NewLine, AppSettings.Generation.StopSequences);
        DefaultEnableTools = AppSettings.Generation.EnableTools;
        DefaultParseltongueEnabled = AppSettings.Generation.Parseltongue.Enabled;
        DefaultParseltongueIntensity = AppSettings.Generation.Parseltongue.Intensity;
        DefaultParseltongueTechnique = AppSettings.Generation.Parseltongue.Technique;
        DefaultParseltongueCustomTriggers = string.Join(Environment.NewLine, AppSettings.Generation.Parseltongue.CustomTriggers);
        DefaultHedgeReducerEnabled = AppSettings.Generation.SemanticTransform.HedgeReducerEnabled;
        DefaultDirectModeEnabled = AppSettings.Generation.SemanticTransform.DirectModeEnabled;
        DefaultCasualModeEnabled = AppSettings.Generation.SemanticTransform.CasualModeEnabled;
        DefaultAutoTuneEnabled = AppSettings.Generation.AutoTune.Enabled;
        DefaultAutoTuneStrategy = AppSettings.Generation.AutoTune.Strategy;
        DefaultMultiModelRacingEnabled = AppSettings.Generation.MultiModelRacing.Enabled;
        DefaultRacingModelCount = AppSettings.Generation.MultiModelRacing.ModelCount;
        await ReloadCollectionsAsync(cancellationToken);
        themes.Apply(AppSettings.Appearance);
        _appearanceReady = true;
        AppearanceStatus = LocalizationService.Get("Changes are saved automatically.");
        GenerationStatus = LocalizationService.Get("Changes are saved automatically.");
        PermissionsStatus = LocalizationService.Get("Changes are saved automatically.");
    }

    public async Task ReloadCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var selectedId = SelectedConnection?.Id;
        Connections.ReplaceWith(await connections.ListAsync(cancellationToken));
        SelectedConnection = selectedId is { } id ? Connections.FirstOrDefault(item => item.Id == id) : Connections.FirstOrDefault(item => item.IsActive);
        Memories.ReplaceWith(await memory.ListAsync(cancellationToken: cancellationToken));
        KnowledgeSources.ReplaceWith(await knowledge.ListSourcesAsync(cancellationToken));
        ScheduledTasks.ReplaceWith(await scheduled.ListAsync(cancellationToken));
        PermissionRules.ReplaceWith(await permissions.ListAsync(cancellationToken));
        OnPropertyChanged(nameof(ReliabilityConnectionLabel));
        RunReliabilityCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsConnectionBusy = true;
        ConnectionStatus = LocalizationService.Get("Testing…");
        var temporarySecretIds = new List<string>();
        try
        {
            var profile = await BuildProbeProfileAsync(temporarySecretIds.Add);
            var result = await providers.Create(profile).TestConnectionAsync(profile);
            ConnectionStatus = result.Success ? $"✓ {result.Message} ({result.Latency.TotalMilliseconds:0} ms)" : LocalizationService.Format("Connection failed: {0}", result.Message);
        }
        catch (Exception ex)
        {
            ConnectionStatus = LocalizationService.Format("Connection failed: {0}", ex.Message);
        }
        finally
        {
            foreach (var secretId in temporarySecretIds)
                await secrets.DeleteAsync(secretId);
            IsConnectionBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        IsConnectionBusy = true;
        ConnectionStatus = LocalizationService.Get("Loading models…");
        var temporarySecretIds = new List<string>();
        try
        {
            var profile = await BuildProbeProfileAsync(temporarySecretIds.Add);
            var models = await providers.Create(profile).ListModelsAsync(profile);
            AvailableModels.ReplaceWith(models.Where(model => !ImageGenerationPolicy.IsImageModel(model)));
            if (string.IsNullOrWhiteSpace(Model) && AvailableModels.Count > 0)
                Model = AvailableModels[0];
            ConnectionStatus = models.Count == 0 ? LocalizationService.Get("The endpoint returned no models; manual model entry remains available.") : LocalizationService.Format("Loaded {0} models.", models.Count);
        }
        catch (Exception ex)
        {
            ConnectionStatus = LocalizationService.Format("Could not load models: {0}. Enter a model manually.", ex.Message);
        }
        finally
        {
            foreach (var secretId in temporarySecretIds)
                await secrets.DeleteAsync(secretId);
            IsConnectionBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveConnectionAsync()
    {
        ConnectionStatus = LocalizationService.Get("Saving…");
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            ConnectionStatus = LocalizationService.Get("Base URL must be an absolute HTTP or HTTPS URL.");
            return;
        }
        if (string.IsNullOrWhiteSpace(ConnectionName) || string.IsNullOrWhiteSpace(Model))
        {
            ConnectionStatus = LocalizationService.Get("Connection name and model are required.");
            return;
        }

        IsConnectionBusy = true;
        var newSecretIds = new List<string>();
        var oldSecretIds = GetSecretIds(SelectedConnection).ToArray();
        var saved = false;
        try
        {
            var profile = await BuildProbeProfileAsync(newSecretIds.Add, Connections.Count == 0 || SelectedConnection?.IsActive == true);
            await connections.SaveAsync(profile);
            saved = true;
            SelectedConnection = profile;
            foreach (var obsoleteId in oldSecretIds.Except(GetSecretIds(profile), StringComparer.Ordinal))
                await secrets.DeleteAsync(obsoleteId);
            ApiKey = string.Empty;
            await ReloadCollectionsAsync();
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
            ConnectionStatus = LocalizationService.Get("Connection saved.");
        }
        catch (Exception ex)
        {
            ConnectionStatus = LocalizationService.Format("Could not save connection: {0}", ex.Message);
        }
        finally
        {
            if (!saved)
                foreach (var secretId in newSecretIds) await secrets.DeleteAsync(secretId);
            IsConnectionBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteConnectionAsync()
    {
        var profile = SelectedConnection;
        if (profile is null)
            return;
        await connections.DeleteAsync(profile.Id);
        foreach (var secretId in GetSecretIds(profile))
            await secrets.DeleteAsync(secretId);
        SelectedConnection = null;
        ClearConnectionEditor();
        await ReloadCollectionsAsync();
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void NewConnection()
    {
        SelectedConnection = null;
        ClearConnectionEditor();
    }

    [RelayCommand]
    private void AddBackupApiKey() => BackupApiKeys.Add(new(null, item => BackupApiKeys.Remove(item)));

    [RelayCommand]
    private async Task SetActiveConnectionAsync()
    {
        if (SelectedConnection is null)
            return;
        var selectedId = SelectedConnection.Id;
        SelectedConnection.IsActive = true;
        SelectedConnection.UpdatedAt = DateTimeOffset.UtcNow;
        await connections.SaveAsync(SelectedConnection);
        AppSettings.ActiveConnectionId = selectedId;
        await settingsRepository.SaveAsync(AppSettings);
        await ReloadCollectionsAsync();
        SelectedConnection = Connections.FirstOrDefault(item => item.Id == selectedId);
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        ConnectionStatus = LocalizationService.Format("{0} is now active.", SelectedConnection?.Name ?? LocalizationService.Get("Connection"));
    }

    [RelayCommand(CanExecute = nameof(CanRunReliability))]
    private async Task RunReliabilityAsync()
    {
        var connection = Connections.FirstOrDefault(item => item.IsActive);
        if (connection is null)
        {
            ReliabilityStatus = LocalizationService.Get("Activate a connection before running the suite.");
            return;
        }

        _reliabilityRun?.Cancel();
        _reliabilityRun?.Dispose();
        _reliabilityRun = new CancellationTokenSource();
        IsReliabilityRunning = true;
        ReliabilityStatus = LocalizationService.Format("Running reliability probes against {0} / {1}…", connection.Name, connection.Model);
        ReliabilityResults.Clear();
        ReliabilityReport = string.Empty;
        OnPropertyChanged(nameof(HasReliabilityReport));
        try
        {
            var options = new ModelReliabilityOptions(
                TestInstructionHierarchy,
                TestObfuscationRobustness,
                TestRefusalCalibration,
                TestAssistantPrefill);
            var report = await reliabilityEvaluator.RunAsync(connection, options, _reliabilityRun.Token);
            ReliabilityResults.ReplaceWith(report.Probes);
            ReliabilityReport = report.Markdown;
            OnPropertyChanged(nameof(HasReliabilityReport));
            ReliabilityStatus = LocalizationService.Format("Completed: {0}/{1} passed, {2} unsupported, {3} errors.", report.PassedCount, report.Probes.Count, report.UnsupportedCount, report.ErrorCount);
        }
        catch (OperationCanceledException) when (_reliabilityRun.IsCancellationRequested)
        {
            ReliabilityStatus = LocalizationService.Get("Reliability evaluation stopped.");
        }
        catch (Exception ex)
        {
            ReliabilityStatus = LocalizationService.Format("Reliability evaluation failed: {0}", ex.Message);
        }
        finally
        {
            IsReliabilityRunning = false;
            _reliabilityRun?.Dispose();
            _reliabilityRun = null;
        }
    }

    [RelayCommand]
    private void StopReliability() => _reliabilityRun?.Cancel();

    [RelayCommand]
    private void ClearReliabilityReport()
    {
        ReliabilityResults.Clear();
        ReliabilityReport = string.Empty;
        ReliabilityStatus = LocalizationService.Get("Report cleared.");
        OnPropertyChanged(nameof(HasReliabilityReport));
    }

    [RelayCommand]
    private async Task SaveAppearanceAsync()
    {
        UpdateAppearanceSettings();
        var version = ++_appearanceChangeVersion;
        themes.Apply(AppSettings.Appearance);
        AppearanceApplied?.Invoke(this, AppSettings.Appearance);
        AppearanceStatus = LocalizationService.Get("Saving…");
        await settingsRepository.SaveAsync(AppSettings);
        if (version == _appearanceChangeVersion)
            AppearanceStatus = LocalizationService.Get("Saved.");
    }

    partial void OnThemeChanged(string value) => ApplyAppearanceLive();
    partial void OnAccentColorChanged(string value) => ApplyAppearanceLive();
    partial void OnFontSizeChanged(double value) => ApplyAppearanceLive();
    partial void OnMessageFontSizeChanged(double value) => ApplyAppearanceLive();
    partial void OnUiScaleChanged(double value) => ApplyAppearanceLive();
    partial void OnSidebarWidthChanged(double value) => ApplyAppearanceLive();
    partial void OnDensityChanged(UiDensity value) => ApplyAppearanceLive();
    partial void OnCornerRadiusChanged(double value) => ApplyAppearanceLive();
    partial void OnTransparencyChanged(double value) => ApplyAppearanceLive();
    partial void OnAnimationsChanged(AnimationLevel value) => ApplyAppearanceLive();
    partial void OnLongTermMemoryEnabledChanged(bool value) => ApplyAppearanceLive();
    partial void OnNotificationsEnabledChanged(bool value) => ApplyAppearanceLive();
    partial void OnFullAutonomyEnabledChanged(bool value) => ApplyPermissionSettingsLive();
    partial void OnInterfaceLanguageChanged(string value) => ApplyInterfaceLanguage(value);
    partial void OnAgentMaxIterationsChanged(int value)
    {
        if(AgentHardMaxIterations<value){AgentHardMaxIterations=value;return;}
        ApplyRuntimeSettingsLive();
    }
    partial void OnAgentHardMaxIterationsChanged(int value)
    {
        if(value<AgentMaxIterations){AgentHardMaxIterations=AgentMaxIterations;return;}
        ApplyRuntimeSettingsLive();
    }
    partial void OnCodingAgentMaxIterationsChanged(int value) => ApplyRuntimeSettingsLive();
    partial void OnCodingAgentTimeoutMinutesChanged(int value) => ApplyRuntimeSettingsLive();
    partial void OnAgentRetryCountChanged(int value) => ApplyRuntimeSettingsLive();
    partial void OnDefaultSystemPromptChanged(string value) => ApplyGenerationSettingsLive();
    partial void OnDefaultPromptPresetChanged(SystemPromptPreset value) { OnPropertyChanged(nameof(SelectedPromptPreset)); ApplyGenerationSettingsLive(); }
    partial void OnDefaultTemperatureChanged(double value) => ApplyGenerationSettingsLive();
    partial void OnDefaultTopPChanged(double value) => ApplyGenerationSettingsLive();
    partial void OnDefaultTopKValueChanged(double value) => ApplyGenerationSettingsLive();
    partial void OnDefaultMaxTokensChanged(int value) => ApplyGenerationSettingsLive();
    partial void OnDefaultPresencePenaltyChanged(double value) => ApplyGenerationSettingsLive();
    partial void OnDefaultFrequencyPenaltyChanged(double value) => ApplyGenerationSettingsLive();
    partial void OnDefaultRepetitionPenaltyValueChanged(double value) => ApplyGenerationSettingsLive();
    partial void OnDefaultSeedValueChanged(double value) => ApplyGenerationSettingsLive();
    partial void OnDefaultStopSequencesChanged(string value) => ApplyGenerationSettingsLive();
    partial void OnDefaultEnableToolsChanged(bool value) => ApplyGenerationSettingsLive();
    partial void OnDefaultParseltongueEnabledChanged(bool value) => ApplyGenerationSettingsLive();
    partial void OnDefaultParseltongueIntensityChanged(ParseltongueIntensity value) { OnPropertyChanged(nameof(SelectedPerturbationIntensity)); ApplyGenerationSettingsLive(); }
    partial void OnDefaultParseltongueTechniqueChanged(ParseltongueTechnique value) { OnPropertyChanged(nameof(SelectedPerturbationTechnique)); ApplyGenerationSettingsLive(); }
    partial void OnDefaultParseltongueCustomTriggersChanged(string value) => ApplyGenerationSettingsLive();
    partial void OnDefaultHedgeReducerEnabledChanged(bool value) => ApplyGenerationSettingsLive();
    partial void OnDefaultDirectModeEnabledChanged(bool value) => ApplyGenerationSettingsLive();
    partial void OnDefaultCasualModeEnabledChanged(bool value) => ApplyGenerationSettingsLive();
    partial void OnDefaultAutoTuneEnabledChanged(bool value) => ApplyGenerationSettingsLive();
    partial void OnDefaultAutoTuneStrategyChanged(AutoTuneStrategy value) { OnPropertyChanged(nameof(SelectedSamplingStrategy)); ApplyGenerationSettingsLive(); }
    partial void OnDefaultMultiModelRacingEnabledChanged(bool value) => ApplyGenerationSettingsLive();
    partial void OnDefaultRacingModelCountChanged(int value) => ApplyGenerationSettingsLive();
    partial void OnIsReliabilityRunningChanged(bool value) => RunReliabilityCommand.NotifyCanExecuteChanged();

    private void ApplyAppearanceLive()
    {
        if (!_appearanceReady)
            return;
        UpdateAppearanceSettings();
        themes.Apply(AppSettings.Appearance);
        AppearanceApplied?.Invoke(this, AppSettings.Appearance);
        AppearanceStatus = LocalizationService.Get("Saving…");
        var version = ++_appearanceChangeVersion;
        _ = SaveAppearanceAfterDelayAsync(version);
    }

    private void UpdateAppearanceSettings()
    {
        AppSettings.Appearance.Theme = Theme;
        AppSettings.Appearance.AccentColor = AccentColor;
        AppSettings.Appearance.FontSize = Math.Clamp(FontSize, 11, 22);
        AppSettings.Appearance.MessageFontSize = Math.Clamp(MessageFontSize, 12, 24);
        AppSettings.Appearance.UiScale = Math.Clamp(UiScale, 0.8, 1.4);
        AppSettings.Appearance.SidebarWidth = Math.Clamp(SidebarWidth, 220, 420);
        AppSettings.Appearance.Density = Density;
        AppSettings.Appearance.CornerRadius = Math.Clamp(CornerRadius, 0, 24);
        AppSettings.Appearance.Transparency = Math.Clamp(Transparency, 0.65, 1);
        AppSettings.Appearance.Animations = Animations;
        AppSettings.LongTermMemoryEnabled = LongTermMemoryEnabled;
        AppSettings.NotificationsEnabled = NotificationsEnabled;
    }

    private void ApplyInterfaceLanguage(string value)
    {
        if (!_appearanceReady)
            return;
        InterfaceLanguage = LocalizationService.NormalizeLanguage(value);
        AppSettings.InterfaceLanguage = InterfaceLanguage;
        LocalizationService.SavePreference(InterfaceLanguage);
        LanguageStatus = LocalizationService.Get("Language selection saved. Restart Neuroset to apply it everywhere.");
        var version = ++_appearanceChangeVersion;
        _ = SaveAppearanceAfterDelayAsync(version);
    }

    [RelayCommand]
    private void RestartApplication()
    {
        LocalizationService.SavePreference(InterfaceLanguage);
        LocalizationService.RestartApplication();
    }

    private void ApplyRuntimeSettingsLive()
    {
        if (!_appearanceReady)
            return;
        AppSettings.AgentMaxIterations = Math.Clamp(AgentMaxIterations, 1, 1000);
        AppSettings.AgentHardMaxIterations = Math.Clamp(AgentHardMaxIterations, AppSettings.AgentMaxIterations, 1000);
        AppSettings.CodingAgentMaxIterations = Math.Clamp(CodingAgentMaxIterations, 16, 1000);
        AppSettings.CodingAgentTimeoutMinutes = Math.Clamp(CodingAgentTimeoutMinutes, 1, 720);
        AppSettings.AgentRetryCount = Math.Clamp(AgentRetryCount, 0, 10);
        AppearanceStatus = LocalizationService.Get("Saving…");
        var version = ++_appearanceChangeVersion;
        _ = SaveAppearanceAfterDelayAsync(version);
    }

    private void ApplyPermissionSettingsLive()
    {
        if (!_appearanceReady)
            return;
        AppSettings.FullAutonomyEnabled = FullAutonomyEnabled;
        PermissionsStatus = LocalizationService.Get("Saving…");
        var version = ++_permissionsChangeVersion;
        _ = SavePermissionSettingsAfterDelayAsync(version);
    }

    partial void OnReasoningChanged(ReasoningPreference value) => ApplyGenerationSettingsLive();

    public async Task SelectActiveModelAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || ImageGenerationPolicy.IsImageModel(model)) return;
        var profiles = await connections.ListAsync();
        var active = profiles.FirstOrDefault(item => item.IsActive) ?? (profiles.Count == 0 ? null : profiles[0]);
        if (active is null || active.Model == model) return;
        active.Model = model;
        active.UpdatedAt = DateTimeOffset.UtcNow;
        await connections.SaveAsync(active);
        // Keep an open connection editor in sync without discarding its other drafts.
        var saved = Connections.FirstOrDefault(item => item.Id == active.Id);
        if (saved is not null) saved.Model = model;
        if (SelectedConnection?.Id == active.Id) Model = model;
        OnPropertyChanged(nameof(ReliabilityConnectionLabel));
        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyGenerationSettingsLive()
    {
        if (!_appearanceReady)
            return;
        AppSettings.ReasoningPreference = Reasoning;
        AppSettings.Generation = new GenerationSettings
        {
            SystemPrompt = DefaultSystemPrompt,
            PromptPreset = DefaultPromptPreset,
            Temperature = DefaultTemperature,
            TopP = DefaultTopP,
            TopK = double.IsNaN(DefaultTopKValue) ? null : (int)Math.Clamp(DefaultTopKValue, 1, 100),
            MaxTokens = DefaultMaxTokens,
            PresencePenalty = DefaultPresencePenalty,
            FrequencyPenalty = DefaultFrequencyPenalty,
            RepetitionPenalty = double.IsNaN(DefaultRepetitionPenaltyValue) ? null : Math.Clamp(DefaultRepetitionPenaltyValue, 0, 2),
            Seed = double.IsNaN(DefaultSeedValue) ? null : (int)Math.Clamp(DefaultSeedValue, int.MinValue, int.MaxValue),
            StopSequences = DefaultStopSequences.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            EnableTools = DefaultEnableTools,
            Parseltongue = new ParseltongueSettings
            {
                Enabled = DefaultParseltongueEnabled,
                Intensity = DefaultParseltongueIntensity,
                Technique = DefaultParseltongueTechnique,
                CustomTriggers = DefaultParseltongueCustomTriggers.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                Seed = AppSettings.Generation.Parseltongue.Seed
            },
            SemanticTransform = new SemanticTransformationSettings
            {
                HedgeReducerEnabled = DefaultHedgeReducerEnabled,
                DirectModeEnabled = DefaultDirectModeEnabled,
                CasualModeEnabled = DefaultCasualModeEnabled
            },
            AutoTune = new AutoTuneSettings { Enabled = DefaultAutoTuneEnabled, Strategy = DefaultAutoTuneStrategy },
            MultiModelRacing = new MultiModelRacingSettings
            {
                Enabled = DefaultMultiModelRacingEnabled,
                ModelCount = DefaultRacingModelCount
            }
        }.Normalize();
        GenerationStatus = LocalizationService.Get("Saving…");
        var version = ++_generationChangeVersion;
        _ = SaveGenerationAfterDelayAsync(version);
    }

    private async Task SaveGenerationAfterDelayAsync(int version)
    {
        try
        {
            await Task.Delay(350);
            if (version != _generationChangeVersion)
                return;
            await settingsRepository.SaveAsync(AppSettings);
            if (version == _generationChangeVersion)
                GenerationStatus = LocalizationService.Get("Saved.");
        }
        catch (Exception ex)
        {
            if (version == _generationChangeVersion)
                GenerationStatus = LocalizationService.Format("Could not save settings: {0}", ex.Message);
        }
    }

    private async Task SaveAppearanceAfterDelayAsync(int version)
    {
        try
        {
            await Task.Delay(350);
            if (version != _appearanceChangeVersion)
                return;
            await settingsRepository.SaveAsync(AppSettings);
            if (version == _appearanceChangeVersion)
                AppearanceStatus = LocalizationService.Get("Saved.");
        }
        catch (Exception ex)
        {
            if (version == _appearanceChangeVersion)
                AppearanceStatus = LocalizationService.Format("Could not save settings: {0}", ex.Message);
        }
    }

    private async Task SavePermissionSettingsAfterDelayAsync(int version)
    {
        try
        {
            await Task.Delay(350);
            if (version != _permissionsChangeVersion)
                return;
            await settingsRepository.SaveAsync(AppSettings);
            if (version == _permissionsChangeVersion)
                PermissionsStatus = LocalizationService.Get("Saved.");
        }
        catch (Exception ex)
        {
            if (version == _permissionsChangeVersion)
                PermissionsStatus = LocalizationService.Format("Could not save settings: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ClearMemoryAsync()
    {
        await memory.ClearAsync();
        await ReloadCollectionsAsync();
    }

    [RelayCommand]
    private async Task DeleteMemoryAsync(MemoryRecord? item)
    {
        if (item is null)
            return;
        await memory.DeleteAsync(item.Id);
        Memories.Remove(item);
    }

    [RelayCommand]
    private async Task DeleteKnowledgeAsync(KnowledgeSource? item)
    {
        if (item is null)
            return;
        await knowledge.DeleteSourceAsync(item.Id);
        KnowledgeSources.Remove(item);
    }

    [RelayCommand]
    private async Task DeleteScheduledTaskAsync(ScheduledTaskDefinition? item)
    {
        if (item is null)
            return;
        await scheduled.DeleteAsync(item.Id);
        ScheduledTasks.Remove(item);
    }

    partial void OnSelectedConnectionChanged(ConnectionProfile? value)
    {
        ClearBackupApiKeys();
        if (value is null)
            return;
        ConnectionName = value.Name;
        BaseUrl = value.BaseUrl;
        ApiKey = string.Empty;
        foreach (var secretId in value.BackupApiKeySecretIds)
            BackupApiKeys.Add(new(secretId, item => BackupApiKeys.Remove(item)));
        Model = value.Model;
        Organization = value.Organization ?? string.Empty;
        TimeoutSeconds = value.TimeoutSeconds;
        Temperature = value.Temperature;
        MaxTokens = value.MaxTokens;
        AvailableModels.Clear();
        ConnectionStatus = value.IsActive ? LocalizationService.Get("Active connection") : string.Empty;
    }

    private async Task<ConnectionProfile> BuildProbeProfileAsync(Action<string> registerTemporarySecret, bool active = false)
    {
        var secretId = SelectedConnection?.ApiKeySecretId ?? string.Empty;
        var profile = Build(secretId, active);
        var primaryKey = ApiKey.Trim();
        var backups = BackupApiKeys.Select(item => (item.SecretId, ApiKey: item.ApiKey.Trim())).ToArray();
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            secretId = await secrets.StoreAsync(primaryKey);
            registerTemporarySecret(secretId);
            profile.ApiKeySecretId = secretId;
        }
        foreach (var backup in backups)
        {
            var backupId = backup.SecretId;
            if (!string.IsNullOrWhiteSpace(backup.ApiKey))
            {
                backupId = await secrets.StoreAsync(backup.ApiKey);
                registerTemporarySecret(backupId);
            }
            if (!string.IsNullOrWhiteSpace(backupId)) profile.BackupApiKeySecretIds.Add(backupId);
        }
        return profile;
    }

    private static IEnumerable<string> GetSecretIds(ConnectionProfile? profile) => profile is null ? [] :
        new[] { profile.ApiKeySecretId }.Concat(profile.BackupApiKeySecretIds)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal);

    private void ClearBackupApiKeys()
    {
        foreach (var item in BackupApiKeys) item.ApiKey = string.Empty;
        BackupApiKeys.Clear();
    }

    private ConnectionProfile Build(string secretId, bool active) => new()
    {
        Id = SelectedConnection?.Id ?? Guid.NewGuid(),
        Name = ConnectionName.Trim(),
        BaseUrl = BaseUrl.Trim().TrimEnd('/'),
        ApiKeySecretId = secretId,
        Model = Model.Trim(),
        Organization = string.IsNullOrWhiteSpace(Organization) ? null : Organization.Trim(),
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 900),
        Temperature = Math.Clamp(Temperature, 0, 2),
        MaxTokens = Math.Clamp(MaxTokens, 1, 1_000_000),
        IsActive = active,
        CreatedAt = SelectedConnection?.CreatedAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private void ClearConnectionEditor()
    {
        ClearBackupApiKeys();
        ConnectionName = "OpenAI-compatible";
        BaseUrl = "https://api.openai.com/v1";
        ApiKey = string.Empty;
        Model = string.Empty;
        Organization = string.Empty;
        TimeoutSeconds = 120;
        Temperature = 0.7;
        MaxTokens = 4096;
        AvailableModels.Clear();
        ConnectionStatus = string.Empty;
    }

    public void Dispose()
    {
        _reliabilityRun?.Cancel();
        _reliabilityRun?.Dispose();
        _reliabilityRun = null;
        GC.SuppressFinalize(this);
    }
}

internal static class ObservableCollectionExtensions
{
    public static void ReplaceWith<T>(this ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }
}
