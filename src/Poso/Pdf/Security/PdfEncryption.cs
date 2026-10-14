using System.Security.Cryptography;
using Poso.Pdf.Objects;

namespace Poso.Pdf.Security;

/// <summary>Encryption algorithm applied to a string or stream.</summary>
internal enum PdfCryptMode
{
    /// <summary>No transformation (Identity crypt filter).</summary>
    None,
    Rc4,
    Aes128,
    Aes256,
}

/// <summary>
/// Mutable holder shared by every parser and stream that touches encrypted content. The handler is
/// attached after the trailer is read, so objects decoded later (including cross-reference stream
/// data) see the key without a bootstrap ordering problem.
/// </summary>
public sealed class PdfEncryptionContext
{
    public PdfEncryption? Handler { get; set; }

    public string? Password { get; set; }

    /// <summary>Object number of the /Encrypt dictionary, whose own strings must never be decrypted.</summary>
    public int EncryptionDictObjectNumber { get; set; } = -1;

    public byte[] DecryptStringIfNeeded(byte[] value, int objectNumber, int generation, bool decryptStrings)
    {
        PdfEncryption? handler = Handler;
        if (handler is null || !decryptStrings || objectNumber == EncryptionDictObjectNumber)
        {
            return value;
        }

        return handler.Decrypt(value, objectNumber, generation, handler.StringMode);
    }

    public byte[] DecryptStreamIfNeeded(byte[] rawData, PdfDictionary streamDictionary, int objectNumber, int generation)
    {
        PdfEncryption? handler = Handler;
        if (handler is null || !handler.ShouldDecryptStream(streamDictionary))
        {
            return rawData;
        }

        return handler.Decrypt(rawData, objectNumber, generation, handler.StreamMode);
    }
}

/// <summary>
/// The PDF standard security handler (ISO 32000-1 7.6.3): derives the file encryption key from the
/// document password and per-object keys, and decrypts strings and streams. Covers RC4 (V1/V2 and V4
/// CFM /V2) and AES-128-CBC (V4 CFM /AESV2). AES-256 (V5 /R5 /R6) is detected but reported unsupported
/// so callers can degrade gracefully instead of failing.
/// </summary>
public sealed class PdfEncryption
{
    private static readonly byte[] PasswordPad =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private static readonly byte[] AesSalt = { 0x73, 0x41, 0x6C, 0x54 }; // "sAlT"

    private readonly byte[] _fileKey;

    private PdfEncryption(byte[] fileKey, int revision, bool encryptMetadata, PdfCryptMode streamMode, PdfCryptMode stringMode)
    {
        _fileKey = fileKey;
        Revision = revision;
        EncryptMetadata = encryptMetadata;
        StreamMode = streamMode;
        StringMode = stringMode;
    }

    public int Revision { get; }

    public bool EncryptMetadata { get; }

    internal PdfCryptMode StreamMode { get; }

    internal PdfCryptMode StringMode { get; }

    /// <summary>True when the supplied password matched the user password entry (/U).</summary>
    public bool UserPasswordVerified { get; private set; }

    /// <summary>
    /// Builds a handler from a resolved /Encrypt dictionary. Returns null for unsupported schemes
    /// (non-standard filter, AES-256 /V5) after reporting through <paramref name="diagnostics"/>.
    /// </summary>
    public static PdfEncryption? TryCreate(
        PdfDictionary encryptDictionary,
        byte[]? id0,
        string? password,
        PdfDiagnostics? diagnostics)
    {
        string? filter = encryptDictionary.GetNameValue("Filter");
        if (filter is not null and not "Standard")
        {
            diagnostics?.Report(PdfUnsupportedKind.CryptFilter, $"Unsupported security handler filter: {filter}");
            return null;
        }

        int version = (int)(encryptDictionary.GetIntegerValue("V") ?? 0);
        int revision = (int)(encryptDictionary.GetIntegerValue("R") ?? 0);
        int lengthBits = (int)(encryptDictionary.GetIntegerValue("Length") ?? (revision >= 3 ? 128 : 40));
        bool encryptMetadata = encryptDictionary.GetBooleanValue("EncryptMetadata") ?? true;

        if (version >= 5 || revision >= 5)
        {
            return TryCreateAes256(encryptDictionary, revision, password, encryptMetadata, diagnostics);
        }

        byte[] owner = ReadFixed(encryptDictionary.GetString("O"), 32);
        byte[] user = ReadFixed(encryptDictionary.GetString("U"), 32);
        int flags = unchecked((int)(encryptDictionary.GetIntegerValue("P") ?? -1));

        (PdfCryptMode streamMode, PdfCryptMode stringMode) = version switch
        {
            <= 3 => (PdfCryptMode.Rc4, PdfCryptMode.Rc4),
            _ => ResolveCryptFilters(encryptDictionary, lengthBits, diagnostics),
        };

        if (streamMode == PdfCryptMode.Aes256 || stringMode == PdfCryptMode.Aes256)
        {
            diagnostics?.Report(PdfUnsupportedKind.CryptFilter, "AES-256 crypt filter (AESV3) is not supported yet");
            return null;
        }

        int keyLength = version switch
        {
            1 => 5,
            2 => 16,
            <= 3 => Math.Clamp(lengthBits / 8, 5, 16),
            _ => Math.Clamp(lengthBits / 8, 5, 16),
        };

        byte[] paddedPassword = PadPassword(password ?? string.Empty);
        byte[] fileKey = ComputeEncryptionKey(paddedPassword, owner, flags, id0 ?? Array.Empty<byte>(), revision, keyLength, encryptMetadata);

        var handler = new PdfEncryption(fileKey, revision, encryptMetadata, streamMode, stringMode);
        handler.UserPasswordVerified = VerifyUserPassword(handler, user, revision, id0 ?? Array.Empty<byte>());
        return handler;
    }

