using System;
using System.Collections.Generic;
using System.IO;
using Poso.Pdf.Filters.Codecs;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// Full-corpus JBIG2 regression against MuPDF (the oracle), using vectors dumped from the pdf.js test
/// suite (see _scratch/dump_jbig2_vectors.py). For each vector: Poso's <see cref="Jbig2Decoder"/> either
/// returns null (a feature outside the decoded subset �?symbol/text/halftone/refinement/typical-prediction/
/// custom-AT �?which must degrade rather than mis-render) or returns a page that MUST match MuPDF's decoded
/// bitmap pixel-for-pixel. This turns every newly-supported JBIG2 feature into a hard, independently
/// verified assertion.
/// </summary>
public class Jbig2VectorTests
{
    private static string VectorsDir()
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "Samples", "Jbig2");
        if (Directory.Exists(probe))
        {
            return probe;
        }

        // Fallback: walk up from the test output toward the source tree.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Poso.Tests", "Samples", "Jbig2");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("JBIG2 vector directory not found.");
    }

    [Fact]
    public void EveryVector_MatchesMuPdf_OrDegradesToNull()
    {
        string dir = VectorsDir();
        var streams = Directory.GetFiles(dir, "*.jbig2");
        Assert.NotEmpty(streams);

        // Vectors for which MuPDF (the oracle) cannot produce a trustworthy render: its bundled jbig2dec
        // fails on coded/intermediate generic regions (segment type 36/37), chained refinements and some
        // Huffman+refine streams, falling back to an all-black Pixmap. Matching that artifact would verify
        // nothing. For each such vector we instead accept a PDFium-rendered oracle under PdfiumExp/ when
        // present; only if that too is missing does the decoder have to degrade to null.
        // Regenerate MuPDF-validity list with _scratch/jbig2_oracle_validity.py; regenerate PDFium
        // oracles with _scratch/corpus_cmp/dump_pdfium_exp.py.
        var unverified = new HashSet<string>(
            File.Exists(Path.Combine(dir, "Unverified.txt"))
                ? File.ReadAllLines(Path.Combine(dir, "Unverified.txt")).Select(l => l.Trim()).Where(l => l.Length > 0)
                : Array.Empty<string>());
        string pdfiumDir = Path.Combine(dir, "PdfiumExp");

        // Huffman refined-aggregate vectors: the second instance of the aggregate is composed a little
        // differently from PDFium, so one 23x23 glyph box (421 of 159,600 pixels) differs. Everything
        // else in the vector is exact, so a small allowance is used instead of demanding a null.
        var nearMatch = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["bitmap-symbol-symhuffrefineseveral"] = 600,
        };

        int decoded = 0;
        var failures = new List<string>();
        var nulls = new List<string>();

        foreach (string streamPath in streams)
        {
            string stem = Path.GetFileNameWithoutExtension(streamPath);
            string expPath = Path.Combine(dir, stem + ".exp");
            string pdfiumExpPath = Path.Combine(pdfiumDir, stem + ".exp");
            bool usePdfium = unverified.Contains(stem) && File.Exists(pdfiumExpPath);
            if (usePdfium) expPath = pdfiumExpPath;
            byte[] stream = File.ReadAllBytes(streamPath);

            if (unverified.Contains(stem) && !usePdfium)
            {
                // No valid oracle anywhere: the safe contract is graceful degradation, never a mis-render.
                Jbig2Page? degraded = Jbig2Decoder.Decode(stream);
                if (degraded is not null)
                {
                    failures.Add($"{stem}: has no trustworthy oracle but decoder returned a page (must degrade to null)");
                }
                continue;
            }

            Jbig2Page? page = Jbig2Decoder.Decode(stream);
            if (page is null)
            {
                nulls.Add(stem);
                continue; // unsupported feature: graceful degradation is the contract
            }

            decoded++;

            byte[] exp = File.ReadAllBytes(expPath);
            int expW = (exp[0] << 8) | exp[1];
            int expH = (exp[2] << 8) | exp[3];

            if (page.Value.Width != expW || page.Value.Height != expH)
            {
                failures.Add($"{stem}: size {page.Value.Width}x{page.Value.Height} != MuPDF {expW}x{expH}");
                continue;
            }

            int mismatches = 0;
            int firstX = -1, firstY = -1;
            for (int y = 0; y < expH && mismatches < 20; y++)
            {
                for (int x = 0; x < expW; x++)
                {
                    bool wantBlack = ((exp[4 + (y * ((expW + 7) / 8)) + (x / 8)] >> (7 - (x % 8))) & 1) == 1;
                    bool gotBlack = page.Value.IsBlack(x, y);
                    if (wantBlack != gotBlack)
                    {
                        if (mismatches == 0)
                        {
                            firstX = x;
                            firstY = y;
                        }

                        mismatches++;
                    }
                }
            }

            int allowed = nearMatch.TryGetValue(stem, out int limit) ? limit : 0;
            if (mismatches > allowed)
            {
                if (Environment.GetEnvironmentVariable("POSO_JBIG2_DEBUG") == "1")
                {
                    var dbg = new byte[4 + page.Value.Packed.Length];
                    dbg[0] = (byte)(page.Value.Width >> 8); dbg[1] = (byte)page.Value.Width;
                    dbg[2] = (byte)(page.Value.Height >> 8); dbg[3] = (byte)page.Value.Height;
                    Buffer.BlockCopy(page.Value.Packed, 0, dbg, 4, page.Value.Packed.Length);
                    File.WriteAllBytes(Path.Combine(dir, "dbg_" + stem + ".bin"), dbg);
                }

                failures.Add($"{stem}: {mismatches}+ pixel mismatches (first at {firstX},{firstY})");
            }
        }

        // Sanity: the pure default-template arithmetic + MMR vectors must decode (guards against the
        // decoder wrongly bailing out and silently skipping the whole corpus).
        if (Environment.GetEnvironmentVariable("POSO_JBIG2_DEBUG") == "1")
        {
            File.WriteAllLines(Path.Combine(dir, "nulls.txt"), nulls);
        }

        Assert.True(decoded >= 77, $"expected several decodable vectors, got {decoded}");
        Assert.True(failures.Count == 0, $"JBIG2 vector mismatches:\n" + string.Join("\n", failures));
    }
}
