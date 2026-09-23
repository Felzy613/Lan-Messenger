using LanMessenger.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace LanMessenger.Tests;

// Guards the avatar colour each contact gets. The same name has to get the same
// colour on a PC and a Mac, launch after launch, and nothing fails loudly when
// it does not. avatar_palette_vector.json carries the expected values into the
// Swift suite, which asserts the same file. Its numbers come from the FNV-1a
// definition, not from either implementation. Mirror of AvatarPaletteTests.swift.
[TestClass]
public class AvatarPaletteTests
{
    private static JsonElement Vector()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "avatar_palette_vector.json");
        if (!File.Exists(path)) path = "avatar_palette_vector.json";
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }

    private static List<(string Name, uint Fnv1a, int Index)> Cases() =>
        Vector().GetProperty("cases").EnumerateArray()
            .Select(c => (c.GetProperty("name").GetString()!,
                          c.GetProperty("fnv1a").GetUInt32(),
                          c.GetProperty("index").GetInt32()))
            .ToList();

    [TestMethod]
    public void ThePaletteIsTheSharedEightInOrder()
    {
        var expected = Vector().GetProperty("palette").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        CollectionAssert.AreEqual(expected, AvatarPalette.Rgb.Select(rgb => $"#{rgb:x6}").ToList());
    }

    [TestMethod]
    public void EveryNameHashesToTheSharedValue()
    {
        foreach (var (name, fnv1a, _) in Cases())
            Assert.AreEqual(fnv1a, AvatarPalette.Fnv1a(name), name);
    }

    [TestMethod]
    public void EveryNameGetsTheSharedIndex()
    {
        var cases = Cases();
        foreach (var (name, _, index) in cases)
            Assert.AreEqual(index, AvatarPalette.Index(name), name);

        // A vector trimmed to a couple of names would still pass while pinning
        // almost nothing.
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 8).ToList(),
            cases.Select(c => c.Index).Distinct().ToList(), "the vector should reach every slot");
    }
}
