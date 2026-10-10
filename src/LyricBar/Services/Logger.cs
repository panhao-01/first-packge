using System.IO;
using System.Text;

namespace LyricBar.Services;

/// <summary>
/// 极简的调试日志，用于排查现场问题（歌词不同步、崩溃、拖拽异常等）。
/// 日志写在 exe 同目录下的 debug.log，方便查找；如果该目录不可写，
/// 自动退回 %LocalAppData%\LyricBar\debug.log。
/// 原则：写日志绝不能抛异常影响主程序，任何失败都静默忽略。
/// </summary>
public static class Logger
{
    /// <summary>多线程写入时的互斥锁，避免日志内容互相穿插。</summary>
    private static readonly object Gate = new();
    private static string? _logFile;

    /// <summary>日志文件的完整路径（首次访问时确定并缓存）。</summary>
    public static string LogFile => _logFile ??= ResolvePath();

    /// <summary>决定日志落在哪里：优先 exe 同目录，失败则用本地应用数据目录。</summary>
    private static string ResolvePath()
    {
        try
        {
            // 首选：与 LyricBar.exe 放在一起
            var candidate = System.IO.Path.Combine(AppContext.BaseDirectory, "debug.log");
            File.AppendAllText(candidate, string.Empty);
            return candidate;
        }
        catch
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LyricBar");
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "debug.log");
        }
    }

    /// <summary>写一行带时间戳的日志。</summary>
    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                var file = LogFile;
                // 文件超过 2MB 就清空重建，防止无限增长占满磁盘
                if (File.Exists(file) && new FileInfo(file).Length > 2_000_000)
                    File.Delete(file);
                File.AppendAllText(
                    file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* 日志失败不能影响程序运行 */ }
    }

    /// <summary>记录异常，只取异常类型、消息和第一行调用栈，保持日志简短。</summary>
    public static void Error(string context, Exception ex)
    {
        var firstFrame = ex.StackTrace?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        Info($"ERROR [{context}] {ex.GetType().Name}: {ex.Message} | {firstFrame}");
    }
}
