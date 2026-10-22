# Corpus regression pipeline

Renders the pdf.js corpus with both Poso back ends and diffs every page against PDFium,
pixel for pixel. PDFium (via `pypdfium2`) is the only oracle: MuPDF's JBIG2/CCITT fallbacks
and colour handling mis-render whole corpus families, so a Poso-vs-MuPDF diff mixes "Poso is
wrong" with "MuPDF is wrong".

## Requirements

- Python with `pypdfium2` and Pillow (`pip install pypdfium2 pillow`)
- A directory of PDFs — default `D:\_git0\pdfjs\test\pdfs`, override with the `POSO_CORPUS`
  environment variable
- The .NET 10 SDK (for the `sweep` project)

## Usage

```bash
dotnet build sweep -c Release
python harness.py manifest      # list the corpus into manifest.tsv (page 0 of every PDF)
python harness.py pdfium        # render the oracle into out/ (resume-safe)
python harness.py sweep         # render Poso/Skia and Poso/Peach into out/ (resume-safe,
                                # crash-bisecting: a file that hangs the driver is recorded
                                # in out/stuck.tsv and skipped)
python cmp_pdfium.py            # out/report_pdfium.tsv, _summary.txt, _triage.txt
```

Method: scale 2.0 (144 dpi). A pixel counts as different when the average of its three channel
differences exceeds 16/255; the report's headline number is the fraction of such pixels. The
summary also carries an ink-coverage screen per engine — a file where Poso paints far less ink
than PDFium has missing content even when the pixel percentage looks small.

Files where PDFium is demonstrably the wrong side (it ignores `/UserUnit` and does not paint
annotation appearance streams; the manual list lives in `oracle_wrong.tsv`) are flagged in the
`caveat` column and kept out of the triage worklist — but still reported, never silently dropped.

To measure a candidate change against the same oracle without overwriting the baseline, render
into a second directory and point the comparison at it:

```bash
sweep/bin/Release/net10.0/sweep.exe manifest.tsv <other-dir> 2.0
POSO_DIR=<other-dir> REPORT_PREFIX=report_try python cmp_pdfium.py
```

The per-round findings (root causes, before/after numbers, spec clauses) are written to this
directory as `findings_pdfium.md` / `findings_pdfium_zh_CN.md`. Outputs under `out/` are
gitignored.

`scan_nocmap.py` lists the corpus files whose embedded TrueType program has no character map.
A CID font with such a program addresses its glyphs by id, which is legal, but a back end that
has to guess a character-to-glyph mapping guesses wrong - Skia's `GetGlyphPath` returns an empty
path for most of such a font's glyph ids, so the letters do not appear. The scan is what the
Round 41 entry in the findings is based on.
