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
            foreach (var page in DefaultPages()) Items.Add(page);
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

    private static void WriteTemplate()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ============================================================");
        builder.AppendLine("# MateMic 使用指南");
        builder.AppendLine("#");
        builder.AppendLine("# 「使用指南」按钮弹出的内容就是这里写的，按页显示。");
        builder.AppendLine("#   · [页标题] 独占一行 ⇒ 开始新的一页，标题会显示在对话框上。");
        builder.AppendLine("#   · 想加页就在任意位置再写一个 [页标题]，顺序按文件里的先后。");
        builder.AppendLine("#   · 想删页就把那一节整段删掉。");
        builder.AppendLine("#   · 正文可以写多行，回车换行即可；以 # 开头的行是注释，不会显示。");
        builder.AppendLine("#   · 改完保存（UTF-8），重新打开 MateMic 即生效，不用重新编译或安装。");
        builder.AppendLine("#   · 页数不限；内容太长时对话框里会自动出现滚动条。");
        builder.AppendLine("# ============================================================");
        builder.AppendLine();

        foreach (var page in DefaultPages())
        {
            builder.AppendLine("[" + page.Title + "]");
            builder.AppendLine(page.Body);
            builder.AppendLine();
        }

        File.WriteAllText(FilePath, builder.ToString(), new UTF8Encoding(true));
    }

    /// <summary>
    /// 内置的初始三页。文件不存在时按它生成模板 ——
    /// 写的是"骨架 + 真正有用的内容"，作者照着改就行，不必从空白开始。
    /// </summary>
    private static IEnumerable<Page> DefaultPages() => new[]
    {
        new Page("快速开始",
            """
            1. 装好 MateMic 并打开。
            2. 在顶部三个下拉框里选好「输入 / 输出 / 监听」（见下一页）。
            3. 打开工具栏最左边的「音频处理」总开关 —— 这是麦克风出声的总闸，
               它关着时麦克风是静音的（播放器仍能出声）。
            4. 需要哪个模块就单独打开那个开关，再点右侧箭头展开细调。

            顶部工具栏右侧还有「使用指南」与「关于」两个按钮，
            界面上任何地方不清楚都可以回到这一页看看。
            """),

        new Page("设备怎么选",
            """
            三个下拉框要选三个**不同**的设备，这是最容易搞混的地方：

            · 输入：实际在用的物理麦克风。
              ⚠ 不要选 MIXLINE Stream —— 那是给游戏/语音软件用的虚拟设备；
              MateMic 需要拿到真实麦克风的声音。

            · 输出：处理后的声音送到哪里。配合 MIXLINE 用时选「扬声器 (MIXLINE)」。

            · 监听：你实际在用的物理扬声器或耳机，用来听监听内容。
              要和「输出」分开选，否则监听会绕回虚拟设备、听不到真实效果。

            另外在 Windows 声音设置里，把默认录音设备设为 MIXLINE Stream，
            这样游戏和语音软件才能拿到处理后的声音。
            """),

        new Page("MIXLINE 接法",
            """
            MIXLINE 配合设置步骤

            1. 安装并打开 MIXLINE。
            2. 在 MIXLINE 中新建一个输入通道，选择 MateMic 的主输出设备
               （即「扬声器 (MIXLINE)」）作为输入源。
            3. 将该通道路由到 MIXLINE Stream（虚拟麦克风）。
            4. 在 Windows 声音设置中，把默认录音设备设为 MIXLINE Stream。
            5. 回到 MateMic，把「输出」选择为 MIXLINE 的虚拟播放设备。
            6. 在语音软件（Discord / 微信 / QQ / 游戏语音）中把麦克风选为 MIXLINE Stream。

            提示：MateMic 自身不创建虚拟声卡，必须配合 MIXLINE 使用。
            """),
    };
}
