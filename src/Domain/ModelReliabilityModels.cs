namespace Neuroset.Domain;

public enum ReliabilityProbeStatus
{
    Passed,
    Failed,
    Unsupported,
    Error
}

public sealed record ModelReliabilityOptions(
    bool InstructionHierarchy = true,
    bool ObfuscationRobustness = true,
    bool RefusalCalibration = true,
    bool AssistantPrefill = true);

public sealed record ModelReliabilityProbeResult(
    string Id,
    string Category,
    string Name,
    ReliabilityProbeStatus Status,
    string Summary,
    string ResponsePreview,
    double RefusalScore,
    TimeSpan Latency);

public sealed record ModelReliabilityReport(
    Guid Id,
    string ConnectionName,
    string Model,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<ModelReliabilityProbeResult> Probes,
    string Markdown)
{
    public int PassedCount => Probes.Count(probe => probe.Status == ReliabilityProbeStatus.Passed);
    public int FailedCount => Probes.Count(probe => probe.Status == ReliabilityProbeStatus.Failed);
    public int UnsupportedCount => Probes.Count(probe => probe.Status == ReliabilityProbeStatus.Unsupported);
    public int ErrorCount => Probes.Count(probe => probe.Status == ReliabilityProbeStatus.Error);
}
