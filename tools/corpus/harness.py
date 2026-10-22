"""Corpus render harness: pdf.js/test/pdfs vs PDFium / Poso.Skia / Poso.PeachDrawing.

PDFium is the only oracle — MuPDF was dropped because its JBIG2/CCITT fallbacks and colour
handling are worse than Poso's, which makes a Poso-vs-MuPDF diff unreadable. Compare with
cmp_pdfium.py, which reads the PNGs written here.

Subcommands:
  manifest           Build manifest.tsv (one row per PDF, page 0).
  pdfium             Render page 0 via pypdfium2 into out/<tag>-pd.png. Resume-safe.
  sweep              Drive tools/corpus/sweep with crash recovery. Resume-safe.
  mupdf              Legacy, kept only to reproduce old numbers. Not an oracle.

Method: scale=2.0 (=144 dpi), first page of every PDF, all engines render the
SAME page; diff is per-pixel RGB magnitude. Errors are captured per engine.

Paths:
  POSO_CORPUS   directory of PDFs to sweep (default: D:\\_git0\\pdfjs\\test\\pdfs)
  Everything else (manifest, out/) lives next to this file.

Requires pypdfium2 and Pillow; PyMuPDF only for the legacy `mupdf` command.
"""
import argparse, csv, hashlib, os, pathlib, subprocess, sys, time
import pypdfium2 as pdfium
from PIL import Image, ImageChops, ImageStat

ROOT = pathlib.Path(__file__).parent
CORPUS = pathlib.Path(os.environ.get('POSO_CORPUS') or r'D:\_git0\pdfjs\test\pdfs')
OUT = ROOT / 'out'
MANIFEST = ROOT / 'manifest.tsv'
SWEEP_EXE = ROOT / 'sweep' / 'bin' / 'Release' / 'net10.0' / 'sweep.exe'
SCALE = 2.0
PAGE_IDX = 0
DIFF_THRESHOLD = 16  # per-pixel RGB-average diff considered "visibly different"


def safe_tag(pdf: pathlib.Path) -> str:
    stem = pdf.stem.replace(' ', '_')[:60]
    h = hashlib.sha1(str(pdf).lower().encode('utf-8')).hexdigest()[:6]
    return f'{stem}-{h}'


def cmd_manifest():
    OUT.mkdir(exist_ok=True)
    pdfs = sorted(CORPUS.glob('*.pdf'))
    with MANIFEST.open('w', encoding='utf-8') as f:
        for p in pdfs:
            f.write(f'{p}\t{PAGE_IDX}\t{safe_tag(p)}\n')
    print(f'wrote {len(pdfs)} rows -> {MANIFEST}')


def read_manifest(path: pathlib.Path = None):
    path = path or MANIFEST
    rows = []
    for line in path.read_text(encoding='utf-8').splitlines():
        parts = line.split('\t')
        rows.append((parts[0], int(parts[1]), parts[2]))
    return rows


def cmd_mupdf(limit: int = 0):
    # Legacy: MuPDF is not an oracle (its JBIG2/CCITT fallbacks mis-render whole corpus families).
    # Kept only to reproduce the old report numbers; the import is lazy so the maintained path
    # needs no PyMuPDF install.
    import pymupdf as fitz
    fitz.TOOLS.mupdf_display_errors(False)
    OUT.mkdir(exist_ok=True)
    status_path = OUT / 'status_mupdf.tsv'
    done = set()
    if status_path.exists():
        with status_path.open() as f:
            for row in csv.reader(f, delimiter='\t'):
                if row: done.add(row[0])
    rows = read_manifest()
    if limit: rows = rows[:limit]
    with status_path.open('a', encoding='utf-8', newline='') as f:
        w = csv.writer(f, delimiter='\t')
        for i, (pdfPath, pageIdx, tag) in enumerate(rows):
            if tag in done: continue
            png = OUT / f'{tag}-mu.png'
            t0 = time.perf_counter()
            try:
                d = fitz.open(pdfPath)
                try:
                    if pageIdx >= d.page_count:
                        w.writerow([tag, 'SKIP', 'page_out_of_range', d.page_count, '', '', 0])
                        continue
                    pm = d[pageIdx].get_pixmap(matrix=fitz.Matrix(SCALE, SCALE))  # annots=True is default; Poso renders annotations too
                    pm.save(str(png))
                    ms = int((time.perf_counter() - t0) * 1000)
                    w.writerow([tag, 'OK', '', d.page_count, pm.width, pm.height, ms])
                finally:
                    d.close()
            except Exception as e:
                ms = int((time.perf_counter() - t0) * 1000)
                w.writerow([tag, 'ERR', f'{type(e).__name__}: {str(e)[:150]}', '', '', '', ms])
            if (i + 1) % 50 == 0:
                print(f'  mupdf {i+1}/{len(rows)}'); f.flush()


