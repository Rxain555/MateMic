using System.IO;
using System.Text;
using System.Windows;

namespace MateMic.Core;

/// <summary>
/// 界面文案（悬浮说明 + 各类说明文档）的**单一来源**。
///
/// 为什么要有这个类：这些文字散在 XAML 与代码里的时候，改一句话要翻好几个文件、
/// 还要重新编译打包；集中之后，**改数据目录下的「文案.txt」并重启程序即可生效**。
///
/// 设计要点：
///   · 内置默认值（<see cref="Defaults"/>）永远存在，是"文案文件的模板"兼"兜底"；
///     文件缺失会自动按模板重建，某一段被删掉则退回内置文字 —— 界面绝不会因为
///     用户改了文案文件而变成空白或报错。
///   · 文件只在**启动时读一次**，运行期不再碰磁盘（也就没有"改到一半被读到"的问题）。
///   · 解析规则刻意做得极简（见 <see cref="Parse"/>），用记事本就能改：
///     <c>[标识]</c> 开始一段，下面的行都是内容，<c>#</c> 开头是注释。
///   · 这不是给最终用户的自定义功能，界面上没有任何入口；它服务的只是"作者自己想改文案"。
/// </summary>
public static class TextCatalog
{
    /// <summary>文案文件名（位于数据目录，与 config.json / logs 同级）。</summary>
    public const string FileName = "文案.txt";

    /// <summary>资源字典里的键前缀。XAML 里写成 <c>{DynamicResource 文案.xxx}</c>。</summary>
    public const string ResourcePrefix = "文案.";

    public static string FilePath => Path.Combine(ConfigStore.Root, FileName);

