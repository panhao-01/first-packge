using System.Text;

namespace LyricBar.Services;

/// <summary>
/// 歌曲匹配打分器。
/// 搜索接口会返回一大堆同名歌曲（比如「晴天」有几十个版本），
/// 这里负责给每个候选打分，让上层挑出最可能是正在播放的那一首。
/// </summary>
public static class SearchMatcher
{
    /// <summary>
    /// 计算匹配得分，分数越高越可能是同一首歌。
    /// 打分规则：歌名完全相同 +100，歌名互相包含 +50；
    /// 歌手完全相同 +30，歌手互相包含 +15。满分 130。
    /// </summary>
    public static int Score(string candidateTitle, string candidateArtist, string title, string artist)
    {
        var ct = Normalize(candidateTitle);
        var ca = Normalize(candidateArtist);
        var t = Normalize(title);
        var a = Normalize(artist);

        var score = 0;
        if (ct.Length > 0 && ct == t) score += 100;
        else if (t.Length > 0 && (ct.Contains(t) || t.Contains(ct))) score += 50;

        if (a.Length > 0)
        {
            if (ca == a) score += 30;
            else if (ca.Contains(a) || a.Contains(ca)) score += 15;
        }
        return score;
    }

    /// <summary>
    /// 归一化文本：只保留字母、数字和汉字，其余（空格、标点、括号等）一律丢掉并转小写。
    /// 这样 "Sunny Day!" 和 "sunnyday" 会被视为同一个词，避免标点差异影响匹配。
    /// </summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch >= 0x4E00 && ch <= 0x9FFF) // 汉字统一表意文字区间
                sb.Append(ch);
        }
        return sb.ToString();
    }
}
