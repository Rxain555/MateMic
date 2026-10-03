using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace MateMic.Windows;

/// <summary>
/// 系统托盘图标。放在单独的 WinForms 程序集里，避免 WPF 工程同时引入
/// System.Windows.Forms 与 System.Windows 造成的类型歧义。
/// 图标使用嵌入的 Assets\appicon.ico，与程序 exe 图标是同一份。
///
/// 菜单：**本类不再负责菜单外观**。右键只上报 <see cref="MenuRequested"/>，
/// 由 WPF 侧自绘 Windows 11 风格的菜单（<c>Ui\TrayMenuWindow</c>）。
/// 早先用 WinForms 的 <c>ContextMenuStrip</c>（ToolStrip 渲染：方角、左侧图标栏、
/// 旧配色），既做不出圆角/亚克力，也跟不上应用的深色模式；自绘渲染器又出过
/// "两层背景"的 bug，因此改为把菜单做进 WPF 那一侧（那边本来就有完整的主题体系）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private NotifyIcon? _icon;

    /// <summary>要求显示主窗口（双击托盘图标）。</summary>
    public event EventHandler? ShowRequested;

    /// <summary>右键点了托盘图标：请求弹出菜单（位置由 WPF 侧取当前光标）。</summary>
    public event EventHandler? MenuRequested;

    public void Show(string tooltip)
    {
        if (_icon != null) return;

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = tooltip.Length > 62 ? tooltip[..62] : tooltip,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>从嵌入资源读取图标；资源缺失时退回运行时绘制的兜底图标。</summary>
    private static Icon LoadIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("MateMic.Windows.appicon.ico");
            if (stream != null) return new Icon(stream);
        }
        catch
        {
            // 使用下面的兜底图标
        }

        return BuildIcon();
    }

    public void Notify(string title, string message, bool warning = false)
    {
        try
        {
            _icon?.ShowBalloonTip(4000, title, message,
                warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch
        {
        }
    }

    public void Hide()
    {
        if (_icon == null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }

    /// <summary>运行时生成 32×32/32bpp 图标（蓝色圆底 + 白色麦克风剪影），无需外部资源文件。</summary>
    private static Icon BuildIcon()
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var index = (y * size + x) * 4;
                var dx = x - 15.5;
                var dy = y - 15.5;
                var distance = Math.Sqrt(dx * dx + dy * dy);

                if (distance > 15.2)
                {
                    pixels[index + 3] = 0;
                    continue;
                }

                var insideMic =
                    (x >= 13 && x <= 18 && y >= 7 && y <= 18) ||
                    (y >= 19 && y <= 22 && Math.Abs(x - 15.5) <= 4.6) ||
                    (y >= 22 && y <= 25 && Math.Abs(x - 15.5) <= 1.2) ||
                    (y >= 25 && y <= 27 && Math.Abs(x - 15.5) <= 5.5);

                if (insideMic)
                {
                    pixels[index] = 255;
                    pixels[index + 1] = 255;
                    pixels[index + 2] = 255;
                }
                else
                {
                    pixels[index] = 237;      // B
                    pixels[index + 1] = 128;  // G
                    pixels[index + 2] = 47;   // R
                }

                pixels[index + 3] = 255;
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);      // reserved
        writer.Write((ushort)1);      // type = icon
        writer.Write((ushort)1);      // count

        writer.Write((byte)size);
        writer.Write((byte)size);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(40 + pixels.Length);
        writer.Write(22);

        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(pixels.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var y = size - 1; y >= 0; y--)
            writer.Write(pixels, y * size * 4, size * 4);

        writer.Write(new byte[size * size / 8]);
        writer.Flush();
        stream.Position = 0;
        return new Icon(stream);
    }

    public void Dispose() => Hide();
}
