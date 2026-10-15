using System;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M7/D: reference-render regression baseline. Each sample page is rendered by MuPDF (PyMuPDF) to a
/// coarse 4x2 luminance signature captured here; Poso's own render must match within tolerance.
/// Guards core drawing (shapes, image/CCITT decoding) against regressions using MuPDF as the oracle.
/// </summary>
public class ReferenceRenderTests
{
    internal const int GridW = 4;
    internal const int GridH = 2;
    internal const int Tolerance = 60; // coarse cells; allows AA/rounding, gradient sampling and font-rendering differences

    internal const string ShapesPdf = "JVBERi0xLjcKJcK1wrYKJSBXcml0dGVuIGJ5IE11UERGIDEuMjguMgoKMSAwIG9iago8PC9UeXBlL0NhdGFsb2cvUGFnZXMgMiAwIFIvSW5mbzw8L1Byb2R1Y2VyKE11UERGIDEuMjguMik+Pj4+CmVuZG9iagoKMiAwIG9iago8PC9UeXBlL1BhZ2VzL0NvdW50IDEvS2lkc1s0IDAgUl0+PgplbmRvYmoKCjMgMCBvYmoKPDw+PgplbmRvYmoKCjQgMCBvYmoKPDwvVHlwZS9QYWdlL01lZGlhQm94WzAgMCAyMDAgMTAwXS9Sb3RhdGUgMC9SZXNvdXJjZXMgMyAwIFIvUGFyZW50IDIgMCBSL0NvbnRlbnRzWzUgMCBSIDYgMCBSXT4+CmVuZG9iagoKNSAwIG9iago8PC9MZW5ndGggMzkvRmlsdGVyL0ZsYXRlRGVjb2RlPj4Kc3RyZWFtCnja4yrkMlAwUDA0gOCiVK4MLkMFkFCQuwKEUZSu4MQVyAUAqhcIFgplbmRzdHJlYW0KZW5kb2JqCgo2IDAgb2JqCjw8L0xlbmd0aCA0Mi9GaWx0ZXIvRmxhdGVEZWNvZGU+PgpzdHJlYW0KeNrjKuQyNDBQMFAAkSBclMqVwWWgoGcGFAtyV4CyitIVnLgCuQDL9gjdCmVuZHN0cmVhbQplbmRvYmoKCnhyZWYKMCA3CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDA0MiAwMDAwMCBuIAowMDAwMDAwMTIwIDAwMDAwIG4gCjAwMDAwMDAxNzIgMDAwMDAgbiAKMDAwMDAwMDE5MyAwMDAwMCBuIAowMDAwMDAwMzA2IDAwMDAwIG4gCjAwMDAwMDA0MTMgMDAwMDAgbiAKCnRyYWlsZXIKPDwvU2l6ZSA3L1Jvb3QgMSAwIFIvSURbPDAwQzNBQUMzQUNDMzhCQzI4MkMyQkM2QkMyODhDMjhDPjw5NTJCMkFDRjg0MDNBQkQ3OEM2N0ZDQ0VENUIzNERCOD5dPj4Kc3RhcnR4cmVmCjUyMwolJUVPRgo=";
    internal static readonly int[] ShapesExpected = { 85, 84, 51, 51, 85, 84, 51, 51 }; // 160x80 scale 0.800000

    private const string CcittPdf = "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";
    private static readonly int[] CcittExpected = { 147, 82, 168, 255, 213, 142, 213, 255 }; // 160x80 scale 8.888889

    internal const string ShadingPdf = "JVBERi0xLjQKJeLjz9MKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCAyMDAgMTAwXSAvUmVzb3VyY2VzIDw8IC9QYXR0ZXJuIDw8IC9QMCA1IDAgUiA+PiA+PiAvQ29udGVudHMgNCAwIFIgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA1MCA+PgpzdHJlYW0KWy9QYXR0ZXJuIC9EZXZpY2VHcmF5XSBjcyAvUDAgc2NuIDAgMCAyMDAgMTAwIHJlIGYKZW5kc3RyZWFtCmVuZG9iago1IDAgb2JqCjw8IC9QYXR0ZXJuVHlwZSAyIC9TaGFkaW5nIDw8IC9TaGFkaW5nVHlwZSAyIC9Db2xvclNwYWNlIC9EZXZpY2VHcmF5IC9Db29yZHMgWzAgMCAyMDAgMF0gL0RvbWFpbiBbMCAxXSAvRnVuY3Rpb24gNiAwIFIgPj4gPj4KZW5kb2JqCjYgMCBvYmoKPDwgL0Z1bmN0aW9uVHlwZSAyIC9DMCBbMF0gL0MxIFsxXSAvRG9tYWluIFswIDFdIC9OIDEgPj4KZW5kb2JqCnhyZWYKMCA3CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMDY0IDAwMDAwIG4gCjAwMDAwMDAxMjEgMDAwMDAgbiAKMDAwMDAwMDI1MCAwMDAwMCBuIAowMDAwMDAwMzUwIDAwMDAwIG4gCjAwMDAwMDA0OTAgMDAwMDAgbiAKdHJhaWxlcgo8PCAvU2l6ZSA3IC9Sb290IDEgMCBSID4+CnN0YXJ0eHJlZgo1NjIKJSVFT0YK";
    internal static readonly int[] ShadingExpected = { 30, 93, 157, 221, 30, 94, 157, 221 }; // 160x80 scale 0.800000

