using System;
using System.IO;
using Poso.Geometry;
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

    /// <summary>
    /// A CID-keyed subset can carry a CFF header longer than 4 bytes: pdf.js issue13147.pdf writes a
    /// 12-byte header. The readers used to start the INDEX walk at a fixed 4, which parsed the tables as
    /// garbage, so the font was rejected and its CJK text drew in a substitute. The walk must begin at
    /// the header size the program declares.
    /// </summary>
    [Fact]
    public void CidCffProgram_LongHeader_ReadsItsCidToGidMap()
    {
        byte[] cff = File.ReadAllBytes(FontsSample("futogob-cid-subset.bin"));
        Assert.Equal(12, cff[2]);

        Assert.True(CidCffProgram.TryCreate(cff, out CidCffProgram? program));
        Assert.Equal(99, program!.GlyphCount);

        // The page's first code, CID 0x096A, is glyph 79 of the subset - not 2410, which is what an
        // identity assumption would hand the renderer.
        Assert.True(program.CidToGid.TryGetValue(0x096A, out ushort gid));
        Assert.Equal((ushort)79, gid);
    }

    /// <summary>
    /// Generators write an identity CharMatrix as a placeholder: pdf.js issue11713.pdf's font dict says
    /// [1 0 0 1 0 0] while its top DICT carries the real 1/1000. Taking the identity would put the
    /// wrapper's unitsPerEm at 1 and blow every glyph up to a thousand em, so it must be skipped and
    /// the parent's scale kept.
    /// </summary>
    [Fact]
    public void CidCffProgram_IdentityCharMatrixInFontDict_KeepsTheParentScale()
    {
        byte[] cff = File.ReadAllBytes(FontsSample("issue11713-cid-identity-fd.bin"));
        Assert.True(CidCffProgram.TryCreate(cff, out CidCffProgram? program));
        Assert.NotNull(program);

        // The top DICT declares 1/1000 and the FD's identity must not override it.
        Assert.Equal(0.001f, program!.FontMatrix[0], 6);
        Assert.Equal(0.001f, program.FontMatrix[3], 6);
    }

    private static string FontsSample(string name)
    {
        string probe = IOPath.Combine(AppContext.BaseDirectory, "Samples", "Fonts", name);
        if (File.Exists(probe))
        {
            return probe;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = IOPath.Combine(dir.FullName, "tests", "Poso.Tests", "Samples", "Fonts", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"font sample '{name}' not found.", name);
    }

    /// <summary>
    /// A CID-keyed font dict declares its design-space scale as CharMatrix (12 7), not FontMatrix (7):
    /// pdf.js text_clip_cff_cid.pdf's FD carries only 12 7, at 1/2048. The OpenType wrapper derives its
    /// unitsPerEm from this matrix, so reading the wrong one scaled every glyph 2x - PeachDrawing drew
    /// the page's outlines at two em wide until the FD matrix was read.
    /// </summary>
    [Fact]
    public void CidCffProgram_CharMatrixInFontDict_BecomesTheDesignScale()
    {
        byte[] cff = File.ReadAllBytes(FontsSample("reitam-cid-charmatrix.bin"));
        Assert.True(CidCffProgram.TryCreate(cff, out CidCffProgram? program));
        Assert.NotNull(program);

        // 1/2048, not the 1/1000 default: the FD's CharMatrix must win over the absent top FontMatrix.
        Assert.Equal(0.000488281f, program!.FontMatrix[0], 6);
        Assert.Equal(0.000488281f, program.FontMatrix[3], 6);
    }

    /// <summary>
    /// The outline reader must run the charstrings under the same FD CharMatrix: a glyph of this font
    /// spans about 1300 design units, which is 0.63 em at 1/2048 and 1.3 em at the 1/1000 default -
    /// the doubled reading is exactly what made the glyphs overlap.
    /// </summary>
    [Fact]
    public void CidCffOutlines_CharMatrixScalesGlyphsToEm()
    {
        byte[] cff = File.ReadAllBytes(FontsSample("reitam-cid-charmatrix.bin"));
        Assert.True(CffOutlines.TryCreate(cff, out OutlineSource? outlines) && outlines is not null);

        var builder = new PathBuilder();
        Assert.True(outlines!.TryGetOutline(24, builder), "gid 24 must yield an outline");
        RectF b = builder.Build().Bounds;

        // The charstring's full point extent (control points included, as fontTools reads it) is
        // (39, 0)-(1361, 1448); at 1/2048 that is 0.65 em wide and 0.71 em tall. A 2x reading would
        // land near 1.3.
        Assert.True(b.Width < 0.7f, $"the CharMatrix must scale the outline to em, got width {b.Width}");
        Assert.True(b.Height < 0.75f, $"the CharMatrix must scale the outline to em, got height {b.Height}");
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
