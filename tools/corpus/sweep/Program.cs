using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using SkiaSharp;
using System.Runtime.InteropServices;

sealed class Sweep
{
    // Some corpus pages are bounded but deep (a form whose soft mask draws a form that draws a soft
    // mask), and a stack overflow cannot be caught: it takes the process with it. The loop therefore
    // runs on a thread with room to spare, and the caller restarts it after any page that still dies.
    static int Main(string[] args)
    {
        int exit = 1;
        Thread loop = new(() => exit = Run(args), 512 * 1024 * 1024);
        loop.IsBackground = false;
        loop.Start();
        loop.Join();
        return exit;
    }

    static int Run(string[] args)
    {
        string manifest = args[0];
        string outDir = args[1];
        float scale = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 2f;
        StreamWriter? diags = args.Length > 3 ? new StreamWriter(args[3], true) { AutoFlush = true } : null;
        Directory.CreateDirectory(outDir);
        string current = "";
        PdfDocument? doc = null;
        foreach (string line in File.ReadLines(manifest))
        {
            string[] parts = line.Split('\t');
            string pdfPath = parts[0];
            int page = int.Parse(parts[1]);
            string tag = parts.Length > 2 ? parts[2] : "p";
            string outPath = System.IO.Path.Combine(outDir, tag + "-poso.png");

            try
            {
                if (current != pdfPath)
                {
                    doc?.Dispose();
                    doc = PdfDocument.FromArray(File.ReadAllBytes(pdfPath));
                    current = pdfPath;
                }

                PdfPage pg = doc!.Pages[page];
                int w = (int)Math.Ceiling(pg.Width * scale);
                int h = (int)Math.Ceiling(pg.Height * scale);
                foreach (IGraphicsFactory factory in new IGraphicsFactory[] { SkiaGraphicsFactory.Default, Poso.PeachDrawing.PeachGraphicsFactory.Default })
                {
                    bool peach = factory is Poso.PeachDrawing.PeachGraphicsFactory;
                    string target = peach ? outPath.Replace("-poso.png", "-peach.png") : outPath;
                    if (File.Exists(target)) { continue; }
                    using IGraphicsContext ctx = factory.CreateContext(w, h);
                    string backend = factory is Poso.PeachDrawing.PeachGraphicsFactory ? "peach" : "skia";
                    pg.Render(factory, ctx, new PdfRenderOptions(scale, 0f, PosoColor.White), f =>
                    {
                        Console.WriteLine($"  UNSUPPORTED {f.Kind}: {f.Message}");
                        diags?.WriteLine($"{tag}	{backend}	{f.Kind}	{f.Message}");
                    });
                    using IImage img = ctx.Snapshot();
                    byte[] px = new byte[img.Width * img.Height * img.BytesPerPixel];
                    img.CopyPixels(px);
                    SavePng(px, img, target);
                    Console.WriteLine($"ok {System.IO.Path.GetFileName(target)} {img.Width}x{img.Height}");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"FAIL {tag}: {e}");
            }
        }

        doc?.Dispose();
        diags?.Dispose();
        return 0;
    }

    static void SavePng(byte[] pixels, IImage img, string path)
    {
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var skImage = SKImage.FromPixels(
                new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
                handle.AddrOfPinnedObject(),
                img.Width * img.BytesPerPixel);
            using SKData? data = skImage.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data!.ToArray());
        }
        finally
        {
            handle.Free();
        }
    }
}
