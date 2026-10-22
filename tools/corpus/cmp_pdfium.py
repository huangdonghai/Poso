"""Poso vs PDFium — the corpus comparison, with PDFium as the only oracle.

MuPDF is no longer used as a reference: its JBIG2/CCITT/colour fallbacks are worse than
Poso's own decoders, so a Poso-vs-MuPDF diff mixes "Poso is wrong" with "MuPDF is wrong".

Reads the already-rendered PNGs from out/:
    <tag>-pd.png     PDFium (pypdfium2, scale=2.0)
    <tag>-poso.png   Poso / SkiaSharp
    <tag>-peach.png  Poso / PeachDrawing

Writes:
    out/report_pdfium.tsv    one row per corpus file
    out/summary_pdfium.txt   coverage + buckets + ranked outliers
    out/triage_pdfium.txt    worst-first worklist, both backends, for triage
"""
import csv
import pathlib
import re
import os
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import harness as H  # noqa: E402  (manifest, _diff_pair, _load_status, _png_array)

OUT = H.OUT
# The Poso/Peach PNGs can be read from a second render directory (POSO_DIR=...) so a change can be
# measured against the same PDFium oracle without overwriting the baseline; REPORT_PREFIX names the
# outputs. PDFium's own PNGs always come from OUT.
POSO_DIR = pathlib.Path(os.environ.get('POSO_DIR') or OUT)
REPORT_PREFIX = os.environ.get('REPORT_PREFIX', 'report_pdfium')
BUCKETS = ['identical(<1%)', 'close(<5%)', 'moderate(<20%)', 'big(<50%)', 'huge(>=50%)']

_flag_cache = {}


def pdf_flags(pdf_path):
    """Cheap raw-byte facts about a corpus file, used to keep PDFium's own blind spots out of
    the "Poso is wrong" bucket. PDFium (like Chrome) ignores /UserUnit, and FPDF_RenderPageBitmap
    does not paint annotation appearance streams, while Poso does both. Regex on raw bytes misses
    entries inside object streams; that only ever hides a caveat, never invents one."""
    if pdf_path in _flag_cache:
        return _flag_cache[pdf_path]
    try:
        raw = pathlib.Path(pdf_path).read_bytes()
    except OSError:
        raw = b''
    userunit = 1.0
    m = re.search(rb'/UserUnit\s+([0-9.]+)', raw)
    if m:
        try:
            userunit = float(m.group(1))
        except ValueError:
            pass
    flags = (userunit, bool(re.search(rb'/Annots', raw)))
    _flag_cache[pdf_path] = flags
    return flags


def manual_caveats():
    """Tags where PDFium has been shown to be wrong, from oracle_wrong.tsv. The automatic caveats
    below are detected; these are not, so they are maintained by hand with the evidence."""
    path = pathlib.Path(__file__).parent / 'oracle_wrong.tsv'
    found = {}
    if path.exists():
        for line in path.read_text(encoding='utf-8').splitlines():
            if not line.strip() or line.lstrip().startswith('#'):
                continue
            parts = line.split('\t')
            if len(parts) >= 2:
                found[parts[0].strip()] = parts[1].strip()
    return found


def bucket(pct):
    if pct < 1:
        return BUCKETS[0]
    if pct < 5:
        return BUCKETS[1]
    if pct < 20:
        return BUCKETS[2]
    if pct < 50:
        return BUCKETS[3]
    return BUCKETS[4]


def ink_fraction(path):
    try:
        im = H._png_array(path)
    except Exception:
        return None
    n = im.size[0] * im.size[1]
    dark = sum(im.convert('L').point(lambda v: 255 if v < 235 else 0).tobytes()) / 255
    return 100.0 * dark / n


