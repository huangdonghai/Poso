using System;
using System.IO;
using Poso.Pdf.Filters.Codecs;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Regression for the self-decoded CMYK/YCCK JPEG path (see <see cref="JpegDecoder"/> and
/// <see cref="JpegColorConverter"/>). Native codecs (Skia) mis-render Adobe CMYK/YCCK JPEGs because they
/// omit the CMYK inversion, producing visibly wrong colours (e.g. the "Advanced Flutter" cover butterfly
/// renders brown instead of blue).
///
/// Vectors were dumped from real PDFs (see _scratch/gen_jpeg_vectors.py); the oracles are:
///   * Layer 1 - CMYK samples: MuPDF's own JPEG decode + YCCK->CMYK (the authoritative sample values).
///   * Layer 2 - RGB: pdf.js's DeviceCmykCS polynomial applied to MuPDF's CMYK (independent reference).
/// Because a self-written IDCT cannot bit-match libjpeg's fixed-point one, comparisons are tolerance-based;
/// the thresholds sit far below the ~120 mean error of the inverted (buggy) path, so they still catch any
/// real regression.
/// </summary>
public class JpegCmykTests
{
    internal static string VectorsDir()
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "Samples", "Jpeg");
        if (Directory.Exists(probe))
        {
            return probe;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Poso.Tests", "Samples", "Jpeg");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("JPEG CMYK vector directory not found.");
    }

    [Theory]
    [InlineData("flutter-cover", 2)]
    [InlineData("rust-a", 2)]
    [InlineData("rust-tall", 2)]
    [InlineData("cmykjpeg", 0)]
    public void CmykJpeg_MatchesMuPdfAndPdfJs(string name, int expectedTransform)
    {
        string dir = VectorsDir();
        byte[] jpeg = File.ReadAllBytes(Path.Combine(dir, name + ".jpg"));

        JpegDecoder.Result? result = JpegDecoder.Decode(jpeg);
        Assert.NotNull(result);
        Assert.Equal(4, result!.ComponentCount);
        Assert.Equal(expectedTransform, result.AdobeTransform);

        // Layer 1: decoded CMYK vs MuPDF's CMYK samples.
        byte[] cmykHdr = File.ReadAllBytes(Path.Combine(dir, name + ".cmyk"));
        int w = BitConverter.ToInt32(cmykHdr, 0);
        int h = BitConverter.ToInt32(cmykHdr, 4);
        byte[] refCmyk = cmykHdr.AsSpan(12).ToArray();
        Assert.Equal(w, result.Width);
        Assert.Equal(h, result.Height);

        byte[] myCmyk = JpegColorConverter.ToCmyk(result)!;
        Assert.Equal(refCmyk.Length, myCmyk.Length);
        double cmykMean = MeanAbsDiff(myCmyk, refCmyk);
        Assert.True(cmykMean < 10, $"{name}: CMYK mean |diff| {cmykMean:F2} too high vs MuPDF");

        // Layer 2: final RGB vs pdf.js-from-MuPDF reference.
        byte[] bgra = JpegColorConverter.ToBgra32(result)!;
        byte[] rgbHdr = File.ReadAllBytes(Path.Combine(dir, name + ".rgb"));
        byte[] refRgb = rgbHdr.AsSpan(8).ToArray();
        var myRgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            myRgb[i * 3] = bgra[i * 4 + 2];
            myRgb[i * 3 + 1] = bgra[i * 4 + 1];
            myRgb[i * 3 + 2] = bgra[i * 4];
        }
        double rgbMean = MeanAbsDiff(myRgb, refRgb);
        Assert.True(rgbMean < 10, $"{name}: RGB mean |diff| {rgbMean:F2} too high vs pdf.js reference");
    }

    [Fact]
    public void FlutterCover_ButterflyIsBlue()
    {
        // The concrete bug: the cover butterfly rendered brown (inverted). Assert it is now blue.
        string dir = VectorsDir();
        byte[] jpeg = File.ReadAllBytes(Path.Combine(dir, "flutter-cover.jpg"));
        JpegDecoder.Result? result = JpegDecoder.Decode(jpeg);
        Assert.NotNull(result);
        byte[] bgra = JpegColorConverter.ToBgra32(result!)!;

        // Butterfly body patch (image coords), where MuPDF decodes a blue (B >> R).
        int cx = 650, cy = 650, r = 6, w = result!.Width;
        long sumR = 0, sumB = 0; int n = 0;
        for (int y = cy - r; y <= cy + r; y++)
        {
            for (int x = cx - r; x <= cx + r; x++)
            {
                int o = (y * w + x) * 4;
                sumB += bgra[o];
                sumR += bgra[o + 2];
                n++;
            }
        }
        Assert.True(sumB / n > sumR / n + 30, $"butterfly body should be blue (B={sumB / n} R={sumR / n})");
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }
        return sum / (double)a.Length;
    }
}
