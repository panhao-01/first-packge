namespace LyricBar.Services;

/// <summary>
/// 播放进度换算工具：把播放器「上一次上报的进度 + 上报时刻」换算成当前时刻的进度。
/// 单独抽成纯函数，是为了能脱离 Windows 媒体接口做单元测试。
/// </summary>
public static class PlaybackClock
{
    /// <summary>上报时刻超过这个时长就认为数据不可信，直接采用原始进度。</summary>
    public static readonly TimeSpan MaxSampleAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 计算当前进度：
    /// 正在播放时 = 上报进度 + (当前时刻 - 上报时刻)；
    /// 暂停时保持上报进度不变。
    /// 时间戳异常（来自未来超过 2 秒、或早于 2 分钟）时退回原始进度，
    /// 避免算出离谱值影响歌词定位。
    /// </summary>
    public static double Compute(
        double reportedMs,
        DateTimeOffset reportedAt,
        DateTimeOffset now,
        bool playing)
    {
        // 进度不可能为负，先做一次兜底
        if (reportedMs < 0) reportedMs = 0;

        // 暂停状态下进度是冻结的，不做时间补偿
        if (!playing) return reportedMs;

        // 上报时刻比现在还晚超过 2 秒，说明时间戳不可信（允许少量时钟偏差）
        if (reportedAt - now > TimeSpan.FromSeconds(2)) return reportedMs;

        var age = now - reportedAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;       // 小偏差按 0 处理
        if (age > MaxSampleAge) return reportedMs;          // 过于久远，不可信

        // 关键：把「样本已经过去的时间」加回去，消除播放器上报间隔造成的固定滞后
        return reportedMs + age.TotalMilliseconds;
    }

    /// <summary>
    /// 暂停瞬间应该固化的进度。
    /// 必须按「仍在播放」换算当前真实进度，不能退回上一次上报的旧值，
    /// 否则暂停时歌词会回跳一句。
    /// </summary>
    public static double FreezeOnPause(
        double reportedMs,
        DateTimeOffset reportedAt,
        DateTimeOffset pauseAt)
    {
        return Compute(reportedMs, reportedAt, pauseAt, playing: true);
    }

    /// <summary>
    /// 判断这是不是「重复样本」。
    /// 规则：
    /// 1) 时间戳更新 → 一定是新样本；
    /// 2) 时间戳更旧 → 旧样本，忽略；
    /// 3) 时间戳相同 → 只有进度也相同才算重复。
    /// 这样同一秒内拖动进度条（进度值明显变化）不会被误判为重复。
    /// </summary>
    public static bool IsDuplicateSample(
        DateTimeOffset sampleAt,
        double samplePosMs,
        DateTimeOffset lastAdoptedAt,
        double lastAdoptedPosMs)
    {
        if (sampleAt > lastAdoptedAt) return false;
        if (sampleAt < lastAdoptedAt) return true;
        return Math.Abs(samplePosMs - lastAdoptedPosMs) < 1.0;
    }

    /// <summary>
    /// 判断样本是否是「切歌前残留的旧时间轴」。
    /// 播放器切换曲目时，标题会比时间轴先更新，期间读到的仍是上一首的进度；
    /// 这类样本的上报时刻明显早于检测到切歌的时刻，必须丢弃。
    /// </summary>
    public static bool IsStaleAfterTrackChange(
        DateTimeOffset sampleAt,
        DateTimeOffset trackChangedAt,
        TimeSpan tolerance)
    {
        return sampleAt < trackChangedAt - tolerance;
    }
}
