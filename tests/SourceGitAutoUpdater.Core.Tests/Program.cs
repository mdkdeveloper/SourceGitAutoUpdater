using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceGitAutoUpdater.Core;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
var roots = new List<string>();
AppPaths Fixture()
{
    var path = Path.Combine(Path.GetTempPath(), "SourceGitAutoUpdater.Tests", Guid.NewGuid().ToString("N"));
    roots.Add(path);
    Directory.CreateDirectory(path);
    var paths = new AppPaths(path, portable: true);
    Directory.CreateDirectory(paths.DataDirectory);
    File.WriteAllText(paths.PreferencePath, "{\"IgnoreUpdateTag\":\"v2026.22\",\"Check4UpdatesOnStartup\":true,\"FutureField\":{\"value\":42},\"Theme\":\"Dark\"}");
    return paths;
}
byte[] Zip(params (string Name, byte[] Body)[] entries)
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var (name, body) in entries)
        {
            using var output = archive.CreateEntry(name).Open();
            output.Write(body);
        }
    return memory.ToArray();
}
byte[] Executable() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "SourceGitAutoUpdater.Core.Tests.exe"));
(ReleaseInfo Release, HttpClient Client) Download(byte[] zip, string? digest = null, Version? version = null)
{
    var asset = new ReleaseAsset("sourcegit_2026.23.win-x64.zip", new Uri("https://github.com/sourcegit-scm/sourcegit/releases/download/v2026.23/sourcegit_2026.23.win-x64.zip"), zip.Length, digest);
    return (new ReleaseInfo("v2026.23", version ?? new Version(2026,23,0,0), DateTimeOffset.UtcNow, "Changes", asset), new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) })));
}
void BadArchive(params (string Name, byte[] Body)[] entries)
{
    var paths = Fixture();
    var archive = Path.Combine(paths.Root, "test.zip");
    File.WriteAllBytes(archive, Zip(entries));
    Throws<InvalidDataException>(() => UpdaterEngine.ExtractArchive(archive, Path.Combine(paths.Root, "output")));
}
void SeedOld(AppPaths paths) => File.WriteAllText(paths.ExecutablePath, "previous executable");

