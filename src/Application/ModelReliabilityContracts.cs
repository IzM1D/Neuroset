using Neuroset.Domain;

namespace Neuroset.Application;

public interface IModelReliabilityEvaluator
{
    Task<ModelReliabilityReport> RunAsync(
        ConnectionProfile connection,
        ModelReliabilityOptions options,
        CancellationToken cancellationToken = default);
}
