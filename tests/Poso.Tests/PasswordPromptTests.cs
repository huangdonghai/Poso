using System;
using System.Threading.Tasks;
using Poso.Graphics;
using Poso.Pdf;
using Poso.Skia;
using Xunit;

namespace Poso.Tests;

/// <summary>
/// M1 integration: the password-prompt open helpers wire document decryption through a caller-supplied
/// callback (the shape a viewer UI uses), without any UI dependency. The encrypted sample is the same
/// authentic AES-256 (R6) PDF used by <see cref="Aes256Tests"/> (user password "secret123").
/// </summary>
public class PasswordPromptTests
{
    private const string Aes256Base64 = "JVBERi0xLjcKJcK1wrYKJSBXcml0dGVuIGJ5IE11UERGIDEuMjguMgoKMSAwIG9iago8PC9UeXBlL0NhdGFsb2cvUGFnZXMgMiAwIFIvSW5mbzw8L1Byb2R1Y2VyPERCNDYyMUNENzI5MjE1RDA3RDhCMTg2NTM2NzE3Mzk3QkM5MUNCRkQxRTY4REYxRkRFQjIwOTQwRDNGNEYyODM+Pj4+PgplbmRvYmoKCjIgMCBvYmoKPDwvVHlwZS9QYWdlcy9Db3VudCAxL0tpZHNbNCAwIFJdPj4KZW5kb2JqCgozIDAgb2JqCjw8L0ZvbnQ8PC9oZWx2IDUgMCBSPj4+PgplbmRvYmoKCjQgMCBvYmoKPDwvVHlwZS9QYWdlL01lZGlhQm94WzAgMCAyMDAgMTAwXS9Sb3RhdGUgMC9SZXNvdXJjZXMgMyAwIFIvUGFyZW50IDIgMCBSL0NvbnRlbnRzWzYgMCBSXT4+CmVuZG9iagoKNSAwIG9iago8PC9UeXBlL0ZvbnQvU3VidHlwZS9UeXBlMS9CYXNlRm9udC9IZWx2ZXRpY2EvRW5jb2RpbmcvV2luQW5zaUVuY29kaW5nPj4KZW5kb2JqCgo2IDAgb2JqCjw8L0xlbmd0aCA5Nj4+CnN0cmVhbQqxNyvuCV87ISwAc+hA6e3wrkMncv8tiOR701slWb1Z/3cGbQvlP+/hv2/0FUFJbV4bhfVX+pamCBYFeQ3Xm4SYgBH5Js5bLkl+JX8bWaXJdLnvooM8vxlylyIwkIkQG84KZW5kc3RyZWFtCmVuZG9iagoKeHJlZgowIDcKMDAwMDAwMDAwMCA2NTUzNSBmIAowMDAwMDAwMDQyIDAwMDAwIG4gCjAwMDAwMDAxNzIgMDAwMDAgbiAKMDAwMDAwMDIyNCAwMDAwMCBuIAowMDAwMDAwMjY1IDAwMDAwIG4gCjAwMDAwMDAzNzIgMDAwMDAgbiAKMDAwMDAwMDQ2MSAwMDAwMCBuIAoKdHJhaWxlcgo8PC9TaXplIDcvUm9vdCAxIDAgUi9JRFs8QzJCNEMzODVDM0I1NjlDM0JGQzJCRjdDQzI4RjUxQzM+PDZBQTU1MzBEMjBBNkRDN0U3MUIyQjU1RDgxMjYwREFGPl0vRW5jcnlwdDw8L0ZpbHRlci9TdGFuZGFyZC9SIDYvViA1L0xlbmd0aCAyNTYvUCAtMzM4OC9FbmNyeXB0TWV0YWRhdGEgdHJ1ZS9TdG1GL1N0ZENGL1N0ckYvU3RkQ0YvQ0Y8PC9TdGRDRjw8L0F1dGhFdmVudC9Eb2NPcGVuL0NGTS9BRVNWMy9MZW5ndGggMzI+Pj4+L088Njk2NDI5MDdERTc5MkFFNkVCMDdEMUVCMEEzOEI4RjEyQkMyQjRBMzE2RTQ4RjIxNzY5RkIzOUE0M0JCMDNBQkY0MkIxMkE4MjNFMDE2NDY5QzI5QjNGRURFQjMwMjc3Pi9VPEQ2Qjg4NjRFRjY5Qjc2MkMwRDdERjFEODQ2QjZCMzUyRTg1M0YwNUFDMDcxRTNEODEzOUEwNjlGMDZBNEMwMzExMEIzMzZDMjE5RkFCQTM4QjFEMDFEMDI5MTVEMEY2RT4vT0U8QUY5QUE1NjEzNkQ3MTgzMDBDNkE0MjZCNDI1NTc1OEUxQTg3NEZBMEY0Nzc3NTBBNDdEMUIzNDE3RjcxRTE1NT4vVUU8NjM5OEIxQkIxRjQ5QTRFMzBDOEZCOUM3QUYzNTUyNDM1OTVCODMyOTVCOTYzNTA2ODYwQkRCMzE1MDYyRTcxRT4vUGVybXM8RUQ4NzVCMUQ0N0Q4NkJCRTYxMjdDQTVFRDBGQzVFODE+Pj4+PgpzdGFydHhyZWYKNjA2CiUlRU9GCg==";

