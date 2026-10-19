using Poso.Pdf.Objects;

namespace Poso.Pdf.Filters;

/// <summary>Stream filter chain. Image codec headers (DCT/JPX/CCITT/JBIG2) are passed through unchanged to the image layer.</summary>
public static class PdfFilters
{
    /// <summary>Apply the complete filter chain to the raw stream data.</summary>
    public static byte[] Apply(PdfStream stream) => Apply(stream, null);

    /// <summary>Apply the complete filter chain to the raw stream data, reporting unsupported filters to <paramref name="diagnostics"/>.</summary>
    public static byte[] Apply(PdfStream stream, PdfDiagnostics? diagnostics) =>
        Apply(stream.RawData.ToArray(), stream.Dictionary, diagnostics);

    /// <summary>Apply the filter chain described by <paramref name="dictionary"/> to <paramref name="rawBytes"/>.</summary>
    public static byte[] Apply(byte[] rawBytes, PdfDictionary dictionary, PdfDiagnostics? diagnostics)
    {
        var filters = new List<string>();
        var parms = new List<PdfDictionary?>();

        switch (dictionary.Get("Filter"))
        {
            case PdfName name:
                filters.Add(name.Value);
                parms.Add(GetParms(dictionary.Get("DecodeParms"), 0));
                break;

            case PdfArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is PdfName filterName)
                    {
                        filters.Add(filterName.Value);
                        parms.Add(GetParms(dictionary.Get("DecodeParms"), filters.Count - 1));
                    }
                }

                break;
        }

        return ApplyChain(rawBytes, filters, parms, diagnostics);
    }

    /// <summary>Apply filters in order; stop and return the current data when an image codec header is encountered.</summary>
    public static byte[] ApplyChain(byte[] data, List<string> filters, List<PdfDictionary?> parms) =>
        ApplyChain(data, filters, parms, null);

    /// <summary>
    /// Apply filters in order. Unsupported filters (Crypt and unknown) are skipped as a no-op and
    /// reported through <paramref name="diagnostics"/>, unless that instance is strict (then it throws).
    /// </summary>
    public static byte[] ApplyChain(byte[] data, List<string> filters, List<PdfDictionary?> parms, PdfDiagnostics? diagnostics)
    {
        byte[] current = data;
        for (int i = 0; i < filters.Count; i++)
        {
            string filter = filters[i];
            PdfDictionary? parm = i < parms.Count ? parms[i] : null;

            switch (filter)
            {
                case "FlateDecode":
                case "Fl":
                    current = FlateDecodeFilter.Decode(current);
                    current = Predictors.Apply(current, parm);
                    break;

                case "LZWDecode":
                case "LZW":
                    current = LzwDecodeFilter.Decode(current, parm);
                    current = Predictors.Apply(current, parm);
                    break;

                case "ASCIIHexDecode":
                case "AHx":
                    current = AsciiHexDecodeFilter.Decode(current);
                    break;

                case "ASCII85Decode":
                case "A85":
                    current = Ascii85DecodeFilter.Decode(current);
                    break;

                case "RunLengthDecode":
                case "RL":
                    current = RunLengthDecodeFilter.Decode(current);
                    break;

                case "BrotliDecode":
                case "Brotli":
                    current = BrotliDecodeFilter.Decode(current);
                    break;

                case "DCTDecode":
                case "DCT":
                case "JPXDecode":
                case "CCITTFaxDecode":
                case "CCF":
                case "JBIG2Decode":
                    // Image data: decode in the image layer (the core does not handle JPEG/JPEG2000/CCITT/JBIG2).
                    return current;

                case "Crypt":
                    diagnostics?.Report(PdfUnsupportedKind.CryptFilter, "The Crypt filter (encrypted content) is not supported yet");
                    break;

                default:
                    diagnostics?.Report(PdfUnsupportedKind.StreamFilter, $"Unsupported stream filter: {filter}");
                    break;
            }
        }

        return current;
    }

    private static PdfDictionary? GetParms(PdfObject? decodeParms, int index)
    {
        return decodeParms switch
        {
            PdfDictionary dictionary => dictionary,
            PdfArray array => index >= 0 && index < array.Count ? array[index] as PdfDictionary : null,
            _ => null,
        };
    }
}
