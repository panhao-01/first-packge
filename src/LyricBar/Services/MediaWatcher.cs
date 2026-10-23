using System.Diagnostics;
using System.IO;
using Windows.Media.Control;

namespace LyricBar.Services;

/// <summary>当前正在播放的曲目信息。</summary>
public sealed record MediaInfo(string Title, string Artist, string AppName);

/// <summary>
/// 通过 Windows 系统媒体控制接口（SMTC）监听当前播放的歌曲。
/// 浏览器和已知的视频类应用会被排除，避免网页播放抢占音乐播放器。
///
/// 进度处理说明：部分播放器（典型如网易云）在 SMTC 里完全不上报时间轴
/// （位置、时长、时间戳全是 0），因此本类会用本地时钟重建播放进度，
/// 并在每次切歌时归零；能正常上报时间轴的播放器（如 Spotify）则直接采信它。
/// </summary>
public sealed class MediaWatcher : IDisposable
{
    /// <summary>命中这些关键字的应用一律不作为音乐来源（浏览器、视频客户端等）。</summary>
    private static readonly string[] ExcludedAppMarkers =
    {
        "chrome", "msedge", "microsoftedge", "firefox", "opera", "brave",
        "vivaldi", "arc", "iexplore", "edge", "browser",
        "bilibili", "iqiyi", "youku", "qqlive", "potplayer", "vlc",
    };

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    /// <summary>主动轮询定时器：播放器上报慢时靠它补上。</summary>
    private System.Threading.Timer? _pollTimer;
    /// <summary>刷新防重入标记（0 空闲 / 1 刷新中）。</summary>
    private int _refreshing;

    // --- 进度追踪 ---------------------------------------------------------
    // 情况一：播放器上报的时间轴可信 —— 记录锚点，用本地时钟外推。
    private bool _timelineReliable;
    private double _anchorPosMs;
    /// <summary>锚点对应的播放器上报时刻（不是读取时刻）。</summary>
    private DateTimeOffset _anchorAt = DateTimeOffset.Now;

    // 情况二：播放器不上报时间轴 —— 用「切歌至今经过的时间」重建。
    private DateTime _songStartAt = DateTime.Now;
    private TimeSpan _pausedAccum = TimeSpan.Zero;
    private DateTime? _pausedAt;
    private string? _lastTrackSignature;
    /// <summary>上一次采纳的播放器样本时刻，用于过滤重复读取的旧样本。</summary>
    private DateTimeOffset _lastAdoptedUpdated = DateTimeOffset.MinValue;
    /// <summary>上一次采纳的播放器进度值，配合时间戳判断是否为同一份样本。</summary>
    private double _lastAdoptedPosMs;
    /// <summary>检测到切歌的时刻，用于过滤上一首残留的时间轴样本。</summary>
    private DateTimeOffset _trackChangedAt = DateTimeOffset.MinValue;

    // 刚刚发起、但播放器还没确认的播放/暂停状态。
    private bool? _assumedPlaying;
    private DateTime _assumeUntil;
    /// <summary>
    /// 是否因为「真实暂停」冻结过锚点。
    /// 只有这种情况恢复播放时才需要重置锚点时间；
    /// 程序刚启动或播放器误报状态时不能重置，否则会丢掉样本年龄造成滞后。
    /// </summary>
    private bool _pausedAnchorFrozen;

    /// <summary>播放器上报的原始进度值，仅用于日志排查。</summary>
    public double LastRawPositionMs { get; private set; }
    /// <summary>播放器上报的原始时间戳，仅用于日志排查。</summary>
    public DateTimeOffset LastRawUpdated { get; private set; }
    /// <summary>true 表示当前采用播放器自己的时间轴，false 表示用本地时钟重建。</summary>
    public bool UsesPlayerTimeline => _timelineReliable;

    /// <summary>曲目信息或播放状态可能变化时触发。</summary>
    public event Action? MediaChanged;
    /// <summary>播放器上报的时间轴发生变化（拖动进度、跳转）时触发。</summary>
    public event Action? TimelineChanged;

    public bool HasSession => _session != null;
    /// <summary>当前是否在播放。</summary>
    public bool IsPlaying { get; private set; }
    /// <summary>当前曲目。</summary>
    public MediaInfo? Current { get; private set; }

