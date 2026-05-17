using LanMessenger.Core.Persistence;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace LanMessenger.Core.Services;

public sealed record UpdateInfo(string Version, string Notes, Uri DownloadUrl, long ExpectedSize);

public enum UpdateProgressState { Idle, Downloading, Installing, Failed }

public sealed record UpdateProgress(UpdateProgressState State, double Fraction = 0, string? Message = null);

// Fetches updates from GitHub Releases, picks the Windows installer asset, downloads,
// verifies, then spawns the installer in silent mode and exits.
//
// Layout assumptions (matching .github/workflows/release.yml):
//   - Per-platform tag:  windows-vX.Y.Z
//   - Combined tag:      release-winX.Y.Z-macA.B.C
//   - Asset filename:    LanMessenger-Setup-X.Y.Z.exe (Inno Setup installer)
public sealed class UpdateService
{
    public static UpdateService Shared { get; } = new();

    private static readonly HttpClient _http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LanMessenger-Windows", CurrentStaticVersion));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
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
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Log("Unexpected JSON shape");
                return null;
            }

            var picked = PickLatestWindows(doc.RootElement);
            if (picked is null)
            {
                Log("No Windows release found");
                return null;
            }
            if (CompareVersions(picked.Version, CurrentVersion) > 0)
            {
                Log($"Update available: {picked.Version} (we're on {CurrentVersion})");
                return picked;
            }
            Log($"Already on latest ({CurrentVersion} >= {picked.Version})");
            return null;
        }
        catch (Exception ex)
        {
            Log($"Check failed: {ex.Message}");
            return null;
        }
    }

    // Backwards-compatible entry point used by the existing Settings page.
    public async Task<string?> CheckForUpdateAsync(string repoOrUrl)
    {
        var repo = NormalizeRepo(repoOrUrl);
        var info = await CheckAsync(repo).ConfigureAwait(false);
        return info?.Version;
    }

    private static string NormalizeRepo(string s)
    {
        s = (s ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return ConfigStore.Shared.Config.UpdateRepo;
        // Accept "owner/repo", "github.com/owner/repo", "https://github.com/owner/repo"
        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(s);
                var parts = uri.AbsolutePath.Trim('/').Split('/');
                if (parts.Length >= 2) return $"{parts[0]}/{parts[1]}";
            }
            catch { }
        }
        if (s.Contains('/') && !s.Contains(' ')) return s;
        return ConfigStore.Shared.Config.UpdateRepo;
    }

    // MARK: - Download + install

    public async Task<bool> DownloadAndInstallAsync(UpdateInfo info, Action<UpdateProgress>? onProgress = null, CancellationToken ct = default)
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
            // Single-instance install lock — if another update is running in another process, bail out.
            var lockPath = Path.Combine(stagingDir, "install.lock");
            FileStream? lockFile = null;
            try
            {
                lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
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

                onProgress?.Invoke(new(UpdateProgressState.Downloading, 0));
                Log($"Downloading {info.DownloadUrl} → {setupPath}");
                await DownloadAsync(info.DownloadUrl, setupPath, info.ExpectedSize, onProgress, ct).ConfigureAwait(false);

                // Sanity-check the file
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
                Log($"Download verified ({actualSize} bytes)");

                onProgress?.Invoke(new(UpdateProgressState.Installing, 1));
                Log("Launching installer in silent mode");

                // Remove the Zone.Identifier ADS that HttpClient can leave on downloaded
                // files. If the stream is present, ShellExecute with Verb="open" will fail
                // with error 5 (Access Denied) before UAC even appears; removing it first
                // lets the elevation flow proceed normally.
                try { File.Delete(setupPath + ":Zone.Identifier"); } catch { }

                // /VERYSILENT runs with no UI, /SUPPRESSMSGBOXES squelches errors, /NORESTART
                // avoids forced reboot, /CLOSEAPPLICATIONS+/RESTARTAPPLICATIONS lets Inno
                // shut us down and relaunch the new build after install.
                var psi = new ProcessStartInfo(setupPath)
                {
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                    UseShellExecute = true,
                    Verb = "runas",  // explicitly request elevation so UAC always fires cleanly
                };
                try
                {
                    Process.Start(psi);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    // User clicked "No" on the UAC prompt — treat as a soft cancel, not a crash.
                    Log("UAC prompt declined by user");
                    onProgress?.Invoke(new(UpdateProgressState.Failed, 1, "Update canceled"));
                    return false;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    // ShellExecute handed the launch off to the UAC elevation broker but
                    // couldn't return a process handle — the installer will start once the
                    // user approves.  Log and fall through so we still schedule the exit.
                    Log($"ShellExecute elevation handoff (error 5); scheduling exit");
                }
                Log("Installer spawned — exiting current process so the installer can replace files");

                // Give the installer a moment to start, then quit ourselves so files
                // aren't locked. Inno will relaunch us via /RESTARTAPPLICATIONS.
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
        finally
        {
            _gate.Release();
        }
    }

    // MARK: - Helpers

    private UpdateInfo? PickLatestWindows(JsonElement releases)
    {
        // Releases array, newest first. Pick the first non-draft release that has
        // a Windows installer asset.
        var ordered = new List<JsonElement>();
        foreach (var r in releases.EnumerateArray()) ordered.Add(r);
        ordered.Sort((a, b) =>
        {
            var da = a.TryGetProperty("published_at", out var pa) ? pa.GetString() ?? "" : "";
            var db = b.TryGetProperty("published_at", out var pb) ? pb.GetString() ?? "" : "";
            return string.CompareOrdinal(db, da);
        });

        foreach (var rel in ordered)
        {
            if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var version = ExtractVersion(tag);
            if (string.IsNullOrEmpty(version)) continue;

            JsonElement? winAsset = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = (asset.TryGetProperty("name", out var n) ? n.GetString() : "")?.ToLowerInvariant() ?? "";
                if (name.EndsWith(".exe") && (name.Contains("setup") || name.Contains("windows") || name.Contains("-win")))
                {
                    winAsset = asset; break;
                }
            }
            // Looser fallback: any .exe in the release
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

            var notes = rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new UpdateInfo(version, notes, url, size);
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
        // Skip non-digits
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

    private async Task DownloadAsync(Uri url, string destination, long expectedSize, Action<UpdateProgress>? progress, CancellationToken ct)
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
                progress?.Invoke(new(UpdateProgressState.Downloading, Math.Min(1.0, (double)received / total)));
            }
        }
        progress?.Invoke(new(UpdateProgressState.Downloading, 1.0));
    }

    private void Log(string message)
    {
        var line = $"[UpdateService {DateTime.UtcNow:u}] {message}{Environment.NewLine}";
        try { File.AppendAllText(_logPath, line); } catch { }
        Debug.WriteLine(line);
    }
}
