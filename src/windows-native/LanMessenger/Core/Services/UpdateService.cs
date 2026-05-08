using System.Reflection;
using System.Text.Json;

namespace LanMessenger.Core.Services;

// Checks the configured update server for a newer version.
public sealed class UpdateService
{
    public static UpdateService Shared { get; } = new();

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private UpdateService() { }

    public string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    // Returns the latest version string if an update is available, otherwise null.
    public async Task<string?> CheckForUpdateAsync(string updateServerUrl)
    {
        if (string.IsNullOrWhiteSpace(updateServerUrl)) return null;
        try
        {
            var url = updateServerUrl.TrimEnd('/') + "/lan-messenger-update.json";
            var json = await _http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Expect { "version": "1.2.3", ... }
            if (!root.TryGetProperty("version", out var vProp)) return null;
            var latest = vProp.GetString();
            if (latest is null) return null;

            if (Version.TryParse(latest, out var latestVer) &&
                Version.TryParse(CurrentVersion, out var currentVer) &&
                latestVer > currentVer)
                return latest;

            return null;
        }
        catch { return null; }
    }
}