    public bool ShouldDecryptStream(PdfDictionary streamDictionary)
    {
        if (StreamMode == PdfCryptMode.None)
        {
            return false;
        }

        // When metadata is not encrypted the XML metadata stream is left in the clear.
        if (!EncryptMetadata &&
            streamDictionary.GetNameValue("Type") == "XObject" &&
            streamDictionary.GetNameValue("Subtype") == "XML")
        {
            return false;
        }

        return true;
    }

    internal byte[] Decrypt(byte[] data, int objectNumber, int generation, PdfCryptMode mode)
    {
        if (mode == PdfCryptMode.None || data.Length == 0)
        {
            return data;
        }

        if (mode == PdfCryptMode.Aes256)
        {
            // V5 (R5/R6): the 32-byte file key is used directly (no per-object key), AES-256-CBC
            // with a random IV stored as the first 16 bytes and PKCS#7 padding.
            return AesCbcPkcs7Decrypt(_fileKey, data);
        }

        byte[] objectKey = ComputeObjectKey(objectNumber, generation, mode);
        return mode switch
        {
            PdfCryptMode.Rc4 => Rc4.Process(objectKey, data),
            PdfCryptMode.Aes128 => AesCbcDecrypt(objectKey, data),
            _ => data,
        };
    }

    // ---- Key derivation (ISO 32000-1 7.6.3.3) ----

    private static byte[] ComputeEncryptionKey(
        byte[] paddedPassword,
        byte[] owner,
        int flags,
        byte[] id0,
        int revision,
        int keyLength,
        bool encryptMetadata)
    {
        using var md5 = MD5.Create();
        var seed = new List<byte>(paddedPassword.Length + owner.Length + 4 + id0.Length + 4);
        seed.AddRange(paddedPassword);
        seed.AddRange(owner);
        seed.AddRange(ToLittleEndian(flags, 4));
        seed.AddRange(id0);
        if (revision >= 4 && !encryptMetadata)
        {
            seed.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        }

        byte[] hash = md5.ComputeHash(seed.ToArray());
        if (revision >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = md5.ComputeHash(hash, 0, keyLength);
            }
        }

