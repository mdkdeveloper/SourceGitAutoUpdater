using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using SourceGitAutoUpdater.Core;
using SourceGitAutoUpdater.Windows;

namespace SourceGitAutoUpdater;

internal static class Program
{
    internal static string[] Arguments { get; private set; } = [];
    internal static string WorkingDirectory { get; private set; } = Environment.CurrentDirectory;
    internal static LauncherInstanceBroker? Broker { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex)
        {
            try
            {
                var paths = new AppPaths(AppContext.BaseDirectory);
                Directory.CreateDirectory(paths.StateDirectory);
                File.AppendAllText(Path.Combine(paths.StateDirectory, "errors.log"), $"{DateTimeOffset.Now:o} {ex}\n");
            }
            catch { }
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        Arguments = args;
        if (!OperatingSystem.IsWindows()) return 1;
        TaskbarIntegration.SetProcessIdentity();
        var paths = new AppPaths(AppContext.BaseDirectory);
        if (IsSpecialMode(args) && File.Exists(paths.ExecutablePath))
        {
            using var gate = InstallationGate.AcquireShared(paths.Root);
            var shortcut = paths.IsPortable ? Path.Combine(paths.Root, "SourceGitAutoUpdater.lnk")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SourceGitAutoUpdater.lnk");
            try { ShortcutManager.Ensure(Environment.ProcessPath!, shortcut); }
            catch { /* A failed shortcut does not prevent a Git editor from running. */ }
            return SpecialModeHost.Run(paths.ExecutablePath, Environment.ProcessPath!, args, WorkingDirectory);
        }
        bool preview = args.Contains("--preview-update", StringComparer.Ordinal);
        if (!preview)
        {
            Broker = new LauncherInstanceBroker(paths.Root);
            if (!Broker.IsPrimary)
            {
                try { Broker.ForwardAsync(args, WorkingDirectory).GetAwaiter().GetResult(); return 0; }
                catch { return 1; }
                finally { Broker.Dispose(); }
            }
        }
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex)
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.AppendAllText(Path.Combine(paths.StateDirectory, "errors.log"), $"{DateTimeOffset.Now:o} {ex}\n");
            return 1;
        }
        finally { Broker?.Dispose(); }
    }

    internal static bool IsSpecialMode(string[] args) =>
        args.FirstOrDefault() is "--history" or "--blame" or "--core-editor" or "--rebase-todo-editor" or "--rebase-message-editor"
        || Environment.GetEnvironmentVariable("SOURCEGIT_LAUNCH_AS_ASKPASS") == "TRUE";

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect().WithInterFont().ConfigureFonts(manager => manager.AddFontCollection(
            new EmbeddedFontCollection(new Uri("fonts:SourceGitAutoUpdater"), new Uri("avares://SourceGitAutoUpdater/Resources/Fonts"))))
        .LogToTrace();
}


