using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace LanMessenger.Tests;

/// <summary>
/// The HID usage to scan code table.
///
/// A table like this fails in the worst possible way: one wrong entry means one
/// key types the wrong character, on someone else's machine, and nobody notices
/// until they try that key. It cannot be verified by using it — you would have to
/// press every key on a remote host — so the properties that must hold are
/// asserted directly instead.
/// </summary>
[TestClass]
public class HidKeyMapTests
{
    [TestMethod]
    public void EveryLetterAndDigitIsMapped()
    {
        for (ushort usage = 0x04; usage <= 0x27; usage++)
        {
            Assert.IsNotNull(HidKeyMap.ScanCode(usage),
                $"HID usage 0x{usage:X2} (a letter or digit) has no scan code");
        }
    }

    [TestMethod]
    public void NoTwoKeysShareAScanCodeAndExtendedFlag()
    {
        // The single most likely defect: a copied line left pointing at the
        // previous key's code. Two keys with the same (code, extended) pair are
        // indistinguishable to Windows, so one of them types the other.
        //
        // The keypad is the deliberate exception — keypad 1-9, 0 and decimal
        // share codes with the navigation block, and on a real keyboard Num Lock
        // is what tells them apart.
        var keypad = new HashSet<ushort> { 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
                                           0x60, 0x61, 0x62, 0x63, 0x55 };

        var seen = new Dictionary<(ushort, bool), ushort>();
        for (ushort usage = 0; usage < 0x100; usage++)
        {
            if (keypad.Contains(usage)) continue;
            if (HidKeyMap.ScanCode(usage) is not { } mapped) continue;

            var key = (mapped.ScanCode, mapped.Extended);
            Assert.IsFalse(seen.ContainsKey(key),
                $"HID 0x{usage:X2} and 0x{seen.GetValueOrDefault(key):X2} both map to "
                + $"scan 0x{mapped.ScanCode:X2} extended={mapped.Extended}");
            seen[key] = usage;
        }
    }

    [TestMethod]
    public void TheArrowsAndNavigationBlockAreExtended()
    {
        // The grey block carries a 0xE0 prefix on a real keyboard. Without the
        // extended flag these become their keypad twins, so Left arrow types 4.
        foreach (ushort usage in new ushort[] { 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E,
                                                0x4F, 0x50, 0x51, 0x52 })
        {
            var mapped = HidKeyMap.ScanCode(usage);
            Assert.IsNotNull(mapped, $"HID 0x{usage:X2} is unmapped");
            Assert.IsTrue(mapped!.Value.Extended, $"HID 0x{usage:X2} should be extended");
        }
    }

    [TestMethod]
    public void RightHandModifiersAreDistinctFromLeftHandOnes()
    {
        // Right Alt is AltGr on most European layouts. Mapping it to Left Alt
        // would break every accented character on those keyboards while looking
        // perfectly fine on a US one.
        var leftAlt = HidKeyMap.ScanCode(0xE2)!.Value;
        var rightAlt = HidKeyMap.ScanCode(0xE6)!.Value;
        Assert.AreEqual(leftAlt.ScanCode, rightAlt.ScanCode, "both are the alt scan code");
        Assert.IsFalse(leftAlt.Extended);
        Assert.IsTrue(rightAlt.Extended, "AltGr is the extended one");

        var leftControl = HidKeyMap.ScanCode(0xE0)!.Value;
        var rightControl = HidKeyMap.ScanCode(0xE4)!.Value;
        Assert.IsFalse(leftControl.Extended);
        Assert.IsTrue(rightControl.Extended);
    }

    [TestMethod]
    public void EveryModifierUsageIsMappable()
    {
        // ReleaseEverything walks this list at teardown. An entry with no scan
        // code would be a modifier that can be pressed and never lifted.
        foreach (ushort usage in HidKeyMap.ModifierUsages)
        {
            Assert.IsNotNull(HidKeyMap.ScanCode(usage),
                $"modifier HID 0x{usage:X2} cannot be released");
        }
    }

    [TestMethod]
    public void PauseIsDeliberatelyUnmapped()
    {
        // Its make code is the three-byte 0xE1 0x1D 0x45, which SendInput cannot
        // express as one scan code. A wrong guess sends a stray Ctrl, and a host
        // that cannot type Pause is a smaller problem than one that silently
        // presses Control.
        Assert.IsNull(HidKeyMap.ScanCode(0x48));
    }

