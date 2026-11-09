using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Neuroset.Application;
using Neuroset.Domain;
using Neuroset.App.Localization;

namespace Neuroset.App.ViewModels;

public partial class ChatMessageViewModel : ObservableObject
{
    public Guid Id { get; init; }
    public MessageRole Role { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Guid? CharacterId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    [ObservableProperty] public partial string Content { get; set; } = string.Empty;
    [ObservableProperty] public partial MessageStatus Status { get; set; }
    public ObservableCollection<AttachmentInfo> Attachments { get; } = [];
    public ObservableCollection<ToolActivityViewModel> Tools { get; } = [];
    public ObservableCollection<ToolActivityGroupViewModel> ToolGroups { get; } = [];
    public ObservableCollection<AgentPlannedActionViewModel> PlannedActions { get; } = [];
    [ObservableProperty] public partial bool IsToolJournalExpanded { get; set; }
    [ObservableProperty] public partial bool HasTools { get; set; }
    [ObservableProperty] public partial bool AreToolsCompleted { get; set; } = true;
    [ObservableProperty] public partial string ToolJournalSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasAgentRunDetails { get; set; }
    [ObservableProperty] public partial bool HasPlannedActions { get; set; }
    [ObservableProperty] public partial string RequestedActionsSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string RequestedActionTypesSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string GenerationTimingSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string ToolJournalCompactStatus { get; set; } = string.Empty;
    public ObservableCollection<HtmlArtifactViewModel> HtmlArtifacts { get; } = [];
    public ObservableCollection<GeneratedImageViewModel> GeneratedImages { get; } = [];
    public ObservableCollection<PresentationFileViewModel> Presentations { get; } = [];
    public ObservableCollection<WidgetViewModel> Widgets { get; } = [];
    public ObservableCollection<RichTableViewModel> Tables { get; } = [];
    public ObservableCollection<CanvasViewModel> Canvases { get; } = [];
    public Guid VersionGroupId { get; init; }
    public int VersionCount { get; init; } = 1;
    public int VersionPosition { get; init; } = 1;
    public bool HasVersions => VersionCount > 1;
    public bool HasPreviousVersion => VersionPosition > 1;
    public bool HasNextVersion => VersionPosition < VersionCount;
    public bool CanRegenerate => Role == MessageRole.Assistant;
    public string ToolJournalHeader { get; } = LocalizationService.Get("Neural network uses tools");
    public string RoleLabel => Role switch { MessageRole.User=>LocalizationService.Get("You"),MessageRole.Assistant when !string.IsNullOrWhiteSpace(CharacterName)=>CharacterName,MessageRole.Assistant=>"Neuroset",MessageRole.Tool=>LocalizationService.Get("Tool"),_=>LocalizationService.Get("System") };
    public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm",System.Globalization.CultureInfo.CurrentCulture);
    public static ChatMessageViewModel FromDomain(ChatMessage message) => FromDomainGroup([message]);

    public static ChatMessageViewModel FromDomainGroup(IReadOnlyList<ChatMessage> messages)
    {
        if(messages.Count==0)throw new ArgumentException("At least one message is required.",nameof(messages));
        var first=messages[0];
        var last=messages[^1];
        var content=CombineContent(messages.Select(message=>message.Content));
        var characterId=first.Metadata.TryGetValue(RoleplayMessageMetadata.CharacterId,out var rawCharacterId)&&Guid.TryParse(rawCharacterId,out var parsedCharacterId)?parsedCharacterId:(Guid?)null;
        var characterName=first.Metadata.TryGetValue(RoleplayMessageMetadata.CharacterName,out var rawCharacterName)?rawCharacterName:string.Empty;
        var vm=new ChatMessageViewModel{Id=first.Id,Role=first.Role,CharacterId=characterId,CharacterName=characterName,Content=content,Timestamp=first.CreatedAt,Status=last.Status,VersionGroupId=first.VersionGroupId,VersionCount=first.VersionCount,VersionPosition=first.VersionPosition};
        foreach(var attachment in messages.SelectMany(message=>message.Attachments).DistinctBy(attachment=>attachment.Id))vm.Attachments.Add(attachment);
        foreach(var call in messages.SelectMany(message=>message.ToolCalls))
        {
            var activity=new ToolActivityViewModel{CallId=call.Id,Name=call.Name,ArgumentsJson=call.ArgumentsJson,Status=call.Execution is null?LocalizationService.Get("Pending"):call.Execution.Succeeded?LocalizationService.Get("Completed"):LocalizationService.Get("Failed")};
            if(call.Execution is { } execution)activity.Complete(execution.Succeeded,execution.Output,execution.Error,execution.CompletedAt is { } completed?completed-execution.StartedAt:null,execution.Metadata);
            else
            {
                activity.Status=LocalizationService.Get("Interrupted");
                activity.Complete(false,string.Empty,LocalizationService.Get("This tool call ended without a result. It can be retried in a new response."));
            }
            vm.AddToolActivity(activity);
            if(call.Name.Equals(ImageGenerationPolicy.ToolName,StringComparison.OrdinalIgnoreCase)&&call.Execution is{Succeeded:true}&&TryCreateGeneratedImage(call.Id,call.Execution.Output,out var generatedImage))vm.GeneratedImages.Add(generatedImage);
            if(call.Name.Equals(PresentationPolicy.ToolName,StringComparison.OrdinalIgnoreCase)&&call.Execution is{Succeeded:true}&&TryCreatePresentation(call.Id,call.Execution.Output,out var presentation))vm.Presentations.Add(presentation);
            if(call.Name.Equals("render_html",StringComparison.OrdinalIgnoreCase)&&call.Execution is{Succeeded:true}&&TryCreateHtmlArtifact(call.Id,call.Execution.Output,out var artifact))vm.HtmlArtifacts.Add(artifact);
            if(call.Name.Equals("math_canvas",StringComparison.OrdinalIgnoreCase)&&call.Execution is{Succeeded:true}&&TryCreateHtmlArtifact(call.Id,call.Execution.Output,out var mathArtifact))vm.HtmlArtifacts.Add(mathArtifact);
            if(call.Name.Equals("widgets",StringComparison.OrdinalIgnoreCase)&&call.Execution is{Succeeded:true}&&TryCreateWidget(call.Id,call.Execution.Output,out var widget))vm.Widgets.Add(widget);
            if(call.Name.Equals("rich_table",StringComparison.OrdinalIgnoreCase)&&call.Execution is{Succeeded:true}&&TryCreateRichTable(call.Id,call.Execution.Output,out var table))vm.Tables.Add(table);
            if(call.Name.Equals("canvas",StringComparison.OrdinalIgnoreCase)&&call.Execution is{Succeeded:true}&&TryCreateCanvas(call.Id,call.Execution.Output,out var canvas))vm.Canvases.Add(canvas);
        }
        var runMetadata=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var message in messages)
            foreach(var item in message.Metadata)
                if(AgentRunMessageMetadata.AllKeys.Contains(item.Key,StringComparer.OrdinalIgnoreCase))runMetadata[item.Key]=item.Value;
        vm.RestoreAgentRunMetadata(runMetadata);
        return vm;
    }

