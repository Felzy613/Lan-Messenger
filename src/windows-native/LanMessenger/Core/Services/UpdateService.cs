using LanMessenger.Core.Persistence;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LanMessenger.Core.Services;

// Represents an available update found on GitHub Releases.
// Sha256Url is null for releases that pre-date SHA256 sidecar publishing.
public sealed record UpdateInfo(string Version, string Notes, Uri DownloadUrl, Uri? Sha256Url, long ExpectedSize);

public enum UpdateProgressState { Idle, Downloading, Verifying, Installing, Failed }

public sealed record UpdateProgress(UpdateProgressState State, double Fraction = 0, string? Message = null);

// Fetches updates from GitHub Releases, picks the Windows installer asset,
// downloads and verifies it (SHA256 when a .sha256 sidecar is present), then
// spawns the Inno Setup installer in silent mode and exits.
//
// Release layout (matching .github/workflows/release.yml):
//   - Combined tag:      release-winX.Y.Z-macA.B.C   ← preferred
//   - Per-platform tag:  windows-vX.Y.Z               ← fallback
//   - Asset:             LanMessenger-Setup-X.Y.Z.exe
//   - Sidecar:           LanMessenger-Setup-X.Y.Z.exe.sha256  (optional)
public sealed class UpdateService
{
    public static UpdateService Shared { get; } = new();