        return Trim(hash, keyLength);
    }

    private byte[] ComputeObjectKey(int objectNumber, int generation, PdfCryptMode mode)
    {
        using var md5 = MD5.Create();
        var seed = new List<byte>(_fileKey.Length + 5 + 4);
        seed.AddRange(_fileKey);
        seed.AddRange(ToLittleEndian(objectNumber, 3));
        seed.AddRange(ToLittleEndian(generation, 2));
        if (mode != PdfCryptMode.Rc4)
        {
            seed.AddRange(AesSalt);
        }

        byte[] hash = md5.ComputeHash(seed.ToArray());
        int length = Math.Min(16, _fileKey.Length + 5);
        return Trim(hash, length);
    }

    private static bool VerifyUserPassword(PdfEncryption handler, byte[] user, int revision, byte[] id0)
    {
        // ISO 32000-1 Algorithms 4/5: the /U value is derived from the fixed padding string (and, for
        // revision 3+, the first /ID element) encrypted with the file key. The password influences the
        // result only through the file key, not directly. So verification uses PasswordPad, not the password.
        if (revision <= 2)
        {
            byte[] encrypted = Rc4.Process(handler._fileKey, PasswordPad);
            return SequenceEqualPrefix(encrypted, user, 32);
        }

        using (var md5 = MD5.Create())
        {
            var temp = new List<byte>(PasswordPad.Length + id0.Length);
            temp.AddRange(PasswordPad);
            temp.AddRange(id0);
            byte[] hash = Trim(md5.ComputeHash(temp.ToArray()), 16);

            byte[] value = Rc4.Process(handler._fileKey, hash);
            var key = new byte[handler._fileKey.Length];
            for (int round = 1; round <= 19; round++)
            {
                for (int i = 0; i < key.Length; i++)
                {
                    key[i] = (byte)(handler._fileKey[i] ^ round);
                }

                value = Rc4.Process(key, value);
            }

            return SequenceEqualPrefix(value, user, 16);
        }
    }

    private static (PdfCryptMode Stream, PdfCryptMode String) ResolveCryptFilters(PdfDictionary encryptDictionary, int lengthBits, PdfDiagnostics? diagnostics)
    {
        PdfDictionary? cryptFilters = encryptDictionary.GetDictionary("CF");
        string? streamName = encryptDictionary.GetNameValue("StmF");
        string? stringName = encryptDictionary.GetNameValue("StrF");

        PdfCryptMode streamMode = MapFilter(cryptFilters, streamName, lengthBits, diagnostics);
        PdfCryptMode stringMode = MapFilter(cryptFilters, stringName, lengthBits, diagnostics);
        return (streamMode, stringMode);
    }

    private static PdfCryptMode MapFilter(PdfDictionary? cryptFilters, string? name, int lengthBits, PdfDiagnostics? diagnostics)
    {
        if (name is null)
        {
            return PdfCryptMode.Rc4; // V4 without named filters defaults to the RC4 handler
        }

        if (name == "Identity")
        {
            return PdfCryptMode.None;
        }

        PdfDictionary? filter = cryptFilters?.GetDictionary(name);
        string? cfm = filter?.GetNameValue("CFM") ?? "V2";
        return cfm switch
        {
            "None" => PdfCryptMode.None,
            "V2" => PdfCryptMode.Rc4,
            "AESV2" => PdfCryptMode.Aes128,
            "AESV3" => PdfCryptMode.Aes256,
            _ => PdfCryptMode.Rc4,
        };
    }

    // ---- Primitives ----

    private static byte[] AesCbcDecrypt(byte[] key, byte[] data)
    {
        if (data.Length < 16)
        {
            return data;
        }

        var iv = new byte[16];
        Array.Copy(data, 0, iv, 0, 16);
        int payloadLength = data.Length - 16;
        var payload = new byte[payloadLength];
        Array.Copy(data, 16, payload, 0, payloadLength);

        using var aes = Aes.Create();
        aes.BlockSize = 128;
        aes.KeySize = key.Length * 8;
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.FeedbackSize = 128;

        try
        {
            aes.Padding = PaddingMode.PKCS7;
            return aes.DecryptCbc(payload, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            // Some writers omit the PKCS#7 padding block; return the raw CBC output.
            return aes.DecryptCbc(payload, iv, PaddingMode.None);
        }
    }

    private static byte[] PadPassword(string password)
    {
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(password);
        var padded = new byte[32];
        int copy = Math.Min(bytes.Length, 32);
        Array.Copy(bytes, 0, padded, 0, copy);
        if (copy < 32)
        {
            Array.Copy(PasswordPad, 0, padded, copy, 32 - copy);
        }

        return padded;
    }

    // ---- AES-256 (V5 / R5 / R6), ISO 32000-2 7.12.2 ----

    private static PdfEncryption? TryCreateAes256(
        PdfDictionary encryptDictionary,
        int revision,
        string? password,
        bool encryptMetadata,
        PdfDiagnostics? diagnostics)
    {
        byte[] u = ReadFixed(encryptDictionary.GetString("U"), 48);
        byte[] ue = ReadFixed(encryptDictionary.GetString("UE"), 32);
        byte[] o = ReadFixed(encryptDictionary.GetString("O"), 48);
        byte[] oe = ReadFixed(encryptDictionary.GetString("OE"), 32);
        if (revision is < 5 or > 6)
        {
            diagnostics?.Report(PdfUnsupportedKind.CryptFilter, $"Unsupported AES-256 revision {revision}");
            return null;
        }

        byte[] pwd = System.Text.Encoding.UTF8.GetBytes(password ?? string.Empty);
        if (pwd.Length > 127)
        {
            pwd = pwd[..127];
        }

        byte[] empty = Array.Empty<byte>();
        byte[] fileKey;
        bool userVerified;
        if (SequenceEqualPrefix(CalculateHash(revision, pwd, u[32..40], empty), u, 32))
        {
            byte[] tmp = CalculateHash(revision, pwd, u[40..48], empty);
            fileKey = AesCbcNoPaddingDecrypt(tmp, new byte[16], ue);
            userVerified = true;
        }
        else if (SequenceEqualPrefix(CalculateHash(revision, pwd, o[32..40], u), o, 32))
        {
            byte[] tmp = CalculateHash(revision, pwd, o[40..48], u);
            fileKey = AesCbcNoPaddingDecrypt(tmp, new byte[16], oe);
            userVerified = false;
        }
        else
        {
            diagnostics?.Report(PdfUnsupportedKind.CryptFilter, "Incorrect password or unsupported AES-256 (V5/R5/R6) parameters");
            return null;
        }

        var handler = new PdfEncryption(fileKey, revision, encryptMetadata, PdfCryptMode.Aes256, PdfCryptMode.Aes256);
        handler.UserPasswordVerified = userVerified;
        return handler;
    }

    /// <summary>Algorithm 2.B (R6) / SHA-256 (R5): iterated hash used by the AES-256 handler.</summary>
    private static byte[] CalculateHash(int revision, byte[] password, byte[] salt, byte[] udata)
    {
        byte[] k = SHA256.HashData(Concat(password, salt, udata));
        if (revision < 6)
        {
            return k;
        }

        int count = 0;
        while (true)
        {
            count++;
            byte[] block = Concat(password, k, udata);
            byte[] e = AesCbcNoPaddingEncrypt(k[..16], k[16..32], Repeat(block, 64));
            k = (Sum(e[..16]) % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };
            if (count >= 64 && e[e.Length - 1] <= count - 32)
            {
                break;
            }
        }

        return k[..32];
    }

    private static byte[] AesCbcPkcs7Decrypt(byte[] key, byte[] data)
    {
        if (data.Length < 32)
        {
            return data;
        }

        using var aes = NewAes(key, CipherMode.CBC);
        var iv = new byte[16];
        Array.Copy(data, 0, iv, 0, 16);
        var payload = new byte[data.Length - 16];
        Array.Copy(data, 16, payload, 0, payload.Length);
        try
        {
            return aes.DecryptCbc(payload, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return aes.DecryptCbc(payload, iv, PaddingMode.None);
        }
    }

    private static byte[] AesCbcNoPaddingDecrypt(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = NewAes(key, CipherMode.CBC);
        return aes.DecryptCbc(data, iv, PaddingMode.None);
    }

    private static byte[] AesCbcNoPaddingEncrypt(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = NewAes(key, CipherMode.CBC);
        return aes.EncryptCbc(data, iv, PaddingMode.None);
    }

    private static Aes NewAes(byte[] key, CipherMode mode)
    {
        var aes = Aes.Create();
        aes.BlockSize = 128;
        aes.KeySize = key.Length * 8;
        aes.Key = key;
        aes.Mode = mode;
        aes.FeedbackSize = 128;
        return aes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var list = new List<byte>();
        foreach (byte[] part in parts)
        {
            list.AddRange(part);
        }

        return list.ToArray();
    }

    private static byte[] Repeat(byte[] data, int times)
    {
        var result = new byte[data.Length * times];
        for (int i = 0; i < times; i++)
        {
            Array.Copy(data, 0, result, i * data.Length, data.Length);
        }

        return result;
    }

    private static int Sum(byte[] data)
    {
        int total = 0;
        foreach (byte b in data)
        {
            total += b;
        }

        return total;
    }

    private static byte[] ReadFixed(PdfString? value, int length)
    {
        var buffer = new byte[length];
        if (value is not null)
        {
            ReadOnlySpan<byte> src = value.Value;
            int copy = Math.Min(src.Length, length);
            src.Slice(0, copy).CopyTo(buffer.AsSpan(0, copy));
        }

        return buffer;
    }

    private static byte[] Trim(byte[] data, int length)
    {
        if (length >= data.Length)
        {
            return data;
        }

        var trimmed = new byte[length];
        Array.Copy(data, trimmed, length);
        return trimmed;
    }

    private static byte[] ToLittleEndian(int value, int bytes)
    {
        var result = new byte[bytes];
        for (int i = 0; i < bytes; i++)
        {
            result[i] = (byte)((value >> (8 * i)) & 0xFF);
        }

        return result;
    }

    private static bool SequenceEqualPrefix(byte[] a, byte[] b, int length)
    {
        int n = Math.Min(Math.Min(a.Length, b.Length), length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return n > 0;
    }
}

/// <summary>Minimal RC4 (ARCFOUR) used by the classic PDF security handler.</summary>
internal static class Rc4
{
    public static byte[] Process(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var state = new int[256];
        for (int i = 0; i < 256; i++)
        {
            state[i] = i;
        }

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
        }

        var output = new byte[data.Length];
        int x = 0;
        int y = 0;
        for (int k = 0; k < data.Length; k++)
        {
            x = (x + 1) & 0xFF;
            y = (y + state[x]) & 0xFF;
            (state[x], state[y]) = (state[y], state[x]);
            output[k] = (byte)(data[k] ^ state[(state[x] + state[y]) & 0xFF]);
        }

        return output;
    }
}
