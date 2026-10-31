using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// The Fyltrix logo: a folder with a magnifying lens over it, on a 256 × 256 grid.
// One definition drives every output, so the SVG, PNGs and .ico always match.

var outDir = args.Length > 0 ? args[0] : RepoAssets();
Directory.CreateDirectory(outDir);
var sta = new Thread(() => Generate(outDir)); // WPF rendering needs an STA thread
sta.SetApartmentState(ApartmentState.STA);
sta.Start();
sta.Join();

static void Generate(string outDir)
{

var shapes = new List<Shape>
{
    // folder back with its tab
    new("M24,64 A16,16 0 0 1 40,48 H94 A16,16 0 0 1 105.3,52.7 L122.7,70 H216 A16,16 0 0 1 232,86 V190 A16,16 0 0 1 216,206 H40 A16,16 0 0 1 24,190 Z",
        Fill: new Grad("#E9A21F", "#F6BE45")),
    // folder front
    new("M24,104 A16,16 0 0 1 40,88 H216 A16,16 0 0 1 232,104 V190 A16,16 0 0 1 216,206 H40 A16,16 0 0 1 24,190 Z",
        Fill: new Grad("#FFC94D", "#FFDF8A")),
    // lens handle
    new("M190,178 L228,216", Stroke: "#1E3A8A", StrokeWidth: 26),
    // lens glass
    new("M98,140 A52,52 0 1 0 202,140 A52,52 0 1 0 98,140 Z", Fill: new Grad("#E0EEFF", "#BBD7FF", Opacity: 0.96)),
    // glass highlight
    new("M121,128 A32,32 0 0 1 150,110", Stroke: "#FFFFFF", StrokeWidth: 9, Opacity: 0.9),
    // lens ring
    new("M98,140 A52,52 0 1 0 202,140 A52,52 0 1 0 98,140 Z", Stroke: "#2563EB", StrokeWidth: 16),
};

// ---- fit the drawing to the canvas ----
// The shapes leave a wide empty border on the 256 grid, so the icon looked smaller than other apps' in the taskbar (27 × 23
// of 32 px against Edge's / VS Code's full 32 × 32). Scale the drawing (strokes and round caps included) to fill the canvas,
// keeping a hairline margin, and centre it. The design itself is unchanged.
var bounds = Rect.Empty;
foreach (var s in shapes)
{
    var geo = Geometry.Parse(s.Data);
    bounds.Union(s.Stroke != null ? geo.GetRenderBounds(new Pen(Brushes.Black, s.StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }) : geo.Bounds);
}
const double Margin = 3; // of 256
double fit = (256 - 2 * Margin) / Math.Max(bounds.Width, bounds.Height);
var place = new Matrix(fit, 0, 0, fit, 128 - fit * (bounds.Left + bounds.Width / 2), 128 - fit * (bounds.Top + bounds.Height / 2));

// ---- SVG source ----
var svg = new StringBuilder();
svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 256 256\" width=\"256\" height=\"256\">");
svg.AppendLine("  <title>Fyltrix</title>");
svg.AppendLine("  <defs>");
for (int i = 0; i < shapes.Count; i++)
    if (shapes[i].Fill is { } g)
        svg.AppendLine($"    <linearGradient id=\"g{i}\" x1=\"0\" y1=\"1\" x2=\"0\" y2=\"0\"><stop offset=\"0\" stop-color=\"{g.From}\"/><stop offset=\"1\" stop-color=\"{g.To}\"/></linearGradient>");
svg.AppendLine("  </defs>");
svg.AppendLine($"  <g transform=\"matrix({F(place.M11)} 0 0 {F(place.M22)} {F(place.OffsetX)} {F(place.OffsetY)})\">");
for (int i = 0; i < shapes.Count; i++)
{
    var s = shapes[i];
    var attrs = s.Fill != null ? $"fill=\"url(#g{i})\"" + (s.Fill.Opacity < 1 ? $" fill-opacity=\"{F(s.Fill.Opacity)}\"" : "") : "fill=\"none\"";
    if (s.Stroke != null) attrs += $" stroke=\"{s.Stroke}\" stroke-width=\"{F(s.StrokeWidth)}\" stroke-linecap=\"round\"" + (s.Opacity < 1 ? $" stroke-opacity=\"{F(s.Opacity)}\"" : "");
    svg.AppendLine($"    <path d=\"{s.Data}\" {attrs}/>");
}
svg.AppendLine("  </g>");
svg.AppendLine("</svg>");
File.WriteAllText(Path.Combine(outDir, "Fyltrix.svg"), svg.ToString());

