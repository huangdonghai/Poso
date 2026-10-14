using System;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Pdf.Objects;
using Poso.Pdf.Security;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M1: AES-256 (V5 / R6) decryption. The embedded document is an authentic PyMuPDF/AES-256 PDF
/// (user password "secret123") produced with MuPDF as ground truth.
/// </summary>
public class Aes256Tests
{
    private const string PdfBase64 = "JVBERi0xLjcKJcK1wrYKJSBXcml0dGVuIGJ5IE11UERGIDEuMjguMgoKMSAwIG9iago8PC9UeXBlL0NhdGFsb2cvUGFnZXMgMiAwIFIvSW5mbzw8L1Byb2R1Y2VyPERCNDYyMUNENzI5MjE1RDA3RDhCMTg2NTM2NzE3Mzk3QkM5MUNCRkQxRTY4REYxRkRFQjIwOTQwRDNGNEYyODM+Pj4+PgplbmRvYmoKCjIgMCBvYmoKPDwvVHlwZS9QYWdlcy9Db3VudCAxL0tpZHNbNCAwIFJdPj4KZW5kb2JqCgozIDAgb2JqCjw8L0ZvbnQ8PC9oZWx2IDUgMCBSPj4+PgplbmRvYmoKCjQgMCBvYmoKPDwvVHlwZS9QYWdlL01lZGlhQm94WzAgMCAyMDAgMTAwXS9Sb3RhdGUgMC9SZXNvdXJjZXMgMyAwIFIvUGFyZW50IDIgMCBSL0NvbnRlbnRzWzYgMCBSXT4+CmVuZG9iagoKNSAwIG9iago8PC9UeXBlL0ZvbnQvU3VidHlwZS9UeXBlMS9CYXNlRm9udC9IZWx2ZXRpY2EvRW5jb2RpbmcvV2luQW5zaUVuY29kaW5nPj4KZW5kb2JqCgo2IDAgb2JqCjw8L0xlbmd0aCA5Nj4+CnN0cmVhbQqxNyvuCV87ISwAc+hA6e3wrkMncv8tiOR701slWb1Z/3cGbQvlP+/hv2/0FUFJbV4bhfVX+pamCBYFeQ3Xm4SYgBH5Js5bLkl+JX8bWaXJdLnvooM8vxlylyIwkIkQG84KZW5kc3RyZWFtCmVuZG9iagoKeHJlZgowIDcKMDAwMDAwMDAwMCA2NTUzNSBmIAowMDAwMDAwMDQyIDAwMDAwIG4gCjAwMDAwMDAxNzIgMDAwMDAgbiAKMDAwMDAwMDIyNCAwMDAwMCBuIAowMDAwMDAwMjY1IDAwMDAwIG4gCjAwMDAwMDAzNzIgMDAwMDAgbiAKMDAwMDAwMDQ2MSAwMDAwMCBuIAoKdHJhaWxlcgo8PC9TaXplIDcvUm9vdCAxIDAgUi9JRFs8QzJCNEMzODVDM0I1NjlDM0JGQzJCRjdDQzI4RjUxQzM+PDZBQTU1MzBEMjBBNkRDN0U3MUIyQjU1RDgxMjYwREFGPl0vRW5jcnlwdDw8L0ZpbHRlci9TdGFuZGFyZC9SIDYvViA1L0xlbmd0aCAyNTYvUCAtMzM4OC9FbmNyeXB0TWV0YWRhdGEgdHJ1ZS9TdG1GL1N0ZENGL1N0ckYvU3RkQ0YvQ0Y8PC9TdGRDRjw8L0F1dGhFdmVudC9Eb2NPcGVuL0NGTS9BRVNWMy9MZW5ndGggMzI+Pj4+L088Njk2NDI5MDdERTc5MkFFNkVCMDdEMUVCMEEzOEI4RjEyQkMyQjRBMzE2RTQ4RjIxNzY5RkIzOUE0M0JCMDNBQkY0MkIxMkE4MjNFMDE2NDY5QzI5QjNGRURFQjMwMjc3Pi9VPEQ2Qjg4NjRFRjY5Qjc2MkMwRDdERjFEODQ2QjZCMzUyRTg1M0YwNUFDMDcxRTNEODEzOUEwNjlGMDZBNEMwMzExMEIzMzZDMjE5RkFCQTM4QjFEMDFEMDI5MTVEMEY2RT4vT0U8QUY5QUE1NjEzNkQ3MTgzMDBDNkE0MjZCNDI1NTc1OEUxQTg3NEZBMEY0Nzc3NTBBNDdEMUIzNDE3RjcxRTE1NT4vVUU8NjM5OEIxQkIxRjQ5QTRFMzBDOEZCOUM3QUYzNTUyNDM1OTVCODMyOTVCOTYzNTA2ODYwQkRCMzE1MDYyRTcxRT4vUGVybXM8RUQ4NzVCMUQ0N0Q4NkJCRTYxMjdDQTVFRDBGQzVFODE+Pj4+PgpzdGFydHhyZWYKNjA2CiUlRU9GCg==";

