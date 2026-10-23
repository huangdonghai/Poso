# Poso vs PDFium — corpus sweep findings

Corpus: `D:\_git0\pdfjs\test\pdfs` (977 PDFs), page 0, scale 2.0 (144 dpi).
Oracle: PDFium via `pypdfium2` (`out/*-pd.png`). MuPDF is no longer used.
Produced by `cmp_pdfium.py`; raw data in `out/report_pdfium.tsv`, worklist in `out/triage_pdfium.txt`.

## Coverage

| | ok | failed |
| --- | --- | --- |
| PDFium | 954 | 23 (13 encrypted, 9 malformed, 1 OOM) |
| Poso/Skia | 967 | 10 |
| Poso/Peach | 967 | 10 |

953 files were compared Skia-vs-PDFium, 953 Peach-vs-PDFium. 9 files fail in both engines.

## Agreement (pixels differing by >16/255)

| bucket | Skia | Peach | Skia, oracle caveats excluded |
| --- | --- | --- | --- |
| identical (<1%) | 550 | 555 | 548 |
| close (<5%) | 266 | 263 | 258 |
| moderate (<20%) | 126 | 124 | 115 |
| big (<50%) | 8 | 8 | 4 |
| huge (>=50%) | 3 | 3 | 0 |

So 85.6% of comparable files are within 5% of PDFium, and 98.8% within 20%; with the oracle
caveats excluded, no file is at or above 50%. Files where Poso paints nothing while PDFium has
content are down from 11 to **1** per backend (rc_annotation, annotation chrome, recorded only).

## PDFium is not a fair oracle on 24 files

Kept out of the "Poso is wrong" tally, listed in `out/report_pdfium.tsv` column `caveat`:

- `oracle-annots` (21) — PDFium's `FPDF_RenderPageBitmap` does not paint annotation appearance
  streams; Poso does (by design, see README). The page canvas differs by the whole annotation.
  Examples: `annotation-tx2`, `bug1844576`, `160F-2019` (AcroForm field shading), `resetform`.
- `oracle-userunit` (3) — PDFium ignores `/UserUnit`. Poso is the correct side:
  `issue19176` (`/UserUnit 72`), `bug1947248_text` / `bug1947248_forms` (`/UserUnit 3`).
- `PDFIUM-BLANK` (1) — `jbig2_file_header`: PDFium cannot decode a JBIG2 stream that carries a
  JBIG2 file header and paints nothing; Poso decodes it (43% ink). pdf.js lists this file as
  `type: eq`, so content is expected and PDFium is the questionable side.

## Round 2 — fixed, and a warning about the Peach numbers

Two fixes landed after the first pass (all 300 unit tests pass, 3 new ones added):

1. **Box arrays whose elements are indirect are now resolved element by element**
   (`PdfDocument.GetArrayNumber`, used by the page boxes, a form's `/BBox` and `/Matrix`, a tiling
   pattern's `/BBox`, and an annotation's `/Rect`/`/BBox`). Verified across the corpus:
   `franz_2` Peach 97.31% -> 2.83%; `issue7872` renders at 500x100 instead of 1224x1584 (sk 6.19 ->
   4.97, pe 6.19 -> 4.35); `issue15441` 7.41 -> 2.05; `issue10542_reduced` 6.16 -> 2.16;
   `issue11403_reduced` 5.98 -> 4.01; `issue8480` and `poppler-90-0-fuzzed` (previously a 60 s hang)
   now render at all. No file got worse.
2. **CalGray/CalRGB now adapt their declared white point to D65** (`Cielab.AdaptToD65`). `calgray`
   had a pink cast on every cell; it is now neutral. Note this file is one where **PDFium is the
   wrong oracle**: PDFium writes `g*255` and skips the transfer function, while pdf.js's own
   reference formula (`295.8*L^(1/3)-40.8`) is what the corpus expects. Poso's mean difference from
   that formula fell from 8.69 to 4.83; PDFium sits at 45.04. The `pct>16` column does not move
   because the two engines disagree on tone, not on hue.

**The Peach numbers in this report are context-dependent.** Peach's output for a given page changes
with what was rendered earlier in the same process: `ZapfDingbats` takes at least three different
values, and `bug900822.pdf` followed by `ZapfDingbats.pdf` reproduces it in a two-file run. Two full
runs of the *same* binary are byte-identical, and Skia is byte-identical in every context, so under
the single-threaded sweep this is a deterministic per-process cache effect, Peach-only. The only process-global mutable state on
that path is `PeachFont.SharedFontSet`, which holds typefaces for the process lifetime; font
*selection* is identical in both contexts, so the divergence is inside PeachDrawing.Text's caches.
The same effect makes the unit suite order-dependent, and under xUnit's parallel runner not even
stable run to run: three consecutive runs of one binary gave 0, 0 and 1 failures, and the failing
test differed (`OddLengthDashArray_StillStrokesTheLine`, then
`SoftMaskTests.ExtGStateMaskOnATransparencyGroup_SurvivesTheFormClearingIt`). Both fail with these
fixes stashed too. Treat a Peach-only delta of a few percent as noise until it reproduces in a
single-file run.

## Round 3 - two more fixes, and another file where PDFium is wrong

1. **`smask_alpha_oob_transfer` (97.22% -> 0.00%, both back ends)**: an `/S /Alpha` soft mask takes the
   group's alpha, and the group's content is clipped to its `/BBox`; outside it - and wherever inside it
   nothing was painted - the group is transparent, so the alpha is 0 and the `/TR` applies to that 0.
   Poso forced those pixels to zero and skipped the transfer function, so a mask smaller than its element
   erased the element outside the box instead of lifting it. PDFium and pdf.js both lift ("Alpha SMasks
   ignore /BC for the alpha output" in pdf.js's source; the out-of-bounds alpha is 0 and goes through the
   transfer map). The other seventeen soft-mask/knockout files are byte-identical.
2. **`jpx_smaskindata`: `/SMaskInData 2` samples have to be un-premultiplied first.** That value says the
   colour samples are already multiplied by the alpha, with `/Matte` naming the colour they were
   composited against: `C = (C_stored - M) / alpha + M`, which is what pdf.js applies in
   `core/image.js #undoPreblend`. Poso read them as plain colour and multiplied again, and ignored the
   matte. All eight sample points across the four 2x1 images now read what pdf.js reads.

   PDFium is the wrong side on this file - it paints the stored premultiplied samples opaquely and
   ignores the mask channel for both 1 and 2 - so the diff against it rises from 46.07% to 54.38%. The
   file is `type: eq` in the pdf.js manifest, so pdf.js is the reference here.

## Round 4 - shading patterns inside annotation appearances

**`issue7821` (Skia 38.70% -> 10.73%, Peach 37.22% -> 9.21%)**: an appearance stream is a form, so its
default space is the appearance's own (the one its /BBox is in), and a shading pattern's /Matrix is
measured from that space (8.7.3.1). Poso set the CTM but left the stream entry matrix at the page's, so
a PatternType 2 inside an appearance was placed by the page transform instead of the BBox-to-Rect
placement. `issue7821`'s stamp has a 152-point axis and is placed at `Rect [448 2.7 611 52]`, so the
whole visible area sat past the end of the axis and the stamp came out flat. The other 34 annotation
files are byte-identical, and the entry matrix is restored on return.

### Where the corpus stands now (five fixes committed)

- Skia vs PDFium: 520 identical, 246 <5%, 161 <20%, 15 <50%, 3 >=50%
- With the 27 verified "PDFium is wrong" files excluded: **no Skia file is >=50%** (11 in <50%)
- Largest remaining real diffs: `issue16316` (38.16%), `issue16038` (33.63%), `issue7229` (28.63%)
- Largest Peach-only item: `issue12841_reduced` (82.88%, a DCTDecode failure that blanks the page;
  diagnosed, not fixed)

## Round 9 - `bug1308536`: SkiaSharp refuses this CFF program (decision: leave it)

Skia reads 22.21% here, Peach 7.25%, and Skia's ink is 19.57% against PDFium's 10.66% - it draws far more
than anyone else. The page shows whole lines smeared into black blocks; Peach and PDFium are fine.

The embedded program is a 4.6KB bare CFF (70 glyphs). SkiaSharp fails to load it, so Poso falls back to
Arial and keeps the PDF's /Widths - which is correct, text placement follows the PDF. But those widths
describe an ultra-condensed face (123, 121, 50 ...) and Arial's glyphs are much wider, so they overlap.

Per-table bisection settles where the fault is: swapping each of stonesans' eight tables into the failing
container, **only the CFF table makes Skia accept it**. The program itself is legal - both DICTs parse
cleanly and in bounds (`/Private [77, 4568]`, `defaultWidthX 251`, `nominalWidthX 223`), 70 glyphs, no
empty charstring, a format 1 charset, and hmtx/maxp agree with the glyph count. Replacing the Private DICT
with a minimal one changes nothing, so that is not it either. Peach, on the FreeType path, loads the same
bytes without complaint, as does PDFium.

**Conclusion: a compatibility difference between SkiaSharp's CFF parser and FreeType/PDFium. Not something
to work around in Poso.** The exact trigger was not tracked down (the remaining candidates are one
charstring operator and a charset SID past the String INDEX), and it would not change the decision.

One approach was tried and rejected: spacing a substituted face by its own advances. It took this file
from 22.21% to 12.68% but dropped ink to 2.42% against PDFium's 10.66%, because characters the substitute
cannot map get a MissingWidth and pile up at the origin. The glyphs are wrong either way, so keeping the
PDF's placement is the better-supported choice.

## Round 10 - `function_based_shading_cmyk` (22.75%): a CMYK conversion model difference, not a bug

The page holds three function-based shadings (Type 1 with a `/Size [2 2]` sampled function, DeviceCMYK).
Both engines agree on the hues - white to cyan to magenta to yellow - with Poso slightly brighter:

| corner | PDFium | Poso |
| --- | --- | --- |
| (u0,v0) white | (236,234,244) | (239,240,247) |
| (u1,v0) cyan | (0,176,225) | (1,188,231) |
| (u0,v1) magenta | (233,64,142) | (248,73,154) |
| (u1,v1) yellow | (197,182,47) | (208,184,64) |

Poso's `PosoColor.FromCmyk` carries **pdf.js's `DeviceCmykCS` polynomial coefficients** verbatim (the
constants from `src/core/colorspace.js:707` onward); working one sample out by hand gives (35,195,225) for
(0.774,0.014,0.106,0) against a measured (37,191,220). PDFium converts through ICC/SWOP tables instead.

So this is not PDFium being wrong - its model is closer to real print, while pdf.js's polynomial is an
approximation that avoids shipping a profile. Poso agreeing with pdf.js is deliberate (the comment on
`FromCmyk` says it is the same conversion element colours use), and pdf.js is this corpus's reference. Doing
better means carrying a CMYK ICC profile and interpolating through it, which is a different order of work.
Not doing it.

