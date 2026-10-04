using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MateMic.Core;

/// <summary>
/// AI 变声（以及将来其它大模型）的**组件**：一个清单 + 若干数据段。
///
/// 为什么这样设计：
///   · 组件动辄上百 MB，塞进安装包会让安装包从 35 MB 涨到几百 MB；做成组件后按需安装；
///   · 分发渠道有单文件大小限制（例如蓝奏云免费账号 100 MB/文件），
///     所以组件允许**把一个文件切成多段**，安装时按序号拼回去；
///   · GitHub Release 那条路可以直接发整包（zip），程序两条路都吃。
///
/// 清单（manifest.json）记录每段的大小与 SHA-256，以及拼接后整文件的 SHA-256。
/// 逐段校验 + 整体校验都通过才落盘，避免写进半个坏模型。
/// </summary>
public sealed class VoiceComponentManifest
{
    /// <summary>组件标识（目录名），如 <c>rvc-engine</c>。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名，如「RVC 变声引擎」。</summary>
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>组件类型：<c>engine</c> = AI 变声引擎；<c>model</c> = 音色模型。</summary>
    public string Kind { get; set; } = "engine";

    /// <summary>引擎种类（引擎组件才有），如 <c>rvc</c>。</summary>
    public string? Engine { get; set; }

    /// <summary>落盘时相对 <see cref="ConfigStore.ModelsDirectory"/> 的子目录，如 <c>voice\engine</c>。</summary>
    public string TargetDirectory { get; set; } = string.Empty;

    /// <summary>拼接后的文件名，如 <c>contentvec.int8.onnx</c>。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>拼接后的总字节数。</summary>
    public long Size { get; set; }

    /// <summary>拼接后的 SHA-256（小写十六进制）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>数据段，按数组顺序拼接。</summary>
    public VoiceComponentPart[] Parts { get; set; } = Array.Empty<VoiceComponentPart>();
}

public sealed class VoiceComponentPart
{
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>组件安装的结果。</summary>
public sealed record VoiceComponentInstallResult(bool Ok, string Message, VoiceComponentManifest? Manifest)
{
    public static VoiceComponentInstallResult Fail(string message) => new(false, message, null);
}

/// <summary>
/// 组件安装器：从用户拖入的一组文件里识别组件、校验、拼接、落盘。
/// 纯文件操作，不碰音频链路，因此可以用 <c>--componentcheck</c> 单独验证。
/// </summary>
public static class VoiceComponentInstaller
{
    public const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>已安装的组件（扫描 <c>models\&lt;TargetDirectory&gt;\*\manifest.json</c>）。</summary>
    public static IReadOnlyList<VoiceComponentManifest> LoadInstalled(string componentsDirectory)
    {
        var list = new List<VoiceComponentManifest>();
        if (!Directory.Exists(componentsDirectory)) return list;

        foreach (var file in Directory.EnumerateFiles(componentsDirectory, ManifestFileName, SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<VoiceComponentManifest>(File.ReadAllText(file), JsonOptions);
                if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Id)) list.Add(manifest);
            }
            catch (Exception ex)
            {
                Log.Warn($"读取组件清单失败：{file}（{ex.Message}）");
            }
        }

