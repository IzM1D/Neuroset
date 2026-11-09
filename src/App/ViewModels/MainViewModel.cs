using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neuroset.Application;
using Neuroset.Domain;
using L = Neuroset.App.Localization.LocalizationService;

namespace Neuroset.App.ViewModels;

public partial class MainViewModel(
    IConversationRepository conversations,
    IConnectionRepository connections,
    ISettingsRepository settingsRepository,
    IAgentRuntime agent,
    IRoleplayRuntime roleplay,
    IAttachmentService attachments,
    SettingsViewModel settings,
    ILLMProviderFactory providers) : ObservableObject, IDisposable
{
    public ObservableCollection<Conversation> Conversations { get; } = [];
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<AttachmentInfo> PendingAttachments { get; } = [];

    /// <summary>
    /// Very large pastes are held here instead of in the editor: a WinUI TextBox lays out its whole
    /// content on the UI thread, so a multi-hundred-kilobyte paste freezes the window. The text is
    /// still sent verbatim — <see cref="ComposerTextPolicy.Compose"/> joins it back on send.
    /// </summary>
    public ObservableCollection<PastedTextBlock> PastedTextBlocks { get; } = [];
    public SettingsViewModel Settings { get; } = settings;

    [ObservableProperty] public partial Conversation? CurrentConversation { get; set; }
    [ObservableProperty] public partial string ComposerText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsGenerating { get; set; }
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }
    [ObservableProperty] public partial bool IsFirstRun { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = L.Get("Ready");
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial string ActiveModel { get; set; } = L.Get("No model connected");
    [ObservableProperty] public partial double SidebarWidth { get; set; } = 280;
    [ObservableProperty] public partial Guid? EditingMessageId { get; set; }
    [ObservableProperty] public partial MessageRole? EditingMessageRole { get; set; }
    public ReasoningPreference SelectedReasoning => Settings.Reasoning;
    public ObservableCollection<string> ModelOptions { get; } = [];
    private IReadOnlyList<string> _chatModels = [];
    private string _modelFilter = string.Empty;
    [ObservableProperty] public partial bool IsLoadingModels { get; set; }
    [ObservableProperty] public partial bool IsSwitchingModel { get; set; }
    [ObservableProperty] public partial string ModelPickerStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string? AvailableImageModel { get; set; }
    [ObservableProperty] public partial bool IsImageGenerationRequested { get; set; }

    public bool IsImageGenerationAvailable => AvailableImageModel is not null && !IsRoleplayConversation;
    public bool CanRequestImageGeneration => IsImageGenerationAvailable && !IsGenerating && EditingMessageRole != MessageRole.Assistant;
    public string ImageGenerationTooltip => L.Get(IsImageGenerationRequested
        ? "Image generation enabled for this message. Click to disable."
        : "Generate an image for this message");

    public Microsoft.UI.Xaml.GridLength SidebarGridWidth => new(SidebarWidth);
    public bool HasConversation => CurrentConversation is not null;
    public bool HasPastedText => PastedTextBlocks.Count > 0;
    public bool IsRoleplayConversation => CurrentConversation?.Mode == ConversationMode.Roleplay;
    public bool IsRoleplayPaused => IsRoleplayConversation && CurrentConversation!.Roleplay.IsPaused;
    public bool CanSendMessage => !IsGenerating && ComposerTextPolicy.HasContent(ComposerText, PastedTextBlocks) &&
        (!IsRoleplayConversation || (!IsRoleplayPaused && CurrentConversation!.Roleplay.PendingSpeakerId.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase)));
    public string RoleplayPauseButtonText => L.Get(IsRoleplayPaused ? "Resume" : "Pause");
    public string RoleplayTurnText
    {
        get
        {
            if (!IsRoleplayConversation) return string.Empty;
            var settings = CurrentConversation!.Roleplay;
            var isUserTurn = settings.PendingSpeakerId.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase);
            var next = isUserTurn
                ? string.IsNullOrWhiteSpace(settings.PlayerName) ? L.Get("Your turn") : L.Format("Your turn as {0}", settings.PlayerName.Trim())
                : settings.Characters.FirstOrDefault(character => character.Id.ToString("D").Equals(settings.PendingSpeakerId, StringComparison.OrdinalIgnoreCase))?.Name is { Length: > 0 } name
                    ? L.Format("Next turn: {0}", name)
                    : L.Get("Waiting for the next turn");
            if (settings.IsPaused) return L.Format("Roleplay paused · {0}", next);
            return next;
        }
    }
    public string CurrentConversationTitle => string.IsNullOrWhiteSpace(CurrentConversation?.Title) ? L.Get("New chat") : CurrentConversation.Title;
    public string CurrentConversationSubtitle => CurrentConversation switch
    {
        { Mode: ConversationMode.Coding, WorkspacePath: { Length: > 0 } workspace, UseCustomGenerationSettings: true } => L.Format("Coding · Custom generation · Full access · {0}", workspace),
        { Mode: ConversationMode.Coding, WorkspacePath: { Length: > 0 } workspace } => L.Format("Coding · Global generation · Full access · {0}", workspace),
        { Mode: ConversationMode.Coding, UseCustomGenerationSettings: true } => L.Get("Coding · Custom generation · Working directory unavailable"),
        { Mode: ConversationMode.Coding } => L.Get("Coding · Global generation · Working directory unavailable"),
        { Mode: ConversationMode.Roleplay, Roleplay.Characters.Count: 1 } => L.Format("Roleplay · {0} · Tools disabled", CurrentConversation.Roleplay.Characters[0].Name),
        { Mode: ConversationMode.Roleplay } => L.Format("Roleplay · {0} characters · Tools disabled", CurrentConversation.Roleplay.Characters.Count),
        { UseCustomGenerationSettings: true } => L.Get("Chat · Custom generation settings"),
        _ => L.Get("Chat · Global generation settings")
    };
    public string EditingMessageDescription => EditingMessageRole == MessageRole.Assistant
        ? L.Get("Editing an assistant response creates another saved version.")
        : L.Get("Editing your message creates a new branch and regenerates the response.");
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsEditingMessage => EditingMessageId is not null;

    private CancellationTokenSource? _generation;
    private CancellationTokenSource? _messageLoad;
    private CancellationTokenSource? _imageModelLoad;
    private long _messageLoadVersion;
    private bool _settingsEventsHooked;
    private readonly List<string> _composerHistory = [];
    private int _composerHistoryIndex;
    private string _composerHistoryDraft = string.Empty;
    private bool _navigatingComposerHistory;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _generationProgressTimer;
    private ChatMessageViewModel? _timedGenerationMessage;
    private bool _roleplayAdvanceRunning;
    private bool _suppressRoleplayAutoAdvance;

    public async Task InitializeAsync()
    {
        await Settings.LoadAsync();
        if (!_settingsEventsHooked)
        {
            Settings.AppearanceApplied += OnAppearanceApplied;
            Settings.ConnectionsChanged += OnConnectionsChanged;
            _settingsEventsHooked = true;
        }
        SidebarWidth = Settings.SidebarWidth;
        await ReloadConversationsAsync();
        var active = await GetActiveConnectionAsync();
        IsFirstRun = !Settings.AppSettings.FirstRunCompleted || active is null;
        ActiveModel = active?.Model ?? L.Get("No model connected");
        _ = RefreshImageModelAsync(active);
        CurrentConversation = Conversations.FirstOrDefault();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (_generationProgressTimer is null && dispatcher is not null)
        {
            _generationProgressTimer = dispatcher.CreateTimer();
            _generationProgressTimer.Interval = TimeSpan.FromSeconds(1);
            _generationProgressTimer.Tick += OnGenerationProgressTimerTick;
        }
    }

    public async Task ReloadConversationsAsync(string? search = null)
    {
        var selectedId = CurrentConversation?.Id;
        var items = await conversations.ListAsync(search);
        foreach (var item in items.Where(item => string.IsNullOrWhiteSpace(item.Title) || item.Title == "New chat")) item.Title = L.Get("New chat");
        Conversations.ReplaceWith(items);
        if (selectedId is { } id) CurrentConversation = Conversations.FirstOrDefault(item => item.Id == id) ?? CurrentConversation;
    }

    public Task SearchAsync(string query) => ReloadConversationsAsync(query);

    public void FilterModels(string query)
    {
        _modelFilter = query.Trim();
        ModelOptions.ReplaceWith(_chatModels.Where(model => model.Contains(_modelFilter, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task SelectModelAsync(string model)
    {
        if (IsGenerating || IsSwitchingModel || !_chatModels.Contains(model)) return;
        IsSwitchingModel = true;
        try { await Settings.SelectActiveModelAsync(model); ActiveModel = model; }
        catch (Exception exception)
        {
            ModelPickerStatus = L.Format("Could not change model: {0}", exception.Message);
        }
        finally { IsSwitchingModel = false; }
    }

    public async Task ReloadModelsAsync()
    {
        try { if (!IsLoadingModels) await RefreshImageModelAsync(await GetActiveConnectionAsync()); }
        catch (Exception) { ModelPickerStatus = L.Get("Could not load the model list. Your current model is still available."); }
    }

    partial void OnCurrentConversationChanged(Conversation? value)
    {
        _messageLoad?.Cancel();
        Messages.Clear();
        ResetComposerHistory([]);
        CancelEdit();
        IsImageGenerationRequested = false;
        OnPropertyChanged(nameof(IsImageGenerationAvailable));
        OnPropertyChanged(nameof(CanRequestImageGeneration));
        OnPropertyChanged(nameof(HasConversation));
        OnPropertyChanged(nameof(CurrentConversationTitle));
        OnPropertyChanged(nameof(CurrentConversationSubtitle));
        NotifyRoleplayStateChanged();
        if (value is not null)
        {
            var resumeRoleplay = !_suppressRoleplayAutoAdvance && value.Mode == ConversationMode.Roleplay &&
                !value.Roleplay.IsPaused && !value.Roleplay.PendingSpeakerId.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase);
            _ = LoadConversationAsync(value, resumeRoleplay);
        }
    }

    private async Task LoadConversationAsync(Conversation conversation, bool resumeRoleplay)
    {
        await LoadMessagesAsync(conversation.Id);
        if (resumeRoleplay && CurrentConversation?.Id == conversation.Id) await TryAdvanceRoleplayAsync();
    }

    private async Task LoadMessagesAsync(Guid id)
    {
        var version = Interlocked.Increment(ref _messageLoadVersion);
        _messageLoad?.Cancel();
        _messageLoad?.Dispose();
        _messageLoad = new CancellationTokenSource();
        var token = _messageLoad.Token;
        try
        {
            var loaded = await conversations.GetMessagesAsync(id, token);
            if (token.IsCancellationRequested || version != _messageLoadVersion || CurrentConversation?.Id != id) return;
            var visibleMessages = ConversationMessageGrouping.GroupVisibleResponses(loaded)
                .Select(ChatMessageViewModel.FromDomainGroup);
            Messages.ReplaceWith(visibleMessages);
            ResetComposerHistory(loaded.Where(message => message.Role == MessageRole.User).Select(message => message.Content));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version == _messageLoadVersion) ErrorMessage = ex.Message;
        }
    }

    public async Task<Conversation> CreateConversationAsync(ConversationMode mode, string? workspacePath = null, RoleplaySettings? roleplaySettings = null)
    {
        var normalizedWorkspace = mode == ConversationMode.Coding
            ? Path.GetFullPath(workspacePath ?? throw new ArgumentException(L.Get("A working directory is required for coding chats."), nameof(workspacePath)))
            : null;
        if (normalizedWorkspace is not null) Directory.CreateDirectory(normalizedWorkspace);
        var conversation = new Conversation
        {
            Title = L.Get("New chat"),
            Mode = mode,
            WorkspacePath = normalizedWorkspace,
            FullAccessEnabled = mode == ConversationMode.Coding,
            Roleplay = roleplaySettings ?? new RoleplaySettings()
        };
        if (mode == ConversationMode.Roleplay)
        {
            conversation.Generation.EnableTools = false;
            conversation.Roleplay.StartingSpeakerId = RoleplayTurnOrder.NormalizeSpeakerId(conversation.Roleplay, conversation.Roleplay.StartingSpeakerId);
            conversation.Roleplay.PendingSpeakerId = conversation.Roleplay.StartingSpeakerId;
        }
        await conversations.SaveAsync(conversation);
        Conversations.Insert(0, conversation);
        CurrentConversation = conversation;
        IsSettingsOpen = false;
        return conversation;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!CanSendMessage) return;
        var requestStartedAt = DateTimeOffset.UtcNow;
        var text = ComposerTextPolicy.Compose(ComposerText, PastedTextBlocks);
        if (string.IsNullOrWhiteSpace(text)) return;
        var generateImage = IsImageGenerationRequested && CanRequestImageGeneration;
        IsGenerating = true;
        _generation?.Dispose();
        _generation = new CancellationTokenSource();
        try
        {
            var connection = await GetActiveConnectionAsync();
            if (connection is null)
            {
                IsFirstRun = true;
                ErrorMessage = L.Get("Create an API connection before sending a message.");
                return;
            }
            if (CurrentConversation is null) await CreateConversationAsync(ConversationMode.Chat);
            var current = CurrentConversation!;
            IsImageGenerationRequested = false;

            var roleBeingEdited = EditingMessageRole;
            if (EditingMessageId is { } editingId)
            {
                var original = await conversations.GetMessageAsync(editingId);
                if (original is null)
                {
                    CancelEdit();
                    ErrorMessage = L.Get("The message being edited is no longer available.");
                    return;
                }
                await conversations.CreateMessageVersionAsync(editingId, text, PendingAttachments.Count > 0 ? PendingAttachments.ToList() : null);
                PendingAttachments.Clear();
                ClearPastedText();
                CancelEdit(clearComposer: false);
                ComposerText = string.Empty;
                await LoadMessagesAsync(current.Id);
                if (roleBeingEdited == MessageRole.Assistant) return;
            }
            else
            {
                var history = await conversations.GetMessagesAsync(current.Id);
                var message = new ChatMessage
                {
                    ConversationId = current.Id,
                    Role = MessageRole.User,
                    Content = text,
                    ParentMessageId = history.Count == 0 ? null : history[^1].Id,
                    Attachments = PendingAttachments.ToList()
                };
                await conversations.SaveMessageAsync(message);
                PendingAttachments.Clear();
                ClearPastedText();
                ComposerText = string.Empty;
                Messages.Add(ChatMessageViewModel.FromDomain(message));
            }

            AddComposerHistory(text);
            var titleChanged = current.Title is "New chat" || current.Title == L.Get("New chat");
            if (titleChanged)
            {
                current.Title = text.Length > 48 ? text[..48] + "…" : text;
                current.UpdatedAt = DateTimeOffset.UtcNow;
                await conversations.SaveAsync(current);
                OnPropertyChanged(nameof(CurrentConversationTitle));
            }
            if (current.Mode == ConversationMode.Roleplay)
            {
                current.Roleplay.PendingSpeakerId = RoleplayTurnOrder.GetNextSpeakerId(current.Roleplay, RoleplaySpeaker.User);
                await conversations.SaveAsync(current);
                NotifyRoleplayStateChanged();
                await GenerateRoleplayTurnsCoreAsync(current, connection);
            }
            else
            {
                await GenerateAsync(current, connection, generationStartedAt: requestStartedAt, generateImage: generateImage);
            }
            if (titleChanged)
                await ReloadConversationsAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = L.Get("Stopped");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusText = L.Get("Error");
        }
        finally
        {
            if (IsGenerating)
            {
                IsGenerating = false;
                _generation?.Dispose();
                _generation = null;
            }
        }
    }

    [RelayCommand]
    private void EditMessage(ChatMessageViewModel? message)
    {
        if (IsGenerating || message is null || message.Role is not (MessageRole.User or MessageRole.Assistant)) return;
        // Loading a huge body into the TextBox would freeze the window for as long as it takes to lay
        // the whole text out, so an oversized message is not editable in the composer.
        if (message.Content.Length > ComposerTextPolicy.MaximumComposerCharacters)
        {
            ErrorMessage = L.Format("This message is too long to edit in the input box ({0} characters, limit {1}). Copy it and send a new message instead.", message.Content.Length, ComposerTextPolicy.MaximumComposerCharacters);
            return;
        }
        EditingMessageId = message.Id;
        EditingMessageRole = message.Role;
        ComposerText = message.Content;
    }

    [RelayCommand]
    private void CancelEdit() => CancelEdit(clearComposer: true);

    private void CancelEdit(bool clearComposer)
    {
        EditingMessageId = null;
        EditingMessageRole = null;
        if (clearComposer) { ComposerText = string.Empty; ClearPastedText(); }
    }

    [RelayCommand]
    private async Task RegenerateAsync(ChatMessageViewModel? message)
    {
        if (IsGenerating || CurrentConversation is null) return;
        var connection = await GetActiveConnectionAsync();
        if (connection is null) { IsFirstRun = true; return; }
        var history = (await conversations.GetMessagesAsync(CurrentConversation.Id)).ToList();
        var assistant = message is null ? history.LastOrDefault(item => item.Role == MessageRole.Assistant) : history.FirstOrDefault(item => item.Id == message.Id && item.Role == MessageRole.Assistant);
        if (assistant is null) return;
        var assistantIndex = history.FindIndex(item => item.Id == assistant.Id);
        var promptHistory = history.Take(assistantIndex).ToArray();
        if (CurrentConversation.Mode == ConversationMode.Roleplay)
        {
            var character = message?.CharacterId is { } characterId ? CurrentConversation.Roleplay.Characters.FirstOrDefault(item => item.Id == characterId) : null;
            if (character is null) return;
            var roleplayResponseVersion = await conversations.CreateMessageVersionAsync(assistant.Id, string.Empty, []);
            var visibleRoleplayAssistant = Messages.FirstOrDefault(item => item.Id == assistant.Id);
            if (visibleRoleplayAssistant is not null) Messages.Remove(visibleRoleplayAssistant);
            IsGenerating = true;
            _generation?.Dispose();
            _generation = new CancellationTokenSource();
            try { await GenerateRoleplayCharacterAsync(CurrentConversation, character, promptHistory, connection, _generation.Token, roleplayResponseVersion.Id); }
            finally { IsGenerating = false; _generation.Dispose(); _generation = null; }
            return;
        }
        if (promptHistory.LastOrDefault()?.Role != MessageRole.User) return;
        var responseVersion = await conversations.CreateMessageVersionAsync(assistant.Id, string.Empty, []);
        var visibleAssistant = Messages.FirstOrDefault(item => item.Id == assistant.Id);
        if (visibleAssistant is not null) Messages.Remove(visibleAssistant);
        var generateImage = IsImageGenerationRequested && CanRequestImageGeneration;
        IsImageGenerationRequested = false;
        await GenerateAsync(CurrentConversation, connection, promptHistory, responseVersion.Id, DateTimeOffset.UtcNow, generateImage);
    }

    [RelayCommand]
    private Task RetryAsync(ChatMessageViewModel? message) => RegenerateAsync(message);

    [RelayCommand]
    private async Task DeleteMessageAsync(ChatMessageViewModel? message)
    {
        if (message is null || IsGenerating || CurrentConversation is null) return;
        await conversations.DeleteMessageAsync(message.Id);
        await LoadMessagesAsync(CurrentConversation.Id);
    }

    [RelayCommand]
    private async Task PreviousMessageVersionAsync(ChatMessageViewModel? message) => await ActivateAdjacentVersionAsync(message, -1);

    [RelayCommand]
    private async Task NextMessageVersionAsync(ChatMessageViewModel? message) => await ActivateAdjacentVersionAsync(message, 1);

    private async Task ActivateAdjacentVersionAsync(ChatMessageViewModel? message, int direction)
    {
        if (message is null || IsGenerating || CurrentConversation is null) return;
        if (await conversations.ActivateAdjacentMessageVersionAsync(message.Id, direction)) await LoadMessagesAsync(CurrentConversation.Id);
    }

    private async Task GenerateAsync(Conversation conversation, ConnectionProfile connection, IReadOnlyList<ChatMessage>? historyOverride = null, Guid? responseMessageId = null, DateTimeOffset? generationStartedAt = null, bool generateImage = false)
    {
        var startedAt = generationStartedAt ?? DateTimeOffset.UtcNow;
        var streaming = new ChatMessageViewModel { Id = responseMessageId ?? Guid.NewGuid(), Role = MessageRole.Assistant, Timestamp = DateTimeOffset.UtcNow, Status = MessageStatus.Streaming };
        streaming.BeginAgentRun(startedAt);
        Messages.Add(streaming);
        StartGenerationProgressClock(streaming);
        IsGenerating = true;
        StatusText = L.Get("Thinking…");
        ErrorMessage = string.Empty;
        _generation ??= new CancellationTokenSource();
        var failed = false;
        Guid? savedFailureMessageId = null;
        var startsNewAssistantPhase = false;
        var textBatch = new TextDeltaBatcher(TimeSpan.FromMilliseconds(75));
        async ValueTask FlushTextAsync(bool force = false)
        {
            if (!textBatch.TryDrain(out var text, force)) return;
            streaming.Content += text;
            await Task.Yield();
        }
        try
        {
            var history = historyOverride ?? await conversations.GetMessagesAsync(conversation.Id);
            var runOptions = AgentRunOptions.ForConversation(conversation, Settings.AppSettings);
            await foreach (var modelEvent in agent.RunAsync(new AgentRunRequest(conversation, history, connection, runOptions, responseMessageId, startedAt, GenerateImage: generateImage), _generation.Token))
            {
                switch (modelEvent)
                {
                    case TextDeltaEvent delta:
                        var lastCharacter = textBatch.LastCharacter ?? (streaming.Content.Length == 0 ? null : streaming.Content[^1]);
                        if (startsNewAssistantPhase && lastCharacter is { } last && delta.Delta.Length > 0 &&
                            !char.IsWhiteSpace(last) && !char.IsWhiteSpace(delta.Delta[0]))
                            textBatch.Append(Environment.NewLine + Environment.NewLine);
                        startsNewAssistantPhase = false;
                        textBatch.Append(delta.Delta);
                        await FlushTextAsync();
                        StatusText = L.Get("Generating…");
                        break;
                    case ToolCallStartedEvent started: await FlushTextAsync(force: true); streaming.GetOrAddToolActivity(started.CallId,started.Name,L.Get("Preparing…")); StatusText = L.Get("Using tools…"); break;
                    case ToolCallArgumentsEvent arguments: FindTool(streaming, arguments.CallId, "tool").ArgumentsJson += arguments.ArgumentsDelta; break;
                    case ToolExecutionStartedEvent started: FindTool(streaming, started.CallId, started.Name).Status = L.Get("Running…"); break;
                    case ToolExecutionCompletedEvent completed:
                        var tool = FindTool(streaming, completed.CallId, completed.Name);
                        tool.Status = completed.Succeeded ? L.Format("Completed in {0:0.0}s", completed.Duration.TotalSeconds) : L.Get("Failed");
                        tool.Complete(completed.Succeeded,completed.Output,completed.Error,completed.Duration,completed.Metadata);
                        startsNewAssistantPhase = true;
                        if (completed.Succeeded && completed.Name.Equals(PresentationPolicy.ToolName, StringComparison.OrdinalIgnoreCase) &&
                            ChatMessageViewModel.TryCreatePresentation(completed.CallId, completed.Output, out var presentation) &&
                            streaming.Presentations.All(item => item.CallId != presentation.CallId))
                            streaming.Presentations.Add(presentation);
                        if (completed.Succeeded && completed.Name.Equals(ImageGenerationPolicy.ToolName, StringComparison.OrdinalIgnoreCase) &&
                            ChatMessageViewModel.TryCreateGeneratedImage(completed.CallId, completed.Output, out var generatedImage) &&
                            streaming.GeneratedImages.All(item => item.CallId != generatedImage.CallId))
                            streaming.GeneratedImages.Add(generatedImage);
                        if (completed.Succeeded && completed.Name.Equals("render_html", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(streaming.Content)) streaming.Content = L.Get("Interactive visualization");
                            if (ChatMessageViewModel.TryCreateHtmlArtifact(completed.CallId, completed.Output, out var artifact) &&
                                streaming.HtmlArtifacts.All(item => item.CallId != artifact.CallId))
                                streaming.HtmlArtifacts.Add(artifact);
                        }
                        if (completed.Succeeded && completed.Name.Equals("math_canvas", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(streaming.Content)) streaming.Content = L.Get("Mathematical canvas");
                            if (ChatMessageViewModel.TryCreateHtmlArtifact(completed.CallId, completed.Output, out var mathArtifact) &&
                                streaming.HtmlArtifacts.All(item => item.CallId != mathArtifact.CallId))
                                streaming.HtmlArtifacts.Add(mathArtifact);
                        }
                        if (completed.Succeeded && completed.Name.Equals("widgets", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(streaming.Content)) streaming.Content = L.Get("Here is the requested widget.");
                            if (ChatMessageViewModel.TryCreateWidget(completed.CallId, completed.Output, out var widget) &&
                                streaming.Widgets.All(item => item.CallId != widget.CallId))
                                streaming.Widgets.Add(widget);
                        }
                        if (completed.Succeeded && completed.Name.Equals("rich_table", StringComparison.OrdinalIgnoreCase))
                        {
                            if (ChatMessageViewModel.TryCreateRichTable(completed.CallId, completed.Output, out var table) &&
                                streaming.Tables.All(item => item.CallId != table.CallId))
                                streaming.Tables.Add(table);
                        }
                        if (completed.Succeeded && completed.Name.Equals("canvas", StringComparison.OrdinalIgnoreCase))
                        {
                            if (ChatMessageViewModel.TryCreateCanvas(completed.CallId, completed.Output, out var canvas) &&
                                streaming.Canvases.All(item => item.CallId != canvas.CallId))
                                streaming.Canvases.Add(canvas);
                        }
                        break;
                    case AgentProgressEvent progress:
                        streaming.ApplyAgentProgress(progress);
                        break;
                    case ModelRecoveryEvent recovered: StatusText = L.Get(recovered.Message); break;
                    case ModelErrorEvent error: await FlushTextAsync(force: true); failed = true; savedFailureMessageId = error.SavedMessageId; ErrorMessage = error.Message; streaming.Status = MessageStatus.Failed; break;
                }
            }
            await FlushTextAsync(force: true);
            if (failed && savedFailureMessageId is null) await PersistInterruptedMessageAsync(conversation.Id, streaming, MessageStatus.Failed, history.Count == 0 ? null : history[^1].Id, responseMessageId);
            await LoadMessagesAsync(conversation.Id);
            StatusText = L.Get(failed ? "Error" : "Ready");
        }
        catch (OperationCanceledException)
        {
            streaming.Status = MessageStatus.Cancelled;
            var history = await conversations.GetMessagesAsync(conversation.Id);
            await PersistInterruptedMessageAsync(conversation.Id, streaming, MessageStatus.Cancelled, history.Count == 0 ? null : history[^1].Id, responseMessageId);
            await LoadMessagesAsync(conversation.Id);
            StatusText = L.Get("Stopped");
        }
        catch (Exception ex)
        {
            streaming.Status = MessageStatus.Failed;
            ErrorMessage = ex.Message;
            var history = await conversations.GetMessagesAsync(conversation.Id);
            await PersistInterruptedMessageAsync(conversation.Id, streaming, MessageStatus.Failed, history.Count == 0 ? null : history[^1].Id, responseMessageId);
            await LoadMessagesAsync(conversation.Id);
            StatusText = L.Get("Error");
        }
        finally
        {
            IsGenerating = false;
            _generation?.Dispose();
            _generation = null;
            StopGenerationProgressClock(streaming);
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task PersistInterruptedMessageAsync(Guid conversationId, ChatMessageViewModel streaming, MessageStatus status, Guid? parentId, Guid? responseMessageId)
    {
        if (string.IsNullOrWhiteSpace(streaming.Content) && streaming.Tools.Count == 0) return;
        var message = responseMessageId is { } id ? await conversations.GetMessageAsync(id) : null;
        message ??= new ChatMessage { Id = streaming.Id, ConversationId = conversationId, Role = MessageRole.Assistant, CreatedAt = streaming.Timestamp };
        message.Content = streaming.Content;
        message.Status = status;
        message.ParentMessageId = parentId;
        message.UpdatedAt = DateTimeOffset.UtcNow;
        message.Metadata["interrupted"] = "true";
        var storedMessages = await conversations.GetMessagesAsync(conversationId);
        var storedCallIds = storedMessages.Where(stored => stored.Id != message.Id).SelectMany(stored => stored.ToolCalls).Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var tool in streaming.Tools)
        {
            if (!tool.IsCompleted)
            {
                tool.Status = L.Get("Interrupted");
                tool.Complete(false, string.Empty, L.Get("This tool call ended without a result. It can be retried in a new response."));
            }
            if (storedCallIds.Contains(tool.CallId)) continue;
            message.ToolCalls.RemoveAll(call => call.Id == tool.CallId);
            var completedAt = DateTimeOffset.UtcNow;
            message.ToolCalls.Add(new ToolCallRecord
            {
                Id = tool.CallId,
                Name = tool.Name,
                ArgumentsJson = tool.ArgumentsJson,
                Execution = new ToolExecutionRecord
                {
                    ToolCallId = tool.CallId,
                    ToolName = tool.Name,
                    StartedAt = completedAt - (tool.Duration ?? TimeSpan.Zero),
                    CompletedAt = completedAt,
                    Succeeded = tool.Succeeded,
                    Output = tool.RawOutput,
                    Error = tool.Error,
                    Metadata = new Dictionary<string, string>(tool.Metadata)
                }
            });
        }
        streaming.WriteAgentRunMetadata(message.Metadata, DateTimeOffset.UtcNow);
        await conversations.SaveMessageAsync(message);
    }

    public async Task<string> GenerateRoleplayPromptAsync(string description, RoleplayPromptKind kind = RoleplayPromptKind.Character)
    {
        var connection = await GetActiveConnectionAsync() ?? throw new InvalidOperationException(L.Get("Create an API connection before generating a character prompt."));
        return await roleplay.GenerateCharacterPromptAsync(description, connection, kind);
    }

    public async Task<Conversation> CreateRoleplayConversationAsync(IReadOnlyList<RoleplayCharacter> characters, string? sceneDescription = null, string? playerName = null, string? playerDescription = null, string? startingSpeakerId = null)
    {
        var validCharacters = characters
            .Where(character => !string.IsNullOrWhiteSpace(character.Name) && !string.IsNullOrWhiteSpace(character.SystemPrompt))
            .Select(character => new RoleplayCharacter { Id = character.Id, Name = character.Name.Trim(), SystemPrompt = character.SystemPrompt.Trim() })
            .ToList();
        if (validCharacters.Count == 0) throw new InvalidOperationException(L.Get("Add at least one character with a name and system prompt."));
        var settings = new RoleplaySettings
        {
            Characters = validCharacters,
            SceneDescription = sceneDescription?.Trim() ?? string.Empty,
            PlayerName = playerName?.Trim() ?? string.Empty,
            PlayerDescription = playerDescription?.Trim() ?? string.Empty
        };
        settings.StartingSpeakerId = RoleplayTurnOrder.NormalizeSpeakerId(settings, startingSpeakerId);
        settings.PendingSpeakerId = settings.StartingSpeakerId;
        Conversation conversation;
        _suppressRoleplayAutoAdvance = true;
        try
        {
            conversation = await CreateConversationAsync(ConversationMode.Roleplay, roleplaySettings: settings);
            conversation.Title = validCharacters.Count == 1 ? validCharacters[0].Name : string.Join(", ", validCharacters.Take(3).Select(character => character.Name));
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await conversations.SaveAsync(conversation);
            await ReloadConversationsAsync();
            CurrentConversation = Conversations.FirstOrDefault(item => item.Id == conversation.Id) ?? conversation;
        }
        finally
        {
            _suppressRoleplayAutoAdvance = false;
        }
        if (!conversation.Roleplay.PendingSpeakerId.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase))
            _ = TryAdvanceRoleplayAsync();
        return conversation;
    }

    public async Task UpdateRoleplaySettingsAsync(Conversation conversation, IReadOnlyList<RoleplayCharacter> characters, string? sceneDescription = null, string? playerName = null, string? playerDescription = null, string? startingSpeakerId = null)
    {
        if (IsGenerating || conversation.Mode != ConversationMode.Roleplay) return;
        var validCharacters = characters
            .Where(character => !string.IsNullOrWhiteSpace(character.Name) && !string.IsNullOrWhiteSpace(character.SystemPrompt))
            .Select(character => new RoleplayCharacter { Id = character.Id, Name = character.Name.Trim(), SystemPrompt = character.SystemPrompt.Trim() })
            .ToList();
        if (validCharacters.Count == 0) throw new InvalidOperationException(L.Get("A roleplay chat requires at least one character with a system prompt."));
        conversation.Roleplay.Characters = validCharacters;
        conversation.Roleplay.SceneDescription = sceneDescription?.Trim() ?? conversation.Roleplay.SceneDescription;
        conversation.Roleplay.PlayerName = playerName?.Trim() ?? conversation.Roleplay.PlayerName;
        conversation.Roleplay.PlayerDescription = playerDescription?.Trim() ?? conversation.Roleplay.PlayerDescription;
        conversation.Roleplay.StartingSpeakerId = RoleplayTurnOrder.NormalizeSpeakerId(conversation.Roleplay, startingSpeakerId ?? conversation.Roleplay.StartingSpeakerId);
        if (!conversation.Roleplay.PendingSpeakerId.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase) &&
            validCharacters.All(character => !character.Id.ToString("D").Equals(conversation.Roleplay.PendingSpeakerId, StringComparison.OrdinalIgnoreCase)))
            conversation.Roleplay.PendingSpeakerId = RoleplaySpeaker.User;
        conversation.Generation.EnableTools = false;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await conversations.SaveAsync(conversation);
        await ReloadConversationsAsync();
        CurrentConversation = Conversations.FirstOrDefault(item => item.Id == conversation.Id) ?? conversation;
        NotifyRoleplayStateChanged();
    }

    [RelayCommand]
    private async Task ToggleRoleplayPauseAsync()
    {
        var conversation = CurrentConversation;
        if (conversation?.Mode != ConversationMode.Roleplay || IsGenerating) return;
        conversation.Roleplay.IsPaused = !conversation.Roleplay.IsPaused;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await conversations.SaveAsync(conversation);
        NotifyRoleplayStateChanged();
        if (!conversation.Roleplay.IsPaused && !conversation.Roleplay.PendingSpeakerId.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase))
            _ = TryAdvanceRoleplayAsync();
    }

    private async Task TryAdvanceRoleplayAsync()
    {
        var conversation = CurrentConversation;
        if (_suppressRoleplayAutoAdvance || _roleplayAdvanceRunning || IsGenerating || conversation?.Mode != ConversationMode.Roleplay || conversation.Roleplay.IsPaused) return;
        var connection = await GetActiveConnectionAsync();
        if (connection is null) return;
        _roleplayAdvanceRunning = true;
        IsGenerating = true;
        _generation?.Dispose();
        _generation = new CancellationTokenSource();
        StatusText = L.Get("Preparing…");
        try
        {
            await GenerateRoleplayTurnsCoreAsync(conversation, connection);
            StatusText = L.Get("Ready");
        }
        catch (OperationCanceledException)
        {
            StatusText = L.Get("Stopped");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusText = L.Get("Error");
        }
        finally
        {
            IsGenerating = false;
            _generation?.Dispose();
            _generation = null;
            _roleplayAdvanceRunning = false;
            NotifyRoleplayStateChanged();
        }
    }

    private async Task GenerateRoleplayTurnsCoreAsync(Conversation conversation, ConnectionProfile connection)
    {
        var token = _generation?.Token ?? CancellationToken.None;
        var settings = conversation.Roleplay;
        try
        {
            for (var turn = 0; turn < settings.Characters.Count; turn++)
            {
                token.ThrowIfCancellationRequested();
                if (settings.IsPaused) return;
                var history = await conversations.GetMessagesAsync(conversation.Id, token);
                var speakerId = RoleplayTurnOrder.NormalizeSpeakerId(settings, settings.PendingSpeakerId);
                settings.PendingSpeakerId = speakerId;
                if (speakerId.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase)) return;

                var character = settings.Characters.FirstOrDefault(item => item.Id.ToString("D").Equals(speakerId, StringComparison.OrdinalIgnoreCase));
                if (character is null)
                {
                    await HandRoleplayTurnToPlayerAsync(conversation, token);
                    return;
                }

                await GenerateRoleplayCharacterAsync(conversation, character, history, connection, token);
                settings.PendingSpeakerId = RoleplayTurnOrder.GetNextSpeakerId(settings, speakerId);
                conversation.UpdatedAt = DateTimeOffset.UtcNow;
                await conversations.SaveAsync(conversation, token);
                NotifyRoleplayStateChanged();
                if (settings.PendingSpeakerId.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase)) return;
            }

            await HandRoleplayTurnToPlayerAsync(conversation, token);
        }
        catch (OperationCanceledException)
        {
            await HandRoleplayTurnToPlayerAsync(conversation, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            await HandRoleplayTurnToPlayerAsync(conversation, CancellationToken.None);
            throw;
        }
    }

    private async Task HandRoleplayTurnToPlayerAsync(Conversation conversation, CancellationToken token)
    {
        conversation.Roleplay.PendingSpeakerId = RoleplaySpeaker.User;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await conversations.SaveAsync(conversation, token);
        NotifyRoleplayStateChanged();
    }

    private async Task GenerateRoleplayCharacterAsync(Conversation conversation, RoleplayCharacter character, IReadOnlyList<ChatMessage> history, ConnectionProfile connection, CancellationToken token, Guid? responseMessageId = null)
    {
        var streaming = new ChatMessageViewModel
        {
            Id = responseMessageId ?? Guid.NewGuid(),
            Role = MessageRole.Assistant,
            CharacterId = character.Id,
            CharacterName = character.Name,
            Timestamp = DateTimeOffset.UtcNow,
            Status = MessageStatus.Streaming
        };
        Messages.Add(streaming);
        StatusText = L.Format("{0} is writing…", character.Name);
        var failed = false;
        var textBatch = new TextDeltaBatcher(TimeSpan.FromMilliseconds(75));
        async ValueTask FlushTextAsync(bool force = false)
        {
            if (!textBatch.TryDrain(out var text, force)) return;
            streaming.Content += text;
            await Task.Yield();
        }
        var generation = (conversation.UseCustomGenerationSettings ? conversation.Generation : Settings.AppSettings.Generation).Normalize();
        generation.EnableTools = false;
        try
        {
            await foreach (var modelEvent in roleplay.StreamCharacterAsync(conversation, character, history, connection, generation, SelectedReasoning, token))
            {
                switch (modelEvent)
                {
                    case TextDeltaEvent delta: textBatch.Append(delta.Delta); await FlushTextAsync(); break;
                    case ModelErrorEvent error: await FlushTextAsync(force: true); failed = true; streaming.Status = MessageStatus.Failed; ErrorMessage = error.Message; break;
                }
            }
            await FlushTextAsync(force: true);
        }
        catch (Exception)
        {
            Messages.Remove(streaming);
            throw;
        }
        if (string.IsNullOrWhiteSpace(streaming.Content))
        {
            Messages.Remove(streaming);
            if (failed) throw new InvalidOperationException(ErrorMessage);
            throw new InvalidOperationException(L.Format("{0} returned an empty response.", character.Name));
        }

        var message = responseMessageId is { } responseId ? await conversations.GetMessageAsync(responseId, token) : null;
        message ??= new ChatMessage { Id = streaming.Id, ConversationId = conversation.Id, Role = MessageRole.Assistant };
        message.Content = streaming.Content;
        message.Status = failed ? MessageStatus.Failed : MessageStatus.Completed;
        message.ParentMessageId = history.Count == 0 ? null : history[^1].Id;
        message.UpdatedAt = DateTimeOffset.UtcNow;
        message.Metadata[RoleplayMessageMetadata.CharacterId] = character.Id.ToString("D");
        message.Metadata[RoleplayMessageMetadata.CharacterName] = character.Name;
        message.Metadata[RoleplayMessageMetadata.ResponseGroupId] = Guid.NewGuid().ToString("D");
        await conversations.SaveMessageAsync(message, token);
        await LoadMessagesAsync(conversation.Id);
        if (failed) throw new InvalidOperationException(ErrorMessage);
    }

    private void NotifyRoleplayStateChanged()
    {
        OnPropertyChanged(nameof(IsRoleplayConversation));
        OnPropertyChanged(nameof(IsRoleplayPaused));
        OnPropertyChanged(nameof(RoleplayPauseButtonText));
        OnPropertyChanged(nameof(RoleplayTurnText));
        OnPropertyChanged(nameof(CanSendMessage));
        SendCommand.NotifyCanExecuteChanged();
    }

    public string NavigateComposerHistory(int direction, string currentText)
    {
        if (_composerHistory.Count == 0 || direction == 0) return currentText;
        if (direction < 0)
        {
            if (_composerHistoryIndex == _composerHistory.Count) _composerHistoryDraft = currentText;
            _composerHistoryIndex = Math.Max(0, _composerHistoryIndex - 1);
        }
        else
        {
            _composerHistoryIndex = Math.Min(_composerHistory.Count, _composerHistoryIndex + 1);
        }
        var value = _composerHistoryIndex == _composerHistory.Count ? _composerHistoryDraft : _composerHistory[_composerHistoryIndex];
        _navigatingComposerHistory = true;
        ComposerText = value;
        _navigatingComposerHistory = false;
        return value;
    }

    private void ResetComposerHistory(IEnumerable<string> messages)
    {
        _composerHistory.Clear();
        _composerHistory.AddRange(messages.Where(x => !string.IsNullOrWhiteSpace(x)));
        _composerHistoryIndex = _composerHistory.Count;
        _composerHistoryDraft = string.Empty;
    }

    private void AddComposerHistory(string text)
    {
        if (_composerHistory.Count == 0 || _composerHistory[^1] != text) _composerHistory.Add(text);
        _composerHistoryIndex = _composerHistory.Count;
        _composerHistoryDraft = string.Empty;
    }

    private bool CanSend() => CanSendMessage;

    partial void OnComposerTextChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendMessage));
        if (!_navigatingComposerHistory) { _composerHistoryIndex = _composerHistory.Count; _composerHistoryDraft = value; }
    }
    partial void OnIsGeneratingChanged(bool value) { SendCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanSendMessage)); OnPropertyChanged(nameof(CanRequestImageGeneration)); }
    partial void OnAvailableImageModelChanged(string? value)
    {
        if (value is null) IsImageGenerationRequested = false;
        OnPropertyChanged(nameof(IsImageGenerationAvailable));
        OnPropertyChanged(nameof(CanRequestImageGeneration));
    }
    partial void OnIsImageGenerationRequestedChanged(bool value) => OnPropertyChanged(nameof(ImageGenerationTooltip));

    /// <summary>Keeps a large paste out of the editor while still sending it verbatim.</summary>
    public void AddPastedText(string? pastedText)
    {
        if (string.IsNullOrEmpty(pastedText)) return;
        var lines = pastedText.AsSpan().Count('\n') + 1;
        PastedTextBlocks.Add(PastedTextBlock.Create(pastedText, L.Format("Pasted text · {0} characters · {1} lines", pastedText.Length, lines)));
        NotifyPastedTextChanged();
        StatusText = L.Get("Large paste kept outside the input box; it is sent with your message.");
    }

    [RelayCommand]
    private void RemovePastedText(PastedTextBlock? block)
    {
        if (block is null || !PastedTextBlocks.Remove(block)) return;
        NotifyPastedTextChanged();
    }

    private void ClearPastedText()
    {
        if (PastedTextBlocks.Count == 0) return;
        PastedTextBlocks.Clear();
        NotifyPastedTextChanged();
    }

    private void NotifyPastedTextChanged()
    {
        OnPropertyChanged(nameof(HasPastedText));
        OnPropertyChanged(nameof(CanSendMessage));
        SendCommand.NotifyCanExecuteChanged();
    }
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnSidebarWidthChanged(double value) => OnPropertyChanged(nameof(SidebarGridWidth));
    partial void OnEditingMessageIdChanged(Guid? value) { OnPropertyChanged(nameof(IsEditingMessage)); OnPropertyChanged(nameof(EditingMessageDescription)); }
    partial void OnEditingMessageRoleChanged(MessageRole? value)
    {
        if (value == MessageRole.Assistant) IsImageGenerationRequested = false;
        OnPropertyChanged(nameof(EditingMessageDescription));
        OnPropertyChanged(nameof(CanRequestImageGeneration));
    }

    [RelayCommand] private void Stop() { if (_generation is null) return; StatusText = L.Get("Stopping…"); _generation.Cancel(); }
    [RelayCommand] private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private async Task DeleteConversationAsync(Conversation? conversation)
    {
        if (conversation is null || IsGenerating) return;
        var deletingActive = CurrentConversation?.Id == conversation.Id;
        if (deletingActive) CurrentConversation = null;
        await conversations.DeleteAsync(conversation.Id);
        var existing = Conversations.FirstOrDefault(item => item.Id == conversation.Id);
        if (existing is not null) Conversations.Remove(existing);
        if (deletingActive) CurrentConversation = Conversations.FirstOrDefault();
    }

    [RelayCommand]
    private async Task PinConversationAsync(Conversation? conversation)
    {
        if (conversation is null || IsGenerating) return;
        conversation.IsPinned = !conversation.IsPinned;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await conversations.SaveAsync(conversation);
        await ReloadConversationsAsync();
    }

    public async Task RenameConversationAsync(Conversation conversation, string title)
    {
        if (IsGenerating) return;
        var normalized = title.Trim();
        if (normalized.Length == 0) return;
        conversation.Title = normalized.Length > 80 ? normalized[..80] : normalized;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await conversations.SaveAsync(conversation);
        await ReloadConversationsAsync();
        CurrentConversation = Conversations.FirstOrDefault(item => item.Id == conversation.Id) ?? CurrentConversation;
    }

    public async Task UpdateConversationSystemPromptAsync(Conversation conversation, string systemPrompt)
    {
        var generation=conversation.Generation.Normalize();
        generation.SystemPrompt=systemPrompt;
        await UpdateConversationGenerationSettingsAsync(conversation,true,generation);
    }

    public async Task UpdateConversationGenerationSettingsAsync(Conversation conversation, bool useCustomSettings, GenerationSettings generation)
    {
        if (IsGenerating) return;
        conversation.UseCustomGenerationSettings = useCustomSettings;
        conversation.Generation = generation.Normalize();
        if (conversation.Mode == ConversationMode.Roleplay) conversation.Generation.EnableTools = false;
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await conversations.SaveAsync(conversation);
        await ReloadConversationsAsync();
        if (CurrentConversation?.Id == conversation.Id)
            CurrentConversation = Conversations.FirstOrDefault(item => item.Id == conversation.Id) ?? CurrentConversation;
    }

    public async Task ImportAttachmentsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            StatusText = L.Format("Indexing {0}…", Path.GetFileName(path));
            try { PendingAttachments.Add(await attachments.ImportAsync(path)); }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }
        StatusText = L.Get("Ready");
    }

    [RelayCommand]
    private async Task RemovePendingAttachmentAsync(AttachmentInfo? attachment)
    {
        if (attachment is null || !PendingAttachments.Contains(attachment)) return;
        try { await attachments.DeleteAsync(attachment); PendingAttachments.Remove(attachment); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    public async Task CompleteFirstRunAsync()
    {
        await Settings.SaveConnectionCommand.ExecuteAsync(null);
        if (Settings.Connections.Count == 0) return;
        Settings.AppSettings.FirstRunCompleted = true;
        Settings.AppSettings.ActiveConnectionId = Settings.Connections.FirstOrDefault(connection => connection.IsActive)?.Id;
        await settingsRepository.SaveAsync(Settings.AppSettings);
        IsFirstRun = false;
        ActiveModel = Settings.Connections.FirstOrDefault(connection => connection.IsActive)?.Model ?? Settings.Model;
    }

    private async Task<ConnectionProfile?> GetActiveConnectionAsync()
    {
        var available = await connections.ListAsync();
        return available.FirstOrDefault(connection => connection.IsActive) ?? (available.Count == 0 ? null : available[0]);
    }

    private void OnAppearanceApplied(object? sender, AppearanceSettings appearance) => SidebarWidth = appearance.SidebarWidth;
    private async void OnConnectionsChanged(object? sender, EventArgs args)
    {
        try
        {
            var active = await GetActiveConnectionAsync();
            ActiveModel = active?.Model ?? L.Get("No model connected");
            _ = RefreshImageModelAsync(active);
            if (!_suppressRoleplayAutoAdvance && active is not null && CurrentConversation is { Mode: ConversationMode.Roleplay } conversation &&
                !conversation.Roleplay.IsPaused && !conversation.Roleplay.PendingSpeakerId.Equals(RoleplaySpeaker.User, StringComparison.OrdinalIgnoreCase))
                _ = TryAdvanceRoleplayAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    private static ToolActivityViewModel FindTool(ChatMessageViewModel message, string callId, string name)
    {
        return message.GetOrAddToolActivity(callId,name);
    }

    private async Task RefreshImageModelAsync(ConnectionProfile? connection)
    {
        _imageModelLoad?.Cancel();
        using var pending = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _imageModelLoad = pending;
        IsLoadingModels = true;
        ModelPickerStatus = string.Empty;
        _chatModels = connection is { Model.Length: > 0 } ? [connection.Model] : [];
        FilterModels(_modelFilter);
        AvailableImageModel = null;
        IsImageGenerationRequested = false;
        try
        {
            if (connection is null)
            {
                ModelPickerStatus = L.Get("Add a connection in settings to choose a model.");
                return;
            }
            var models = await providers.Create(connection).ListModelsAsync(connection, pending.Token);
            if (!pending.IsCancellationRequested && ReferenceEquals(_imageModelLoad, pending))
            {
                AvailableImageModel = ImageGenerationPolicy.SelectModel(models);
                _chatModels = models.Append(connection.Model).Where(model => !string.IsNullOrWhiteSpace(model) && !ImageGenerationPolicy.IsImageModel(model))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                FilterModels(_modelFilter);
                if (models.Count == 0) ModelPickerStatus = L.Get("The endpoint returned no models; manual model entry remains available.");
            }
        }
        catch (Exception)
        {
            if (ReferenceEquals(_imageModelLoad, pending))
                ModelPickerStatus = L.Get("Could not load the model list. Your current model is still available.");
        }
        finally
        {
            if (ReferenceEquals(_imageModelLoad, pending)) { _imageModelLoad = null; IsLoadingModels = false; }
        }
    }

    private void StartGenerationProgressClock(ChatMessageViewModel message)
    {
        if (_generationProgressTimer is null)
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher is not null)
            {
                _generationProgressTimer = dispatcher.CreateTimer();
                _generationProgressTimer.Interval = TimeSpan.FromSeconds(1);
                _generationProgressTimer.Tick += OnGenerationProgressTimerTick;
            }
        }
        _timedGenerationMessage = message;
        message.RefreshGenerationClock(DateTimeOffset.UtcNow);
        _generationProgressTimer?.Start();
    }

    private void StopGenerationProgressClock(ChatMessageViewModel message)
    {
        if (!ReferenceEquals(_timedGenerationMessage, message)) return;
        message.RefreshGenerationClock(DateTimeOffset.UtcNow);
        _generationProgressTimer?.Stop();
        _timedGenerationMessage = null;
    }

    private void OnGenerationProgressTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) =>
        _timedGenerationMessage?.RefreshGenerationClock(DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (_settingsEventsHooked)
        {
            Settings.AppearanceApplied -= OnAppearanceApplied;
            Settings.ConnectionsChanged -= OnConnectionsChanged;
        }
        _messageLoad?.Cancel(); _messageLoad?.Dispose();
        _imageModelLoad?.Cancel();
        _generation?.Cancel(); _generation?.Dispose();
        if (_generationProgressTimer is not null)
        {
            _generationProgressTimer.Stop();
            _generationProgressTimer.Tick -= OnGenerationProgressTimerTick;
        }
        GC.SuppressFinalize(this);
    }
}
