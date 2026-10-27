using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SourceGitAutoUpdater.Core;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Digest);
public sealed record ReleaseInfo(string TagName, Version Version, DateTimeOffset PublishedAt, string Body, ReleaseAsset Asset);
public sealed record UpdateProgress(string Phase, long Received, long? Total)
{
    public double? Percent => Total is > 0 ? Math.Clamp(Received * 100.0 / Total.Value, 0, 100) : null;
}

public sealed class ReleaseService
{
    private readonly HttpClient _client;
    public ReleaseService(HttpClient? client = null) => _client = client ?? new HttpClient();

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/sourcegit-scm/sourcegit/releases/latest");
        request.Headers.UserAgent.ParseAdd("SourceGitAutoUpdater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag) ?? throw new InvalidDataException("Unsupported SourceGit release version.");
        var assets = root.GetProperty("assets").EnumerateArray().Select(asset => new ReleaseAsset(
            asset.GetProperty("name").GetString() ?? "",
            new Uri(asset.GetProperty("browser_download_url").GetString()!),
            asset.GetProperty("size").GetInt64(),
            asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String ? digest.GetString() : null)).ToList();
        var selected = SelectAsset(assets, version) ?? throw new InvalidDataException("No compatible official Windows ZIP was found.");
        return new ReleaseInfo(tag, version, root.GetProperty("published_at").GetDateTimeOffset(), root.GetProperty("body").GetString() ?? "", selected);
    }

    public static Version? ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^v?\d+\.\d+(?:\.\d+){0,2}$", RegexOptions.CultureInvariant)) return null;
        return Version.TryParse(value.TrimStart('v'), out var version) ? Normalize(version) : null;
    }

    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    public static string Runtime => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException("Only Windows x64 and ARM64 are supported.")
    };

    public static ReleaseAsset? SelectAsset(IEnumerable<ReleaseAsset> assets, Version version, string? runtime = null)
    {
        runtime ??= Runtime;
        if (runtime is not ("win-x64" or "win-arm64")) throw new ArgumentException("Unsupported runtime.", nameof(runtime));
        var matches = assets.Where(asset =>
        {
            var match = Regex.Match(asset.Name, @"^sourcegit_(\d+\.\d+(?:\.\d+){0,2})\." + Regex.Escape(runtime) + @"\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && ParseVersion(match.Groups[1].Value) == Normalize(version);
        }).ToList();
        if (matches.Count > 1) throw new InvalidDataException("Ambiguous official Windows release assets.");
        return matches.SingleOrDefault();
    }

    public static Version? GetInstalledVersion(string executable)
    {
        if (!File.Exists(executable)) return null;
        var info = FileVersionInfo.GetVersionInfo(executable);
        return ParseVersion(info.FileVersion ?? "") ?? ParseVersion(info.ProductVersion?.Split('+')[0] ?? "");
    }

    public static bool ShouldOfferUpdate(ReleaseInfo release, Version? installed, string? ignoredTag, bool force = false)
        => installed is null || release.Version > Normalize(installed) && (force || !string.Equals(release.TagName, ignoredTag, StringComparison.Ordinal));
}


