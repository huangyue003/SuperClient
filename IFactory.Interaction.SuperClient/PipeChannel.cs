using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient;

public abstract partial class PipeChannel<TPackageInfo> : ChannelBase<TPackageInfo>, IPipeChannel
{
    private IPipelineFilter<TPackageInfo> _pipelineFilter;
    private readonly CancellationTokenSource _cts = new();
    private readonly IObjectPipe<TPackageInfo> _packagePipe;
    private Task _readsTask = Task.CompletedTask;
    private Task _sendsTask = Task.CompletedTask;
    private Task _closingTask = Task.CompletedTask;
    private int _started;
    private int _enumerating;
    private bool _streamEnded;

    private long _partialLength;
    protected SemaphoreSlim SendLock { get; } = new(1, 1);
    protected Pipe Out { get; }
    protected Pipe In { get; }
    protected ILogger? Logger { get; }
    protected ChannelOptions Options { get; }
    Pipe IPipeChannel.Out => Out;
    Pipe IPipeChannel.In => In;
    IPipelineFilter IPipeChannel.PipelineFilter => _pipelineFilter;

    protected PipeChannel(IPipelineFilter<TPackageInfo> pipelineFilter, ChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(pipelineFilter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPackageLength);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ReceiveTimeout);
        ArgumentOutOfRangeException.ThrowIfNegative(options.SendTimeout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPendingPackages);
        _pipelineFilter = pipelineFilter;
        Options = options;
        Logger = options.Logger;
        _packagePipe = options.ReadAsDemand
            ? new DefaultObjectPipeWithSupplyControl<TPackageInfo>(options.MaxPendingPackages)
            : new DefaultObjectPipe<TPackageInfo>(options.MaxPendingPackages);
        Out = options.Out ?? new Pipe(new PipeOptions(minimumSegmentSize:
            options.SendBufferSize > 0 ? options.SendBufferSize : 4096));
        // The parser must be able to buffer a whole maximum-sized frame.
        In = options.In ?? new Pipe(new PipeOptions(
            pauseWriterThreshold: Math.Max(65536L, (long)options.MaxPackageLength * 2),
            resumeWriterThreshold: Math.Max(32768L, options.MaxPackageLength)));
    }

    public override void Start()
    {
        CheckChannelOpen();
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Channel has already started.");
        _readsTask = ProcessReads();
        _sendsTask = ProcessSends();
        _closingTask = HandleClosingAsync();
    }

    private async Task HandleClosingAsync()
    {
        try { await Task.WhenAll(_readsTask, _sendsTask).ConfigureAwait(false); }
        catch (Exception ex) { if (!IsIgnorableException(ex)) OnError("Channel processing failed.", ex); }
        finally
        {
            if (true)
            {
                try { Close(); }
                catch (Exception ex) { if (!IsIgnorableException(ex)) OnError("Transport cleanup failed.", ex); }
                finally { OnClosed(); }
            }
        }
    }

    public override async IAsyncEnumerable<TPackageInfo> RunAsync()
    {
        if (_started == 0) throw new InvalidOperationException("Channel has not started.");
        if (Interlocked.Exchange(ref _enumerating, 1) != 0) throw new InvalidOperationException("Only one package consumer is supported.");
        try
        {
            if (_streamEnded) yield break;
            while (true)
            {
                var result = await _packagePipe.ReadAsync().ConfigureAwait(false);
                if (!result.HasValue) { _streamEnded = true; await _closingTask.ConfigureAwait(false); yield break; }
                yield return result.Value;
            }
        }
        finally { Volatile.Write(ref _enumerating, 0); }
    }

    protected virtual async Task ProcessSends()
    {
        try
        {
            while (!await ProcessOutputRead(Out.Reader, _cts).ConfigureAwait(false)) { }
        }
        catch (Exception ex) { FailTransport(ex, "Send loop failed."); }
        finally { await Out.Reader.CompleteAsync().ConfigureAwait(false); }
    }

    protected async ValueTask<bool> ProcessOutputRead(PipeReader reader, CancellationTokenSource cts)
    {
        var result = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
        try
        {
            if (!result.IsCanceled && !result.Buffer.IsEmpty)
                await SendSequenceAsync(result.Buffer, System.Diagnostics.Stopwatch.GetTimestamp()).ConfigureAwait(false);
            return result.IsCanceled || result.IsCompleted;
        }
        finally { reader.AdvanceTo(result.Buffer.End); }
    }
    protected virtual async Task ProcessReads()
        => await Task.WhenAll(FillPipeAsync(In.Writer), ReadPipeAsync(In.Reader)).ConfigureAwait(false);

    protected virtual async Task FillPipeAsync(PipeWriter writer)
    {
        var supply = _packagePipe as ISupplyController;
        using var registration = _cts.Token.Register(() => supply?.SupplyEnd());
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (supply is not null) await supply.SupplyRequired().ConfigureAwait(false);
                _cts.Token.ThrowIfCancellationRequested();
                var memory = writer.GetMemory(Options.ReceiveBufferSize > 0 ? Options.ReceiveBufferSize : 4096);
                using var timeout = CreateTimeout(_cts.Token, Options.ReceiveTimeout);
                int count;
                try { count = await FillPipeWithDataAsync(memory, timeout?.Token ?? _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (!_cts.IsCancellationRequested && timeout?.IsCancellationRequested == true)
                { throw new TimeoutException("Receive timed out.", ex); }
                if (count == 0)
                {
                    CloseReason ??= SuperClient.CloseReason.RemoteClosing;
                    break;
                }
                writer.Advance(count);
                LastActiveTime = DateTimeOffset.Now;
                var result = await writer.FlushAsync(_cts.Token).ConfigureAwait(false);
                if (result.IsCanceled || result.IsCompleted) break;
            }
        }
        catch (Exception ex) { FailTransport(ex, "Receive loop failed."); }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
            // Synchronize completion with application writes; PipeWriter is single-producer.
            await CompleteOutputWriterAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask CompleteOutputWriterAsync()
    {
        var acquired = SendLock.Wait(0);
        try
        {
            if (!acquired)
            {
                try { await SendLock.WaitAsync(_cts.Token).ConfigureAwait(false); acquired = true; }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
            }
            await Out.Writer.CompleteAsync().ConfigureAwait(false);
        }
        finally { if (acquired) SendLock.Release(); }
    }
    protected async Task ReadPipeAsync(PipeReader reader)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.End;
                try
                {
                    if (result.IsCanceled) break;
                    if (!buffer.IsEmpty) ParseBuffer(buffer, out consumed, out examined);
                    if (result.IsCompleted)
                    {
                        if (buffer.Slice(consumed).Length > 0 || _partialLength > 0)
                            throw new ProtocolException("Connection ended with an incomplete package.");
                        break;
                    }
                }
                finally { reader.AdvanceTo(consumed, examined); }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (ChannelQueueOverflowException ex)
        {
            Logger?.LogError(ex, "Channel {Identity} receive queue overflow: pending={Pending}, capacity={Capacity}.",
                Identity, ex.Pending, ex.Capacity);
            RecordFailure(ex, SuperClient.CloseReason.InternalError, "receive-queue");
        }
        catch (Exception ex)
        {
            CloseReason = SuperClient.CloseReason.ProtocolError;
            OnError("Protocol error.", ex);
            _cts.Cancel();
            Close();
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            WriteEOFPackage();
            Interlocked.CompareExchange(ref _failure, new Failure(new IOException("Receive stream ended."),
                CloseReason ?? SuperClient.CloseReason.RemoteClosing), null);
            _cts.Cancel();
        }
    }

    private void ParseBuffer(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
    {
        consumed = buffer.Start;
        examined = buffer.End;
        int switchesWithoutProgress = 0;
        while (!buffer.IsEmpty)
        {
            var filter = _pipelineFilter;
            var reader = new SequenceReader<byte>(buffer);
            var package = filter.Filter(ref reader);
            var next = filter.NextFilter;
            long count = reader.Consumed;
            long length = _partialLength + count + (package is null && next is null ? reader.Remaining : 0);
            if (length > Options.MaxPackageLength) throw new ProtocolException($"Package exceeds {Options.MaxPackageLength} bytes.");
            _partialLength += count;
            consumed = reader.Position;
            if (next is not null)
            {
                next.Context = filter.Context;
                _pipelineFilter = next;
            }
            if (package is not null)
            {
                if (count == 0 && _partialLength == 0) throw new ProtocolException("Filter returned a package without consuming data.");
                _partialLength = 0;
                filter.Reset();
                _packagePipe.Write(package);
            }
            else if (next is null)
            {
                // A pending consumer still needs more bytes for this fragmented frame.
                (_packagePipe as ISupplyController)?.SupplyEnd();
                return;
            }
            else filter.Reset();
            if (count == 0)
            {
                if (++switchesWithoutProgress > 64) throw new ProtocolException("Pipeline filter made no progress.");
            }
            else switchesWithoutProgress = 0;
            buffer = buffer.Slice(reader.Position);
        }
        examined = consumed;
        if (_partialLength > 0) (_packagePipe as ISupplyController)?.SupplyEnd();
    }

    private void FailTransport(Exception ex, string message)
    {
        if (!_cts.IsCancellationRequested)
            RecordFailure(ex, ex is TimeoutException ? SuperClient.CloseReason.TimeOut : SuperClient.CloseReason.SocketError, message);
    }    protected static CancellationTokenSource? CreateTimeout(CancellationToken token, int milliseconds)
    {
        if (milliseconds == 0) return null;
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(milliseconds);
        return source;
    }

    public override async ValueTask CloseAsync(CloseReason closeReason)
    {
        if (Volatile.Read(ref _closedInternally) != 0) return;
        Interlocked.CompareExchange(ref _failure, new Failure(new IOException("Channel closed."), closeReason), null);
        CloseReason ??= closeReason;
        _cts.Cancel();
        try { Close(); }
        finally
        {
            In.Reader.CancelPendingRead();
            In.Writer.CancelPendingFlush();
            Out.Reader.CancelPendingRead();
            Out.Writer.CancelPendingFlush();
            await _closingTask.ConfigureAwait(false);
            OnClosed();
            Volatile.Write(ref _closedInternally, 1);
        }
    }

    private int _closedInternally;
    protected void CheckChannelOpen()
    {
        if (IsClosed || _cts.IsCancellationRequested) throw new InvalidOperationException("Channel is closed.");
    }
    protected void WritePackageWithEncoder<TPackage>(IBufferWriter<byte> writer, IPackageEncoder<TPackage> encoder, TPackage package)
    {
        CheckChannelOpen();
        encoder.Encode(writer, package);
    }
    protected internal ArraySegment<T> GetArrayByMemory<T>(ReadOnlyMemory<T> memory)
        => MemoryMarshal.TryGetArray(memory, out var result) ? result : throw new InvalidOperationException("Expected array-backed memory.");
    protected void WriteEOFPackage() => _packagePipe.Complete();
    public override ValueTask DetachAsync() => ValueTask.FromException(new NotSupportedException(
        "Transport ownership cannot be transferred. Use CloseAsync instead."));
    protected void OnError(string message, Exception? ex = null) => Logger?.LogError(ex, "{Message}", message);
    protected virtual bool IsIgnorableException(Exception ex)
        => ex is ObjectDisposedException or OperationCanceledException;
    protected abstract void Close();
    protected abstract ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory, CancellationToken cancellationToken);
    protected abstract ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken);
}





