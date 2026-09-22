using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicMate.Core;

/// <summary>
/// 应用数据目录。默认就是**程序安装目录**（exe 同级的 MicMate 数据目录），
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
    public static string LogsDirectory => Path.Combine(Root, "logs");
    public static string ConfigPath => Path.Combine(Root, "config.json");

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
    ///   3. %LocalAppData%\MicMate  —— 仅当安装目录只读（如装在 Program Files）
    ///   4. %TEMP%\MicMate          —— 最后兜底，保证程序始终能启动
    /// </summary>
    private static string ResolveRoot()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            candidates.Add(Path.Combine(baseDirectory, "data"));
            candidates.Add(Path.Combine(baseDirectory, "MicMate"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "MicMate"));

        candidates.Add(Path.Combine(Path.GetTempPath(), "MicMate"));

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
    /// 把早期版本写在 %LocalAppData%\MicMate 的数据搬到程序目录。
    /// 只搬一次（目标目录已有 config.json 就跳过），只搬配置、模型与录音。
    /// </summary>
    private static void MigrateFromLocalAppDataIfNeeded()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData)) return;

            var legacy = Path.Combine(localAppData, "MicMate");
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
        Directory.CreateDirectory(LogsDirectory);
        MigrateFromLocalAppDataIfNeeded();
    }

    public AppConfig Load()
    {
        EnsureDirectories();
        if (!File.Exists(ConfigPath)) return new AppConfig();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error("配置读取失败，已回退到默认配置", ex);
            TryBackupBrokenConfig();
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        try
        {
            EnsureDirectories();
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
