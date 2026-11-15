namespace MateMic.Core;

/// <summary>
/// 检查更新的自检（<c>MateMic.exe --updatecheck</c>）。
///
/// 它做四件事，把"能不能用"和"安不安全"一次性验完：
///   1. 真正请求一次 version.json（含 302 重定向），打印最终地址与原始内容；
///   2. 用生产代码的 <see cref="UpdateChecker.Parse"/> 解析，确认字段齐全；
///   3. 校验下载链接是否在 <c>allowedHosts</c> 白名单内（安全边界）；
///   4. 跑一组版本比较用例 —— 重点覆盖 `0.4.10 > 0.4.9` 这种"字符串比会判错"的情形。
///
/// 用法：
///   MateMic.exe --updatecheck            用当前程序版本比较
///   MateMic.exe --updatecheck --local 0.4.2
/// </summary>
internal static class UpdateCheckSelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.WriteLine("==== 检查更新自检 ====");

        var local = "0.0.0";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--local") { local = args[i + 1]; break; }
        }
        Console.WriteLine($"本地版本（比较基准）= {local}");

        // ---- 1) 请求 ----
        Console.WriteLine("\n-- 1) 请求 version.json --");
        var info = await UpdateChecker.FetchAsync().ConfigureAwait(false);
        if (info is null)
        {
            Console.WriteLine("✗ 拉取或解析失败（网络不可用 / 返回非 200 / 内容不合规 / 下载链接不在白名单）");
            Console.WriteLine("  提示：软件里遇到这种情况会**静默忽略**，不影响正常使用。");
            PrintVersionCases(local);
            return 1;
        }

        Console.WriteLine("✓ 拉取并解析成功");
        Console.WriteLine($"  版本号   ：{info.Version}");
        Console.WriteLine($"  更新时间 ：{info.PublishedAt}");
        Console.WriteLine($"  下载链接 ：{info.DownloadUrl}");
        Console.WriteLine($"  提取码   ：{info.ExtractCode}");
        Console.WriteLine($"  白名单   ：{string.Join(", ", info.AllowedHosts)}");
        Console.WriteLine("  更新内容 ：");
        foreach (var line in info.Notes.Replace("\r\n", "\n").Split('\n'))
            Console.WriteLine("    " + line);

        // ---- 2) 安全校验 ----
        Console.WriteLine("\n-- 2) 下载链接安全校验 --");
        var allowed = UpdateChecker.IsDownloadUrlAllowed(info.DownloadUrl, info.AllowedHosts);
        Console.WriteLine(allowed
            ? "  ✓ 链接为 https 且主机在白名单内，允许打开浏览器"
            : "  ✗ 未通过校验（软件里会拒绝打开浏览器并提示用户手动访问）");

        var negativeCases = new (string Url, string[] Hosts, string Why)[]
        {
            ("http://wwbxe.lanzoue.com/x", new[] { "wwbxe.lanzoue.com" }, "非 https"),
            ("https://evil.example.com/x", new[] { "wwbxe.lanzoue.com" }, "主机不在白名单"),
            ("https://wwbxe.lanzoue.com/x", Array.Empty<string>(), "白名单为空"),
        };
        var badCount = 0;
        foreach (var (url, hosts, why) in negativeCases)
        {
            var ok = UpdateChecker.IsDownloadUrlAllowed(url, hosts);
            if (ok) badCount++;
            Console.WriteLine($"  {(ok ? "✗ 误放行" : "✓ 已拒绝")}：{why}（{url}）");
        }

        // ---- 3) 版本比较 ----
        Console.WriteLine("\n-- 3) 与本地版本比较 --");
        var newer = UpdateChecker.IsNewer(info.Version, local);
        Console.WriteLine(newer
            ? $"  ✓ 远端 {info.Version} 比本地 {local} **新** ⇒ 软件会显示「新版本」按钮"
            : $"  ✓ 远端 {info.Version} 不比本地 {local} 新 ⇒ 不提示");

        PrintVersionCases(local);

        var failures = (allowed ? 0 : 1) + badCount;
        Console.WriteLine(failures == 0
            ? "\n==== 全部通过 ===="
            : $"\n==== 有 {failures} 项未通过 ====");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>版本比较用例：覆盖"字符串比会出错"的边界。</summary>
    private static void PrintVersionCases(string local)
    {
        Console.WriteLine("\n-- 版本比较用例（逐段数字）--");
        var cases = new (string Remote, string Base, bool Expect)[]
        {
            ("0.4.3", "0.4.2", true),
            ("0.4.2", "0.4.2", false),
            ("0.4.1", "0.4.2", false),
            ("0.4.10", "0.4.9", true),   // 字符串比会判错：'1' < '9'
            ("0.10.0", "0.9.9", true),   // 字符串比会判错
            ("1.0", "1.0.0", false),     // 缺的段按 0
            ("1.0.1", "1.0", true),
            ("v0.5.0", "0.4.9", true),   // 允许 v 前缀
            ("0.5.0-beta", "0.4.9", true), // 预发布后缀按主版本比
            ("abc", "0.4.2", false),     // 非法版本不提示
        };

        var failed = 0;
        foreach (var (remote, @base, expect) in cases)
        {
            var got = UpdateChecker.IsNewer(remote, @base);
            var ok = got == expect;
            if (!ok) failed++;
            Console.WriteLine($"  {(ok ? "✓" : "✗")} IsNewer({remote}, {@base}) = {got}（期望 {expect}）");
        }
        if (failed > 0) Console.WriteLine($"  ⚠ 有 {failed} 个用例未通过");
    }
}
