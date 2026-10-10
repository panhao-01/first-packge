using System.Threading;
using System.Windows;
using LyricBar.Services;
using LyricBar.Views;
using Application = System.Windows.Application;

namespace LyricBar;

/// <summary>
/// 程序入口。负责：单实例控制、全局异常记录、创建歌条窗口与托盘图标、启动媒体监听。
/// 注意本类是 WPF 的 Application，与 WinForms 的 Application 同名，
/// 因此文件顶部用 using 别名做了区分。
/// </summary>
public partial class App : Application
{
    /// <summary>单实例互斥锁：避免同时运行多个歌条。</summary>
    private static Mutex? _mutex;
    private MediaWatcher _watcher = null!;
    private LyricService _lyrics = null!;
    private AppSettings _settings = null!;
    private MainWindow _bar = null!;
    private System.Windows.Forms.NotifyIcon? _tray;

    /// <summary>程序启动流程。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 把未处理异常写进日志，方便事后定位崩溃原因
        DispatcherUnhandledException += (s, args) => Logger.Error("Dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex) Logger.Error("AppDomain", ex);
        };
        Logger.Info($"=== LyricBar start (pid={Environment.ProcessId}) ===");

        // 单实例检查：第二个实例直接退出，避免托盘出现多个图标
        _mutex = new Mutex(true, "LyricBar_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Logger.Info("Another instance is already running; exiting.");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        // 关闭设置窗口时不能退出程序，只有托盘「退出」才结束
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settings = SettingsService.Load();
        _lyrics = new LyricService();
        _watcher = new MediaWatcher();

        _bar = new MainWindow(_watcher, _lyrics, _settings);
        _bar.Show();

        CreateTrayIcon();

        // 异步启动媒体监听，不阻塞界面
        _ = _watcher.StartAsync();
    }

    /// <summary>创建系统托盘图标与右键菜单（设置 / 退出）。</summary>
    private void CreateTrayIcon()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "LyricBar",
            Visible = true,
        };
        try
        {
            // 优先用 exe 自带图标
            if (!string.IsNullOrEmpty(Environment.ProcessPath))
                _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        }
        catch
        {
            // 取图标失败时用系统默认图标兜底
            _tray.Icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("设置", image: null, (s, e) => OpenSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", image: null, (s, e) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => OpenSettings();
    }

    /// <summary>打开设置窗口。</summary>
    private void OpenSettings()
    {
        var win = new SettingsWindow(_bar, _settings);
        win.Show();
        win.Activate();
    }

    /// <summary>退出程序：先隐藏托盘图标再关闭窗口。</summary>
    private void ExitApp()
    {
        if (_tray is not null) _tray.Visible = false;
        _bar?.Close();
        Shutdown();
    }

    /// <summary>退出时释放资源并释放单实例锁。这里必须判空，
    /// 因为第二个实例会在初始化完成前就走到这里。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
