using System.Diagnostics;
using Mapache.Sip.Poc;

namespace Mapache.Tests.Sip;

public class LatencyTrackerTests
{
    private long _now;
    private readonly LatencyTracker _tracker;

    public LatencyTrackerTests() => _tracker = new LatencyTracker(() => _now);

    private void AdvanceMs(int ms) => _now += Stopwatch.Frequency * ms / 1000;

    [Fact]
    public void MeasuresFromLastVoiceToFirstAgentAudio()
    {
        _tracker.OnUserVoice();
        AdvanceMs(500);
        _tracker.OnUserVoice();
        AdvanceMs(800);

        Assert.NotNull(_tracker.OnAgentAudio());
        Assert.Equal(800, _tracker.Samples.Single().TotalMilliseconds, precision: 0);
    }

    [Fact]
    public void AgentAudioWithoutUserTurnIsNotMeasured()
    {
        Assert.Null(_tracker.OnAgentAudio());

        _tracker.OnUserVoice();
        _tracker.OnAgentAudio();
        Assert.Null(_tracker.OnAgentAudio());
        Assert.Single(_tracker.Samples);
    }

    [Fact]
    public void SummaryReportsPercentiles()
    {
        foreach (var ms in new[] { 900, 1100, 1400 })
        {
            _tracker.OnUserVoice();
            AdvanceMs(ms);
            _tracker.OnAgentAudio();
        }

        Assert.Equal("3 turnos, p50 1100 ms, p90 1400 ms, máx 1400 ms", _tracker.Summary());
    }
}