    /// <summary>开始监听：接管当前会话，并启动周期性主动刷新。</summary>
    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (sender, args) => Attach(PickSession());
        Attach(PickSession());

        // 有些播放器（例如网易云）的 SMTC 事件会晚好几秒才发出来，
        // 所以自己主动轮询；100ms 用于验证轮询频率对定位延迟的影响。
        _pollTimer = new System.Threading.Timer(
            _ => _ = RefreshAsync(),
            null,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// 当前播放进度（毫秒）。这里是纯计算，不会调用系统接口，
    /// 因此可以被界面定时器高频读取而不会卡住 UI 线程。
    /// </summary>
    public double PositionMs
    {
        get
        {
            if (_session is null) return 0;

            // 播放器时间轴可信：从锚点开始，用本地时钟向前外推
            if (_timelineReliable)
            {
                // 用「上报进度 + (现在 - 上报时刻)」换算，补偿播放器上报间隔造成的滞后
                return Math.Max(0, PlaybackClock.Compute(
                    _anchorPosMs, _anchorAt, DateTimeOffset.Now, IsPlaying));
            }

            // 无可用时间轴：用本地时钟重建（切歌归零、暂停冻结）
            var elapsed = DateTime.Now - _songStartAt - _pausedAccum;
            if (_pausedAt is { } paused) elapsed -= DateTime.Now - paused;
            return Math.Max(0, elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// 播放 / 暂停。命令本身很快（毫秒级），但网易云会晚约 10 秒才上报新状态，
    /// 所以这里下发成功后立刻按「已生效」处理，避免界面等待十秒。
    /// </summary>
    public async Task PlayPauseAsync()
    {
        var s = _session;
        if (s is null) return;
        var sw = Stopwatch.StartNew();
        var wantPlaying = !IsPlaying;
        var ok = wantPlaying
            ? await s.TryPlayAsync().AsTask()
            : await s.TryPauseAsync().AsTask();
        Logger.Info($"control: play/pause command accepted={ok} returned after {sw.ElapsedMilliseconds}ms");
        if (ok) ApplyAssumedPlaying(wantPlaying);
        await RefreshAsync();
    }

    /// <summary>下一首。</summary>
    public async Task NextAsync()
    {
        var s = _session;
        if (s is null) return;
        var sw = Stopwatch.StartNew();
        var ok = await s.TrySkipNextAsync().AsTask();
        Logger.Info($"control: next accepted={ok} returned after {sw.ElapsedMilliseconds}ms");
        await RefreshAsync();
    }

    /// <summary>上一首。</summary>
    public async Task PrevAsync()
    {
        var s = _session;
        if (s is null) return;
        var sw = Stopwatch.StartNew();
        var ok = await s.TrySkipPreviousAsync().AsTask();
        Logger.Info($"control: prev accepted={ok} returned after {sw.ElapsedMilliseconds}ms");
        await RefreshAsync();
    }

    /// <summary>
    /// 采纳我们刚请求的播放/暂停状态，并设定一个确认期限。
    /// 播放器在这段时间内确认后就以它的状态为准；超时则放弃假设。
    /// </summary>
    private void ApplyAssumedPlaying(bool playing)
    {
        _assumedPlaying = playing;
        _assumeUntil = DateTime.Now.AddSeconds(20);
        SetPlayState(playing);
        Logger.Info($"control: assuming playing={playing}");
        MediaChanged?.Invoke();
    }

    /// <summary>更新播放状态，同时维护「暂停累计时长」以保证进度计算正确。</summary>
    private void SetPlayState(bool playing)
    {
        var wasPlaying = IsPlaying;

        if (playing)
        {
            // 从暂停恢复：把这段暂停时间累加到「已暂停时长」里
            if (_pausedAt is { } paused)
            {
                _pausedAccum += DateTime.Now - paused;
                _pausedAt = null;
            }

            // 只有从「真实暂停」恢复时，才从冻结位置重新开始计时，
            // 避免把暂停时长算进去。启动时的状态切换不能走这里。
            if (!wasPlaying && _timelineReliable && _pausedAnchorFrozen)
            {
                _anchorAt = DateTimeOffset.Now;
                _pausedAnchorFrozen = false;
            }
        }
        else
        {
            // 首次进入暂停：记下暂停开始的时刻
            _pausedAt ??= DateTime.Now;

            // 关键修复：暂停瞬间把「当前换算进度」固化为锚点。
            // 不固化的话会退回播放器上一次上报的旧值，歌词回跳一句。
            if (wasPlaying && _timelineReliable)
            {
                var now = DateTimeOffset.Now;
                var frozen = PlaybackClock.FreezeOnPause(_anchorPosMs, _anchorAt, now);
                _anchorPosMs = frozen;
                _anchorAt = now;
                _pausedAnchorFrozen = true;
                Logger.Info($"pause: frozen at {frozen:F0}ms (last report {LastRawPositionMs:F0}ms)");
            }
        }

        IsPlaying = playing;
    }

    /// <summary>从所有会话中挑选第一个「非浏览器 / 非视频应用」的会话。</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null) return null;

        // 遍历全部会话而不是只取「最近活跃」的那个，
        // 因为浏览器经常把「最近活跃」这个位置占掉。
        foreach (var s in _manager.GetSessions())
        {
            if (!IsExcluded(GetAppName(s.SourceAppUserModelId)))
                return s;
        }

        // 兜底：如果当前会话本身合格就用它
        var current = _manager.GetCurrentSession();
        return current is not null && !IsExcluded(GetAppName(current.SourceAppUserModelId))
            ? current
            : null;
    }

    /// <summary>把 AUMID（应用标识）转换成人能看懂的应用名。</summary>
    private static string GetAppName(string aumid)
    {
        var bang = aumid.IndexOf('!');
        if (bang > 0) aumid = aumid[..bang];
        var name = Path.GetFileName(aumid.Replace('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? aumid : name;
    }

    /// <summary>应用名命中排除列表时返回 true。</summary>
    private static bool IsExcluded(string appName)
    {
        var lower = appName.ToLowerInvariant();
        foreach (var marker in ExcludedAppMarkers)
            if (lower.Contains(marker)) return true;
        return false;
    }

    /// <summary>切换监听的会话，并重置进度时钟。</summary>
    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session)) return;

        _session = session;
        _lastTrackSignature = null;
        ResetClock();

        if (session is null)
        {
            // 没有任何合格会话：回到空闲状态
            Current = null;
            IsPlaying = false;
            _timelineReliable = false;
            MediaChanged?.Invoke();
            return;
        }

        // 会话对象生命周期短，事件处理器随之消亡，所以不必显式退订
        session.MediaPropertiesChanged += (s, e) => _ = RefreshAsync();
        session.PlaybackInfoChanged += (s, e) => _ = RefreshAsync();
        session.TimelinePropertiesChanged += (s, e) => OnTimelineChanged(s);

        _ = RefreshAsync();
    }

    /// <summary>把重建用的本地时钟归零（切歌、换会话时调用）。</summary>
    private void ResetClock()
    {
        _songStartAt = DateTime.Now;
        _anchorAt = DateTimeOffset.Now;
        _anchorPosMs = 0;
        _pausedAccum = TimeSpan.Zero;
        _pausedAt = null;
        _timelineReliable = false;
        _lastAdoptedUpdated = DateTimeOffset.MinValue;
        _lastAdoptedPosMs = 0;
        _pausedAnchorFrozen = false;
        _trackChangedAt = DateTimeOffset.MinValue;
    }

    /// <summary>播放器上报时间轴变化时，刷新锚点。</summary>
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var tl = s.GetTimelineProperties();
            LastRawPositionMs = tl.Position.TotalMilliseconds;
            LastRawUpdated = tl.LastUpdatedTime;
            TryAdoptReportedTimeline(tl.Position.TotalMilliseconds, tl.LastUpdatedTime);
            TimelineChanged?.Invoke();
        }
        catch { /* 会话可能已经消失 */ }
    }

