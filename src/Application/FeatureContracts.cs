using Neuroset.Domain;

namespace Neuroset.Application;

public sealed record SearchResult(string Title, string Url, string Snippet, string Source);
public sealed record SearchAttempt(string Provider, string Status, int ResultCount, int? HttpStatusCode = null, string? Detail = null, double DurationMilliseconds = 0);
public sealed record SearchResponse(IReadOnlyList<SearchResult> Results, IReadOnlyList<SearchAttempt> Attempts);
public sealed record FetchedPage(string Url, string Title, string Text, string ContentType, DateTimeOffset FetchedAt);
public sealed record PythonExecutionResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration, bool TimedOut);
public sealed record UserDataItem(string Collection, string Key, string ValueJson, DateTimeOffset UpdatedAt);

public interface ISearchProvider
{
    Task<SearchResponse> SearchAsync(string query, int limit = 5, CancellationToken cancellationToken = default);
}

public interface IWebContentFetcher
{
    Task<FetchedPage> FetchAsync(Uri uri, CancellationToken cancellationToken = default);
}

public interface ITextExtractor
{
    bool CanExtract(string extension);
    Task<string> ExtractAsync(string path, CancellationToken cancellationToken = default);
}

public interface IEmbeddingService
{
    int Dimensions { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public interface IKnowledgeService
{
    Task<KnowledgeSource> IndexAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query, int limit = 6, CancellationToken cancellationToken = default);
}

public interface IPythonSandbox
{
    Task<PythonExecutionResult> ExecuteAsync(string code, TimeSpan timeout, string? workingDirectory = null, bool fullAccess = false, CancellationToken cancellationToken = default);
}

public interface INotificationService
{
    Task ShowAsync(string title, string message, CancellationToken cancellationToken = default);
}

public interface IAttachmentService
{
    Task<AttachmentInfo> ImportAsync(string path, CancellationToken cancellationToken = default);
    Task DeleteAsync(AttachmentInfo attachment, CancellationToken cancellationToken = default);
}

public interface ISchedulerService
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    DateTimeOffset? CalculateNextRun(ScheduledTaskDefinition task, DateTimeOffset from);
}

public interface IUserDataStore
{
    Task<IReadOnlyList<UserDataItem>> ListAsync(string collection, int limit = 100, CancellationToken cancellationToken = default);
    Task<UserDataItem?> GetAsync(string collection, string key, CancellationToken cancellationToken = default);
    Task UpsertAsync(UserDataItem item, CancellationToken cancellationToken = default);
    Task DeleteAsync(string collection, string key, CancellationToken cancellationToken = default);
}
