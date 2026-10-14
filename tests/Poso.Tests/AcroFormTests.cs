using System.Collections.Generic;
using System.Linq;
using Poso.Pdf;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M4 (non-interactive subset): reading AcroForm field structure — typing from /FT + /Ff, attribute
/// inheritance down the /Parent chain, dotted fully-qualified names, choice options and widget rectangles.
/// Interactive filling / appearance regeneration is intentionally out of scope.
/// </summary>
public class AcroFormTests
{
    private static PdfFormField Field(PdfDocument document, string fullName) =>
        document.GetFormFields().First(f => f.FullyQualifiedName == fullName);

    [Fact]
    public void FlattenedFields_IncludeRootsAndDescendants()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAcroFormPdf());

        IReadOnlyList<PdfFormField> fields = document.GetFormFields();
        string[] names = fields.Select(f => f.FullyQualifiedName).ToArray();

        // name, person, person.first, person.last, color, agree, gender, langs
        Assert.Equal(8, fields.Count);
        Assert.Contains("person.first", names);
        Assert.Contains("person.last", names);
    }

    [Fact]
    public void TextField_ReadsValueAndWidgetRect()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAcroFormPdf());
        PdfFormField name = Field(document, "name");

        Assert.Equal(PdfFieldType.TextField, name.Type);
        Assert.Equal("John Doe", name.Value);
        Assert.NotNull(name.Rect);
        Assert.Equal(50f, name.Rect!.Value.Left);
        Assert.Equal(200f, name.Rect!.Value.Width); // 250 - 50
    }

    [Fact]
    public void Inheritance_FieldTypeAndValueResolveThroughParentChain()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAcroFormPdf());

        // "first" declares its own value but inherits /FT /Tx from the "person" group.
        PdfFormField first = Field(document, "person.first");
        Assert.Equal(PdfFieldType.TextField, first.Type);
        Assert.Equal("Ada", first.Value);

        // "last" has neither /FT nor /V: both are inherited from the group.
        PdfFormField last = Field(document, "person.last");
        Assert.Equal(PdfFieldType.TextField, last.Type);
        Assert.Equal("DefaultV", last.Value);
    }

    [Fact]
    public void Dropdown_ParsesMixedOptionsAndValue()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAcroFormPdf());
        PdfFormField color = Field(document, "color");

        Assert.Equal(PdfFieldType.Dropdown, color.Type);
        Assert.Equal("g", color.Value);
        Assert.Equal(3, color.Options.Count);

        // Plain string option: export == display.
        Assert.Equal("Plain", color.Options[0].ExportValue);
        Assert.Equal("Plain", color.Options[0].DisplayValue);

        // Paired option [export display].
        Assert.Equal("r", color.Options[1].ExportValue);
        Assert.Equal("Red", color.Options[1].DisplayValue);
    }

    [Fact]
    public void ButtonTypes_DistinguishedByFlags()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAcroFormPdf());

        PdfFormField agree = Field(document, "agree");
        Assert.Equal(PdfFieldType.CheckBox, agree.Type);
        Assert.Equal("On", agree.Value); // /V is a name; read as its token
        Assert.NotNull(agree.Rect);       // rectangle taken from the field's own widget entry

        Assert.Equal(PdfFieldType.RadioButton, Field(document, "gender").Type);
        Assert.Equal("male", Field(document, "gender").Value);
    }

    [Fact]
    public void ListBox_WhenChoiceWithoutComboFlag()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateAcroFormPdf());
        PdfFormField langs = Field(document, "langs");

        Assert.Equal(PdfFieldType.ListBox, langs.Type);
        Assert.Equal("b", langs.Value);
        Assert.Equal(3, langs.Options.Count);
    }

    [Fact]
    public void NoForm_Document_ReturnsEmptyFields()
    {
        using PdfDocument document = PdfDocument.FromArray(PdfFixtures.CreateImageSoftMaskPdf());

        Assert.Null(document.AcroForm);
        Assert.Empty(document.GetFormFields());
    }
}