One false lead worth recording: early samples landed on antialiased edges and made the yellow corner read as
blue-purple, which looked like PDFium transposing the `Size [2 2]` axes. Sampling 6% inside the corners puts
all four in agreement and rules that out.

## Round 11 - `issue7229` page 0, the remaining 12.5%: the file is damaged, leaving it

With the page tree fixed both pages render, but page 0 still differs. The cause is that this image's data is
not where its dictionary says it is:

- Page 0 references `obj 1`, whose dictionary says `/Length 806614`, but what follows `stream` is 3454 bytes
  of **XMP** (`<?xpacket ...`) and then `endstream`.
- The actual JPEG sits at **offset 176**, right behind the `1 0 obj` header, and is exactly 806614 bytes -
  so `/Length` is right and the *data* is misplaced.
- `807826 + 806614 = 1614440` runs past the 1153154-byte file, so Poso's length check rejects it (correctly),
  falls back to searching for `endstream`, and gets the XMP - which cannot be decoded.
- Page 1 references `obj 9`, an intact copy of the same picture (341094 bytes), and renders fine.

Which one PDFium paints was settled by rendering each alone against its page 0: the 806614-byte span
differs by 12.50%, the 341094-byte one by 28.63%. **PDFium paints the former** - it recovered the misplaced
data by scanning for the SOI marker.

**So this is a damaged file and a more aggressive recovery heuristic, the same shape as `bug1308536`. Leave
it.** Poso checking `/Length`, failing, and falling back to `endstream` is the specified behaviour; guessing
"skip the leading junk and look for SOI" would be a heuristic built for one broken file.

## Round 12 - `issue7901` renders its text now (21.86% -> 11.58%)

The page says "The Free Software Definition"; Poso painted "üäÍ†Ë çéÎŸ Ì{×í æ…".

The font is a `CIDFontType0` whose embedded program is a **name-keyed CFF** (an OTTO container around a
231-glyph CFF). A CID font numbers its codes by the encoding, the CFF numbers its glyphs by the charset,
and the two orders are unrelated - code 175 is "T" while glyph 17 is the "T". For a program with no CID
space of its own Poso fell back to "code is the glyph id", which is the wrong table. PeachDrawing agreed,
so the fault sat in the shared pipeline.

Two halves. `PdfFont` now pulls the CFF table out of a real OTTO container - the existing extractor only
handles containers without a head table, so a finished one went through unparsed and never supplied its
glyph names. `Type0PdfFont` then resolves the code through ToUnicode to a character, the Adobe Glyph List
to a name, and the CFF charset to a glyph id. A space comes straight from the name "space": this file maps
code 32 to U+00FE, which painted a thorn at every word break.

21.72% -> 13.05% on Skia, 21.86% -> 11.58% on PeachDrawing, the text legible; `issue215.pdf` goes 7.50% ->
6.48%. No corpus regressions.

**No unit test ships with this**, stated plainly: a fixture built from the same font renders correctly
without the fix as well, so it would have asserted nothing. It is verified on the corpus, and the shape of
the failure is written down here.

**Left over**: one stray glyph remains at the word breaks, from a gid/CFF order mismatch inside the OTTO
container. Separate from this fix, not addressed.

## Round 13 - `issue19634` (20.95%): Type3 with nested soft masks, diagnosed but not fixed

Five lines of "guest", each drawn by **two stacked Type3 fonts**. F4 builds its glyphs out of a bitmap
mask - `32 0 0 32 -128 -1312 cm`, `/G23 gs`, `0 0 42 61 re f`, where /G23 is
`/SMask << /S /Luminosity /G form >>` and the form references a 42x61 grayscale JPEG. F5 draws the real
outline (`d1`, a run of `c` curves, `h f`).

PDFium shows green: F5's outline, painted second, on top. Poso shows a pale red block: it painted **only
F4** - whose mask has mean 65, dark, so the red comes out washed - and **F5's outline never appeared**.

Ruled out: the mask JPEG decodes fine on all three decoders; F5's CharProc is complete (ends in `h f`);
F5 has no /Resources but needs none, and the `font.Resources ?? _currentResources` fallback holds; and the
`d1` stencil suppression only covers colour operators inside the charproc, not the outer `0 1 0 rg`.

**Why it is not fixed**: this file stacks Type3, `d1` stencil, a nested Luminosity soft mask (a form that
references another ExtGState), and a resource-less font. Building a minimal reproduction costs more than the
20% of one file is worth. The diagnosis is recorded so the next occurrence in real documents can be picked
up directly.

## Round 14 - `issue2177`: PeachDrawing's sheared tiling pattern (19.76% -> 7.97%, fixed)

Skia read 8.93% here, **PeachDrawing 19.76%**, and the pattern inside its three circles came out as
scattered specks.

The tiling pattern's `/Matrix` is `[1.4 1 -.5 .7 0 0]` - a **shear**. The interpreter handles that
specially, but only for a back end declaring `SupportsTransformedImagePlacement`; without it every cell was
flattened to an axis-aligned bounding rectangle, the shear was dropped, and the cells landed as specks.
Skia declares the flag. PeachDrawing never did - and `PeachContext.DrawImage` has always blitted through
the current CTM (`Blit(..., _ctm)`), so the capability was there and simply undeclared.

Declaring it: 19.76% -> **7.97%**, the best of the three engines, and `tiling_patterns_variations.pdf`
follows from 5.37% to 3.34%. No corpus regressions.

## Round 15 - the colour of an uncoloured tiling pattern (`issue16038`, `tiling_patterns_variations`)

`issue16038` is a field of hatching: `[/Pattern /DeviceRGB] cs` selects "pgfprgb", then
`0 0 1 /pgfpat21 scn` names a pattern whose cell is one stroked line (`0.3985 w ... l S`). A PaintType 2
pattern carries no colour of its own - it comes from the operands scn put ahead of the pattern name, in
the base space of the Pattern array. Poso read neither: the base of `[/Pattern <base>]` was dropped and
`PaintType` was never consulted, so the cell stroked in the default black and the page came out grey.

The pattern space keeps its base kind now, `SelectPattern` takes the colour operands left after the
pattern name is popped, and `RenderCell` seeds the cell's fill and stroke with the result.

The lines are blue. `issue16038` itself only reaches 33.16% because what remains is line weight - at 2x
a 0.4pt rule inside a 2.99pt cell is under a pixel, and the two engines antialias it differently. The
corpus's dedicated pattern file shows the gain: `tiling_patterns_variations.pdf` 3.34% -> **0.21%** on
both back ends, no regressions.

## Round 16 - `copy_paste_ligatures` (23.54%): small-text antialiasing, not a bug

In the thumbnail Poso's ligatures (ff, fi, fl, ffi, ffl) look like separate letters; magnified, the glyphs
are identical and both engines draw real ligatures. Column statistics put the difference evenly across the
line (17-30%) with no displacement.

The same convergence test the earlier font cases used: rendered on its own, this file reads 17.01% at 2x
and **3.72% at 8x** - ordinary antialiasing of 12pt text at low resolution, no semantic error to fix.

## Round 17 - `text_clip_cff_cid` (Peach 19.04%, Skia 0.22%): Peach's CID glyph spacing, located but not fixed

Skia reads **0.22%** on this file and Peach 19.04% - the largest Peach-falls-behind-Skia gap in the corpus.
The triage list sorts on the worse of the two, so it shows 19.04% and reads as a shared defect; it is not.

**Ruled out**: the text clip (`7 Tr`) is not the cause. Rendering the same CID-keyed CFF on its own, with no
clipping at all, Peach still crushes "ABC123" into a blob (ink 90655 against Skia's 34416). The CID-to-GID
map is also right - a probe shows `CID 68 -> gid 24 ("A")` with width 0.707, matching `/W`.

**Located**: Peach's glyph **spacing** is too small; the glyph shapes themselves are correct. The suspect is
the boundary between `glyph.X` (em units) and `EmToPoint` in `PeachGraphicsContext`, which computes
`originX + x * scale` - `x * scale` folds design units into em, so an `originX` already in em cannot share
that arithmetic. Not pursued further because pinning it down needs finer instrumentation around
`UnitsPerEm`, and this pass had run out of room.

**Also**: `ClippingOnlyText_ClipsTheFillAfterTheTextObject` now runs on both factories instead of Skia only
(`3c9e105`); both pass, which confirms Peach's text clipping is fine in the simple case and the fault here
is the font.

## Round 18 - the three "Skia falls behind" files: none is a Poso defect

Working the list from the previous round, starting with the files where Peach does better. All three need
no code change:

- **`bug1743245` (reported 11.48%, 4.33% rendered alone)**: the page's background is a grid of thousands of
  **0.5pt stroked rules**; the outer CTM scales by 0.317, so each is **0.16 device pixels** wide. Skia
  antialiases everything, and a 0.16-coverage line necessarily washes out to about 217 - arithmetically
  right - while PDFium and Peach clamp sub-pixel strokes to a minimum width and land at 133/142. Both
  policies are defensible; Skia's is more correct and looks far worse. Converges to **0.91%** at 8x.
- **`issue12399_reduced` (Skia 9.88%, Peach 3.23%)**: Skia reports `FontFallback` - the embedded bare CFF
  (59 glyphs) fails to load, falls back to system Times New Roman while still spacing by the PDF's
  `/Widths`, and the line smears into gibberish. Peach loads it and draws "Consolidated financial
  statements" exactly right. Wrapped with `CffOpenType.Build` and handed to `SKTypeface.FromData` it
  returns null, while two other CFFs (13415 and 34995 bytes) load through the same path - **SkiaSharp's
  CFF parser refusing a particular program, the same shape as `bug1308536`**. Not working around Skia.
- **`issue4061` (Skia 8.62%, Peach 2.71%)**: CJK text, identical on all three engines, differing only in
  stroke weight from antialiasing. Converges to **1.38%** at 8x.

**The report ordering changed too**: the triage list now carries `skia_pct`, `peach_pct` and `worse`.
Sorting on the worse of the two put `text_clip_cff_cid` (Skia 0.22%, Peach 19.04%) near the top as if it
were a shared defect when only PeachDrawing is wrong - which is what sent this pass after the wrong lead
first.

## Round 19 - `text_clip_cff_cid` closed out: the fault is inside PeachText

> **Corrected in Round 27**: "inside PeachText" was only half right. A CID-keyed font dict declares its
> scale as CharMatrix (operator 12 7), not FontMatrix (7); Poso's readers looked only for 7 and fell to
> the 1/1000 default - Peach's 2x came through the wrapper's wrong unitsPerEm, and Poso's own outline
> reader produced the same 2x. Fixed in Round 27; Peach 19.04% -> 0.88%.

The previous round placed it at "Peach's glyph spacing is too small". This round pins the number: for the
same glyph id 24 the two back ends produce outline bounds of **Skia `(0.019, 0, 0.645, 0.707)`** against
**Peach `(0.039, 0, 1.322, 1.448)`** - Peach is exactly **twice** the size. Each glyph is two ems wide, so
they overlap, which reads as tight spacing.

Ruled out one at a time:

| check | result |
| --- | --- |
| CID-to-GID map | correct (CID 68 -> gid 24 "A", width 0.707 matching `/W`) |
| `CidCffProgram` FontMatrix | `[0.001,0,0,0,0.001,0]`, upem 1000, correct |
| the container's `head.unitsPerEm` | 1000, correct |
| the container's placeholder `hmtx` advance | set to `upem` instead of `upem/2`, Peach still 19.04% - **not it** |
| `head`'s FontBBox (this font's is `[-300,-410,2045,1800]`, wider than its em) | scaled onto the upem, Peach still 19.04% - **not it** |

