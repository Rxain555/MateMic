# MicMate

面向普通用户的 Windows 桌面实时麦克风处理工具。软件**不创建虚拟声卡**，而是与 **MIXLINE** 配合工作：

```
物理麦克风 → MicMate（降噪 / 音色 / 效果 / 增益）
           → 输出到 MIXLINE 的输入通道
           → MIXLINE 路由到 MIXLINE Stream（虚拟麦克风）
           → 系统默认录音设备设为 MIXLINE Stream
           → 语音软件（Discord / 微信 / QQ / 游戏语音）收到处理后的音频
```

实现依据为《MicMate 项目规划书 v2.2》，界面严格对齐 UI 效果图（浅色圆角扁平风 + 系统标题栏）。

---

## 1. 当前进度

| 阶段 | 模块 | 状态 |
|---|---|---|
| 一 | M1 项目骨架 + 主窗口 | ✅ 完成 |
| 一 | M2 设备枚举 + 麦克风采集 | ✅ 完成 |
| 一 | M3 音频输出到 MIXLINE 虚拟设备 | ✅ 完成 |
| 一 | M4 监听输出 | ✅ 完成 |
| 一 | M5 效果链框架（可动态增删） | ✅ 完成 |
| 二 | M6 增益 | ✅ 完成 |
| 二 | M7 噪声门 | ✅ 完成 |
| 二 | M8 音色风格预设（6 组 EQ 曲线） | ✅ 完成 |
| 二 | M9 AI 降噪（ONNX 模型加载 + 谱减后端） | ✅ 完成（见 §5 说明） |
| 二 | M10 响度平衡（RMS 简化版） | ✅ 完成 |
| 二 | M11 混响 / 延迟 / 合唱 / 电音 | ✅ 完成 |
| 二 | M12 效果器单例容器 | ✅ 完成 |
| 三 | M13 FFT 频谱（输入 + 输出）+ RMS 电平条 | ✅ 完成 |
| 三 | M14 音频文件播放器 + 50:50 混合 | ✅ 完成 |
| 三 | M15 全局快捷键绑定 | ✅ 完成 |
| 三 | M16 播放路由开关 + 独立音量 + 播放列表持久化 | ✅ 完成 |
| 四 | M17/M18 录音 + 背景噪声采集 | ✅ 完成 |
| 四 | M19 Python 运行时检测与自动安装 | ⚠️ 已实现，未实测（见 §6） |
| 四 | M20 训练脚本调用 + 进度反馈 | ⚠️ 已实现，含内置训练器（见 §6） |
| 四 | M21 模型文件夹扫描 + 模型验证 | ✅ 完成 |
| 四 | M22 配置持久化 | ✅ 完成 |
| 五 | M23–M25 扩展（手机串流 / AOA / 变声） | ⬜ 未开始（规划书标注为可选） |

---

## 2. 运行要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 1607+ / Windows 11（x64） |
| 运行时 | .NET 9 Desktop Runtime（`Microsoft.WindowsDesktop.App 9.x`） |
| 采样率 | 48 kHz（软件内部统一为 48 kHz / 单声道 / 32-bit float） |
| 虚拟声卡 | 需另行安装 MIXLINE（软件只发送音频，不创建虚拟设备） |

> **与规划书的差异**：规划书写的是 .NET 8 + NAudio 3。NAudio 3.x 最低要求 `net9.0`，
> 因此工程目标框架改为 `net9.0-windows`（LTS 中 .NET 8 仍受支持，仅 NAudio 3 不支持）。
> 界面与功能规格完全按规划书实现。

---

## 3. 构建与运行

### 在 Visual Studio 里操作（推荐）

1. 双击打开 **`D:\DSH\MicMate\MicMate.sln`**（它会同时加载 `MicMate` 和 `MicMate.Windows` 两个工程）。
2. 首次打开时若提示还原 NuGet 包：右键解决方案 → **还原 NuGet 包**。本机包缓存已经还原过，通常无需联网。
3. 在「解决方案资源管理器」里右键 **MicMate** 工程 → **设为启动项目**。
4. 按 **F5**（调试运行）或 **Ctrl+F5**（不调试运行）。

就这样，不需要其他配置：`MicMate.csproj.user` 已设好工作目录，`tools\train_denoise.py` 会自动复制到输出目录。

**开发小贴士**

| 场景 | 做法 |
|---|---|
| 只想改界面外观 | 运行中直接改 XAML，XAML 热重载会即时生效，不必重启 |
| 想隔离测试数据（不动真实配置） | 调试 → MicMate 属性 → 调试 → 命令行参数填 `--appdata D:\temp\micmate-dev` |
| 想看渲染结果截图 | 命令行参数填 `--selfcheck D:\ui.png`，程序注入合成信号、截图后自动退出 |
| 想验证训练链路 | 命令行参数填 `--selftrain D:\train_out`，跑完自动退出并写日志 |
| 程序“起不来” | 看 `%LocalAppData%\MicMate\logs\app_yyyyMMdd.log`，启动失败原因都在里面 |

### 命令行方式

```powershell
dotnet restore MicMate.sln          # 还原依赖（NuGet.config 已配国内镜像 nuget.azure.cn）
dotnet build MicMate.sln -c Debug   # 构建
dotnet run --project MicMate.csproj # 运行
```

