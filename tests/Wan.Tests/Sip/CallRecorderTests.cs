using System.Buffers.Binary;
using Mapache.Sip.Media;

namespace Mapache.Tests.Sip;

public class CallRecorderTests
{
    [Fact]
    public void HeaderDescribesPcm16MonoAt8Khz()
    {
        var header = CallRecorder.Header(samples: 8000);

        Assert.Equal("RIFF"u8.ToArray(), header[..4]);
        Assert.Equal("WAVE"u8.ToArray(), header[8..12]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)));    // PCM
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)));    // mono
        Assert.Equal(8000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)));
        Assert.Equal(16000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40))); // 1 s de datos
    }

    [Fact]
    public async Task MixesBothSidesAndWritesAValidFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapache-test-{Guid.NewGuid():N}.wav");
        try
        {
            var recorder = new CallRecorder(path);
            recorder.AddCaller(Enumerable.Repeat((short)1000, 1600).ToArray());
            recorder.AddOther(Enumerable.Repeat((short)500, 1600).ToArray());
            await Task.Delay(300);
            var duration = await recorder.StopAsync();

            var bytes = await File.ReadAllBytesAsync(path);
            var dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));
            Assert.True(duration > TimeSpan.FromMilliseconds(100));
            Assert.Equal(bytes.Length - 44, (int)dataBytes);
            // Las dos voces se suman en la misma muestra.
            Assert.Equal(1500, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
