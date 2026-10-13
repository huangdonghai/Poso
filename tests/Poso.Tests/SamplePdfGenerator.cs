using System.Text;

namespace Poso.Tests;

/// <summary>
/// Builds the demo document shipped as samples/Sample.Avalonia/Assets/sample.pdf.
/// Three pages exercising text, vector paths, images, form XObjects, links and an outline.
/// Everything is uncompressed so the file stays easy to inspect and diff.
/// </summary>
public static class SamplePdfGenerator
{
    public static byte[] Build()
    {
        var bodies = new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /Outlines 11 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 5 0 R 7 0 R] /Count 3 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> /XObject << /Fm1 10 0 R >> >> /Contents 4 0 R /Annots [12 0 R 13 0 R] >>",
            [5] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> /Contents 6 0 R >>",
            [7] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> /XObject << /Im1 14 0 R /Fm1 10 0 R >> >> /Contents 8 0 R >>",
            [9] = Font(),
            [10] = Form(),
            [11] = "<< /Type /Outlines /First 15 0 R /Last 16 0 R /Count 2 >>",
            [12] = "<< /Type /Annot /Subtype /Link /Rect [60 560 300 650] /Border [0 0 1] /A << /S /GoTo /D [5 0 R /Fit] >> >>",
            [13] = "<< /Type /Annot /Subtype /Link /Rect [320 560 552 650] /Border [0 0 1] /A << /S /URI /URI (https://example.com/poso) >> >>",
            [15] = "<< /Title (Chapter 1 - Introduction) /Parent 11 0 R /Next 16 0 R /Dest [3 0 R /Fit] >>",
            [16] = "<< /Title (Chapter 2 - Details) /Parent 11 0 R /Prev 15 0 R /First 17 0 R /Last 17 0 R /Count 1 /Dest [5 0 R /Fit] >>",
            [17] = "<< /Title (Section 2.1 - Typography) /Parent 16 0 R /Dest [5 0 R /XYZ 60 700 0] >>",
            [18] = "<< /Title (Poso Sample Document) /Author (Poso) /Creator (Poso Sample Generator) >>",
        };

        var streams = new Dictionary<int, (string Dictionary, byte[] Data)>
        {
            [4] = (string.Empty, Ascii(Page1())),
            [6] = (string.Empty, Ascii(Page2())),
            [8] = (string.Empty, Ascii(Page3())),
            [14] = ($"/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {8 * 8 * 3}", GradientPixels()),
        };

        var output = new MemoryStream();
        void Write(string text) => output.Write(Ascii(text));

        Write("%PDF-1.7\n");
        var offsets = new Dictionary<int, long>();

        foreach (int number in bodies.Keys.Concat(streams.Keys).OrderBy(n => n))
        {
            offsets[number] = output.Position;
            Write($"{number} 0 obj\n");

            if (streams.TryGetValue(number, out (string Dictionary, byte[] Data) stream))
            {
                Write($"<< {stream.Dictionary} >>\nstream\n");
                output.Write(stream.Data);
                Write("\nendstream");
            }
            else
            {
                Write(bodies[number]);
            }

            Write("\nendobj\n");
        }

        long xrefOffset = output.Position;
        int count = bodies.Count + streams.Count + 1; // object 0 is the free head
        Write($"xref\n0 {count}\n");
        Write("0000000000 65535 f \n");
        for (int i = 1; i < count; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {count} /Root 1 0 R /Info 18 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return output.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static string Font()
    {
        // 95 widths for codes 32..126: space is narrow, everything else uses the Helvetica average
        var widths = new StringBuilder();
        for (int code = 32; code <= 126; code++)
        {
            widths.Append(code == 32 ? "278" : "556");
            widths.Append(' ');
        }

        return $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{widths.ToString().TrimEnd()}] >>";
    }

    private static string Form() =>
        "<< /Type /XObject /Subtype /Form /FormType 1 /BBox [0 0 100 60] /Length " +
        Ascii(FormContent()).Length +
        " >>\nstream\n" +
        FormContent() +
        "\nendstream";

    private static string FormContent() =>
        "0.95 0.75 0.10 rg 0 0 m 50 60 l 100 0 l 50 20 l h f\n" +
        "0.10 0.10 0.10 RG 1.5 w 0 0 m 50 60 l 100 0 l S\n";

    private static string Page1() =>
        "q 1 0 0 1 60 690 cm /Fm1 Do Q\n" +
        "BT /F1 30 Tf 60 700 Td (Poso PDF Viewer) Tj ET\n" +
        "BT /F1 12 Tf 60 672 Td (A from-scratch PDF pipeline for .NET and Avalonia) Tj ET\n" +
        "0.20 0.40 0.90 rg 60 560 492 90 re f\n" +
        "1 1 1 rg 60 470 492 70 re f\n" +
        "0.10 0.60 0.30 rg 60 380 m 160 380 160 440 260 440 c 360 440 360 380 460 380 c h f\n" +
        "0.90 0.10 0.10 RG 3 w 60 350 m 552 350 l S\n" +
        "0 0 0 RG 1 w [6 3] 0 d 60 330 m 552 330 l S\n" +
        "[] 0 d\n" +
        "BT /F1 10 Tf 60 590 Td 16 TL (First body line with leading.) Tj T* (Second body line.) Tj T* (Third body line.) Tj T* (Fourth body line.) Tj ET\n" +
        "BT /F1 10 Tf 60 300 Td (Vector graphics: rectangle fills, cubic Bezier paths, dashed strokes.) Tj ET\n";

    private static string Page2() =>
        "BT /F1 22 Tf 60 700 Td (Text Rendering) Tj ET\n" +
        "BT /F1 12 Tf 60 660 Td (Plain 12pt text in WinAnsi encoding.) Tj ET\n" +
        "BT /F1 12 Tf 60 630 Td 150 Tz (Wide via horizontal scaling.) Tj ET\n" +
        "BT /F1 12 Tf 60 600 Td 4 Tc (Letter spacing applied.) Tj ET\n" +
        "BT /F1 12 Tf 60 570 Td [(Ker) -250 (ned) -250 ( text) -250 ( via TJ.)] TJ ET\n" +
        "BT /F1 14 Tf 6 Ts 60 530 Td (Raised by text rise.) Tj ET\n" +
        "BT /F1 14 Tf 1 Tr 60 490 Td (Stroked text mode.) Tj ET\n" +
        "0 0.45 0.75 rg 60 440 300 60 re f\n" +
        "1 1 1 rg BT /F1 11 Tf 75 470 Td (White text on blue) Tj ET\n" +
        "q 1 0 0 1 420 400 cm 0.9 0.5 0 rg 40 0 m 80 50 l 120 0 l 40 25 l h f Q\n";

    private static string Page3() =>
        "BT /F1 22 Tf 60 700 Td (Images and Forms) Tj ET\n" +
        "q 288 0 0 192 60 420 cm /Im1 Do Q\n" +
        "q 1 0 0 1 420 420 cm /Fm1 Do Q\n" +
        "BT /F1 10 Tf 60 380 Td (Left: uncompressed RGB image XObject. Right: form XObject with its own BBox.) Tj ET\n";

    /// <summary>8x8 RGB gradient: red grows with x, green with y, blue constant.</summary>
    private static byte[] GradientPixels()
    {
        var pixels = new byte[8 * 8 * 3];
        int index = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                pixels[index++] = (byte)(x * 32);
                pixels[index++] = (byte)(y * 32);
                pixels[index++] = 128;
            }
        }

        return pixels;
    }
}
