using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MateMic.Ai;
using MateMic.Core;

namespace MateMic;

/// <summary>
/// 「添加 AI 变声」的对话框内容（由 <see cref="Ui.DialogHost.ShowCustom"/> 承载）。
///
/// 作用是把"装组件"这件事变成三条都走得通的路：
///   1. 点「打开组件文件夹」，把下载好的文件放进去；
///   2. 直接把文件**拖进来**（按文件名自动归到 runtime / engine / voices / index）；
///   3. 放好后点「重新检测」看状态。
/// 组件齐全后，主界面的处理链里就会出现可用的 AI 变声卡片。
/// </summary>
public partial class AiComponentPanel : UserControl
{
    /// <summary>
    /// 原生运行时必需的文件。判定逻辑与 <see cref="AiComponent"/> 一致，
    /// 这里只是为了在拖放时按名字决定"该放进哪个子目录"。
    /// </summary>
    private static readonly string[] RuntimePrefixes =
    {
        "onnxruntime", "cublas", "cudnn", "nvrtc", "nvjitlink",
    };

    public AiComponentPanel()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var status = AiComponent.Inspect();

        RuntimeStatusText.Text = status.HasRuntime ? "✓ 就绪" : "✗ 缺少文件";
        EngineStatusText.Text = status.HasEngine ? "✓ 就绪（contentvec + rmvpe）" : "✗ 缺少内容编码器或音高提取模型";
        VoiceStatusText.Text = status.VoiceCount > 0 ? $"✓ {status.VoiceCount} 个" : "✗ 还没有音色模型";
        IndexStatusText.Text = status.IndexCount > 0
            ? $"{status.IndexCount} 个（可选，用于提升音色相似度）"
            : "无（可选）";

        ComponentDirText.Text = $"组件目录：{ConfigStore.AiComponentDirectory}";
        DropHintText.Text = status.State == AiComponentState.Ready
            ? "组件已就绪，可以关闭本窗口"
            : "把组件文件拖到这里";
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.AiComponentDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigStore.AiComponentDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DropHintText.Text = "打开文件夹失败：" + ex.Message;
        }
    }

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 拖进来的文件按**文件名**归位——用户不需要知道哪个文件该放哪个子目录。
    /// 认不出的文件一律放进 engine\（让用户自己决定，也好过丢弃）。
    /// </summary>
    private void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

        var placed = new Dictionary<string, int>();
        var failed = 0;
        foreach (var path in dropped)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var name = Path.GetFileName(path);
                var target = DirectoryFor(name);
                Directory.CreateDirectory(target);
                File.Copy(path, Path.Combine(target, name), overwrite: true);
                var folder = Path.GetFileName(target);
                placed[folder] = placed.GetValueOrDefault(folder) + 1;
            }
            catch { failed++; }
        }

        Refresh();
        var summary = placed.Count > 0
            ? string.Join("、", placed.Select(kv => $"{kv.Key} {kv.Value} 个"))
            : "没有可用的文件";
        DropHintText.Text = failed == 0
            ? $"已接收：{summary}"
            : $"已接收：{summary}；{failed} 个失败（可能被占用）";
        if (AiComponent.Inspect().State == AiComponentState.Ready)
            DropHintText.Text += " —— 组件已就绪，重启程序即可使用";
    }

    /// <summary>按文件名判断归属目录。</summary>
    private static string DirectoryFor(string fileName)
    {
        var lower = fileName.ToLowerInvariant();

        if (lower.EndsWith(".index")) return ConfigStore.AiIndexDirectory;
        if (RuntimePrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
            return ConfigStore.AiRuntimeDirectory;
        if (lower is "contentvec.onnx" or "rmvpe.onnx" or "f0-crepe-tiny.onnx")
            return ConfigStore.AiEngineDirectory;
        if (lower.EndsWith(".onnx")) return ConfigStore.AiVoicesDirectory;

        return ConfigStore.AiEngineDirectory;
    }
}
