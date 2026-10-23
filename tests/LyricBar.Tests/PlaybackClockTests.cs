using LyricBar.Services;
using Xunit;

namespace LyricBar.Tests;

/// <summary>
/// 播放进度换算的回归测试。
/// 这些用例锁定的是「播放器上报延迟必须补偿」这一行为，
/// 防止退回到直接用原始进度、导致歌词固定落后的老问题。
/// </summary>
public class PlaybackClockTests
{
    /// <summary>
    /// 核心回归用例：Spotify 大约每 2 秒才上报一次，
    /// 读取到 5009ms 时如果这条样本已经是 2 秒前的，
    /// 当前真实进度应该是 7009ms，而不是 5009ms。
    /// </summary>
    [Fact]
    public void Compute_PlayingWithTwoSecondOldSample_AddsElapsedTime()
    {
        var now = new DateTimeOffset(2026, 10, 23, 13, 16, 2, 109, TimeSpan.FromHours(8));
        var reportedAt = now.AddSeconds(-2);

        var pos = PlaybackClock.Compute(5009, reportedAt, now, playing: true);

        Assert.Equal(7009, pos, precision: 3);
    }

    /// <summary>暂停时进度必须冻结，不能继续累加。</summary>
    [Fact]
    public void Compute_Paused_DoesNotAdvance()
    {
        var now = new DateTimeOffset(2026, 10, 23, 13, 16, 2, 109, TimeSpan.FromHours(8));

        var pos = PlaybackClock.Compute(5009, now.AddSeconds(-2), now, playing: false);

        Assert.Equal(5009, pos, precision: 3);
    }

    /// <summary>时间戳过于久远说明数据不可信，退回原始进度。</summary>
    [Fact]
    public void Compute_StaleSample_ReturnsReportedPosition()
    {
        var now = new DateTimeOffset(2026, 10, 23, 13, 16, 2, 109, TimeSpan.FromHours(8));

        var pos = PlaybackClock.Compute(5009, now.AddMinutes(-10), now, playing: true);

        Assert.Equal(5009, pos, precision: 3);
    }

    /// <summary>时间戳来自未来说明数据不可信，退回原始进度。</summary>
    [Fact]
    public void Compute_FutureTimestamp_ReturnsReportedPosition()
    {
        var now = new DateTimeOffset(2026, 10, 23, 13, 16, 2, 109, TimeSpan.FromHours(8));

        var pos = PlaybackClock.Compute(5009, now.AddSeconds(10), now, playing: true);

        Assert.Equal(5009, pos, precision: 3);
    }

    /// <summary>允许极小的时钟偏差，此时按 0 补偿处理，不应出现负增加。</summary>
    [Fact]
    public void Compute_SmallClockSkew_DoesNotGoNegative()
    {
        var now = new DateTimeOffset(2026, 10, 23, 13, 16, 2, 109, TimeSpan.FromHours(8));

        var pos = PlaybackClock.Compute(5009, now.AddMilliseconds(800), now, playing: true);

        Assert.Equal(5009, pos, precision: 3);
    }

    /// <summary>
    /// 用 2026-10-23 实际日志里的 Spotify 采样回放：
    /// 修复前这些点分别滞后约 3.8 秒、1.5 秒、0.9 秒，
    /// 修复后换算结果应与真实进度一致。
    /// </summary>
    [Fact]
    public void Compute_ReplayRealSpotifySamples_RemovesLag()
    {
        // 以一个固定日期为基准，把日志里「第几秒」换算成时间
        var baseDate = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.FromHours(8));

        var samples = new (double RawPos, int UpdatedAtMs, int ReadAtMs, double Expected)[]
        {
            (5009,  54_000, 56_017,  7_026),   // 5009 + 2017
            (9509,  58_000, 60_074, 11_583),   // 9509 + 2074
            (14010, 63_000, 64_142, 15_152),   // 14010 + 1142
        };

