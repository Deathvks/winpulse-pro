using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinPulseOptimizer;

internal sealed record WinPulseRelease(Version Version, Uri Page);

internal static class GitHubReleaseChecker
{
    internal const string Repository = "Deathvks/winpulse-pro";
    private static readonly Uri LatestEndpoint = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases/");

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(7),
        MaxResponseContentBufferSize = 256 * 1024
    };

    internal static async Task<WinPulseRelease?> GetNewerReleaseAsync(Version installedVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestEndpoint);
        request.Headers.UserAgent.ParseAdd("WinPulsePro/" + installedVersion.ToString(3));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true, MustRevalidate = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var release = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseNewerRelease(release.RootElement, installedVersion);
    }

    internal static WinPulseRelease? ParseNewerRelease(JsonElement release, Version installedVersion)
    {
        var current = new Version(installedVersion.Major, installedVersion.Minor, Math.Max(0, installedVersion.Build));
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var preview) || preview.ValueKind != JsonValueKind.False ||
            !TryReadString(release, "tag_name", out string tag) || !tag.StartsWith('v') ||
            !Version.TryParse(tag.AsSpan(1), out var latest) || latest.Build < 0 || latest.Revision >= 0 ||
            !string.Equals(tag, $"v{latest.ToString(3)}", StringComparison.Ordinal) ||
            latest <= current ||
            !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.Object && TryReadString(asset, "name", out string name) &&
                name == "WinPulseSetup.exe" && TryReadString(asset, "state", out string state) && state == "uploaded" &&
                asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number &&
                size.TryGetInt64(out long length) && length > 0)
                return new(latest, new Uri(ReleasesPage, "tag/" + tag));
        }
        return null;
    }

    private static bool TryReadString(JsonElement item, string name, out string value)
    {
        value = item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? "" : "";
        return value.Length > 0;
    }
}
