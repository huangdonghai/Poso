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

    /// <summary>
    /// The ExtGState /SMask <b>Mask dictionary</b> (its /G names the group, /S the subtype, /BC the
    /// backdrop), or null when the element is unmasked.
    /// </summary>
    public Objects.PdfDictionary? SoftMask { get; set; }

    /// <summary>
    /// The CTM that was current when the soft mask was set. The mask group is a piece of page geometry
    /// - it is placed by that transform, not by whatever the element being masked has since stacked on
    /// top of it - so the two only coincide for content that never calls <c>cm</c>.
    /// </summary>
    public Matrix3x2 SoftMaskCtm = Matrix3x2.Identity;

    /// <summary>Alpha isolation (ExtGState /AIS): groups do not blend with the backdrop.</summary>
    public bool AlphaIsolate { get; set; }

    /// <summary>Text knockout (ExtGState /TK).</summary>
    public bool TextKnockout { get; set; }

    /// <summary>
    /// True while the content is being drawn into a transparency group whose /K (knockout) flag is set.
    /// Such a group clears its earlier contents wherever a new element paints, so a nested group cannot
    /// blend with what is already on the surface: it has to be composited as if the backdrop beneath it
    /// were transparent (PDF 32000-1 11.3.3.3).
    /// </summary>
    public bool GroupIsKnockout { get; set; }

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
        SoftMaskCtm = SoftMaskCtm,
        AlphaIsolate = AlphaIsolate,
        TextKnockout = TextKnockout,
        GroupIsKnockout = GroupIsKnockout,
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