产物：`MicMate\bin\Debug\net9.0-windows\MicMate.exe`
（`MicMate.Windows.dll` 为托盘图标所在的 WinForms 程序集，需与主程序放在一起）

> **不要在 csproj 里设 `RuntimeIdentifier`**。设了会把输出挪到 `bin\Debug\net9.0-windows\win-x64\`，
> 而 Visual Studio 调试时用的启动路径与"直接双击 exe"可能不一致，
> 结果数据目录（exe 同级的 `data\`）被分到两处，表现为「VS 里调试不保存配置、直接运行却会保存」。

数据目录 = **exe 同级的 `data\`**（`config.json` / `models\` / `recordings\` / `logs\` / `runtime\`）。
按 F5 调试时它就在 `bin\Debug\net9.0-windows\data\`。

工作区根目录还提供了 `build-and-run.bat`，双击即可「构建 + 启动」。

### 命令行参数

| 参数 | 说明 |
|---|---|
| `--appdata <目录>` | 覆盖数据目录（调试 / 便携模式），默认 `%LocalAppData%\MicMate` |
| `--selfcheck <png> [--expanded]` | 渲染自检：注入合成频谱、截图后自动退出；`--expanded` 先展开全部模块 |
| `--selftrain <目录>` | 训练链路自检：合成底噪 → 内置训练 → ONNX 校验 → 提取曲线 |
| `--audiocheck [秒数] [输出设备关键字] [输入设备关键字]` | **音频链路诊断**：逐级打印端点静音/音量、采集与输出的实际电平与样本数，并给出结论 |
| `--audiocheck --fx` | **效果器自检**：对合成信号跑一遍四个效果，打印各自对信号的改变量（用于确认效果真的生效） |

### 听不到声音时先跑 `--audiocheck`

```powershell
MicMate.exe --audiocheck 5 MIXLINE
```

它会输出这类判断，直接指出问题在哪一级：

| 结论 | 含义 |
|---|---|
| 采集侧几乎没有数据 | 录音设备选错，或该设备本身不出数据 |
| 采集有数据但电平极低 | 麦克风没在拾音 / 系统输入静音或音量过低 |
| 输出侧几乎没有数据 | 播放链路没被拉动（播放设备不可用） |
| 输出侧有数据但电平极低 | 混音或处理把信号压没了 |
| 两侧都有正常电平 | 链路正常，问题在 MIXLINE 的路由或接收端 |

### 图标

`Assets\appicon.png` 是设计源文件，`Assets\appicon.ico` 是由它生成的**多尺寸图标**
（16/24/32/48/64/128/256，每尺寸以 PNG 压缩存储）。三处共用同一份 ICO：

| 用途 | 实现方式 |
|---|---|
| exe / 资源管理器 / 任务栏 | csproj 的 `<ApplicationIcon>Assets\appicon.ico</ApplicationIcon>` |
| 窗口（标题栏、Alt+Tab） | 代码从输出目录读取，见 `MainWindow.ApplyWindowIcon` |
| 系统托盘 | 以嵌入资源 `MicMate.Windows.appicon.ico` 打进 `MicMate.Windows.dll` |

> 窗口图标**不能**写成 XAML 的 `Icon="pack://application:,,,/Assets/appicon.ico"`：
> 该文件在 csproj 中以 `None` 方式复制到输出目录，并未打包进程序集资源，
> pack URI 会在 XAML 解析阶段抛 `XamlParseException`（行 7 列 67），
> 而弹出的错误框会把进程挂在后台——表现为「打开软件就卡住」。

修改 PNG 后重新生成 ICO：

```powershell
dotnet run --project tools\dev\IconBuilder -- MicMate\Assets\appicon.png MicMate\Assets\appicon.ico
Copy-Item MicMate\Assets\appicon.ico MicMate.Windows\Assets\appicon.ico -Force
```

### 首次启动的默认状态

效果图里的开关状态、音频列表内容、麦克风名称、训练时间等**都是示例内容，不作为默认值**。实际默认如下：

* 总开关「音频处理」、监听、开机自启：**关**；「最小化到托盘」：开
  （⚠️ 总开关默认关闭 ⇒ **启动时麦克风静音**，这是「闭麦开关」语义；要先打开它才会有声音送往 MIXLINE）
* 六个处理模块（噪声门 / AI 降噪 / 响度平衡 / 音色风格 / 效果器 / 增益）：**全部关闭，且面板收起**（箭头朝左）
  ——展开状态会随配置持久化，下次启动保持原样
* 音色风格的 6 个预设、效果器的 4 种效果：**一个都不选中**（开启模块后需自行选择预设，否则不参与处理）
* 全局快捷键：未设置；音频列表：空（显示“列表为空”提示）
* 设备下拉框：实时枚举本机真实设备，而不是效果图里的占位文字

### 依赖包

| 包 | 版本 | 用途 |
|---|---|---|
| NAudio | 3.1.0 | WASAPI 采集/播放、Media Foundation、`WasapiPlayer`/`WasapiRecorder`、`NAudio.Effects` 效果内核 |
| NWaves | 0.9.5 | 备用 DSP 算法库（FFT / 滤波器 / 重采样） |
| Microsoft.ML.OnnxRuntime | 1.20.1 | ONNX 降噪模型的加载、校验与推理 |

### 自检命令（便于排查环境问题）

```powershell
# 渲染自检：注入合成频谱后截图并退出，用于离线核对界面
MicMate.exe --selfcheck D:\ui_check.png

