using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LyricBar.Services;

namespace LyricBar;

/// <summary>
/// 歌条主窗口。职责：
/// 1) 显示歌名/歌手/歌词，并按时推进歌词；
/// 2) 拖动并吸附到屏幕四边，停靠在左/右时切换成竖排；
/// 3) 悬停显示上一首 / 播放暂停 / 下一首；
/// 4) 滚轮手动校准歌词时间；
/// 5) 通过设置窗口实时调整字号与点击穿透。
/// </summary>
public partial class MainWindow : Window
{
    // 两种停靠形态下的窗口尺寸：上下为横条，左右为竖条
    private static readonly System.Windows.Size HorizontalSize = new(720, 56);
    private static readonly System.Windows.Size VerticalSize = new(56, 520);

    private readonly MediaWatcher _watcher;
    private readonly LyricService _lyrics;
    private readonly AppSettings _settings;
    /// <summary>界面刷新定时器：每 200ms 推进一次歌词。</summary>
    private readonly DispatcherTimer _timer;

    /// <summary>当前歌曲的歌词行。</summary>
    private List<LrcLine> _lines = new();
    /// <summary>当前曲目标识（歌名|歌手），用于判断是否换歌。</summary>
    private string _trackKey = "";
    /// <summary>歌词请求的取消源：换歌时取消上一首未完成的请求。</summary>
    private CancellationTokenSource? _fetchCts;
    /// <summary>当前显示的歌词行号，-1 表示前奏占位。</summary>
    private int _lastLineIndex = int.MinValue;
    private DateTime _lastSampleAt = DateTime.MinValue;
    private DateTime _lastSnapshotAt = DateTime.MinValue;
    private DateTime _lastActionAt = DateTime.MinValue;
    private string _lastActionName = "";

    // 滚轮手动校准的临时偏移：只对当前这首歌有效，换歌归零。
    // 因为播放器完全不上报进度，用户在播放器里拖动进度条我们是感知不到的，
    // 只能由用户手动对齐。
    private double _seekOffsetMs;
    private DateTime _syncIndicatorUntil = DateTime.MinValue;

    // 手写拖拽状态（不用 WPF 自带的 DragMove，避免它鼠标捕获异常导致崩溃）
    private bool _dragging;
    private System.Windows.Point _lastScreen;

    // 点击穿透所需的窗口样式常量
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    public MainWindow(MediaWatcher watcher, LyricService lyrics, AppSettings settings)
    {
        InitializeComponent();
        _watcher = watcher;
        _lyrics = lyrics;
        _settings = settings;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += Timer_Tick;

        // 媒体事件在后台线程触发，这里切回 UI 线程再更新界面
        _watcher.MediaChanged += () => Dispatcher.InvokeAsync(OnMediaChanged);
    }

