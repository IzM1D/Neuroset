namespace Neuroset.Domain;

public sealed class KnowledgeSource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int ChunkCount { get; set; }
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? IndexedAt { get; set; }
}

public sealed class KnowledgeChunk
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceId { get; init; }
    public int Ordinal { get; init; }
    public string Content { get; init; } = string.Empty;
    public float[] Embedding { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record KnowledgeSearchHit(
    Guid ChunkId,
    Guid SourceId,
    string SourceName,
    string Content,
    double Score,
    IReadOnlyDictionary<string, string> Metadata);

public sealed class MemoryRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MemoryScope Scope { get; init; }
    public Guid? ConversationId { get; init; }
    public string Content { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public double Importance { get; set; } = 0.5;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;
}