    [Fact]
    public void OpensAes256R6WithPassword_AndRenders()
    {
        byte[] bytes = Convert.FromBase64String(PdfBase64);
        using PdfDocument document = PdfDocument.FromArray(bytes, "secret123");
        Assert.True(document.IsEncrypted);
        Assert.True(document.UserPasswordVerified);
        Assert.Equal(1, document.PageCount);

        using IGraphicsContext context = SkiaGraphicsFactory.Default.CreateContext(200, 100);
        document.Pages[0].Render(SkiaGraphicsFactory.Default, context, new PdfRenderOptions(1f, 0f, PosoColor.White));

        using IImage image = context.Snapshot();
        byte[] px = new byte[image.Width * image.Height * image.BytesPerPixel];
        image.CopyPixels(px);
        int ink = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i] < 200 || px[i + 1] < 200 || px[i + 2] < 200)
            {
                ink++;
            }
        }

        Assert.True(ink > 50, $"decrypted text should render ink ({ink})");
    }

    [Fact]
    public void Handler_DirectTryCreate_Succeeds()
    {
        var dict = new PdfDictionary();
        dict["Filter"] = new PdfName("Standard");
        dict["V"] = new PdfNumber(5, true);
        dict["R"] = new PdfNumber(6, true);
        dict["Length"] = new PdfNumber(256, true);
        dict["P"] = new PdfNumber(-3388, true);
        dict["EncryptMetadata"] = PdfBoolean.True;
        dict["O"] = new PdfString(Convert.FromHexString("69642907DE792AE6EB07D1EB0A38B8F12BC2B4A316E48F21769FB39A43BB03ABF42B12A823E016469C29B3FEDEB30277"));
        dict["U"] = new PdfString(Convert.FromHexString("D6B8864EF69B762C0D7DF1D846B6B352E853F05AC071E3D8139A069F06A4C03110B336C219FABA38B1D01D02915D0F6E"));
        dict["OE"] = new PdfString(Convert.FromHexString("AF9AA56136D718300C6A426B4255758E1A874FA0F477750A47D1B3417F71E155"));
        dict["UE"] = new PdfString(Convert.FromHexString("6398B1BB1F49A4E30C8FB9C7AF355243595B83295B963506860BDB315062E71E"));

        PdfEncryption? handler = PdfEncryption.TryCreate(dict, null, "secret123", null);
        Assert.NotNull(handler);
        Assert.True(handler!.UserPasswordVerified);
    }

    [Fact]
    public void WrongPassword_DoesNotVerify()
    {
        byte[] bytes = Convert.FromBase64String(PdfBase64);
        using PdfDocument document = PdfDocument.FromArray(bytes, "wrong-password");
        Assert.False(document.UserPasswordVerified);
    }
}