    /// <summary>窗口加载完成后套用设置、定位到停靠边并启动刷新定时器。</summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDebugOverrides();
        SetFontSize(_settings.FontSize);
        SetClickThrough(_settings.ClickThrough);
        ApplyOrientation(_settings.DockEdge);
        PositionOnEdge(GetWorkAreaDip(), _settings.DockEdge);
        Logger.Info($"window loaded: edge={_settings.DockEdge} size={Width}x{Height} at {Left},{Top}");
        _timer.Start();
    }

    /// <summary>
    /// 调试开关：exe 同目录存在 debug-ui.txt 时，用文件内容覆盖停靠边
    /// （Left/Right/Top/Bottom），并把窗口界面定时渲染成 ui-snapshot.png。
    /// 仅用于排查界面问题，日常使用不需要这个文件。
    /// </summary>
    private void ApplyDebugOverrides()
    {
        try
        {
            var flag = Path.Combine(AppContext.BaseDirectory, "debug-ui.txt");
            if (!File.Exists(flag)) return;
            var edge = File.ReadAllText(flag).Trim();
            if (edge is "Left" or "Right" or "Top" or "Bottom")
            {
                _settings.DockEdge = edge;
                Logger.Info($"debug-ui override: edge={edge}");
            }
        }
        catch { /* 仅调试用途 */ }
    }

    private bool DebugSnapshotEnabled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "debug-ui.txt"));

    /// <summary>把窗口自身渲染成 PNG，便于在没有桌面权限时检查界面效果。</summary>
    private void MaybeDebugSnapshot()
    {
        if (!DebugSnapshotEnabled) return;
        if ((DateTime.Now - _lastSnapshotAt).TotalSeconds < 3) return;
        _lastSnapshotAt = DateTime.Now;
        try
        {
            var w = (int)Math.Round(ActualWidth);
            var h = (int)Math.Round(ActualHeight);
            if (w <= 0 || h <= 0) return;
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(AppContext.BaseDirectory, "ui-snapshot.png"));
            encoder.Save(fs);
        }
        catch { /* 仅调试用途 */ }
    }

    // ---------- 曲目 / 歌词状态 ----------

    /// <summary>曲目或播放状态变化时的处理：更新显示，必要时重新拉歌词。</summary>
    private void OnMediaChanged()
    {
        var info = _watcher.Current;
        UpdatePlayIcon();

        // 没有播放中的音乐（或只检测到被排除的应用）：回到待机文案
        if (info is null || string.IsNullOrWhiteSpace(info.Title))
        {
            _trackKey = "";
            _lines = new List<LrcLine>();
            _lastLineIndex = int.MinValue;
            SetTrack("等待播放…", "");
            SetLyric("在任意播放器中播放音乐，这里会同步显示歌词");
            return;
        }

        SetTrack(info.Title, info.Artist);

        var key = info.Title + "|" + info.Artist;
        if (key != _trackKey)
        {
            Logger.Info($"track: title='{info.Title}' artist='{info.Artist}' " +
                        $"app='{info.AppName}' playing={_watcher.IsPlaying}{ActionDelaySuffix()}");
            _trackKey = key;
            _seekOffsetMs = 0; // 新歌从对齐状态开始
            _lines = new List<LrcLine>();
            _lastLineIndex = int.MinValue;
            SetLyric("正在搜索歌词…");
            _ = LoadLyricsAsync(info.Title, info.Artist);
        }
    }

    /// <summary>异步获取歌词；如果期间又换歌，旧请求作废。</summary>
    private async Task LoadLyricsAsync(string title, string artist)
    {
        _fetchCts?.Cancel();
        var cts = _fetchCts = new CancellationTokenSource();
        try
        {
            var lines = await _lyrics.GetLyricAsync(title, artist).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            if (lines is { Count: > 0 })
            {
                Logger.Info($"lyrics loaded: {lines.Count} lines for '{title} - {artist}' " +
                            $"span={lines[0].TimeMs:F0}..{lines[^1].TimeMs:F0}ms");
                _lines = lines;
                _lastLineIndex = int.MinValue;
                SyncLyric(); // 立即跳到当前该显示的那一句
            }
            else
            {
                Logger.Info($"lyrics NOT found for '{title} - {artist}'");
                SetLyric("未找到歌词");
            }
        }
        catch
        {
            if (!cts.IsCancellationRequested) SetLyric("歌词加载失败");
        }
    }

    /// <summary>界面定时器：推进歌词，并每 2 秒打一条诊断日志。</summary>
    private void Timer_Tick(object? sender, EventArgs e)
    {
        SyncLyric();
        MaybeDebugSnapshot();

        // 每 2 秒采样一次，用来在日志里看进度有没有正常推进、选中了哪一行
        if ((DateTime.Now - _lastSampleAt).TotalSeconds >= 2)
        {
            _lastSampleAt = DateTime.Now;
            Logger.Info($"tick pos={_watcher.PositionMs:F0}ms idx={_lastLineIndex} " +
                        $"lines={_lines.Count} playing={_watcher.IsPlaying} " +
                        $"mode={(_watcher.UsesPlayerTimeline ? "player" : "clock")} " +
                        $"rawPos={_watcher.LastRawPositionMs:F0} " +
                        $"rawUpd={_watcher.LastRawUpdated:yyyy-MM-dd HH:mm:ss} " +
                        $"lyric='{LyricTextH.Text}'");
        }
    }

    /// <summary>把显示的行推进到与当前播放进度匹配的那一句。</summary>
    private void SyncLyric()
    {
        if (_lines.Count == 0) return;
        if (DateTime.Now < _syncIndicatorUntil) return; // 校准提示显示中，先别覆盖

        // 实际使用的位置 = 播放位置 − 全局偏移 − 本次手动校准偏移
        var pos = _watcher.PositionMs - _settings.OffsetMs - _seekOffsetMs;
        var idx = LrcParser.FindIndex(_lines, pos);
        if (idx < 0)
        {
            // 还没到第一句歌词（前奏），显示音符占位而不是死盯第一句
            if (_lastLineIndex != -1)
            {
                _lastLineIndex = -1;
                SetLyric("♪");
            }
            return;
        }
        if (idx != _lastLineIndex)
        {
            _lastLineIndex = idx;
            SetLyric(_lines[idx].Text);
        }
    }

    // ---------- 界面更新辅助 ----------

    /// <summary>同时更新横排与竖排的歌名/歌手。</summary>
    private void SetTrack(string title, string artist)
    {
        TitleTextH.Text = title;
        ArtistTextH.Text = artist;
        var combined = string.IsNullOrWhiteSpace(artist) ? title : $"{title} · {artist}";
        TrackTextV.Text = ToVertical(combined, 12);
    }

    /// <summary>同时更新横排与竖排的歌词文本。</summary>
    private void SetLyric(string text)
    {
        LyricTextH.Text = text;
        LyricTextV.Text = ToVertical(text, 16);
    }

    /// <summary>把文字改成一字一行的竖排形式（超过长度上限就省略）。</summary>
    private static string ToVertical(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length > maxChars) text = text[..maxChars] + "…";
        return string.Join("\n", text.ToCharArray());
    }

    /// <summary>设置字号：歌词用设定值，歌名/歌手按比例略小。</summary>
    private void SetFontSize(double size)
    {
        var clamped = Math.Clamp(size, 12, 28);
        LyricTextH.FontSize = clamped;
        LyricTextV.FontSize = clamped;
        TitleTextH.FontSize = Math.Max(11, clamped - 5);
        ArtistTextH.FontSize = Math.Max(10, clamped - 7);
        TrackTextV.FontSize = Math.Max(10, clamped - 6);
    }

    private void UpdatePlayIcon() => SetPlayIcon(_watcher.IsPlaying);

    /// <summary>用 Segoe MDL2 字体图标表示播放/暂停状态。</summary>
    private void SetPlayIcon(bool playing)
    {
        var glyph = playing ? "\uE769" : "\uE768"; // E769=暂停图标，E768=播放图标
        PlayPauseButtonH.Content = glyph;
        PlayPauseButtonV.Content = glyph;
    }

    /// <summary>计算「距离上次点击过了多久」，用于日志里衡量播放器响应速度。</summary>
    private string ActionDelaySuffix()
    {
        if (_lastActionAt == DateTime.MinValue) return "";
        var ms = (DateTime.Now - _lastActionAt).TotalMilliseconds;
        var suffix = $" (responded to '{_lastActionName}' after {ms:F0}ms)";
        _lastActionAt = DateTime.MinValue;
        return suffix;
    }

    /// <summary>记录一次控制按钮点击，供后续日志计算延迟。</summary>
    private void NoteAction(string name)
    {
        _lastActionAt = DateTime.Now;
        _lastActionName = name;
        Logger.Info($"click: {name}");
    }

    // ---------- 播放控制 ----------

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        NoteAction("prev");
        await _watcher.PrevAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        NoteAction("next");
        await _watcher.NextAsync();
    }

    /// <summary>播放/暂停：先立刻切换图标给用户反馈，再交给监听器确认。</summary>
    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        NoteAction("play/pause");
        SetPlayIcon(!_watcher.IsPlaying); // 立即反馈
        await _watcher.PlayPauseAsync();  // 完成后由 MediaChanged 校正
    }

    // ---------- 拖拽与边缘吸附 ----------

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2) return; // 双击不触发拖拽（历史上会因此崩溃退出）
        _dragging = true;
        _lastScreen = PointToScreen(e.GetPosition(this));
        CaptureMouse();
    }

    /// <summary>
    /// 滚轮手动校准歌词：在播放器里拖过进度条后可用来对齐。
    /// 上滚歌词前进，下滚歌词后退，按住 Shift 每次加减 5 秒；
    /// 校准量只对当前这首歌有效，换歌自动归零。
    /// </summary>
    private void Window_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5000 : 1000;
        _seekOffsetMs += e.Delta > 0 ? -step : step;
        _seekOffsetMs = Math.Clamp(_seekOffsetMs, -600_000, 600_000);

        // 短暂显示校准提示，1.5 秒后自动恢复显示歌词
        _syncIndicatorUntil = DateTime.Now.AddMilliseconds(1500);
        _lastLineIndex = int.MinValue; // 提示结束后强制重画歌词
        SetLyric($"同步 {(_seekOffsetMs >= 0 ? "+" : "")}{_seekOffsetMs / 1000.0:0.#}s");
        Logger.Info($"sync nudge: offset={_seekOffsetMs:F0}ms");
        e.Handled = true;
    }

    /// <summary>拖拽中：把窗口跟着鼠标移动（按屏幕坐标差值平移）。</summary>
    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        var screen = PointToScreen(e.GetPosition(this));
        Left += screen.X - _lastScreen.X;
        Top += screen.Y - _lastScreen.Y;
        _lastScreen = screen;
    }

    /// <summary>松开鼠标：结束拖拽并吸附到最近的屏幕边缘。</summary>
    private void Window_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        SnapToNearestEdge();
    }

    /// <summary>比较窗口中心到四条边的距离，吸附到最近的一条并保存到设置。</summary>
    private void SnapToNearestEdge()
    {
        var wa = GetWorkAreaDip();
        var cx = Left + Width / 2;
        var cy = Top + Height / 2;

        var edges = new (string Name, double Dist)[]
        {
            ("Left",   Math.Abs(cx - wa.Left)),
            ("Right",  Math.Abs(wa.Right - cx)),
            ("Top",    Math.Abs(cy - wa.Top)),
            ("Bottom", Math.Abs(wa.Bottom - cy)),
        };

        var best = edges.OrderBy(e => e.Dist).First();
        _settings.DockEdge = best.Name;

        // 先按新朝向调整窗口尺寸，再沿该边定位
        ApplyOrientation(best.Name);
        PositionOnEdge(wa, best.Name);
        Logger.Info($"snap: edge={best.Name} size={Width}x{Height} at {Left},{Top}");
        SettingsService.Save(_settings);
    }

    /// <summary>切换横排 / 竖排：停靠左右用竖条，停靠上下用横条。</summary>
    private void ApplyOrientation(string edge)
    {
        var vertical = edge is "Left" or "Right";
        if (vertical)
        {
            Width = VerticalSize.Width;
            Height = VerticalSize.Height;
            HorizontalRoot.Visibility = Visibility.Collapsed;
            VerticalRoot.Visibility = Visibility.Visible;
        }
        else
        {
            Width = HorizontalSize.Width;
            Height = HorizontalSize.Height;
            HorizontalRoot.Visibility = Visibility.Visible;
            VerticalRoot.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>把窗口贴到工作区的指定边缘（左右垂直居中，上下水平居中）。</summary>
    private void PositionOnEdge(Rect wa, string edge)
    {
        var w = Width;
        var h = Height;
        switch (edge)
        {
            case "Left":
                Left = wa.Left;
                Top = wa.Top + (wa.Height - h) / 2;
                break;
            case "Right":
                Left = wa.Right - w;
                Top = wa.Top + (wa.Height - h) / 2;
                break;
            case "Top":
                Left = wa.Left + (wa.Width - w) / 2;
                Top = wa.Top;
                break;
            default: // Bottom
                Left = wa.Left + (wa.Width - w) / 2;
                Top = wa.Bottom - h;
                break;
        }
    }

    /// <summary>
    /// 获取主屏工作区（不含任务栏）并换算成 WPF 的设备无关单位。
    /// 系统 API 返回的是物理像素，高分屏下需要除以 DPI 缩放比。
    /// </summary>
    private Rect GetWorkAreaDip()
    {
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                   ?? new System.Drawing.Rectangle(0, 0, 1920, 1040);
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.PixelsPerInchX / 96.0;
        var scaleY = dpi.PixelsPerInchY / 96.0;
        return new Rect(area.Left / scaleX, area.Top / scaleY, area.Width / scaleX, area.Height / scaleY);
    }

    // ---------- 鼠标悬停 ----------

    /// <summary>鼠标移入时显示播放控制按钮。</summary>
    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ControlsPanelH.Opacity = 1;
        ControlsPanelV.Opacity = 1;
    }

    /// <summary>鼠标移出时隐藏控制按钮，保持界面干净。</summary>
    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ControlsPanelH.Opacity = 0;
        ControlsPanelV.Opacity = 0;
    }

    // ---------- 点击穿透与设置 ----------

    /// <summary>设置窗口点保存时调用：套用字号、穿透、停靠边并持久化。</summary>
    public void ApplySettings(AppSettings settings)
    {
        SetFontSize(settings.FontSize);
        SetClickThrough(settings.ClickThrough);
        ApplyOrientation(settings.DockEdge);
        PositionOnEdge(GetWorkAreaDip(), settings.DockEdge);
        Logger.Info($"settings applied: font={settings.FontSize} offset={settings.OffsetMs} " +
                    $"clickThrough={settings.ClickThrough} edge={settings.DockEdge}");
        SettingsService.Save(settings);
    }

    /// <summary>拖动字号滑块时的实时预览，不写配置文件。</summary>
    public void PreviewFontSize(double size) => SetFontSize(size);

    /// <summary>
    /// 开关鼠标点击穿透：加上 WS_EX_TRANSPARENT 后鼠标事件会穿透到下层窗口。
    /// </summary>
    private void SetClickThrough(bool enabled)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (enabled) style |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            else style &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
            _ = SetWindowLong(hwnd, GWL_EXSTYLE, style);
        }
        catch
        {
            // 窗口句柄还没准备好时忽略
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