    /// <summary>
    /// 只有在播放器上报的时间轴看起来合理时才采用它。
    /// 网易云会报出 1601 年的时间戳和 0 进度，这类值一律忽略，
    /// 否则算出来的进度会是个天文数字（曾导致歌词永远停在最后一句）。
    /// </summary>
    private void TryAdoptReportedTimeline(double reportedMs, DateTimeOffset updated)
    {
        if (updated.Year < 2000) return;                        // 1601 之类的假时间戳
        if (reportedMs < 0 || reportedMs > 21_600_000) return;  // 进度超出 0~6 小时

        // 时间戳必须落在合理范围内，否则宁可保留旧锚点，避免算出离谱进度
        var age = DateTimeOffset.Now - updated;
        if (age < TimeSpan.FromSeconds(-5) || age > PlaybackClock.MaxSampleAge) return;

        // 切歌后播放器可能还会短暂上报上一首的旧时间轴，必须丢弃
        if (PlaybackClock.IsStaleAfterTrackChange(updated, _trackChangedAt, TimeSpan.FromSeconds(1.5))) return;

        // 只有「时间戳和进度都没变」才算重复样本。
        // 同一时刻内拖动进度条时进度会明显变化，必须立即采纳。
        if (PlaybackClock.IsDuplicateSample(updated, reportedMs, _lastAdoptedUpdated, _lastAdoptedPosMs)) return;

        _lastAdoptedPosMs = reportedMs;
        _lastAdoptedUpdated = updated;
        _timelineReliable = true;
        _anchorPosMs = reportedMs;
        _anchorAt = updated; // 关键：锚定播放器上报时刻，而不是我读取它的时刻

        // 记录每次采纳的新样本，便于量测「拖动到定位」的真实延迟
        Logger.Info($"timeline adopt: pos={reportedMs:F0} upd={updated:HH:mm:ss.fff} " +
                    $"age={(DateTimeOffset.Now - updated).TotalMilliseconds:F0}ms");
    }