Test("numeric versions normalize, padded minor supported, prereleases rejected", () =>
{
    Assert(ReleaseService.ParseVersion("v2026.03") == new Version(2026,3,0,0));
    Assert(ReleaseService.ParseVersion("2026.23.1") == new Version(2026,23,1,0));
    Assert(ReleaseService.ParseVersion("v2026.23-rc1") is null);
    Assert(ReleaseService.ParseVersion("malformed") is null);
    Assert(ReleaseService.ParseVersion("v2026.23")! > ReleaseService.ParseVersion("v2026.9")!);
});
Test("asset selection picks architecture and exact numeric version", () =>
{
    ReleaseAsset Asset(string name) => new(name, new Uri("https://github.com/x"), 1, null);
    var assets = new[] { Asset("sourcegit_2026.03.win-x64.zip"), Asset("sourcegit_2026.03.win-arm64.zip"), Asset("sourcegit_2026.03.osx-x64.zip"), Asset("sourcegit_2026.30.win-x64.zip") };
    Assert(ReleaseService.SelectAsset(assets, new Version(2026,3), "win-x64")!.Name.EndsWith("03.win-x64.zip"));
    Assert(ReleaseService.SelectAsset(assets, new Version(2026,3), "win-arm64")!.Name.EndsWith("03.win-arm64.zip"));
    Assert(ReleaseService.SelectAsset(assets, new Version(2027,3), "win-x64") is null);
    Throws<InvalidDataException>(() => ReleaseService.SelectAsset([assets[0], assets[0]], new Version(2026,3), "win-x64"));
});
Test("Skip suppresses one exact tag, next version returns, manual check overrides", () =>
{
    var (release, _) = Download([]);
    Assert(!ReleaseService.ShouldOfferUpdate(release, new Version(2026,22), "v2026.23"));
    Assert(ReleaseService.ShouldOfferUpdate(release, new Version(2026,22), "v2026.22"));
    Assert(ReleaseService.ShouldOfferUpdate(release, new Version(2026,22), "v2026.23", force: true));
    Assert(!ReleaseService.ShouldOfferUpdate(release, new Version(2026,24), null, force: true));
    Assert(!ReleaseService.ShouldOfferUpdate(release, new Version(2026,23), null));
    Assert(ReleaseService.ShouldOfferUpdate(release, null, "v2026.23"), "Skip must never prevent first installation");
});
Test("settings mutation preserves unknown fields and old backup", () =>
{
    var paths = Fixture();
    var original = File.ReadAllText(paths.PreferencePath);
    using var lease = SourceGitLease.TryAcquire(paths);
    new PreferencesStore(paths).Update("v2026.23");
    var preferences = new PreferencesStore(paths).Read();
    Assert(preferences["FutureField"]!["value"]!.GetValue<int>() == 42);
    Assert(preferences["IgnoreUpdateTag"]!.GetValue<string>() == "v2026.23");
    Assert(!preferences["Check4UpdatesOnStartup"]!.GetValue<bool>());
    Assert(File.ReadAllText(paths.PreferencePath + ".bak") == original);
    new PreferencesStore(paths).Update();
    Assert(new PreferencesStore(paths).Read()["IgnoreUpdateTag"]!.GetValue<string>() == "v2026.23");
});
Test("corrupt or non-object settings are never replaced", () =>
{
    var paths = Fixture();
    foreach (var json in new[] { "broken json", "null", "[]" })
    {
        File.WriteAllText(paths.PreferencePath, json);
        try { new PreferencesStore(paths).Update(); throw new Exception("Expected settings rejection"); }
        catch (Exception error) when (error is JsonException or InvalidDataException) { }
        Assert(File.ReadAllText(paths.PreferencePath) == json);
    }
});
Test("SourceGit lease excludes concurrent instances", () =>
{
    var paths = Fixture();
    using (var first = SourceGitLease.TryAcquire(paths))
    {
        Assert(first is not null);
        Assert(SourceGitLease.TryAcquire(paths) is null);
    }
    using var after = SourceGitLease.TryAcquire(paths);
    Assert(after is not null);
});
Test("archive traversal and Windows path escape rejected", () =>
{
    foreach (var name in new[] { "../outside", "SourceGit/../outside", "SourceGit/..\\outside", "/SourceGit/SourceGit.exe", "SourceGit/C:/evil", "SourceGit/file:stream", "SourceGit/trailing. /evil" })
        BadArchive((name, Encoding.UTF8.GetBytes("bad")));
});
Test("archives cannot inject portable data or duplicate paths", () =>
{
    BadArchive(("SourceGit/data/preference.json", Encoding.UTF8.GetBytes("{}")));
    BadArchive(("SourceGit/SourceGit.exe", [1]), ("SourceGit/sourcegit.EXE", [2]));
});
Test("wrong root and missing executable rejected", () =>
{
    BadArchive(("Different/SourceGit.exe", [1]));
    BadArchive(("SourceGit/README", [1]));
});
Test("archive symbolic links rejected", () =>
{
    var paths = Fixture();
    var zip = Path.Combine(paths.Root, "link.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("SourceGit/SourceGit.exe");
        entry.ExternalAttributes = unchecked((int)0xA1FF0000);
    }
    Throws<InvalidDataException>(() => UpdaterEngine.ExtractArchive(zip, Path.Combine(paths.Root, "out")));
});
Test("official download URL constrained to upstream HTTPS release assets", () =>
{
    Assert(UpdaterEngine.IsOfficialAssetUrl(new Uri("https://github.com/sourcegit-scm/sourcegit/releases/download/v1/package.zip")));
    foreach (var url in new[] { "http://github.com/sourcegit-scm/sourcegit/releases/download/a", "https://evil.test/sourcegit-scm/sourcegit/releases/download/a", "https://github.com/attacker/sourcegit/releases/download/a", "https://github.com/sourcegit-scm/sourcegit/releases/download-fake/a" })
        Assert(!UpdaterEngine.IsOfficialAssetUrl(new Uri(url)));
});
Test("PE executable validates architecture and exact release version", () =>
{
    var exe = Path.Combine(AppContext.BaseDirectory, "SourceGitAutoUpdater.Core.Tests.exe");
    UpdaterEngine.ValidateExecutable(exe, new Version(2026,23));
    Throws<InvalidDataException>(() => UpdaterEngine.ValidateExecutable(exe, new Version(2026,24)));
    var paths = Fixture();
    File.WriteAllBytes(paths.ExecutablePath, [1,2,3]);
    Throws<BadImageFormatException>(() => UpdaterEngine.ValidateExecutable(paths.ExecutablePath, new Version(2026,23)));
});
AsyncTest("successful install verifies digest, preserves portable data and backup", async () =>
{
    var paths = Fixture();
    SeedOld(paths);
    File.WriteAllText(Path.Combine(paths.DataDirectory, "custom-settings.json"), "keep me");
    var zip = Zip(("SourceGit/SourceGit.exe", Executable()), ("SourceGit/LICENSE", Encoding.UTF8.GetBytes("MIT")));
    var (release, client) = Download(zip, "sha256:" + Convert.ToHexString(SHA256.HashData(zip)));
    using (var lease = SourceGitLease.TryAcquire(paths))
    {
        await new UpdaterEngine(paths, client).InstallAsync(release);
        Assert(SourceGitLease.TryAcquire(paths) is null, "Lease must follow preserved portable data");
    }
    Assert(ReleaseService.GetInstalledVersion(paths.ExecutablePath) == new Version(2026,23,0,0));
    Assert(File.ReadAllLines(Path.Combine(paths.SourceGitDirectory,"sourcegit-managed-files.txt")).Contains("SourceGit.exe"));
    Assert(File.ReadAllText(Path.Combine(paths.DataDirectory, "custom-settings.json")) == "keep me");
    Assert(File.ReadAllText(Path.Combine(paths.TransactionDirectory, "backup", "SourceGit.exe")) == "previous executable");
    Assert(new PreferencesStore(paths).Read()["FutureField"]!["value"]!.GetValue<int>() == 42);
});
AsyncTest("digest mismatch leaves existing program and data untouched", async () =>
{
    var paths = Fixture(); SeedOld(paths);
    var (release, client) = Download(Zip(("SourceGit/SourceGit.exe", Executable())), "sha256:" + new string('0',64));
    await ThrowsAsync<InvalidDataException>(() => new UpdaterEngine(paths, client).InstallAsync(release));
    Assert(File.ReadAllText(paths.ExecutablePath) == "previous executable");
    Assert(new PreferencesStore(paths).Read()["Theme"]!.GetValue<string>() == "Dark");
});
AsyncTest("size mismatch and truncated downloads never replace existing program", async () =>
{
    var paths = Fixture(); SeedOld(paths);
    var (release, client) = Download([1,2,3]);
    release = release with { Asset = release.Asset with { Size = 9 } };
    await ThrowsAsync<InvalidDataException>(() => new UpdaterEngine(paths, client).InstallAsync(release));
    Assert(File.ReadAllText(paths.ExecutablePath) == "previous executable");
});
AsyncTest("failed executable validation leaves installation unchanged", async () =>
{
    var paths = Fixture(); SeedOld(paths);
    var (release, client) = Download(Zip(("SourceGit/SourceGit.exe", Executable())), version: new Version(2026,24));
    await ThrowsAsync<InvalidDataException>(() => new UpdaterEngine(paths, client).InstallAsync(release));
    Assert(File.ReadAllText(paths.ExecutablePath) == "previous executable");
});
foreach (var checkpoint in new[] { "validated", "backup-entry-moved", "old-moved", "install-entry-moved", "new-moved" })
{
    var selected = checkpoint;
    AsyncTest("transaction rollback after injected failure: " + selected, async () =>
    {
        var paths = Fixture(); SeedOld(paths);
        var original = File.ReadAllText(paths.PreferencePath);
        var (release, client) = Download(Zip(("SourceGit/SourceGit.exe", Executable())));
        using (var lease = SourceGitLease.TryAcquire(paths))
        {
            var engine = new UpdaterEngine(paths, client, point => { if (point == selected) throw new IOException("Injected failure"); });
            await ThrowsAsync<IOException>(() => engine.InstallAsync(release));
            Assert(SourceGitLease.TryAcquire(paths) is null);
        }
        Assert(File.ReadAllText(paths.ExecutablePath) == "previous executable");
        Assert(File.ReadAllText(paths.PreferencePath) == original);
        new UpdaterEngine(paths).Recover();
        Assert(File.ReadAllText(paths.PreferencePath) == original, "Recovery must be idempotent");
    });
}
foreach (var point in new[] { "backing-up", "installing" })
{
    var selected = point;
    Test("restart recovery preserves stationary portable data lock: " + selected, () =>
    {
        var paths = Fixture(); SeedOld(paths);
        var original = File.ReadAllText(paths.PreferencePath);
        var stage = Path.Combine(paths.TransactionDirectory, "stage", "SourceGit");
        var backup = Path.Combine(paths.TransactionDirectory, "backup");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(stage, "SourceGit.exe"), "new executable");
        File.WriteAllText(Path.Combine(paths.TransactionDirectory, "journal.json"), JsonSerializer.Serialize(new { Phase = selected, Version = "2026.23.0.0", NewEntries = new[] { "SourceGit.exe" } }));
        File.Move(paths.ExecutablePath, Path.Combine(backup, "SourceGit.exe"));
        if (selected == "installing") File.Move(Path.Combine(stage, "SourceGit.exe"), paths.ExecutablePath);
        using (var lease = SourceGitLease.TryAcquire(paths))
        {
            Assert(lease is not null);
            new UpdaterEngine(paths).Recover();
            Assert(SourceGitLease.TryAcquire(paths) is null, "Restart lease must remain on original data");
        }
        Assert(File.ReadAllText(paths.ExecutablePath) == "previous executable");
        Assert(File.ReadAllText(paths.PreferencePath) == original);
    });
}
Test("a second restart during rollback preserves already restored original files", () =>
{
    var paths = Fixture(); SeedOld(paths);
    var backup = Path.Combine(paths.TransactionDirectory, "backup");
    Directory.CreateDirectory(backup);
    File.WriteAllText(Path.Combine(backup,"LICENSE"), "old license");
    File.WriteAllText(Path.Combine(paths.TransactionDirectory, "journal.json"), JsonSerializer.Serialize(new { Phase = "restoring", Version = "2026.23.0.0", NewEntries = new[] { "SourceGit.exe", "LICENSE" } }));
    using var lease = SourceGitLease.TryAcquire(paths);
    new UpdaterEngine(paths).Recover();
    Assert(File.ReadAllText(paths.ExecutablePath) == "previous executable");
    Assert(File.ReadAllText(Path.Combine(paths.SourceGitDirectory,"LICENSE")) == "old license");
});
AsyncTest("release API parses stable assets with correct runtime", async () =>
{
    var json = "{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v2026.23\",\"published_at\":\"2026-10-01T00:00:00Z\",\"body\":\"Release notes\",\"assets\":[{\"name\":\"sourcegit_2026.23." + ReleaseService.Runtime + ".zip\",\"browser_download_url\":\"https://github.com/sourcegit-scm/sourcegit/releases/download/v2026.23/package.zip\",\"size\":123,\"digest\":null}]}";
    using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
    var release = await new ReleaseService(client).GetLatestAsync();
    Assert(release?.TagName == "v2026.23" && release.Asset.Size == 123);
});
AsyncTest("release API failures surface for silent-start fallback", async () =>
{
    using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
    await ThrowsAsync<HttpRequestException>(() => new ReleaseService(client).GetLatestAsync());
});

