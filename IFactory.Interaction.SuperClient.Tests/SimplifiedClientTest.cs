using System.Buffers;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class SimplifiedClientTest
{
    private sealed class HeaderFilter() : FixedHeaderPipelineFilter(1)
    {
        protected override int GetBodyLengthFromHeader(ref ReadOnlySequence<byte> buffer) => buffer.FirstSpan[0];
    }
    [Fact]
    public void HeaderFilterPreservesFollowingPackets()
    {
        var filter = new HeaderFilter();
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[] { 2, 10, 11, 1, 20 }));
        Assert.Equal(new byte[] { 2, 10, 11 }, filter.Filter(ref reader));
        filter.Reset();
        Assert.Equal(new byte[] { 1, 20 }, filter.Filter(ref reader));
        Assert.True(reader.End);
    }
    [Fact]
    public void HeaderFilterWaitsForWholePacket()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[] { 3, 1, 2 }));
        Assert.Null(new HeaderFilter().Filter(ref reader));
        Assert.Equal(0, reader.Consumed);
    }
    [Fact]
    public void FixedFilterHonorsCurrentPosition()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4 }));
        reader.Advance(1);
        var filter = new FixedSizePipelineFilter(2);
        Assert.Equal(new byte[] { 1, 2 }, filter.Filter(ref reader));
        Assert.Equal(new byte[] { 3, 4 }, filter.Filter(ref reader));
        Assert.Null(filter.Filter(ref reader));
    }
    [Fact]
    public void TerminatorConsumesOnlyOnePacketAndCopiesDelimiter()
    {
        byte[] delimiter = [13, 10];
        var filter = new TerminatorPipelineFilter(delimiter);
        delimiter[0] = 0;
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[] { 1, 13, 10, 2, 13, 10 }));
        Assert.Equal(new byte[] { 1 }, filter.Filter(ref reader));
        Assert.Equal(new byte[] { 2 }, filter.Filter(ref reader));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidSizes(int size) => Assert.Throws<ArgumentOutOfRangeException>(() => new FixedSizePipelineFilter(size));
    [Fact]
    public void RejectsEmptyTerminator() => Assert.Throws<ArgumentException>(() => new TerminatorPipelineFilter(ReadOnlyMemory<byte>.Empty));
}