**Conclusion: PeachText expands the outlines of this CID-keyed CFF at roughly twice their scale.** PeachDrawing
is a third-party NuGet package with no source in reach, and there is no clean fix on Poso's side - sniffing
for "outlines that look too big" and correcting them is patching somebody else's library and would risk other
fonts. Same disposition as the SkiaSharp CFF cases (`bug1308536`, `issue12399_reduced`): **recorded, not
worked around.**

**It also corrects last round's wording**: not "spacing too small", but "glyphs at double size overlapping".

## Round 20 - sampling the 147 consistent-diff files

Fourteen drawn at random from the 147 files where both back ends are >=5% off, classified by content:
about ten are fonts (six Type1, four Type0, four of those the same `tracemonkey` family), the rest shading,
XObjects and optional content.

The convergence test sorts the real differences from the rendering noise:

| file | 2x | 8x | reading |
| --- | --- | --- | --- |
| `tracemonkey` | 7.81% | **1.64%** | converges - antialiasing, not a defect |
| `issue6721_reduced` | 7.57% | 5.79% | partly converges - mixed, not chased |
| `issue15716` | 14.72% | **13.04%** | **does not converge** - structural |

`issue15716` is a grid of card suits: same shapes on both sides, but **Skia's glyphs are larger and more
solidly filled**. That is the substitute face (`Segoe UI Symbol`) against PDFium's built-in mapping - the
glyph outlines are simply different, the same disposition as `bug1308536`. Worth noting this file came down
from 32.75% to 15.46% when the dingbat mapping was fixed; the remaining 13% is the shapes themselves.

**Conclusion**: the sample is dominated by antialiasing and substitute faces; no common semantic defect
hiding in that batch that one fix would clear.

## Appendix - the Peach flake in the unit suite (pre-existing, unrelated to any of these rounds)

A full `dotnet test` run often reports one failure, and **which test it is changes from run to run**
(`OddLengthDashArray_StillStrokesTheLine`, `ExtGStateMaskOnATransparencyGroup_SurvivesTheFormClearingIt`,
`ShadingPatternStroke_OnAnOffsetMaskedLayer_LandsWhereItWasClipped` have all been seen). Run alone, every one
of them passes. Three consecutive full runs: fail, pass, fail.

Same origin as the Peach cross-document state noted in Round 5 (`PeachFont.SharedFontSet`'s process-lifetime
cache plus xUnit's parallel runner), and it predates every change in these rounds - verified by stashing the
source edits and watching it happen anyway. How to read it: if the failing case is a Peach-parameterised one
and it passes on its own, it is the flake, not a regression.

## Round 21 - five of the nine "PDFium opens it, Poso does not" files now render

A different angle this round: ignore the pixel diffs and look at files PDFium opens while Poso throws or
refuses - those nine are what a user actually hits, as a blank page.

| file | cause | now |
| --- | --- | --- |
| `issue1985` | /Indexed lookup shorter than one entry, so `maxIndex` is -1 and `Math.Clamp` throws | 2.69% |
| `operator_list_cycle` | xref offset past the end of the file, `PdfLexer`'s `_pos` unclamped -> IndexOutOfRange | **0.00%** |
| `GHOSTSCRIPT-698804-1-fuzzed` | subsection says `0 2` and lists seven, catalog never indexed | **0.00%** |
| `issue18986` | same | **0.00%** |
| `PDFBOX-3148-2-fuzzed` | same | **0.00%** |

The first two are plain crashes, not toleration questions: one unchecked precondition of `Math.Clamp`, one
lexer constructor taking its start on trust. The other three share a cause - **the table produces a trailer
but not a catalog** - and the loader's existing rebuild fallback stops at "registers nothing" and "no /Root",
one step short.

`PdfDocument` now checks, after the table is assigned, that the catalog is reachable and that the /Pages it
names resolves, and repairs the table otherwise. **The position matters**: doing it inside `PdfXref.Load`
failed, because resolving an object goes through the xref being built, so every healthy document looked
broken - the two existing tests asserting `WasRebuilt` stays false caught that first attempt.

**Still failing (15)**: `issue9418` (its /Pages does not parse even after a rebuild), `issue11549_reduced`
(`Dictionary keys must be names`), `PDFBOX-4352-0` (`Unexpected keyword 'E'`), `issue19517` (a 12608x16806
page, 850M pixels at 2x - not a defect), and eleven that PDFium rejects too.

**No unit test ships with this**, stated plainly: a fixture built from `CreateSimplePdf` with an
under-declared subsection still resolves without the fix, so it would assert nothing. Verified on the corpus.

## Round 22 - unreadable files down from 20 to 12

Three more:

| file | cause | now |
| --- | --- | --- |
| `issue11549_reduced` | a keyword sitting where a dictionary key belongs; ParseDictionary threw | 4.74% / 3.74% |
| `issue9418` | an incremental update rewrites /Root at the /Info dictionary - a dictionary, so it resolves, but it has no /Pages | 1.65% / 0.37% |
| `PDFBOX-3148-2-fuzzed`, `GHOSTSCRIPT-698804`, `issue18986` | (previous round) catalog unreachable | 0.00% |

**The `issue9418` one is worth remembering**: the scan already finds the real catalog
(`catalogObjectNumber`), but only replaced /Root when /Root was *absent*. This file's /Root exists and
names a dictionary - the /Info one - so it resolves and then has no /Pages. "a /Root that is not a catalog"
now counts as absent, in both the rebuild path and the document-level check that triggers it.

**Corpus now**: 965 of 977 render on Skia (957 at the start of this pass), 12 do not; PDFium opens 954.

**The 12 left**: `issue19517` (a 12608x16806 page - 850M pixels at 2x, the allocation fails; a test-harness
limit rather than a defect), `PDFBOX-4352-0` (encrypted, and object 6's body is mangled to `E< /CF ...`),
and ten that PDFium refuses as well.

## Round 23 - a font the back end rejects paints from its own embedded outlines

**14 files** in the corpus report `FontFallback`: the back end could not load the embedded glyph program
and drew the text in a system substitute. These were previously parked as "record, do not fix" (rounds 9,
18, 20) because a substitute's letterforms are inherently not the PDF's.

This round builds the fallback. Poso parses the embedded program itself - a Type 2 charstring interpreter
for CFF (name-keyed and CID-keyed, local and global subroutines, the flex family, the seac composite form,
hintmask stepping) and a `glyf` reader for TrueType (short/long loca, quadratics elevated to cubics,
composite glyphs) - and where the back end reports a substitute, paints each glyph's outline with an
ordinary `FillPath` at the pen position the `/Widths` give. The gate is the back end's `UsedFallback` flag:
a font it loads never reaches this code.

Across all 977 files, **exactly six renders change**; the other 971 are byte-identical.

| file | before | after | note |
| --- | --- | --- | --- |
| `bug1308536` | 23.58% | **14.13%** | Skia rejected its Type1C subset; the outlines come back |
| `issue8480` (Peach) | 10.11% | **8.00%** | |
| `issue12399_reduced` | 11.41% | **10.49%** | |
| `bug1539074.1` / `issue7544` / `issue11768_reduced` | 0.59 / 0.12 / 1.93 | 0.60 / 0.12 / 2.02 | antialiasing-level noise |

The 0.09% on `issue11768_reduced` is not a Poso regression: PDFium draws a **.notdef box** for that
JIS-UTF8 CID font (the oracle itself fails to render it), so Poso drawing the real glyph reads as "less
like" it.

Digging in turned up a real bug, committed on its own: `Type1ToCff` opened a negative CFF bcd real with
the `0xC` nibble (the "E-" exponent marker) instead of `0xE` (the minus sign), so a reader parsed `-0.004`
as 0. pdf.js `bug1539074.1.pdf`'s second font declares FontMatrix `[0.001 0.002 0.003 -0.004 0 0]`, and
that zero flattened every y coordinate onto the baseline, shearing the glyph into a page-wide black wedge.

**The round 9 "do not fix" verdict is reversed**: `bug1308536` drops from the 22% band to 14%, and the
remaining gap is the embedded subset's letterforms versus PDFium's, no longer a back-end capability limit.

## Round 24 - two upstream parsing fixes found through the fallback

Chasing the remaining fallback files turned up two more basic parser bugs - fixing them lets the back end
load the programs directly, so the outline fallback is not even needed:

1. **A CFF program's INDEX tables start at its declared header size, not at 4** (`79ae542`). All three CFF
   readers walked the tables from a hardcoded offset of 4, but the header's third byte is its size and a
   CID-keyed subset can write more: pdf.js `issue13147.pdf` declares 12. Its program parsed as garbage,
   the CID->GID map never built, and the CJK text drew in a substitute. Now **16.09% -> 9.75%** on both
   back ends, with "準会場で受験の方" drawn correctly.
2. **A .pfa program's eexec body is ASCIIHex text** (`20ecea6`). A Type 1 program in .pfa form encodes the
   eexec ciphertext as hex digit pairs up to a `>`; feeding those characters straight to the decryptor
   yields garbage. The reader now recognises the body by sampling its leading run (real ciphertext begins
   with four random IV bytes, which land on non-hex characters about 94% of the time) and hex-decodes
   first. `issue11740_reduced` (Russian "Оглавление") goes **10.95% -> 8.25%**, and `issue4630` (a Verdana
   .pfa) **2.99% -> 1.82%** as a bonus.

**Full A/B**: exactly six renders change across the 977 (3 files x 2 back ends), **all better, zero
regressions**; 331 tests green.

**Where the 14 fallback files stand**: `issue13147`, `issue11740_reduced` and `issue4630` no longer report
a fallback at all. The rest of the remaining gap is genuine letterform difference between the embedded
subset and whatever the reference used. `issue11740_reduced` still sits at 8.25%: the glyphs are right but
the spacing is too wide ("О лаг венв" against "Оглавление"), which lives in how a Type0/CIDFontType0 CID
numbering aligns with the converted CFF's charset - recorded, not fixed.

## Round 26 - embedded fonts no longer live in the shared FontSet forever

`PeachFont.SharedFontSet` is a process-wide static, and PeachDrawing's `FontSet` has **no removal API**:
whatever `AddData` registers - the program's bytes, its family record, its descriptor - lives as long as
the set does. Every embedded program of every document used to be registered into that one set, which is
an unbounded per-document leak in any long-running viewer or server.

Each embedded program now loads through a **private `FontSet` per font**, which is dropped with the
`PeachFont`. Three probes carry the design: a fresh `FontSet` builds in <1ms and still sees the
process-wide system families (`TryFindFamily("Arial")` works, because that is a read of the system
database); `AddData` touches only instance state, never the static installed list, and is invisible to
other instances; and a matched `Typeface` is **self-contained** - it keeps answering metrics and glyph
requests after the set that created it is gone. The shared set now only serves system-family lookups and
never grows. Skia needs no counterpart: `SKTypeface.FromData` registers nothing, and its typeface is
disposed with the font. The regression test `PeachFontLifetimeTests` reads PeachDrawing's internals
directly and pins "loading and disposing twenty embedded fonts leaves the shared set's custom-family
count at zero"; both tests fail when the fix is stashed. All 1929 corpus renders are byte-identical -
only the lifetime changed.

**One old attribution to correct**: Round 5 blamed the "Peach numbers depend on context" scramble on
`SharedFontSet` accumulation. This round re-ran the documented repro (`ZapfDingbats.pdf` after
`bug900822.pdf`) across processes: the hashes are **identical before and after the fix** (alone `2A4A...`,
after `7668...`). The scramble is therefore **not** in the set Poso registers into but inside
PeachDrawing.Text's own static name-keyed caches (`FontFactory.FontSourcesByName/ByKey`, the
`TypefaceCache` singleton), which Poso cannot clear. This fix addresses memory retention, not the
scramble - the latter keeps the Round 5 reading rule (a Peach-parameterised case that passes alone is
jitter, not a regression).

**Jitter statistics**: before the fix roughly two thirds of suite runs showed one failure (a different
test each time); after it, 19 of 20 runs were clean. A large drop, not a disappearance - consistent with
the attribution: the locked-write/unlocked-read race on the shared set is gone, but the system-font
resolution path still writes PeachDrawing.Text's static caches, which Poso cannot reach.

## Round 27 - text_clip_cff_cid's real culprit: a CID font dict's CharMatrix (12 7)

Round 26 kept the file at "PeachText's problem, Poso cannot fix". This round falsified that directly:
**Poso's own outline reader produced the same 2x** (probe: gid 24 is Skia 0.645x0.707, Peach
1.322x1.448, and Poso's CffOutlines 1.327). Hand-decoding the charstring gives coordinates of about
(66,34)-(1265,1349) design units, and 1349/2048 = 0.66 em matches Skia - **1/2048 is the right scale**.

Root cause: a CID-keyed font dict declares its scale as CharMatrix (12 7), not the name-keyed
FontMatrix (7). Both CidCffProgram and CffOutlines looked only for 7 and fell to the 1/1000 default, so
the wrapper's unitsPerEm was wrong (Peach's 2x) and the outline reader's matrix was wrong (Poso's 2x).
The fix (`57120b8`) adds CffMatrix.Declared, resolved FD 12 7 -> FD 7 -> top 12 7 -> top 7, shared by
the CID reader and the outline reader.

