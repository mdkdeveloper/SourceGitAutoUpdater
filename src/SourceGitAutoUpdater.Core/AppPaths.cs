using System.Text.Json;
using System.Text.Json.Nodes;

namespace SourceGitAutoUpdater.Core;

public sealed class AppPaths
{
    public AppPaths(string root, bool? portable = null)
    {
        Root = Path.GetFullPath(root);
        IsPortable = portable ?? !File.Exists(Path.Combine(Root, "installed.json"));
    }

    public string Root { get; }
    public bool IsPortable { get; }
    public string SourceGitDirectory => Path.Combine(Root, "SourceGit");
    public string ExecutablePath => Path.Combine(SourceGitDirectory, "SourceGit.exe");
    public string DataDirectory => IsPortable ? Path.Combine(SourceGitDirectory, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SourceGit");
    public string StateDirectory => IsPortable ? Path.Combine(Root, ".updater") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceGitAutoUpdater");
    public string PreferencePath => Path.Combine(DataDirectory, "preference.json");
    public string TransactionDirectory => Path.Combine(Root, ".sourcegit-update");
}

public sealed class PreferencesStore(AppPaths paths)
{
    public JsonObject Read()
    {
        if (!File.Exists(paths.PreferencePath)) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(paths.PreferencePath)) as JsonObject
            ?? throw new InvalidDataException("SourceGit preference.json must contain a JSON object.");
    }

    // Caller must own SourceGitLease. Read the latest contents on every mutation.
    public void Update(string? ignoredTag = null)
    {
        var preferences = Read();
        preferences["Check4UpdatesOnStartup"] = false;
        if (ignoredTag is not null) preferences["IgnoreUpdateTag"] = ignoredTag;
        Directory.CreateDirectory(paths.DataDirectory);
        AtomicFile.Write(paths.PreferencePath, preferences.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), backup: true);
    }
}

public static class SourceGitLease
{
    public static IDisposable? TryAcquire(AppPaths paths)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            // The updater preserves this directory and lock in place during file replacement.
            return new FileStream(Path.Combine(paths.DataDirectory, "process.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
    }
}

internal static class AtomicFile
{
    internal static void Write(string path, string content, bool backup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, backup ? path + ".bak" : null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}