def cmd_pdfium(limit: int = 0):
    """Render page 0 via PDFium (Chrome's engine) as the oracle."""
    OUT.mkdir(exist_ok=True)
    status_path = OUT / 'status_pdfium.tsv'
    done = set()
    if status_path.exists():
        with status_path.open() as f:
            for row in csv.reader(f, delimiter='\t'):
                if row: done.add(row[0])
    rows = read_manifest()
    if limit: rows = rows[:limit]
    with status_path.open('a', encoding='utf-8', newline='') as f:
        w = csv.writer(f, delimiter='\t')
        for i, (pdfPath, pageIdx, tag) in enumerate(rows):
            if tag in done: continue
            png = OUT / f'{tag}-pd.png'
            t0 = time.perf_counter()
            try:
                doc = pdfium.PdfDocument(pdfPath)
                try:
                    n = len(doc)
                    if pageIdx >= n:
                        w.writerow([tag, 'SKIP', 'page_out_of_range', n, '', '', 0]); continue
                    page = doc[pageIdx]
                    bmp = page.render(scale=SCALE)
                    pil = bmp.to_pil()
                    pil.save(str(png))
                    ms = int((time.perf_counter() - t0) * 1000)
                    w.writerow([tag, 'OK', '', n, pil.size[0], pil.size[1], ms])
                finally:
                    doc.close()
            except Exception as e:
                ms = int((time.perf_counter() - t0) * 1000)
                w.writerow([tag, 'ERR', f'{type(e).__name__}: {str(e)[:150]}', '', '', '', ms])
            if (i + 1) % 50 == 0:
                print(f'  pdfium {i+1}/{len(rows)}'); f.flush()


def cmd_sweep(limit: int = 0, batch: int = 50):
    if not SWEEP_EXE.exists():
        print(f'missing {SWEEP_EXE}; build it first: dotnet build {SWEEP_EXE.with_suffix(".csproj")} -c Release')
        return
    OUT.mkdir(exist_ok=True)
    stuck_path = OUT / 'stuck.tsv'
    stuck = set()
    if stuck_path.exists():
        stuck = set(stuck_path.read_text().split())
    rows = read_manifest()
    if limit: rows = rows[:limit]
    todo = [r for r in rows if r[2] not in stuck
            and not ((OUT / f'{r[2]}-poso.png').exists() and (OUT / f'{r[2]}-peach.png').exists())]
    print(f'sweep todo: {len(todo)} (skipped {len(stuck)} stuck, {len(rows)-len(todo)-len(stuck)} already-done)')
    if not todo: return
    diags_path = OUT / 'diags.tsv'

    def run(lines, label):
        tmp = OUT / f'batch_{label}.tsv'
        with tmp.open('w', encoding='utf-8') as f:
            for r in lines:
                f.write(f'{r[0]}\t{r[1]}\t{r[2]}\n')
        t0 = time.perf_counter()
        # Generous cap: 15s per file, min 60s, max 400s. Any hang gets bisected.
        to = max(60, min(400, len(lines) * 15))
        try:
            rc = subprocess.run([str(SWEEP_EXE), str(tmp), str(OUT), str(SCALE), str(diags_path)],
                                capture_output=True, text=True, timeout=to)
            ms = int((time.perf_counter() - t0) * 1000)
            return rc.returncode, ms
        except subprocess.TimeoutExpired:
            ms = int((time.perf_counter() - t0) * 1000)
            return -9, ms

    def process(lines, depth=0):
        if not lines: return
        rc, ms = run(lines, f'd{depth}_{len(lines)}_{hashlib.md5(lines[0][2].encode()).hexdigest()[:6]}')
        if rc == 0:
            print(f'  batch of {len(lines)} ok in {ms}ms'); return
        if len(lines) == 1:
            tag = lines[0][2]
            print(f'  STUCK on {tag} (rc={rc}, {ms}ms)')
            with stuck_path.open('a') as f: f.write(tag + '\n')
            stuck.add(tag)
            return
        mid = len(lines) // 2
        process(lines[:mid], depth + 1)
        process(lines[mid:], depth + 1)

    for i in range(0, len(todo), batch):
        process(todo[i:i+batch])