        foreach (var s in samples)
        {
            var pos = PlaybackClock.Compute(
                s.RawPos,
                baseDate.AddMilliseconds(s.UpdatedAtMs),
                baseDate.AddMilliseconds(s.ReadAtMs),
                playing: true);

            Assert.Equal(s.Expected, pos, precision: 0);
        }
    }

    /// <summary>
    /// 暂停瞬间必须固化「当前换算进度」。
    /// 日志里的真实场景：原始进度 1069ms，采样时刻在 12 秒前，
    /// 此时暂停应冻结在 13069ms，而不是退回 1069ms（那会回跳一句）。
    /// </summary>
    [Fact]
    public void FreezeOnPause_UsesCurrentProgress_NotStaleReport()
    {
        var reportedAt = new DateTimeOffset(2026, 10, 23, 13, 20, 33, TimeSpan.FromHours(8));
        var pauseAt = reportedAt.AddSeconds(12);

        var frozen = PlaybackClock.FreezeOnPause(1069, reportedAt, pauseAt);

        Assert.Equal(13069, frozen, precision: 3);
    }

    /// <summary>
    /// 时间戳和进度都相同，才是真正的重复样本，必须忽略，
    /// 否则暂停后会把刚固化的进度覆盖回旧值。
    /// </summary>
    [Fact]
    public void IsDuplicateSample_SameTimeAndSamePosition_ReturnsTrue()
    {
        var sampleAt = new DateTimeOffset(2026, 10, 23, 13, 20, 33, TimeSpan.FromHours(8));

        Assert.True(PlaybackClock.IsDuplicateSample(sampleAt, 5009, sampleAt, 5009));
    }

    /// <summary>
    /// 时间戳相同但进度变了，说明同一时刻内发生了拖动，
    /// 这种情况必须采纳，不能因为时间戳没变就丢掉。
    /// </summary>
    [Fact]
    public void IsDuplicateSample_SameTimeDifferentPosition_ReturnsFalse()
    {
        var sampleAt = new DateTimeOffset(2026, 10, 23, 13, 20, 33, TimeSpan.FromHours(8));

        Assert.False(PlaybackClock.IsDuplicateSample(sampleAt, 47000, sampleAt, 5009));
    }

    /// <summary>时间戳更新的样本一律视为新样本。</summary>
    [Fact]
    public void IsDuplicateSample_NewerTime_ReturnsFalse()
    {
        var sampleAt = new DateTimeOffset(2026, 10, 23, 13, 20, 33, TimeSpan.FromHours(8));

        Assert.False(PlaybackClock.IsDuplicateSample(sampleAt.AddSeconds(2), 5009, sampleAt, 5009));
    }

    /// <summary>
    /// 切歌时标题先更新、时间轴后更新，期间读到的上一首旧样本必须丢弃。
    /// </summary>
    [Fact]
    public void IsStaleAfterTrackChange_OldSample_ReturnsTrue()
    {
        var changedAt = new DateTimeOffset(2026, 10, 23, 13, 39, 53, 880, TimeSpan.FromHours(8));
        var oldSampleAt = new DateTimeOffset(2026, 10, 23, 13, 39, 50, 328, TimeSpan.FromHours(8));

        Assert.True(PlaybackClock.IsStaleAfterTrackChange(oldSampleAt, changedAt, TimeSpan.FromSeconds(1.5)));
    }

    /// <summary>新歌刚开始、时间轴略早于检测时刻的样本仍然有效。</summary>
    [Fact]
    public void IsStaleAfterTrackChange_RecentSample_ReturnsFalse()
    {
        var changedAt = new DateTimeOffset(2026, 10, 23, 13, 39, 53, 880, TimeSpan.FromHours(8));
        var newSampleAt = changedAt.AddMilliseconds(-800);

        Assert.False(PlaybackClock.IsStaleAfterTrackChange(newSampleAt, changedAt, TimeSpan.FromSeconds(1.5)));
    }
}