# 训练链路自检：生成合成底噪 → 内置训练 → ONNX 校验 → 提取降噪曲线
MicMate.exe --selftrain D:\train_out
```

---

## 4. MIXLINE 配合设置

1. 安装并打开 MIXLINE。
2. 在 MIXLINE 中新建一个输入通道，输入源选择 **MicMate 的主输出设备**（即“扬声器 (MIXLINE)”）。
3. 将该通道路由到 **MIXLINE Stream**（虚拟麦克风）。
4. 在 Windows 声音设置中，把默认录音设备设为 **MIXLINE Stream**。
5. 回到 MicMate，把工具栏的「输出」选为 **MIXLINE 的虚拟播放设备**。
6. 语音软件（Discord / 微信 / QQ / 游戏语音）的麦克风选择 **MIXLINE Stream**。

未检测到名称包含 `MIXLINE` 的播放设备时，界面顶部会显示黄色提示并提供「查看设置指南」，但不阻止软件运行。

---

## 5. 音频链路与实现要点

```
WASAPI 采集(BufferedWaveProvider) → 原始输入频谱取样
  → 噪声门 → AI降噪 → 响度平衡 → 音色风格 → 效果器(单例) → 增益
  → 与播放器音频 50:50 混合
  → OutputBus（唯一取样点：输出频谱 + 复制给监听环形缓冲）
      ├→ 软限幅 → 单声道转立体声 → 主输出播放器（主动拉取，驱动整条链）
      └→ 监听环形缓冲 → 监听播放器（独立拉取）
