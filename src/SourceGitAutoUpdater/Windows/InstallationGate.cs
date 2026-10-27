using System.Security.Cryptography;
using System.Text;

namespace SourceGitAutoUpdater.Windows;

/// <summary>
/// Coordinates standalone SourceGit editor/viewer processes with this updater's short mutations.
/// A shared lease lasts for the child process lifetime; an exclusive lease covers only disk mutation.
/// Direct launches of SourceGit outside this launcher cannot participate in this gate.
/// </summary>
public static class InstallationGate
{
    public static IDisposable? TryAcquireExclusive(string root)
    {
        var path = GetPath(root);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when (IsContention(error)) { return null; }
    }

    public static IDisposable AcquireShared(string root, CancellationToken cancellationToken = default)
    {
        var path = GetPath(root);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite); }
            catch (IOException error) when (IsContention(error))
            {
                // This runs before GUI initialization for direct editor/viewer invocations.
                // Do not spin while an update commits; cancellation remains responsive.
                if (cancellationToken.WaitHandle.WaitOne(50))
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static bool IsContention(IOException error) => (error.HResult & 0xffff) is 32 or 33;

    private static string GetPath(string root)
    {
        var canonical = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var identity = Environment.UserName + "|" + canonical;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceGitAutoUpdater", "instances");
        Directory.CreateDirectory(directory);
        // As with the broker lock, retain the empty gate file to avoid delete/recreate races.
        return Path.Combine(directory, hash + ".gate");
    }
}
