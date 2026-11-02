using System.Buffers;
using Microsoft.Extensions.Logging;
using SuperSocket.ProtoBase;

namespace IFactory.Interaction.SuperClient;

public class ArrayPackageEncoder : IPackageEncoder<byte[]>
{
    public int Encode(IBufferWriter<byte> writer, byte[] package)
    {
        ArgumentNullException.ThrowIfNull(package);
        writer.Write(package);
        return package.Length;
    }
}

public class FixedSizePipelineFilter : PipelineFilterBase<byte[]>
{
    private readonly int _size;
    public FixedSizePipelineFilter(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        _size = size;
    }
    public override byte[] Filter(ref SequenceReader<byte> reader)
    {
        if (reader.Remaining < _size) return null!;
        var package = reader.Sequence.Slice(reader.Position, _size);
        var decoded = DecodePackage(ref package);
        reader.Advance(_size);
        return decoded;
    }
    protected override byte[] DecodePackage(ref ReadOnlySequence<byte> buffer) => buffer.ToArray();
}

public abstract class FixedHeaderPipelineFilter : FixedSizePipelineFilter
{
    protected readonly int _headerSize;
    public FixedHeaderPipelineFilter(int headerSize) : base(headerSize) => _headerSize = headerSize;
    public override byte[] Filter(ref SequenceReader<byte> reader)
    {
        if (reader.Remaining < _headerSize) return null!;
        var header = reader.Sequence.Slice(reader.Position, _headerSize);
        var bodyLength = GetBodyLengthFromHeader(ref header);
        if (bodyLength < 0) throw new ProtocolException("Negative package body length.");
        int totalSize = checked(_headerSize + bodyLength);
        if (reader.Remaining < totalSize) return null!;
        var package = reader.Sequence.Slice(reader.Position, totalSize);
        var decoded = DecodePackage(ref package);
        reader.Advance(totalSize);
        return decoded;
    }
    protected abstract int GetBodyLengthFromHeader(ref ReadOnlySequence<byte> buffer);
}

public class TerminatorPipelineFilter : PipelineFilterBase<byte[]>
{
    private readonly ReadOnlyMemory<byte> _terminator;
    public TerminatorPipelineFilter(ReadOnlyMemory<byte> terminator)
    {
        if (terminator.IsEmpty) throw new ArgumentException("Terminator cannot be empty.", nameof(terminator));
        _terminator = terminator.ToArray();
    }
    public override byte[] Filter(ref SequenceReader<byte> reader)
    {
        if (!reader.TryReadTo(out ReadOnlySequence<byte> package, _terminator.Span, advancePastDelimiter: false)) return null!;
        var decoded = DecodePackage(ref package);
        reader.Advance(_terminator.Length);
        return decoded;
    }
    protected override byte[] DecodePackage(ref ReadOnlySequence<byte> buffer) => buffer.ToArray();
}

public class SimplifiedClient : SuperClient<byte[], byte[]>
{
    public SimplifiedClient(string uri, IPipelineFilter<byte[]> pipelineFilter, ILogger? logger = null)
        : base(uri, pipelineFilter, new ArrayPackageEncoder(), new ImmediatePackageScheduler<byte[], byte[]>(), logger) { }
    public SimplifiedClient(string uri, IPipelineFilter<byte[]> pipelineFilter, ChannelOptions options, ILogger? logger = null)
        : base(uri, pipelineFilter, new ArrayPackageEncoder(), new ImmediatePackageScheduler<byte[], byte[]>(), logger, options) { }}