    private static readonly HttpClient _http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("LanMessenger-Windows", CurrentStaticVersion));
        c.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _logPath;

    private UpdateService()
    {
        try { Directory.CreateDirectory(ConfigStore.Shared.LogsDirectory); } catch { }
        _logPath = Path.Combine(ConfigStore.Shared.LogsDirectory, "update.log");
    }

    public string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static string CurrentStaticVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    // MARK: - Check

    public async Task<UpdateInfo?> CheckAsync(string repo, CancellationToken ct = default)
    {
        repo = repo?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(repo)) { Log("CheckAsync: empty repo"); return null; }

        // per_page=100 rather than GitHub's default 30: every version ships as
        // up to three releases (macos-v, windows-v, combined), so 30 is only
        // ~10 versions of history — not enough to merge the changelog for
        // someone a long way behind.
        var url = $"https://api.github.com/repos/{repo}/releases?per_page=100";
        Log($"Checking {url}");
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"HTTP {(int)resp.StatusCode} from GitHub");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { Log("Unexpected JSON shape"); return null; }

            var picked = PickLatestWindows(doc.RootElement);
            if (picked is null) { Log("No Windows release found"); return null; }

            if (CompareVersions(picked.Version, CurrentVersion) > 0)
            {
                Log($"Update available: {picked.Version} (we're on {CurrentVersion})");
                return picked;
            }
            Log($"Already on latest ({CurrentVersion} >= {picked.Version})");
            return null;
        }
        catch (Exception ex) { Log($"Check failed: {ex.Message}"); return null; }
    }

    // MARK: - Download + verify + install

    public async Task<bool> DownloadAndInstallAsync(
        UpdateInfo info,
        Action<UpdateProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            Log("Refusing concurrent install");
            onProgress?.Invoke(new(UpdateProgressState.Failed, 0, "Update already running"));
            return false;
        }
        try
        {
            var stagingDir = ConfigStore.Shared.UpdateStagingDirectory;
            Directory.CreateDirectory(stagingDir);

            // Single-process install lock.
            var lockPath = Path.Combine(stagingDir, "install.lock");
            FileStream? lockFile = null;
            try { lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch
            {
                Log("Install lock held by another process");
                onProgress?.Invoke(new(UpdateProgressState.Failed, 0, "Another instance is updating"));
                return false;
            }

            try
            {
                var fileName = Path.GetFileName(info.DownloadUrl.AbsolutePath);
                if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    fileName = $"LanMessenger-Setup-{info.Version}.exe";
                var setupPath = Path.Combine(stagingDir, fileName);
                if (File.Exists(setupPath)) File.Delete(setupPath);

                // Step 1: fetch the expected SHA256 (if sidecar was published)
                string? expectedSHA256 = null;
                if (info.Sha256Url is not null)
                {
                    Log($"Fetching SHA256 sidecar: {info.Sha256Url}");
                    expectedSHA256 = await FetchSHA256SidecarAsync(info.Sha256Url, ct).ConfigureAwait(false);
                    if (expectedSHA256 is not null)
                        Log($"Expected SHA256: {expectedSHA256}");
                    else
                        Log("SHA256 sidecar unavailable — integrity check will use size only");
                }

                // Step 2: download
                onProgress?.Invoke(new(UpdateProgressState.Downloading, 0));
                Log($"Downloading {info.DownloadUrl} → {setupPath}");
                await DownloadAsync(info.DownloadUrl, setupPath, info.ExpectedSize, p =>
                    onProgress?.Invoke(new(UpdateProgressState.Downloading, p * 0.9)), ct).ConfigureAwait(false);

                // Step 3: verify size
                var actualSize = new FileInfo(setupPath).Length;
                if (actualSize < 512 * 1024)
                {
                    Log($"Downloaded file too small ({actualSize} bytes) — refusing to install");
                    onProgress?.Invoke(new(UpdateProgressState.Failed, 1, "Downloaded file looks corrupt"));
                    return false;
                }
                if (info.ExpectedSize > 0 && Math.Abs(actualSize - info.ExpectedSize) > 64 * 1024)
                {
                    Log($"Size mismatch: expected {info.ExpectedSize}, got {actualSize}");
                    onProgress?.Invoke(new(UpdateProgressState.Failed, 1, "Size mismatch — refusing to install"));
                    return false;
                }
                Log($"Download complete ({actualSize} bytes)");

                // Step 4: verify SHA256 (if available)
                onProgress?.Invoke(new(UpdateProgressState.Verifying, 0.9));
                if (expectedSHA256 is not null)
                {
                    Log("Verifying SHA256…");
                    var actualSHA256 = await ComputeSHA256HexAsync(setupPath, ct).ConfigureAwait(false);
                    Log($"Actual SHA256:   {actualSHA256}");
                    if (!string.Equals(actualSHA256, expectedSHA256, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("SHA256 mismatch — refusing to install");
                        onProgress?.Invoke(new(UpdateProgressState.Failed, 1, "Integrity check failed — download may be corrupt"));
                        return false;
                    }
                    Log("SHA256 verified ✓");
                }

                // Step 5: launch installer
                onProgress?.Invoke(new(UpdateProgressState.Installing, 1));
                Log("Launching installer in silent mode");

                // Kill all other instances of this app so the installer isn't blocked
                // waiting for files to be unlocked.
                var currentPid = Environment.ProcessId;
                foreach (var proc in Process.GetProcessesByName("LanMessenger"))
                {
                    if (proc.Id == currentPid) continue;
                    try { proc.Kill(entireProcessTree: true); proc.WaitForExit(2000); } catch { }
                }

                // Remove Zone.Identifier so ShellExecute elevation proceeds cleanly.
                try { File.Delete(setupPath + ":Zone.Identifier"); } catch { }

                var psi = new ProcessStartInfo(setupPath)
                {
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                    UseShellExecute = true,
                    Verb = "runas",
                };
                try
                {
                    Process.Start(psi);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    Log("UAC prompt declined by user");
                    onProgress?.Invoke(new(UpdateProgressState.Failed, 1, "Update canceled"));
                    return false;
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    // Elevation handoff succeeded; installer starts after UAC approval.
                    Log($"ShellExecute elevation handoff (error 5); scheduling exit");
                }

                Log("Installer spawned — force-exiting so the installer can replace files");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    // Environment.Exit is synchronous and kills the process immediately,
                    // unlike Application.Current.Exit() which queues an async WinUI shutdown
                    // and can leave file handles open long enough to block the installer.
                    Environment.Exit(0);
                });
                return true;
            }
            finally { lockFile?.Dispose(); try { File.Delete(lockPath); } catch { } }
        }
        catch (Exception ex)
        {
            Log($"Install failed: {ex}");
            onProgress?.Invoke(new(UpdateProgressState.Failed, 0, ex.Message));
            return false;
        }
        finally { _gate.Release(); }
    }

    // MARK: - Release notes

    /// Strips the parts of a GitHub release body that belong on the release
    /// page rather than in an in-app changelog: everything from the first
    /// "---" rule or a "## Downloads" / "## Install" heading, plus a leading
    /// "## What's New" heading (combined releases carry one, per-platform
    /// pre-releases do not, and the panel already names the version).
    public static string StripReleasePageSections(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var lines  = raw.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var result = new List<string>();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t == "---" || t.StartsWith("## Downloads", StringComparison.Ordinal)
                           || t.StartsWith("## Install", StringComparison.Ordinal))
                break;
            if (result.Count == 0)
            {
                if (t.Length == 0) continue;
                if (t.StartsWith("## What's New", StringComparison.OrdinalIgnoreCase)) continue;
            }
            result.Add(line);
        }
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            result.RemoveAt(result.Count - 1);
        return string.Join("\n", result);
    }

    /// Changelog for an update, covering *every* release between the running
    /// version and the one being offered — newest first, each under its own
    /// "## Version X.Y.Z" heading.
    ///
    /// Skipping releases is the ordinary case: anyone who has not opened the
    /// app for a week is several versions behind. Showing only the newest
    /// release's body hid every change made in between, so "what am I about to
    /// install" was only ever answered for the last hop.
    ///
    /// The input may list the same version twice — a per-platform pre-release
    /// and the combined release carry the same build — so versions are
    /// de-duplicated and the caller's ordering decides which body wins. A
    /// release whose body is empty after stripping loses to the duplicate that
    /// still has content.
    public static string MergedReleaseNotes(
        IEnumerable<(string Version, string Body)> releases,
        string currentVersion,
        string targetVersion)
    {
        // Stable version-descending order; the original index is the tiebreak
        // so callers keep control over which duplicate wins.
        var candidates = releases
            .Select((r, i) => (r.Version, r.Body, Index: i))
            .Where(r => !string.IsNullOrEmpty(r.Version)
                        && CompareVersions(r.Version, currentVersion) > 0
                        && CompareVersions(r.Version, targetVersion) <= 0)
            .OrderBy(r => r, Comparer<(string Version, string Body, int Index)>.Create(
                (a, b) =>
                {
                    var cmp = CompareVersions(b.Version, a.Version); // newest first
                    return cmp != 0 ? cmp : a.Index - b.Index;       // then input order
                }))
            .ToList();

        var sections = new List<(string Version, string Body)>();
        var seen     = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (seen.Contains(candidate.Version)) continue;
            var body = StripReleasePageSections(candidate.Body);
            if (string.IsNullOrWhiteSpace(body)) continue;
            seen.Add(candidate.Version);
            sections.Add((candidate.Version, body));
        }

        // One hop reads better without a heading repeating what the panel
        // already says; two or more need to be told apart.
        if (sections.Count == 0) return "";
        if (sections.Count == 1) return sections[0].Body;
        return string.Join("\n\n",
            sections.Select(s => $"## Version {s.Version}\n\n{s.Body}"));
    }

    // MARK: - Helpers

    // Finds the best Windows release: the highest Windows version that actually
    // ships an installer, preferring the combined release when the same version
    // exists as both.
    private UpdateInfo? PickLatestWindows(JsonElement releases)
    {
        var all = new List<JsonElement>();
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            all.Add(r);
        }

        // Sort by Windows semantic version descending so a newer version always
        // wins over an older one, whatever its tag style or publish date.
        //
        // This used to be two passes — every combined release first, then every
        // platform release, each ordered by publish date — which meant an older
        // combined release that happens to carry an .exe was preferred over a
        // NEWER windows-vX.Y.Z pre-release. The combined release for a build is
        // only created once both platforms have published, so between a Windows
        // build and its combined release the updater reported "up to date" while
        // a newer installer sat in the feed. macOS fixed the same bug in
        // PickLatestMac; the Windows half never got it.
        //
        // The combined-first preference survives as a tiebreak *within* one
        // version: it is the public release, and its body is the nicer
        // changelog, which also decides which copy MergedReleaseNotes keeps.
        all.Sort((a, b) =>
        {
            var av = ExtractVersion(TagOf(a));
            var bv = ExtractVersion(TagOf(b));
            var cmp = CompareVersions(bv, av);           // newest version first
            if (cmp != 0) return cmp;

            var aCombined = IsCombinedTag(TagOf(a));
            var bCombined = IsCombinedTag(TagOf(b));
            if (aCombined != bCombined) return aCombined ? -1 : 1;

            var da = a.TryGetProperty("published_at", out var pa) ? pa.GetString() ?? "" : "";
            var db = b.TryGetProperty("published_at", out var pb) ? pb.GetString() ?? "" : "";
            return string.CompareOrdinal(db, da);        // newest publish first
        });

        foreach (var rel in all)
        {
            if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            var version = ExtractVersion(TagOf(rel));
            if (string.IsNullOrEmpty(version)) continue;

            JsonElement? winAsset = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = (asset.TryGetProperty("name", out var n) ? n.GetString() : "")?.ToLowerInvariant() ?? "";
                if (name.EndsWith(".exe") && (name.Contains("setup") || name.Contains("windows") || name.Contains("-win")))
                { winAsset = asset; break; }
            }
            if (winAsset is null)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = (asset.TryGetProperty("name", out var n) ? n.GetString() : "")?.ToLowerInvariant() ?? "";
                    if (name.EndsWith(".exe")) { winAsset = asset; break; }
                }
            }
            if (winAsset is null) continue;

            var urlStr = winAsset.Value.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(urlStr) || !Uri.TryCreate(urlStr, UriKind.Absolute, out var url)) continue;

            long size = 0;
            if (winAsset.Value.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number)
                size = s.GetInt64();

            // Look for the .sha256 sidecar across every release, not just
            // this one. The public combined release intentionally only
            // ships the bare installer; sidecars live on the per-platform
            // pre-release. Walk all releases so we still get integrity
            // verification.
            var assetName = winAsset.Value.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
            Uri? sha256Uri = FindSidecarAcrossReleases(all, assetName + ".sha256");

            // Every release between what is installed and what is being
            // offered, not just this one's body — see MergedReleaseNotes.
            var notes = MergedReleaseNotes(
                all.Select(r => (
                    Version: ExtractVersion(TagOf(r)),
                    Body: r.TryGetProperty("body", out var bd) ? bd.GetString() ?? "" : "")),
                CurrentVersion,
                version);
            return new UpdateInfo(version, notes, url, sha256Uri, size);
        }
        return null;
    }

    private static string TagOf(JsonElement release) =>
        release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";

    private static bool IsCombinedTag(string tag) =>
        tag.StartsWith("release-", StringComparison.OrdinalIgnoreCase);

    private static Uri? FindSidecarAcrossReleases(List<JsonElement> releases, string sidecarFileName)
    {
        foreach (var rel in releases)
        {
            if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name != sidecarFileName) continue;
                var urlStr = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(urlStr) && Uri.TryCreate(urlStr, UriKind.Absolute, out var url))
                    return url;
            }
        }
        return null;
    }

    // Extracts the WINDOWS X.Y.Z from "windows-vX.Y.Z" or
    // "release-winX.Y.Z-macA.B.C", and "" from a tag that names only macOS.
    //
    // The macOS-only rejection matters because the two platforms version
    // independently: without it "macos-v1.9.0" reads as Windows 1.9.0, and
    // that release's changelog lands in the Windows update panel under a
    // version Windows never had.
    public static string ExtractVersion(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return "";
        var lower = tag.ToLowerInvariant();
        var idx = lower.IndexOf("win", StringComparison.Ordinal);
        if (idx < 0)
        {
            if (lower.Contains("mac", StringComparison.Ordinal)) return "";
            idx = 0;
        }
        var span = lower.AsSpan(idx);
        var start = 0;
        while (start < span.Length && !char.IsDigit(span[start])) start++;
        var end = start;
        while (end < span.Length && (char.IsDigit(span[end]) || span[end] == '.')) end++;
        return start < end ? span[start..end].ToString() : "";
    }

    public static int CompareVersions(string a, string b)
    {
        var av = (a ?? "").Split('.').Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();
        var bv = (b ?? "").Split('.').Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();
        var len = Math.Max(av.Length, bv.Length);
        for (var i = 0; i < len; i++)
        {
            var ai = i < av.Length ? av[i] : 0;
            var bi = i < bv.Length ? bv[i] : 0;
            if (ai != bi) return ai - bi;
        }
        return 0;
    }

    // Downloads the .sha256 sidecar (tiny text file) and returns the hex hash.
    // Returns null if unavailable or malformed.
    private async Task<string?> FetchSHA256SidecarAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Accept "<hex>  <filename>" (sha256sum format) or just "<hex>".
            var hex = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                          .FirstOrDefault(p => p.Length == 64 && p.All(c => "0123456789abcdefABCDEF".Contains(c)));
            return hex?.ToLowerInvariant();
        }
        catch { return null; }
    }

    // Streams a file through SHA256 and returns the lowercase hex digest.
    private static async Task<string> ComputeSHA256HexAsync(string filePath, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var buf = new byte[64 * 1024];
        int read;
        while ((read = await fs.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            sha.TransformBlock(buf, 0, read, null, 0);
        sha.TransformFinalBlock([], 0, 0);
        return BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
    }

    private async Task DownloadAsync(Uri url, string destination, long expectedSize, Action<double>? onProgress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? expectedSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destination);
        var buffer = new byte[64 * 1024];
        long received = 0;
        var lastReport = DateTime.UtcNow;
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            var now = DateTime.UtcNow;
            if (total > 0 && (now - lastReport).TotalMilliseconds > 150)
            {
                lastReport = now;
                onProgress?.Invoke(Math.Min(1.0, (double)received / total));
            }
        }
        onProgress?.Invoke(1.0);
    }

    private void Log(string message)
    {
        var line = $"[UpdateService {DateTime.UtcNow:u}] {message}{Environment.NewLine}";
        try { File.AppendAllText(_logPath, line); } catch { }
        Debug.WriteLine(line);
    }
}
