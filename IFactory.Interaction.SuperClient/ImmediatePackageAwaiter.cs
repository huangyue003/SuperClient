namespace IFactory.Interaction.SuperClient;

public class ImmediatePackageAwaiter<TPackage> : IPackageAwaiter<TPackage> where TPackage : class
{
    private readonly TaskCompletionSource<TPackage?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Action? Completed { get; set; }

    public async ValueTask<TPackage?> ReadAsync(int millisecondsTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite);
        try
        {
            return await _completion.Task.WaitAsync(millisecondsTimeout == Timeout.Infinite
                ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(millisecondsTimeout)).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        finally { Completed?.Invoke(); }
    }

    public void Write(TPackage? package) => _completion.TrySetResult(package);
}

