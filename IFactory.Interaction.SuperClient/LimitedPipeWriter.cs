using System.IO.Pipelines;

namespace IFactory.Interaction.SuperClient;

// Bounds both allocation requests and advances, before user code can commit an
// oversized frame. The underlying Pipe owns pooled segments until send finishes.
internal sealed class LimitedPipeWriter(PipeWriter inner, int limit) : PipeWriter
{
    private long _written;
    public override bool CanGetUnflushedBytes => true;
    public override long UnflushedBytes => inner.UnflushedBytes;
    public override void Advance(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > limit - _written) throw new ArgumentException($"Send exceeds {limit} bytes.");
        inner.Advance(bytes); _written += bytes;
    }
    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var remaining = (int)(limit - _written);
        if (remaining == 0 || sizeHint > remaining) throw new ArgumentException($"Send exceeds {limit} bytes.");
        var memory = inner.GetMemory(Math.Max(1, sizeHint));
        return memory[..Math.Min(memory.Length, remaining)];
    }
    public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
    public override void CancelPendingFlush() => inner.CancelPendingFlush();
    public override void Complete(Exception? exception = null) => inner.Complete(exception);
    public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
}