**One trap to guard**: an identity matrix here is a placeholder, not a statement. issue11713.pdf's FD
says [1 0 0 1 0 0] while its top DICT carries the real 1/1000 - taking the identity at face value sets
unitsPerEm to 1 and blows every glyph to a thousand em wide (the first version of this fix pushed
issue11713 from 0.12% to 45.18% exactly that way). CffMatrix skips identities and keeps searching,
converging on FreeType's "plausible matrix" guard.

**Result**: text_clip_cff_cid Peach **19.04% -> 0.88%** (Skia was always 0.22% - it reads 12 7 correctly
when parsing CFF itself). Full A/B: against the Round 26 baseline only this one file changes - the
identity guard erased the three regressions (issue11713/issue16500/issue3061) the first version had
introduced. Three new tests pin it: the FD CharMatrix wins, identities are skipped, outlines land in em.

**The lesson in the attribution chain**: the same 2x was blamed on "PeachText internals" in Round 19,
maintained in Round 26, and only in Round 27 did it turn out both sides had the same bug. A third-party
library's error does not exclude ours - when both readings agree on the wrong number, the one renderer
that got it right (Skia) is the clue.

## Round 28 - a TrueType fallback finds its glyph only through the sfnt's cmap

When the fallback landed (Round 23), ResolveOutlineGlyph found outlines by glyph name alone. That is
fine for Type1/Type1C name-keyed programs, but a simple TrueType font has no glyph names: the PDF code
maps straight to a glyph id through the program's built-in encoding, which for a renumbered subset
only the program itself knows. Every code of a TrueType program PeachDrawing rejects (pdf.js
bug1200096.pdf's Times subset) therefore returned -1, the fallback had nothing to paint, and Peach
kept drawing the substitute face.

The fix (`39c66a4`): TrueTypeOutlines reads the sfnt's `cmap` (formats 0/2/4/6/12; format 2 is the
8/16-bit mixed table bug1200096's subset uses), and SimplePdfFont sends the code's Unicode value
through it when the name route fails. Verified on that subset: code 65 -> gid 36, the 'A' outline is
0.70 em wide and sits on the baseline.

**Nearly neutral in the numbers**: only the two Peach renders move in the full A/B - bug1200096
8.63% -> 8.62%, issue14117 5.82% -> 5.84% - because the substitute Peach had been given is a Times
family too, so the shapes were already close. What changed is that the page now draws the document's
own glyphs rather than a lookalike.

New tests: the reader is sampled directly over gid 30..44 (at least ten outlines, all distinct), and
a Peach end-to-end test pins the chain (the program forces UsedFallback -> NeedsOutlineFallback ->
code 65 yields an outline -> the page carries ink).

## Round 29 - an unreadable encryption dictionary no longer refuses the document

Rebuilding the baseline meant re-running the stale "no page" list (20 files) one by one. Eight of them
had been fixed in earlier rounds and the list had simply never been regenerated. The real head of the
list was pdf.js PDFBOX-4352-0.pdf: its /Encrypt object was fuzzed into opening with "E<" instead of
"<<", Resolve threw a PdfFormatException out of the PdfDocument constructor, and the whole document was
refused. PDFium opens it and shows one white page - the content stream really is AES ciphertext, which
nobody can decrypt, but "cannot open" and "blank page" are different answers.

The fix (`9a57ad6`): a dictionary the parser cannot read is treated as absent, the document opens as if
unencrypted and each stream renders whatever its filter recovers; the object-header scan fallback gets
the same guard. Only this file changes across the corpus - from no page to a 400x100 render that is
pixel-identical to PDFium's (all white). A test pins "opens and stays blank" instead of painting
ciphertext. poppler-742 / poppler-937 also report "Unexpected keyword" but their content is the damaged
part, and PDFium rejects them too.

**The rebuilt numbers** (`out2/` baseline, `report_new.tsv`): Skia 966 ok (11 missing), Peach 966 (11),
up from 957/955. Peach improved sharply on a batch of files through the fallback and font work:
franz_2 97.31% -> 2.83%, issue12841_reduced 82.88% -> 0.27%, endchar 32.55% -> 5.34%,
text_clip_cff_cid 19.04% -> 0.05%, issue15716 32.75% -> 15.46%, issue2177 19.76% -> 7.97%. The only
file that got worse is jpx_smaskindata 46.07% -> 54.38%: since `429cb18` Poso un-premultiplies JPX
samples as pdf.js does, while PDFium ignores /SMaskInData and paints the premultiplied samples
opaquely - a recorded oracle disagreement, not a regression. bug1802506's rise is the same story one
level over: annotation appearance streams, which PDFium does not paint (`oracle-annots`).

**The rebuild also exposed a Peach-only failure**: issue3405r rendered under Skia but threw
OverflowException under Peach. Its embedded WenQuanYi program declares 9 tables where the directory
holds 8 - the ninth "entry" is really head's first 16 bytes, tag "\0\1\0\0", offset 1.76 billion,
length 1.59 billion. RepairSubset clamped the length to `data.Length - offset`, a negative number, the
running total went negative, and `new byte[total]` overflowed. The fix (`c761fb8`) drops an entry whose
range does not fit the program instead of clamping it. Peach now renders the page at 400x100, mean 232.8
against PDFium's 232.5, and its no-page count matches Skia's 11.


## Round 30 - the blank-page sweep: 11 down to 5, plus pattern placement

The Round 29 rebuild showed that "blank page" hid below the percentage thresholds: a whole small
element missing is only a 3-10% diff, so it never reached the top of the sort. This round fixed the
causes one by one:

1. **A stray token inside a TJ array** (`bug1953099`) - one lone '-' made PdfArray parsing throw; the
   content loop swallowed it and the rest of the array became stray operands, so TJ showed nothing.
   The token is now skipped (`74e0960`). Blank -> 1.55%.
2. **Glyph 0 is a glyph** (`issue8234`) - the subset maps code 32 onto gid 0 and paints .notdef, while
   Poso skipped zero. Every recorded glyph is handed to the backend now (`2030206`). 1.23% -> 0.02%.
3. **A font named by the content but absent from /Resources** (`issue5954`) - PDFium draws a stock
   Helvetica; Poso dropped the run. Poso now substitutes the same (`2030206`). Blank -> 2.4-2.9%.
4. **Peach's cmap shadowed by an empty table** (`issue2017r`, `bug1027533`, `issue4800`,
   `bug1151216`, `issue7020`, `visibility_expressions`) - RepairSubset's empty (3,1) won FreeType's
   lookup over the real (3,0)/(1,0)/(0,1) tables, so every code was gid 0. SfntCmap now merges by
   preference, skips zeros and normalises the symbol range, and PeachFont prefers the program's own
   map (`76bbd67`). Six pages back to 2.6-4.6%.
5. **Non-embedded CJK fonts** (`issue8372`, `issue13343`) - predefined CMap byte layout and Unicode,
   plus a system CJK family per ordering (`3b39791`). The substitute's shapes and advances differ, so
   the percentage grows (2.03% -> 3.62%, 3.99% -> 6.84%), but the pages now carry the right text
   (目录 / (57)【要約】) instead of nothing.
