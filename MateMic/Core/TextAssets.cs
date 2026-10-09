using System.IO;
using System.Text;

namespace MateMic.Core;

/// <summary>
/// 内置的长文案资源：**作者可以直接编辑**的正文放在仓库 `Assets\` 下，编译时编成
/// EmbeddedResource（与使用指南同一套路，见 <see cref="GuideCatalog"/>）。
///
/// 为什么把这些从 C# 常量搬出来：改一句话就得动代码、还要在几千行里翻半天。
/// 搬成 txt 之后，改文案就是改一个文件的事。
///
/// ⚠ 与使用指南一样是**内置**的：改完必须**重新编译**才生效，
/// 不是"改完重启程序"。所以这些文件只服务于作者，不面向最终用户的 data\ 目录。
/// </summary>
public static class TextAssets
{
    private const string AboutResource = "MateMic.About.txt";
    private const string HoldKeyRiskResource = "MateMic.HoldKeyRisk.txt";

    private static string? _about;
    private static string? _holdKeyRisk;

    /// <summary>
    /// 「关于」对话框的声明正文。
    /// 开头那行「MateMic vX.Y.Z   MIT 许可」由调用方拼上（版本号要运行时读程序集）。
    /// </summary>
    public static string About => _about ??= Read(AboutResource, "（内置的「关于」文案缺失）");

    /// <summary>「同步按住键 · 风险说明」的正文（标题与按钮文案仍在代码里）。</summary>
    public static string HoldKeyRisk => _holdKeyRisk ??= Read(HoldKeyRiskResource, "（内置的风险说明文案缺失）");

    /// <summary>
    /// 启动时预读一遍并写日志。用途与 <see cref="GuideCatalog.Load"/> 相同：
    /// "我改了 txt 怎么没生效"这类问题，看一眼启动日志的字数就能判断，
    /// 不必去点开对话框确认。
    /// </summary>
    public static void Load()
        => Log.Info($"内置文案已就绪：「关于」{About.Length} 字 / 「风险说明」{HoldKeyRisk.Length} 字");

    /// <summary>
    /// 读一个内置资源：去掉 <c>#</c> 注释行、统一换行、裁掉首尾空白。
    /// 任何一步失败都只记日志并返回兜底文案 —— 这个类会在"点按钮"的路径上被调用，
    /// 绝不能因为资源缺失把对话框变成异常。
    /// </summary>
    private static string Read(string resourceName, string fallback)
    {
        try
        {
            using var stream = typeof(TextAssets).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Log.Warn($"内置文案资源不存在：{resourceName}（请检查 MateMic.csproj 的 EmbeddedResource）");
                return fallback;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd()
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith('#'))
                .Select(line => line.TrimEnd());

            var text = string.Join(Environment.NewLine, lines).Trim('\n', ' ', '\t');
            return text.Length > 0 ? text : fallback;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取内置文案失败（{resourceName}）：{ex.Message}");
            return fallback;
        }
    }
}
