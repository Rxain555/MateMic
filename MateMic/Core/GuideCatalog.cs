using System.IO;
using System.Text;

namespace MateMic.Core;

/// <summary>
/// 使用指南的内容。
///
/// **完全内置在程序集里**：正文来自仓库的 `Assets\使用指南.txt`，编译时编成 EmbeddedResource。
/// 不从 `data\` 读、也不往 `data\` 写。
///
/// 为什么不做成外部文件（早先那一版是）：
/// 这份说明是**作者写给用户的**，不是给用户自己改的 —— 所以"每台机器各存一份、各改各的"
/// 没有意义，反而带来两个麻烦：
///   · 覆盖安装时到底该不该冲掉用户改过的那份？怎么选都别扭；
///   · `data\` 是用户数据目录，多塞一个不该由用户维护的文件只会让人困惑。
///
/// 内置之后语义很干净：**作者改那个 txt → 重新编译打包 → 所有人看到新的**，
/// 覆盖安装自然也就能更新。开发时想预览改动，重新编译一次即可。
/// </summary>
public static class GuideCatalog
{
    /// <summary>内置资源名（见 <c>MateMic.csproj</c> 里的 LogicalName）。</summary>
    private const string ResourceName = "MateMic.Guide.Default.txt";

    /// <summary>一页：标题（显示在对话框上的页码区）与正文。</summary>
    public readonly record struct Page(string Title, string Body);

    private static readonly List<Page> Items = new();

    /// <summary>当前页列表。必须在构造窗口之前 Load。</summary>
    public static IReadOnlyList<Page> Pages => Items;

    public static void Load()
    {
        Items.Clear();

        foreach (var (title, body) in Parse(ToLines(Text())))
        {
            if (string.IsNullOrWhiteSpace(title)) continue;
            // 正文留空 = 这一页暂时不写东西，仍然保留（作者可能先搭好目录再填）
            Items.Add(new Page(title, body));
        }

        if (Items.Count == 0) Log.Warn("内置使用指南里没有解析到任何一页，请检查 Assets\\使用指南.txt");
        else Log.Info($"使用指南已就绪：{Items.Count} 页（内置）");
    }

    // ================================================================ 读内置正文

    private static string Text()
    {
        try
        {
            using var stream = typeof(GuideCatalog).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                Log.Warn("内置使用指南资源不存在：" + ResourceName);
                return FallbackText;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Warn("读取内置使用指南失败：" + ex.Message);
            return FallbackText;
        }
    }

    /// <summary>连内置资源都读不到时的最后兜底（正常情况不会走到）。</summary>
    private const string FallbackText =
        """
        [简单开始]
        MateMic 的使用说明还没有内容。
        """;

    // ================================================================ 解析

    /// <summary>把一段文本按行拆开（统一成 \n 再拆，兼容 CRLF）。</summary>
    private static string[] ToLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// 解析：<c>[页标题]</c> 独占一行开始一页，下面的行都是正文，<c>#</c> 开头是注释。
    /// 首尾空行去掉、中间空行保留（正文里的分段有意义）。
    /// </summary>
    private static IEnumerable<(string Title, string Body)> Parse(string[] lines)
    {
        string? title = null;
        var buffer = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                if (title != null) yield return (title, Trim(buffer));
                title = line[1..^1].Trim();
                buffer.Clear();
                continue;
            }

            if (title != null) buffer.Add(line);
        }

        if (title != null) yield return (title, Trim(buffer));
    }

    private static string Trim(List<string> lines)
    {
        var start = 0;
        var end = lines.Count - 1;
        while (start <= end && string.IsNullOrWhiteSpace(lines[start])) start++;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end])) end--;
        return start > end ? string.Empty : string.Join(Environment.NewLine, lines.Skip(start).Take(end - start + 1));
    }
}