6. **Tiling pattern placement** (`issue13561_reduced`) - the lattice is anchored at the /BBox, the
   pattern to the content stream's entry transform, and mirrored matrices take the transformed path
   (`70df055`). Blank -> 0.88% (ink 0.77% against PDFium's 0.76%).
7. **A corrupt cmap offset** (`bug1050040`) - a subtable offset that wraps negative passed the old
   bounds test and read past the array, killing the Peach render. Out-of-range records are skipped
   (`1ff794b`).

**Final numbers**: both back ends 967/977 (10 missing), with identical missing sets; "Poso blank,
PDFium has ink" is down to 5 each. Two known gaps were not fixed: `rc_annotation` (a Text note icon
PDFium synthesises) and `bug1865341` (a FreeText annotation with no /AP, whose appearance has to be
built from /DA), plus the three `bitmap-symbol-symhuffrefine*` files (JBIG2 Huffman symbol
dictionaries with symbol refinement, which the decoder still returns null for).


## Round 31 - the ink screen: five more content paths

With the blank pages gone, files whose inked area is under 60% of PDFium's (27 of them, uncaveated)
were triaged one by one. Five had a real cause:

1. **Codec-encoded image masks** (`issue4379`) - only DCT/JPX masks went through the image pipeline, so
   a CCITT stencil was read as raw samples and the 1000x800 indexed image became a band of noise.
   CCITT and JBIG2 masks now decode through the image pipeline, with the coverage read from a white
   stencil so premultiplication keeps its alpha (`94350f8`). 0.63% -> 0.00%, pixel-identical.
2. **A form inherits its caller's resources** (`issue1055r`) - a form with no /Resources inherits the
   resources in force where it is invoked (ISO 32000-1 Table 95). 9.09% -> 2.34%.
3. **A Type3 procedure keeps the caller's text matrix** (`pr4922`) - the procedure's own Tm replaced it,
   so every glyph after the first was positioned from the glyph's origin. 6.38% -> 3.63% (Peach 2.51%).
4. **A symbolic TrueType maps its codes through its own cmap** (`issue8229`) - the subset's (1,0)
   format-6 table is keyed by the PDF code while /ToUnicode calls code 1 'R'. After the backend and the
   character lookup fail, the code itself is tried. 10.57% -> 4.31%.
5. **`g<glyph id>` names** (`issue13316_reduced`) - YouYuan names its glyphs /g5167; the Adobe Glyph
   List does not know them and there is no post table, while PDFium reads the number as the glyph index.
   Skia 7.93% -> 4.23%, Peach 7.94% -> 1.48%. `canvas-464d32` also improved through fix 2 (5.95% ->
   4.32%).

Full A/B (`out6`): **zero regressions**, five improvements. Left open:

- `issue14462_reduced` - its embedded Type1 program is truncated (fontTools refuses it too) and its
  glyph-name table ends in binary noise; each viewer guesses differently.
- `issue14999_reduced` - its Type3 glyphs are inline CCITT stencils that Poso paints as thin strips
  (the negative-y Type3 FontMatrix combined with the inline image placement); not fully diagnosed.
- `issue15441` - a non-embedded Identity CID font with no ToUnicode, so the CIDs are the original
  font's glyph ids; PDFium and Skia happen to draw them, Peach's substitute has a different glyph
  table. There is no correct answer to match.


## Round 32 - Huffman symbol refinement and aggregation in JBIG2

The three bitmap-symbol-symhuffrefine files were the last "Poso blank, PDFium 7.1% ink" images.
The symbol dictionary segment returned null as soon as SDHUFF=1 and SDREFAGG=1. Both constructions
now follow PDFium's JBig2_SddProc (`fd7436b`):

- A single refinement names its reference symbol with SBSYMCODELEN raw bits, then RDX/RDY through
  Table B.15 and the arithmetic bitmap's byte size through B.1; the bitmap is decoded with the
  refinement context and the Huffman stream advances past it.
- An aggregate is a text region over the imported symbols plus the ones just decoded. Unlike a text
  region segment, it carries no symbol-code table: PDFium numbers the symbols with fixed-width
  identity ids (ceil(log2(SBNUMSYMS)) bits), and the instance data uses B.11/B.6/B.8 with per-instance
  refinement through B.15/B.1. The placement core was extracted from the Huffman text region decoder
  so both share it.

symhuffrefineone is pixel-identical to PDFium (7.24% -> 0.00%); symhuffrefineseveral goes to 0.29%
(421 of 159,600 pixels: one 23x23 glyph box of the aggregate still lands differently, recorded as a
near-match in the vector test); symhuffrefine-textrefine still degrades to null, since its aggregate
interleaves refinements in a layout this decoder does not follow yet. The other 74 JBIG2 vectors are
unchanged.

**A correction to Round 31** (`276a9c2`): the g<glyph id> convention also caught
bug1027533.pdf's /g0024 and /g003a, which are zero-padded character codes rather than glyph ids.
Names with a leading zero now take the normal character/Unicode route, putting the file back at
Skia 3.14% / Peach 2.74% while issue13316_reduced keeps its fix.

**Final numbers**: both back ends 967/977, 953 comparable files; 82.4% within 5%, 98.7% within 20%;
three blank pages remain per backend (symhuffrefine-textrefine, rc_annotation, bug1865341).


## Round 33 - annotation appearances (Ink and FreeText) and a Peach cmap flag

Two blank pages in the list were annotations: bug1865341.pdf (FreeText with no /AP) and
annotation-ink-without-appearance.pdf (Ink with no /AP). Neither subtype requires an appearance
stream, and both were dropped when a file omitted one.

- **Ink** keeps its geometry in /InkList (one array of alternating x/y per stroke); the strokes are
  drawn in /C with the /BS width and round joins (`09bb667`). 0.41% -> 0.25%.
- **FreeText** names font, size and colour in /DA and carries the string in /Contents (UTF-16 when
  its BOM says so). The appearance is synthesised: the text clipped to /Rect plus the box border
  PDFium draws, dropping characters the default font cannot encode - PDFium renders "Załącznik" as
  "Zacznik" for that reason. bug1865341 1.41% -> 1.12% (Peach 1.08%), freetext_no_appearance 0.23%
  -> 0.15%.

**A Peach gap found on the side** (`8fb3144`): IFont.MapsCodePointsToGlyphs defaulted to true on the
PeachDrawing font, so a TrueType program with no cmap was kept instead of substituted, and
TrueType_without_cmap.pdf was blank on Peach (Skia paints 0.41%). The check now inspects the
original program's table directory (RepairSubset adds a synthetic cmap, so the repaired bytes cannot
answer it). The page paints 0.46% on Peach and issue14297's Peach diff drops 4.89% -> 3.97%.

**Not done**: rc_annotation's Text note icon (PDFium-drawn annotation chrome, not document content)
and symhuffrefine-textrefine. After both fixes two blank pages remain.

**Two open items Round 35 turned up**:

- issue11279.pdf's page number "1" is not drawn: the font's /CharSet holds only /one /A /B, so the digit
  is not in it. PDFium draws it with a substitute; Poso cannot - that is the "what to use when a font
  lacks the glyph" question, and it is a feature gap of its own.
- issue14999_reduced.pdf's Type3 glyphs are in the right place now but each letter still misses its
  lower half. Its charproc scales the content by 0.01 into a 10600-unit cell, so the glyphs sit at
  1:10000 of the image - a pathological case that PDFium and MuPDF agree on and Poso does not.


## Round 34 - a degenerate contour paints its edge

issue4260_reduced.pdf is a table whose every line is a zero-height rectangle ("848 1085 10159 0 re"). A
strict fill of a contour that encloses no area leaves it blank; PDFium paints its edge, and Poso drew
only the page's outer box.

The fix (`416edc8`):

- A degenerate contour is drawn as a one-device-pixel line, aligned to the pixel it falls in. It is not
  a stroked path: a stroke thinner than a pixel is anti-aliased into a pale line rather than the solid
  black PDFium paints.
- Only a contour made entirely of straight segments counts: a curve whose points happen to be collinear
  is still a curve, and PDFium does not draw it as a line.
- A path that mixes a degenerate contour with one that encloses area is left unfilled - the winding of
  the degenerate contour cancels the other one.
- A contour with nothing but a move-to draws no shape, so it is neither a shape nor a degenerate line;
  counting it as one cancelled the real contour next to it (`4a641d5`; pdf.js issue11279.pdf's form ends
  with "h m f", and its two SoftLight circles went missing with it).

issue4260_reduced 4.04% -> 1.83%, with no regression anywhere in the corpus.

**An independent defect it uncovered**: issue11279.pdf's two SoftLight circles (66,324 pixels in
PDFium) are not drawn at all here (48 pixels). That is the form's transparency group and SoftLight
blending, unrelated to this round, and the next target.


## Round 35 - a Type3 glyph procedure runs in the font's own space

The canvas of a Type3 glyph procedure is FontMatrix * text space * CTM (ISO 32000-1 9.5.5), but only
the canvas followed that transform: the interpreter's graphics state kept the page's matrix, so the
procedure's own operators - a cm above all - were measured against the wrong basis.

issue19634.pdf's F4 glyph (a d1 template, a rectangle filled in the text colour) was therefore drawn
as a mis-placed block trailing every line, instead of the soft grey shadow PDFium paints. With the
graphics state following the canvas, **19.20% -> 6.74%**: the ink drops from 4453 pixels to 3669
against PDFium's 3841, and the shadow takes its shape.

**This overturns the Round 27 arbitration**: the remaining 19.2% was then judged "PDFium is the
outlier" (on the strength of MuPDF standing with Poso on 92% of the differing pixels). The arbitration
was sound, but it hid the real cause - Poso's letters were wrong to begin with.

The same change puts issue14999_reduced.pdf's glyphs in the right place (its text used to be
squeezed into a thin strip), but each letter still misses its lower half; that is not diagnosed yet.


## Round 36 - a CFF without an encoding is read through the standard one; MMR and the PDF K values part company

**A CFF with no built-in encoding** (`c5aa12c`): such a program has no code table of its own, so the code
has to be read as a standard-encoding code (the code names a standard string, the charset says which
glyph carries that name). The reader looked only at the program's own table, which is empty in that case,
so the code had no glyph at all.

pdf.js issue11279.pdf's symbolic CMR10 subset holds "one", "A" and "B" and has no Encoding operator, so
the page number - the standard code for "one" - was left undrawn. The file now matches PDFium (0.01%).
The same change brings issue6721_reduced 7.82% -> 2.65%, bug860632 2.94% -> 1.20% and issue3566
3.85% -> 3.00%.

**MMR and the PDF K values part company**: a JBIG2 collective bitmap is Group 4 (MMR), and it used to
borrow the PDF's K = -1, which means "Group 3 1-D without EOL codes". The two meanings were mixed, so
changing one broke the other. MMR now has its own entry point (DecodeMmr).

**Two attempts that were abandoned** (recorded so they are not repeated):

- An inline image's /DP<<...>> was not parsed (ReadInlineValue handles arrays only), so an inline
  CCITT's /K -1 and /Columns were both lost. Parsing them helps issue14999 (17.81% -> 10.07%) but
  hurts issue4706 (5.18% -> 10.39%), and the inline CCITT is not where issue4706's difference comes
  from - **reverted**, only the MMR split stayed. (Round 40: the regression came from the rewind, not
  the parse; the same change is now in, and both files improve.)
