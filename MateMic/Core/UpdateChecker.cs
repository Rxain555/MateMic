using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MateMic.Core;

/// <summary>版本信息（来自 Gitee 上的 version.json）。</summary>
public sealed record UpdateInfo(
    string Version,
    string PublishedAt,
    string Notes,
    string DownloadUrl,
    string ExtractCode,
    string[] AllowedHosts);

/// <summary>
/// 检查更新。
///
/// ## 数据来源
/// <c>https://gitee.com/Rxain/checkforupdates/raw/master/version.json</c>
///
/// ⚠ Gitee 的 raw 链接会 **302 重定向到 CDN**（<c>raw.giteeusercontent.com</c>）。
/// 这不是"链接坏了"：`HttpClient` 默认自动跟随重定向即可。
/// 之所以**只写 gitee.com 这一个地址、不写死 CDN 域名**，是因为 CDN 域名属于
/// 服务端的实现细节，哪天 Gitee 换 CDN，写死 CDN 的老版本软件就永远检查不到更新了；
/// 而官方入口的重定向会自动跟着变。
///
/// ## 安全设计（每一项都对应一个具体风险）
/// 1. **校验重定向后的最终主机名**：只认 gitee.com / raw.giteeusercontent.com。
///    只校验初始 URL 的话，中间被重定向到恶意域也发现不了（这是重定向带来的新攻击面）。
/// 2. **下载链接域名白名单**：version.json 里的 <c>allowedHosts</c> 与 <c>downloadUrl</c>
///    必须一致，否则拒绝打开浏览器 —— 否则一旦 version.json 被篡改，用户会因为
///    "信任这个软件"而点进钓鱼站。
/// 3. **强制 https**：下载链接与数据源都必须 https。
/// 4. **响应体积上限 64 KB**：防止异常/恶意的超大响应。
/// 5. **全程失败静默**：网络问题、格式错误、超时都只记一条 Debug 日志，
///    绝不影响软件启动与正常使用。
/// 6. **超时 5 秒**：避免卡住。
///
/// ## 版本比较
/// 逐段按数字比较（`0.4.10` > `0.4.9`），**不能按字符串比**，否则 0.4.10 会被判成更旧。
/// </summary>
public static class UpdateChecker
{
    /// <summary>数据源（只写官方入口，CDN 由重定向自动处理）。</summary>
    private const string FeedUrl = "https://gitee.com/Rxain/checkforupdates/raw/master/version.json";

    private const int TimeoutMs = 5000;
    private const int MaxBytes = 64 * 1024;

