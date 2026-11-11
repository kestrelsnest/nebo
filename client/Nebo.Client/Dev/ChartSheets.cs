using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nebo.Client.Core;
using Nebo.Client.Map.Airfields;
using SkiaSharp;

namespace Nebo.Client.Dev;

/// <summary>Development aid (snapshot modes "chart:&lt;field&gt;" and "charts"): the airfield
/// diagram as printed sheets — Letter portrait in a NATOPS-style frame (<see cref="AirfieldDiagramView.Sheet"/>).
/// Per field: a PDF of the page at 600 dpi, a 1536×2048 kneeboard page (DCS's 768×1024 at twice
/// the resolution: DCS scales it, sharper in VR and zoomed) and a mockup of the sheet on a desk;
/// all fields of the run in one PDF as well.</summary>
public static class ChartSheets
{
    private const double PageW = 816, PageH = 1056;  // Letter at 96 DIP per inch
    private const double PrintDpi = 600;
    private const double KneeboardScale = 2;         // × DCS's 768×1024

    /// <summary>Writes the sheets of these fields into <paramref name="dir"/>, one folder per file
    /// type: PDF (one per field, and all fields in one), Kneeboard and Mockup. One report line each.</summary>
    public static List<string> Save(IReadOnlyList<Airfield> fields, Theatre theatre, string dir)
    {
        var report = new List<string>();
        var pages = new List<byte[]>();
        for (int n = 0; n < fields.Count; n++)
        {
            var field = fields[n];
            var view = new AirfieldDiagramView
            {
                Sheet = true,
                SheetDocument = $"EN 01-AFC-{theatre.Id.ToUpperInvariant()}",
                SheetPage = $"1-{n + 1}",
                Width = PageW,
                Height = PageH,
            };
            view.Measure(new Size(PageW, PageH));
            view.Arrange(new Rect(0, 0, PageW, PageH));
            view.Show(field, ChartLabels.For(field));
            view.UpdateLayout();

            pages.Add(Output(view, string.Concat(field.Name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_'), dir));
            report.Add($"chart: {field.Name}: {view.DebugState}");
        }
        if (pages.Count > 1) report.Add($"chart: {pages.Count} pages in {WriteAll(pages, dir, $"_{theatre.Id} all airfields.pdf")}");
        return report;
    }

    /// <summary>Other sheets (RWR threat table): each Letter page like an airfield's, named in
    /// order, and all of them in one PDF.</summary>
    public static string SavePages(IReadOnlyList<(string Name, Visual Page)> sheets, string dir, string allName)
    {
        var pages = sheets.Select(s => Output(s.Page, s.Name, dir)).ToList();
        return WriteAll(pages, dir, allName);
    }

    /// <summary>One page into the type folders: PDF, Kneeboard, Mockup. Returns the print PNG.</summary>
    private static byte[] Output(Visual page, string name, string dir)
    {
        string pdfDir = Path.Combine(dir, "PDF"), kneeboardDir = Path.Combine(dir, "Kneeboard"), mockupDir = Path.Combine(dir, "Mockup");
        foreach (var d in new[] { pdfDir, kneeboardDir, mockupDir }) Directory.CreateDirectory(d);
        var sheet = Render(page, PageW, PageH, PrintDpi);
        var png = Encode(sheet);
        WritePdf([png], Path.Combine(pdfDir, $"{name}.pdf"));
        Write(Kneeboard(sheet), Path.Combine(kneeboardDir, $"{name}.png"));
        Write(Mockup(sheet), Path.Combine(mockupDir, $"{name}.png"));
        return png;
    }

    private static string WriteAll(IReadOnlyList<byte[]> pages, string dir, string name)
    {
        var path = Path.Combine(dir, "PDF", name);
        WritePdf(pages, path);
        return path;
    }

    private static BitmapSource Render(Visual v, double w, double h, double dpi)
    {
        var bmp = new RenderTargetBitmap((int)Math.Round(w * dpi / 96), (int)Math.Round(h * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>The page scaled into DCS's 768×1024 kneeboard (at <see cref="KneeboardScale"/>), white
    /// margins top and bottom.</summary>
    private static BitmapSource Kneeboard(BitmapSource sheet)
    {
        const double w = 768, h = 1024;
        var s = Math.Min(w / PageW, h / PageH);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(sheet, new Rect((w - PageW * s) / 2, (h - PageH * s) / 2, PageW * s, PageH * s));
        }
        return Render(visual, w, h, 96 * KneeboardScale);
    }

    /// <summary>The sheet lying on a desk, slightly turned, with a soft shadow, a paper edge and
    /// the three binder holes of a manual page.</summary>
    private static BitmapSource Mockup(BitmapSource sheet)
    {
        const double w = 1500, h = 1800, pw = 1200, ph = pw * PageH / PageW;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        var desk = new LinearGradientBrush(Color.FromRgb(0x4A, 0x46, 0x41), Color.FromRgb(0x2C, 0x29, 0x26), 45);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(desk, null, new Rect(0, 0, w, h));
            var page = new Rect((w - pw) / 2, (h - ph) / 2, pw, ph);
            dc.PushTransform(new RotateTransform(-1.4, w / 2, h / 2));
            // Shadow: offset layers, fainter outwards.
            for (int k = 12; k >= 1; k--)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(10 + (12 - k) * 2), 0, 0, 0)), null,
                    Rect.Inflate(Rect.Offset(page, 10, 16), k * 2.2, k * 2.2), k * 2, k * 2);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFA, 0xF8, 0xF2)), null, page);
            dc.DrawImage(sheet, page);
            // Paper: a faint warm tone over the white and a darker edge.
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(18, 0xC8, 0xB8, 0x90)), new Pen(new SolidColorBrush(Color.FromRgb(0xC9, 0xC4, 0xB8)), 1.5), page);
            var holeX = page.Left + pw * 18 / PageW;
            foreach (var f in new[] { 0.18, 0.5, 0.82 })
            {
                var c = new Point(holeX, page.Top + ph * f);
                dc.DrawEllipse(desk, new Pen(new SolidColorBrush(Color.FromRgb(0xB5, 0xAF, 0xA2)), 1.5), c, 11, 11);
            }
            dc.Pop();
        }
        return Render(visual, w, h, 96);
    }

    private static byte[] Encode(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var m = new MemoryStream();
        enc.Save(m);
        return m.ToArray();
    }

    private static void Write(BitmapSource bmp, string path) => File.WriteAllBytes(path, Encode(bmp));

    /// <summary>The 300 dpi pages as one Letter PDF (SkiaSharp, which the map already ships).</summary>
    private static void WritePdf(IReadOnlyList<byte[]> pages, string path)
    {
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = "Airfield diagrams", Creator = "NEBO", RasterDpi = (float)PrintDpi });
        foreach (var png in pages)
        {
            var canvas = doc.BeginPage(612, 792);  // Letter in points
            using var image = SKImage.FromEncodedData(png);
            canvas.DrawImage(image, SKRect.Create(0, 0, 612, 792), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            doc.EndPage();
        }
        doc.Close();
    }
}