    private const string TilingPdf = "JVBERi0xLjQKJeLjz9MKMSAwIG9iago8PCAvVHlwZSAvQ2F0YWxvZyAvUGFnZXMgMiAwIFIgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL1R5cGUgL1BhZ2VzIC9LaWRzIFszIDAgUl0gL0NvdW50IDEgPj4KZW5kb2JqCjMgMCBvYmoKPDwgL1R5cGUgL1BhZ2UgL1BhcmVudCAyIDAgUiAvTWVkaWFCb3ggWzAgMCAxMDAgMTAwXSAvUmVzb3VyY2VzIDw8IC9QYXR0ZXJuIDw8IC9QMCA1IDAgUiA+PiA+PiAvQ29udGVudHMgNCAwIFIgPj4KZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA0OSA+PgpzdHJlYW0KWy9QYXR0ZXJuIC9EZXZpY2VSR0JdIGNzIC9QMCBzY24gMCAwIDEwMCAxMDAgcmUgZgplbmRzdHJlYW0KZW5kb2JqCjUgMCBvYmoKPDwgL0xlbmd0aCA0NyAvVHlwZSAvUGF0dGVybiAvUGF0dGVyblR5cGUgMSAvUGFpbnRUeXBlIDEgL1RpbGluZ1R5cGUgMSAvQkJveCBbMCAwIDIwIDIwXSAvWFN0ZXAgMjAgL1lTdGVwIDIwID4+CnN0cmVhbQoxIDEgMSByZyAwIDAgMjAgMjAgcmUgZiAxIDAgMCByZyAwIDAgMTAgMjAgcmUgZgplbmRzdHJlYW0KZW5kb2JqCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAwMDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMDY0IDAwMDAwIG4gCjAwMDAwMDAxMjEgMDAwMDAgbiAKMDAwMDAwMDI1MCAwMDAwMCBuIAowMDAwMDAwMzQ5IDAwMDAwIG4gCnRyYWlsZXIKPDwgL1NpemUgNiAvUm9vdCAxIDAgUiA+PgpzdGFydHhyZWYKNTQxCiUlRU9GCg==";
    private static readonly int[] TilingExpected = { 153, 153, 187, 187, 153, 153, 187, 187 }; // 160x160 scale 1.600000

    [Theory]
    [InlineData(ShapesPdf, 0.800000, 160, 80, "Shapes")]
    [InlineData(CcittPdf, 8.888889, 160, 80, "Ccitt")]
    [InlineData(ShadingPdf, 0.800000, 160, 80, "Shading")]
    [InlineData(TilingPdf, 1.600000, 160, 160, "Tiling")]
    public void MatchesMuPdfReferenceSignature(string base64, double scale, int width, int height, string kind)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        using PdfDocument document = PdfDocument.FromArray(bytes);
        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(width, height);
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions((float)scale, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);

        int[] actual = Signature(px, image.Width, image.Height);
        int[] expected = kind switch { "Shapes" => ShapesExpected, "Ccitt" => CcittExpected, "Shading" => ShadingExpected, "Tiling" => TilingExpected, _ => ShapesExpected };

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(actual[i] - expected[i]) <= Tolerance,
                $"{kind} cell {i}: Poso={actual[i]} MuPDF={expected[i]}");
        }
    }

    internal static int[] Signature(byte[] px, int width, int height)
    {
        var cells = new int[GridW * GridH];
        for (int gy = 0; gy < GridH; gy++)
        {
            for (int gx = 0; gx < GridW; gx++)
            {
                int x0 = gx * width / GridW, x1 = Math.Max(x0 + 1, (gx + 1) * width / GridW);
                int y0 = gy * height / GridH, y1 = Math.Max(y0 + 1, (gy + 1) * height / GridH);
                long total = 0; int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int o = (y * width + x) * 4; // BGRA
                        total += (px[o] + px[o + 1] + px[o + 2]) / 3;
                        count++;
                    }
                }
                cells[(gy * GridW) + gx] = (int)(total / count);
            }
        }
        return cells;
    }
}