- The font descriptor's /CharSet lists the glyph names, which would translate the PDF's private names
  ("Psi", "Gamma") into the program's ("y", "T"). TeX fonts are named that way (issue6132,
  issue14462_reduced and others), but the CFF's charset and the CharStrings order do not agree, so
  routing it properly needs the full Type 1C semantics - **deferred**.

**The remaining ink deficit** (19 files) falls into three kinds: TeX private-encoding fonts (issue6132,
issue14462_reduced, issue4934, issue4573, issue8586 and others, which need the full Type 1C semantics),
Type 3 glyph spaces with a pathological layout (issue14999_reduced), and annotation appearances
(rc_annotation, bug1538111, annotation-highlight-without-appearance).


## Round 37 - a glyph is filled from its outline, not from the glyph atlas

Skia can draw a run of text from its glyph atlas - the pre-rasterised masks it keeps per size - or by
filling the glyph outlines. We used the atlas (the comment claimed it was faster). The atlas stores a
coverage mask per size, so its anti-aliasing inks the letterforms a little lighter and a little
narrower than the analytic path fill: pdf.js endchar.pdf's "LE HOLD-UP PLAN" came out a pixel
narrower, with the strokes a pixel off.

Filling from the outlines (`2e4d895`) moves **139 corpus files towards PDFium and one away**. The
Skia share of files within 1% of PDFium goes from 532 to 549, and within 5% from 254 to 263. It is no
slower either: the whole corpus renders in 3m40s with the atlas and 3m42s without it, so the atlas
bought nothing at these sizes.

The font's hinting is off as well (`c8c5eb1`): a PDF viewer must not snap glyphs to the pixel grid
(ISO 32000-1 9.7.3.3), and the outline this font hands out was the shape the rasteriser would have
hinted - hinting applied to a one-unit shape nobody ever sees at that size.

**One attempt abandoned**: widening a stroke that is thinner than a device pixel to one pixel.
bug1743245.pdf's grid lines go from grey to black and the difference drops 11.48% -> 2.78%, but
vertical.pdf's 0.1pt rules are **grey** in PDFium (47% coverage) and the test sets its band for the
two ways of drawing text. PDFium paints a hairline at its actual coverage rather than always at one
pixel - **reverted**.

## Round 38 - a CFF container carries the character map of its own program

The OpenType container Poso builds around a bare CFF program had an **empty cmap**, so a back end
that opens the font had no way to map a character to a glyph and fell back to numbering the glyphs
itself - a numbering that does not always match the program's. pdf.js issue6132.pdf's cmbx12 came
out as evenly spaced rubbish because every letter was drawn with the wrong outline.

The container now carries a (3,1) format 4 cmap built from the program's charset through the Adobe
Glyph List, and the core routes glyph-name addressed fonts through the back end so the id it gets
back always addresses the right outline (`1d9dc20`). Six corpus files move closer to PDFium and
none moves away: issue6901 6.93% -> 4.32%, issue7439 3.85% -> 1.82%, issue14462_reduced 8.02% ->
7.02%, issue11477_reduced 4.23% -> 3.48%, bug859204 3.20% -> 2.90%, issue6132 7.23% -> 7.20%.

## Round 39 - a Huffman symbol dictionary entry with no instance keeps its slot

The last "Poso blank, PDFium has ink" image file. The three `bitmap-symbol-symhuffrefine*` vectors
share one segment structure (page info, a plain Huffman dictionary, a SDREFAGG dictionary, a text
region); only `textrefine` adds SBREFINE on the text region. Round 32 fixed refinement and
aggregation for `one` and `several`; `textrefine` still degraded to null and painted nothing.

Ported against the **current** PDFium (pypdfium2 5.14 ships the post-2023 layout; the local 2019
PDFium checkout is stale on exactly the points below). Four differences, all real:

- **REFAGGNINST == 0**: PDFium (`jbig2_sdd_proc.cpp`) sets `BS = nullptr` *before* the dispatch, so
  a zero-instance entry is stored as a symbol with no bitmap, keeps its slot, is exported, and is
  skipped at placement. Poso returned null for the whole dictionary, which aborted the page - the
  direct cause of the blank page (textrefine's dictionary has two such entries). A null symbol now
  flows through as `Jbig2Symbol.Empty`.
- **The aggregate's corner**: a multi-instance aggregate is a nested text region, and PDFium forces
  `REFCORNER = JBIG2_CORNER_TOPLEFT`. Poso passed 0 - but PDFium/jbig2dec number BOTTOMLEFT as 0,
  so the aggregate's glyphs were shifted up by their full height and fell off the canvas. The
  decoder now names the corners with the enum it matches.
- **The aggregate's symbol-id width**: PDFium numbers aggregate instances with
  `max(1, CeilLog2(SDNUMINSYMS + SDNUMNEWSYMS))` bits - the width of the dictionary's *declared*
  symbol count, not the number currently available. For textrefine that is 4 bits where Poso read
  3, so every following value (RDW/RDH/RDX/RDY/BMSIZE) decoded from the wrong bit; the trace showed
  a BMSIZE of 5145 for a 157-byte segment. `several` only escaped this by coincidence (6 symbols,
  both formulas give 3 bits).
- **Null references**: where PDFium fails the dictionary (single refinement of a null symbol) or the
  region (text region instance refining a null symbol), Poso now fails the same way; the arithmetic
  text region matches PDFium's stricter contract (an out-of-range id or a null instance aborts the
  region), while the Huffman path skips a null instance without counting it - PDFium's `continue`
  still reads the strip's next coordinate delta, and the port keeps that flow.

textrefine now matches PDFium **pixel-identically (7.24% -> 0.00%)**, and several converges too
(0.29% -> 0.00%): PDFium renders all three vectors identically, and so does Poso now. A same-
method full-corpus sweep shows no regression; files where Poso paints nothing while PDFium has
content are down to **1** per backend (rc_annotation, annotation chrome). Test: the corpus file
itself, added to the existing Huffman-dictionary theory (`jbig2-huffman-textrefine.pdf`).

## Round 40 - an inline image dictionary keeps its dictionaries and its keywords

Two facts about inline image keys (ISO 32000-1 8.9.7 Table 89): the value after a key can be a
**keyword** (`/IM true`, `/I false`), and it can be a **dictionary** (`/DP<</K -1 /Columns 106>>`).
`ReadInlineValue` accepted neither: keyword values fell through to null and `<<` was not a recognised
value start. So pdf.js issue14999_reduced.pdf - a Type 3 font whose glyph descriptions are inline
1-bit CCITT image masks - lost both its mask flag and its parameters. The masks decoded with the
default Group 3 2-D model (the codestream is 1-D), and without `/IM true` they painted as opaque
grayscale fields rather than stencils in the text colour. The page read as faint noise under the
real words.

Fixing only the dictionary, the file got **worse** (10.07% -> 17.81%): the correctly decoded mask is a
filled field with the letterform as a hole - exactly what paints when the mask flag is missing. Both
halves were needed, and the Round 36 "abandoned" note about issue4706 is explained: the first attempt
rewound with `Position - 1`, which is right for `[` but lands on the **second `<` of `<<`**, so
issue4706's dictionaries parsed as garbage. Rewinding to the token's own start fixes it for both -
issue4706 now improves along with the rest.

Files moved: issue14999_reduced 10.07 -> 6.50 (its letters are complete and sit where PDFium's do;
the residual is stroke weight), issue4706 5.26 -> 2.91, bug1245391_reduced 5.18 -> 3.97,
images_1bit_grayscale 9.40 -> 8.79, issue6605 0.85 -> 0.72, bug1795263 0.47 -> 0.39, plus two smaller
shifts. Zero regressions across the corpus. Test: the corpus file as `inline-imagemask-ccitt.pdf`,
which fails on either half-revert (noise 1.2%, opaque field ~13%, fixed 8.3%).

## Round 41 - a font subset with an unordered loca, and a CID font with no character map

**84 corpus files embed a TrueType program with no cmap** (the scan is in `tools/corpus`). A CID font
with such a program addresses its glyphs by id, so the missing character map is legal - but a back
end that has to guess a character-to-glyph mapping guesses wrong. Skia's `GetGlyphPath` returns an
**empty path for most of such a font's glyph ids**, so the letters simply do not appear. The no-cmap
files' median difference is 3.8x the rest of the corpus (1.17% against 0.31%), and 26 of the 83
comparable ones differ by more than 3%.

pdf.js issue11131_reduced.pdf is the clearest case: an Identity-H CIDFontType2 whose 71-glyph
program has no cmap, of which **eight letters went missing** (the CIDs 4, 8, 9, 13, 23, 24, 28, 48 -
exactly the ids Skia cannot reach). PDFium draws all 23 letters of that line.

**Two separate faults, both fixed.**

*The loca table may be unordered.* This subset's `loca` is not monotonic (`loca[4] = 10674`,
`loca[5] = 2188`), and its 69 distinct boundary values tile the `glyf` table - but the intervals
run backwards for 37 of the 71 glyphs. A reader that takes the interval's length for the glyph's
size then computes a negative length and finds nothing there. **The length is only an upper bound:
the record carries its own size in its header.** FreeType says so explicitly (ttpload.c):
`if (pos2 >= pos1) *asize = pos2 - pos1; else *asize = glyf_len - pos1;` - the glyph is kept and
its length widened to the end of the table. Poso now does the same, and the eight letters come
back. An *empty* glyph (`loca[g] == loca[g+1]`) is a different case and must stay empty: its
offset points at whatever follows it, and reading there paints the next glyph.

*The back end cannot reach the glyphs it cannot map.* A CID font's codes are glyph ids, so the
glyphs are not looked up by character at all - but Skia's `GetGlyphPath` goes through its own
character-to-glyph path, and for a face it could not map it returns nothing for most ids. So a CID
font whose program has no cmap is painted from the program's own outlines, **per glyph**: the ones
the back end can reach stay with it (it draws them better), and only the ones it cannot reach come
from the program. Routing the whole font through the program instead was measurably worse - it cost
2 percentage points on issue4402_reduced, whose letters the back end draws well.

Files moved: issue11131_reduced 5.32% -> 3.85% and bug1650302_reduced 3.59% -> 3.10%, with no
regression anywhere in the corpus. The residual on the first is stroke weight. Test: the corpus
file as `cidfont-nocmap-loca.pdf`, which fails without the loca fix (1.9% of the page inked against
4.6% with it).

**The letterforms were still polygons.** With the glyphs found at last, they came out with straight
stems and square shoulders, and a TrueType contour is not made of straight lines: an off-curve point
is the *control point* of the quadratic curve that ends at the next on-curve point. The reader
emitted that control point as both cubic control points, which collapses the curve onto a straight
line. Each quadratic is now elevated to the cubic that traces the same curve - its control points
two thirds of the way from the ends towards the quadratic's control point, the same conversion the
Skia back end already applies to the outlines Skia hands it.

