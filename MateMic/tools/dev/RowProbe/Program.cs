using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 用法：RowProbe <png> <x> [<x2> ...]
// 对每一列：找出分组容器（底色 #E9E9E9 或边框 #C9C9C9）的最低行，
// 再找出该行之上最后一张白色卡片（#FFFFFF）的底边，报告两者间距。
var file = args[0];
var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(file)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
var w = converted.PixelWidth;
var h = converted.PixelHeight;
var stride = w * 4;
var pixels = new byte[stride * h];
converted.CopyPixels(pixels, stride, 0);

static bool Near(byte[] px, int stride, int x, int y, byte r, byte g, byte b, int tol)
{
    var i = y * stride + x * 4;
    return Math.Abs(px[i + 2] - r) <= tol && Math.Abs(px[i + 1] - g) <= tol && Math.Abs(px[i] - b) <= tol;
}

Console.WriteLine($"image {w}x{h}");
for (var a = 1; a < args.Length; a++)
{
    var x = int.Parse(args[a]);

    var containerBottom = -1;
    for (var y = h - 1; y >= 0; y--)
    {
        if (Near(pixels, stride, x, y, 0xE9, 0xE9, 0xE9, 3) || Near(pixels, stride, x, y, 0xC9, 0xC9, 0xC9, 3))
        {
            containerBottom = y;
            break;
        }
    }

    var lastCardBottom = -1;
    for (var y = Math.Max(0, containerBottom - 1); y >= 0; y--)
    {
        if (Near(pixels, stride, x, y, 0xFF, 0xFF, 0xFF, 2)) { lastCardBottom = y; break; }
    }

    var gap = containerBottom > 0 && lastCardBottom > 0 ? containerBottom - lastCardBottom : -1;
    Console.WriteLine($"x={x,4}  容器最低行 y={containerBottom,3}  最下方白色卡底边 y={lastCardBottom,3}  间距 {gap,2} px");
}
