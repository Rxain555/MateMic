using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using MicMate.Core;

namespace MicMate.Denoise;

/// <summary>
/// Python 运行时检测与自动安装（项目书 3.5.1）：
/// 1. 优先使用内嵌运行时 %LocalAppData%\MicMate/runtime/
/// 2. 其次检测系统已安装的 Python 3.10+
/// 3. 都没有时，由 UI 询问用户，确认后自动下载安装到内嵌目录（不影响系统已有 Python）
/// </summary>
public static class PythonRuntime
{
    /// <summary>与内嵌运行时直接对应的预编译 CPython（NuGet 包，解压即用）。</summary>
    private const string EmbeddedNuGetPackage = "python";
    private const string EmbeddedNuGetVersion = "3.12.10";
    private const string EmbeddedNuGetUrl =
        "https://nuget.azure.cn/v3-flatcontainer/python/3.12.10/python.3.12.10.nupkg";

    /// <summary>官方安装程序（需要完整运行时的场景，静默安装到内嵌目录）。</summary>
    private const string OfficialInstallerUrl =
        "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe";

    public sealed record PythonInfo(string Executable, string Display, bool IsEmbedded, Version Version);

    public static string RuntimeDirectory => ConfigStore.RuntimeDirectory;

    public static PythonInfo? Find()
    {
        // 1) 内嵌运行时
        var embedded = FindEmbedded();
        if (embedded != null) return embedded;

        // 2) 系统 PATH 中的 python
        foreach (var candidate in EnumerateSystemCandidates())
        {
            var info = Probe(candidate);
            if (info != null) return info;
        }

        return null;
    }

    public static PythonInfo? FindEmbedded()
    {
        try
        {
            if (!Directory.Exists(RuntimeDirectory)) return null;
            var exe = Directory.EnumerateFiles(RuntimeDirectory, "python.exe", SearchOption.AllDirectories)
                .OrderBy(p => p.Length)
                .FirstOrDefault();
            return exe == null ? null : Probe(exe);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateSystemCandidates()
    {
        yield return "python.exe";
        yield return "python3.exe";

        var localApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
        if (Directory.Exists(localApps))
        {
            foreach (var dir in Directory.EnumerateDirectories(localApps).OrderByDescending(d => d))
                yield return Path.Combine(dir, "python.exe");
        }
    }

    /// <summary>执行 python --version 并判断版本是否满足 3.10+。</summary>
    public static PythonInfo? Probe(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(6000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }

                IsMicrosoftStoreStub(executable);
                return null;
            }

            if (process.ExitCode != 0)
            {
                IsMicrosoftStoreStub(executable);
                return null;
            }

            var match = Regex.Match(output, @"Python\s+(\d+)\.(\d+)\.(\d+)");
            if (!match.Success) return null;

            var version = new Version(
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value));

            if (version < new Version(3, 10)) return null;

            var display = $"Python {version} ({executable})";
            return new PythonInfo(executable, display, false, version);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Windows 上 %LocalAppData%\Microsoft\WindowsApps\python.exe 可能只是应用商店的应用执行别名，
    /// 未安装真实 Python 时运行它会静默失败（退出码 9009）。这里给出明确提示，避免用户困惑。
    /// </summary>
    public static bool IsMicrosoftStoreStub(string executable)
    {
        if (!executable.Contains(@"Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase)) return false;
        Log.Warn("检测到的是 Microsoft Store 的 Python 应用执行别名（尚未安装真实 Python）。" +
                 "请在“应用商店”安装 Python，或使用本程序的自动安装功能。");
        return true;
    }

    /// <summary>解析可用运行时；找不到时调用 <paramref name="askInstall"/> 询问用户。</summary>
    public static async Task<PythonInfo?> ResolveAsync(
        Action<string, double> report,
        CancellationToken token,
        Func<Task<bool>>? askInstall = null)
    {
        var existing = Find();
        if (existing != null)
        {
            Log.Info("使用 Python 运行时：" + existing.Display);
            return existing;
        }

        if (askInstall == null)
        {
            Log.Warn("未检测到 Python 运行时，且没有安装确认回调。");
            return null;
        }

        var confirmed = await askInstall();
        if (!confirmed) return null;

        return await InstallAsync(report, token);
    }

    /// <summary>
    /// 下载并解压内嵌 CPython 到 %LocalAppData%\MicMate/runtime/。
    /// 使用 NuGet 上预编译的官方 CPython 包（zip 内容原样打包，解压即得 python.exe）。
    /// </summary>
    public static async Task<PythonInfo?> InstallAsync(Action<string, double> report, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            var nupkg = Path.Combine(Path.GetTempPath(), $"python.{EmbeddedNuGetVersion}.nupkg");

            report("正在下载 Python 运行时…", 0.05);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var response = await http.GetAsync(EmbeddedNuGetUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = File.Create(nupkg);

                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                    copied += read;
                    if (total > 0) report("正在下载 Python 运行时…", 0.05 + copied / (double)total * 0.7);
                }
            }

            report("正在解压运行时…", 0.78);
            var extractDir = Path.Combine(RuntimeDirectory, "python-" + EmbeddedNuGetVersion);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);

            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(nupkg);
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    if (!entry.FullName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)) continue;

                    var relative = entry.FullName["tools/".Length..];
                    var destination = Path.GetFullPath(Path.Combine(extractDir, relative));
                    if (!destination.StartsWith(Path.GetFullPath(extractDir), StringComparison.OrdinalIgnoreCase)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, true);
                }
            }, token);

            try
            {
                File.Delete(nupkg);
            }
            catch
            {
            }

            report("正在验证运行时…", 0.9);
            var info = FindEmbedded();
            if (info == null)
            {
                Log.Error("内嵌运行时解压后仍无法启动 python.exe");
                return null;
            }

            report("正在安装训练依赖（numpy / onnx）…", 0.94);
            await InstallDependenciesAsync(info.Executable, report, token);
            report("Python 运行时已就绪", 1.0);
            return info;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Python 运行时自动安装失败", ex);
            return null;
        }
    }

    /// <summary>用 pip 安装训练依赖。失败时给出提示（不视为致命错误，训练脚本会再报告一次）。</summary>
    private static async Task InstallDependenciesAsync(string python, Action<string, double> report, CancellationToken token)
    {
        // 内嵌运行时默认不带 pip，先尝试 ensurepip
        await RunAsync(python, "-m ensurepip --default-pip", token);
        var result = await RunAsync(python, "-m pip install --no-warn-script-location numpy onnx", token);
        if (result != 0)
            Log.Warn("pip 安装 numpy/onnx 未成功，训练脚本将再次尝试。可手动执行：" +
                     $"\"{python}\" -m pip install numpy onnx");
        else
            report("训练依赖已安装", 0.99);
    }

    private static async Task<int> RunAsync(string executable, string arguments, CancellationToken token)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return -1;
            await process.WaitForExitAsync(token);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Warn($"执行 {executable} {arguments} 失败：{ex.Message}");
            return -1;
        }
    }

    /// <summary>官方安装程序的下载地址（供“手动安装”引导使用）。</summary>
    public static string ManualInstallerUrl => OfficialInstallerUrl;
}