    /// <summary>
    /// 重新读取一次会话状态：曲目、播放状态、时间轴。
    /// 由播放器事件和轮询定时器共同调用。
    /// </summary>
    private async Task RefreshAsync()
    {
        // 事件和轮询可能同时触发，这里保证同一时刻只有一次刷新在执行
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        var s = _session;
        try
        {
            if (s is null) return;
            var props = await s.TryGetMediaPropertiesAsync().AsTask();
            var info = s.GetPlaybackInfo();
            var tl = s.GetTimelineProperties();

            var title = props.Title ?? "";
            var artist = props.Artist ?? "";
            var app = GetAppName(s.SourceAppUserModelId);
            Current = new MediaInfo(title, artist, app);

            // 曲目变了就把重建时钟归零
            var signature = $"{title}|{artist}|{app}";
            if (signature != _lastTrackSignature)
            {
                _lastTrackSignature = signature;
                ResetClock();
                // 记录切歌时刻；比这更早的时间轴样本属于上一首，不能采纳
                _trackChangedAt = DateTimeOffset.Now;
            }

            LastRawPositionMs = tl.Position.TotalMilliseconds;
            LastRawUpdated = tl.LastUpdatedTime;
            TryAdoptReportedTimeline(tl.Position.TotalMilliseconds, tl.LastUpdatedTime);

            // 暂停时不计入播放时长
            var nowPlaying = info.PlaybackStatus ==
                             GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // 如果本地刚发过播放/暂停命令，先按本地判断，等播放器确认
            if (_assumedPlaying is { } assumed)
            {
                if (nowPlaying == assumed || DateTime.Now > _assumeUntil)
                {
                    _assumedPlaying = null;
                    Logger.Info($"control: player reports playing={nowPlaying}");
                }
                else
                {
                    nowPlaying = assumed;
                }
            }

            if (nowPlaying != IsPlaying)
                Logger.Info($"state: playing {IsPlaying} -> {nowPlaying} (rawStatus={info.PlaybackStatus})");
            SetPlayState(nowPlaying);

            MediaChanged?.Invoke();
        }
        catch { /* 两次调用之间会话可能已失效 */ }
        finally { _refreshing = 0; }
    }

    /// <summary>释放资源，停止轮询。</summary>
    public void Dispose()
    {
        _session = null;
        _pollTimer?.Dispose();
        _pollTimer = null;
    }
}