string LatestJson(string tag, byte[] zip, bool prerelease = false) => JsonSerializer.Serialize(new
{
    draft = false, prerelease, tag_name = tag, published_at = DateTimeOffset.UtcNow, body = "Official release notes",
    assets = new[] { new
    {
        name = $"sourcegit_{tag.TrimStart('v')}.{ReleaseService.Runtime}.zip",
        browser_download_url = $"https://github.com/sourcegit-scm/sourcegit/releases/download/{tag}/sourcegit_{tag.TrimStart('v')}.{ReleaseService.Runtime}.zip",
        size = zip.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(zip))
    } }
});
HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

AsyncTest("bootstrap installs latest despite saved Skip and preserves native preferences", async () =>
{
    var paths = Fixture();
    File.WriteAllText(paths.PreferencePath, File.ReadAllText(paths.PreferencePath).Replace("v2026.22", "v2026.23"));
    var original = File.ReadAllText(paths.PreferencePath);
    var zip = Zip(("SourceGit/SourceGit.exe", Executable()));
    var phases = new List<string>();
    ReleaseInfo? resolved = null;
    using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com" ? Json(LatestJson("v2026.23", zip)) : Bytes(zip)));
    using var lease = SourceGitLease.TryAcquire(paths);
    using var service = new BootstrapInstaller(paths, client, releaseResolved: release => resolved = release);
    var release = await service.InstallLatestAsync(new InlineProgress(progress => phases.Add(progress.Phase)));
    Assert(release.TagName == "v2026.23" && resolved == release);
    Assert(File.Exists(paths.ExecutablePath));
    Assert(File.ReadAllText(paths.PreferencePath) == original, "Bootstrap must not disable native update checks before caller acknowledges successful install");
    Assert(phases[0] == "Checking" && phases.Contains("Downloading") && phases.Contains("Complete"));
});
AsyncTest("bootstrap Retry re-queries latest release instead of reusing failed tag", async () =>
{
    var paths = Fixture();
    var original = File.ReadAllText(paths.PreferencePath);
    var zip = Zip(("SourceGit/SourceGit.exe", Executable()));
    var queries = 0;
    var resolved = new List<string>();
    using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
        ? Json(LatestJson(++queries == 1 ? "v2026.22" : "v2026.23", zip)) : Bytes(zip)));
    using var lease = SourceGitLease.TryAcquire(paths);
    using var service = new BootstrapInstaller(paths, client, releaseResolved: release => resolved.Add(release.TagName));
    await ThrowsAsync<InvalidDataException>(() => service.InstallLatestAsync());
    Assert(!File.Exists(paths.ExecutablePath) && File.ReadAllText(paths.PreferencePath) == original);
    var release = await service.InstallLatestAsync();
    Assert(queries == 2 && release.TagName == "v2026.23");
    Assert(resolved.SequenceEqual(new[] { "v2026.22", "v2026.23" }));
    Assert(File.ReadAllText(paths.PreferencePath) == original);
});
AsyncTest("bootstrap network failure is one attempt and Retry starts a fresh query", async () =>
{
    var paths = Fixture();
    var original = File.ReadAllText(paths.PreferencePath);
    var zip = Zip(("SourceGit/SourceGit.exe", Executable()));
    var queries = 0;
    using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
        ? ++queries == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(LatestJson("v2026.23", zip)) : Bytes(zip)));
    using var lease = SourceGitLease.TryAcquire(paths);
    using var service = new BootstrapInstaller(paths, client);
    await ThrowsAsync<HttpRequestException>(() => service.InstallLatestAsync());
    Assert(queries == 1 && !File.Exists(paths.ExecutablePath));
    Assert(File.ReadAllText(paths.PreferencePath) == original);
    await service.InstallLatestAsync();
    Assert(queries == 2 && File.Exists(paths.ExecutablePath));
});
AsyncTest("bootstrap cancellation while checking does not download or modify native settings", async () =>
{
    var paths = Fixture();
    var original = File.ReadAllText(paths.PreferencePath);
    var calls = 0;
    using var cancellation = new CancellationTokenSource();
    using var client = new HttpClient(new AsyncHandler(async (_, token) =>
    {
        calls++;
        cancellation.Cancel();
        await Task.Delay(Timeout.Infinite, token);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }));
    using var lease = SourceGitLease.TryAcquire(paths);
    using var service = new BootstrapInstaller(paths, client);
    await ThrowsAsync<OperationCanceledException>(() => service.InstallLatestAsync(cancellationToken: cancellation.Token));
    Assert(calls == 1 && !File.Exists(paths.ExecutablePath));
    Assert(File.ReadAllText(paths.PreferencePath) == original);
});
AsyncTest("bootstrap cancellation during download cleans staging and preserves native settings", async () =>
{
    var paths = Fixture();
    var original = File.ReadAllText(paths.PreferencePath);
    var zip = Zip(("SourceGit/SourceGit.exe", Executable()));
    using var cancellation = new CancellationTokenSource();
    using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com" ? Json(LatestJson("v2026.23", zip)) : Bytes(zip)));
    using var lease = SourceGitLease.TryAcquire(paths);
    using var service = new BootstrapInstaller(paths, client);
    var progress = new InlineProgress(value => { if (value.Phase == "Downloading" && value.Received > 0) cancellation.Cancel(); });
    await ThrowsAsync<OperationCanceledException>(() => service.InstallLatestAsync(progress, cancellation.Token));
    Assert(!File.Exists(paths.ExecutablePath));
    Assert(File.ReadAllText(paths.PreferencePath) == original);
    Assert(!Directory.Exists(Path.Combine(paths.TransactionDirectory,"stage")));
});
AsyncTest("bootstrap pre-cancelled attempt performs no network request", async () =>
{
    var paths = Fixture();
    var calls = 0;
    using var client = new HttpClient(new Handler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); }));
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    using var service = new BootstrapInstaller(paths, client);
    await ThrowsAsync<OperationCanceledException>(() => service.InstallLatestAsync(cancellationToken: cancellation.Token));
    Assert(calls == 0 && !File.Exists(paths.ExecutablePath));
});
AsyncTest("bootstrap rejects absence of a stable release without touching preferences", async () =>
{
    var paths = Fixture(); var original = File.ReadAllText(paths.PreferencePath);
    using var client = new HttpClient(new Handler(_ => Json(LatestJson("v2026.23", [], prerelease: true))));
    using var service = new BootstrapInstaller(paths, client);
    await ThrowsAsync<InvalidDataException>(() => service.InstallLatestAsync());
    Assert(!File.Exists(paths.ExecutablePath) && File.ReadAllText(paths.PreferencePath) == original);
});
foreach (var checkpoint in new[] { "validated", "old-moved", "install-entry-moved", "new-moved" })
{
    var selected = checkpoint;
    AsyncTest("interrupted first installation restores no-executable state and data: " + selected, async () =>
    {
        var paths = Fixture(); var original = File.ReadAllText(paths.PreferencePath);
        var zip = Zip(("SourceGit/SourceGit.exe", Executable()));
        using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com" ? Json(LatestJson("v2026.23", zip)) : Bytes(zip)));
        using var lease = SourceGitLease.TryAcquire(paths);
        using var service = new BootstrapInstaller(paths, client, point => { if (point == selected) throw new IOException("Injected bootstrap interruption"); });
        await ThrowsAsync<IOException>(() => service.InstallLatestAsync());
        Assert(!File.Exists(paths.ExecutablePath));
        Assert(File.ReadAllText(paths.PreferencePath) == original);
        new UpdaterEngine(paths).Recover();
        Assert(!File.Exists(paths.ExecutablePath) && File.ReadAllText(paths.PreferencePath) == original);
    });
}
AsyncTest("bootstrap commit checkpoint is last cancellable boundary", async () =>
{
    var paths = Fixture(); var original = File.ReadAllText(paths.PreferencePath);
    var zip = Zip(("SourceGit/SourceGit.exe", Executable()));
    using var cancellation = new CancellationTokenSource();
    using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com" ? Json(LatestJson("v2026.23", zip)) : Bytes(zip)));
    using var lease = SourceGitLease.TryAcquire(paths);
    var validated = false;
    using var service = new BootstrapInstaller(paths, client, point =>
    {
        if (point != "validated") return;
        Assert(!File.Exists(paths.ExecutablePath));
        validated = true;
        // Models the UI disabling cancellation at this synchronous commit checkpoint.
        cancellation.Cancel();
    });
    await service.InstallLatestAsync(cancellationToken: cancellation.Token);
    Assert(validated && File.Exists(paths.ExecutablePath));
    Assert(File.ReadAllText(paths.PreferencePath) == original);
});

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failures++; Console.WriteLine("FAIL " + name + Environment.NewLine + exception); }
}
// Test directories are generated GUID children; never remove an arbitrary caller-supplied path.
foreach (var root in roots)
{
    var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SourceGitAutoUpdater.Tests")) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed");
return failures == 0 ? 0 : 1;

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
}



sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
}

sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
{
    public void Report(UpdateProgress value) => report(value);
}