    [TestMethod]
    public void F11AndF12BreakTheFunctionKeyRun()
    {
        // The trap this test exists for. F1-F10 are contiguous at 0x3B-0x44, but
        // F11 and F12 are not: they were added after the original 83-key layout
        // had already taken the codes that would have followed, so they sit at
        // 0x57 and 0x58. Running the sequence through them lands on Num Lock and
        // Scroll Lock — the first version of this table did exactly that, and
        // the duplicate-scan-code test above is what caught it.
        Assert.AreEqual((ushort)0x3B, HidKeyMap.ScanCode(0x3A)!.Value.ScanCode);  // F1
        Assert.AreEqual((ushort)0x44, HidKeyMap.ScanCode(0x43)!.Value.ScanCode);  // F10
        Assert.AreEqual((ushort)0x57, HidKeyMap.ScanCode(0x44)!.Value.ScanCode);  // F11
        Assert.AreEqual((ushort)0x58, HidKeyMap.ScanCode(0x45)!.Value.ScanCode);  // F12

        Assert.AreEqual((ushort)0x45, HidKeyMap.ScanCode(0x53)!.Value.ScanCode,
            "Num Lock must still own 0x45");
        Assert.AreEqual((ushort)0x46, HidKeyMap.ScanCode(0x47)!.Value.ScanCode,
            "Scroll Lock must still own 0x46");

        Assert.IsNull(HidKeyMap.ScanCode(0x68), "F13 is not in this table");
    }

    [TestMethod]
    public void TheInverseTableAgreesWithTheForwardOne()
    {
        // The viewer inverts this table rather than repeating it, so the two
        // cannot drift. This asserts the inversion is total for everything the
        // keypad does not deliberately alias: every key a host can press is one
        // a viewer can send.
        var keypadAliases = new HashSet<ushort> { 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E,
                                                  0x5F, 0x60, 0x61, 0x62, 0x63, 0x55 };
        for (ushort usage = 0; usage < 0x100; usage++)
        {
            if (keypadAliases.Contains(usage)) continue;
            if (HidKeyMap.ScanCode(usage) is not { } mapped) continue;

            Assert.AreEqual(usage,
                HidKeyMap.UsageForScanCode(mapped.ScanCode, mapped.Extended),
                $"HID 0x{usage:X2} did not survive the round trip");
        }
    }

    [TestMethod]
    public void TheExtendedFlagSeparatesTheKeypadFromTheNavigationBlock()
    {
        // Keypad 7 and Home share scan code 0x47; only the extended flag tells
        // them apart. Inverting on the code alone would make the arrow keys type
        // digits — the single most visible way this could go wrong.
        Assert.AreEqual((ushort)0x4A, HidKeyMap.UsageForScanCode(0x47, extended: true));   // Home
        Assert.AreEqual((ushort)0x5F, HidKeyMap.UsageForScanCode(0x47, extended: false));  // Keypad 7
    }

    [TestMethod]
    public void AnUnknownScanCodeProducesNoUsage()
    {
        // Returning a usage for an unmapped code would send a keystroke nobody
        // pressed.
        Assert.IsNull(HidKeyMap.UsageForScanCode(0xFFFF, extended: false));
    }

    [TestMethod]
    public void AnUnknownUsageIsNullRatherThanZero()
    {
        // Zero is a valid scan code, so returning it for "I do not know this key"
        // would inject a real keystroke for every unrecognised usage.
        Assert.IsNull(HidKeyMap.ScanCode(0xFFFF));
        Assert.IsNull(HidKeyMap.ScanCode(0x00));
    }

    [TestMethod]
    public void LettersMapToTheQwertyPositionsNotAlphabeticalOrder()
    {
        // HID orders letters A-Z; the keyboard does not. Q is the top-left key
        // (0x10) and A is the home-row-left key (0x1E) — if these come out
        // alphabetical, the table was generated by counting rather than by
        // position, and every letter is wrong.
        Assert.AreEqual((ushort)0x1E, HidKeyMap.ScanCode(0x04)!.Value.ScanCode);  // A
        Assert.AreEqual((ushort)0x10, HidKeyMap.ScanCode(0x14)!.Value.ScanCode);  // Q
        Assert.AreEqual((ushort)0x2C, HidKeyMap.ScanCode(0x1D)!.Value.ScanCode);  // Z
        Assert.AreEqual((ushort)0x15, HidKeyMap.ScanCode(0x1C)!.Value.ScanCode);  // Y
    }
}
