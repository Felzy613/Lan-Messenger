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

        var url = $"https://api.github.com/repos/{repo}/releases";
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

                Log("Installer spawned — exiting so the installer can replace files");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    Microsoft.UI.Xaml.Application.Current.Exit();
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

    // MARK: - Helpers

    // Finds the best Windows release: prefers combined tags, falls back to platform tags.
    private UpdateInfo? PickLatestWindows(JsonElement releases)
    {
        var all = new List<JsonElement>();
        foreach (var r in releases.EnumerateArray()) all.Add(r);
        all.Sort((a, b) =>
        {
            var da = a.TryGetProperty("published_at", out var pa) ? pa.GetString() ?? "" : "";
            var db = b.TryGetProperty("published_at", out var pb) ? pb.GetString() ?? "" : "";
            return string.CompareOrdinal(db, da); // newest first
        });

        // Two passes: combined releases first, then per-platform.
        foreach (var combined in new[] { true, false })
        {
            foreach (var rel in all)
            {
                if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

                var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var isCombined = tag.StartsWith("release-", StringComparison.OrdinalIgnoreCase);
                if (combined != isCombined) continue;

                var version = ExtractVersion(tag);
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

                // Look for an optional .sha256 sidecar among the same release's assets.
                var assetName = winAsset.Value.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                Uri? sha256Uri = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var sidecarName = asset.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
                    if (sidecarName == assetName + ".sha256")
                    {
                        var sidUrl = asset.TryGetProperty("browser_download_url", out var su) ? su.GetString() : null;
                        if (!string.IsNullOrEmpty(sidUrl)) Uri.TryCreate(sidUrl, UriKind.Absolute, out sha256Uri);
                        break;
                    }
                }

                var notes = rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                return new UpdateInfo(version, notes, url, sha256Uri, size);
            }
        }
        return null;
    }

    // Extracts X.Y.Z from "windows-vX.Y.Z" or "release-winX.Y.Z-macA.B.C".
    public static string ExtractVersion(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return "";
        var lower = tag.ToLowerInvariant();
        var idx = lower.IndexOf("win", StringComparison.Ordinal);
        if (idx < 0) idx = 0;
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
