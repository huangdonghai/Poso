"""Scan the corpus for embedded TrueType programs that have no cmap table.

A TrueType font without a cmap cannot be addressed by character. Skia's GetGlyphPath
returns an empty path for many of its glyph ids in that case, so a CID font (Identity-H)
whose program is such a font loses the glyphs whose ids Skia cannot reach.

The corpus directory is the POSO_CORPUS environment variable, as in harness.py.
"""
import os
import pathlib
import re
import struct
import sys
import zlib

CORPUS = pathlib.Path(os.environ.get('POSO_CORPUS') or r'D:\_git0\pdfjs\test\pdfs')


def has_cmap(font: bytes) -> bool:
    if len(font) < 12:
        return False
    num_tables = struct.unpack('>H', font[4:6])[0]
    for i in range(num_tables):
        off = 12 + i * 16
        if off + 16 > len(font):
            return False
        if font[off:off + 4] == b'cmap':
            return True
    return False


def scan(path: pathlib.Path):
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
            if len(dec) < 12:
                continue
            if dec[:4] not in (b'\x00\x01\x00\x00', b'true', b'ttcf', b'OTTO'):
                continue
            if not has_cmap(dec):
                found.append(len(dec))
    return found


def main():
    files = sorted(CORPUS.glob('*.pdf'))
    hits = []
    for p in files:
        found = scan(p)
        if found:
            hits.append((p.name, found))
    print(f'{len(hits)} of {len(files)} corpus files embed a TrueType program with no cmap:')
    for name, sizes in hits:
        print(f'  {name}  ({", ".join(str(s) for s in sizes)} bytes)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
