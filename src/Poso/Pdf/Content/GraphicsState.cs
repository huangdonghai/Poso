using System.Numerics;
using Poso.Graphics;
using Poso.Pdf.Fonts;

namespace Poso.Pdf.Content;

/// <summary>
/// Graphics state (PDF 32000-1:2008 8.4). q/Q saves and restores the entire graphics state,
/// including the text state and the CTM.
/// </summary>
internal sealed class GraphicsState
{
    public Matrix3x2 Ctm = Matrix3x2.Identity;

    public PosoColor FillColor = PosoColor.Black;

    public PosoColor StrokeColor = PosoColor.Black;

    public StrokeStyle Stroke = StrokeStyle.Default;

    // Text state
    public PdfFont? Font { get; set; }

    public float FontSize { get; set; }

    public float CharSpacing { get; set; }

    public float WordSpacing { get; set; }

    public float HorizontalScale { get; set; } = 1f;

    public float Leading { get; set; }

    public float TextRise { get; set; }

    public TextRenderMode RenderMode { get; set; } = TextRenderMode.Fill;

    // Extended graphics state (the commonly used subset of ExtGState)
    public float FillAlpha { get; set; } = 1f;

    public float StrokeAlpha { get; set; } = 1f;

    public BlendMode BlendMode { get; set; } = BlendMode.Normal;

    /// <summary>Soft-mask image (ExtGState /SMask) applied when painting; null when none.</summary>
    public Objects.PdfStream? SoftMask { get; set; }

    /// <summary>Alpha isolation (ExtGState /AIS): groups do not blend with the backdrop.</summary>
    public bool AlphaIsolate { get; set; }

    /// <summary>Text knockout (ExtGState /TK).</summary>
    public bool TextKnockout { get; set; }

    public GraphicsState Clone() => new()
    {
        Ctm = Ctm,
        FillColor = FillColor,
        StrokeColor = StrokeColor,
        Stroke = Stroke,
        Font = Font,
        FontSize = FontSize,
        CharSpacing = CharSpacing,
        WordSpacing = WordSpacing,
        HorizontalScale = HorizontalScale,
        Leading = Leading,
        TextRise = TextRise,
        RenderMode = RenderMode,
        FillAlpha = FillAlpha,
        StrokeAlpha = StrokeAlpha,
        BlendMode = BlendMode,
        SoftMask = SoftMask,
        AlphaIsolate = AlphaIsolate,
        TextKnockout = TextKnockout,
    };
}

/// <summary>Current color space kind (determines the component count of scn/SCN operands).</summary>
internal enum ColorSpaceKind
{
    Unknown,
    DeviceGray,
    DeviceRgb,
    DeviceCmyk,
    Indexed,
    Separation,
    DeviceN,
    Pattern,
    IccBased,
    Lab,
    CalGray,
    CalRgb,
}
