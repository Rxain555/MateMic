using System.IO;
using System.Text;

namespace MateMic.Core;

/// <summary>
/// 使用指南的分页内容。
///
/// **只有使用指南走外部文件**：作者要自己写说明，而且要能任意增删页，
/// 所以它单独放在数据目录的「使用指南.txt」里，用 <c>[页标题]</c> 分节 ——
/// 一节就是一页，顺序按文件里的先后。
///
/// 为什么不像原来那样把所有界面文案都放进一个文件：
/// 2026-10-06 作者判断"大多数控件没必要显示悬浮说明"，
/// 于是**去掉了全部悬浮说明**，说明集中写进这里。
/// 程序里其余几处长文本（关于 / 风险说明 / MIXLINE 接法）是功能性对话框，
/// 不是"使用说明"，因此仍留在代码里。
/// </summary>
public static class GuideCatalog
{
    public const string FileName = "使用指南.txt";

    public static string FilePath => Path.Combine(ConfigStore.Root, FileName);

    /// <summary>一页：标题（同时用作对话框里的页码标签）与正文。</summary>
    public readonly record struct Page(string Title, string Body);

    private static readonly List<Page> Items = new();

    /// <summary>当前页列表。文件缺失或解析失败时至少有一页（内置默认内容）。</summary>
    public static IReadOnlyList<Page> Pages => Items;

    /// <summary>启动时读一次。必须在窗口构造之前调用。</summary>
    public static void Load()
    {
        Items.Clear();

        try
        {
            if (!File.Exists(FilePath))
            {
                WriteTemplate();
                Log.Info("使用指南文件不存在，已按内置默认内容生成模板：" + FilePath);
            }

            foreach (var (title, body) in Parse(File.ReadAllLines(FilePath, Encoding.UTF8)))
            {
                if (string.IsNullOrWhiteSpace(title)) continue;
                // 正文留空 = 这一页暂时不写东西，仍然保留（作者可能先搭好目录再填）
                Items.Add(new Page(title, body));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("使用指南读取失败，改用内置默认内容：" + ex.Message);
        }

        if (Items.Count == 0)
        {
            // 外部文件读不出来（或整篇都被注释掉）时，退回程序集里内置的那份
            foreach (var (title, body) in Parse(ToLines(DefaultFileText())))
                if (!string.IsNullOrWhiteSpace(title)) Items.Add(new Page(title, body));

            Log.Warn("使用指南里没有解析到任何一页，已改用内置默认内容。");
        }

        Log.Info($"使用指南已就绪：{Items.Count} 页，来自 {FilePath}");
    }

    // ================================================================ 解析 / 模板

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

    /// <summary>把一段文本按行拆开（统一成 \n 再拆，兼容 CRLF）。</summary>
    private static string[] ToLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static void WriteTemplate()
        => File.WriteAllText(FilePath, DefaultFileText(), new UTF8Encoding(true));

    /// <summary>内置资源名（见 <c>MateMic.csproj</c> 里的 LogicalName）。</summary>
    private const string DefaultResourceName = "MateMic.Guide.Default.txt";

    /// <summary>
    /// 内置的默认指南 —— **就是作者自己写的那一份**，放在仓库的
    /// <c>MateMic\Assets\使用指南.txt</c>，编译时嵌进程序集。
    ///
    /// ⚠ 为什么不做成"复制到输出目录的文件"：那样它会跟着 `data\` 一起被改掉/删掉，
    /// 而且覆盖安装时行为不好界定。嵌进程序集则"总是有一份可用的初始内容"。
    /// 也不硬编码在 C# 里 —— 作者要经常改这份说明，改 txt、重新编译打包即可。
    ///
    /// 首次运行（`data\使用指南.txt` 不存在）时，原文会被写到那里；
    /// 此后用户改的是自己那份，升级覆盖安装不会冲掉。
    /// </summary>
    private static string DefaultFileText()
    {
        try
        {
            using var stream = typeof(GuideCatalog).Assembly
                .GetManifestResourceStream(DefaultResourceName);

            if (stream == null)
            {
                Log.Warn("内置使用指南资源不存在：" + DefaultResourceName);
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
        可以直接编辑 data\使用指南.txt，写完保存、重开程序即生效。
        """;
}
