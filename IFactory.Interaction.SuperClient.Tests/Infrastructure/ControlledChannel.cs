using System.Buffers;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace IFactory.Interaction.SuperClient.Tests.Infrastructure;

// Every transport operation is visible before the test releases it. No timing
// assumptions, real network failures, polling, or Thread.Sleep are needed.
internal class ControlledChannel(ChannelOptions? options = null)
    : PipeChannel<byte[]>(new FixedSizePipelineFilter(1), options ?? new ChannelOptions())
{
    internal sealed class SendCall(byte[] bytes)
    {
        private readonly TaskCompletionSource<int> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Bytes { get; } = bytes;
        public void Accept(int count) => _result.TrySetResult(count);
        public void Fail(Exception error) => _result.TrySetException(error);
        internal Task<int> WaitAsync(CancellationToken token) => _result.Task.WaitAsync(token);
    }
    private readonly Channel<SendCall> _calls = Channel.CreateUnbounded<SendCall>();
    private readonly List<byte> _accepted = new();
    public byte[] Accepted { get { lock (_accepted) return _accepted.ToArray(); } }
    public Task<SendCall> NextSendAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    protected override async ValueTask<int> SendOverIOAsync(ReadOnlySequence<byte> buffer, CancellationToken token)
    {
        var call = new SendCall(buffer.ToArray());
        _calls.Writer.TryWrite(call);
        var count = await call.WaitAsync(token).ConfigureAwait(false);
        if (count > 0 && count <= call.Bytes.Length)
            lock (_accepted) _accepted.AddRange(call.Bytes.AsSpan(0, count).ToArray());
        return count;
    }
    protected override async ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory, CancellationToken token)
    { await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); return 0; }
    protected override void Close() { }
}

internal sealed class RecordingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();
    public (LogLevel Level, string Message)[] Entries { get { lock (_entries) return _entries.ToArray(); } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
    { lock (_entries) _entries.Add((level, formatter(state, error))); }
}