def main():
    rows = H.read_manifest()
    known_wrong = manual_caveats()
    pd_status = H._load_status(OUT / 'status_pdfium.tsv')
    diags_by_tag = {}
    dpath = OUT / 'diags.tsv'
    if dpath.exists():
        for line in dpath.read_text(encoding='utf-8', errors='replace').splitlines():
            p = line.split('\t')
            if len(p) >= 3:
                diags_by_tag.setdefault(p[0], []).append((p[1], p[2], p[3] if len(p) > 3 else ''))

    stats = {
        'total': len(rows),
        'pd_ok': 0, 'pd_err': 0,
        'sk_ok': 0, 'sk_missing': 0,
        'pe_ok': 0, 'pe_missing': 0,
        'cmp_skia': 0, 'cmp_peach': 0,
        'sk_blank_pd_ink': 0, 'pe_blank_pd_ink': 0,
        'pd_blank_sk_ink': 0, 'pd_blank_pe_ink': 0,
        'pd_err_poso_ok': 0, 'both_fail': 0,
        'caveats': 0, 'verified_oracle': 0,
    }
    buckets = {'skia': dict.fromkeys(BUCKETS, 0), 'peach': dict.fromkeys(BUCKETS, 0)}
    buckets_clean = {'skia': dict.fromkeys(BUCKETS, 0), 'peach': dict.fromkeys(BUCKETS, 0)}
    outliers = {'skia': [], 'peach': []}
    outliers_clean = {'skia': [], 'peach': []}
    caveat_tags = []
    pd_errors, poso_errors, pd_err_poso_ok, both_fail = [], [], [], []

    with (OUT / f'{REPORT_PREFIX}.tsv').open('w', encoding='utf-8', newline='') as f:
        w = csv.writer(f, delimiter='\t')
        w.writerow(['tag', 'pdfium', 'skia', 'peach',
                    'sk_vs_pd_mean', 'sk_vs_pd_max', 'sk_vs_pd_pct',
                    'pe_vs_pd_mean', 'pe_vs_pd_max', 'pe_vs_pd_pct',
                    'ink_pd_pct', 'ink_sk_pct', 'ink_pe_pct',
                    'sk_dims', 'pe_dims', 'caveat', 'diags'])
        for pdfPath, pageIdx, tag in rows:
            pd_row = pd_status.get(tag, ['?'])
            pd_state = pd_row[1] if len(pd_row) > 1 else '?'
            pd_err = pd_row[2] if len(pd_row) > 2 else ''
            pd_path = OUT / f'{tag}-pd.png'
            sk_path = POSO_DIR / f'{tag}-poso.png'
            pe_path = POSO_DIR / f'{tag}-peach.png'
            sk_state = 'OK' if sk_path.exists() else 'MISSING'
            pe_state = 'OK' if pe_path.exists() else 'MISSING'

            if pd_state == 'OK':
                stats['pd_ok'] += 1
            else:
                stats['pd_err'] += 1
                pd_errors.append((tag, pd_err))
                if sk_state == 'OK' and pe_state == 'OK':
                    stats['pd_err_poso_ok'] += 1
                    pd_err_poso_ok.append(tag)
            if sk_state == 'OK':
                stats['sk_ok'] += 1
            else:
                stats['sk_missing'] += 1
                poso_errors.append(tag)
            if pe_state == 'OK':
                stats['pe_ok'] += 1
            else:
                stats['pe_missing'] += 1
            if pd_state != 'OK' and sk_state != 'OK':
                stats['both_fail'] += 1
                both_fail.append(tag)

            ink_pd = ink_fraction(pd_path) if pd_state == 'OK' else None
            ink_sk = ink_fraction(sk_path) if sk_state == 'OK' else None
            ink_pe = ink_fraction(pe_path) if pe_state == 'OK' else None

            cells = {}
            dims = {}
            for name, path, state in (('skia', sk_path, sk_state), ('peach', pe_path, pe_state)):
                cells[name] = (None, None, None, '')
                dims[name] = None
                if pd_state != 'OK' or state != 'OK':
                    continue
                mean, mx, pct, _, _, wA, hA, wB, hB, note = H._diff_pair(tag, path, pd_path, stats)
                if mean is None:
                    continue
                stats[f'cmp_{name}'] += 1
                cells[name] = (mean, mx, pct, f'{wA}x{hA} vs {wB}x{hB} ({note})')
                dims[name] = (wA, hA, wB, hB)
                buckets[name][bucket(pct)] += 1
                outliers[name].append((pct, tag, mean, mx))

            # Does PDFium's own behaviour explain the gap? Keep those out of the "Poso is wrong"
            # tally, but still report them, so nothing is silently dropped.
            caveat = ''
            if pd_state == 'OK' and sk_state == 'OK':
                d = dims['skia']
                uu, has_annots = pdf_flags(pdfPath)
                if tag in known_wrong:
                    caveat = 'oracle-verified'
                elif d and (d[0], d[1]) != (d[2], d[3]):
                    ratio = (d[0] / d[2]) if d[2] else 0.0
                    if uu != 1.0 and abs(ratio - uu) < 0.02:
                        caveat = f'oracle-userunit({uu:g})'
                    else:
                        caveat = 'SIZE-DISAGREE'
                elif cells['skia'][2] is not None and cells['skia'][2] > 2 and ink_pd is not None and ink_sk is not None:
                    if ink_pd < 0.5 and ink_sk > 2:
                        caveat = 'oracle-annots' if has_annots else 'PDFIUM-BLANK'
                    elif has_annots and ink_pd < 0.5 * ink_sk:
                        caveat = 'oracle-annots'
            if caveat == 'oracle-verified':
                caveat = known_wrong.get(tag, caveat)
            if caveat:
                stats['caveats'] += 1
                caveat_tags.append((tag, caveat))
                stats['verified_oracle'] += 1 if tag in known_wrong else 0
            else:
                for name in ('skia', 'peach'):
                    if cells[name][2] is not None:
                        buckets_clean[name][bucket(cells[name][2])] += 1
                        outliers_clean[name].append((cells[name][2], tag, cells[name][0], cells[name][1]))

            if ink_sk is not None and ink_pd is not None:
                if ink_sk < 0.5 and ink_pd > 2:
                    stats['sk_blank_pd_ink'] += 1
                if ink_pd < 0.5 and ink_sk > 2:
                    stats['pd_blank_sk_ink'] += 1
            if ink_pe is not None and ink_pd is not None:
                if ink_pe < 0.5 and ink_pd > 2:
                    stats['pe_blank_pd_ink'] += 1
                if ink_pd < 0.5 and ink_pe > 2:
                    stats['pd_blank_pe_ink'] += 1

            dg = diags_by_tag.get(tag, [])
            dg_str = ';'.join(f'{b}:{k}({m[:60]})' for b, k, m in dg[:3]) if dg else ''

            def fmt(name, idx):
                v = cells[name][idx]
                if v is None:
                    return ''
                return f'{v:.2f}' if idx == 0 else (f'{v}' if idx == 1 else f'{v:.2f}')

            w.writerow([tag, pd_state, sk_state, pe_state,
                        fmt('skia', 0), fmt('skia', 1), fmt('skia', 2),
                        fmt('peach', 0), fmt('peach', 1), fmt('peach', 2),
                        f'{ink_pd:.2f}' if ink_pd is not None else '',
                        f'{ink_sk:.2f}' if ink_sk is not None else '',
                        f'{ink_pe:.2f}' if ink_pe is not None else '',
                        cells['skia'][3], cells['peach'][3], caveat, dg_str])

    for name in outliers:
        outliers[name].sort(reverse=True)
        outliers_clean[name].sort(reverse=True)

    lines = []
    def emit(s=''):
        print(s)
        lines.append(s)

    emit('Poso vs PDFium — pdf.js/test/pdfs, page 0, scale 2.0 (144 dpi)')
    emit(f'total corpus files: {stats["total"]}')
    emit(f'PDFium:       ok={stats["pd_ok"]}  load/render errors={stats["pd_err"]}')
    emit(f'Poso/Skia:    ok={stats["sk_ok"]}  missing={stats["sk_missing"]}')
    emit(f'Poso/Peach:   ok={stats["pe_ok"]}  missing={stats["pe_missing"]}')
    emit(f'compared Skia-vs-PDFium:  {stats["cmp_skia"]}')
    for k in BUCKETS:
        emit(f'  {k}: {buckets["skia"][k]}')
    emit(f'compared Peach-vs-PDFium: {stats["cmp_peach"]}')
    for k in BUCKETS:
        emit(f'  {k}: {buckets["peach"][k]}')
    emit('')
    emit(f'excluding {stats["caveats"]} files where PDFium\'s own behaviour explains the gap '
         '(it ignores /UserUnit, and it does not paint annotation appearance streams):')
    emit('  Skia:  ' + ', '.join(f'{k}={v}' for k, v in buckets_clean['skia'].items()))
    emit('  Peach: ' + ', '.join(f'{k}={v}' for k, v in buckets_clean['peach'].items()))
    emit('')
    emit(f'blank-vs-ink: skia blank but PDFium has ink={stats["sk_blank_pd_ink"]}, '
         f'peach blank but PDFium has ink={stats["pe_blank_pd_ink"]}, '
         f'PDFium blank but skia has ink={stats["pd_blank_sk_ink"]}, '
         f'PDFium blank but peach has ink={stats["pd_blank_pe_ink"]}')
    emit(f'PDFium cannot open but Poso renders: {stats["pd_err_poso_ok"]}')
    emit(f'neither engine produces a page: {stats["both_fail"]}')

    for name, label in (('skia', 'Skia'), ('peach', 'Peach')):
        emit('')
        emit(f'top {label}-vs-PDFium outliers (pct pixels >16 diff):')
        for p, t, m, mx in outliers[name][:30]:
            emit(f'  {p:6.2f}%  mean={m:5.2f} max={mx:3}  {t}')
        emit(f'top {label}-vs-PDFium outliers, oracle caveats excluded:')
        for p, t, m, mx in outliers_clean[name][:30]:
            emit(f'  {p:6.2f}%  mean={m:5.2f} max={mx:3}  {t}')

    emit('')
    emit(f'files where PDFium is the questionable side ({len(caveat_tags)}):')
    for t, c in caveat_tags:
        emit(f'  {c:24} {t}')
    emit('')
    emit(f'PDFium load/render errors ({len(pd_errors)}):')
    for t, e in pd_errors:
        emit(f'  {t}  {e[:90]}')
    emit('')
    emit(f'Poso produced no page ({len(poso_errors)}):')
    for t in poso_errors:
        emit(f'  {t}')

    (OUT / f'{REPORT_PREFIX}_summary.txt').write_text('\n'.join(lines) + '\n', encoding='utf-8')

    # worst-first worklist across both backends, restricted to files where PDFium is a fair oracle
    worst = {}
    for name in outliers_clean:
        for p, t, m, mx in outliers_clean[name]:
            prev = worst.get(t)
            if prev is None or p > prev[0]:
                worst[t] = (p, m, mx)
    # Both back ends' own numbers: sorting on the worse of the two hides which one is at fault.
    # text_clip_cff_cid sat near the top of this list at Skia 0.22% against Peach 19.04%, and the first
    # guess was the shared pipeline when the fault was PeachDrawing alone.
    triage = ['pct_pixels_diff\tmean_diff\tmax_diff\tskia_pct\tpeach_pct\tworse\ttag']
    for t, (p, m, mx) in sorted(worst.items(), key=lambda kv: -kv[1][0]):
        sk = next((v[0] for v in outliers_clean['skia'] if v[1] == t), None)
        pe = next((v[0] for v in outliers_clean['peach'] if v[1] == t), None)
        if sk is not None and pe is not None and abs(sk - pe) <= 3:
            worse = 'both'
        elif pe is not None and (sk is None or pe > sk):
            worse = 'peach'
        else:
            worse = 'skia'
        triage.append(f'{p:.2f}\t{m:.2f}\t{mx}\t{sk or 0:.2f}\t{pe or 0:.2f}\t{worse}\t{t}')
    triage.append('')
    triage.append('# excluded: PDFium is the questionable side on these')
    for t, c in caveat_tags:
        triage.append(f'# {c}\t{t}')
    (OUT / f'{REPORT_PREFIX}_triage.txt').write_text('\n'.join(triage) + '\n', encoding='utf-8')

    print(f'\nwrote {OUT / (REPORT_PREFIX + ".tsv")}')
    print(f'wrote {OUT / (REPORT_PREFIX + "_summary.txt")}')
    print(f'wrote {OUT / (REPORT_PREFIX + "_triage.txt")}')


if __name__ == '__main__':
    main()
