using System.Text.Json;
using System.Text.Json.Serialization;

namespace MateMic.Core;

/// <summary>
/// 应用数据目录。默认就是**程序安装目录**（exe 同级的 MateMic 数据目录），
/// 即绿色软件/便携模式：配置、模型、录音、日志、内嵌运行时全部就地存放，
/// 不往 C 盘写任何东西。只有当安装目录不可写（例如装在 Program Files）时才回退到用户目录。
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string? _overrideRoot;
    private static readonly Lazy<string> RootLazy = new(ResolveRoot);

    public static string Root => _overrideRoot ?? RootLazy.Value;

    public static string ModelsDirectory => Path.Combine(Root, "models");
    public static string RecordingsDirectory => Path.Combine(Root, "recordings");
    public static string RuntimeDirectory => Path.Combine(Root, "runtime");

    /// <summary>
    /// 组件目录（AI 变声引擎、音色模型等按需安装的大文件）。
    /// **刻意与 <see cref="ModelsDirectory"/>（降噪模型）分开**：两者性质不同，
    /// 分开后卸载/清理只需删一个子树，也不会让降噪模型的扫描去碰变声的大文件。
    /// </summary>
    public static string ComponentsDirectory => Path.Combine(Root, "components");

    /// <summary>
    /// AI 变声组件的根目录（<c>data\components\ai</c>）。
    ///
    /// 目录结构（由组件包原样展开即可，程序不自行下载）：
    /// <code>
    ///   runtime\   GPU 版 onnxruntime.dll + onnxruntime_providers_cuda.dll + CUDA/cuDNN 运行时
    ///   engine\    contentvec.onnx（内容编码器）、rmvpe.onnx（音高提取）
    ///   voices\    音色模型（合成器 .onnx）
    ///   index\     音色索引（FAISS .index，可选）
    /// </code>
    /// 放在 <see cref="ComponentsDirectory"/> 之下而不是单独开一棵树，
    /// 是为了"卸载 AI 变声"只需删这一个子树，且降噪的模型扫描不会碰到这些大文件。
    /// </summary>
    public static string AiComponentDirectory => Path.Combine(ComponentsDirectory, "ai");
    public static string AiRuntimeDirectory => Path.Combine(AiComponentDirectory, "runtime");
    public static string AiEngineDirectory => Path.Combine(AiComponentDirectory, "engine");
    public static string AiVoicesDirectory => Path.Combine(AiComponentDirectory, "voices");
    public static string AiIndexDirectory => Path.Combine(AiComponentDirectory, "index");

    public static string LogsDirectory => Path.Combine(Root, "logs");
    public static string ConfigPath => Path.Combine(Root, "config.json");

    /// <summary>
    /// **随程序内置**的模型目录：exe 同级的 models\。
    /// 用于分发时把 .onnx 直接打进安装包/压缩包——用户装完即可在「AI 降噪 → 模型」里选到，
    /// 不必自己往 data\ 里拷。
    /// 与 <see cref="ModelsDirectory"/>（用户自己的模型目录）分开，是因为 data\ 属于用户数据
    /// （清理解压目录、重装程序时会被删），而内置模型应该跟着程序走。
    /// </summary>
    public static string BundledModelsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>
    /// 覆盖数据目录：命令行 --appdata &lt;目录&gt;。必须在访问上述任一属性之前调用。
    /// </summary>
    public static void UseRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        _overrideRoot = Path.GetFullPath(directory);
    }

    /// <summary>
    /// 数据根目录选择顺序（**默认就地存放，不写 C 盘**）：
    ///   1. 程序安装目录\data        —— 默认。exe 在哪，数据就在哪
    ///   2. 程序安装目录（本身可写时直接放这里，兼容旧的便携用法）
    ///   3. %LocalAppData%\MateMic  —— 仅当安装目录只读（如装在 Program Files）
    ///   4. %TEMP%\MateMic          —— 最后兜底，保证程序始终能启动
    /// </summary>
    private static string ResolveRoot()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            candidates.Add(Path.Combine(baseDirectory, "data"));
            candidates.Add(Path.Combine(baseDirectory, "MateMic"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "MateMic"));

        candidates.Add(Path.Combine(Path.GetTempPath(), "MateMic"));

        foreach (var candidate in candidates)
        {
            if (IsWritable(candidate)) return candidate;
        }

        return candidates[^1];
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把早期版本写在 %LocalAppData%\MateMic 的数据搬到程序目录。
    /// 只搬一次（目标目录已有 config.json 就跳过），只搬配置、模型与录音。
    /// </summary>
    private static void MigrateFromLocalAppDataIfNeeded()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData)) return;

            var legacy = Path.Combine(localAppData, "MateMic");
            if (!Directory.Exists(legacy)) return;
            if (Path.GetFullPath(legacy).Equals(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(ConfigPath)) return;

            var moved = 0;
            foreach (var folder in new[] { string.Empty, "models", "recordings" })
            {
                var source = Path.Combine(legacy, folder);
                if (!Directory.Exists(source)) continue;

                var destination = Path.Combine(Root, folder);
                Directory.CreateDirectory(destination);

                foreach (var file in Directory.EnumerateFiles(source))
                {
                    var target = Path.Combine(destination, Path.GetFileName(file));
                    if (File.Exists(target)) continue;
                    File.Move(file, target);
                    moved++;
                }
            }

            if (moved > 0)
                Log.Info($"已把 {moved} 个文件从 {legacy} 迁移到 {Root}（数据改为就地存放）");
        }
        catch (Exception ex)
        {
            Log.Warn("迁移旧数据目录失败（不影响使用）：" + ex.Message);
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(ComponentsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        MigrateLegacyDataIfNeeded();
        MigrateFromLocalAppDataIfNeeded();
    }

    /// <summary>
    /// 从旧名字（MicMate）的数据目录搬过来。改名只影响程序名，用户的数据不该跟着"消失"：
    ///   · 老版本把数据放在 exe 同级的 data\，而新旧 exe 都在 bin\&lt;配置&gt;\net9.0-windows\，
    ///     因此老数据往往就在"上一次编译输出"的同名路径下，按兄弟目录直接找；
    ///   · 安装目录整体改名时（MicMate\ → MateMic\），老目录与新目录同级。
    /// 只搬配置、模型与录音；目标目录已有 config.json 就认为已经是新数据，不再搬。
    ///
    /// ⚠ 路径推导刻意不靠"固定上跳几层"：那种写法一旦目录层级与预期不同
    /// （例如输出被放到 bin\x64\Debug\net9.0-windows\），就会推出
    /// `...\Release\Debug\data` 这种明显不存在的路径，而且静默失败、极难发现。
    /// 这里改为按目录名校验：先找到输出层次名（net9.0-windows），
    /// 再以它为锚点往上一级找 bin，然后遍历 bin 下的每个配置目录拼候选。
    /// </summary>
    private static void MigrateLegacyDataIfNeeded()
    {
        try
        {
            if (File.Exists(ConfigPath)) return;

            var baseDirectory = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Log.Info($"旧版数据迁移检查：程序目录 {baseDirectory}，数据目录 {Root}");
            var candidates = BuildLegacyCandidates(baseDirectory);

            foreach (var candidate in candidates)
            {
                var hasConfig = File.Exists(Path.Combine(candidate, "config.json"));
                Log.Info($"旧版数据迁移候选：{candidate}（存在配置={hasConfig}）");
                if (IsSamePath(candidate, Root)) continue;
                if (!hasConfig) continue;
                if (!CopyLegacyData(candidate)) continue;

                Log.Info($"已从旧版数据目录 {candidate} 迁移配置/模型/录音到 {Root}");
                break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("迁移旧版（MicMate）数据失败（不影响使用）：" + ex.Message);
        }
    }

    /// <summary>
    /// 推导"可能的旧数据目录"。以当前 exe 所在目录为起点向上走，
    /// 每遇到一个名字形如 <c>bin</c> 的目录，就把它的每个同级子目录都当成
    /// 一个"构建配置目录"，再拼上输出层次名与 data。
    /// 这样无论当前跑的是 Debug 还是 Release，都能找到兄弟配置下的同名数据目录。
    /// </summary>
    private static List<string> BuildLegacyCandidates(string baseDirectory)
    {
        var candidates = new List<string>();
        var outputName = Path.GetFileName(baseDirectory);   // net9.0-windows

        var current = new DirectoryInfo(baseDirectory);
        for (var depth = 0; depth < 5 && current != null; depth++)
        {
            var parent = current.Parent;
            if (parent != null && parent.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                // current 就是 bin 下的某个"构建配置目录"（Debug / Release / x64…）
                foreach (var sibling in SafeDirectories(parent))
                {
                    candidates.Add(Path.Combine(sibling.FullName, outputName, "data"));
                }

                // bin\Debug\net9.0-windows\data\..\..\..\.. → 安装根目录；其下的 MicMate 是改名前的安装目录
                var installRoot = parent.Parent;
                if (installRoot != null)
                {
                    candidates.Add(Path.Combine(installRoot.FullName, "MicMate", "bin", current.Name,
                        outputName, "data"));
                }

                break;
            }

            current = parent;
        }

        // 便携用法兜底：老版本可能把数据直接放在 exe 同级（没有 data 子目录）
        candidates.Add(baseDirectory);

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<DirectoryInfo> SafeDirectories(DirectoryInfo parent)
    {
        try
        {
            return parent.EnumerateDirectories();
        }
        catch (Exception ex)
        {
            Log.Warn("枚举构建配置目录失败：" + ex.Message);
            return Array.Empty<DirectoryInfo>();
        }
    }

    private static bool IsSamePath(string a, string b)
    {
        try
        {
            return Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把老目录里的配置、模型、录音复制过来（不删除老数据，出问题还能手工找回）。</summary>
    private static bool CopyLegacyData(string legacyRoot)
    {
        try
        {
            var moved = 0;

            var legacyConfig = Path.Combine(legacyRoot, "config.json");
            if (File.Exists(legacyConfig) && !File.Exists(ConfigPath))
            {
                Directory.CreateDirectory(Root);
                File.Copy(legacyConfig, ConfigPath, false);
                moved++;
            }

            foreach (var folder in new[] { "models", "recordings" })
            {
                var source = Path.Combine(legacyRoot, folder);
                if (!Directory.Exists(source)) continue;

                var destination = Path.Combine(Root, folder);
                Directory.CreateDirectory(destination);

                foreach (var file in Directory.EnumerateFiles(source))
                {
                    var target = Path.Combine(destination, Path.GetFileName(file));
                    if (File.Exists(target)) continue;
                    File.Copy(file, target, false);
                    moved++;
                }
            }

            return moved > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("读取旧版数据目录失败：" + ex.Message);
            return false;
        }
    }

    public AppConfig Load()
    {
        EnsureDirectories();
        if (!File.Exists(ConfigPath))
        {
            var fresh = new AppConfig();
            ConfigMigrations.Apply(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();

            // 迁移必须紧跟反序列化：早于它读配置的代码会拿到旧语义
            // （典型是 MainWindow 构造函数里的 ApplyThemeToResources 读 DarkMode）。
            ConfigMigrations.Apply(config);
            return config;
        }
        catch (Exception ex)
        {
            Log.Error("配置读取失败，已回退到默认配置", ex);
            TryBackupBrokenConfig();
            var fallback = new AppConfig();
            ConfigMigrations.Apply(fallback);
            return fallback;
        }
    }

    public void Save(AppConfig config)
    {
        try
        {
            EnsureDirectories();

            // 覆盖之前先留一份带时间戳的备份（最多保留 20 份）。
            // 数据目录位于 bin\<配置>\net9.0-windows\data，`dotnet clean` 或手工删除 bin
            // 会把配置一起删掉；有备份至少能捞回来。
            // 备份失败绝不影响正常保存。
            TrySnapshotBeforeOverwrite();

            var json = JsonSerializer.Serialize(config, Options);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, true);
        }
        catch (Exception ex)
        {
            Log.Error("配置保存失败", ex);
        }
    }

    /// <summary>数据目录名，备份就放在它下面（跟着数据走，不会跑到别处）。</summary>
    private static string BackupDirectory => Path.Combine(Root, "config-backups");

    private const int MaxConfigBackups = 20;

    /// <summary>把当前 config.json 复制一份到 config-backups\config_yyyyMMdd_HHmmss.json，并裁剪旧备份。</summary>
    private static void TrySnapshotBeforeOverwrite()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            Directory.CreateDirectory(BackupDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var target = Path.Combine(BackupDirectory, $"config_{stamp}.json");

            // 同一秒内多次保存只留一份，避免刷出一堆重复文件
            if (!File.Exists(target)) File.Copy(ConfigPath, target, false);

            var files = Directory.EnumerateFiles(BackupDirectory, "config_*.json")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxConfigBackups)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 单个旧备份删不掉无所谓
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("配置快照备份失败（不影响保存）：" + ex.Message);
        }
    }

    private static void TryBackupBrokenConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                File.Move(ConfigPath, ConfigPath + ".broken", true);
        }
        catch
        {
        }
    }
}
