using System.Text.RegularExpressions;

namespace LyricBar.Services;

/// <summary>一行歌词：时间点（毫秒）+ 歌词文本。</summary>
public sealed record LrcLine(double TimeMs, string Text);

/// <summary>
/// LRC 歌词解析器：把 "[00:12.34]歌词内容" 这种格式解析成带时间戳的行列表。
/// </summary>
public static partial class LrcParser
{
    /// <summary>匹配形如 [mm:ss]、[mm:ss.xx]、[mm:ss.xxx] 的时间标签。</summary>
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimestampTag();

    /// <summary>
    /// 解析整段 LRC 文本。会跳过 [ti:]、[ar:]、[al:] 等元信息标签，
    /// 同一行如果有多个时间标签（副歌复用）会展开成多条记录，最后按时间排序。
    /// </summary>
    public static List<LrcLine> Parse(string? lrc)
    {
        var result = new List<LrcLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return result;

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = TimestampTag().Matches(line);
            if (matches.Count == 0) continue; // 没有时间标签（例如 [ti:歌名]）直接跳过

            // 歌词正文取最后一个时间标签之后的部分
            var text = line[(matches[^1].Index + matches[^1].Length)..].Trim();
            if (text.Length == 0) continue; // 空行（间奏）不显示

            foreach (Match m in matches)
            {
                var minutes = double.Parse(m.Groups[1].Value);
                var seconds = double.Parse(m.Groups[2].Value);
                double frac;
                if (m.Groups[3].Success)
                {
                    // 小数部分按位数换算：".5" 是 0.5 秒，".50" 是 0.50 秒
                    var digits = m.Groups[3].Value;
                    frac = double.Parse(digits) / Math.Pow(10, digits.Length);
                }
                else frac = 0;

                var ms = (minutes * 60 + seconds + frac) * 1000;
                result.Add(new LrcLine(ms, text));
            }
        }

        result.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return result;
    }

    /// <summary>
    /// 用二分查找找出某个播放位置应该显示第几行。
    /// 返回最后一个时间点不晚于 posMs 的行；如果还没到第一句歌词则返回 -1。
    /// </summary>
    public static int FindIndex(List<LrcLine> lines, double posMs)
    {
        var lo = 0;
        var hi = lines.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].TimeMs <= posMs) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}
