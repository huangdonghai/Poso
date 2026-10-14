using Poso.Geometry;
using Poso.Pdf.Objects;

namespace Poso.Pdf;

/// <summary>Interactive form field type (ISO 32000-1 12.7.3), resolved from the inherited /FT plus /Ff flags.</summary>
public enum PdfFieldType
{
    Unknown,
    TextField,
    CheckBox,
    RadioButton,
    PushButton,
    Dropdown,
    ListBox,
    Signature,
}

/// <summary>A single choice option of a combo/list box field: an export value and a (possibly same) display value.</summary>
public readonly record struct PdfChoiceOption(string ExportValue, string DisplayValue);

/// <summary>
/// A form field read from the document's AcroForm (read-only view: name, type, current value, options and
/// bounding rectangle). Field attributes that PDF inherits down the hierarchy (/FT, /Ff, /DA, and /V for
/// non-button fields) are resolved by walking the /Parent chain, per ISO 32000-1 12.3.4.3. Filling in
/// values and regenerating appearances (interactive editing) is intentionally out of scope.
/// </summary>
public sealed class PdfFormField
{
    internal PdfFormField(
        string name,
        string fullyQualifiedName,
        PdfFieldType type,
        string? fieldTypeRaw,
        int flags,
        string? value,
        RectF? rect,
        IReadOnlyList<PdfChoiceOption> options,
        IReadOnlyList<PdfFormField> children)
    {
        Name = name;
        FullyQualifiedName = fullyQualifiedName;
        Type = type;
        FieldTypeRaw = fieldTypeRaw;
        Flags = flags;
        Value = value;
        Rect = rect;
        Options = options;
        Children = children;
    }

    /// <summary>Partial field name (/T), without the ancestor prefix.</summary>
    public string Name { get; }

    /// <summary>Dotted full name formed from all ancestor /T values (ISO 32000-1 12.3.4.2).</summary>
    public string FullyQualifiedName { get; }

    public PdfFieldType Type { get; }

    /// <summary>Raw inherited /FT token ("Btn"/"Tx"/"Ch"/"Sig"), or null when absent.</summary>
    public string? FieldTypeRaw { get; }

    /// <summary>Inherited field flags (/Ff) as a raw bit field.</summary>
    public int Flags { get; }

    /// <summary>Whether the field is read-only (ReadOnly flag, bit 1).</summary>
    public bool IsReadOnly => (Flags & (1 << 0)) != 0;

    /// <summary>Current value (/V, inherited where applicable). Multi-value fields join parts with '\n'.</summary>
    public string? Value { get; }

    /// <summary>Bounding rectangle in page user space (y-up) from the field's widget, if any.</summary>
    public RectF? Rect { get; }

    /// <summary>Choice options (/Opt) for dropdown/list fields; empty otherwise.</summary>
    public IReadOnlyList<PdfChoiceOption> Options { get; }

    /// <summary>Child fields (widget annotations are folded into the parent field, not listed here).</summary>
    public IReadOnlyList<PdfFormField> Children { get; }
}

/// <summary>
/// A document's interactive form (AcroForm): the root field list plus document-wide defaults. Obtained from
/// <see cref="PdfDocument.AcroForm"/>; <c>null</c> when the document declares no form.
/// </summary>
public sealed class PdfAcroForm
{
    private const int ComboBit = 1 << 17;   // /Ch: combo box (dropdown) vs list box
    private const int PushbuttonBit = 1 << 16; // /Btn: push button
    private const int RadioBit = 1 << 15;   // /Btn: radio button group
    private const int MultilineBit = 1 << 12;  // /Tx: multi-line text

    internal PdfAcroForm(PdfDocument document, PdfDictionary dictionary, IReadOnlyList<PdfFormField> fields)
    {
        Document = document;
        Dictionary = dictionary;
        Fields = fields;
        DefaultAppearance = (document.Resolve(dictionary.Get("DA")) as PdfString)?.AsTextString() ?? string.Empty;
        NeedAppearances = dictionary.GetBooleanValue("NeedAppearances") ?? false;
    }

    private PdfDocument Document { get; }

    internal PdfDictionary Dictionary { get; }

    /// <summary>Root form fields (each may itself have <see cref="PdfFormField.Children"/>).</summary>
    public IReadOnlyList<PdfFormField> Fields { get; }

    /// <summary>Document-wide default appearance string (/DA), inherited by fields.</summary>
    public string DefaultAppearance { get; }

    /// <summary>Whether the viewer is asked to regenerate appearance streams (/NeedAppearances).</summary>
    public bool NeedAppearances { get; }

    /// <summary>Loads the AcroForm referenced from the document catalog, or <c>null</c> when there is none.</summary>
    internal static PdfAcroForm? Load(PdfDocument document)
    {
        if (document.Catalog is not { } catalog
            || document.Resolve(catalog.Get("AcroForm")) is not PdfDictionary acroForm)
        {
            return null;
        }

        var fields = new List<PdfFormField>();
        if (document.Resolve(acroForm.Get("Fields")) is PdfArray fieldArray)
        {
            foreach (PdfObject entry in fieldArray)
            {
                PdfFormField? field = ParseField(document, entry, prefix: string.Empty);
                if (field is not null)
                {
                    fields.Add(field);
                }
            }
        }

        return new PdfAcroForm(document, acroForm, fields);
    }

    /// <summary>Depth guard against malformed parent/kid cycles.</summary>
    private const int MaxDepth = 128;