// ---- PNGs and ICO ----
var icoSizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
var pngSizes = new[] { 16, 24, 32, 48, 64, 128, 256, 512, 1024 };
var images = new Dictionary<int, BitmapSource>();
foreach (var size in icoSizes.Concat(pngSizes).Distinct()) images[size] = Render(shapes, size, place);
foreach (var size in pngSizes)
{
    using var fs = File.Create(Path.Combine(outDir, $"Fyltrix-{size}.png"));
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(images[size]));
    enc.Save(fs);
}
WriteIco(Path.Combine(outDir, "Fyltrix.ico"), icoSizes.Select(s => images[s]).ToList());
Console.WriteLine("Wrote logo assets to " + outDir);
}

// src/Fyltrix/Assets of the repository this tool sits in (bin/Debug/… or bin/x64/Debug/…, so walk up rather than count)
static string RepoAssets()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "src", "Fyltrix", "Fyltrix.csproj"))) return Path.Combine(dir.FullName, "src", "Fyltrix", "Assets");
    throw new DirectoryNotFoundException("Run this from the Fyltrix repository, or pass the output folder.");
}

static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

static BitmapSource Render(List<Shape> shapes, int size, Matrix place)
{
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
        dc.PushTransform(new MatrixTransform(place));
        foreach (var s in shapes)
        {
            var geo = Geometry.Parse(s.Data);
            Brush? fill = null;
            if (s.Fill is { } g)
            {
                fill = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(g.From), (Color)ColorConverter.ConvertFromString(g.To), new Point(0, 1), new Point(0, 0)) { Opacity = g.Opacity };
            }
            Pen? pen = null;
            if (s.Stroke != null)
            {
                // tiny sizes: keep strokes at least ~1 device pixel so the lens ring doesn't vanish
                double minWidth = 256.0 / size * 1.1 / place.M11;
                pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Stroke)) { Opacity = s.Opacity }, Math.Max(s.StrokeWidth, minWidth))
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            }
            dc.DrawGeometry(fill, pen, geo);
        }
        dc.Pop();
        dc.Pop();
    }
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(dv);
    bmp.Freeze();
    return bmp;
}

// ICO: 256 px as PNG (Vista+), smaller sizes as 32-bit BMP for the widest compatibility (shell, WPF, WinForms tray).
static void WriteIco(string path, List<BitmapSource> frames)
{
    var blobs = frames.Select(f => f.PixelWidth >= 256 ? Png(f) : Dib(f)).ToList();
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
    int offset = 6 + 16 * frames.Count;
    for (int i = 0; i < frames.Count; i++)
    {
        int s = frames[i].PixelWidth;
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(blobs[i].Length); w.Write(offset);
        offset += blobs[i].Length;
    }
    foreach (var b in blobs) w.Write(b);
}

static byte[] Png(BitmapSource f)
{
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(f));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

static byte[] Dib(BitmapSource f)
{
    int s = f.PixelWidth;
    var bgra = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0); // un-premultiplied, as ICO expects
    var pixels = new byte[s * s * 4];
    bgra.CopyPixels(pixels, s * 4, 0);
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(s); w.Write(s * 2); // height covers XOR + AND masks
    w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = s - 1; y >= 0; y--) w.Write(pixels, y * s * 4, s * 4); // bottom-up rows
    int maskStride = ((s + 31) / 32) * 4;
    for (int y = s - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (int x = 0; x < s; x++)
            if (pixels[(y * s + x) * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
        w.Write(row);
    }
    return ms.ToArray();
}

record Grad(string From, string To, double Opacity = 1);
record Shape(string Data, Grad? Fill = null, string? Stroke = null, double StrokeWidth = 0, double Opacity = 1);
