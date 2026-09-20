using LanMessenger.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// Guards the in-app changelog: an update that skips versions has to show the
// notes for every version it skips, not just the newest one. See
// UpdateService.MergedReleaseNotes. Mirrors UpdateNotesTests.swift.
[TestClass]
public class UpdateNotesTests
{
    // A per-platform release body as CI writes it: categorized sections, then
    // a "---" rule and the artifact table that belongs on the release page.
    private static string PlatformBody(string line) =>
        "### 🐛 Bug Fixes\n"
        + "\n"
        + $"- {line}\n"
        + "\n"
        + "---\n"
        + "\n"
        + "**Windows v9.9.9** — platform pre-release\n"
        + "\n"
        + "| Format | Use case |\n"
        + "|--------|----------|\n"
        + "| `.exe` | Standard install |";

    // ── Stripping ───────────────────────────────────────────────────────────

    [TestMethod]
    public void StripsEverythingFromTheHorizontalRule()
    {
        Assert.AreEqual(
            "### 🐛 Bug Fixes\n\n- Fixed the thing",
            UpdateService.StripReleasePageSections(PlatformBody("Fixed the thing")));
    }

    [TestMethod]
    public void StripsDownloadsAndInstallHeadings()
    {
        var body = "### ✨ New Features\n\n- Something new\n\n## Downloads\n\n| Platform | Installer |";
        Assert.AreEqual(
            "### ✨ New Features\n\n- Something new",
            UpdateService.StripReleasePageSections(body));
    }

    // The combined release opens with "## What's New"; the per-platform one
    // does not. Dropping it keeps both shapes rendering identically under a
    // version heading.
    [TestMethod]
    public void DropsLeadingWhatsNewHeading()
    {
        Assert.AreEqual(
            "### 🐛 Bug Fixes\n\n- Fixed it",
            UpdateService.StripReleasePageSections("## What's New\n\n### 🐛 Bug Fixes\n\n- Fixed it"));
    }

    [TestMethod]
    public void KeepsWhatsNewHeadingThatIsNotLeading()
    {
        StringAssert.Contains(
            UpdateService.StripReleasePageSections("Intro line\n\n## What's New\n\n- Fixed it"),
            "## What's New");
    }

    [TestMethod]
    public void HandlesCarriageReturns()
    {
        Assert.AreEqual(
            "### 🐛 Bug Fixes\n\n- Fixed it",
            UpdateService.StripReleasePageSections("### 🐛 Bug Fixes\r\n\r\n- Fixed it\r\n\r\n---\r\n\r\n## Downloads"));
    }

