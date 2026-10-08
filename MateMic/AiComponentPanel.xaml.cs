using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using MateMic.Ai;
using MateMic.Core;

namespace MateMic;

/// <summary>
/// 「添加 AI 变声」的对话框内容（由 <see cref="Ui.DialogHost.ShowCustom"/> 承载）。
///
/// **只负责组件本身**（通用组件 + 运算后端），不检测也不安装音色与索引 ——
/// 那两样由用户直接拖到主界面（2026-10-08 用户要求：界面太复杂，对话框只管组件）。
///
/// 装组件有三条路：
///   1. 把**组件包 .zip 拖进来**（自动解压并按目录合并，最省事）；
///   2. 把**解压后的文件夹**或零散文件拖进来；
///   3. 点拖放区选文件，或点「打开组件文件夹」自己放。
/// </summary>
public partial class AiComponentPanel : UserControl
{
    /// <summary>零散文件按名字判断归属时用到的前缀。</summary>
    private static readonly string[] RuntimePrefixes =
    {
        "onnxruntime", "cublas", "cudnn", "nvrtc", "nvjitlink", "cufft", "nvblas", "directml", "vcomp", "faiss", "libopenblas",
    };

    public AiComponentPanel()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var status = AiComponent.Inspect();
        var installed = AiComponent.InstalledProviders();

        CoreStatusText.Text = status.HasRuntime && status.HasEngine
            ? "✓ 已安装"
            : (status.HasRuntime ? "✗ 缺推理引擎模型" : "✗ 未安装");

        ProviderStatusText.Text = installed.Count > 0
            ? "✓ " + string.Join("、", installed.Select(AiComponent.ProviderDisplayName))
            : "✗ 未安装";

        ProviderDetailText.Text = installed.Count > 0
            ? "可选后端：CUDA（NVIDIA，最快）· DirectML（AMD/Intel/NVIDIA 通用，约 15MB）· CPU（约 5MB）"
            : "请安装至少一个运算组件：CUDA / DirectML / CPU";

        ComponentDirText.Text = $"组件目录：{ConfigStore.AiComponentDirectory}";

        DropHintText.Text = status.HasRuntime && status.HasEngine && installed.Count > 0
            ? "组件已就绪，可以关闭本窗口"
            : "把组件包拖到这里";
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

    private void OnDropZoneClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择组件包",
            Filter = "组件包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        Install(dialog.FileNames);
    }

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
        Install(dropped);
    }

    // ---------------------------------------------------------------- 安装

    /// <summary>
    /// 安装用户拖入/选中的东西。三种形式分别处理：
    ///   · .zip  → 解压后按内部目录结构合并（组件包就是这个形式）
    ///   · 目录  → 递归复制进去，按内部的 runtime / providers / engine 子目录归位
    ///   · 其它  → 当作零散文件，按文件名猜归属
    /// </summary>
    private void Install(IEnumerable<string> paths)
    {
        var placed = new Dictionary<string, int>();
        var failed = 0;
        var zipCount = 0;

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    InstallZip(path, placed);
                    zipCount++;
                }
                else if (Directory.Exists(path))
                {
                    InstallDirectory(path, placed);
                }
                else if (File.Exists(path))
                {
                    var target = DirectoryFor(Path.GetFileName(path));
                    Directory.CreateDirectory(target);
                    File.Copy(path, Path.Combine(target, Path.GetFileName(path)), overwrite: true);
                    Bump(placed, Path.GetFileName(target));
                }
            }
            catch
            {
                failed++;
            }
        }

        Refresh();

        var summary = placed.Count > 0
            ? string.Join("、", placed.Select(kv => $"{kv.Key} {kv.Value} 个"))
            : "没有可用的内容";
        var lead = zipCount > 0 ? $"已安装 {zipCount} 个组件包：" : "已接收：";
        DropHintText.Text = failed == 0 ? lead + summary : $"{lead}{summary}；{failed} 项失败（可能被占用）";

        var status = AiComponent.Inspect();
        if (status.HasRuntime && status.HasEngine && AiComponent.InstalledProviders().Count > 0)
            DropHintText.Text += " —— 组件已就绪，重启程序即可使用";
    }

    /// <summary>
    /// 解压组件包并按目录合并。实现已提到 <see cref="AiComponent.InstallFromZip"/>，
    /// 这样主界面直接拖入 zip 时走的是同一份逻辑。
    /// </summary>
    private static void InstallZip(string zipPath, Dictionary<string, int> placed)
    {
        var written = AiComponent.InstallFromZip(zipPath);
        if (written > 0) Bump(placed, "组件包");
    }

    /// <summary>
    /// 复制一个解压后的组件文件夹。若它内部有 runtime / providers / engine 子目录，
    /// 就按名字归位；否则把里面的文件按文件名逐个判断。
    /// </summary>
    private static void InstallDirectory(string dir, Dictionary<string, int> placed)
    {
        var root = ConfigStore.AiComponentDirectory;
        Directory.CreateDirectory(root);

        var known = new[] { "runtime", "providers", "engine" };
        var nested = known
            .Select(k => (Name: k, Path: System.IO.Path.Combine(dir, k)))
            .Where(x => Directory.Exists(x.Path))
            .ToList();

        if (nested.Count > 0)
        {
            foreach (var (name, source) in nested)
            {
                var dest = System.IO.Path.Combine(root, name);
                CopyTree(source, dest);
                Bump(placed, name);
            }
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var target = DirectoryFor(System.IO.Path.GetFileName(file));
            Directory.CreateDirectory(target);
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)), overwrite: true);
            Bump(placed, System.IO.Path.GetFileName(target));
        }
    }

    private static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(source))
            CopyTree(sub, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(sub)));
    }

    private static void Bump(Dictionary<string, int> placed, string key)
        => placed[key] = placed.GetValueOrDefault(key) + 1;

    /// <summary>零散文件按文件名判断归属目录。</summary>
    private static string DirectoryFor(string fileName)
    {
        var lower = fileName.ToLowerInvariant();

        if (lower.EndsWith(".index")) return ConfigStore.AiIndexDirectory;
        if (lower is "contentvec.onnx" or "rmvpe.onnx" or "f0-crepe-tiny.onnx")
            return ConfigStore.AiEngineDirectory;
        if (lower.EndsWith(".onnx")) return ConfigStore.AiVoicesDirectory;

        // onnxruntime / cudnn / cublas / DirectML / faiss 这些都属于"运算后端"，
        // 但零散拖入时无法判断是哪个后端 —— 统一放进 cuda\（最常见的场景），
        // 用户要装 DirectML/CPU 应当用组件包。
        if (RuntimePrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
            return ConfigStore.AiProviderDirectory("cuda");

        return ConfigStore.AiEngineDirectory;
    }
}