    /// <summary>允许的数据源主机（重定向之后也要落在这个集合里）。</summary>
    private static readonly string[] AllowedFeedHosts = { "gitee.com", "raw.giteeusercontent.com" };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // 必须允许自动重定向：Gitee raw → CDN 是 302。
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            // 不跟随到非 https 的地址
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };

        // 必须带浏览器 UA：Gitee CDN 会对"无 UA / 爬虫 UA"返回 403（实测踩到过）。
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return client;
    }

    /// <summary>
    /// 拉取并解析版本信息。任何失败都返回 null（不抛异常）。
    /// </summary>
    public static async Task<UpdateInfo?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            // **防缓存**（2026-11-15 用户实测：云端已把版本改回 0.4.2，程序仍报 0.4.3）。
            // Gitee 的 raw 走 CDN，改动后 CDN 节点可能还吐旧内容；再加上中间代理也可能缓存。
            // 三层一起上：
            //   ① URL 带一次性参数（让 CDN 视为不同资源，绕过按 URL 的缓存键）；
            //   ② Cache-Control: no-cache / no-store；
            //   ③ Pragma: no-cache（给老代理看）。
            var url = FeedUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store, max-age=0");
            request.Headers.TryAddWithoutValidation("Pragma", "no-cache");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            // ⚠ 校验**重定向之后**的真实地址，而不是初始 URL。
            var actual = response.RequestMessage?.RequestUri;
            if (actual is null ||
                actual.Scheme != Uri.UriSchemeHttps ||
                !AllowedFeedHosts.Any(h => string.Equals(actual.Host, h, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Debug($"[检查更新] 数据源最终地址不在白名单：{actual}");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.Debug($"[检查更新] 数据源返回 {(int)response.StatusCode}");
                return null;
            }

            // 体积上限：先看 Content-Length，再按实际读取截断
            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                Log.Debug("[检查更新] 数据源响应过大，已忽略");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var buffer = new char[MaxBytes];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0)
            {
                Log.Debug("[检查更新] 数据源内容为空");
                return null;
            }
            if (read >= MaxBytes)
            {
                Log.Debug("[检查更新] 数据源内容超过上限，已忽略");
                return null;
            }

            var json = new string(buffer, 0, read);
            return Parse(json);
        }
        catch (Exception ex)
        {
            // 网络不可用 / 超时 / 格式错误：一律静默
            Log.Debug("[检查更新] 请求失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>解析 version.json。字段缺失或下载链接不合规都返回 null。</summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = GetString(root, "version");
            var downloadUrl = GetString(root, "downloadUrl");
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                Log.Debug("[检查更新] version 或 downloadUrl 缺失");
                return null;
            }

            var allowed = GetStringArray(root, "allowedHosts");
            if (!IsDownloadUrlAllowed(downloadUrl, allowed))
            {
                // 这是安全边界：宁可放弃这次更新提示，也不把用户送到未知站点。
                Log.Warn($"[检查更新] 下载链接不在白名单内，已忽略：{downloadUrl}");
                return null;
            }

            return new UpdateInfo(
                version.Trim(),
                GetString(root, "publishedAt").Trim(),
                GetString(root, "notes"),
                downloadUrl.Trim(),
                GetString(root, "extractCode").Trim(),
                allowed);
        }
        catch (Exception ex)
        {
            Log.Debug("[检查更新] JSON 解析失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>下载链接必须是 https，且主机在白名单里（白名单为空则一律拒绝）。</summary>
    public static bool IsDownloadUrlAllowed(string url, string[] allowedHosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (allowedHosts.Length == 0) return false;

        return allowedHosts.Any(h =>
            !string.IsNullOrWhiteSpace(h) &&
            string.Equals(uri.Host, h.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 逐段数字比较：返回 true 表示 <paramref name="remote"/> 比 <paramref name="local"/> 新。
    /// 段数不同时缺的段按 0 处理（1.0 == 1.0.0）。
    /// 任一侧无法解析成数字 ⇒ 返回 false（不提示更新，避免误报）。
    /// </summary>
    public static bool IsNewer(string remote, string local)
    {
        var r = ParseVersion(remote);
        var l = ParseVersion(local);
        if (r is null || l is null) return false;

        var length = Math.Max(r.Length, l.Length);
        for (var i = 0; i < length; i++)
        {
            var rv = i < r.Length ? r[i] : 0;
            var lv = i < l.Length ? l[i] : 0;
            if (rv > lv) return true;
            if (rv < lv) return false;
        }
        return false;
    }

    /// <summary>把 "0.4.2" / "0.4.2.1" / "1.0.0-beta" 解析成数字数组；非法返回 null。</summary>
    private static int[]? ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 去掉可能的 v 前缀与预发布后缀（1.0.0-beta ⇒ 1.0.0）
        var cleaned = text.Trim();
        if (cleaned.StartsWith('v') || cleaned.StartsWith('V')) cleaned = cleaned[1..];
        var dash = cleaned.IndexOf('-');
        if (dash >= 0) cleaned = cleaned[..dash];

        var parts = cleaned.Split('.');
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var value) || value < 0) return null;
            result.Add(value);
        }
        return result.Count == 0 ? null : result.ToArray();
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static string[] GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value)) list.Add(value.Trim());
            }
        }
        return list.ToArray();
    }
}
