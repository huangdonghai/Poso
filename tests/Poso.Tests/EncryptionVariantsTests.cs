using System;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>M1: AES-128 (V4) and RC4-128 (V3/R3) decryption, validated against PyMuPDF/MuPDF documents (user password "pw123").</summary>
public class EncryptionVariantsTests
{
    private const string Aes128Base64 = "JVBERi0xLjcKJcK1wrYKJSBXcml0dGVuIGJ5IE11UERGIDEuMjguMgoKMSAwIG9iago8PC9UeXBlL0NhdGFsb2cvUGFnZXMgMiAwIFIvSW5mbzw8L1Byb2R1Y2VyPEY3QUNFMjkwMDM1OUU4QjA0NzUwQUFDRENFMTJFMUU2NkY0MDRDOTZFMkY0OERBMDBEM0E4NDQyRkE2RDk0Mzk+Pj4+PgplbmRvYmoKCjIgMCBvYmoKPDwvVHlwZS9QYWdlcy9Db3VudCAxL0tpZHNbNCAwIFJdPj4KZW5kb2JqCgozIDAgb2JqCjw8L0ZvbnQ8PC9oZWx2IDUgMCBSPj4+PgplbmRvYmoKCjQgMCBvYmoKPDwvVHlwZS9QYWdlL01lZGlhQm94WzAgMCAyMDAgMTAwXS9Sb3RhdGUgMC9SZXNvdXJjZXMgMyAwIFIvUGFyZW50IDIgMCBSL0NvbnRlbnRzWzYgMCBSXT4+CmVuZG9iagoKNSAwIG9iago8PC9UeXBlL0ZvbnQvU3VidHlwZS9UeXBlMS9CYXNlRm9udC9IZWx2ZXRpY2EvRW5jb2RpbmcvV2luQW5zaUVuY29kaW5nPj4KZW5kb2JqCgo2IDAgb2JqCjw8L0xlbmd0aCA5Nj4+CnN0cmVhbQpX78rF3MbCjsDoeXeUG9aWsGMUZt8QHL6IjYar1pSTx/ubbBpAjgqxtMOM5IAxJdSKivR8zMHofZLnlj/nIRdGVQN2yLlDM017krQlOAKjNkCC5du1WAobpkIXf5jooycKZW5kc3RyZWFtCmVuZG9iagoKeHJlZgowIDcKMDAwMDAwMDAwMCA2NTUzNSBmIAowMDAwMDAwMDQyIDAwMDAwIG4gCjAwMDAwMDAxNzIgMDAwMDAgbiAKMDAwMDAwMDIyNCAwMDAwMCBuIAowMDAwMDAwMjY1IDAwMDAwIG4gCjAwMDAwMDAzNzIgMDAwMDAgbiAKMDAwMDAwMDQ2MSAwMDAwMCBuIAoKdHJhaWxlcgo8PC9TaXplIDcvUm9vdCAxIDAgUi9JRFs8QzJBQTRDQzJBNkMzQkRDMzhDM0VDMzg1QzM5QTc2QzM+PEJGMEQ2NDgxNEUwNTg1QTlGNjg5NzZDMERGRjU2N0NBPl0vRW5jcnlwdDw8L0ZpbHRlci9TdGFuZGFyZC9SIDQvViA0L0xlbmd0aCAxMjgvUCAtMzM4OC9FbmNyeXB0TWV0YWRhdGEgdHJ1ZS9TdG1GL1N0ZENGL1N0ckYvU3RkQ0YvQ0Y8PC9TdGRDRjw8L0F1dGhFdmVudC9Eb2NPcGVuL0NGTS9BRVNWMi9MZW5ndGggMTY+Pj4+L088MkY3MDg0NjVGRDdGRTZBNDBFNkQ0NTc5REFBMjA5Mzc3Njc0RTg4ODFEQkU5Qjc5MEE3QTREMEFCQjZFRDFEQz4vVTwwNDE4RjZFOUQ5RkI3QTU5QkJGNzVBMTExRTU1NzY0NDI4QkY0RTVFNEU3NThBNDE2NDAwNEU1NkZGRkEwMTA4Pj4+Pj4Kc3RhcnR4cmVmCjYwNgolJUVPRgo=";
    private const string Rc4128Base64 = "JVBERi0xLjcKJcK1wrYKJSBXcml0dGVuIGJ5IE11UERGIDEuMjguMgoKMSAwIG9iago8PC9UeXBlL0NhdGFsb2cvUGFnZXMgMiAwIFIvSW5mbzw8L1Byb2R1Y2VyPERFNENCQjY1ODhEMTMzREExRDkxNzc2OT4+Pj4+CmVuZG9iagoKMiAwIG9iago8PC9UeXBlL1BhZ2VzL0NvdW50IDEvS2lkc1s0IDAgUl0+PgplbmRvYmoKCjMgMCBvYmoKPDwvRm9udDw8L2hlbHYgNSAwIFI+Pj4+CmVuZG9iagoKNCAwIG9iago8PC9UeXBlL1BhZ2UvTWVkaWFCb3hbMCAwIDIwMCAxMDBdL1JvdGF0ZSAwL1Jlc291cmNlcyAzIDAgUi9QYXJlbnQgMiAwIFIvQ29udGVudHNbNiAwIFJdPj4KZW5kb2JqCgo1IDAgb2JqCjw8L1R5cGUvRm9udC9TdWJ0eXBlL1R5cGUxL0Jhc2VGb250L0hlbHZldGljYS9FbmNvZGluZy9XaW5BbnNpRW5jb2Rpbmc+PgplbmRvYmoKCjYgMCBvYmoKPDwvTGVuZ3RoIDY3Pj4Kc3RyZWFtCkSV/NOto1hR6L/cZpp+Yl3DEmdmaWomMZ+PTWAPWuaF9m51nOA9oxzrYLkzytiuDt0R3DbFrrX4hQN84hbvhvQe1AkKZW5kc3RyZWFtCmVuZG9iagoKeHJlZgowIDcKMDAwMDAwMDAwMCA2NTUzNSBmIAowMDAwMDAwMDQyIDAwMDAwIG4gCjAwMDAwMDAxMzIgMDAwMDAgbiAKMDAwMDAwMDE4NCAwMDAwMCBuIAowMDAwMDAwMjI1IDAwMDAwIG4gCjAwMDAwMDAzMzIgMDAwMDAgbiAKMDAwMDAwMDQyMSAwMDAwMCBuIAoKdHJhaWxlcgo8PC9TaXplIDcvUm9vdCAxIDAgUi9JRFs8QzI4NkMyQkE2N0MzOTU2NkMzQkFDMjgzMDM1NjQ3QzM+PEM5MTJBNDlDRDBDMDQwRjFBMTgxRjVFMUJDMDNEQzEzPl0vRW5jcnlwdDw8L0ZpbHRlci9TdGFuZGFyZC9SIDMvViAyL0xlbmd0aCAxMjgvUCAtMzM4OC9FbmNyeXB0TWV0YWRhdGEgdHJ1ZS9PPDJGNzA4NDY1RkQ3RkU2QTQwRTZENDU3OURBQTIwOTM3NzY3NEU4ODgxREJFOUI3OTBBN0E0RDBBQkI2RUQxREM+L1U8Qjc5NDU4QkJFOThEMjgzRjYyQTgwMUIwRjZCRDI5MDIyOEJGNEU1RTRFNzU4QTQxNjQwMDRFNTZGRkZBMDEwOD4+Pj4+CnN0YXJ0eHJlZgo1MzcKJSVFT0YK";

    [Theory]
    [InlineData(Aes128Base64)]
    [InlineData(Rc4128Base64)]
    public void OpensEncryptedPdf_AndRenders(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        using PdfDocument document = PdfDocument.FromArray(bytes, "pw123");
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

        Assert.True(ink > 50, $"decrypted content should render ink ({ink})");
    }
}
