using LanMessenger.Core.Persistence;
using LanMessenger.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanMessenger.Tests;

// The suite runs as the same user as the installed app, so a path that
// resolves to %APPDATA% is the live app's file. Both the history file and
// client.log were written by the test host before every persistence singleton
// asked TestIsolation where to go. Mirrors TestIsolationTests.swift.
[TestClass]
public sealed class TestIsolationTests
{
    private static readonly string RealAppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanMessenger");
    private static readonly string Temp = Path.GetTempPath();

    private static void AssertScratch(string path)
    {
        StringAssert.StartsWith(path, Temp, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(path.StartsWith(RealAppDir, StringComparison.OrdinalIgnoreCase), path);
    }

    [TestMethod]
    public void TheSuiteIsRecognisedAsATestRun() =>
        Assert.IsTrue(TestIsolation.IsActive);

    /// <summary>
    /// Only the logger's own tests set the override; everything else — every
    /// MessagingService and remote-desktop test — logs through the default.
    /// </summary>
    [TestMethod]
    public void WithNoOverrideTheLoggerWritesToScratch()
    {
        var saved = LanLogger.TestLogDirectoryOverride;
        LanLogger.TestLogDirectoryOverride = null;
        try
        {
            AssertScratch(LanLogger.LogsDirectory);
            foreach (var channel in Enum.GetValues<LanLogger.LogChannel>())
                AssertScratch(LanLogger.LogPathFor(channel));

            var marker = $"isolation-{Guid.NewGuid():N}";
            LanLogger.Info("TestIsolation", marker);
            var written = File.ReadAllText(
                Path.Combine(TestIsolation.ScratchPath("logs"), "client.log"));
            StringAssert.Contains(written, marker);
        }
        finally
        {
            LanLogger.TestLogDirectoryOverride = saved;
        }
    }

    [TestMethod]
    public void ConfigAndTheFilesFiledUnderItLiveInScratch()
    {
        var store = ConfigStore.Shared;
        AssertScratch(store.AppDataDirectory);
        AssertScratch(store.HistoryFilePath);
        AssertScratch(store.InboxDirectory);
        AssertScratch(store.UpdateStagingDirectory);
        AssertScratch(store.LogsDirectory);
    }
}
