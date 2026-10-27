using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SourceGitAutoUpdater.Core;
using SourceGitAutoUpdater.ViewModels;
using SourceGitAutoUpdater.Views;
using SourceGitAutoUpdater.Windows;

namespace SourceGitAutoUpdater;

internal sealed class LauncherController(IClassicDesktopStyleApplicationLifetime lifetime)
{
    private readonly AppPaths _paths = new(AppContext.BaseDirectory);
    private readonly Queue<(string[] Args, string Cwd)> _pending = new();
    private TaskbarIntegration? _taskbar;
    private DispatcherTimer? _timer;
    private Process? _child;
    private bool _ready;
    private DateTime _started;
    private bool _seenProcess;
    private double _zoom = 1;

    internal async Task StartAsync()
    {
        if (Program.Arguments.Contains("--preview-update"))
        {
            await PreviewAsync();
            return;
        }
        if (Program.Broker is { } broker)
        {
            broker.Diagnostic = Log;
            broker.LaunchRequested += (args, cwd) => Dispatcher.UIThread.Post(() => Forward(args, cwd));
            broker.Start();
        }
        lifetime.Exit += (_, _) => { _timer?.Stop(); _taskbar?.Dispose(); _child?.Dispose(); };
        try
        {
            _taskbar = new TaskbarIntegration(_paths.ExecutablePath, Environment.ProcessPath!) { Diagnostic = Log };
            _taskbar.Start();
            var shortcut = _paths.IsPortable ? Path.Combine(_paths.Root, "SourceGitAutoUpdater.lnk")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SourceGitAutoUpdater.lnk");
            ShortcutManager.Ensure(Environment.ProcessPath!, shortcut);
        }
        catch (Exception ex) { Log(ex.ToString()); }

        try
        {
            if (ProcessHost.HasManagedProcess(_paths.ExecutablePath)) { Launch(Program.Arguments, Program.WorkingDirectory); return; }
            using (var lease = SourceGitLease.TryAcquire(_paths))
            {
                if (lease is null)
                {
                    if (File.Exists(_paths.ExecutablePath)) { Launch(Program.Arguments, Program.WorkingDirectory); return; }
                    throw new IOException(Texts.T("SourceGit settings are in use. Close other SourceGit instances and launch the updater again.", "Налаштування SourceGit зайняті. Закрийте інші екземпляри SourceGit і запустіть апдейтер знову."));
                }
                using (AcquireMutationGate()) new UpdaterEngine(_paths).Recover();
                var preferences = new PreferencesStore(_paths);
                var current = preferences.Read();
                ((App)Application.Current!).ApplyPreferences(current);
                _zoom = Math.Clamp(current["Zoom"]?.GetValue<double>() ?? 1, .5, 3);
                await ImportIfNeededAsync();
                current = preferences.Read();
                _zoom = Math.Clamp(current["Zoom"]?.GetValue<double>() ?? 1, .5, 3);
                ((App)Application.Current!).ApplyPreferences(current);
                if (!File.Exists(_paths.ExecutablePath))
                {
                    if (!await InstallFirstVersionAsync()) { lifetime.Shutdown(); return; }
                    using (AcquireMutationGate()) preferences.Update();
                }
                else
                {
                    using (AcquireMutationGate()) preferences.Update();
                    var force = Program.Arguments.Contains("--check-updates", StringComparer.Ordinal);
                    ReleaseInfo? release;
                    try { release = await new ReleaseService().GetLatestAsync(); }
                    catch (Exception ex)
                    {
                        Log(ex.ToString());
                        if (force) await MessageWindow.AskAsync(Texts.Error, ex.Message, Texts.Launch);
                        release = null;
                    }
                    var installed = ReleaseService.GetInstalledVersion(_paths.ExecutablePath);
                    if (release is not null && ReleaseService.ShouldOfferUpdate(release, installed, current["IgnoreUpdateTag"]?.GetValue<string>(), force))
                        await OfferUpdateAsync(release, installed, preferences);
                    else if (force && release is not null)
                        await MessageWindow.AskAsync(Texts.T("Software Update", "Оновлення програми"), Texts.T("There are currently no updates available.", "Наразі оновлень немає."), Texts.Launch);
                }
            }
            if (File.Exists(_paths.ExecutablePath)) Launch(Program.Arguments, Program.WorkingDirectory);
            else lifetime.Shutdown(1);
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            await MessageWindow.AskAsync(Texts.Error, ex.Message, File.Exists(_paths.ExecutablePath) ? Texts.Launch : Texts.T("Close", "Закрити"));
            if (File.Exists(_paths.ExecutablePath)) Launch(Program.Arguments, Program.WorkingDirectory);
            else lifetime.Shutdown(1);
        }
    }

