using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LyricBar.Services;

/// <summary>
/// 用户设置数据。注意：这里只允许放能被 JSON 序列化的普通数值，
/// 之前放过 double.NaN 导致保存时抛异常、进程崩溃，教训在此。
/// </summary>
public sealed class AppSettings
{
    /// <summary>歌词字号。</summary>
    public double FontSize { get; set; } = 18;
    /// <summary>全局歌词偏移（毫秒），正数表示歌词延后显示。</summary>
    public int OffsetMs { get; set; }
    /// <summary>是否开启鼠标点击穿透。</summary>
    public bool ClickThrough { get; set; }
    /// <summary>是否开机自启。</summary>
    public bool AutoStart { get; set; }
    /// <summary>停靠边缘："Left"、"Right"、"Top" 或 "Bottom"。</summary>
    public string DockEdge { get; set; } = "Bottom";
}

/// <summary>
/// 设置的读写：保存到 %LocalAppData%\LyricBar\settings.json，
/// 开机自启通过注册表 HKCU\...\Run 实现。
/// </summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>配置文件路径。</summary>
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LyricBar", "settings.json");

    /// <summary>读取设置；文件不存在或损坏时返回默认值，绝不抛异常。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s is not null) return s;
            }
        }
        catch { /* 读取失败就退回默认设置 */ }
        return new AppSettings();
    }

    /// <summary>保存设置到磁盘。</summary>
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }

    /// <summary>
    /// 写入或删除开机自启项。开启时把当前 exe 路径写进注册表 Run 键。
    /// </summary>
    public static void ApplyAutoStart(bool enabled)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "LyricBar";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(valueName, $"\"{exe}\"");
        }
        else
        {
            if (key.GetValue(valueName) is not null)
                key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>查询当前是否已经开启开机自启。</summary>
    public static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("LyricBar") is not null;
    }
}