    private static PdfFormField? ParseField(PdfDocument document, PdfObject reference, string prefix, int depth = 0)
    {
        if (depth > MaxDepth || document.Resolve(reference) is not PdfDictionary dictionary)
        {
            return null;
        }

        string partial = (document.Resolve(dictionary.Get("T")) as PdfString)?.AsTextString() ?? string.Empty;
        string fullName = partial.Length == 0
            ? prefix
            : prefix.Length == 0 ? partial : prefix + "." + partial;

        string? fieldType = InheritedName(document, dictionary, "FT");
        int flags = (int)(InheritedInteger(document, dictionary, "Ff") ?? 0);
        PdfFieldType type = Classify(fieldType, flags);

        // /V must not be inherited by button children; everything else inherits it down the chain.
        PdfObject? valueObject = type is PdfFieldType.CheckBox or PdfFieldType.RadioButton or PdfFieldType.PushButton
            ? dictionary.Get("V")
            : Inherited(document, dictionary, "V");
        string? value = ReadValue(document, valueObject);

        IReadOnlyList<PdfChoiceOption> options = type is PdfFieldType.Dropdown or PdfFieldType.ListBox
            ? ReadOptions(document, dictionary)
            : Array.Empty<PdfChoiceOption>();

        var children = new List<PdfFormField>();
        RectF? rect = ReadOwnRect(document, dictionary);

        if (document.Resolve(dictionary.Get("Kids")) is PdfArray kids)
        {
            foreach (PdfObject kidObject in kids)
            {
                if (document.Resolve(kidObject) is not PdfDictionary kid)
                {
                    continue;
                }

                if (kid.GetNameValue("Subtype") == "Widget")
                {
                    // Widget annotation: contributes the rectangle, not a child field.
                    rect ??= ReadOwnRect(document, kid);
                    continue;
                }

                PdfFormField? child = ParseField(document, kidObject, fullName, depth + 1);
                if (child is not null)
                {
                    children.Add(child);
                }
            }
        }

        return new PdfFormField(partial, fullName, type, fieldType, flags, value, rect, options, children);
    }

    private static PdfFieldType Classify(string? fieldType, int flags) => fieldType switch
    {
        "Tx" => PdfFieldType.TextField,
        "Sig" => PdfFieldType.Signature,
        "Ch" => (flags & ComboBit) != 0 ? PdfFieldType.Dropdown : PdfFieldType.ListBox,
        "Btn" when (flags & PushbuttonBit) != 0 => PdfFieldType.PushButton,
        "Btn" when (flags & RadioBit) != 0 => PdfFieldType.RadioButton,
        "Btn" => PdfFieldType.CheckBox,
        _ => PdfFieldType.Unknown,
    };

    private static RectF? ReadOwnRect(PdfDocument document, PdfDictionary dictionary)
    {
        if (document.Resolve(dictionary.Get("Rect")) is not PdfArray rect || rect.Count < 4)
        {
            return null;
        }

        return RectF.FromLbrb(
            rect.GetNumber(0)?.AsFloat() ?? 0f,
            rect.GetNumber(1)?.AsFloat() ?? 0f,
            rect.GetNumber(2)?.AsFloat() ?? 0f,
            rect.GetNumber(3)?.AsFloat() ?? 0f);
    }

    private static IReadOnlyList<PdfChoiceOption> ReadOptions(PdfDocument document, PdfDictionary dictionary)
    {
        if (document.Resolve(dictionary.Get("Opt")) is not PdfArray options)
        {
            return Array.Empty<PdfChoiceOption>();
        }

        var list = new List<PdfChoiceOption>(options.Count);
        foreach (PdfObject entry in options)
        {
            if (document.Resolve(entry) is PdfArray pair && pair.Count >= 2)
            {
                list.Add(new PdfChoiceOption(ReadValue(document, pair[0]) ?? string.Empty, ReadValue(document, pair[1]) ?? string.Empty));
            }
            else
            {
                string? value = ReadValue(document, entry);
                if (value is not null)
                {
                    list.Add(new PdfChoiceOption(value, value));
                }
            }
        }

        return list;
    }

    // ---- Inheritance helpers (ISO 32000-1 12.3.4.3): walk up the /Parent chain ----

    private static PdfObject? Inherited(PdfDocument document, PdfDictionary field, string key)
    {
        PdfDictionary? current = field;
        int depth = 0;
        while (current is not null && depth++ < MaxDepth)
        {
            PdfObject? value = current.Get(key);
            if (value is not null)
            {
                return value;
            }

            current = document.Resolve(current.Get("Parent")) as PdfDictionary;
        }

        return null;
    }

    private static string? InheritedName(PdfDocument document, PdfDictionary field, string key) =>
        (document.Resolve(Inherited(document, field, key)) as PdfName) switch
        {
            PdfName name => name.Value,
            _ => null,
        };

    private static long? InheritedInteger(PdfDocument document, PdfDictionary field, string key) =>
        (document.Resolve(Inherited(document, field, key)) as PdfNumber) switch
        {
            PdfNumber number => number.AsLong(),
            _ => null,
        };

    private static string? ReadValue(PdfDocument document, PdfObject? raw)
    {
        switch (document.Resolve(raw))
        {
            case null:
                return null;
            case PdfString text:
                return text.AsTextString();
            case PdfName name:
                return name.Value;
            case PdfArray array:
            {
                var parts = new List<string>(array.Count);
                foreach (PdfObject item in array)
                {
                    string? part = ReadValue(document, item);
                    if (part is not null)
                    {
                        parts.Add(part);
                    }
                }

                return parts.Count == 0 ? null : string.Join("\n", parts);
            }

            default:
                return null;
        }
    }
}