The walk also had to count the points a curve consumes. Two off-curve points in a row imply an
on-curve point halfway between them, and the second of them is the control point of the curve that
*leaves* that implied point, so the pair is walked as two curves and consumes both points; the old
count used the second one twice.

The letterforms are right now - the letter's shoulders taper, the strokes bow. The page's number
against PDFium moves by 0.15 percentage points (3.85% -> 4.00%), which is the sub-pixel
rasterisation landing differently than the polygons did rather than a shape that is still wrong: the
glyphs' positions, sizes and ink totals all match, and PDFium's own rendering of that line is a
curve.

**What the diagnosis cost.** The first reading of this font - the unordered `loca`, the record
lengths that never matched the intervals, the chain that broke after two records, fontTools
refusing the file - all said "the font is corrupt". It is not: FreeType reads all 71 glyphs from
it, and so does Acrobat. The evidence that settled it was not more analysis of the bytes but
asking a real TrueType implementation, which is the lesson worth keeping - a reference
implementation answers in minutes what reverse-engineering did not answer in an hour.

**An audit of the other outline paths** (the four rounds of fixes all touched `TrueTypeOutlines`,
so the CFF and Type 1 routes were checked the same way). Poso has three outline readers:
`TrueTypeOutlines` (quadratics elevated to cubics), `CffOutlines` and the Type 1 path, which
converts to CFF and then goes through `CffOutlines`. Both of the latter work in *native cubic*
Béziers - Type 2 charstrings carry `curveto`/`flex` as cubics - so neither has the class of bug
that turned TrueType curves into straight lines. That was confirmed by reading the code, not by
another render.

The audit also cross-checked our reader against FreeType across **586 embedded programs and
32,517 glyphs**, comparing the bounding box each produces in em space (`_scratch/corpus_cmp/
crosscheck_outlines.py`, using a small FreeType dumper). **95.2% agree exactly.** The rest fall
into three buckets, none of which is a drawing fault:

* **729 glyphs differ by at most 0.005 em.** The quadratic-to-cubic elevation is exact as a
  curve but moves the control points, and our bounds are computed from control points - so the
  numbers move slightly while the shape does not.
* **513 glyphs differ by a pure translation of the same-sized box.** FreeType folds `hmtx`'s
  left side bearing into its metrics via phantom points (`ttgload.c`: `pp1 = xmin - lsb`);
  we report the glyph record's own extent. Confirmed not to affect the render: the ink centroid
  of a page built from such glyphs lands within a pixel of PDFium's.
* **327 glyphs differ otherwise**, mostly a contour of a *single* point that we drop
  (`count < 2`) and FreeType keeps. A one-point contour has no area, so it draws nothing; it
  only shows up in the bounds. An annotation font's `.notdef` is the clearest case - its fifth
  contour is one point at the descender, and that is the whole of the difference.

So the TrueType curve fix was the only real defect of its kind, and the remaining numbers are
measurement conventions rather than shapes that are wrong.

## Round 42 - a TeX font's built-in encoding, and the curves its charstrings draw

pdf.js issue6132.pdf is a TeX paper whose title is set in cmbx12, a Type1C program with a private
encoding: the PDF's /Encoding names only six glyphs (Gamma, Psi, Omega, fl, dieresis and one more),
so every other code has to reach the program's **built-in CFF encoding table**. Two codes failed
there - 14 (the ffi ligature in "Efficient") and 12 (fi in "Application") - and the letters E, F,
t, a, d, e, o, p and s were missing or deformed on top of that. Three separate faults, all in the
CFF route.

*The built-in encoding table was mis-parsed.* CFF 2.24 format 0 lists one **code** per glyph, in
glyph order; format 1 lists runs of (first code, nLeft) whose codes and glyph indexes both run
consecutive across runs - there are no glyph bytes in the table at all. The old reader consumed one
byte per format-0 run (the run is one byte, but the loop also had to advance the glyph counter
per run, which it did off-by-one) and read format 1 as (code, glyph) byte pairs, which is a layout
that does not exist. A supplement (format byte & 0x80) names its glyph by **SID**, which reaches a
glyph index only through the charset - the old reader had no charset to consult. The fix parses
both formats per spec, inverts the charset for the supplement, and follows FreeType's
`cff_encoding_load` (which is what PDFium uses through its bundled FreeType) on the corner cases:
glyph 0 is never coded, a code at or beyond the glyph count is dropped, and offset 0/1 (the
predefined Standard/Expert encodings) are left to the caller's fallback.

*Skia's own CFF parser returns an empty path for some of the program's charstrings.* With the
encoding fixed, the ligatures appeared but the page still inked 3.2% against PDFium's 7.8%. The
probe showed Skia's `GetGlyphPath` failing for exactly the missing letters (glyph ids 50, 68, 70,
71, 98, 101, 102, 112, 113, 116, 117) while FreeType - and PeachDrawing - read every one of them
from the same wrapped container. The cmap-less CID route from Round 41 already had the right
shape: ask the back end per glyph, and take from the program's own reader the ones the back end
cannot reach. That per-glyph probe now runs for simple fonts with a CFF program too (the back end's
own id is the one probed, since that is the one it draws with).

*Our charstring interpreter put the hvcurveto/vhcurveto deltas on the wrong axis.* The fallback
drew the letters, but E had a wedge cut out of its middle arm and C was deformed. `hvcurveto`'s
groups are (dx1, dx2, dy2, dy3) - the curve leaves horizontally, so the *third* operand belongs to
the axis it does not leave on, and a fifth operand on the last group gives that axis its remaining
coordinate (dx4, not dy4). The old reader assigned args[3] to dx3 and args[4] to dy3, swapping the
two axes' shares of every alternating curve. FreeType's `cffdecode.c` is explicit about it. After
the fix, **all 130 glyphs of cmbx12 match FreeType's on-curve points exactly** (the only difference
is one explicit closing point per contour, which fills identically).

**A regression the A/B caught.** The per-glyph probe widened to simple fonts made Peach lose ink on
the seac composites of `glyph_accent` and `issue7101`: Peach's `TryGetGlyphOutline` reported no
outline for a seac glyph, while its text path expands seac itself when drawing. The probe and the
drawer must answer the same question - can this face paint this glyph - so the probe now expands
the base-plus-accent pair the same way the drawer does. Both files returned to their baseline
numbers, and `issue8480` (also seac composites) improved on Peach 6.68% -> 3.30% as a side effect.

Files moved (Skia unless noted): issue12399_reduced 8.39% -> 3.60%, bug1308536 10.45% -> 8.02%,
issue6132 7.20% -> 6.22% (ink 3.22% -> 6.74% against PDFium's 7.79%), issue3566 2.19% -> 1.60%
(Peach 1.93% -> 1.34%), issue8480 Peach 6.68% -> 3.30%. No regression anywhere in the corpus
(report27 -> report28, 977 files). Test: the title strip of issue6132 as `tex-private-encoding.pdf`
(Skia inked 5.1% of it before the fix, 10.2% after; PDFium 11.2%).

The diagnosis followed the standing rule - no guessing against the bytes. The encoding table's real
layout was settled by reading FreeType's loader and fontTools' decoder side by side, the axis swap
by replaying cmbx12's "E" operand by operand against FreeType's trace, and the Skia blindness by
probing `GetGlyphPath` glyph by glyph inside a container that FreeType loads without complaint.

## Round 43 - a Type 1 charstring entry with no ND marker does not swallow the next glyph

pdf.js issue14462_reduced.pdf draws one line - "Issue 14462" - from a native binary Type 1 (a
`FontFile`, PFB with eexec). Poso painted a single character: the whole title vanished except its
last digit, and both back ends agreed, so the fault was in the program reader, not in either
rasteriser.

The PFB's CharStrings dictionary writes each entry as `/name length RD <binary> ND`, but this
generator leaves the `ND` out for many entries, so the byte right after the charstring data is the
`/` of the next entry's name. The reader handles that case - it steps the stream back over the
token it misread - but the step-back was one byte short: it moved the cursor without consuming
the byte it landed on, leaving the tokenizer's lookahead one byte ahead of the cursor. The next
`GetToken` then read the `/` delimiter as a glyph name, every real glyph was followed by a fake
entry (`/` or binary junk, zero-length charstring), and the fake entries' empty charstrings pushed
the real ones out of the code-to-glyph table. An eexec decryption of the program and a dump of the
decoded CharStrings showed the corruption directly: `zero`, `three`, `F`, `r`, ... interleaved with
`/` entries and garbage names, where the encoding should have been `zero one two three ...`
monotonic.

The fix makes the step-back consume its byte, matching pdf.js's `prevChar` (`stream.skip(-2)` then
`getChar()` - its comment names this very file): the cursor ends one byte before where the misread
token started, and the tokenizer's next read sees the `/` again. All 84 glyph names now decode in
program order, the CFF conversion carries them through, and the line renders whole.