    public ToolActivityViewModel GetOrAddToolActivity(string callId,string name,string? status=null)
    {
        var existing=Tools.FirstOrDefault(item=>item.CallId==callId);
        if(existing is not null)return existing;
        var activity=new ToolActivityViewModel{CallId=callId,Name=name,Status=status??LocalizationService.Get("Starting…")};
        AddToolActivity(activity);
        return activity;
    }

    private void AddToolActivity(ToolActivityViewModel activity)
    {
        Tools.Add(activity);
        var group=ToolGroups.FirstOrDefault(item=>item.Name.Equals(activity.Name,StringComparison.OrdinalIgnoreCase));
        if(group is null)
        {
            group=new ToolActivityGroupViewModel(activity.Name);
            ToolGroups.Add(group);
        }
        group.Add(activity);
        activity.PropertyChanged+=OnToolActivityPropertyChanged;
        RefreshToolJournal();
    }

    private void OnToolActivityPropertyChanged(object? sender,PropertyChangedEventArgs args)
    {
        if(args.PropertyName is nameof(ToolActivityViewModel.IsCompleted) or nameof(ToolActivityViewModel.Status))RefreshToolJournal();
    }

    private void RefreshToolJournal()
    {
        HasTools=Tools.Count>0;
        AreToolsCompleted=Tools.Count==0||Tools.All(item=>item.IsCompleted);
        ToolJournalSummary=LocalizationService.Format("{0} tool actions",Tools.Count);
        RefreshAgentRunSummary(DateTimeOffset.UtcNow);
    }

    private readonly Dictionary<string,int> _requestedActionCounts=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,int> _completedActionCounts=new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _generationStartedAt;
    private DateTimeOffset? _estimatedCompletionAt;
    private TimeSpan? _generationDuration;
    private int _requestedActionCount;
    private int _completedActionCount;
    private bool _isGenerationFinalizing;
    private bool _isGenerationCompleted;

    public void BeginAgentRun(DateTimeOffset startedAt)
    {
        _generationStartedAt=startedAt;
        _generationDuration=null;
        _estimatedCompletionAt=null;
        _isGenerationFinalizing=false;
        _isGenerationCompleted=false;
        HasAgentRunDetails=true;
        RefreshAgentRunSummary(DateTimeOffset.UtcNow);
    }

    public void ApplyAgentProgress(AgentProgressEvent progress)
    {
        _generationStartedAt=progress.StartedAt;
        _requestedActionCount=progress.RequestedActionCount;
        _completedActionCount=progress.CompletedActionCount;
        _requestedActionCounts.Clear();
        foreach(var item in progress.RequestedActionCounts)_requestedActionCounts[item.Key]=item.Value;
        _completedActionCounts.Clear();
        foreach(var item in progress.CompletedActionCounts)_completedActionCounts[item.Key]=item.Value;
        PlannedActions.Clear();
        foreach(var action in progress.PlannedActions)PlannedActions.Add(new(action.Tool,action.Task,action.EstimatedToolCalls));
        HasPlannedActions=PlannedActions.Count>0;
        _isGenerationFinalizing=progress.IsFinalizing;
        _isGenerationCompleted=progress.IsCompleted;
        _generationDuration=progress.IsCompleted?progress.Elapsed:null;
        _estimatedCompletionAt=progress.IsCompleted
            ?null
            :progress.EstimatedCompletionAt??(progress.EstimatedRemaining is{ } remaining?progress.Timestamp+remaining:null);
        HasAgentRunDetails=true;
        RefreshAgentRunSummary(progress.Timestamp);
    }

    public void RefreshGenerationClock(DateTimeOffset now)
    {
        if(_generationStartedAt is null)return;
        RefreshAgentRunSummary(now);
    }

    public void WriteAgentRunMetadata(Dictionary<string,string> metadata,DateTimeOffset now)
    {
        foreach(var key in AgentRunMessageMetadata.AllKeys)metadata.Remove(key);
        if(_generationStartedAt is not{ } startedAt)return;
        var elapsed=_generationDuration??(now<startedAt?TimeSpan.Zero:now-startedAt);
        metadata[AgentRunMessageMetadata.StartedAt]=startedAt.ToString("O",CultureInfo.InvariantCulture);
        metadata[AgentRunMessageMetadata.DurationMilliseconds]=elapsed.TotalMilliseconds.ToString("0.###",CultureInfo.InvariantCulture);
        metadata[AgentRunMessageMetadata.RequestedActionCount]=_requestedActionCount.ToString(CultureInfo.InvariantCulture);
        metadata[AgentRunMessageMetadata.RequestedActionCounts]=JsonSerializer.Serialize(_requestedActionCounts,DomainJson.Options);
        metadata[AgentRunMessageMetadata.PlannedActions]=JsonSerializer.Serialize(PlannedActions.Select(item=>new AgentPlannedAction(item.Tool,item.Task,item.EstimatedToolCalls)),DomainJson.Options);
        metadata[AgentRunMessageMetadata.CompletedActionCount]=_completedActionCount.ToString(CultureInfo.InvariantCulture);
        metadata[AgentRunMessageMetadata.CompletedActionCounts]=JsonSerializer.Serialize(_completedActionCounts,DomainJson.Options);
        metadata[AgentRunMessageMetadata.IsFinalizing]=_isGenerationFinalizing?"true":"false";
        metadata[AgentRunMessageMetadata.IsCompleted]=_isGenerationCompleted?"true":"false";
        if(_isGenerationCompleted)
            metadata[AgentRunMessageMetadata.CompletedAt]=(startedAt+elapsed).ToString("O",CultureInfo.InvariantCulture);
        else if(_estimatedCompletionAt is{ } estimate)
            metadata[AgentRunMessageMetadata.EstimatedRemainingMilliseconds]=Math.Max(0,(estimate-now).TotalMilliseconds).ToString("0.###",CultureInfo.InvariantCulture);
    }

