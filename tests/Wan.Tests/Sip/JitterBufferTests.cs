using Wan.Sip.Media;

namespace Wan.Tests.Sip;

public class JitterBufferTests
{
    private static byte[] P(byte id) => [id];

    [Fact]
    public void InOrderPacketsAreReleasedImmediately()
    {
        var buffer = new JitterBuffer(depth: 3);

        Assert.Equal([P(1)], buffer.Push(100, P(1)));
        Assert.Equal([P(2)], buffer.Push(101, P(2)));
    }

    [Fact]
    public void OutOfOrderPacketIsReordered()
    {
        var buffer = new JitterBuffer(depth: 3);
        buffer.Push(100, P(1));

        Assert.Empty(buffer.Push(102, P(3)));
        Assert.Equal([P(2), P(3)], buffer.Push(101, P(2)));
        Assert.Equal(0, buffer.Lost);
    }

    [Fact]
    public void MissingPacketIsSkippedWhenDepthIsExceeded()
    {
        var buffer = new JitterBuffer(depth: 2);
        buffer.Push(100, P(1));

        Assert.Empty(buffer.Push(102, P(3)));
        Assert.Empty(buffer.Push(103, P(4)));
        Assert.Equal([P(3), P(4), P(5)], buffer.Push(104, P(5)));
        Assert.Equal(1, buffer.Lost);
    }

    [Fact]
    public void LateAndDuplicatePacketsAreDropped()
    {
        var buffer = new JitterBuffer();
        buffer.Push(100, P(1));
        buffer.Push(101, P(2));

        Assert.Empty(buffer.Push(100, P(1)));
        Assert.Equal(1, buffer.Dropped);
    }

    [Fact]
    public void SurvivesSequenceWrapAround()
    {
        var buffer = new JitterBuffer();
        buffer.Push(65534, P(1));
        buffer.Push(65535, P(2));

        Assert.Empty(buffer.Push(1, P(4)));
        Assert.Equal([P(3), P(4)], buffer.Push(0, P(3)));
    }
}