    // ── Merging ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void MergesEveryVersionBetweenInstalledAndOffered()
    {
        var releases = new (string, string)[]
        {
            ("1.1.3", PlatformBody("Newest fix")),
            ("1.1.2", PlatformBody("Middle fix")),
            ("1.1.1", PlatformBody("Already installed")),
        };
        var notes = UpdateService.MergedReleaseNotes(releases, "1.1.1", "1.1.3");

        StringAssert.Contains(notes, "## Version 1.1.3");
        StringAssert.Contains(notes, "Newest fix");
        StringAssert.Contains(notes, "## Version 1.1.2");
        StringAssert.Contains(notes, "Middle fix");
        // The version the user is already running must not be listed.
        Assert.IsFalse(notes.Contains("## Version 1.1.1"));
        Assert.IsFalse(notes.Contains("Already installed"));
        // Newest first.
        Assert.IsTrue(notes.IndexOf("## Version 1.1.3", StringComparison.Ordinal)
                      < notes.IndexOf("## Version 1.1.2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SingleHopHasNoVersionHeading()
    {
        var releases = new (string, string)[] { ("1.1.2", PlatformBody("Only fix")) };
        var notes = UpdateService.MergedReleaseNotes(releases, "1.1.1", "1.1.2");
        Assert.IsFalse(notes.Contains("## Version"));
        Assert.AreEqual("### 🐛 Bug Fixes\n\n- Only fix", notes);
    }

    // A release newer than the asset we are actually offering (a combined
    // release whose installer has not been published yet) must not advertise
    // changes the download does not contain.
    [TestMethod]
    public void IgnoresVersionsNewerThanTheOfferedBuild()
    {
        var releases = new (string, string)[]
        {
            ("1.1.4", PlatformBody("Not in this download")),
            ("1.1.3", PlatformBody("Newest fix")),
        };
        var notes = UpdateService.MergedReleaseNotes(releases, "1.1.1", "1.1.3");
        Assert.IsFalse(notes.Contains("Not in this download"));
        StringAssert.Contains(notes, "Newest fix");
    }

    // The same build appears twice — once as windows-vX.Y.Z, once inside the
    // combined release — and must produce one section, not two.
    [TestMethod]
    public void DeduplicatesRepeatedVersions()
    {
        var releases = new (string, string)[]
        {
            ("1.1.2", "## What's New\n\n### 🐛 Bug Fixes\n\n- Combined copy"),
            ("1.1.2", PlatformBody("Platform copy")),
        };
        var notes = UpdateService.MergedReleaseNotes(releases, "1.1.1", "1.1.2");
        StringAssert.Contains(notes, "Combined copy");
        Assert.IsFalse(notes.Contains("Platform copy"));
    }

    // An empty duplicate must not shadow the copy that still has content.
    [TestMethod]
    public void EmptyDuplicateLosesToTheOneWithContent()
    {
        var releases = new (string, string)[]
        {
            ("1.1.2", "---\n\n## Downloads\n\n| Platform |"),
            ("1.1.2", PlatformBody("Platform copy")),
        };
        var notes = UpdateService.MergedReleaseNotes(releases, "1.1.1", "1.1.2");
        StringAssert.Contains(notes, "Platform copy");
    }

    [TestMethod]
    public void UnparseableTagsAreSkipped()
    {
        var releases = new (string, string)[]
        {
            ("", PlatformBody("No version")),
            ("1.1.2", PlatformBody("Real fix")),
        };
        var notes = UpdateService.MergedReleaseNotes(releases, "1.1.1", "1.1.2");
        Assert.IsFalse(notes.Contains("No version"));
        StringAssert.Contains(notes, "Real fix");
    }

    // The two platforms version independently, so a macOS-only tag must not be
    // read as a Windows version — its changelog would otherwise show up in the
    // Windows update panel under a version Windows never had.
    [TestMethod]
    public void TagVersionsAreAttributedToTheRightPlatform()
    {
        Assert.AreEqual("1.8.4", UpdateService.ExtractVersion("windows-v1.8.4"));
        Assert.AreEqual("1.7.2", UpdateService.ExtractVersion("release-win1.7.2-mac1.8.4"));
        Assert.AreEqual("",      UpdateService.ExtractVersion("macos-v1.9.0"));
        // A tag naming neither platform still falls back to its first number.
        Assert.AreEqual("2.0.1", UpdateService.ExtractVersion("v2.0.1"));
    }

    [TestMethod]
    public void ForeignPlatformReleasesAreLeftOutOfTheMerge()
    {
        var releases = new (string, string)[]
        {
            (UpdateService.ExtractVersion("macos-v1.9.0"), PlatformBody("Mac-only change")),
            (UpdateService.ExtractVersion("windows-v1.1.2"), PlatformBody("Windows fix")),
        };
        var notes = UpdateService.MergedReleaseNotes(releases, "1.1.1", "1.1.2");
        Assert.IsFalse(notes.Contains("Mac-only change"));
        StringAssert.Contains(notes, "Windows fix");
    }

    [TestMethod]
    public void NoNewerReleasesYieldsEmptyNotes()
    {
        var releases = new (string, string)[] { ("1.1.1", PlatformBody("Installed")) };
        Assert.AreEqual("", UpdateService.MergedReleaseNotes(releases, "1.1.1", "1.1.1"));
    }

    // Version ordering must be numeric, not lexicographic: 1.1.10 is newer
    // than 1.1.9, and both belong in a merge that starts at 1.1.8.
    [TestMethod]
    public void OrdersNumericallyNotLexicographically()
    {
        var releases = new (string, string)[]
        {
            ("1.1.9",  PlatformBody("Nine")),
            ("1.1.10", PlatformBody("Ten")),
        };
        var notes = UpdateService.MergedReleaseNotes(releases, "1.1.8", "1.1.10");
        Assert.IsTrue(notes.IndexOf("## Version 1.1.10", StringComparison.Ordinal)
                      < notes.IndexOf("## Version 1.1.9", StringComparison.Ordinal));
    }
}
