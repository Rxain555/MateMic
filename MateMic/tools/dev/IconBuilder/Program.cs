using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 用法：IconBuilder <源 PNG> <输出 ICO>
// 把 PNG 缩放到多个尺寸并打包成标准 ICO（每个尺寸用 PNG 压缩存储，Vista+ 支持）。
var source = args[0];
var target = args[1];
var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };

var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(source)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
Console.WriteLine($"source {frame.PixelWidth}x{frame.PixelHeight} {frame.Format}");

var blobs = new List<(int Size, byte[] Data)>();
foreach (var size in sizes)
{
    var scaled = new TransformedBitmap(frame,
        new ScaleTransform(size / (double)frame.PixelWidth, size / (double)frame.PixelHeight));
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(scaled));
    using var ms = new MemoryStream();
    encoder.Save(ms);
    blobs.Add((size, ms.ToArray()));
    Console.WriteLine($"  {size}x{size} -> {ms.Length} bytes");
}

using var output = File.Create(target);
using var writer = new BinaryWriter(output);
writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)blobs.Count);

var offset = 6 + 16 * blobs.Count;
foreach (var (size, data) in blobs)
{
    var dim = size >= 256 ? (byte)0 : (byte)size;
    writer.Write(dim);
    writer.Write(dim);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write((uint)data.Length);
    writer.Write((uint)offset);
    offset += data.Length;
}

foreach (var (_, data) in blobs) writer.Write(data);
writer.Flush();
Console.WriteLine($"ICO written: {new FileInfo(target).Length} bytes");
