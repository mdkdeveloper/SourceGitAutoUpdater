using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SourceGitAutoUpdater.Windows;

public static class ShortcutManager
{
    /// <summary>Creates a stable launcher shortcut. The user decides whether to pin it.</summary>
    public static void Ensure(string launcherExecutable, string shortcutPath)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var executable = Path.GetFullPath(launcherExecutable);
        var destination = Path.GetFullPath(shortcutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        object instance = new ShellInterop.ShellLink();
        try
        {
            var link = (ShellInterop.IShellLinkW)instance;
            link.SetPath(executable);
            link.SetArguments(string.Empty);
            link.SetDescription("Launch SourceGit with automatic release updates");
            link.SetIconLocation(executable, 0);
            link.SetShowCmd(1);
            // A Start-menu launch starts from the user's profile instead of a versioned app directory.
            link.SetWorkingDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var store = (ShellInterop.IPropertyStore)instance;
            ShellInterop.SetString(store, ShellInterop.AppId, TaskbarIntegration.AppId);
            Marshal.ThrowExceptionForHR(store.Commit());
            ((IPersistFile)instance).Save(destination, true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
}
