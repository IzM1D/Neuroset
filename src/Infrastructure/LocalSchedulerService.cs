using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Infrastructure;

public sealed partial class LocalSchedulerService(
    IScheduledTaskRepository repository,
    IConversationRepository conversations,
    IConnectionRepository connections,
    IServiceProvider services,
    INotificationService notifications,
    ILogger<LocalSchedulerService> logger) : ISchedulerService, IDisposable
{
    private CancellationTokenSource? _source;
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) return Task.CompletedTask;
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Do not run repository or agent work inline on the WinUI dispatcher.
        // Resolving IAgentRuntime lazily also breaks the scheduler/tool registration cycle:
        // scheduler -> agent -> tool registry -> scheduler tool -> scheduler.
        _loop = Task.Run(() => RunLoopAsync(_source.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_source is null || _loop is null) return;
        await _source.CancelAsync().ConfigureAwait(false);
        try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _loop = null; _source.Dispose(); _source = null;
    }

    public DateTimeOffset? CalculateNextRun(ScheduledTaskDefinition task, DateTimeOffset from)
    {
        return task.Kind switch
        {
            ScheduleKind.Once => task.RunAt is { } once && once > from ? once : null,
            ScheduleKind.Interval => from.AddMinutes(Math.Max(1, task.IntervalMinutes ?? 60)),
            ScheduleKind.Daily => NextLocalTime(from, task.TimeOfDay ?? new TimeOnly(9, 0), null),
            ScheduleKind.Weekly => NextLocalTime(from, task.TimeOfDay ?? new TimeOnly(9, 0), task.DayOfWeek ?? DayOfWeek.Monday),
            ScheduleKind.Cron when !string.IsNullOrWhiteSpace(task.CronExpression) => CronExpression.Parse(task.CronExpression, CronFormat.Standard).GetNextOccurrence(from, TimeZoneInfo.Local),
            _ => null
        };
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try { await RunDueTasksAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogSchedulerError(logger, ex.Message); }
        } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task RunDueTasksAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var tasks = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
        IAgentRuntime? agent = null;
        foreach (var task in tasks.Where(x => x.IsEnabled && x.NextRunAt is not null && x.NextRunAt <= now))
        {
            var connection = (await connections.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(x => x.IsActive);
            if (connection is null) { LogNoConnection(logger, task.Id); continue; }
            var conversation = new Conversation { Title = $"Scheduled · {task.Name}" };
            await conversations.SaveAsync(conversation, cancellationToken).ConfigureAwait(false);
            var userMessage = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.User, Content = task.Prompt };
            await conversations.SaveMessageAsync(userMessage, cancellationToken).ConfigureAwait(false);
            var messages = new List<ChatMessage> { userMessage };
            var finalText = new System.Text.StringBuilder();
            agent ??= services.GetRequiredService<IAgentRuntime>();
            await foreach (var modelEvent in agent.RunAsync(new AgentRunRequest(conversation, messages, connection, new AgentRunOptions()), cancellationToken).ConfigureAwait(false))
                if (modelEvent is TextDeltaEvent delta) finalText.Append(delta.Delta);
            task.LastRunAt = now; task.NextRunAt = CalculateNextRun(task, now.AddSeconds(1)); if (task.Kind == ScheduleKind.Once) task.IsEnabled = false;
            await repository.SaveAsync(task, cancellationToken).ConfigureAwait(false);
            if (task.NotifyOnCompletion) await notifications.ShowAsync(task.Name, finalText.Length == 0 ? "Scheduled task completed." : Truncate(finalText.ToString(), 300), cancellationToken).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset NextLocalTime(DateTimeOffset from, TimeOnly time, DayOfWeek? day)
    {
        var local = TimeZoneInfo.ConvertTime(from, TimeZoneInfo.Local); var date = local.Date;
        if (day is { } target) { var delta = ((int)target - (int)date.DayOfWeek + 7) % 7; date = date.AddDays(delta); }
        var candidate = new DateTimeOffset(date + time.ToTimeSpan(), TimeZoneInfo.Local.GetUtcOffset(date + time.ToTimeSpan()));
        if (candidate <= from) candidate = candidate.AddDays(day is null ? 1 : 7);
        return candidate;
    }
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";
    public void Dispose() { _source?.Cancel(); _source?.Dispose(); }
    [LoggerMessage(LogLevel.Error, "Scheduler loop error={Error}")] private static partial void LogSchedulerError(ILogger logger, string error);
    [LoggerMessage(LogLevel.Warning, "Scheduled task {TaskId} is due but no active LLM connection exists")] private static partial void LogNoConnection(ILogger logger, Guid taskId);
}