Files moved: issue14462_reduced 7.02% -> 2.44% (Peach 7.04% -> 2.11%; ink 0.62% -> 5.97%/6.10%
against PDFium's 6.64%). No regression anywhere in the corpus (report28 -> report29, 977 files).
Test: the corpus file as `issue14462-reduced.pdf`, which inks 0.6% of the page before the fix and
6.0% after.

The lesson is in the failure mode: a parser that is *almost* right about where a token ends does
not visibly fail on the font - it silently fabricates entries, and the rendering evidence (a
hundred missing letters) points at the rasteriser rather than at the byte cursor.

## Round 44 - a /Decode array no codec ever sees, a shading under its mask, and text filled with a pattern

Four faults in the painting path, each found by following the ink the reference renderer puts down
and ours does not.

*The DCT path ignored /Decode.* A JPEG's samples are remapped by the image's /Decode array -
`min + sample * (max - min)`, the same `decode_min_`/`decode_step_` PDFium computes in
CPDF_DIB::GetDecodeAndMaskArray and applies to whatever its decoder returns. No codec sees the
dictionary, and ours handed the decoded raster straight to the page, so a file drawn with
`/Decode [1 0]` (the complement form) came out in its stored tones instead of the inverted ones:
pdf.js issue7406.pdf's logo printed white-on-black where every viewer shows black-on-white. The
array is now applied to the decoded pixels on the DCT path, whatever channel count the codec
produced - the CMYK route already had its own mapping. The one wrinkle: issue7406's JPEG is
progressive (SOF2), which the managed decoder refuses, so the raster comes from the back end and
the mapping runs on its BGRA.

*The alpha reader read a JPEG as raw samples.* The same file's Luminosity soft mask stores its
coverage as a JPEG too, and DecodeAlphaChannel treated the stream's bytes as rows of pixels - so
the mask was noise and the element it masked vanished. A mask is a gray image like any other, and
a producer may store it as a JPEG, so it now goes through the decoder and takes the single
component's plane.

*A shading never saw the ExtGState's soft mask.* The `sh` operator painted the shading straight
to the page. Every other paint opens the mask's group first; the shading was the one element that
did not, so pdf.js issue18956.pdf's faded gradient came back as a solid band. `sh` now opens the
group like the rest.

*Text could not be filled with a pattern.* `scn` selects a shading or tiling pattern as the paint,
and a pattern is not a colour - it cannot be handed to the text painter as one. The glyphs of the
run are now drawn white on a surface of their own, that surface becomes a layer mask, and the
pattern paints inside the layer across the whole device, so exactly the letterforms are covered.
pdf.js issue9243.pdf draws its title through a shading pattern this way; the word came out solid
black. Both glyph routes take part: the ones the back end draws, and the ones the embedded
program's own outlines supply when it cannot.

The pattern-through-text route needs a depth guard of its own: a tiling pattern's cell can hold
text, and that text can be filled with a pattern again, and the cell is rendered by a *fresh*
interpreter whose counter starts at zero. The corpus has such a file
(ContentStreamCycleType3insideType3), and without carrying the depth into the cell interpreter the
render recursed to a stack overflow. The depth now survives into the nested interpreter, and past
four levels the pattern paint is dropped so the rest of the page stands.

*The radial shading picked the wrong root.* A Type 3 shading's parameter is the root of the
quadratic the two circles define, and which root is the visible one depends on how the cone runs.
Ours preferred the smaller root and fell back to the other when the radius went negative, which
fills the intersecting-circle configurations with a solid blob. PDFium's DrawRadialShading decides
by the cone's direction - a radius that shrinks while the circles contain each other traverses the
sweep backwards - and carries the factor 2 in the quadratic's middle term that our solver left
out. The solver now mirrors it: pdf.js radial_gradients.pdf's four circle configurations (13.81%
-> 0.49%) come out ring for ring.

Files moved: issue7406 15.93% -> 7.16% (Peach 15.48% -> 7.63%), issue18956 13.57% -> 6.14%,
issue9243 13.79% -> 1.14% (Peach 0.03%), radial_gradients 13.81% -> 0.49%. The pattern-through-text
route carried other files with it: pattern_text_embedded_font 10.10% -> 3.28%, ZapfDingbats (Peach)
9.03% -> 7.31%, ShowText-ShadingPattern 1.76% -> 1.08%, issue19360 1.05% -> 0.48%, issue8111
(Peach) 0.55% -> 0.00%. No regression anywhere in the corpus (report29 -> report30, 977 files). Tests: the four corpus files as fixtures, each
asserting the property that was broken - the complement tone, the faded band, the gradient along
the word, and the ink total of the four panels.

*An encoding CMap can carry its mapping as bfchar.* The bfchar/bfrange operators belong to a
ToUnicode CMap, but a producer that swaps the two is not rare, and readers accept it: pdf.js reads
the destination string's bytes as a CID (`convertCidString` - one byte is the value, two are
`(b0 << 8) | b1`). pdf.js bug920426.pdf's /Encoding is a CMap whose whole mapping is 100 bfchar
entries, and its text now renders "Checkliste Service". PDFium ignores bfchar there and paints
.notdef boxes, so this one is recorded in `oracle_wrong.tsv`; the corpus diff against PDFium moves
only from 16.60% to 14.10% because the reference itself is the wrong side.

**Two files that are not Poso's fault, arbitrated.** `function_based_shading_cmyk` looked like a
color bug (ours 22.75% from PDFium), but MuPDF agrees with Poso to 0.81% while differing from
PDFium by 19.29%: PDFium wraps the 2x2 sampled CMYK function at its far corner (dark blue where
the sample says yellow+black). Recorded in `oracle_wrong.tsv`. `issue16038`'s hatch lines differ
because the stroke is 0.3985 pt - 0.8 device pixels at 2x: PDFium snaps it onto one pixel row while
ours antialiases it across two, and the ink measure counts the pale second row. That is the
sub-pixel-thin-line band this project does not chase.

**A note on Peach's run-to-run noise.** A full re-render changes four Peach numbers that no code
change can explain (ZapfDingbats 7.31% -> 9.03%, issue8092 0.50% -> 0.93%, issue15443 and
issue7580 by ~0.1pp). None of those files carries an embedded CMap, and rendering ZapfDingbats
alone reproduces 7.31% with the same binary - PeachDrawing's output for some files depends on
which fonts were rendered before it in the process. A Peach "regression" of this size in a resumed
versus a single-shot run is that noise, not a code change; Skia, which shares no such state, is the
tie-breaker.

**A note on measuring ink in tests.** The radial fix looked like it had failed in the unit test
while the corpus render showed it working: the test counted a single colour channel below the
threshold, and the gradient is red and green. Counting ink needs the darkest channel, which is
what the corpus comparison's luminance does.

## Real Poso issues, worst first

1. **`issue19634` (20.95% -> 19.20%): a Type3 glyph's graphics state leaked out - fixed (`4f9d00b`);
   the residual is an oracle disagreement, not a Poso bug (MuPDF arbitration, Round 27).**
   A glyph description runs as if surrounded by q and Q (ISO 32000-1 9.5.5), but ExecuteType3Glyph only
   saved the canvas, not the interpreter's own graphics state. F4's charprocs select a Luminosity /SMask
   through gs; that mask leaked into the F5 run painted over the same line, hiding the green text under a
   white layer. With the fix the green returns (line-for-line with the reference) and the file drops to
   19.20%. Of the residual diff pixels, 41% are F4's shadow hue (Poso faint red, PDFium neutral grey) -
   and on those pixels **MuPDF is red too, 92% of them**, siding with Poso. A `d1` stencil is painted in
   the text colour (red) per the spec, so Poso is right and PDFium is the outlier. The rest is heavier
   green-stroke weight and AA at the glyph edges (MuPDF itself differs from PDFium by 19.75% on this
   page). Reclassified as oracle disagreement + antialiasing; no longer a Poso to-do.

2. **`pr12564` (Skia 10.03% / Peach 16.48%): PeachDrawing scrambles layout under extreme scaling.**
   The page strokes 1pt hairlines with `12700 w` under `0.0000787402 cm`; Peach scatters the text into
   unreadable garbage while Skia renders it correctly. The scramble is **deterministic** (two renders in one
   process are byte-identical), so it is not the `SharedFontSet` cross-file pollution, and A/B shows it
   predates the font-fallback work. The fault lies inside the third-party PeachDrawing (no source access);
   Poso cannot fix it, only record it.

3. **`smask_alpha_oob_transfer` — fixed** (97.22% → 0.00% on both back ends). Removed from the top of the list.

**The round 25 triage verdict**: after the outline fallback landed, every remaining 10%+ "real diff"
candidate was checked, and apart from the two above none is a Poso semantic bug:

**Round 27's MuPDF arbitration**: the five remaining unmarked 20%+ files (`issue16316`, `issue16038`,
`issue14165`, `copy_paste_ligatures`, `function_based_shading_cmyk`) were rendered through MuPDF for a
three-way comparison. None shows the "both references agree, Poso is the outlier" pattern - all three
renderers disagree pairwise (issue16316: mu-vs-poso 33% is even smaller than mu-vs-pdfium 47%;
issue14165 differs 93%+ in every pair, which is three different font substitutions). Everything falls
into the antialiasing/hinting/substituted-fonts band. **No clean Poso bug remains.** The font-fidelity
line closes here.

- `22060_A1_01_Plans` (17.36%): the hatches are one large JPEG tiled as a pattern (`/R13`, 2480x2630);
  hairlines come out faint at 2x but converge to PDFium's level at 8x (ink 0.167 vs 0.144). Geometry is
  right; this is downsample quality.
- `issue12337` (15.38%): a dense text page, ink 6.09% vs 5.27%, the two renders are visually identical -
  PDFium's text is simply a touch heavier.
- `issue2884_reduced` (17.13%): one line of CJK (Identity-H CIDFontType2); all three renderers draw the
  same glyphs, only stroke weight differs.
- `issue3458` (17.25%): ink 14.17% vs 14.14% - a uniform colour/edge difference, nothing missing.

In other words, **the remaining large diffs are no longer "Poso paints less" but "Poso paints it with a
slightly different stroke weight or colour"** - the font-fidelity line is essentially converged.

## Other large-but-plausible diffs

`jpx_smaskindata` (54.38%, the oracle disagreement from Round 29), `issue16038` (38.30%, its hatch
sits on a different phase than PDFium's; 15.6% ink against 16.8%), `issue16316` (38.16%),
`knockout_inner_backdrop` (34.50%), `calgray` (72.76%, PDFium writes g*255 and skips the transfer
function; pdf.js's reference formula is what the file expects), `issue19176` (62.57%, PDFium ignores
/UserUnit, so Poso is the correct side). Full ranked list: `out/report5_triage.txt`.

## Poso files that produce no page (10 Skia / 10 Peach)

The two back ends now share the same missing set. Of the 10, **9 are also rejected by PDFium**; the
one exception is `issue19517`:

- `issue19517` - `Unable to allocate pixels for the bitmap`: MediaBox 12608x16806, so scale 2 asks for
  25216x33612 (about 3.4 GB). SkiaSharp's allocation fails while PDFium's happens to succeed. This is
  the caller's canvas size exceeding available memory, not a rendering-semantics bug; a lower scale
  renders the page.
- `issue19484_1`, `issue19484_2`, `issue3371`, `pr6531_1`, `print_protection` - encrypted files PDFium
  reports as incorrect password or load failures (/Pages unreachable).
- `poppler-85140` - PDFium itself raises MemoryError.
- `poppler-937`, `REDHAT-1531897`, `poppler-395-0-fuzzed` - PDFium refuses them as page or data
  format errors.

## Reproducing

The pipeline lives in `tools/corpus` (`harness.py`, `cmp_pdfium.py`, the `sweep` project). Requires
Python with `pypdfium2` + Pillow, and a PDF corpus (`POSO_CORPUS` env var, default
`D:\_git0\pdfjs\test\pdfs`).

```bash
cd tools/corpus
dotnet build sweep -c Release   # the Poso renderer driver
python harness.py manifest      # 977 rows
python harness.py pdfium        # render the oracle (resume-safe)
python harness.py sweep         # render Poso/Skia + Poso/Peach, with crash bisection
python cmp_pdfium.py            # report_pdfium.tsv + summary_pdfium.txt + triage_pdfium.txt
```

To measure a change without overwriting the baseline, render into another directory and point the
comparison at it:

```bash
sweep/bin/Release/net10.0/sweep.exe manifest.tsv <other-dir> 2.0
POSO_DIR=<other-dir> REPORT_PREFIX=report_try python cmp_pdfium.py
```
