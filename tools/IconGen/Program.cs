// Renders assets/icon.svg into every icon format the app and its packages need.
// Run from the repository root:  dotnet run --project tools/IconGen
using System.Text;
using SkiaSharp;
using Svg.Skia;

string source = Path.Combine("assets", "icon.svg");
string output = Path.Combine("assets", "generated");
Directory.CreateDirectory(output);

using var svg = new SKSvg();
if (svg.Load(source) is not { } picture) throw new InvalidDataException("Could not read " + source);

byte[] Render(int size)
{
    using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(SKColors.Transparent);
        float scale = size / picture.CullRect.Width;
        canvas.Scale(scale);
        canvas.DrawPicture(picture, new SKPaint { IsAntialias = true });
    }
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

foreach (int size in new[] { 16, 24, 32, 48, 64, 128, 256, 512, 1024 })
    File.WriteAllBytes(Path.Combine(output, $"icon-{size}.png"), Render(size));

// .ico: a directory of PNG images.
int[] icoSizes = { 16, 24, 32, 48, 64, 128, 256 };
using (var ico = new BinaryWriter(File.Create(Path.Combine(output, "icon.ico"))))
{
    ico.Write((short)0); ico.Write((short)1); ico.Write((short)icoSizes.Length);
    var images = icoSizes.Select(Render).ToArray();
    int offset = 6 + 16 * icoSizes.Length;
    for (int i = 0; i < icoSizes.Length; i++)
    {
        ico.Write((byte)(icoSizes[i] >= 256 ? 0 : icoSizes[i])); ico.Write((byte)(icoSizes[i] >= 256 ? 0 : icoSizes[i]));
        ico.Write((byte)0); ico.Write((byte)0); ico.Write((short)1); ico.Write((short)32);
        ico.Write(images[i].Length); ico.Write(offset);
        offset += images[i].Length;
    }
    foreach (byte[] image in images) ico.Write(image);
}

// .icns: big-endian chunks holding PNG images.
(string Type, int Size)[] icnsTypes = { ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024), ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512) };
var chunks = icnsTypes.Select(t => (t.Type, Data: Render(t.Size))).ToList();
using (var icns = new BinaryWriter(File.Create(Path.Combine(output, "icon.icns"))))
{
    void BigEndian(int value) { icns.Write((byte)(value >> 24)); icns.Write((byte)(value >> 16)); icns.Write((byte)(value >> 8)); icns.Write((byte)value); }
    icns.Write(Encoding.ASCII.GetBytes("icns"));
    BigEndian(8 + chunks.Sum(c => 8 + c.Data.Length));
    foreach (var (type, data) in chunks)
    {
        icns.Write(Encoding.ASCII.GetBytes(type));
        BigEndian(8 + data.Length);
        icns.Write(data);
    }
}

// A contact sheet for checking how the icon reads at each size, on a light and on a dark background.
int[] preview = { 16, 24, 32, 48, 64, 128, 256 };
using (var sheet = new SKBitmap(preview.Sum() + 20 * (preview.Length + 1), 2 * (256 + 40)))
using (var canvas = new SKCanvas(sheet))
{
    canvas.Clear(new SKColor(0xF3, 0xF4, 0xF6));
    canvas.DrawRect(0, 296, sheet.Width, 296, new SKPaint { Color = new SKColor(0x14, 0x17, 0x20) });
    int x = 20;
    foreach (int size in preview)
    {
        using var image = SKImage.FromEncodedData(Render(size));
        canvas.DrawImage(image, x, 148 - size / 2, SKSamplingOptions.Default);
        canvas.DrawImage(image, x, 296 + 148 - size / 2, SKSamplingOptions.Default);
        x += size + 20;
    }
    canvas.Flush();
    using var image2 = SKImage.FromBitmap(sheet);
    using var data = image2.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(Path.Combine(output, "preview.png"), data.ToArray());
}
Console.WriteLine("Icons written to " + output);