    private void RestoreAgentRunMetadata(Dictionary<string,string> metadata)
    {
        if(!metadata.TryGetValue(AgentRunMessageMetadata.StartedAt,out var rawStartedAt)||
            !DateTimeOffset.TryParse(rawStartedAt,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var startedAt))return;
        _generationStartedAt=startedAt;
        _requestedActionCount=ReadInt(metadata,AgentRunMessageMetadata.RequestedActionCount);
        _completedActionCount=ReadInt(metadata,AgentRunMessageMetadata.CompletedActionCount);
        ReadCountMap(metadata,AgentRunMessageMetadata.RequestedActionCounts,_requestedActionCounts);
        ReadCountMap(metadata,AgentRunMessageMetadata.CompletedActionCounts,_completedActionCounts);
        PlannedActions.Clear();
        if(metadata.TryGetValue(AgentRunMessageMetadata.PlannedActions,out var rawPlan))
        {
            try
            {
                foreach(var action in JsonSerializer.Deserialize<AgentPlannedAction[]>(rawPlan,DomainJson.Options)??[])
                    PlannedActions.Add(new(action.Tool,action.Task,action.EstimatedToolCalls));
            }
            catch(JsonException){ }
        }
        HasPlannedActions=PlannedActions.Count>0;
        _isGenerationFinalizing=ReadBoolean(metadata,AgentRunMessageMetadata.IsFinalizing);
        _isGenerationCompleted=ReadBoolean(metadata,AgentRunMessageMetadata.IsCompleted);
        if(metadata.TryGetValue(AgentRunMessageMetadata.DurationMilliseconds,out var rawDuration)&&
            double.TryParse(rawDuration,NumberStyles.Float,CultureInfo.InvariantCulture,out var durationMilliseconds))
            _generationDuration=TimeSpan.FromMilliseconds(Math.Max(0,durationMilliseconds));
        if(!_isGenerationCompleted&&metadata.TryGetValue(AgentRunMessageMetadata.EstimatedRemainingMilliseconds,out var rawRemaining)&&
            double.TryParse(rawRemaining,NumberStyles.Float,CultureInfo.InvariantCulture,out var remainingMilliseconds))
            _estimatedCompletionAt=DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(Math.Max(0,remainingMilliseconds));
        HasAgentRunDetails=true;
        RefreshAgentRunSummary(DateTimeOffset.UtcNow);
    }

    private void RefreshAgentRunSummary(DateTimeOffset now)
    {
        var fallbackCompleted=Math.Max(_completedActionCount,Tools.Count(tool=>!tool.IsWorkPlan&&tool.IsCompleted));
        RequestedActionsSummary=_requestedActionCount>0
            ?LocalizationService.Format("Requested actions: {0} · completed: {1}",_requestedActionCount,fallbackCompleted)
            :_isGenerationCompleted
                ?LocalizationService.Format("Completed actions: {0}",fallbackCompleted)
                :LocalizationService.Format("Refining the action plan · completed: {0}",fallbackCompleted);

        var actionCounts=_requestedActionCounts.Count>0?_requestedActionCounts:_completedActionCounts;
        RequestedActionTypesSummary=string.Join(" · ",actionCounts
            .Where(item=>item.Value>0)
            .OrderBy(item=>ToolActivityViewModel.GroupDisplayName(item.Key),StringComparer.CurrentCultureIgnoreCase)
            .Select(item=>$"{ToolActivityViewModel.GroupDisplayName(item.Key)} — {item.Value}"));

        if(_generationStartedAt is not{ } startedAt)
        {
            GenerationTimingSummary=string.Empty;
            ToolJournalCompactStatus=ToolJournalSummary;
            return;
        }
        var elapsed=_generationDuration??(now<startedAt?TimeSpan.Zero:now-startedAt);
        if(_isGenerationCompleted)
        {
            var formatted=FormatDuration(elapsed);
            GenerationTimingSummary=LocalizationService.Format("Total generation time: {0}",formatted);
            ToolJournalCompactStatus=formatted;
        }
        else if(_estimatedCompletionAt is{ } estimate)
        {
            if(estimate>now)
            {
                var remaining=estimate-now;
                GenerationTimingSummary=LocalizationService.Format("Elapsed: {0} · approximately {1} remaining",FormatDuration(elapsed),FormatDuration(remaining));
                ToolJournalCompactStatus=LocalizationService.Format("≈ {0} remaining",FormatDuration(remaining));
            }
            else if(_isGenerationFinalizing)
            {
                GenerationTimingSummary=LocalizationService.Format("Elapsed: {0} · finishing…",FormatDuration(elapsed));
                ToolJournalCompactStatus=LocalizationService.Get("Finishing…");
            }
            else
            {
                GenerationTimingSummary=LocalizationService.Format("Elapsed: {0} · refining the estimate…",FormatDuration(elapsed));
                ToolJournalCompactStatus=LocalizationService.Get("Refining estimate…");
            }
        }
        else
        {
            GenerationTimingSummary=LocalizationService.Format("Elapsed: {0} · estimating time remaining…",FormatDuration(elapsed));
            ToolJournalCompactStatus=LocalizationService.Get("Estimating…");
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds=Math.Max(0,(int)Math.Ceiling(duration.TotalSeconds));
        if(totalSeconds<60)return LocalizationService.Format("{0} sec",totalSeconds);
        var totalMinutes=totalSeconds/60;
        var seconds=totalSeconds%60;
        if(totalMinutes<60)return seconds==0?LocalizationService.Format("{0} min",totalMinutes):LocalizationService.Format("{0} min {1} sec",totalMinutes,seconds);
        var hours=totalMinutes/60;
        var minutes=totalMinutes%60;
        return minutes==0?LocalizationService.Format("{0} h",hours):LocalizationService.Format("{0} h {1} min",hours,minutes);
    }

    private static int ReadInt(Dictionary<string,string> metadata,string key)=>
        metadata.TryGetValue(key,out var value)&&int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var parsed)?parsed:0;
    private static bool ReadBoolean(Dictionary<string,string> metadata,string key)=>
        metadata.TryGetValue(key,out var value)&&bool.TryParse(value,out var parsed)&&parsed;
    private static void ReadCountMap(Dictionary<string,string> metadata,string key,Dictionary<string,int> target)
    {
        target.Clear();
        if(!metadata.TryGetValue(key,out var raw))return;
        try
        {
            foreach(var item in JsonSerializer.Deserialize<Dictionary<string,int>>(raw,DomainJson.Options)??[])target[item.Key]=item.Value;
        }
        catch(JsonException){ }
    }

    private static string CombineContent(IEnumerable<string> segments)
    {
        var result=string.Empty;
        foreach(var segment in segments.Where(segment=>!string.IsNullOrEmpty(segment)))
        {
            if(result.Length>0&&!char.IsWhiteSpace(result[^1])&&!char.IsWhiteSpace(segment[0]))result+=Environment.NewLine+Environment.NewLine;
            result+=segment;
        }
        return result;
    }

