using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace SourceGitAutoUpdater.Core;

/// <summary>Caller owns SourceGitLease throughout recovery and installation.</summary>
public sealed class UpdaterEngine
{
    private readonly AppPaths _paths;
    private readonly HttpClient _client;
    private readonly Action<string>? _checkpoint;
    private const long MaximumExpandedSize = 1024L * 1024 * 1024;
    private string Work => _paths.TransactionDirectory;
    private string Stage => Path.Combine(Work, "stage");
    private string StagedApp => Path.Combine(Stage, "SourceGit");
    private string Backup => Path.Combine(Work, "backup");
    private string JournalPath => Path.Combine(Work, "journal.json");

    public UpdaterEngine(AppPaths paths, HttpClient? client = null, Action<string>? checkpoint = null)
    {
        _paths = paths;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _checkpoint = checkpoint;
    }

    private sealed record Journal(string Phase, string Version, string[] NewEntries);
    private void Save(Journal journal) => AtomicFile.Write(JournalPath, JsonSerializer.Serialize(journal));

    public void Recover()
    {
        if (!File.Exists(JournalPath)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath))
            ?? throw new InvalidDataException("Invalid updater recovery journal.");
        if (journal.Phase is "committed" or "rolled-back") return;
        if (journal.NewEntries is null || journal.NewEntries.Any(name => !SafeEntryName(name)))
            throw new InvalidDataException("Unsafe updater recovery journal.");
        if (journal.Phase == "prepared")
        {
            DeleteTree(Stage);
            Save(journal with { Phase = "rolled-back" });
            return;
        }
        if (journal.Phase is not ("backing-up" or "installing" or "restoring")) throw new InvalidDataException("Unknown updater recovery phase.");
        Directory.CreateDirectory(_paths.SourceGitDirectory);
        if (journal.Phase == "installing")
        {
            foreach (var name in journal.NewEntries) DeleteEntry(Path.Combine(_paths.SourceGitDirectory, name));
        }
        if (journal.Phase != "restoring")
        {
            // Persist before moving originals back. A second interruption must never
            // delete original entries already restored by the previous recovery.
            journal = journal with { Phase = "restoring" };
            Save(journal);
        }
        if (Directory.Exists(Backup))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(Backup))
            {
                var name = Path.GetFileName(entry);
                if (!SafeEntryName(name)) throw new InvalidDataException("Unsafe backup entry.");
                var target = Path.Combine(_paths.SourceGitDirectory, name);
                DeleteEntry(target);
                MoveEntry(entry, target);
            }
        }
        DeleteTree(Stage);
        Save(journal with { Phase = "rolled-back" });
    }

    public async Task InstallAsync(ReleaseInfo release, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Recover();
        var installed = ReleaseService.GetInstalledVersion(_paths.ExecutablePath);
        if (installed is not null && release.Version <= installed)
            throw new InvalidOperationException("The requested release is not newer than the installed SourceGit.");
        if (!IsOfficialAssetUrl(release.Asset.DownloadUrl)) throw new InvalidDataException("The asset is not an official SourceGit release download.");
        Directory.CreateDirectory(Work);
        DeleteTree(Stage);
        // Retain the previous successful version until another update begins.
        DeleteTree(Backup);
        var zip = Path.Combine(Work, "download.zip");
        var journal = new Journal("prepared", release.Version.ToString(), []);
        Save(journal);
        try
        {
            await DownloadAsync(release.Asset, zip, progress, cancellationToken);
            progress?.Report(new UpdateProgress("Extracting", 0, null));
            ExtractArchive(zip, Stage, cancellationToken);
            ValidateExecutable(Path.Combine(StagedApp, "SourceGit.exe"), release.Version);
            cancellationToken.ThrowIfCancellationRequested();
            _checkpoint?.Invoke("validated");
            var managedFiles = Directory.EnumerateFiles(StagedApp, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(StagedApp, file).Replace('\\', '/'))
                .Where(file => !file.Equals("sourcegit-managed-files.txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.Ordinal).ToArray();
            File.WriteAllLines(Path.Combine(StagedApp, "sourcegit-managed-files.txt"), managedFiles);
            var newEntries = Directory.EnumerateFileSystemEntries(StagedApp).Select(Path.GetFileName).Cast<string>().ToArray();
            if (newEntries.Any(name => !SafeEntryName(name))) throw new InvalidDataException("Unsafe staged package entry.");
            journal = journal with { Phase = "backing-up", NewEntries = newEntries };
            Save(journal);
            progress?.Report(new UpdateProgress("Installing", 0, null));
            Directory.CreateDirectory(_paths.SourceGitDirectory);
            Directory.CreateDirectory(Backup);
            // Keep SourceGit/data and its process.lock stationary on Windows.
            // Only package entries move, each under an on-disk recovery phase.
            foreach (var entry in Directory.EnumerateFileSystemEntries(_paths.SourceGitDirectory))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
                if (!SafeEntryName(name)) throw new InvalidDataException("Unsafe installed package entry.");
                MoveEntry(entry, Path.Combine(Backup, name));
                _checkpoint?.Invoke("backup-entry-moved");
            }
            _checkpoint?.Invoke("old-moved");
            journal = journal with { Phase = "installing" };
            Save(journal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(StagedApp))
            {
                MoveEntry(entry, Path.Combine(_paths.SourceGitDirectory, Path.GetFileName(entry)));
                _checkpoint?.Invoke("install-entry-moved");
            }
            _checkpoint?.Invoke("new-moved");
            Save(journal with { Phase = "committed" });
            if (File.Exists(zip)) File.Delete(zip);
            DeleteTree(Stage);
            progress?.Report(new UpdateProgress("Complete", 1, 1));
        }
        catch
        {
            Recover();
            throw;
        }
    }

    private static bool SafeEntryName(string name) => !string.IsNullOrEmpty(name) && name is not ("." or "..")
        && !name.Equals("data", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(['/', '\\', ':']) < 0;

    private static void MoveEntry(string source, string target)
    {
        if (Directory.Exists(source)) Directory.Move(source, target);
        else File.Move(source, target);
    }

    private void DeleteEntry(string path)
    {
        var root = Path.GetFullPath(_paths.SourceGitDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var absolute = Path.GetFullPath(path);
        if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !SafeEntryName(Path.GetRelativePath(root, absolute)))
            throw new InvalidOperationException("Unsafe installation cleanup path.");
        if (Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Unexpected installation link.");
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path)) File.Delete(path);
    }

    public static bool IsOfficialAssetUrl(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/sourcegit-scm/sourcegit/releases/download/", StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.UserInfo);

    private async Task DownloadAsync(ReleaseAsset asset, string target, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("SourceGitAutoUpdater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var expected = asset.Size > 0 ? asset.Size : response.Content.Headers.ContentLength;
        if (expected is > MaximumExpandedSize) throw new InvalidDataException("The release archive is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long received = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, token);
            if (count == 0) break;
            received += count;
            if (received > MaximumExpandedSize || (expected is > 0 && received > expected)) throw new InvalidDataException("Release size exceeded the expected size.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            progress?.Report(new UpdateProgress("Downloading", received, expected));
        }
        await output.FlushAsync(token);
        output.Flush(flushToDisk: true);
        if (expected is > 0 && received != expected) throw new InvalidDataException("The release download is incomplete.");
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.IsNullOrWhiteSpace(asset.Digest))
        {
            if (!asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(actual, asset.Digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release SHA-256 verification failed.");
        }
    }

    public static void ExtractArchive(string zipPath, string destination, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(zipPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments[0] != "SourceGit" || name.StartsWith('/')
                || segments.Any(p => p is "." or ".." || p.Contains(':') || p.EndsWith('.') || p.EndsWith(' '))
                || segments.Length > 1 && segments[1].Equals("data", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unexpected or unsafe release archive path: " + name);
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Links are not permitted in release archives.");
            var target = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive path escaped staging.");
            if (!seen.Add(target)) throw new InvalidDataException("Duplicate release archive path.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedSize || archive.Entries.Count > 10000) throw new InvalidDataException("Release expands beyond allowed limits.");
            if (name.EndsWith('/')) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[65536];
                long written = 0;
                int count;
                while ((count = source.Read(buffer)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    written += count;
                    if (written > entry.Length) throw new InvalidDataException("Invalid expanded file size.");
                    output.Write(buffer, 0, count);
                }
                if (written != entry.Length) throw new InvalidDataException("Truncated release archive entry.");
            }
        }
        if (!File.Exists(Path.Combine(root, "SourceGit", "SourceGit.exe"))) throw new InvalidDataException("SourceGit.exe is missing from the release archive.");
    }

    public static void ValidateExecutable(string executable, Version expectedVersion)
    {
        using var stream = File.OpenRead(executable);
        using var pe = new PEReader(stream);
        var machine = pe.PEHeaders.CoffHeader.Machine;
        var expectedMachine = ReleaseService.Runtime == "win-arm64" ? Machine.Arm64 : Machine.Amd64;
        if (machine != expectedMachine || pe.PEHeaders.PEHeader is null)
            throw new InvalidDataException("The release executable architecture does not match this Windows installation.");
        var version = ReleaseService.GetInstalledVersion(executable);
        if (version is null || ReleaseService.Normalize(version) != ReleaseService.Normalize(expectedVersion))
            throw new InvalidDataException("The release executable version does not match its tag.");
    }

    private void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        var absolute = Path.GetFullPath(path);
        var boundary = Path.GetFullPath(Work).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing deletion outside updater transaction directory.");
        if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Unexpected link in updater transaction directory.");
        Directory.Delete(absolute, recursive: true);
    }
}



