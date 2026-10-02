namespace Neuroset.Domain;

public sealed class ScheduledTaskDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public ScheduleKind Kind { get; set; }
    public DateTimeOffset? RunAt { get; set; }
    public int? IntervalMinutes { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public TimeOnly? TimeOfDay { get; set; }
    public string? CronExpression { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ToolPermissionRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ToolName { get; init; }
    public ToolRisk Risk { get; init; }
    public PermissionPolicy Policy { get; set; } = PermissionPolicy.Ask;
    public Guid? ConversationId { get; init; }
    public string? ConstraintJson { get; init; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