def _png_array(path: pathlib.Path):
    im = Image.open(path).convert('RGB')
    return im


def _load_status(path: pathlib.Path):
    d = {}
    if path.exists():
        with path.open() as f:
            for row in csv.reader(f, delimiter='\t'):
                if row: d[row[0]] = row
    return d


def _diff_pair(tag, a_path, b_path, size_log):
    """Return (mean, max, pct_diff, inkA, inkB, wA, hA, wB, hB, dims_ok)."""
    try:
        a = _png_array(a_path); b = _png_array(b_path)
    except Exception as e:
        return None, None, None, None, None, 0, 0, 0, 0, f'loaderr:{type(e).__name__}'
    wA, hA = a.size; wB, hB = b.size
    if (wA, hA) != (wB, hB):
        # align to min
        w = min(wA, wB); h = min(hA, hB)
        a = a.crop((0, 0, w, h)); b = b.crop((0, 0, w, h))
        dims_note = f'dim-mismatch {wA}x{hA} vs {wB}x{hB}'
    else:
        dims_note = 'ok'
    diff = ImageChops.difference(a, b)
    st = ImageStat.Stat(diff)
    mean = sum(st.mean) / 3.0
    maxv = max(int(e[1]) for e in st.extrema)
    # fraction pixels where mean-of-3-channels > threshold
    gray = diff.convert('L')
    mask = gray.point(lambda v: 255 if v > DIFF_THRESHOLD else 0)
    n_diff = sum(mask.tobytes()) // 255
    pct = 100.0 * n_diff / (a.size[0] * a.size[1])
    # ink fraction: non-white pixels (>=16 off-white in any channel)
    aL = a.convert('L'); bL = b.convert('L')
    ink_a = sum(aL.point(lambda v: 255 if v < 235 else 0).tobytes()) / 255 / (a.size[0] * a.size[1]) * 100
    ink_b = sum(bL.point(lambda v: 255 if v < 235 else 0).tobytes()) / 255 / (b.size[0] * b.size[1]) * 100
    return mean, maxv, pct, ink_a, ink_b, wA, hA, wB, hB, dims_note


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('cmd', choices=['manifest', 'mupdf', 'pdfium', 'sweep'])
    ap.add_argument('--limit', type=int, default=0)
    ap.add_argument('--batch', type=int, default=50)
    a = ap.parse_args()
    if a.cmd == 'manifest': cmd_manifest()
    elif a.cmd == 'mupdf': cmd_mupdf(a.limit)
    elif a.cmd == 'pdfium': cmd_pdfium(a.limit)
    elif a.cmd == 'sweep': cmd_sweep(a.limit, a.batch)


if __name__ == '__main__':
    main()
