namespace Neuroset.Tools;

internal static class WebOperation
{
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        // Cancellation alone is insufficient for a provider/handler that ignores its token.
        // WaitAsync also bounds our wait; the operation retains ownership of its resources.
        try
        {
            return await operation(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The web operation timed out after {timeout.TotalSeconds:0.###} seconds.", exception);
        }
    }
}
