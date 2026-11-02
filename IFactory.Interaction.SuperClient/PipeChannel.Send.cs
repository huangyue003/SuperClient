using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient;

public abstract partial class PipeChannel<TPackageInfo>
{
    private sealed record Failure(Exception Error, CloseReason Reason);
    private Failure? _failure;
    protected virtual int MaximumSendLength => Options.MaxPackageLength;
    protected virtual bool SendEmptyBuffer => false;

    /// <summary>Waits until all bytes are accepted by the transport; does not acknowledge peer receipt.</summary>
    /// <remarks>SendTimeout covers lock queueing and transport. No automatic retry is performed.</remarks>
    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer)
        => SendSequenceAsync(new ReadOnlySequence<byte>(buffer), Stopwatch.GetTimestamp());

    public override ValueTask SendAsync<TPackage>(IPackageEncoder<TPackage> encoder, TPackage package)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        return SendBufferedAsync(writer => encoder.Encode(writer, package), Stopwatch.GetTimestamp());
    }

    public override ValueTask SendAsync(Action<PipeWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return SendBufferedAsync(write, Stopwatch.GetTimestamp());
    }

    private async ValueTask SendBufferedAsync(Action<PipeWriter> write, long started)
    {
        CheckChannelOpen();
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0,
            minimumSegmentSize: Math.Min(4096, MaximumSendLength)));
        try
        {
            // User code never owns the transport lock or the shared Out.Writer.
            // A stalled synchronous callback cannot prevent another thread closing.
            write(new LimitedPipeWriter(pipe.Writer, MaximumSendLength));
            await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            var result = await pipe.Reader.ReadAsync().ConfigureAwait(false);
            try { await SendSequenceAsync(result.Buffer, started).ConfigureAwait(false); }
            finally { pipe.Reader.AdvanceTo(result.Buffer.End); }
        }
        finally
        {
            await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask SendSequenceAsync(ReadOnlySequence<byte> buffer, long started)
    {
        CheckChannelOpen();
        if (buffer.Length > MaximumSendLength) throw new ArgumentException($"Send exceeds {MaximumSendLength} bytes.");
        var length = buffer.Length;
        var remainingBudget = Options.SendTimeout == 0 ? 0
            : Options.SendTimeout - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (Options.SendTimeout > 0 && remainingBudget <= 0)
            throw SendTimedOut(length, 0, "prepare", close: false);
        using var timeout = CreateTimeout(_cts.Token, remainingBudget == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(remainingBudget)));
        var token = timeout?.Token ?? _cts.Token;
        try { await SendLock.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!_cts.IsCancellationRequested && timeout?.IsCancellationRequested == true)
                throw SendTimedOut(length, 0, "send-queue", close: false);
            throw new InvalidOperationException("Channel closed before sending began.", _failure?.Error);
        }
        long accepted = 0;
        try
        {
            CheckChannelOpen();
            try
            {
                if (buffer.IsEmpty && SendEmptyBuffer)
                    await SendOverIOAsync(buffer, token).ConfigureAwait(false);
                while (!buffer.IsEmpty)
                {
                    token.ThrowIfCancellationRequested();
                    var count = await SendOverIOAsync(buffer, token).ConfigureAwait(false);
                    if (count <= 0 || count > buffer.Length) throw new IOException("Transport returned an invalid send count.");
                    accepted += count;
                    buffer = buffer.Slice(count);
                }
                LastActiveTime = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested && timeout?.IsCancellationRequested == true && ex is OperationCanceledException)
                    throw SendTimedOut(length, accepted, "send", close: true);
                var failure = Volatile.Read(ref _failure);
                var error = new ChannelTransportException(failure?.Reason ?? SuperClient.CloseReason.SocketError,
                    accepted, length - accepted, failure?.Error ?? ex);
                RecordFailure(error, error.Reason, "send", accepted, length - accepted);
                throw error;
            }
        }
        finally { SendLock.Release(); }
    }

    private TimeoutException SendTimedOut(long length, long accepted, string stage, bool close)
    {
        var error = new TimeoutException($"Channel '{Identity}' {stage} timed out; accepted={accepted}, pending={length - accepted}.");
        error.Data[nameof(ChannelTransportException.BytesAccepted)] = accepted;
        error.Data[nameof(ChannelTransportException.BytesPending)] = length - accepted;
        if (close) RecordFailure(error, SuperClient.CloseReason.TimeOut, stage, accepted, length - accepted);
        else Logger?.LogWarning(error, "Channel {Identity} {Stage} timed out: accepted={Accepted}, pending={Pending}.",
            Identity, stage, accepted, length - accepted);
        return error;
    }

    private void RecordFailure(Exception error, CloseReason reason, string stage, long accepted = 0, long pending = 0)
    {
        if (Interlocked.CompareExchange(ref _failure, new Failure(error, reason), null) is not null) return;
        CloseReason = reason;
        // Expected shutdown is filtered by state, never by exception type alone.
        Logger?.LogError(error, "Channel {Identity} {Stage} failed: reason={Reason}, accepted={Accepted}, pending={Pending}.",
            Identity, stage, reason, accepted, pending);
        _cts.Cancel();
        try { Close(); }
        catch (Exception cleanup) { OnError("Transport cleanup failed.", cleanup); }
    }
}
