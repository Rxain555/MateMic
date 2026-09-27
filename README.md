# MateMic

> 给游戏与语音通话用的 Windows 实时麦克风处理工具：降噪、响度平衡、变声与音效，处理完的话筒信号再交给虚拟声卡（MIXLINE / VB-Cable）给队友听。

MateMic 常驻系统托盘，把物理麦克风的声音做一遍实时处理，再送到虚拟声卡上，这样游戏、Discord、OBS 里选到的话筒就是你处理后的声音。全部处理在本机完成，**不联网、无账号、无遥测**。

> 项目原名 **MicMate**，因与某硬件产品重名存在法律风险而改名为 **MateMic**。

---

## 功能

**音频处理链**（按顺序，每个模块可单独开关，未启用的模块会从链上物理移除）

| 模块 | 说明 |
|---|---|
| 噪声门 | 底噪抑制，阈值与释放时间可调 |
| AI 降噪 | ONNX 模型实时推理，支持 48 kHz 与 16 kHz 模型；内置 3 个模型，也可自行放入 `.onnx` |
| 响度平衡 | 自动增益，把语音稳定到目标响度 |
| 音色风格 | 清亮 / 沉稳 / 深邃 / 尖锐 / 空灵 / 自然 |
| 效果器 | 混响 / 延迟 / 合唱 / 电音 / 炸麦 |
| 增益 | 手动增益 −24 ~ +24 dB |

**设备与监听**

- 输入 / 输出 / 监听**三个设备分别选择**，各自记忆
- **设备热插拔自动恢复**：麦克风拔掉再插回会自动重连，掉线期间用系统默认设备顶着
- 输入与输出双频谱（48 段对数频带）+ 渐变电平条，实时看清信号

**播放器**

- 伴奏 / 语音包列表，每个条目可绑定全局播放快捷键，也可以「同步按住键」（播放时自动按下某个键、结束自动松开，用于游戏按键说话）

**其它**

- 全局快捷键一键开关音频处理，带冲突检测
- 开机自启（静默收进托盘）
- 关闭到托盘
- 自绘标题栏 + 亚克力材质 + 扁平化界面
- 配置、模型、录音、日志全部放在程序目录下的 `data\`，**绿色便携**

## 界面

![MateMic 主界面](MateMic/tools/dev/ui-preview-modern-acrylic.png)

## 快速开始

### 1. 安装

- **安装程序**：运行 `MateMic-<版本>-setup.exe`，按向导完成（默认装到 `%LocalAppData%\Programs\MateMic`，不需要管理员权限）
- **绿色版**：解压 `MateMic-<版本>-portable.zip` 到任意目录，双击 `MateMic.exe`

需要 **Windows 10 1809 及以上**，以及 **.NET 9 桌面运行时**（框架依赖发布；缺失时程序不会启动，去 [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/9.0) 装 Desktop Runtime 9.x x64）。

### 2. Windows 声音设置

把 **MIXLINE Stream** 设为默认输入设备。

### 3. 设备选择

| 项 | 选什么 |
|---|---|
| 输入 | 实际在用的物理麦克风 |
| 输出 | `扬声器 (MIXLINE)` |
| 监听 | 实际在用的物理扬声器 / 耳机（不用监听可以不选） |

### 4. MIXLINE 中

- 添加输入：`MateMic`
- 添加输出：`MIXLINE Stream`
- 把 `MateMic` 节点连接至 `MIXLINE Stream` 节点

> MateMic **不自己创建虚拟声卡**，必须配合 MIXLINE（或 VB-Cable）使用。装完程序后点界面右上角的**「使用指南」**按钮，里面就是上面这几步。

### 5. 开始使用

打开「音频处理」总开关，左侧按需开启各模块。

## 降噪模型

程序目录下的 `models\` 里**随包内置**了 3 个模型，装完即可在「AI 降噪 → 模型」里选：

| 模型 | 采样率 | 大小 |
|---|---|---|
| `dpdfnet2_48khz_hr` | 48 kHz | 10.1 MB |
| `dpdfnet8_48khz_hr` | 48 kHz | 14.2 MB |
| `gtcrn_simple` | 16 kHz | 0.5 MB |

也可以把自己的 `.onnx` 放进 `models\`（或 `data\models\`），重启后即出现在下拉框里。已实测支持 16 kHz 与 48 kHz 的频谱域流式模型（GTCRN / DPDFNet / PureVox 系）。

## 从源码构建

```bat
:: 构建（必须串行）
dotnet build MateMic\MateMic.sln -c Debug -m:1

:: 构建并运行
dotnet run --project MateMic\MateMic.csproj
```

也可以直接用 Visual Studio 打开 `MateMic\MateMic.sln`（会同时加载 `MateMic` 与 `MateMic.Windows` 两个工程）。

依赖：NAudio 3.1.0、NWaves 0.9.5、Microsoft.ML.OnnxRuntime 1.20.1。

### 打包

```powershell
.\打包\make-package.ps1              # 绿色版
.\打包\make-package.ps1 -WithSetup   # 顺便编译安装程序（需要 Inno Setup 6）
```

产物输出到仓库目录的上一级 `发布包\`（绿色 zip 与安装程序）。`-WithSetup` 需要本机已安装 Inno Setup 6；未安装时脚本会自动退化成只出绿色包。

## 数据存放位置

全部在**程序目录下的 `data\`**，不往系统盘写文件：

```
data\
  config.json          配置
  models\              自己放的降噪模型
  recordings\          录音
  logs\                日志
  config-backups\      配置自动备份（最多 20 份）
```

⚠️ 数据目录在程序目录内，所以**不要删除或 `dotnet clean` 构建输出目录**，否则配置会跟着没。数据目录不可写时（例如装在 `Program Files`）程序会退到 `%LocalAppData%\MateMic`。

## 已知限制

- 只支持 Windows（依赖 WASAPI）
- **不自己创建虚拟声卡**，必须配合 MIXLINE 或 VB-Cable
- 波形域降噪模型只支持 48 kHz / 每帧 480 样本；16 kHz 模型需为频谱域形态
- 窗口固定 1180×800，不可拉伸（三栏布局按此宽度设计）
- 「同步按住键」使用 `SendInput` 注入按键，**理论上存在被反作弊判定的风险**，因此默认关闭并附风险说明

## 许可

本项目采用 [MIT 协议](LICENSE)。

## 致谢

- 虚拟声卡方案：[MIXLINE](https://www.logitech.com/) / [VB-Cable](https://vb-audio.com/Cable/)
- 降噪模型：[DPDFNet](https://github.com/ceva-ip/DPDFNet)、[GTCRN](https://github.com/Xiaobin-Rong/gtcrn)
- 音频库：[NAudio](https://github.com/naudio/NAudio)、[NWaves](https://github.com/ar1st0crat/NWaves)、[ONNX Runtime](https://onnxruntime.ai/)
- 对标参考：[MeowMic](https://github.com/NanCheng-L/MeowMic)
