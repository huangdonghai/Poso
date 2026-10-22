"""Cross-check Poso's TrueType outline reader against FreeType's.

For every TrueType program embedded in the corpus, the glyph bounds Poso's reader produces
are compared with the bounds FreeType's reader produces for the same program. The point
counts cannot be compared (a quadratic becomes a cubic in Poso's path interface), but the
bounds are the shape's extent, so a misread point, a mis-handled off-curve point or a
mis-walked contour shows up there.

The reference dump comes from ftdump (a small FreeType program, see tools/corpus/README);
Poso's side comes from the OutlineDumpTest probe in the test project.
"""
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).parent
CORPUS = pathlib.Path(os.environ.get('POSO_CORPUS') or r'D:\_git0\pdfjs\test\pdfs')
FTDUMP = pathlib.Path(os.environ.get('POSO_FTDUMP') or r'C:\Users\dunhi\AppData\Local\Temp\ftprobe\b7\Release\ftdump.exe')
TESTS = pathlib.Path(os.environ.get('POSO_TESTS') or r'D:\_git0\Poso\tests\Poso.Tests')
WORK = pathlib.Path(os.environ.get('POSO_WORK') or ROOT / 'crosscheck')
WORK.mkdir(exist_ok=True)

# The PDFs in the corpus that embed a TrueType program (FontFile2). A program is found by
# looking for a stream that starts with an sfnt tag.
import zlib

SFNT = (b'\x00\x01\x00\x00', b'true', b'ttcf', b'OTTO')


def embedded_programs(path: pathlib.Path):
    """The sfnt programs embedded in a PDF."""
    try:
        data = path.read_bytes()
    except OSError:
        return []
    found = []
    for m in re.finditer(rb'stream\r?\n', data):
        start = m.end()
        end = data.find(b'endstream', start)
        if end < 0:
            continue
        blob = data[start:end]
        for candidate in (blob,):
            try:
                dec = zlib.decompressobj().decompress(candidate)
            except Exception:
                dec = candidate
            if len(dec) > 12 and dec[:4] in SFNT:
                found.append(dec)
    return found


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def main():
    if not FTDUMP.exists():
        print(f'missing {FTDUMP}')
        return 1

    files = sorted(CORPUS.glob('*.pdf'))
    checked = 0
    mismatched = []
    for pdf in files:
        for i, program in enumerate(embedded_programs(pdf)):
            prog_path = WORK / f'{pdf.stem}-{i}.ttf'
            prog_path.write_bytes(program)
            ref_path = WORK / f'{pdf.stem}-{i}.ref.txt'
            r = run([str(FTDUMP), str(prog_path), '0', '400'])
            if r.returncode != 0:
                continue
            ref_path.write_text(r.stdout)

            env = dict(os.environ)
            env['POSO_OUTLINE_DUMP'] = str(WORK / f'{pdf.stem}-{i}.poso.txt')
            env['POSO_OUTLINE_PROGRAM'] = str(prog_path)
            env['POSO_OUTLINE_FIRST'] = '0'
            env['POSO_OUTLINE_COUNT'] = '400'
            r = run(['dotnet', 'test', str(TESTS / 'Poso.Tests.csproj'), '-c', 'Release',
                     '--no-build', '--filter', 'Dump_TrueTypeOutlines_ForCrossCheck'], env=env)
            if r.returncode != 0:
                continue

            ref = {}
            for line in ref_path.read_text().splitlines():
                m = re.match(r'^(\d+): points=(\d+) contours=(\d+) bbox=([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)', line)
                if m:
                    ref[int(m.group(1))] = tuple(float(m.group(k)) for k in (4, 5, 6, 7))

            ours = {}
            poso_path = WORK / f'{pdf.stem}-{i}.poso.txt'
            if not poso_path.exists():
                continue
            for line in poso_path.read_text().splitlines():
                m = re.match(r'^(\d+): segs=\d+ bounds=([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)', line)
                if m:
                    ours[int(m.group(1))] = tuple(float(m.group(k)) for k in (2, 3, 4, 5))

            checked += 1
            for gid, rb in ref.items():
                ob = ours.get(gid)
                if ob is None:
                    continue
                # An empty glyph in one and not the other is a mismatch.
                ref_empty = (rb[2] - rb[0]) <= 0.0001
                our_empty = (ob[2] - ob[0]) <= 0.0001
                if ref_empty != our_empty:
                    mismatched.append((pdf.name, gid, 'empty', rb, ob))
                    continue
                if ref_empty:
                    continue
                # The bounds must agree to within a thousandth of an em.
                if max(abs(rb[k] - ob[k]) for k in range(4)) > 0.001:
                    mismatched.append((pdf.name, gid, 'bounds', rb, ob))

    print(f'checked {checked} programs')
    print(f'mismatched glyphs: {len(mismatched)}')
    by_file = {}
    for name, gid, kind, rb, ob in mismatched:
        by_file.setdefault(name, []).append((gid, kind, rb, ob))
    for name in sorted(by_file, key=lambda n: -len(by_file[n]))[:25]:
        entries = by_file[name]
        print(f'  {name}: {len(entries)} mismatched glyphs')
        for gid, kind, rb, ob in entries[:4]:
            print(f'     gid {gid} {kind}: ref={tuple(round(v,4) for v in rb)} ours={tuple(round(v,4) for v in ob)}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
