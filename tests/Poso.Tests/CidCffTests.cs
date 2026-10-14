using System;
using System.IO;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Fonts;
using Poso.Skia;
using Xunit;
using IOPath = System.IO.Path;

namespace Poso.Tests;

/// <summary>
/// CID-keyed CFF (FontFile3 /CIDFontType0C) support: the embedded program used to be rejected by the
/// name-keyed reader, so a Type 0 / Identity CJK font fell through to a system substitute and rendered
/// the wrong glyphs. The fix passes the raw CFF through to the renderer and derives the CID->GID map
/// from the program's charset. This surfaced on a real 732-page book whose fonts MuPDF rendered fine.
/// </summary>
public class CidCffTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void CidCffProgram_ReadsNonIdentityCharsetIntoCidToGidMap()
    {
        byte[] cff = PdfFixtures.BuildCidKeyedCffProgram(out int glyphCount, out (int Cid, int Gid)[] expected);

        Assert.True(CidCffProgram.TryCreate(cff, out CidCffProgram? program));
        Assert.NotNull(program);
        Assert.Equal(glyphCount, program!.GlyphCount);

        // The map must come from the charset (GID order), not from an identity assumption.
        foreach ((int cid, int gid) in expected)
        {
            Assert.True(program.CidToGid.TryGetValue(cid, out ushort found), $"CID {cid} missing");
            Assert.Equal((ushort)gid, found);
        }

        // A CID that is not in the charset has no glyph (and is not silently mapped to itself).
        Assert.False(program.CidToGid.TryGetValue(2, out _));
    }

    [Fact]
    public void CidKeyedCffPdf_RendersEmbeddedFont()
    {
        byte[] pdf = File.ReadAllBytes(PdfSample("cid_cff.pdf"));
        using PdfDocument document = PdfDocument.FromArray(pdf);
        PdfPage page = document.Pages[0];

        int width = (int)Math.Ceiling(page.Width);
        int height = (int)Math.Ceiling(page.Height);
        using IGraphicsContext context = Factory.CreateContext(width, height);
        page.Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));
        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * 4];
        image.CopyPixels(px);

        int ink = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i] < 128 || px[i + 1] < 128 || px[i + 2] < 128)
            {
                ink++;
            }
        }

        // MuPDF renders "processor reference chart" with ~1494 dark pixels at scale 1; the embedded
        // CID CFF must produce comparable ink (a blank or rejected-font page would be far lower).
        Assert.InRange(ink, 1200, 1900);
    }

    private static string PdfSample(string name)
    {
        string probe = IOPath.Combine(AppContext.BaseDirectory, "Samples", "Pdf", name);
        if (File.Exists(probe))
        {
            return probe;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = IOPath.Combine(dir.FullName, "tests", "Poso.Tests", "Samples", "Pdf", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"PDF sample '{name}' not found.", name);
    }
}
