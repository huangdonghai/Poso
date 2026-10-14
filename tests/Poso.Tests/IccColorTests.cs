using System;
using System.Globalization;
using System.IO;
using System.Text;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Colors;
using Poso.Skia;
using Xunit;
using IOPath = System.IO.Path;

namespace Poso.Tests;

/// <summary>
/// Validates the managed matrix/TRC ICC transform against lcms2 (the engine MuPDF uses) golden values,
/// generated from real corpus profiles. Tolerance is a few units per channel.
/// </summary>
public class IccColorTests
{
    [Fact]
    public void RgbMatrixProfile_MatchesLcms2()
    {
        IccProfile? icc = IccProfile.TryCreate(File.ReadAllBytes(Sample("rgb_matrix.icc")));
        Assert.NotNull(icc);

        int max = 0;
        foreach (string line in File.ReadAllLines(Sample("rgb_matrix_golden.csv"))[1..])
        {
            if (line.Length == 0)
            {
                continue;
            }

            int[] v = Array.ConvertAll(line.Split(','), s => int.Parse(s, CultureInfo.InvariantCulture));
            icc!.ToRgb(new[] { v[0] / 255f, v[1] / 255f, v[2] / 255f }, out byte r, out byte g, out byte b);
            max = Math.Max(max, Math.Max(Math.Abs(r - v[3]), Math.Max(Math.Abs(g - v[4]), Math.Abs(b - v[5]))));
        }

        Assert.True(max <= 5, $"RGB matrix/TRC vs lcms2 max channel diff = {max}");
    }

    [Fact]
    public void GrayMatrixProfile_MatchesLcms2()
    {
        IccProfile? icc = IccProfile.TryCreate(File.ReadAllBytes(Sample("gray_matrix.icc")));
        Assert.NotNull(icc);

        int max = 0;
        foreach (string line in File.ReadAllLines(Sample("gray_matrix_golden.csv"))[1..])
        {
            if (line.Length == 0)
            {
                continue;
            }

            int[] v = Array.ConvertAll(line.Split(','), s => int.Parse(s, CultureInfo.InvariantCulture));
            icc!.ToRgb(new[] { v[0] / 255f }, out byte r, out byte g, out byte b);
            max = Math.Max(max, Math.Max(Math.Abs(r - v[1]), Math.Max(Math.Abs(g - v[2]), Math.Abs(b - v[3]))));
        }

        Assert.True(max <= 5, $"Gray matrix/TRC vs lcms2 max channel diff = {max}");
    }

    [Fact]
    public void ClutProfile_IsRejected()
    {
        // CLUT (A2B0) profiles must return null so callers keep their existing per-channel approximation.
        Assert.Null(IccProfile.TryCreate(File.ReadAllBytes(Sample("clut_reject.icc"))));
    }

    [Fact]
    public void IccBasedFillColor_IsColorManaged()
    {
        byte[] icc = File.ReadAllBytes(Sample("rgb_matrix.icc"));
        byte[] pdf = BuildIccFillPdf(icc);

        using PdfDocument document = PdfDocument.FromArray(pdf);
        IGraphicsFactory factory = SkiaGraphicsFactory.Default;
        using IGraphicsContext context = factory.CreateContext(10, 10);
        document.Pages[0].Render(factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * 4];
        image.CopyPixels(px);
        int o = ((5 * image.Width) + 5) * 4; // centre pixel, BGRA

        // A fill of device (1,0,0) in this profile becomes sRGB ~(249,66,25), not the raw (255,0,0).
        // Loose bound guards the interpreter wiring; RgbMatrixProfile_MatchesLcms2 pins the exact transform.
        Assert.True(px[o + 2] < 250 && px[o + 1] > 40, $"fill not colour-managed: BGRA=({px[o]},{px[o + 1]},{px[o + 2]})");
    }

    private static byte[] BuildIccFillPdf(byte[] icc)
    {
        using var ms = new MemoryStream();
        void W(string s) { byte[] bb = Encoding.Latin1.GetBytes(s); ms.Write(bb, 0, bb.Length); }
        void WB(byte[] bb) => ms.Write(bb, 0, bb.Length);

        long[] off = new long[6];
        W("%PDF-1.4\n");
        off[1] = ms.Length; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        off[2] = ms.Length; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        off[3] = ms.Length; W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /ColorSpace << /Cs1 [/ICCBased 4 0 R] >> >> /Contents 5 0 R >>\nendobj\n");
        off[4] = ms.Length; W($"4 0 obj\n<< /N 3 /Alternate /DeviceRGB /Length {icc.Length} >>\nstream\n"); WB(icc); W("\nendstream\nendobj\n");
        byte[] content = Encoding.Latin1.GetBytes("/Cs1 cs 1 0 0 sc 0 0 10 10 re f");
        off[5] = ms.Length; W($"5 0 obj\n<< /Length {content.Length} >>\nstream\n"); WB(content); W("\nendstream\nendobj\n");

        int xref = (int)ms.Length;
        W("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
        {
            W($"{off[i]:D10} 00000 n \n");
        }

        W($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string Sample(string name)
    {
        string probe = IOPath.Combine(AppContext.BaseDirectory, "Samples", "Icc", name);
        if (File.Exists(probe))
        {
            return probe;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = IOPath.Combine(dir.FullName, "tests", "Poso.Tests", "Samples", "Icc", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"ICC sample '{name}' not found.", name);
    }
}
