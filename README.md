# Poso

**Poso** is a PDF viewer and rendering library for .NET, written entirely in managed C#.
It parses and renders PDF documents through a small backend-neutral graphics abstraction, so the
same core can draw with SkiaSharp, with the pure-managed PeachDrawing rasterizer, or with any
other backend you plug in — and it ships with an [Avalonia](https://avaloniaui.net/) viewer
control ready to drop into your app.

[简体中文](README.zh_CN.md)

## Features

**Parsing**

- Cross-reference tables, cross-reference streams, object streams, and recovery of damaged files
- Standard security handler: encrypted documents, user/owner passwords, async password prompts
- Whole file kept in memory for fast random access and simple thread safety (viewer scenarios)

**Content decoding**

- Filters: `FlateDecode` (with PNG/TIFF predictors), `LZWDecode`, `ASCII85Decode`,
  `ASCIIHexDecode`, `RunLengthDecode`
- Image codecs implemented in managed code: JPEG, JBIG2, CCITT Fax; JPEG 2000 (`JPXDecode`)
  through the managed CoreJ2K decoder
- Fonts: Type 1, CFF/OpenType, Type 0 (CID), Type 3, embedded and standard 14 fonts,
  CMap and ToUnicode mapping

**Rendering**

- Paths, clipping, text (all paint modes), images, shadings (types 1–7), patterns,
  transparency groups, soft masks, blend modes, forms (XObjects)
- Annotation appearance streams
- Optional content (layers) with per-group visibility

**Document features**

- Page tree with rotation, crop box, and user unit
- Text extraction with lines/spans and hit rectangles, plus in-page text search
- Links, destinations, outline (bookmarks), AcroForm fields

**Viewer**

- `PdfView` Avalonia control: single-page and continuous layouts, fit-page / fit-width / custom
  zoom, rotation, search with hit navigation, link activation and page-changed events
- Runtime switching between the Skia and PeachDrawing back ends

## Packages

| Project                 | What it is                                                       |
| ----------------------- | ---------------------------------------------------------------- |
| `src/Poso`              | Core: PDF parsing, text, and backend-neutral rendering pipeline  |
| `src/Poso.Skia`         | Graphics backend on SkiaSharp                                    |
| `src/Poso.PeachDrawing` | Graphics backend on PeachDrawing (pure managed, no native deps)  |
| `src/Poso.Avalonia`     | `PdfView` control, page render pipeline, bitmap conversion       |
| `tests/Poso.Tests`      | xUnit test suite                                                 |
| `samples/Sample.Avalonia` | Full viewer sample: Desktop, Browser, Android, iOS             |

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

### Render a page to a bitmap

```csharp
using Poso.Pdf;
using Poso.Skia;

using PdfDocument document = PdfDocument.Open("example.pdf");
PdfPage page = document.Pages[0];

const double scale = 2.0; // 2x the 72 dpi PDF user space
int width  = (int)Math.Ceiling(page.Width  * scale);
int height = (int)Math.Ceiling(page.Height * scale);

IGraphicsFactory factory = SkiaGraphicsFactory.Default;
using IGraphicsContext context = factory.CreateContext(width, height);
page.Render(factory, context, new PdfRenderOptions((float)scale, 0f, PosoColor.White));
using IImage image = context.Snapshot();
// image.CopyPixels(...) to get at the BGRA pixels, or convert per backend
```

Swap `SkiaGraphicsFactory.Default` for `Poso.PeachDrawing.PeachGraphicsFactory.Default` to render
without any native dependency — nothing else in your code changes.

### Open a password-protected document

```csharp
PdfDocument document = await PdfDocument.OpenAsync(path, () => PromptPassword());
if (document.NeedsPassword)
{
    // the prompt returned the wrong password; ask again
}
```

### Extract text

```csharp
PdfTextPage text = document.Pages[0].GetText(SkiaGraphicsFactory.Default);
string wholePage = text.Text;
IReadOnlyList<TextLine> lines = text.Lines;
IReadOnlyList<TextHit> hits = text.Search("Poso");
```

### Embed the viewer (Avalonia)

```xml
xmlns:controls="using:Poso.Avalonia.Controls"
...
<controls:PdfView Source="{Binding Document}"
                  ZoomMode="FitWidth"
                  PageLayout="Continuous"
                  PageChanged="OnPageChanged"
                  LinkActivated="OnLinkActivated" />
```

See `samples/Sample.Avalonia` for a complete viewer with outline, thumbnails, search, password
prompt, and a live back-end toggle.

## Building and testing

```bash
dotnet build Poso.slnx
dotnet test tests/Poso.Tests
dotnet run --project samples/Sample.Avalonia/Sample.Avalonia.Desktop
```

## Rendering fidelity

Poso's rendering is validated against **PDFium** (Chrome's engine) on the pdf.js corpus
(`pdfjs/test/pdfs`, 977 PDFs, first page, scale 2.0). The tooling lives in
[`tools/corpus`](tools/corpus) and its findings in
[`tools/corpus/findings_pdfium.md`](tools/corpus/findings_pdfium.md).

| vs PDFium (953 comparable files) | Skia | Peach |
| --- | --- | --- |
| within 1% pixel diff | 552 | 559 |
| within 5% | 822 (86.3%) | 824 (86.5%) |
| pages Poso cannot open | 10 | 10 |

After excluding the files where PDFium itself is known to be the wrong side (it ignores `/UserUnit`
and does not paint annotation appearances), no comparable file differs from the oracle by more than
50%, and the only remaining blank-vs-ink gap is one annotation-chrome page. The pipeline renders the
whole corpus in about four minutes per back end and diffs every page pixel-for-pixel.

## License

MIT — see [LICENSE](LICENSE).
