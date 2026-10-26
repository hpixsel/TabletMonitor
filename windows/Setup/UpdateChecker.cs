using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TabletMonitor.Setup;

public sealed record UpdateInfo(Version Version, string Tag, string ReleaseUrl, string ExeName, string ExeUrl, string ChecksumsUrl);

/// <summary>
/// Updates come from GitHub Releases. A release carries "TabletMonitor-&lt;version&gt;-win-x64.exe",
/// "TabletMonitor-&lt;version&gt;.apk" and "SHA256SUMS.txt" (see release.ps1); the downloaded exe must match its checksum.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "hpixsel/TabletMonitor";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>This build's version; TABLETMONITOR_PRETEND_VERSION overrides it to test updates.</summary>
    public static Version CurrentVersion { get; } =
        Version.TryParse(Environment.GetEnvironmentVariable("TABLETMONITOR_PRETEND_VERSION"), out var pretend) ? pretend :
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TabletMonitor/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Returns the latest release when it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var response = await Http.GetAsync(LatestReleaseApi, token);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warn($"Update check: HTTP {(int)response.StatusCode}");
            return null;
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= CurrentVersion)
            return null;

        string? exeName = null, exeUrl = null, sumsUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase))
                (exeName, exeUrl) = (name, url);
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                sumsUrl = url;
        }
        if (exeName == null || exeUrl == null || sumsUrl == null)
        {
            Log.Warn($"Update {tag}: release has no Windows exe or checksums");
            return null;
        }
        return new UpdateInfo(version, tag, root.GetProperty("html_url").GetString() ?? "", exeName, exeUrl, sumsUrl);
    }

    /// <summary>Downloads the new exe and verifies it against SHA256SUMS.txt. Returns the local path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken token = default)
    {
        var sums = await Http.GetStringAsync(update.ChecksumsUrl, token);
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].Trim().TrimStart('*') == update.ExeName)?[0];
        if (expected == null)
            throw new InvalidOperationException($"No checksum for {update.ExeName}.");

        var dir = Path.Combine(Log.Directory, "update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, update.ExeName);

        using (var response = await Http.GetAsync(update.ExeUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var src = await response.Content.ReadAsStreamAsync(token);
            await using var dst = File.Create(path);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, token)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (total > 0)
                    progress?.Report((double)done / total);
            }
        }

        await using (var file = File.OpenRead(path))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidOperationException("The downloaded update is damaged (checksum mismatch).");
            }
        }
        Log.Info($"Update {update.Tag} downloaded and verified");
        return path;
    }
}