    public static bool TryCreatePresentation(string callId, string output, out PresentationFileViewModel presentation)
    {
        presentation = null!;
        try
        {
            var artifact = JsonSerializer.Deserialize<PresentationArtifact>(output, DomainJson.Options);
            if (artifact is null || artifact.Kind != "presentation" || string.IsNullOrWhiteSpace(artifact.Path) ||
                !Path.IsPathFullyQualified(artifact.Path) || artifact.Format is not ("pptx" or "odp") ||
                !Path.GetExtension(artifact.Path).Equals("." + artifact.Format, StringComparison.OrdinalIgnoreCase)) return false;
            presentation = new(callId, artifact.Title, Path.GetFullPath(artifact.Path), artifact.Format, artifact.SlideCount, artifact.Revision, artifact.Changes ?? []);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool TryCreateGeneratedImage(string callId, string output, out GeneratedImageViewModel image)
    {
        image = null!;
        try
        {
            var artifact = JsonSerializer.Deserialize<GeneratedImageArtifact>(output, DomainJson.Options);
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.Path) ||
                !Path.IsPathFullyQualified(artifact.Path) || !Path.GetExtension(artifact.Path).Equals(".png", StringComparison.OrdinalIgnoreCase)) return false;
            image = new(callId, artifact.Title, Path.GetFullPath(artifact.Path));
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool TryCreateHtmlArtifact(string callId,string output,out HtmlArtifactViewModel artifact)
    {
        artifact=null!;
        try
        {
            using var document=JsonDocument.Parse(output);
            var root=document.RootElement;
            if(!root.TryGetProperty("path",out var pathElement)||string.IsNullOrWhiteSpace(pathElement.GetString()))return false;
            var path=Path.GetFullPath(pathElement.GetString()!);
            if(!Path.GetExtension(path).Equals(".html",StringComparison.OrdinalIgnoreCase))return false;
            var title=root.TryGetProperty("title",out var titleElement)&&!string.IsNullOrWhiteSpace(titleElement.GetString())?titleElement.GetString()!:LocalizationService.Get("Interactive HTML");
            artifact=new(callId,title,path);
            return true;
        }
        catch(Exception exception)when(exception is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool TryCreateWidget(string callId,string output,out WidgetViewModel widget)
    {
        widget=null!;
        try
        {
            using var document=JsonDocument.Parse(output);
            var root=document.RootElement;
            if(!root.TryGetProperty("kind",out var kindElement)||!root.TryGetProperty("title",out var titleElement)||!root.TryGetProperty("primary",out var primaryElement))return false;
            var kind=kindElement.GetString();var title=titleElement.GetString();var primary=primaryElement.GetString();
            if(string.IsNullOrWhiteSpace(kind)||string.IsNullOrWhiteSpace(title)||string.IsNullOrWhiteSpace(primary))return false;
            var rows=new List<WidgetRowViewModel>();
            if(root.TryGetProperty("rows",out var rowsElement)&&rowsElement.ValueKind==JsonValueKind.Array)
                foreach(var row in rowsElement.EnumerateArray())
                    if(row.TryGetProperty("label",out var label)&&row.TryGetProperty("value",out var value)&&!string.IsNullOrWhiteSpace(label.GetString()))rows.Add(new(LocalizationService.Get(label.GetString()!),LocalizeWidgetText(value.GetString()??string.Empty)));
            var secondary=root.TryGetProperty("secondary",out var secondaryElement)&&secondaryElement.ValueKind==JsonValueKind.String?secondaryElement.GetString()??string.Empty:string.Empty;
            var eyebrow=root.TryGetProperty("eyebrow",out var eyebrowElement)&&eyebrowElement.ValueKind==JsonValueKind.String?eyebrowElement.GetString()??LocalizationService.Get("Widget"):LocalizationService.Get("Widget");
            var source=root.TryGetProperty("source",out var sourceElement)&&sourceElement.ValueKind==JsonValueKind.String?sourceElement.GetString():null;
            var updated=root.TryGetProperty("updatedAt",out var updatedElement)&&DateTimeOffset.TryParse(updatedElement.GetString(),out var parsed)?parsed:(DateTimeOffset?)null;
            widget=new(callId,kind,LocalizationService.Get(eyebrow),LocalizationService.Get(title),LocalizeWidgetText(primary),LocalizeWidgetText(secondary),rows,source,updated);
            return true;
        }
        catch(JsonException){return false;}
    }

    private static string LocalizeWidgetText(string value)
    {
        if(string.IsNullOrWhiteSpace(value))return value;
        var exact=LocalizationService.Get(value);
        if(!ReferenceEquals(exact,value)&&!exact.Equals(value,StringComparison.Ordinal))return exact;
        const string feelsLike=" · feels like ";
        var feelsIndex=value.IndexOf(feelsLike,StringComparison.Ordinal);
        if(feelsIndex>=0)return $"{LocalizationService.Get(value[..feelsIndex])} · {LocalizationService.Get("feels like")} {value[(feelsIndex+feelsLike.Length)..]}";
        if(value.EndsWith(" games",StringComparison.Ordinal)&&int.TryParse(value[..^6],out var games))return LocalizationService.Format("{0} games",games);
        var separator=value.LastIndexOf(" · ",StringComparison.Ordinal);
        if(separator>0)
        {
            var tail=value[(separator+3)..];
            var localizedTail=LocalizationService.Get(tail);
            if(!localizedTail.Equals(tail,StringComparison.Ordinal))return $"{value[..(separator+3)]}{localizedTail}";
        }
        return value;
    }

    public static bool TryCreateRichTable(string callId,string output,out RichTableViewModel table)
    {
        table=null!;
        try
        {
            using var document=JsonDocument.Parse(output);var root=document.RootElement;
            if(!root.TryGetProperty("kind",out var kind)||kind.GetString()!="rich_table"||!root.TryGetProperty("title",out var titleElement)||!root.TryGetProperty("columns",out var columnsElement)||columnsElement.ValueKind!=JsonValueKind.Array||!root.TryGetProperty("rows",out var rowsElement)||rowsElement.ValueKind!=JsonValueKind.Array)return false;
            var title=titleElement.GetString();var columns=columnsElement.EnumerateArray().Select(item=>item.GetString()??string.Empty).ToArray();
            if(string.IsNullOrWhiteSpace(title)||columns.Length==0)return false;
            var rows=new List<RichTableRowViewModel>();
            foreach(var rowElement in rowsElement.EnumerateArray())
            {
                if(rowElement.ValueKind!=JsonValueKind.Array)continue;
                var source=rowElement.EnumerateArray().Select(item=>item.GetString()??string.Empty).ToArray();var cells=new string[columns.Length];
                for(var index=0;index<cells.Length;index++)cells[index]=index<source.Length?source[index]:string.Empty;
                rows.Add(new(cells));
            }
            var caption=root.TryGetProperty("caption",out var captionElement)&&captionElement.ValueKind==JsonValueKind.String?captionElement.GetString()??string.Empty:string.Empty;
            var style=root.TryGetProperty("style",out var styleElement)&&styleElement.ValueKind==JsonValueKind.String?styleElement.GetString()??"default":"default";
            table=new(callId,title,columns,rows,caption,style);return true;
        }
        catch(JsonException){return false;}
    }

    public static bool TryCreateCanvas(string callId,string output,out CanvasViewModel canvas)
    {
        canvas=null!;
        try
        {
            using var document=JsonDocument.Parse(output);var root=document.RootElement;
            if(!root.TryGetProperty("kind",out var kind)||kind.GetString()!="canvas"||!root.TryGetProperty("title",out var titleElement)||!root.TryGetProperty("format",out var formatElement)||!root.TryGetProperty("content",out var contentElement))return false;
            var title=titleElement.GetString();var format=formatElement.GetString();var content=contentElement.GetString();
            if(string.IsNullOrWhiteSpace(title)||format is not("markdown" or "code")||string.IsNullOrWhiteSpace(content))return false;
            var language=root.TryGetProperty("language",out var languageElement)&&languageElement.ValueKind==JsonValueKind.String?languageElement.GetString()??string.Empty:string.Empty;
            var description=root.TryGetProperty("description",out var descriptionElement)&&descriptionElement.ValueKind==JsonValueKind.String?descriptionElement.GetString()??string.Empty:string.Empty;
            canvas=new(callId,title,format,content,language,description);return true;
        }
        catch(JsonException){return false;}
    }
}

public sealed record HtmlArtifactViewModel(string CallId,string Title,string SourcePath);
public sealed record GeneratedImageViewModel(string CallId, string Title, string SourcePath);
public sealed record PresentationFileViewModel(string CallId, string Title, string SourcePath, string Format, int SlideCount, int Revision, string[] Changes)
{
    public string Details => LocalizationService.Format("{0} · {1} slides · version {2}", Format.ToUpperInvariant(), SlideCount, Revision);
    public string ChangeSummary => Changes.Length == 0 ? string.Empty : LocalizationService.Get("Changed") + ": " + string.Join("; ", Changes);
}

public sealed class WidgetViewModel(string callId,string kind,string eyebrow,string title,string primary,string secondary,IReadOnlyList<WidgetRowViewModel> rows,string? source,DateTimeOffset? updatedAt)
{
    public string CallId { get; }=callId;
    public string Kind { get; }=kind;
    public string Eyebrow { get; }=eyebrow;
    public string Title { get; }=title;
    public string Primary { get; }=primary;
    public string Secondary { get; }=secondary;
    public IReadOnlyList<WidgetRowViewModel> Rows { get; }=rows;
    public string Glyph=>Kind switch{"calculator"=>"\uE1D0","units"=>"\uE8EF","currency"=>"\uE8D4","weather"=>"\uE706","time"=>"\uE823","sports"=>"\uE7E8",_=>"\uE90F"};
    public string Footer=>source is null?string.Empty:updatedAt is null?LocalizationService.Get(source):$"{LocalizationService.Get(source)} · {updatedAt.Value.ToLocalTime():g}";
}

public sealed record WidgetRowViewModel(string Label,string Value);

public sealed record RichTableViewModel(string CallId,string Title,IReadOnlyList<string> Columns,IReadOnlyList<RichTableRowViewModel> Rows,string Caption,string Style);
public sealed record RichTableRowViewModel(IReadOnlyList<string> Cells);
public sealed record AgentPlannedActionViewModel(string Tool,string Task,int EstimatedToolCalls)
{
    public string DisplayText=>$"{ToolActivityViewModel.GroupDisplayName(Tool)} — {EstimatedToolCalls}: {Task}";
}
public sealed record CanvasViewModel(string CallId,string Title,string Format,string Content,string Language,string Description)
{
    public bool IsCode=>Format.Equals("code",StringComparison.OrdinalIgnoreCase);
    public bool IsMarkdown=>!IsCode;
    public string FormatLabel=>IsCode?(string.IsNullOrWhiteSpace(Language)?"CODE":Language.ToUpperInvariant()):"MARKDOWN";
}

public partial class ToolActivityViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions PrettyJson = new(DomainJson.Options) { WriteIndented = true };
    public string CallId { get; init; }=string.Empty;
    public string Name { get; init; }=string.Empty;
    [ObservableProperty] public partial string Status { get; set; }=LocalizationService.Get("Starting…");
    [ObservableProperty] public partial string ArgumentsJson { get; set; }=string.Empty;
    [ObservableProperty] public partial string Details { get; set; }=LocalizationService.Get("Tool is running…");
    [ObservableProperty] public partial string SearchSummary { get; set; }=LocalizationService.Get("Search is in progress…");
    [ObservableProperty] public partial bool IsCompleted { get; set; }
    [ObservableProperty] public partial bool Succeeded { get; set; }
    public ObservableCollection<WebSearchResultViewModel> SearchResults { get; }=[];
    public ObservableCollection<WebSearchAttemptViewModel> SearchAttempts { get; }=[];
    public string RawOutput { get; private set; }=string.Empty;
    public string Error { get; private set; }=string.Empty;
    public TimeSpan? Duration { get; private set; }
    public Dictionary<string,string> Metadata { get; }=new(StringComparer.OrdinalIgnoreCase);
    public bool IsWebSearch=>Name.Equals("web_search",StringComparison.OrdinalIgnoreCase);
    public bool IsPython=>Name.Equals("run_python",StringComparison.OrdinalIgnoreCase);
    public bool IsWorkPlan=>Name.Equals("request_work_budget",StringComparison.OrdinalIgnoreCase);
    public bool IsGenericReport=>!IsWebSearch;
    public string Glyph=>Name switch{"web_search"=>"\uE774","run_python"=>"\uE943","fetch_url"=>"\uE8A7","request_work_budget"=>"\uE9D9","delegate_coding_task"=>"\uE716","workspace_patch"=>"\uE70F","git_inspect"=>"\uE8A7","git_worktree"=>"\uE8F1","workspace_checkpoint"=>"\uE7C4","coding_context"=>"\uE8B7","mcp_discover" or "mcp_call"=>"\uE774",_=>"\uE90F"};
    public string DisplayName=>Name.Equals(PresentationPolicy.ToolName,StringComparison.OrdinalIgnoreCase)?LocalizationService.Get("Preparing presentation"):Name.Equals(ImageGenerationPolicy.ToolName,StringComparison.OrdinalIgnoreCase)?LocalizationService.Get("Generating image"):LocalizationService.Get(Name switch{"web_search"=>"Searching the web","fetch_url"=>"Reading page","run_python"=>"Running Python","request_work_budget"=>"Planning the work","delegate_coding_task"=>"Delegating coding task","coding_context"=>"Reading project context","workspace_patch"=>"Applying atomic patch","git_inspect"=>"Inspecting Git","git_worktree"=>"Managing worktree","workspace_checkpoint"=>"Managing checkpoint","mcp_discover"=>"Discovering MCP servers","mcp_call"=>"Calling MCP tool","render_html"=>"Rendering interactive HTML","math_canvas"=>"Rendering mathematics","widgets"=>"Preparing widget","rich_table"=>"Formatting table","canvas"=>"Preparing canvas","run_process"=>"Running command","workspace_files"=>"Working with files","knowledge_search"=>"Searching knowledge","http_request"=>"Calling API","database"=>"Accessing user data","memory_search"=>"Reading memory","memory_manage"=>"Updating memory","schedule_task"=>"Updating scheduler","notify_user"=>"Sending notification",_=>Name.Replace('_',' ')});
    public string ReportTitle=>HeaderText;
    public string HeaderText
    {
        get
        {
            if(IsWebSearch)
            {
                var query=ExtractStringArgument(ArgumentsJson,"query");
                return string.IsNullOrWhiteSpace(query)?DisplayName:LocalizationService.Format("Search for “{0}”",query.Trim());
            }
            if(IsPython)
            {
                var title=ExtractPythonTitle();
                return string.IsNullOrWhiteSpace(title)?DisplayName:title;
            }
            if(IsWorkPlan)
            {
                var title=ExtractStringArgument(ArgumentsJson,"title");
                return string.IsNullOrWhiteSpace(title)?DisplayName:title.Trim();
            }
            if(Name is "delegate_coding_task" or "workspace_patch")
            {
                var title=ExtractStringArgument(ArgumentsJson,"title");
                return string.IsNullOrWhiteSpace(title)?DisplayName:title.Trim();
            }
            if(Name is "coding_context" or "git_inspect" or "git_worktree" or "workspace_checkpoint" or "mcp_discover" or "mcp_call")
            {
                var action=ExtractStringArgument(ArgumentsJson,"action");
                return string.IsNullOrWhiteSpace(action)?DisplayName:$"{DisplayName}: {action.Replace('_',' ')}";
            }
            return DisplayName;
        }
    }

    partial void OnArgumentsJsonChanged(string value)
    {
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ReportTitle));
    }

    public void Complete(bool succeeded,string output,string? error=null,TimeSpan? duration=null,IReadOnlyDictionary<string,string>? metadata=null)
    {
        Succeeded=succeeded;
        RawOutput=output??string.Empty;
        Error=error??string.Empty;
        Duration=duration;
        Metadata.Clear();
        if(metadata is not null)foreach(var item in metadata)Metadata[item.Key]=item.Value;
        if(IsWebSearch)BuildWebSearchReport(succeeded,RawOutput,Error);
        else Details=FormatGenericReport(succeeded,RawOutput,Error);
        IsCompleted=true;
    }

    private void BuildWebSearchReport(bool succeeded,string output,string error)
    {
        SearchResults.Clear();
        SearchAttempts.Clear();
        try
        {
            using var document=JsonDocument.Parse(output);
            var root=document.RootElement;
            JsonElement resultsElement;
            string? message=null;
            var hasProviderDiagnostics=false;
            if(root.ValueKind==JsonValueKind.Array)resultsElement=root;
            else if(root.ValueKind==JsonValueKind.Object&&TryGetProperty(root,"results",out resultsElement))
            {
                if(TryGetProperty(root,"message",out var messageElement)&&messageElement.ValueKind==JsonValueKind.String)message=messageElement.GetString();
                if(TryGetProperty(root,"attempts",out var attemptsElement)&&attemptsElement.ValueKind==JsonValueKind.Array)
                {
                    hasProviderDiagnostics=true;
                    foreach(var attempt in attemptsElement.EnumerateArray())
                    {
                        if(attempt.ValueKind!=JsonValueKind.Object)continue;
                        SearchAttempts.Add(new(
                            GetString(attempt,"provider"),
                            GetString(attempt,"status"),
                            GetInt32(attempt,"resultCount"),
                            GetNullableInt32(attempt,"httpStatusCode"),
                            GetString(attempt,"detail"),
                            GetDouble(attempt,"durationMilliseconds")));
                    }
                }
            }
            else throw new JsonException("Search result payload does not contain a results array.");

            if(resultsElement.ValueKind!=JsonValueKind.Array)throw new JsonException("Search results are not an array.");
            foreach(var result in resultsElement.EnumerateArray())
            {
                if(result.ValueKind!=JsonValueKind.Object)continue;
                var title=GetString(result,"title");
                var url=GetString(result,"url");
                if(string.IsNullOrWhiteSpace(title)||!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not("http" or "https"))continue;
                SearchResults.Add(new(title.Trim(),uri,url.Trim(),GetString(result,"snippet").Trim(),GetString(result,"source").Trim()));
            }
            SearchSummary=!succeeded
                ?LocalizationService.Format("Web search failed: {0}",string.IsNullOrWhiteSpace(error)?LocalizationService.Get("Unknown error"):error)
                :SearchResults.Count==0
                ?LocalizationService.Get(!hasProviderDiagnostics
                    ?"This historical search report has no provider diagnostics. Older versions could mistake a provider bot-check page for an empty result set; run the search again."
                    :string.IsNullOrWhiteSpace(message)?"No search results found. Try a more specific query.":message)
                :LocalizationService.Format("{0} web results",SearchResults.Count);
            Details=SearchSummary;
        }
        catch(JsonException)
        {
            SearchSummary=!succeeded&&!string.IsNullOrWhiteSpace(error)
                ?LocalizationService.Format("Web search failed: {0}",error)
                :string.IsNullOrWhiteSpace(output)?LocalizationService.Get("No search report was returned."):output.Trim();
            Details=SearchSummary;
        }
    }

    private string FormatGenericReport(bool succeeded,string output,string error)
    {
        if(!succeeded)
        {
            if(string.IsNullOrWhiteSpace(output))return string.IsNullOrWhiteSpace(error)?LocalizationService.Get("Tool failed"):error;
            return string.IsNullOrWhiteSpace(error)?output:$"{output}{Environment.NewLine}{Environment.NewLine}{LocalizationService.Get("Error")}: {error}";
        }
        if(Name.Equals("rich_table",StringComparison.OrdinalIgnoreCase))return LocalizationService.Get("Native table created.");
        if(Name.Equals("canvas",StringComparison.OrdinalIgnoreCase))return LocalizationService.Get("Native content canvas created.");
        if(Name.Equals("math_canvas",StringComparison.OrdinalIgnoreCase))return LocalizationService.Get("Mathematical canvas created.");
        if(string.IsNullOrWhiteSpace(output))return LocalizationService.Get("The tool completed without a text report.");
        try
        {
            using var document=JsonDocument.Parse(output);
            return JsonSerializer.Serialize(document.RootElement,PrettyJson);
        }
        catch(JsonException){return output.Trim();}
    }

    public string BuildDetailedReport()
    {
        var report=new StringBuilder();
        AppendFormattedLine(report,$"{LocalizationService.Get("Tool")}: {Name}");
        AppendFormattedLine(report,$"{LocalizationService.Get("Status")}: {Status}");
        AppendFormattedLine(report,$"{LocalizationService.Get("Action")}: {HeaderText}");
        if(Duration is { } duration)AppendFormattedLine(report,$"{LocalizationService.Get("Duration")}: {duration.TotalMilliseconds:0.###} ms");
        report.AppendLine();

        if(IsWebSearch)AppendWebSearchReport(report);
        else if(IsPython)AppendPythonReport(report);
        else if(IsWorkPlan)AppendWorkPlanReport(report);
        else AppendGenericReport(report);

        AppendRawSection(report,LocalizationService.Get("Arguments JSON"),ArgumentsJson);
        AppendRawSection(report,LocalizationService.Get("Raw output"),RawOutput);
        AppendRawSection(report,LocalizationService.Get("Error"),Error);
        if(Metadata.Count>0)
        {
            report.AppendLine(LocalizationService.Get("Metadata"));
            foreach(var item in Metadata.OrderBy(item=>item.Key,StringComparer.OrdinalIgnoreCase))AppendFormattedLine(report,$"{item.Key}: {item.Value}");
            report.AppendLine();
        }
        return report.ToString();
    }

    public static string GroupDisplayName(string name)=>LocalizationService.Get(name switch
    {
        "web_search"=>"Web search",
        "fetch_url"=>"Web pages",
        "run_python"=>"Python",
        "request_work_budget"=>"Work plan",
        "delegate_coding_task"=>"Coding delegates",
        "http_request"=>"HTTP / API",
        "run_process"=>"Commands",
        "workspace_files"=>"Files",
        "workspace_patch"=>"Atomic patches",
        "coding_context"=>"Project context",
        "git_inspect"=>"Git inspection",
        "git_worktree"=>"Git worktrees",
        "workspace_checkpoint"=>"Checkpoints",
        "mcp_discover" or "mcp_call"=>"MCP integrations",
        "knowledge_search"=>"Knowledge",
        "render_html"=>"Interactive HTML",
        "math_canvas"=>"Math canvas",
        "image_generation"=>"Image generation",
        "presentation"=>"Presentations",
        "widgets"=>"Widgets",
        "rich_table"=>"Tables",
        "canvas"=>"Canvas",
        "database"=>"Data",
        "memory_search" or "memory_manage"=>"Memory",
        "schedule_task"=>"Scheduler",
        "notify_user"=>"Notifications",
        _=>name.Replace('_',' ')
    });

    private void AppendWebSearchReport(StringBuilder report)
    {
        var query=ExtractStringArgument(ArgumentsJson,"query")??string.Empty;
        report.AppendLine(LocalizationService.Get("Search query"));
        report.AppendLine(query);
        report.AppendLine();
        report.AppendLine(LocalizationService.Get("Provider attempts"));
        if(SearchAttempts.Count==0)report.AppendLine(LocalizationService.Get("No provider diagnostics were returned."));
        foreach(var attempt in SearchAttempts)
        {
            var http=attempt.HttpStatusCode is { } status?$", HTTP {status}":string.Empty;
            AppendFormattedLine(report,$"- {attempt.Provider}: {attempt.StatusLabel}, {attempt.ResultCount} {LocalizationService.Get("results")}{http}, {attempt.DurationMilliseconds:0.###} ms");
            if(!string.IsNullOrWhiteSpace(attempt.Detail))AppendFormattedLine(report,$"  {LocalizationService.Get(attempt.Detail)}");
        }
        report.AppendLine();
        report.AppendLine(LocalizationService.Format("Search results ({0})",SearchResults.Count));
        if(SearchResults.Count==0)report.AppendLine(SearchSummary);
        for(var index=0;index<SearchResults.Count;index++)
        {
            var result=SearchResults[index];
            AppendFormattedLine(report,$"{index+1}. {result.Title}");
            report.AppendLine(result.Url);
            if(!string.IsNullOrWhiteSpace(result.Source))AppendFormattedLine(report,$"{LocalizationService.Get("Source")}: {result.Source}");
            if(!string.IsNullOrWhiteSpace(result.Snippet))report.AppendLine(result.Snippet);
            report.AppendLine();
        }
    }

    private void AppendPythonReport(StringBuilder report)
    {
        report.AppendLine(LocalizationService.Get("Program name"));
        report.AppendLine(ExtractPythonTitle());
        report.AppendLine();
        AppendRawSection(report,LocalizationService.Get("Python code"),ExtractStringArgument(ArgumentsJson,"code")??string.Empty);
        if(TryParseOutput(out var root))
        {
            AppendFormattedLine(report,$"{LocalizationService.Get("Exit code")}: {GetInt32(root,"exitCode")}");
            AppendFormattedLine(report,$"{LocalizationService.Get("Timed out")}: {GetBoolean(root,"timedOut")}");
            AppendFormattedLine(report,$"{LocalizationService.Get("Execution time")}: {GetDouble(root,"duration_ms"):0.###} ms");
            var failureKind=GetString(root,"failureKind");
            if(failureKind.StartsWith("memory_",StringComparison.OrdinalIgnoreCase))
            {
                AppendFormattedLine(report,$"{LocalizationService.Get("Failure reason")}: {LocalizationService.Get("Python exceeded the bounded sandbox memory limit. The agent may make only one materially optimized retry.")}");
                AppendFormattedLine(report,$"{LocalizationService.Get("Memory limit")}: {GetInt32(root,"memoryLimitMb")} MB");
            }
            report.AppendLine();
            AppendRawSection(report,"STDOUT",GetString(root,"standardOutput"));
            AppendRawSection(report,"STDERR",GetString(root,"standardError"));
        }
    }

    private void AppendWorkPlanReport(StringBuilder report)
    {
        if(!TryParseOutput(out var root))
        {
            AppendGenericReport(report);
            return;
        }
        AppendFormattedLine(report,$"{LocalizationService.Get("Requested model iterations")}: {GetInt32(root,"requestedIterations")}");
        AppendFormattedLine(report,$"{LocalizationService.Get("Requested tool actions")}: {GetInt32(root,"requestedActionCount")}");
        AppendFormattedLine(report,$"{LocalizationService.Get("Approved iteration budget")}: {GetInt32(root,"approvedBudget")}");
        AppendFormattedLine(report,$"{LocalizationService.Get("Hard safety ceiling")}: {GetInt32(root,"hardCeiling")}");
        report.AppendLine();
        report.AppendLine(LocalizationService.Get("Planned actions"));
        if(TryGetProperty(root,"plan",out var plan)&&plan.ValueKind==JsonValueKind.Array)
        {
            var index=0;
            foreach(var action in plan.EnumerateArray())
            {
                index++;
                var tool=GetString(action,"tool");
                var task=GetString(action,"task");
                var count=GetInt32(action,"estimated_tool_calls");
                AppendFormattedLine(report,$"{index}. {ToolActivityViewModel.GroupDisplayName(tool)} — {count}");
                if(!string.IsNullOrWhiteSpace(task))AppendFormattedLine(report,$"   {task}");
            }
        }
        report.AppendLine();
    }

    private void AppendGenericReport(StringBuilder report)
    {
        if(!string.IsNullOrWhiteSpace(Details))
        {
            report.AppendLine(LocalizationService.Get("Report"));
            report.AppendLine(Details);
            report.AppendLine();
        }
    }

    private bool TryParseOutput(out JsonElement root)
    {
        root=default;
        if(string.IsNullOrWhiteSpace(RawOutput))return false;
        try
        {
            using var document=JsonDocument.Parse(RawOutput);
            root=document.RootElement.Clone();
            return root.ValueKind==JsonValueKind.Object;
        }
        catch(JsonException){return false;}
    }

    private string ExtractPythonTitle()
    {
        var explicitTitle=ExtractStringArgument(ArgumentsJson,"title");
        if(!string.IsNullOrWhiteSpace(explicitTitle))return explicitTitle.Trim();
        var code=ExtractStringArgument(ArgumentsJson,"code");
        if(!string.IsNullOrWhiteSpace(code))
        {
            var firstComment=code.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
                .FirstOrDefault(line=>line.StartsWith('#'));
            if(!string.IsNullOrWhiteSpace(firstComment))
            {
                var title=firstComment.TrimStart('#',' ');
                if(title.Length>0)return title.Length<=120?title:title[..120];
            }
        }
        return LocalizationService.Get("Python program");
    }

    private static void AppendRawSection(StringBuilder report,string title,string value)
    {
        report.AppendLine(title);
        if(string.IsNullOrEmpty(value))report.AppendLine(LocalizationService.Get("(empty)"));
        else
        {
            report.Append(value);
            if(value[^1] is not('\r' or '\n'))report.AppendLine();
        }
        report.AppendLine();
    }

    private static void AppendFormattedLine(StringBuilder report,FormattableString value)=>report.AppendLine(value.ToString(CultureInfo.CurrentCulture));

    private static string? ExtractStringArgument(string argumentsJson,string argumentName)
    {
        if(string.IsNullOrWhiteSpace(argumentsJson))return null;
        try
        {
            using var document=JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind==JsonValueKind.Object?GetString(document.RootElement,argumentName):null;
        }
        catch(JsonException)
        {
            var keyIndex=argumentsJson.IndexOf($"\"{argumentName}\"",StringComparison.OrdinalIgnoreCase);
            if(keyIndex<0)return null;
            var colonIndex=argumentsJson.IndexOf(':',keyIndex+argumentName.Length+2);
            if(colonIndex<0)return null;
            var quoteIndex=argumentsJson.IndexOf('"',colonIndex+1);
            if(quoteIndex<0)return null;
            for(var index=quoteIndex+1;index<argumentsJson.Length;index++)
            {
                if(argumentsJson[index]!='"'||IsEscaped(argumentsJson,index))continue;
                try{return JsonSerializer.Deserialize<string>(argumentsJson[quoteIndex..(index+1)]);}
                catch(JsonException){return null;}
            }
            return null;
        }
    }

    private static bool IsEscaped(string value,int index)
    {
        var slashCount=0;
        for(var cursor=index-1;cursor>=0&&value[cursor]=='\\';cursor--)slashCount++;
        return slashCount%2==1;
    }

    private static bool TryGetProperty(JsonElement element,string name,out JsonElement value)
    {
        if(element.TryGetProperty(name,out value))return true;
        foreach(var property in element.EnumerateObject())
            if(property.Name.Equals(name,StringComparison.OrdinalIgnoreCase)){value=property.Value;return true;}
        value=default;
        return false;
    }

    private static string GetString(JsonElement element,string name)=>TryGetProperty(element,name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??string.Empty:string.Empty;
    private static int GetInt32(JsonElement element,string name)=>TryGetProperty(element,name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var number)?number:0;
    private static int? GetNullableInt32(JsonElement element,string name)=>TryGetProperty(element,name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var number)?number:null;
    private static double GetDouble(JsonElement element,string name)=>TryGetProperty(element,name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var number)?number:0;
    private static bool GetBoolean(JsonElement element,string name)=>TryGetProperty(element,name,out var value)&&value.ValueKind is JsonValueKind.True or JsonValueKind.False&&value.GetBoolean();
}

public partial class ToolActivityGroupViewModel(string name) : ObservableObject
{
    public string Name { get; }=name;
    public ObservableCollection<ToolActivityViewModel> Activities { get; }=[];
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsCompleted { get; set; }=true;
    [ObservableProperty] public partial string CountLabel { get; set; }=string.Empty;
    public string DisplayName=>ToolActivityViewModel.GroupDisplayName(Name);
    public string Glyph=>Name switch{"web_search"=>"\uE774","run_python"=>"\uE943","fetch_url"=>"\uE8A7","request_work_budget"=>"\uE9D9","delegate_coding_task"=>"\uE716","workspace_patch"=>"\uE70F","git_inspect"=>"\uE8A7","git_worktree"=>"\uE8F1","workspace_checkpoint"=>"\uE7C4","coding_context"=>"\uE8B7","mcp_discover" or "mcp_call"=>"\uE774",_=>"\uE90F"};

    public void Add(ToolActivityViewModel activity)
    {
        Activities.Add(activity);
        activity.PropertyChanged+=OnActivityPropertyChanged;
        Refresh();
    }

    private void OnActivityPropertyChanged(object? sender,PropertyChangedEventArgs args)
    {
        if(args.PropertyName is nameof(ToolActivityViewModel.IsCompleted) or nameof(ToolActivityViewModel.Status))Refresh();
    }

    private void Refresh()
    {
        IsCompleted=Activities.Count==0||Activities.All(activity=>activity.IsCompleted);
        CountLabel=LocalizationService.Format("{0} actions",Activities.Count);
    }
}

public sealed record WebSearchAttemptViewModel(string Provider,string Status,int ResultCount,int? HttpStatusCode,string Detail,double DurationMilliseconds)
{
    public string StatusLabel=>LocalizationService.Get(Status switch{"success"=>"Succeeded","no_results"=>"No results","blocked"=>"Blocked","failed"=>"Failed","timeout"=>"Timed out",_=>Status});
}

public sealed record WebSearchResultViewModel(string Title,Uri NavigateUri,string Url,string Snippet,string Source)
{
    public string SourceLabel=>string.IsNullOrWhiteSpace(Source)?NavigateUri.Host:$"{NavigateUri.Host} · {Source}";
}
