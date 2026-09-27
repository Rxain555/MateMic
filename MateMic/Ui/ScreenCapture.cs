using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MateMic.Core;

namespace MateMic.Ui;

/// <summary>
/// 自检用：把"屏幕上的真实窗口"抓成 PNG。
///
/// 为什么需要它：常规自检（<c>--selfcheck</c>）走的是 RenderTargetBitmap，
/// 它只渲染 WPF 的可视树，**看不到 DWM 合成在窗口背后的亚克力材质**，
/// 半透明区域在截图里会"露白"，无法用来确认材质是否真的生效。
/// 这里改为直接从桌面 DC 用 BitBlt 取像素，拿到的是最终合成结果，
/// 磨砂、圆角、投影都如实呈现（前提是窗口没有被别的窗口挡住）。
/// </summary>
internal static class ScreenCapture
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    /// <summary>让窗口自己把内容画到我们的 DC 上（含非客户区的标题栏与系统按钮）。</summary>
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    /// <summary>PW_RENDERFULLCONTENT：要求渲染完整内容（含非客户区）。</summary>
    private const uint PwRenderFullContent = 0x00000002;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    /// <summary>
    /// 抓取窗口在屏幕上的合成结果并保存为 PNG。
    /// <param name="mode">
    /// <c>screen</c>（默认）= 从窗口 DC 取"屏幕上已合成的像素"，能看到亚克力材质；
    /// <c>frame</c> = 让窗口自己绘制（PrintWindow），**标题栏的最小化/关闭按钮在这条路径下一定会被画出来**，
    /// 用于核对"系统按钮到底有没有显示"——屏幕抓取时它们可能因为不被重绘而缺失。
    /// </param>
    /// <returns>是否成功；失败只写日志，不抛出（自检不应把主程序搞崩）。</returns>
    public static bool CaptureWindow(Window window, string targetPath, string mode = "screen")
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Log.Warn("屏幕自检：窗口句柄为空，跳过。");
            return false;
        }

        // 尽量把窗口提到最前，减少被其它窗口遮挡的概率
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            window.Activate();
            // 让 DWM 有机会完成一次合成
            Thread.Sleep(250);
        }
        catch
        {
            // 取前台失败不影响抓图，继续
        }

        if (!GetWindowRect(hwnd, out var rect))
        {
            Log.Warn("屏幕自检：GetWindowRect 失败。");
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            Log.Warn($"屏幕自检：窗口尺寸异常 {width}x{height}。");
            return false;
        }

        var windowDc = IntPtr.Zero;
        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            // 用窗口 DC 作为来源：BitBlt 从它取像素时，得到的是屏幕上该区域
            // 已经合成好的内容，因此能看到亚克力/圆角/投影的真实效果。
            windowDc = GetWindowDC(hwnd);
            if (windowDc == IntPtr.Zero)
            {
                Log.Warn("屏幕自检：GetWindowDC 失败。");
                return false;
            }

            memoryDc = CreateCompatibleDC(windowDc);
            bitmap = CreateCompatibleBitmap(windowDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                Log.Warn("屏幕自检：创建 GDI 位图失败。");
                return false;
            }

            previous = SelectObject(memoryDc, bitmap);

            if (mode.Equals("frame", StringComparison.OrdinalIgnoreCase))
            {
                // PrintWindow 让窗口把"自己这一份"画出来：非客户区（标题栏、系统按钮）
                // 也会跟着绘制，因此只有这条路径能确认按钮画得出来。
                if (!PrintWindow(hwnd, memoryDc, PwRenderFullContent))
                {
                    Log.Warn("屏幕自检：PrintWindow 失败。");
                    return false;
                }
            }
            else if (!BitBlt(memoryDc, 0, 0, width, height, windowDc, 0, 0, SRCCOPY | CAPTUREBLT))
            {
                Log.Warn("屏幕自检：BitBlt 失败。");
                return false;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(targetPath);
            encoder.Save(stream);

            Log.Info($"自检截图已保存（模式 {mode}）：{targetPath}，区域 {width}x{height}@{rect.Left},{rect.Top}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("屏幕自检截图失败", ex);
            return false;
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            if (windowDc != IntPtr.Zero) ReleaseDC(hwnd, windowDc);
        }
    }
}