    /// <summary>已生效的文案（内置默认值 + 文件覆盖）。</summary>
    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);

    private static bool _loaded;

    /// <summary>一条文案的定义：标识 / 用途提示（只写进模板文件当注释）/ 默认内容。</summary>
    private readonly record struct Entry(string Key, string Hint, string Text);

    /// <summary>
    /// 启动时读一次。**必须在窗口构造之前调用**（界面在构造期就会取文案）。
    /// 顺带把全部文案写进应用级资源，供 XAML 用 <c>DynamicResource</c> 引用。
    /// </summary>
    public static void Load()
    {
        Values.Clear();
        foreach (var entry in Defaults) Values[entry.Key] = entry.Text;

        var fromFile = 0;
        var blank = 0;
        try
        {
            if (!File.Exists(FilePath))
            {
                WriteTemplate();
                Log.Info("文案文件不存在，已按内置默认文案生成模板：" + FilePath);
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, text) in Parse(File.ReadAllLines(FilePath, Encoding.UTF8)))
                {
                    seen.Add(key);
                    if (!Values.ContainsKey(key))
                    {
                        // 文件里出现了程序不认识的标识（多半是手误）：记一条日志，不报错
                        Log.Warn($"文案文件里有未知标识，已忽略：[{key}]");
                        continue;
                    }

                    // **内容留空 = 用户明确关掉这条说明**（相应的悬浮说明会整个不显示）。
                    // 注意与"整段删掉"的区别：删掉会退回内置默认值，留空才是关闭。
                    // 这两种手段各有用处，所以不能把空值也当成"没写"而回退默认。
                    Values[key] = text.Trim();
                    if (Values[key].Length == 0) blank++;
                    else fromFile++;
                }

                // 程序升级后可能新增了文案项。**把它们追加到文件末尾**，
                // 这样用户手里那份文件始终是完整的"可改清单"，
                // 不必去猜"还有哪些控件能加说明"（这正是用户 2026-10-06 提的诉求）。
                // 只追加，**绝不改写已有内容** —— 用户可能已经逐条改过。
                var missing = Defaults.Where(e => !seen.Contains(e.Key)).ToList();
                if (missing.Count > 0)
                {
                    AppendEntries(missing);
                    Log.Info($"文案文件已补齐 {missing.Count} 条新增项（追加到文件末尾，已有内容未改动）");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("文案文件读取失败，全部改用内置默认文案：" + ex.Message);
        }

        Log.Info($"界面文案已就绪：共 {Values.Count} 条，其中 {fromFile} 条来自 {FilePath}"
                 + (blank > 0 ? $"，另有 {blank} 条留空（不显示）" : string.Empty));
        _loaded = true;

        Publish();
    }

    /// <summary>取一条文案；标识写错时返回醒目的占位，便于一眼发现而不是静默空白。</summary>
    public static string Get(string key)
    {
        if (!_loaded) Load();   // 兜底：调用方忘了 Load 也不至于拿到空白
        return Values.TryGetValue(key, out var value) ? value : $"【缺少文案：{key}】";
    }

    /// <summary>把全部文案发布到应用级资源，XAML 用 <c>{DynamicResource 文案.标识}</c> 引用。</summary>
    private static void Publish()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        foreach (var (key, text) in Values)
            resources[ResourcePrefix + key] = text;
    }

    // ================================================================ 解析 / 写模板

    /// <summary>
    /// 解析文案文件。规则（刻意极简，记事本可改）：
    ///   · <c>[标识]</c> 独占一行 ⇒ 开始一段；
    ///   · 段落内的其它行都是内容，原样保留换行；
    ///   · <c>#</c> 开头的行是注释，忽略；
    ///   · 两段之间、首尾的空行会被去掉（不然提示框上下会多出空白）。
    /// </summary>
    private static IEnumerable<(string Key, string Text)> Parse(string[] lines)
    {
        string? key = null;
        var buffer = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                if (key != null) yield return (key, Trim(buffer));
                key = line[1..^1].Trim();
                buffer.Clear();
                continue;
            }

            if (key != null) buffer.Add(line);
        }

        if (key != null) yield return (key, Trim(buffer));
    }

    /// <summary>去掉首尾空行（中间的空行保留 —— 说明文字里的分段是有意义的）。</summary>
    private static string Trim(List<string> lines)
    {
        var start = 0;
        var end = lines.Count - 1;
        while (start <= end && string.IsNullOrWhiteSpace(lines[start])) start++;
        while (end >= start && string.IsNullOrWhiteSpace(lines[end])) end--;
        return start > end ? string.Empty : string.Join(Environment.NewLine, lines.Skip(start).Take(end - start + 1));
    }

    /// <summary>按内置默认文案生成一份带注释的模板文件（只在文件不存在时调用）。</summary>
    private static void WriteTemplate()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ============================================================");
        builder.AppendLine("# MateMic 文案文件 —— 全部「悬浮说明」与「说明对话框正文」都在这里");
        builder.AppendLine("#");
        builder.AppendLine("# 直接改下面的文字，另存为 UTF-8 编码，然后重新打开 MateMic 即可生效");
        builder.AppendLine("# —— 不需要重新编译，也不需要重新安装。");
        builder.AppendLine("#");
        builder.AppendLine("#   · [标识] 是这段文案的名字，请勿改动；要改的是它下面的内容。");
        builder.AppendLine("#   · 内容可以写多行，回车换行即可。");
        builder.AppendLine("#   · 以 # 开头的行是注释（写着这段用在哪），不会显示。");
        builder.AppendLine("#   · 把某段的内容**清空**（[标识] 留着、下面空着）＝ 不再显示这条说明。");
        builder.AppendLine("#   · 本文件按控件分组，**每一条都对应界面上一个具体的控件/部位**，");
        builder.AppendLine("#     想给哪个控件改说明，按分组找过去就行。");
        builder.AppendLine("#   · 程序升级后若新增了可改的条目，会**自动追加到本文件末尾**（不会改动你已改的内容）。");
        builder.AppendLine("#   · {版本} / {键} 这类花括号是占位符，由程序替换，请保留。");
        builder.AppendLine("# ============================================================");
        builder.AppendLine();

        string? section = null;
        foreach (var entry in Defaults)
        {
            var current = SectionOf(entry.Key);
            if (current != section)
            {
                section = current;
                builder.AppendLine("# ------------------------------------------------------------");
                builder.AppendLine("# " + section);
                builder.AppendLine("# ------------------------------------------------------------");
                builder.AppendLine();
            }

            builder.AppendLine("# " + entry.Hint);
            builder.AppendLine("[" + entry.Key + "]");
            builder.AppendLine(entry.Text);
            builder.AppendLine();
        }

        File.WriteAllText(FilePath, builder.ToString(), new UTF8Encoding(true));
    }

    /// <summary>
    /// 把新增（文件里还没有）的条目追加到文件末尾。
    ///
    /// **只追加，不改写已有内容**：用户可能已经逐条改过，任何"重新生成整个文件"的做法
    /// 都会把他的修改冲掉。用无 BOM 追加：文件本身是否带 BOM 由首次生成时决定，
    /// 追加时再写一次 BOM 会在文件中间插入 EF BB BF。
    /// </summary>
    private static void AppendEntries(List<Entry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("# ------------------------------------------------------------");
        builder.AppendLine("# 以下条目是程序更新后新增的，等你来改");
        builder.AppendLine("# ------------------------------------------------------------");
        builder.AppendLine();

        foreach (var entry in entries)
        {
            builder.AppendLine("# " + entry.Hint);
            builder.AppendLine("[" + entry.Key + "]");
            builder.AppendLine(entry.Text);
            builder.AppendLine();
        }

        File.AppendAllText(FilePath, builder.ToString(), new UTF8Encoding(false));
    }

    /// <summary>模板文件里的分组标题（只影响可读性，程序不读它）。</summary>
    private static string SectionOf(string key) => key.Split('.')[0] switch
    {
        "Caption" => "标题栏按钮",
        "Toolbar" => "工具栏",
        "Device" => "设备选择（顶部三个下拉框）",
        "Gate" => "噪声门",
        "Denoise" => "AI 降噪",
        "Voice" => "DSP 变声",
        "Loudness" => "响度平衡",
        "Eq" => "EQ 均衡器",
        "Effect" => "效果器",
        "Gain" => "增益",
        "Player" => "播放器面板",
        "Track" => "音频列表",
        "Spectrum" => "频谱与电平",
        "Level" => "频谱与电平",
        "Dialog" => "说明对话框（标题）",
        "Guide" => "说明对话框（正文）",
        "Risk" => "风险说明",
        "About" => "关于",
        _ => "其它",
    };

    // ================================================================ 默认文案

    /// <summary>
    /// 内置默认文案。**这就是文案文件的模板**：文件不存在时按它生成，
    /// 某一段被删除或留空时也用它兜底。顺序即模板文件里的顺序。
    /// </summary>
    private static readonly Entry[] Defaults =
    {
        // ---------------------------------------------------------- 标题栏
        new("Caption.Minimize", "标题栏「最小化」按钮",
            "最小化"),
        new("Caption.Close", "标题栏「关闭」按钮",
            "关闭"),

        // ---------------------------------------------------------- 工具栏
        new("Toolbar.Processing", "工具栏「音频处理」总开关",
            "总旁通开关：关闭后麦克风静音（播放器仍可送出）"),
        new("Toolbar.Hotkey", "工具栏「全局快捷键」按钮",
            "点击后按下组合键，即可为「音频处理」总开关绑定全局快捷键。注意：全局快捷键会被 MateMic 独占，其他程序（含文本输入框）不会再收到该按键，建议使用带 Ctrl / Alt 的组合键。"),
        new("Toolbar.Monitor", "工具栏「监听」开关",
            """
            让麦克风进监听（物理扬声器）。与右侧播放器的「音频监听」是两个独立开关，四种组合：
            · 都关：没有监听；
            · 只开这个：只听麦克风；
            · 只开音频监听：只听播放的音频；
            · 都开：麦克风 + 播放的音频。
            """),
        new("Toolbar.CloseToTray", "工具栏「关闭到托盘」开关",
            "打开：点右上角关闭按钮时收进系统托盘（双击托盘图标恢复）。关闭：关闭按钮直接退出程序。最小化按钮始终只最小化到任务栏。"),
        new("Toolbar.DarkMode", "工具栏「深色模式」开关",
            "切换深色 / 浅色配色。深色配色在暗环境下更护眼；设置会保存，下次启动继续沿用。"),
        new("Toolbar.Guide", "工具栏「使用指南」按钮",
            "Windows 声音设置、设备选择、MIXLINE 接法"),
        new("Toolbar.About", "工具栏「关于」按钮",
            "字体、模型与第三方组件的署名与许可"),
        new("Toolbar.AutoStart", "工具栏「开机自启」开关",
            "打开后，登录 Windows 时自动启动 MateMic 并直接收进系统托盘（不弹主窗口）。设置会保存，下次开机继续沿用。"),

        // ---------------------------------------------------------- 设备选择
        new("Device.Input", "顶部「输入」设备下拉框",
            """
            选择实际在用的物理麦克风。
            ⚠ 这里不要选 MIXLINE Stream —— 那是给游戏/语音软件用的虚拟设备；MateMic 需要拿到真实麦克风的声音。
            """),
        new("Device.Output", "顶部「输出」设备下拉框",
            """
            处理后的声音送到哪里。
            · 配合 MIXLINE 使用时选「扬声器 (MIXLINE)」；
            · 也可以选普通扬声器，直接本地出声。
            """),
        new("Device.Monitor", "顶部「监听」设备下拉框",
            "选择你实际在用的物理扬声器或耳机，用来听监听内容。要和左边的「输出」分开选，否则监听会绕回虚拟设备、听不到真实效果。"),

        // ---------------------------------------------------------- 噪声门
        new("Gate.Enable", "噪声门模块开关",
            "打开噪声门：低于阈值的持续底噪（空调、风扇、电流声）被压掉；说话时自动让路，说完再关上。"),
        new("Gate.Threshold", "噪声门「阈值」滑条",
            "低于这个电平的声音被当作噪声压掉。−55 dBFS 接近安静房间的底噪水平；说话声音偏小就调高一些（例如 −45）。"),
        new("Gate.Release", "噪声门「释放时间」滑条",
            "声音停下后，噪声门再等多久才关上。太短会把句尾的尾音切掉，太长则能听出明显的“抽气”感。"),

        // ---------------------------------------------------------- AI 降噪
        new("Denoise.OpenFolder", "AI 降噪「打开模型文件夹」按钮",
            "打开降噪模型文件夹"),
        new("Denoise.Enable", "AI 降噪模块开关",
            "打开后按所选模型做 AI 降噪。模型越重，效果通常越好，但占用的 CPU / GPU 也越多。"),
        new("Denoise.Model", "AI 降噪「模型」下拉框",
            "选择降噪模型。列表里既有随程序内置的模型，也有你自己放进模型目录的 .onnx 文件（点右侧按钮可打开该目录）。"),
        new("Denoise.Strength", "AI 降噪「强度」滑条",
            "降噪的作用力度。调高更安静，但过高会让声音发闷、发“糊”。"),
        new("Denoise.Wet", "AI 降噪「干湿比」滑条",
            "降噪结果与原始声音的混合比例。100% 完全使用降噪后的声音；调低会混回一部分原声，更自然但底噪也回来一些。"),

        // ---------------------------------------------------------- 变声
        new("Voice.Enable", "DSP 变声模块开关",
            "打开后按下面四项改变你的音色。它工作在 AI 降噪之后。"),
        new("Voice.Pitch", "DSP 变声「变调」滑条",
            "整体移动音高，单位半音（12 半音 = 一个八度）。只动它会让声音像“加速 / 减速”，通常要和下面的共振峰一起调。"),
        new("Voice.GenderFactor", "DSP 变声「声线」滑条",
            "负值更低沉、正值更清亮。按你的基频分布自动适配，比单纯变调自然。"),
        new("Voice.Formant", "DSP 变声「共振峰」滑条",
            "独立搬移共振峰（声音的“粗细”），不影响音高。这是让变声听起来自然的关键：男→女时音高与共振峰都要往上走。"),
        new("Voice.Mix", "DSP 变声「干湿比」滑条",
            "变声结果与原始声音的混合比例。100% 完全是变声后的声音。"),

        // ---------------------------------------------------------- 响度
        new("Loudness.Enable", "响度平衡模块开关",
            "打开后自动把音量拉到目标响度，说话大声小声听起来一样响（类似“自动增益”）。"),
        new("Loudness.Target", "响度平衡「目标响度」滑条",
            "麦克风信号的平均电平要拉到多少 dBFS。说话偏小就把目标调高（如 −16），偏大就调低（如 −24）。"),
        new("Loudness.Speed", "响度平衡「跟随速度」滑条",
            "增益跟随音量变化的快慢。数值越大反应越快（约 2.5 秒 → 0.15 秒）；太快会有明显的抽吸感，太慢会跟不上。"),

        // ---------------------------------------------------------- EQ 均衡器
        new("Eq.Enable", "EQ 均衡器模块开关",
            "打开后按下面的曲线调整音色。全部 0 dB（平坦）时它不参与处理，等于关闭。"),
        new("Eq.Curve", "EQ 上方的响应曲线",
            "当前 EQ 的形状：横轴是频率（与下方 10 段推子一一对应），中间那条横线是 0 dB。点预设或拖推子都会立刻反映在这里。"),
        new("Eq.Band", "EQ 的垂直推子（10 段共用这一条说明）",
            "上下拖动调整该频段的增益，范围 ±12 dB、0 dB 在正中间。手动拖过之后会自动脱离预设，变成“自定义”。"),
        new("Eq.Preset.Bright", "EQ 预设「清亮」",
            "抬升 4–8 kHz 的空气感、压低 300 Hz 以下的浑浊。适合想让声音更通透、更“靠前”的时候。"),
        new("Eq.Preset.Warm", "EQ 预设「沉稳」",
            "抬升 125–250 Hz 的厚度、削掉刺耳的高频。声音更厚实、更耐听。"),
        new("Eq.Preset.Deep", "EQ 预设「深邃」",
            "大幅加重低频、明显衰减 1–4 kHz 的存在感。声音低沉、有距离感。"),
        new("Eq.Preset.Sharp", "EQ 预设「尖锐」",
            "抬升 4–16 kHz 的清晰度。字头更锋利、更突出，但推得太多容易刺耳。"),
        new("Eq.Preset.Ethereal", "EQ 预设「空灵」",
            "轻抬 2–16 kHz、衰减 500 Hz 一带。声音更轻、更“飘”。"),
        new("Eq.Reset", "EQ「重置」按钮",
            "把所有频段恢复到 0 dB（平坦），并取消预设的选中状态。"),

        // ---------------------------------------------------------- 效果器
        new("Effect.Enable", "效果器模块开关",
            "打开后按选中的效果处理声音（混响 / 延迟 / 和声 / 电话 / 颤音 / 电音 / 炸麦）。"),
        new("Effect.Amount", "效果器「深度」滑条",
            "效果的强度。越大越明显，具体听感取决于所选的那一项效果。"),

        // ---------------------------------------------------------- 增益
        new("Gain.Enable", "增益模块开关",
            "打开后按固定值放大或衰减音量，用来补足麦克风本身的响度差异。"),
        new("Gain.Amount", "增益「增益」滑条",
            "固定的增益量，单位 dB。它是处理链的最后一环，调它不会影响前面各模块的判断。"),

        // ---------------------------------------------------------- 播放器
        new("Player.AudioMonitor", "播放器「音频监听」开关",
            """
            播放器音频（伴奏 / 语音包）是否接入混音总线。
            · 打开：播放器进主输出（MIXLINE，队友能听到），并在工具栏「监听」打开时进监听设备。
            · 关闭：两条输出都没有播放器音频。
            配合工具栏「监听」的四种组合：都关=没有监听；只开音频监听=只听播放器；只开监听=只听麦克风；都开=麦克风+播放器。
            """),
        new("Player.HoldKeySwitch", "播放器「同步按住键」开关",
            "启用后，可以为每个音频文件指定一个按键：播放前自动按下、播放结束后自动松开，用来替代游戏/语音软件里「按键说话」的手动按键。用 Win32 SendInput 注入按键，内核级反作弊（Vanguard / EAC / BattlEye 等）有能力识别注入事件，存在被判定风险，默认关闭。启用时会弹窗说明。"),
        new("Player.Loop", "播放器「循环播放」开关",
            "打开后，播放的音频放完会自动从头再来，直到你手动停止。"),
        new("Player.Volume", "播放器「播放音量」滑条",
            "播放器（伴奏 / 语音包）的音量。只影响播放器这一路，不影响麦克风。"),
        new("Player.Risk", "播放器「风险说明」按钮",
            "查看「同步按住键」的完整风险说明。建议在开启该功能之前读一遍。"),
        new("Player.AddFiles", "播放器「添加音频文件」按钮",
            "把伴奏或语音包加进列表。也可以直接把文件拖进窗口来添加。"),

        // ---------------------------------------------------------- 音频列表
        new("Track.Row", "音频列表每一行",
            "点击播放；正在播放时再点一次即停止。右侧按钮分别设置「全局播放快捷键」与「同步按住键」，最右侧 ✕ 移除"),
        new("Track.Remove", "音频列表最右侧「✕」按钮",
            "从列表移除该音频文件"),
        new("Track.Hotkey.Placeholder", "尚未设置快捷键时按钮上的文字",
            "设快捷键"),
        new("Track.Hotkey.Recording", "正在录入快捷键时按钮上的文字",
            "请按键…"),
        new("Track.Hotkey.Empty", "快捷键按钮（未设置）的悬浮提示",
            "点击设置全局播放快捷键：播放 / 停止该音频（录入时按 Esc 取消、Backspace 清除）"),
        new("Track.Hotkey.Set", "快捷键按钮（已设置）的悬浮提示；{键} 会被替换成当前按键",
            "当前快捷键：{键}（点击可重新设置；录入时按 Esc 取消、Backspace 清除）"),
        new("Track.HoldKey.Placeholder", "尚未设置同步按住键时按钮上的文字",
            "设同步键"),
        new("Track.HoldKey.Empty", "同步按住键按钮（未设置）的悬浮提示",
            "点击设置按键：播放该音频前自动按下、播放结束后自动松开（用来替代游戏里手动按「按键说话」）"),
        new("Track.HoldKey.Set", "同步按住键按钮（已设置）的悬浮提示；{键} 会被替换成当前按键",
            "当前按键：{键}（点击可重新设置；录入时按 Esc 取消、Backspace 清除）"),

        // ---------------------------------------------------------- 频谱与电平
        new("Spectrum.Input", "中栏上方「输入」实时频谱",
            "麦克风经过处理之后的实时频谱：横轴是对数频率刻度（100 Hz – 16 kHz），纵轴是各频段的能量。哪一段抬起来，就是那个频段现在有声音。"),
        new("Spectrum.Output", "中栏下方「输出」实时频谱",
            "最终送出去的信号的频谱（含播放器混音）。与上面的输入频谱对比，能直观看出各模块改动了什么。"),
        new("Level.Meter", "底部「音量合适度」参照尺",
            "麦克风当前音量落在哪一档：左绿右红，越靠右越接近削波。黑色短线是最近几秒的峰值，会缓慢回落。"),

        // ---------------------------------------------------------- 说明对话框
        new("Dialog.UsageGuide.Title", "「使用指南」对话框标题",
            "MateMic 使用指南"),
        new("Dialog.About.Title", "「关于」对话框标题",
            "关于 MateMic"),
        new("Dialog.HoldKeyRisk.Title", "「同步按住键 · 风险说明」对话框标题",
            "同步按住键 · 风险说明"),
        new("Dialog.Mixline.Title", "「MIXLINE 设置指南」对话框标题",
            "MIXLINE 设置指南"),

        new("Guide.Usage", "「使用指南」正文",
            """
            一、Windows 声音设置

            将麦克风 MIXLINE Stream 设为默认输入设备。

            二、设备选择

            输入选择：实际在用的物理麦克风
            输出选择：扬声器 (MIXLINE)
            监听选择：实际在用的物理扬声器

            三、MIXLINE 中

            添加输入：MateMic
            添加输出：MIXLINE Stream
            将 MateMic 节点连接至 MIXLINE Stream 节点
            """),

        new("Guide.Mixline", "「MIXLINE 设置指南」正文",
            """
            MIXLINE 配合设置步骤

            1. 安装并打开 MIXLINE。
            2. 在 MIXLINE 中新建一个输入通道，选择 MateMic 的主输出设备（即「扬声器 (MIXLINE)」）作为输入源。
            3. 将该通道路由到 MIXLINE Stream（虚拟麦克风）。
            4. 在 Windows 声音设置中，把默认录音设备设为 MIXLINE Stream。
            5. 回到 MateMic，把「输出」选择为 MIXLINE 的虚拟播放设备。
            6. 在语音软件（Discord / 微信 / QQ / 游戏语音）中把麦克风选为 MIXLINE Stream。

            提示：MateMic 自身不创建虚拟声卡，必须配合 MIXLINE 使用。
            """),

        new("About", "「关于」正文；{版本} 会被替换成版本号",
            """
            MateMic {版本}   ·   MIT 许可
            https://github.com/Rxain555/MateMic

            界面字体 MiSans（小米，免费商用）。
            内置 3 个 ONNX 降噪模型；自备模型的许可由模型提供方决定。
            第三方组件：NAudio / NWaves / ONNX Runtime（均 MIT）。
            """),

        new("Risk.HoldKey", "「同步按住键」风险说明正文（开启时的确认框与「风险说明」按钮共用）",
            """
            「同步按住键」会用 Win32 SendInput 向系统注入按键事件（按下 / 松开）：
            播放某个音频文件之前自动按下你指定的按键，播放结束后自动松开。
            典型用法：把该键设成游戏或语音软件里「按键说话」的那个键，
            这样按快捷键播放语音包时就不必再手动按住说话键。

            关于反作弊：
            · SendInput 注入的事件带有 LLKHF_INJECTED 标记，属于「软件模拟输入」。
            · 内核级反作弊（Riot Vanguard、Easy Anti-Cheat、BattlEye、FACEIT 等）有能力识别这类事件；
              是否判定为违规由厂商的策略决定，【这个风险无法排除】。
            · 本功能只发送你自己录入的那一个按键，不连发、不循环、不读写任何其它进程、不注入代码。
            · 如果游戏对模拟输入查得很严，请不要使用本功能；
              可改用硬件级方案（键盘宏 / 脚踏开关 / 手柄映射）达到同样的效果。

            因此该功能默认关闭，需要你自己开启。
            """),

        new("Risk.HoldKey.Confirm", "开启「同步按住键」时确认框里的追问",
            "是否启用「同步按住键」？"),
        new("Risk.HoldKey.StateOn", "「风险说明」里显示当前开关状态（已启用）",
            "当前开关：已启用。"),
        new("Risk.HoldKey.StateOff", "「风险说明」里显示当前开关状态（未启用）",
            "当前开关：未启用。"),
    };
}