        return list;
    }

    /// <summary>
    /// 从拖入的路径安装组件。
    /// </summary>
    /// <param name="paths">用户拖入的文件或文件夹。</param>
    /// <param name="componentsDirectory">组件根目录；默认 <see cref="ConfigStore.ComponentsDirectory"/>。</param>
    /// <param name="dryRun">true = 只校验与拼接，不写进模型目录（自检用）。</param>
    public static VoiceComponentInstallResult Install(
        IEnumerable<string> paths,
        string? componentsDirectory = null,
        bool dryRun = false)
    {
        componentsDirectory ??= ConfigStore.ComponentsDirectory;

        // 1) 把拖入的东西摊平成文件列表（文件夹只取顶层，避免误扫整个磁盘）
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                if (Directory.Exists(path))
                {
                    files.AddRange(Directory.EnumerateFiles(path));
                }
                else if (File.Exists(path))
                {
                    // 拖入单个 zip：先解到临时目录再按普通文件处理
                    if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        var temp = Path.Combine(Path.GetTempPath(), "matemic-component-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temp);
                        ZipFile.ExtractToDirectory(path, temp);
                        files.AddRange(Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories));
                    }
                    else
                    {
                        files.Add(path);
                    }
                }
            }
            catch (Exception ex)
            {
                return VoiceComponentInstallResult.Fail($"读取 {Path.GetFileName(path)} 失败：{ex.Message}");
            }
        }

        if (files.Count == 0) return VoiceComponentInstallResult.Fail("没有可识别的文件。");

        // 2) 找清单
        var manifestPath = files.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f), ManifestFileName, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(f).EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase));

        if (manifestPath == null)
        {
            return VoiceComponentInstallResult.Fail(
                $"没找到 {ManifestFileName}，这不是一个组件。（拖放音频文件会加进播放列表）");
        }

        VoiceComponentManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<VoiceComponentManifest>(File.ReadAllText(manifestPath), JsonOptions)
                       ?? throw new InvalidDataException("清单内容为空");
        }
        catch (Exception ex)
        {
            return VoiceComponentInstallResult.Fail($"清单解析失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(manifest.FileName) || manifest.Parts.Length == 0)
        {
            return VoiceComponentInstallResult.Fail("清单不完整：缺少 FileName 或 Parts。");
        }

        // 3) 逐段校验（文件名、大小、SHA-256）
        foreach (var part in manifest.Parts)
        {
            var partPath = files.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), part.Name, StringComparison.OrdinalIgnoreCase));
            if (partPath == null)
            {
                return VoiceComponentInstallResult.Fail($"缺少数据段：{part.Name}");
            }

            var info = new FileInfo(partPath);
            if (part.Size > 0 && info.Length != part.Size)
            {
                return VoiceComponentInstallResult.Fail(
                    $"数据段大小不符：{part.Name}（应为 {part.Size} 字节，实际 {info.Length} 字节），文件可能没下载完。");
            }

            if (!string.IsNullOrWhiteSpace(part.Sha256)
                && !string.Equals(Sha256OfFile(partPath), part.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return VoiceComponentInstallResult.Fail($"数据段校验失败：{part.Name}（内容与清单不一致）。");
            }
        }

        // 4) 按顺序拼接 + 同时算整体哈希
        var targetDir = Path.Combine(componentsDirectory, ResolveTargetDirectory(manifest), manifest.Id);
        Directory.CreateDirectory(targetDir);
        var assembled = Path.Combine(dryRun ? Path.GetTempPath() : targetDir, manifest.FileName);

        try
        {
            using (var output = File.Create(assembled))
            using (var sha = SHA256.Create())
            {
                foreach (var part in manifest.Parts)
                {
                    var partPath = files.First(f =>
                        string.Equals(Path.GetFileName(f), part.Name, StringComparison.OrdinalIgnoreCase));
                    using var input = File.OpenRead(partPath);
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

                if (manifest.Size > 0 && output.Length != manifest.Size)
                {
                    output.Close();
                    TryDelete(assembled);
                    return VoiceComponentInstallResult.Fail(
                        $"拼接后大小不符（应为 {manifest.Size} 字节，实际 {output.Length} 字节）。");
                }

                if (!string.IsNullOrWhiteSpace(manifest.Sha256)
                    && !string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    output.Close();
                    TryDelete(assembled);
                    return VoiceComponentInstallResult.Fail("拼接后整体校验失败（内容与清单不一致）。");
                }
            }

            // 5) 清单也放一份到落盘目录，便于下次扫描
            var manifestCopy = Path.Combine(targetDir, ManifestFileName);
            if (!string.Equals(manifestPath, manifestCopy, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(manifestPath, manifestCopy, overwrite: true);
            }

            var sizeMb = new FileInfo(assembled).Length / 1024.0 / 1024.0;
            var where = dryRun ? "（自检：未写入模型目录）" : targetDir;
            Log.Info($"组件安装成功：{manifest.Name} {manifest.Version} → {assembled}（{sizeMb:0.0} MB）");
            return new VoiceComponentInstallResult(
                true,
                $"已安装 {manifest.Name} {manifest.Version}（{manifest.Parts.Length} 段，{sizeMb:0.0} MB）\n{where}",
                manifest);
        }
        catch (Exception ex)
        {
            TryDelete(assembled);
            Log.Error("组件安装失败", ex);
            return VoiceComponentInstallResult.Fail($"安装失败：{ex.Message}");
        }
    }

    /// <summary>落盘子目录：清单没写就按类型给默认值（引擎 / 音色分开）。</summary>
    private static string ResolveTargetDirectory(VoiceComponentManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.TargetDirectory)) return manifest.TargetDirectory;

        return manifest.Kind.ToLowerInvariant() switch
        {
            "model" => Path.Combine("voice", "model"),
            _ => Path.Combine("voice", "engine"),
        };
    }
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清不掉就算了，临时文件不影响功能
        }
    }

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
