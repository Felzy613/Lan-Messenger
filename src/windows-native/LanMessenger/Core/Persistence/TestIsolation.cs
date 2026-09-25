namespace LanMessenger.Core.Persistence;

/// <summary>
/// Keeps a test run out of the user's real files.
///
/// The suite exercises the app's singletons directly, as the same user as the
/// real app, so anything that resolves a path under %APPDATA% reads and writes
/// the live app's files unless it asks here first. Both times that was left to
/// chance it bit: on macOS a thread under <c>192.168.99.77</c>, dated 1970, sat
/// in a real history file until the user hid it; and a <c>dotnet vstest</c> run
/// on the Dell appended a <c># Session ... app=2.1.1.0</c> header and its own
/// lines to the running app's <c>client.log</c> — the file a user attaches to a
/// bug report. MSTest is never loaded by the app, so its presence is an
/// unambiguous signal.
///
/// Every persistence singleton — <see cref="HistoryStore"/>,
/// <see cref="ConfigStore"/>, <c>LanLogger</c> — resolves its location through
/// this. A new one must too.
/// </summary>
public static class TestIsolation
{
    public static bool IsActive { get; } = AppDomain.CurrentDomain.GetAssemblies()
        .Any(a => a.GetName().Name == "Microsoft.VisualStudio.TestPlatform.TestFramework");

    /// <summary>
    /// A per-process path in the temp directory:
    /// <c>lanmessenger-tests-&lt;pid&gt;-&lt;name&gt;</c>. Per process so two
    /// concurrent runs never share a history file or interleave a log.
    /// </summary>
    public static string ScratchPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"lanmessenger-tests-{Environment.ProcessId}-{name}");
}
