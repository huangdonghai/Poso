using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M1: encrypted PDF support. The fixture encrypts a document with an independent RC4 implementation
/// (standard security handler, V1/R2 40-bit, empty user password); the library must decrypt the
/// content stream and strings and render the page.
/// </summary>
public class EncryptionTests
{
    private static readonly IGraphicsFactory Factory = SkiaGraphicsFactory.Default;

    [Fact]
    public void Open_Rc4EncryptedPdf_DecryptsStreamAndStringAndRenders()
    {
        byte[] data = Rc4PdfBuilder.Build();

        using PdfDocument document = PdfDocument.FromArray(data);
        Assert.True(document.IsEncrypted);
        Assert.True(document.UserPasswordVerified);

        // The /Title lives in an encrypted string in the (indirect) Info dictionary.
        string? title = document.Info?.GetString("Title")?.AsTextString();
        Assert.Equal("SecretTitle", title);

        using IGraphicsContext context = Factory.CreateContext(200, 100);
        document.Pages[0].Render(Factory, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] pixels = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(pixels);

        // The decrypted content stream paints a blue rectangle covering user (10,10)-(90,90).
        int offset = ((80 * image.Width) + 50) * 4; // device (col 50, row 80) -> user (50,20)
        (byte b, byte g, byte r) = (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
        Assert.True(b > 170 && r < 110 && g < 110, $"Decrypted content should paint blue (r={r} g={g} b={b})");
    }

    /// <summary>A self-contained RC4 (V1/R2) encryptor that produces a classic-xref encrypted PDF.</summary>
    private static class Rc4PdfBuilder
    {
        private static readonly byte[] PasswordPad =
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
        };

        public static byte[] Build()
        {
            byte[] id0 = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
            byte[] owner = Enumerable.Range(0, 32).Select(i => (byte)(0x11 + i)).ToArray();
            const int p = -60; // 0xFFFFFFC4
            byte[] paddedUser = PasswordPad; // empty user password

            byte[] fileKey = DeriveFileKey(paddedUser, owner, p, id0);
            byte[] user = Rc4(fileKey, paddedUser); // /U (Algorithm 4, R2)

            string content = "0 0 1 rg 10 10 80 80 re f";
            byte[] contentPlain = Encoding.ASCII.GetBytes(content);
            byte[] contentCipher = Rc4(ObjectKey(fileKey, 4, 0), contentPlain);

            byte[] titlePlain = Encoding.ASCII.GetBytes("SecretTitle");
            byte[] titleCipher = Rc4(ObjectKey(fileKey, 5, 0), titlePlain);

            var objects = new List<(int Number, byte[] Body)>
            {
                (1, Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n")),
                (2, Ascii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n")),
                (3, Ascii("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>\nendobj\n")),
                (4, StreamObject(4, $"/Length {contentCipher.Length}", contentCipher)),
                (5, StringObject(5, "<< /Title ", titleCipher, " >>")),
                (6, Ascii($"6 0 obj\n<< /Filter /Standard /V 1 /R 2 /Length 40 /P {p} /O <{Hex(owner)}> /U <{Hex(user)}> >>\nendobj\n")),
            };

            return Assemble(objects, encryptObjectNumber: 6, id0);
        }

        private static byte[] DeriveFileKey(byte[] paddedUser, byte[] owner, int p, byte[] id0)
        {
            var seed = new List<byte>();
            seed.AddRange(paddedUser);
            seed.AddRange(owner);
            seed.AddRange(LittleEndian(p, 4));
            seed.AddRange(id0);
            byte[] hash = MD5.HashData(seed.ToArray());
            return hash.Take(5).ToArray(); // 40-bit
        }

        private static byte[] ObjectKey(byte[] fileKey, int obj, int gen)
        {
            var seed = new List<byte>();
            seed.AddRange(fileKey);
            seed.AddRange(LittleEndian(obj, 3));
            seed.AddRange(LittleEndian(gen, 2));
            byte[] hash = MD5.HashData(seed.ToArray());
            int length = System.Math.Min(16, fileKey.Length + 5);
            return hash.Take(length).ToArray();
        }

        private static byte[] Assemble(List<(int Number, byte[] Body)> objects, int encryptObjectNumber, byte[] id0)
        {
            using var stream = new MemoryStream();
            WriteAscii(stream, "%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
            var offsets = new Dictionary<int, int>();
            foreach ((int number, byte[] body) in objects)
            {
                offsets[number] = (int)stream.Position;
                stream.Write(body, 0, body.Length);
            }

            int maxNumber = objects.Max(o => o.Number);
            int xrefOffset = (int)stream.Position;
            WriteAscii(stream, $"xref\n0 {maxNumber + 1}\n0000000000 65535 f \n");
            for (int i = 1; i <= maxNumber; i++)
            {
                WriteAscii(stream, offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
            }

            string idHex = Hex(id0);
            WriteAscii(stream, $"trailer\n<< /Size {maxNumber + 1} /Root 1 0 R /Info 5 0 R /Encrypt {encryptObjectNumber} 0 R /ID [<{idHex}> <{idHex}>] >>\nstartxref\n{xrefOffset}\n%%EOF\n");
            return stream.ToArray();
        }

        private static byte[] StreamObject(int number, string extraDict, byte[] cipher)
        {
            string head = $"{number} 0 obj\n<< {extraDict.TrimStart()} >>\nstream\n";
            byte[] headBytes = Ascii(head);
            byte[] tail = Ascii("\nendstream\nendobj\n");
            byte[] result = new byte[headBytes.Length + cipher.Length + tail.Length];
            headBytes.CopyTo(result, 0);
            cipher.CopyTo(result, headBytes.Length);
            tail.CopyTo(result, headBytes.Length + cipher.Length);
            return result;
        }

        private static byte[] StringObject(int number, string prefix, byte[] cipherString, string suffix)
        {
            // Store the encrypted string as a hex literal to preserve arbitrary bytes verbatim.
            return Ascii($"{number} 0 obj\n{prefix}<{Hex(cipherString)}>{suffix}\nendobj\n");
        }

        private static byte[] Rc4(byte[] key, byte[] data)
        {
            int[] s = new int[256];
            for (int i = 0; i < 256; i++)
            {
                s[i] = i;
            }

            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
            }

            var output = new byte[data.Length];
            int x = 0;
            int y = 0;
            for (int k = 0; k < data.Length; k++)
            {
                x = (x + 1) & 0xFF;
                y = (y + s[x]) & 0xFF;
                (s[x], s[y]) = (s[y], s[x]);
                output[k] = (byte)(data[k] ^ s[(s[x] + s[y]) & 0xFF]);
            }

            return output;
        }

        private static byte[] LittleEndian(int value, int bytes)
        {
            var result = new byte[bytes];
            for (int i = 0; i < bytes; i++)
            {
                result[i] = (byte)((value >> (8 * i)) & 0xFF);
            }

            return result;
        }

        private static string Hex(byte[] data) => string.Concat(data.Select(b => b.ToString("X2")));

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        private static void WriteAscii(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
