using System.Text.Json;

namespace VibeRails.Services;

public class UpdateService
{
    private readonly HttpClient _httpClient;
    private readonly string _githubRepo = "robstokes857/vibe-rails";
    private UpdateInfo? _cachedUpdateInfo;
    private DateTime _lastCheck = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromHours(1);

    public UpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "VibeRails-UpdateChecker");
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        // Return cached result if still valid
        if (_cachedUpdateInfo != null && DateTime.UtcNow - _lastCheck < _cacheExpiration)
        {
            return _cachedUpdateInfo;
        }

        try
        {
            var currentVersion = VersionInfo.Version;
            var releaseUrl = $"https://api.github.com/repos/{_githubRepo}/releases/latest";

            var response = await _httpClient.GetAsync(releaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            // Parse JSON using JsonDocument for AOT compatibility
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElement))
            {
                return null;
            }

            var tagName = tagElement.GetString();
            if (string.IsNullOrEmpty(tagName))
            {
                return null;
            }

            // Parse version (remove 'v' prefix if present)
            var latestVersion = tagName.TrimStart('v');
            var updateAvailable = IsNewerVersion(currentVersion, latestVersion);

            var releaseNotesUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : null;

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                    var size = asset.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;

                    if (name != null && downloadUrl != null)
                    {
                        assets.Add(new ReleaseAsset
                        {
                            Name = name,
                            DownloadUrl = downloadUrl,
                            Size = size
                        });
                    }
                }
            }

            _cachedUpdateInfo = new UpdateInfo
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                UpdateAvailable = updateAvailable,
                ReleaseNotesUrl = releaseNotesUrl,
                Assets = assets
            };

            _lastCheck = DateTime.UtcNow;
            return _cachedUpdateInfo;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VibeRails] Error checking for updates: {ex.Message}");
            return null;
        }
    }

    private static bool IsNewerVersion(string current, string latest)
    {
        try
        {
            var currentParts = current.Split('.').Select(int.Parse).ToArray();
            var latestParts = latest.Split('.').Select(int.Parse).ToArray();

            for (int i = 0; i < Math.Min(currentParts.Length, latestParts.Length); i++)
            {
                if (latestParts[i] > currentParts[i]) return true;
                if (latestParts[i] < currentParts[i]) return false;
            }

            return latestParts.Length > currentParts.Length;
        }
        catch
        {
            return false;
        }
    }

}

public class UpdateInfo
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public bool UpdateAvailable { get; set; }
    public string? ReleaseNotesUrl { get; set; }
    public List<ReleaseAsset> Assets { get; set; } = new();
}

public class ReleaseAsset
{
    public string Name { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long Size { get; set; }
}