    private static byte[] EncryptedPdf() => Convert.FromBase64String(Aes256Base64);

    [Fact]
    public void EncryptedDocument_GivingUp_ThrowsCleanly()
    {
        int calls = 0;
        // The provider immediately declines (simulates the user cancelling the password dialog).
        Assert.Throws<PdfPasswordRejectedException>(() =>
            PdfDocument.FromArray(EncryptedPdf(), () => { calls++; return null; }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void EncryptedDocument_CorrectPasswordViaPrompt_OpensAndRenders()
    {
        using PdfDocument document = PdfDocument.FromArray(EncryptedPdf(), () => "secret123");

        Assert.True(document.IsEncrypted);
        Assert.True(document.UserPasswordVerified);
        Assert.False(document.NeedsPassword);

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

        Assert.True(ink > 50, $"password-unlocked document should render ink ({ink})");
    }

    [Fact]
    public void EncryptedDocument_WrongThenCorrectPassword_RetriesUntilUnlocked()
    {
        string[] responses = ["wrong-password", "secret123"];
        int index = 0;
        using PdfDocument document = PdfDocument.FromArray(EncryptedPdf(), () =>
        {
            string answer = responses[index];
            index++;
            return answer;
        });

        Assert.Equal(2, index); // one rejected attempt, then the accepted one
        Assert.True(document.UserPasswordVerified);
    }

    [Fact]
    public void UnencryptedDocument_NeverCallsPrompt()
    {
        byte[] bytes = PdfFixtures.CreateImageSoftMaskPdf();
        bool called = false;
        using PdfDocument document = PdfDocument.FromArray(bytes, () => { called = true; return "unused"; });

        Assert.False(called);
        Assert.False(document.NeedsPassword);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public async Task Async_CorrectPasswordViaPrompt_Opens()
    {
        int calls = 0;
        using PdfDocument document = await PdfDocument.FromArrayAsync(
            EncryptedPdf(),
            () => { calls++; return Task.FromResult<string?>("secret123"); });

        Assert.Equal(1, calls);
        Assert.True(document.UserPasswordVerified);
        Assert.False(document.NeedsPassword);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Async_UnencryptedDocument_NeverCallsPrompt()
    {
        byte[] bytes = PdfFixtures.CreateImageSoftMaskPdf();
        bool called = false;
        using PdfDocument document = await PdfDocument.FromArrayAsync(
            bytes,
            () => { called = true; return Task.FromResult<string?>("unused"); });

        Assert.False(called);
        Assert.Equal(1, document.PageCount);
        await Task.CompletedTask;
    }
}
