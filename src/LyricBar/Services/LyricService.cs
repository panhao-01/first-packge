using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyricBar.Services;

/// <summary>
/// 歌词获取服务：同时搜索 QQ 音乐和网易云，按匹配度挑出最可能的候选，
/// 逐个尝试获取歌词（某个音源没歌词就换下一个），并缓存到
/// %LocalAppData%\LyricBar\cache。
/// </summary>
public sealed class LyricService
{
    /// <summary>这些关键词出现的短行是「作词/作曲/编曲」之类的署名，不是歌词。</summary>
    private static readonly string[] CreditMarkers =
    {
        "作词", "作曲", "词：", "曲：", "编曲", "制作人", "合声", "和声", "吉他", "贝斯",
        "鼓", "录音", "混音", "弦乐", "母带", "监制", "出品", "发行",
    };

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    /// <summary>正在请求中的缓存键，避免同一首歌重复发起网络请求。</summary>
    private readonly ConcurrentDictionary<string, byte> _inflight = new();

    public LyricService(HttpClient? http = null)
    {
        if (http is not null)
        {
            _http = http;
        }
        else
        {
            // 开启 gzip/deflate 自动解压：接口在收到压缩请求头时会返回压缩内容，
            // 不解压就会读成乱码。同时伪装成浏览器 UA，提高接口成功率。
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            _http = new HttpClient(handler);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LyricBar/1.0");
        }

        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LyricBar", "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// 根据歌名和歌手获取歌词。命中缓存直接返回；
    /// 否则并发搜索两个音源，按匹配度依次尝试，返回第一个拿到歌词的结果。
    /// 全部失败返回 null。
    /// </summary>
    public async Task<List<LrcLine>?> GetLyricAsync(string title, string artist)
    {
        var key = CacheKey(title, artist);

        // 1) 先查本地缓存
        var cached = LoadCache(key);
        if (cached is not null) return cached;

        // 2) 同一首歌避免并发重复请求
        if (!_inflight.TryAdd(key, 0)) return null;
        try
        {
            // 3) 两个音源并行搜索，然后合并候选
            var neteaseTask = SearchNetEaseAsync(title, artist);
            var qqTask = SearchQqAsync(title, artist);
            await Task.WhenAll(neteaseTask, qqTask).ConfigureAwait(false);

            var hits = new List<SearchHit>();
            hits.AddRange(neteaseTask.Result);
            hits.AddRange(qqTask.Result);

            // 4) 按匹配度排序后依次尝试。有的音源能搜到歌但没有歌词，
            //    所以必须能回退到下一个候选，否则会出现「明明有歌却没歌词」。
            var ranked = hits
                .Select(hit => (hit, score: SearchMatcher.Score(hit.Title, hit.Artist, title, artist)))
                .Where(x => x.score > 1)
                .OrderByDescending(x => x.score)
                .Select(x => x.hit)
                .ToList();

            var tried = new HashSet<string>();
            foreach (var hit in ranked)
            {
                var identity = hit.Source == "qq" ? $"qq:{hit.SongMid}" : $"ne:{hit.Id}";
                if (!tried.Add(identity)) continue;
                if (tried.Count > 4) break; // 限制请求数量，避免拖慢响应

                var lines = hit.Source == "qq"
                    ? await FetchQqLyricAsync(hit.SongMid!).ConfigureAwait(false)
                    : await FetchNetEaseLyricAsync(hit.Id).ConfigureAwait(false);

                lines = Clean(lines);
                if (lines is { Count: > 0 })
                {
                    Logger.Info($"lyrics source: {hit.Source} '{hit.Title} - {hit.Artist}' -> {lines.Count} lines");
                    SaveCache(key, lines);
                    return lines;
                }
            }
            Logger.Info($"lyrics NOT found for '{title} - {artist}' ({hits.Count} candidates)");
            return null;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    /// <summary>一次搜索命中的候选：来源、网易云 id 或 QQ songmid、歌名、歌手。</summary>
    private sealed record SearchHit(string Source, long Id, string? SongMid, string Title, string Artist);

    // ---------- 网易云音乐 ----------

    /// <summary>搜索网易云，返回候选列表（失败返回空列表）。</summary>
    private async Task<List<SearchHit>> SearchNetEaseAsync(string title, string artist)
    {
        var result = new List<SearchHit>();
        try
        {
            var query = string.IsNullOrWhiteSpace(artist)
                ? title
                : $"{title} {artist}";
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = query.Trim(),
                ["type"] = "1",
                ["offset"] = "0",
                ["limit"] = "10",
            });
            using var resp = await _http.PostAsync("https://music.163.com/api/search/get/", form)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return result;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var resultEl) ||
                !resultEl.TryGetProperty("songs", out var songsEl) ||
                songsEl.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var song in songsEl.EnumerateArray())
            {
                var name = song.TryGetProperty("name", out var n) ? n.GetString() : "";
                var artists = string.Join("/", song.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => x.TryGetProperty("name", out var an) ? an.GetString() : "")
                    : Array.Empty<string?>());
                var id = song.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
                if (id > 0 && !string.IsNullOrWhiteSpace(name))
                    result.Add(new SearchHit("netease", id, null, name, artists));
            }
        }
        catch { /* 网络失败不算致命错误 */ }
        return result;
    }

    /// <summary>按网易云歌曲 id 拉取歌词。</summary>
    private async Task<List<LrcLine>?> FetchNetEaseLyricAsync(long id)
    {
        try
        {
            using var resp = await _http.GetAsync(
                $"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lrc", out var lrcEl) ||
                !lrcEl.TryGetProperty("lyric", out var textEl))
                return null;

            return LrcParser.Parse(textEl.GetString());
        }
        catch { return null; }
    }

    // ---------- QQ 音乐 ----------

    /// <summary>搜索 QQ 音乐，返回候选列表。QQ 侧需要带 Referer 才返回 JSON。</summary>
    private async Task<List<SearchHit>> SearchQqAsync(string title, string artist)
    {
        var result = new List<SearchHit>();
        try
        {
            var query = string.IsNullOrWhiteSpace(artist)
                ? title
                : $"{title} {artist}";
            var url = $"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={Uri.EscapeDataString(query.Trim())}&format=json&n=10";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return result;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("song", out var songEl) ||
                !songEl.TryGetProperty("list", out var listEl) ||
                listEl.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var song in listEl.EnumerateArray())
            {
                var name = song.TryGetProperty("songname", out var n) ? n.GetString() : "";
                var singers = string.Join("/", song.TryGetProperty("singer", out var sg) && sg.ValueKind == JsonValueKind.Array
                    ? sg.EnumerateArray().Select(x => x.TryGetProperty("name", out var sn) ? sn.GetString() : "")
                    : Array.Empty<string?>());
                var mid = song.TryGetProperty("songmid", out var midEl) ? midEl.GetString() : null;
                if (!string.IsNullOrEmpty(mid) && !string.IsNullOrWhiteSpace(name))
                    result.Add(new SearchHit("qq", 0, mid, name, singers));
            }
        }
        catch { /* 网络失败不算致命错误 */ }
        return result;
    }

    /// <summary>按 songmid 拉取 QQ 音乐歌词（返回的是 base64 编码的 LRC）。</summary>
    private async Task<List<LrcLine>?> FetchQqLyricAsync(string songMid)
    {
        try
        {
            var url = $"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={songMid}&g_tk=5381&format=json";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://y.qq.com/");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lyric", out var b64El)) return null;

            var b64 = b64El.GetString();
            if (string.IsNullOrEmpty(b64)) return null;
            var lrcText = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return LrcParser.Parse(lrcText);
        }
        catch { return null; }
    }

    // ---------- 清洗 ----------

    /// <summary>去掉署名行、开头「歌名 - 歌手」标题行以及乱码字符。</summary>
    private static List<LrcLine> Clean(List<LrcLine>? lines)
    {
        var result = new List<LrcLine>();
        if (lines is null) return result;

        foreach (var line in lines)
        {
            var text = Sanitize(line.Text);
            if (text.Length == 0) continue;
            if (IsCreditLine(text)) continue;
            // QQ 歌词首行常是「歌名 - 歌手」，属于标题不是歌词，去掉
            if (result.Count == 0 && text.Contains(" - ")) continue;
            result.Add(new LrcLine(line.TimeMs, text));
        }
        return result;
    }

    /// <summary>清理替换字符（乱码标志）和控制字符。</summary>
    private static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\uFFFD') continue;
            if (char.IsControl(ch)) continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>判断是否是「作词/作曲/编曲…」这类署名行（限制长度以免误伤歌词）。</summary>
    private static bool IsCreditLine(string text)
    {
        if (text.Length > 30) return false;
        foreach (var marker in CreditMarkers)
            if (text.Contains(marker)) return true;
        return false;
    }

    // ---------- 缓存 ----------

    /// <summary>缓存键：歌名+歌手的哈希。前缀 v2 用于让旧版本写的缓存失效。</summary>
    private static string CacheKey(string title, string artist)
    {
        var raw = $"v2|{title.Trim().ToLowerInvariant()}|{artist.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..32];
    }

    /// <summary>读取缓存歌词，不存在或解析为空则返回 null。</summary>
    private List<LrcLine>? LoadCache(string key)
    {
        try
        {
            var path = Path.Combine(_cacheDir, key + ".lrc");
            if (!File.Exists(path)) return null;
            var parsed = LrcParser.Parse(File.ReadAllText(path));
            return parsed.Count > 0 ? parsed : null;
        }
        catch { return null; }
    }

    /// <summary>把歌词写回缓存（写成标准 LRC 文本，UTF-8 无 BOM）。</summary>
    private void SaveCache(string key, List<LrcLine> lines)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                var t = TimeSpan.FromMilliseconds(l.TimeMs);
                sb.Append('[').Append($"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}")
                  .Append(']').AppendLine(l.Text);
            }
            File.WriteAllText(Path.Combine(_cacheDir, key + ".lrc"), sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* 缓存写入失败不影响播放 */ }
    }
}