    private async Task<bool> InstallFirstVersionAsync()
    {
        while (true)
        {
            using var cancellation = new CancellationTokenSource();
            var vm = new UpdateViewModel();
            var window = new UpdateWindow(vm, _zoom);
            window.CancelRequested += () => cancellation.Cancel();
            window.Show();
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (cancellation.IsCancellationRequested) return;
                ApplyProgress(vm, p);
            });
            try
            {
                await Task.Run(async () =>
                {
                    IDisposable? commitGate = null;
                    try
                    {
                        using var installer = new BootstrapInstaller(_paths, checkpoint: phase =>
                        {
                            if (phase != "validated") return;
                            Dispatcher.UIThread.Invoke(() =>
                            {
                                cancellation.Token.ThrowIfCancellationRequested();
                                vm.CanCancel = false;
                                vm.Status = Texts.T("Installing SourceGit…", "Встановлення SourceGit…");
                            });
                            commitGate = AcquireMutationGate();
                        }, releaseResolved: release => Dispatcher.UIThread.Post(() =>
                        {
                            if (!cancellation.IsCancellationRequested) vm.SetRelease(release);
                        }));
                        await installer.InstallLatestAsync(progress, cancellation.Token);

                    }
                    finally { commitGate?.Dispose(); }
                });
                window.Finish();
                return true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                window.Finish();
                return false;
            }
            catch (Exception ex)
            {
                window.Finish();
                Log(ex.ToString());
                var retry = await MessageWindow.AskAsync(Texts.Error, ex.Message, Texts.T("Retry", "Повторити"), Texts.T("Exit", "Вийти"));
                if (!retry) return false;
                // Each attempt refetches latest; no first-install Skip or installed-version fallback.
            }
        }
    }

    private static void ApplyProgress(UpdateViewModel vm, UpdateProgress p)
    {
        vm.IsIndeterminate = p.Percent is null;
        vm.Progress = p.Percent ?? 0;
        vm.Status = p.Phase switch
        {
            "Checking" => Texts.T("Checking the latest stable SourceGit release…", "Перевірка останнього стабільного релізу SourceGit…"),
            "Downloading" => Texts.T("Downloading", "Завантаження") + $"  {p.Received / 1048576.0:F1}" + (p.Total is > 0 ? $" / {p.Total / 1048576.0:F1} MiB" : " MiB"),
            "Extracting" => Texts.T("Checking and extracting the package…", "Перевірка та розпакування пакета…"),
            "Installing" => Texts.T("Installing SourceGit…", "Встановлення SourceGit…"),
            _ => Texts.T("Ready", "Готово")
        };
    }
    private async Task OfferUpdateAsync(ReleaseInfo release, Version? installed, PreferencesStore preferences)
    {
        while (true)
        {
            var vm = new UpdateViewModel(release, installed);
            var window = new UpdateWindow(vm, _zoom);
            var choice = await window.ChooseAsync();
            if (choice != UpdateChoice.Update)
            {
                window.Close();
                if (choice == UpdateChoice.Skip) { using (AcquireMutationGate()) preferences.Update(release.TagName); }
                return;
            }
            vm.IsBusy = true;
            try
            {
                var progress = new Progress<UpdateProgress>(p =>
                {
                    ApplyProgress(vm, p);
                });
                await Task.Run(async () =>
                {
                    IDisposable? commitGate = null;
                    try
                    {
                        var engine = new UpdaterEngine(_paths, checkpoint: phase =>
                        {
                            if (phase == "validated") commitGate = AcquireMutationGate();
                        });
                        await engine.InstallAsync(release, progress);
                    }
                    finally { commitGate?.Dispose(); }
                });
                vm.IsBusy = false;
                window.Close();
                return;
            }
            catch (Exception ex)
            {
                vm.IsBusy = false;
                window.Close();
                Log(ex.ToString());
                var retry = await MessageWindow.AskAsync(Texts.Error, ex.Message, Texts.T("Retry", "Повторити"), Texts.T("Continue", "Продовжити"));
                if (!retry) return;
            }
        }
    }

    private IDisposable AcquireMutationGate()
    {
        var gate = InstallationGate.TryAcquireExclusive(_paths.Root);
        if (gate is null) throw new IOException(Texts.T("SourceGit is running. Close its windows and try updating again.", "SourceGit працює. Закрийте його вікна й повторіть оновлення."));
        if (ProcessHost.HasManagedProcess(_paths.ExecutablePath))
        {
            gate.Dispose();
            throw new IOException(Texts.T("SourceGit opened while preparing the update. The update has been deferred.", "SourceGit відкрито під час підготовки. Оновлення відкладено."));
        }
        return gate;
    }

    private async Task ImportIfNeededAsync()
    {
        if (!_paths.IsPortable || File.Exists(_paths.PreferencePath)) return;
        Directory.CreateDirectory(_paths.StateDirectory);
        var marker = Path.Combine(_paths.StateDirectory, "import-completed");
        if (File.Exists(marker)) return;
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SourceGit");
        if (File.Exists(Path.Combine(source, "preference.json")))
        {
            var accept = await MessageWindow.AskAsync(Texts.T("SourceGit settings", "Налаштування SourceGit"),
                Texts.T("Import your existing SourceGit settings and repository list into this portable copy?", "Імпортувати наявні налаштування SourceGit та список репозиторіїв у цю портативну копію?"),
                Texts.T("Import", "Імпортувати"), Texts.T("Start fresh", "Почати з чистих налаштувань"));
            while (accept)
            {
                try
                {
                    using var sourceLease = new FileStream(Path.Combine(source, "process.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var staging = Path.Combine(_paths.StateDirectory, "import-" + Guid.NewGuid().ToString("N"));
                    CopySettings(source, staging);
                    using var gate = AcquireMutationGate();
                    // Commit preferences last: a failed import is retried, never mistaken for complete.
                    var stagedPreference = Path.Combine(staging, "preference.json");
                    foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                        .Where(file => !string.Equals(file, stagedPreference, StringComparison.OrdinalIgnoreCase)))
                    {
                        var target = Path.Combine(_paths.DataDirectory, Path.GetRelativePath(staging, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
                    }
                    File.Move(stagedPreference, _paths.PreferencePath, overwrite: false);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log(ex.ToString());
                    accept = await MessageWindow.AskAsync(Texts.T("SourceGit settings", "Налаштування SourceGit"),
                        Texts.T("Settings could not be imported. Close the original SourceGit and retry, or start with fresh settings.", "Не вдалося імпортувати налаштування. Закрийте початковий SourceGit і повторіть або почніть із чистих налаштувань."),
                        Texts.T("Retry", "Повторити"), Texts.T("Start fresh", "Почати з чистих налаштувань"));
                }
            }
        }
        File.WriteAllText(marker, "1");
    }
    private static void CopySettings(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.Name.Equals("process.lock", StringComparison.OrdinalIgnoreCase)
                || entry.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                || entry.Name is "preference_tmp.json" or "avatars" or "crashes" or "cache" or "logs") continue;
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory) CopySettings(directory.FullName, target);
            else File.Copy(entry.FullName, target, overwrite: false);
        }
    }

    private void Forward(string[] args, string cwd)
    {
        if (!_ready) { _pending.Enqueue((args, cwd)); return; }
        try { ProcessHost.Launch(_paths.ExecutablePath, args.Where(a => a != "--check-updates"), cwd)?.Dispose(); }
        catch (Exception ex) { Log(ex.ToString()); }
        _taskbar?.Refresh();
    }

    private void Launch(string[] args, string cwd)
    {
        _child = ProcessHost.Launch(_paths.ExecutablePath, args.Where(a => a != "--check-updates"), cwd);
        _started = DateTime.UtcNow;
        _ready = true;
        while (_pending.TryDequeue(out var next)) Forward(next.Args, next.Cwd);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _taskbar?.Refresh();
            bool running = ProcessHost.HasManagedProcess(_paths.ExecutablePath);
            if (running) _seenProcess = true;
            else if (_seenProcess || DateTime.UtcNow - _started > TimeSpan.FromSeconds(5))
            {
                _timer?.Stop();
                lifetime.Shutdown(_child is { HasExited: true } ? _child.ExitCode : 0);
            }
        });
        _timer.Start();
    }

    private async Task PreviewAsync()
    {
        ((App)Application.Current!).ApplyPreferences(new JsonObject(), Program.Arguments);
        var scaleValue = Program.Arguments.FirstOrDefault(a => a.StartsWith("--scale="))?[8..];
        double zoom = double.TryParse(scaleValue, CultureInfo.InvariantCulture, out var scale) ? Math.Clamp(scale, .5, 3) : 1;
        var asset = new ReleaseAsset("preview", new Uri("https://github.com/sourcegit-scm/sourcegit/releases/download/v2026.24/preview.zip"), 0, null);
        var release = new ReleaseInfo("v2026.24", new Version(2026,24,0,0), DateTimeOffset.Parse("2026-10-27T12:00:00Z"),
            "## What's Changed\n\n* Improve repository navigation and diff views.\n* Fix checkout and branch switching edge cases.\n* Update translations.\n\nFull Changelog: `v2026.23...v2026.24`", asset);
        var window = new UpdateWindow(new UpdateViewModel(release, new Version(2026,23)), zoom);
        await window.ChooseAsync();
        window.Close();
        lifetime.Shutdown();
    }

    private void Log(string message)
    {
        try { Directory.CreateDirectory(_paths.StateDirectory); File.AppendAllText(Path.Combine(_paths.StateDirectory, "errors.log"), $"{DateTimeOffset.Now:o} {message}\n"); }
        catch { }
    }
}






