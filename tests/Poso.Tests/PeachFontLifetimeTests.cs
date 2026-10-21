using System;
using System.IO;
using System.Reflection;
using Poso.Graphics;
using Poso.PeachDrawing;
using Xunit;
using PeachText = PeachDrawing.Text;
using IOPath = System.IO.Path;

namespace Poso.Tests;

/// <summary>
/// PeachDrawing's FontSet has no removal API: everything registered through AddData lives as long as
/// the set does. When one set served the whole process, every embedded program of every document - its
/// bytes, its family record, its descriptor - stayed reachable forever. Embedded programs now load
/// through a private set per font, which the PeachFont itself keeps alive and drops with it, so the
/// shared set only ever reads the process-wide system families.
/// </summary>
public class PeachFontLifetimeTests
{
    [Fact]
    public void LoadingAnEmbeddedFontDoesNotRegisterIntoTheSharedSet()
    {
        int before = CustomFamilyCount(PeachFont.SharedFontSet);

        PeachFont? font = PeachFont.Create(File.ReadAllBytes(FontsFile("liberation-sans-regular.ttf")), "LiberationSans");
        Assert.NotNull(font);

        // The font works from its own set.
        Assert.NotEqual((ushort)0, font!.GetGlyphId('A'));
        Assert.True(font.GetAdvanceWidth(font.GetGlyphId('A')) > 0f);

        // And the shared set has gained nothing: the program lives only on the font.
        Assert.Equal(before, CustomFamilyCount(PeachFont.SharedFontSet));

        font.Dispose();
        Assert.Equal(before, CustomFamilyCount(PeachFont.SharedFontSet));
    }

    [Fact]
    public void RepeatedEmbeddedLoadsLeaveTheSharedSetEmpty()
    {
        // Twenty documents' worth of the same program: before the per-font sets, this registered twenty
        // families into the shared set and kept all twenty alive.
        for (int i = 0; i < 20; i++)
        {
            PeachFont? font = PeachFont.Create(File.ReadAllBytes(FontsFile("liberation-sans-regular.ttf")), "LiberationSans");
            Assert.NotNull(font);
            font!.Dispose();
        }

        Assert.Equal(0, CustomFamilyCount(PeachFont.SharedFontSet));
    }

    /// <summary>
    /// The number of families registered through AddData on a set. Read through PeachDrawing's
    /// internals because the public surface exposes only the process-wide installed list; a renamed
    /// internal makes this test fail loudly rather than silently pass.
    /// </summary>
    private static int CustomFamilyCount(PeachText.FontSet set)
    {
        FieldInfo resolverField = typeof(PeachText.FontSet)
            .GetField("<Resolver>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PeachDrawing renamed its FontSet resolver");
        object resolver = resolverField.GetValue(set)
            ?? throw new InvalidOperationException("the FontSet has no resolver");
        FieldInfo names = resolver.GetType()
            .GetField("_customFamilyNames", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PeachDrawing renamed the FontSet custom-family table");
        object collection = names.GetValue(resolver)
            ?? throw new InvalidOperationException("the custom-family table is null");
        return (int)(collection.GetType().GetProperty("Count")?.GetValue(collection)
            ?? throw new InvalidOperationException("the custom-family table has no Count"));
    }

    private static string FontsFile(string name) =>
        IOPath.Combine(AppContext.BaseDirectory, "Samples", "Fonts", name);
}