```

> **输出总线为什么必须是"无源"的**：主输出与监听输出**共用同一个取样点**。
> 早期实现里取样点的上游会被"换成监听链的限幅器"，结果主输出的拉取被挂到监听链上，
> 而监听链没有人在拉 → **整条输出链路断流**（表现为：输入频谱正常、输出频谱无反应、
> MIXLINE 与监听都没有声音）。现在 `OutputBus` 不参与任何一条输出链，
> 只由主输出播放器拉动，监听从它复制出的环形缓冲里独立读取。

* **处理链顺序固定**，用户不可拖拽排序；**未启用的模块从链中物理移除**（`DynamicChain` 用单次原子写发布新的不可变数组，音频线程不加锁、不分配）。
* **效果器单例**：混响 / 延迟 / 合唱 / 电音四选一，切换时重建该节点。
  其中**电音是三级串联**：环形调制（`RingModulatorEffect`，产生金属/机器人音色，这是关键）→ 相位声码器变调（−2 至 −8 半音）→ 高频共振峰 EQ。
  只用变调时听起来仅音高改变、缺少机械感，所以环形调制是主力。
* **播放器音频不经过效果链**，仅在输出前以固定 50:50 比例混入；混音后经软限幅（Look-ahead Limiter + tanh 兜底）避免削波。
* **总旁通开关**（工具栏「音频处理」）就是**闭麦开关**：关闭 = 麦克风静音（不管链路里有没有模块），
  打开 = 出声（链路里有模块就处理，一个都没启用就是直通）。因为首次启动默认关闭，**程序启动时麦克风是静音的**，
  需要先打开这个开关才会往 MIXLINE 送声音；播放器（语音包）不受它影响。
* **频谱**：FFT 1024 点 + Hann 窗，30 fps 仅在界面可见时渲染；采集回调只做定长拷贝，不在音频线程里分配内存或加锁。窗口最小化/隐藏时暂停渲染，音频链路全速运行。
* **实时性**：`WithMmcssThreadPriority("Pro Audio")`、共享模式、低延迟与原始模式（RAW）**逐级降级**（端点不支持 RAW 时自动回退，不会导致引擎启动失败）。
* **降噪实现**（§3.2/3.5.4）：480 samples（10 ms）一帧；内置 STFT 过减谱减 + 维纳增益 + 谱底噪内核；
  「干湿比」按 `out = dry × (1 − wet) + denoised × wet` 混合；单帧推理超过 50 ms 会记录日志并跳过该帧。
  模型下拉框列出 `%LocalAppData%\MicMate\models\*.onnx`，模型被删除时自动回退到默认模型。

### 降噪模型支持哪些格式

载入时会自动识别模型形态（见 `Denoise/ModelCatalog.cs`）：

| 形态 | 张量形状 | 说明 | 代表模型 |
|---|---|---|---|
| **A. 增益曲线域** | 输入 `[*,481]` 对数幅度谱 → 输出 `[*,481]` 逐频点增益(0–1) | 本项目的内置训练器产出 | 自训练模型 |
| **B. 波形域** | 输入 `[1,480]` 波形帧 → 输出 `[1,480]` 降噪波形 | 48 kHz / 10 ms 帧 | 少数波形域导出 |
| **C. 频谱域流式** | 输入 `[1,...,bins,2]` 复频谱 + 循环状态 → 输出同形状 | 参数全部从模型 ONNX 元数据自读；**支持任意采样率与任意组数的状态张量** | **DPDFNet、GTCRN、PureVox 等** |

#### 频谱域支持细节（`Denoise/DpdfNetDenoiseModel.cs`）

* 参数全部读元数据：`n_fft` / `hop_length` / `window_type` / `sample_rate` / 状态初值；
  键名**大小写不敏感**（DPDFNet 小写、PureVox 大写都能读）
* 窗函数按 `window_type` 选择：`vorbis`（DPDFNet）与 `hann_sqrt`（GTCRN）；乘积需满足 COLA
* **复数维位置自适应**：DPDFNet 是 `[1,1,481,2]`、GTCRN 是 `[1,257,1,2]`，
  按模型声明的形状算出频点维/复数维步长后填充，不写死布局
* **循环状态组数自适应**：DPDFNet 1 组、GTCRN 3 组 cache、PureVox 4 组；
  按「输入名 → 同名 `_out` 输出」自动配对并逐帧回传
* **采样率自适应**（`Denoise/RateConverter.cs`）：处理链固定 48 kHz，
  模型非 48 kHz 时先降到模型采样率推理、再升回 48 kHz，并按模型自己的 hop 分帧
* FFT 用自写**混合基 FFT**（960 = 2⁶×3×5 不是 2 的幂，radix-2 不适用）

实测（`tools\dev\DpdfProbe`，语音段越接近 0 越好、噪声段越负越好）：

| 模型 | 采样率 | 单核实时占用 | 全机 CPU | 语音段 | 噪声段 |
|---|---|---|---|---|---|
| **gtcrn_simple** | 16 kHz（经重采样） | **9.3%** | **6.5%** | −1.8 dB | +3.6 dB |
| dpdfnet2_48khz_hr | 48 kHz 原生 | 32.3% | 6.3% | −1.0 dB | +7.9 dB |

> **GTCRN 是更合适的选择**：单核实时占用只有 DPDFNet 的三分之一，噪声抑制也更明显。
> 16 kHz 是语音降噪模型的生态主流，算力需求本来就低。

#### ⚠️ 必须限制 ONNX 推理线程数（曾导致 CPU 占用 50%+）

**症状**：开启降噪后 CPU 占用 50% 以上，远高于同功能的其它软件（约 2%）。

**原因**：`InferenceSession` 构造时**没有传入 `SessionOptions`**，于是
`IntraOpNumThreads` / `InterOpNumThreads` 的设置完全没生效，
ONNX Runtime 默认开线程池把所有核心拉满。实测**等效单核占用 949%**
（CPU 时间是墙钟的 9.5 倍），并且线程调度抖动会直接影响实时音频。

**修法**：构造会话时显式传单线程选项（与官方参考实现 `onnx_backend.py` 一致）：

```csharp
var sessionOptions = new SessionOptions
{
    IntraOpNumThreads = 1,
    InterOpNumThreads = 1,
    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
};
_session = new InferenceSession(path, sessionOptions);
```

**效果**：全机 CPU **59.3% → 6.5%（约 9 倍）**，CPU 时间从墙钟的 9.5 倍降到 1.0 倍。
模型校验路径（`ModelCatalog`）也走同一个 `CreateSession`，避免同样问题。

同时把每帧的状态张量改为**预分配复用**（原先每帧新建 `DenseTensor`，
100 帧/秒下持续产生 GC 压力）。剩余分配来自 ONNX 每帧返回结果张量，实测不影响实时性。

**已知问题**：噪声段仍是**放大**而不是衰减（应为负值）。说明频谱尺度或状态初值仍有偏差，
需要用官方实现的中间张量逐级比对来定位。
**PureVox 系列未经验证**——零元数据（窗口类型、采样率全靠推断），且其目录里混有
**回声消除（`aec*`）与目标说话人提取（`tse*`）模型，那些不是降噪模型，不要放进降噪列表**。
（实测不行的 `v9_fft2048_band256` 已从模型目录移除。）

#### 保底模型（无外部模型时使用）

`Dsp/SpectralDenoiseModel.cs`。**定位**：保证"总有可用的降噪"，不追求高阶算法。
三级结构，各段都经逐样本验证：

1. **二阶 Butterworth 高通（120 Hz）**，标准 RBJ 双二阶系数
2. **全频带向下扩展器**（dB 域）：低于门限才衰减，高于门限必然直通
3. **32 频带频带门控**（在 12 kHz 降采样域算分频带增益，作用回 48 kHz 信号）

实测（语音段越接近 0 越好，底噪越负越好）：

| 强度 | 语音 | 底噪（宽带沙沙声） |
|---|---|---|
| 0 | 0 dB | 0 dB |
| 40 | −1.0 dB | −9.7 dB |
| 70 | −1.3 dB | −27.7 dB |
| 100 | −3.1 dB | −50.7 dB |

> **为什么加第 3 级**：单频带门控对"麦克风电流声/沙沙声"这类**宽带稳态**噪声压不干净——
> 它与语音频带重叠，只能整体压低。分频带后，语音占主导的频带保持直通、
> 噪声占主导的频带被压下去。

#### 降噪相关的已踩坑清单（保留以防重复）

1. **漏积分器式高通不可用**：`state += (x-state)*α; y = x-state` 在 α→1 时极点趋近单位圆、
   对正弦几乎全通，但数值上极敏感，被直流扰动推偏后会把整个信号吃掉
   （实测 α=0.984 时 −65 dB）。**必须用标准 RBJ 双二阶。**
2. **Hann 窗只在 50% 重叠下满足 COLA**（Σ窗≡1）。早期用 75% 重叠 → 块边界幅度起伏，听感"卡"。
3. **分析 + 合成各加一次窗 ⇒ 总窗形 Hann²**，50% 重叠下 Σ Hann² ≡ 3/8，必须除以它
   （否则电平放大 8/3 倍 → 削波失真）。
4. **启动顺序**：`RefreshModels()` 必须**先于**最后一次 `UpdateAllParameters()`。
   否则处理链按"默认模型"构建、之后不再重建，表现为**重启后降噪不生效、
   必须手动重选一次模型**（甚至只是点开一下下拉框就生效）。

### 启动时序（重要：影响"降噪到底有没有生效"）

启动顺序必须是：

1. 回填配置 → 2. `RefreshModels()` 载入配置里指定的模型 → 3. `_engine.Start()` → 4. **启动静音** → 5. 再次套用模型与参数 → 6. **解除启动静音**

两个容易踩的坑：

* **`RefreshModels()` 必须先于最后一次 `UpdateAllParameters()`**。否则处理链按"默认模型"构建且之后不再重建，
  表现为**重启后降噪不生效、必须手动重选一次模型**（点开下拉框也会意外触发一次生效）。
* **启动静音**：链路刚建好时用的还不是配置里的模型，这段时间会输出未降噪的底噪。
  因此 `Start()` 后立刻进入启动静音（`AudioEngine.StartupMute`），模型套用完成才放开。

启动日志应长这样（可用于确认是否正常）：

```
模型扫描完成：3 个外部模型，耗时 348 ms
麦克风输出：静音（启动中，等待降噪模型就绪）
麦克风输出：开启（AI 降噪 → 响度平衡）
启动静音解除，恢复麦克风输出
引擎启动后重新应用：降噪模型=dpdfnet2_48khz_hr，配置=dpdfnet2_48khz_hr
[状态] 降噪已启用：dpdfnet2_48khz_hr
运行期校验：降噪 Enabled=True，链中包含=True，已处理 201 帧，被绕过 0 样本（模型 dpdfnet2_48khz_hr）
```

### ⚠️ 模型扫描必须缓存（否则点下拉框会卡死）

`ModelCatalog.Scan()` 会为**每个**模型创建一次 `InferenceSession` 来校验张量形状。
dpdfnet8 有 15 MB、dpdfnet2 有 10 MB，在 UI 线程上同步做这件事会造成明显卡顿——
用户反馈"点击模型选择下拉框就卡死"正是这个原因。

修法：`Scan()` 结果进缓存，只在首次、训练完成或显式 `InvalidateCache()` 时才真正扫描；
同时记录扫描耗时（正常约 350 ms）。

### 下拉框箭头

工具栏三个设备下拉框与模型下拉框的箭头，样式与模块箭头一致：
**收起朝左、展开旋转 90° 朝下**（`FlatCombo` 模板里的 `Arrow` + `IsDropDownOpen` 触发器）。
注意 `RotateTransform` 不能直接作为 `Setter.TargetName`，必须给整个 `RenderTransform` 赋值。

**教训**：NWaves 一直就在依赖列表里，而它**已经有** `Transforms.Stft`（完美重建）、
`Operations.Tsm.PhaseVocoder`、`Effects.RobotEffect`、`Audio.WaveFile`。
前期我却在自研 FFT / STFT / 相位声码器，连续多轮没做对。现已全部改用 NWaves。

**实测验证**（`tools\dev\NWavesProbe`）：

| 组件 | 结果 |
|---|---|
| `NWaves.Transforms.Stft` 完美重建 | 平均误差 **5e-7**、RMS 一致 ✅ |
| `NWaves.Operations.Tsm.PhaseVocoder` | 正常工作 ✅ |
| `NWaves.Effects.PitchShiftEffect` | ❌ 传半音会溢出异常、传比率结果不正确（**不要用**） |
| `NAudio.Effects.PitchShiftEffect` | ❌ 0 半音就把 355 Hz 变成 71 Hz（**不要用**） |

**变调方案**（`Dsp/PitchShifter.cs`，已接入并实测）：
用可靠的积木自己组合 —— `PhaseVocoder` 时间伸缩 ratio 倍 → 线性插值重采样回原长度。
内部攒够 8192 样本再处理（块太小会导致声码器失效，这是踩过的坑）。
实测偏差 **2–6 音分**（+5 半音 → 475.2 Hz / 期望 473.9，−5 半音 → 266.7 / 265.9）。

**电音（硬调音）已可用**：`Dsp/HardTuneEffect.cs` + `Dsp/PitchDetector.cs`。
基频检测 → 吸附到 C 大调音级 → 上述变调 → 电子化音染。自检结果：

```
输入 415 Hz → 输出 440.4 Hz（音级 440.0，偏差  1.4 音分）✓
输入 355 Hz → 输出 347.8 Hz（音级 349.2，偏差 -7.0 音分）✓
输入 470 Hz → 输出 494.8 Hz（音级 493.9，偏差  3.4 音分）✓
```

> `PitchDetector` 曾有一个关键 bug：自相关取"全局最大峰"会在倍数周期误判
> （355 Hz 测成 71 Hz），必须取**第一个足够高的峰**。这个错误也存在于我早期的
> 两个自检工具里，导致前几轮的结论不可信——现已全部修正。

### 频谱域模型（下一步）

STFT 地基已由 NWaves 提供并验证通过，接下来要做的是：

1. 按模型声明的 `bins` 推 FFT 长度（DPDFNet 481→960、GTCRN 257→512、PureVox 1025→2048），
   用 `NWaves.Transforms.Stft` 做分析与合成
2. 管理模型的循环状态张量（GTCRN 3 组 cache、DPDFNet 1 组 state、PureVox 4 组）
3. 用 `tools\dev\OnnxInfo` 先确认形状，再逐模型接入

**注意**：GTCRN 是 16 kHz 模型，接入需要额外的重采样层。

### 炸麦（劣质对讲机）

`Dsp/MegaphoneDistortionEffect.cs`。听感链条：

1. **强压缩**（比率 8–22、快攻慢放慢恢复）——响度大时被"压扁"，
   增益骤降产生**呼呼的风声/抽气感**，这是劣质对讲机的核心特征
2. **硬削波**——过载失真
3. **带通**（260–400 Hz 高通 + 2.2–4 kHz 低通）——对讲机的窄频响，闷且薄
4. **量化降位**（5–12 bit）——廉价 ADC 毛刺
5. **嘶嘶背景噪音，只在有人声时出现**——没人声时完全安静（用输入电平做人声检测）

> 压缩器用递推式实现，避免逐样本 `pow`：
> 压缩曲线 `y = x^(1/ratio)` ⇒ 增益 `g = x^k`（`k = 1/ratio − 1`），
> 对 `ln x` 做快攻慢放平滑再取指数。

实测改变量 0.383（此前纯削波版本为 0.181）。

### 音色风格为什么"听不出区别"（已修）

实测各风格对信号的平均改变量只有 **0.026–0.051**（对比效果器是 0.16–0.30），
因为原预设只给 ±1.5–3 dB 的 EQ 增益——在真实语音上几乎听不出来。

已把增益提到 **±4–8 dB**，实测改变量变为：

| 风格 | 改前 | 改后 | 电平变化 |
|---|---|---|---|
| 清亮 Bright | 0.026 | **0.062** | −3.0 dB |
| 沉稳 Warm | 0.047 | **0.171** | +4.9 dB |
| 深邃 Deep | 0.051 | **0.149** | +2.8 dB |
| 自然 Natural | 0 | 0 | 0（按设计直通） |

### 训练模型功能：建议**保留但不必投入**

现状：内置训练器产出的是**增益曲线域**（形态 A）的"噪声画像"——它只是给谱减算法
提供一组逐频点过减倍率，本身不是神经网络，效果上限明显低于 GTCRN / DPDFNet。

**结论**：
* 你已有 GTCRN（16 kHz，<1% 占用）与 DPDFNet（48 kHz）两个**实测可用**的模型，
  它们在各方面的表现都优于内置训练器能产出的东西。
* 因此**训练功能对当前使用没有增益**，但作为"无模型可用时的自训练兜底"仍有价值，保留即可。
* 若将来想去掉，可一并移除 `TrainingService` / `NoiseProfileTrainer` / `tools/train_denoise.py`
  以及界面上的训练面板与 `--selftrain` 命令。
### 频谱域与电音：已改用 NWaves 现成模块

内置默认模型是**谱减 + 维纳增益**的经典实现：它不需要任何模型文件，能压掉稳定底噪，
但对非平稳噪声和音乐噪声的处理远不如神经网络模型——**想要好效果请放一个波形域模型进去**。

**尚未完成 / 待后续补充**

* **Python 训练路径未在本机实测**：本机 Python 是 Microsoft Store 的应用执行别名（未安装真实解释器），
  且计划中的内嵌 CPython 下载源在当前网络环境下不可达。因此
  `Denoise/PythonRuntime.cs`（检测 3.10+、NuGet 预编译 CPython 解压到 `%LocalAppData%\MicMate\runtime/`）
  与 `tools/train_denoise.py` 均已按接口写好，但**未经端到端验证**。
* 「降噪强度」对增益曲线域模型体现在过减系数上；对波形域模型目前只作用于干湿比，未做逐模型标定。

---

## 7. 全局快捷键

* 使用 `RegisterHotKey` + `HwndSource.AddHook` 捕获 `WM_HOTKEY`，带 `MOD_NOREPEAT` 防止长按重复触发。
* 点击工具栏「全局快捷键」再按组合键即可绑定总开关热键；点击音频列表右侧的「点击设置快捷键」为单个文件绑定热键。
* `Esc` 取消录入，`Backspace`/`Delete` 清除绑定。
* 限制与提示：热键必须含至少一个非修饰键；禁止 `Win+` 组合；注册失败（错误码 1409）会提示
  “快捷键 {组合} 已被其他程序占用，请更换”；全屏独占应用可能屏蔽热键（规划书已注明）。

---

## 8. 配置与数据目录

```
%LocalAppData%\MicMate\          ← 默认数据根目录（非漫游）
├── config.json          # 全局配置（设备、效果链参数、播放列表、窗口位置等）
├── models/              # ONNX 模型（内置训练器与外部模型都放这里）
├── recordings/          # 录制的底噪样本 noise_yyyyMMdd_HHmmss.wav
├── runtime/             # 内嵌 Python 运行时（按需下载）
└── logs/                # 按天滚动的日志，保留最近 7 天
```

**为什么放在 `%LocalAppData%` 而不是规划书写的 `%AppData%`**：`%AppData%`（Roaming）会被域/企业环境的
漫游配置文件同步到服务器，而这里放的是 ONNX 模型（二进制）、录音 WAV、日志，以及**内嵌 Python 运行时**
（几千个文件 + 原生 DLL）——都不该漫游，否则会拖慢登录、吃掉漫游配额。规划书 6.2 提到单文件发布时
也是改用 `%LocalAppData%`，这里直接统一为同一路径。

启动时会自动把 0.1 版遗留在 `%AppData%\MicMate` 下的配置、模型与录音迁移过来（只搬缺失的文件，不覆盖）。

目录选择顺序（逐级回退，保证程序始终能启动）：

1. `%LocalAppData%\MicMate` —— 默认
2. `%AppData%\MicMate` —— LocalAppData 不可用时
3. `%TEMP%\MicMate` —— 最后兜底

也可用 `--appdata <目录>` 显式指定（便携 / 调试）。

---

## 9. 工程结构

```
MicMate/
├── App.xaml(.cs)              应用入口、单实例、全局异常、自检入口
├── MainWindow.xaml(.cs)       三栏主界面与全部交互
├── SelfTest.cs                离线自检（训练链路 / 渲染校验）
├── GlobalUsings.cs
├── NuGet.config               NuGet 源（国内镜像）
├── Core/
│   ├── AppConfig.cs           配置模型
│   ├── ConfigStore.cs         配置读写 + 数据目录回退
│   ├── Log.cs                 按天滚动日志
│   ├── DeviceService.cs       WASAPI 设备枚举 + 热插拔通知 + MIXLINE 检测
│   ├── HotkeyService.cs       全局热键注册/解析/格式化
│   └── AutoStartService.cs    开机自启（HKCU Run）
├── Audio/
│   ├── AudioEngine.cs         音频图：采集 → 效果链 → 混音 → 主/监听输出
│   ├── AnalysisProviders.cs   分析采样点、监听复制、声道转换
│   ├── PlayerSampleBuffer.cs  播放器环形缓冲
│   └── FilePlayerService.cs   音频文件播放（Media Foundation 解码 + 重采样）
├── Dsp/
│   ├── IAudioEffect.cs        效果器接口 + 可在线重建的处理链
│   ├── DenoiseModels.cs       降噪后端（谱减内核 + ONNX 画像后端）
│   ├── DenoiseEffect.cs       AI 降噪节点（10 ms 分帧、干湿比、超时跳过）
│   ├── NoiseGateEffect.cs     噪声门
│   ├── LoudnessBalanceEffect.cs 响度平衡（400 ms RMS + 指数平滑 + 防削波）
│   ├── ToneStyleEffect.cs     六种音色风格预设 EQ
│   ├── CreativeEffect.cs      混响/延迟/合唱/电音（单例）
│   ├── OutputStage.cs         增益、软限幅、单声道转立体声、频谱分析
│   ├── MicMixer.cs            麦克风 + 播放器 50:50 混合总线
│   └── SmoothedGain.cs        指数平滑增益与 dB 工具
├── Denoise/
│   ├── ModelCatalog.cs        模型扫描 / ONNX 校验 / 曲线提取
│   ├── NoiseProfileTrainer.cs 内置训练器（不依赖 Python）+ ONNX 导出
│   ├── OnnxProtoWriter.cs     极简 ONNX protobuf 写入器
│   ├── NoiseRecorder.cs       10 秒底噪录制
│   ├── PythonRuntime.cs       Python 检测与自动安装（未实测）
│   └── TrainingService.cs     训练流程编排（内置 / Python 双路径）
├── Ui/Theme.xaml              浅色扁平主题（开关、滑块、按钮、下拉框、芯片按钮）
├── ViewModels/TrackViewModel.cs
└── tools/train_denoise.py     Python 训练脚本（与内置训练器接口一致）
```

`MicMate.Windows/`（独立 WinForms 程序集）只提供托盘图标，避免 WPF 工程同时引入
`System.Windows.Forms` 与 `System.Windows` 造成类型歧义。

---

## 10. 已知限制

* 音频链路在上游设备本身是立体声时会先下混为单声道（规划书要求处理链全程单声道），
  单声道→立体声的扩展在输出前完成。
* 采集缓冲区为 80 ms（WASAPI 共享模式的安全区间），端到端延迟受系统混音引擎影响，
  实测输出侧约 7 ms；低延迟采集需要端点混音格式与请求格式一致，当前请求单声道 float 时会走标准共享模式。
* 部分虚拟声卡端点不支持 WASAPI RAW 模式，程序会自动逐级降级并在日志中记录原因。
* 规划书 §3.7.1 关于总开关的描述与语音包行为冲突，本项目按“闭麦但不静音语音包”实现（见 §5）。
* 未实现规划书 §3.8 的后续扩展（手机 USB 串流、实时变声、多效果器串联、VST）。
* 按规划书 §3.8 明确不添加 TSE（目标说话人提取）与 AEC（回声消除）。

---

## 11. 许可

MIT。

---

## 12. 项目书（v2.2）问题清单与建议

项目书是 AI 写的，实现过程中发现以下问题，按重要性排列。**加粗的已在代码里按我的判断处理**。

### 会导致实现不出正确结果（高）

| # | 项目书原文 | 问题 | 处理 |
|---|---|---|---|
| 1 | §6.3 数据目录 `%AppData%/MicMate/` | Roaming 会被域环境同步；模型是二进制、内嵌 Python 是几千个文件，都不该漫游 | **改为 `%LocalAppData%\MicMate` + 自动迁移**，见 §8 |
| 2 | §3.7.1 总开关「关闭后停止音频处理并停止输出，可作为麦克风一键开关」 | 停止输出会连语音包一起静音，与 §3.4 的语音包用途直接冲突：用户闭麦后语音包就废了 | **实现为「麦克风静音，播放器照常送出」**，见 §5 |
| 3 | §3.2「未启用的效果器从处理链中移除，而非简单跳过」 | 字面读是要求 true bypass，与前半句「每个模块可独立启用/禁用」的语义冲突 | 按「物理移除节点」实现；旁通交由各效果自身的 bypass 处理 |
| 4 | §3.3 频谱「暂停时不再调用 `Dispatcher.Invoke`」 | 不该在音频线程调用 UI，与 §4.4 自己的规定矛盾 | 改为音频线程只做定长拷贝进无锁环形缓冲，UI 线程按帧率取走 |
| 5 | §3.2.4 音色预设只给了频率区间，没给频率/增益/Q 的具体值 | 无法直接实现，只能自行编 | 自行编了 6 组峰值滤波器参数，见 `Dsp/ToneStyleEffect.cs` |
| 6 | §5.2「模型输入形状是否与预期匹配（如 `[1,480]` float32）」 | 与 §3.5.4 的「480 samples 波形帧」对应，这要求模型必须是波形域模型；但项目书没规定训练脚本导出什么模型 | 本项目自行约定为「481 频点对数幅度谱 → 481 频点增益」，`ModelCatalog` 会按此校验并给出具体错误 |

### 技术上不准确（中）

| # | 项目书原文 | 问题 | 处理 |
|---|---|---|---|
| 7 | §1.3「C# / .NET 8」+「NAudio 3」 | **二者不可兼得**：NAudio 3.x 最低要求 `net9.0`。而 `WasapiPlayer`/`WasapiPlayerBuilder`/`WithMmcssThreadPriority`/`WithRawMode` 这些 API 只存在于 NAudio 3 | 工程改为 `net9.0-windows`（本机只有 9.0.301 SDK） |
| 8 | §2.2 直接使用 `.WithLowLatency()`、`.WithRawMode()` | 两者都会在**端点不支持时抛 `CoreAudioException`**（部分虚拟声卡就是这样），照抄会导致引擎启动失败 | **实现四级降级**：低延迟+RAW → RAW → 低延迟 → 标准共享，见 `Audio/AudioEngine.cs` |
| 9 | §2.1「采样率固定 48000 Hz」 | WASAPI 共享模式下采集格式必须兼容设备混音格式，强行要求 48k 单声道会让低延迟模式静默失效 | 请求 48k 单声道 float，失败时由引擎自动转换 |
| 10 | §2.1「位深 32-bit float」 | 输出端无法与「任意声卡」兼容 | 内部 32f，输出前转设备支持的格式 |
| 11 | §3.2.2「50:50 固定混合」+「软限幅」 | 语音包与麦克风各 0.5 后，语音包实际听感偏小 | 播放器另给独立音量（0–2 倍），混音比保持 50:50 |
| 12 | §3.4.1「热键数量有限（系统限制约 100 个）」 | 实际限制是**每个进程约 1 万个 ID**，不是 100 | 未做人为限制 |
| 13 | §3.5.1「默认不安装 Python，首次训练时下载预编译 CPython」 | ① 现代 CPython 的 NuGet 包**不带 pip**，`numpy`/`onnx` 仍要联网装；② 内嵌运行时装进 Roaming 目录更不合适；③ 「预计训练时间 20–90 分钟」暗示要跑重训练，实际本项目 10 秒就完成 | **改为内置 C# 训练器，零 Python 依赖**；Python 脚本保留为进阶路径（按住 Ctrl 点「训练模型」） |
| 14 | §6.2「不默认捆绑 Python；首次训练时按需下载内嵌运行时」 | 与 §3.5.1 重复描述同一件事，且两处措辞不一致 | 已在实现中合并为一条路径 |
| 15 | §3.5.3「预计训练时间根据 CPU 型号、核心数、内存自动估算」 | 没有 Python 训练时，估算值（20–90 分钟）与真实耗时（秒级）差两个数量级，会误导用户 | 估算改为约 3–11 分钟量级并标注为粗略值 |

### 缺失或可以更好的（低）

| # | 问题 | 建议 |
|---|---|---|
| 16 | 没有规定**效果器 DSP 算法**（混响/延迟/合唱/电音具体怎么做） | 已直接采用 NAudio 3 自带的 `NAudio.Effects` 内核（Freeverb、延迟线、相位声码器），比自研更稳 |
| 17 | 没有规定音色 EQ 的三段具体参数 | 见第 5 条，已自行定义 |
| 18 | §8.2 要求「连续运行 72 小时」「Windows 10/11 + 不同 MIXLINE 版本兼容性测试」 | 这类验收项在单人开发 + 单机环境下无法完成，建议降级为「自检脚本 + 手动冒烟测试」 |
| 19 | §9 开发计划的验收标准偏笼统（如「能看到麦克风列表」） | 建议补上可自动化验证的判据（本项目用 `--selfcheck` / `--selftrain` 覆盖了渲染与训练两条链路） |
| 20 | §3.8 明确不添加 AEC / TSE | 合理，保留 |
| 21 | §7 性能目标给了具体 CPU 百分比 | 没有说明测量方法（采样周期、是否含频谱渲染），难以判定达成与否 |
