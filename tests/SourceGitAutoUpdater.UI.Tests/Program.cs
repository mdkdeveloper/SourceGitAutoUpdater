using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Fonts;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using SourceGitAutoUpdater;
using SourceGitAutoUpdater.Core;
using SourceGitAutoUpdater.ViewModels;
using SourceGitAutoUpdater.Views;

namespace SourceGitAutoUpdater.UI.Tests;

internal static class Program
{
    private static readonly List<string> Checks = [];
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            // SetupWithoutStarting supplies no desktop lifetime: the real launcher controller
            // never starts, and no SourceGit, filesystem settings or desktop UI is touched.
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, FrameBufferFormat = Avalonia.Platform.PixelFormat.Bgra8888 })
                .WithInterFont()
                .ConfigureFonts(manager => manager.AddFontCollection(new EmbeddedFontCollection(
                    new Uri("fonts:SourceGitAutoUpdater"), new Uri("avares://SourceGitAutoUpdater/Resources/Fonts"))))
                .SetupWithoutStarting();
            var output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "ui-tests"));
            Directory.CreateDirectory(output);
            var results = new List<object>();
            var app = (App)Application.Current!;
            Assert(app.ApplicationLifetime is null, "Headless tests must never initialize the desktop launcher lifetime.");
            var hashes = new HashSet<string>();
            foreach (var theme in new[] { "Dark", "Light" })
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                Apply(app, theme, "uk_UA");
                var vm = new UpdateViewModel(Release(), new Version(2026,22));
                var window = new UpdateWindow(vm, scale);
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var buttons = ValidateLayout(window);
                using var frame = window.CaptureRenderedFrame() ?? throw new Exception("Headless Skia did not render a frame.");
                var filename = $"{theme.ToLowerInvariant()}-{scale * 100:0}.png";
                var path = Path.Combine(output, filename);
                frame.Save(path);
                Assert(frame.PixelSize.Width >= 500 * scale && frame.PixelSize.Height >= 350 * scale, "Scaled frame dimensions are smaller than the content.");
                var pixelColors = new HashSet<int>();
                var greenPixels = 0;
                using (var locked = frame.Lock())
                {
                    var pixels = new byte[locked.RowBytes * locked.Size.Height];
                    Marshal.Copy(locked.Address, pixels, 0, pixels.Length);
                    for (var offset = 0; offset + 4 <= pixels.Length; offset += 4)
                    {
                        pixelColors.Add(BitConverter.ToInt32(pixels, offset));
                        if (pixels[offset + 3] > 128 && pixels[offset + 1] > 60
                            && pixels[offset + 1] > pixels[offset + 2] * 1.5 && pixels[offset + 1] > pixels[offset] * 1.5)
                            greenPixels++;
                    }
                }
                Assert(greenPixels <= 500 * scale * scale, "Green info icon paints outside its 14x14 layout bounds; verify icon stretch/geometry.");
                Assert(pixelColors.Count > 50, "Rendered PNG must contain painted text, controls and background, not a blank image.");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                Assert(hashes.Add(hash), "Theme/scale render unexpectedly matches a previous PNG.");
                Checks.Add($"{theme} {scale * 100:0}%: button/text bounds, release notes wrapping, populated Skia PNG");
                Console.WriteLine($"PASS {theme} {scale * 100:0}% ({frame.PixelSize.Width}x{frame.PixelSize.Height}, {pixelColors.Count} colors)");
                results.Add(new { Theme = theme, ApplicationZoom = scale, File = filename, Width = frame.PixelSize.Width, Height = frame.PixelSize.Height, Colors = pixelColors.Count, GreenIconPixels = greenPixels, Buttons = buttons });
                vm.Status = "Downloading release archive — 42 MB / 80 MB";
                vm.IsBusy = true;
                vm.Progress = 52.5;
                vm.IsIndeterminate = false;
                Dispatcher.UIThread.RunJobs();
                ValidateLayout(window);
                Assert(window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && (button.Classes.Contains("flat") || button.Classes.Contains("caption_button"))).All(button => !button.IsEnabled), "Busy dialog must disable update, skip and close.");
                var progress = window.GetVisualDescendants().OfType<ProgressBar>().Single();
                Assert(progress.IsEffectivelyVisible && progress.Value == 52.5 && !progress.IsIndeterminate, "Busy progress must be visible and reflect download progress.");
                window.Close();
                Assert(window.IsVisible, "Busy dialog must reject closing.");
                vm.IsBusy = false;
                window.Close();
            }
            TestBootstrap(app, output, results, hashes);
            Apply(app, "Dark", "en_US");
            TestChoice(UpdateChoice.Update, 0);
            TestChoice(UpdateChoice.Skip, 1);
            TestEscape();
            TestPreferences(app);
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                Renderer = "Avalonia.Headless 11.3.22 + software Skia",
                Scaling = "Application zoom via the actual UpdateWindow LayoutTransformControl; native monitor DPI is not simulated.",
                Checks,
                Renders = results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {Checks.Count} UI checks; {results.Count} rendered PNGs and report.json in {output}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void TestBootstrap(App app, string output, List<object> results, HashSet<string> hashes)
    {
        foreach (var theme in new[] { "Dark", "Light" })
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            Apply(app, theme, "uk_UA");
            var vm = new UpdateViewModel();
            Assert(vm.IsFirstInstall && vm.Release is null && !vm.HasRelease && vm.IsBusy && vm.CanCancel && !vm.CanChoose,
                "Bootstrap must start busy, cancellable and without invented release metadata.");
            var changed = new HashSet<string>();
            vm.PropertyChanged += (_, change) => { if (change.PropertyName is { } property) changed.Add(property); };
            var window = new UpdateWindow(vm, scale);
            var cancellationCount = 0;
            window.CancelRequested += () => { cancellationCount++; vm.CanCancel = false; };
            vm.Status = "Перевіряємо останній стабільний реліз…";
            window.Show();
            Dispatcher.UIThread.RunJobs();
            ValidateBootstrap(window, hasRelease: false, canCancel: true);
            CaptureBootstrap(window, output, $"bootstrap-checking-{theme.ToLowerInvariant()}-{scale * 100:0}.png", theme, scale, "checking", results, hashes);
            vm.SetRelease(Release());
            vm.Status = "Завантаження офіційного пакета — 42 MB / 80 MB";
            vm.Progress = 52.5;
            vm.IsIndeterminate = false;
            Dispatcher.UIThread.RunJobs();
            Assert(vm.HasRelease && vm.Release is not null && vm.TagName == "v2026.23" && vm.Body == Release().Body,
                "Resolved bootstrap metadata must describe the actual release.");
            Assert(changed.Contains(nameof(UpdateViewModel.HasRelease)) && changed.Contains(nameof(UpdateViewModel.Body)),
                "SetRelease must notify bindings when bootstrap metadata arrives.");
            ValidateBootstrap(window, hasRelease: true, canCancel: true);
            var editor = window.FindControl<TextEditor>("ReleaseNotes")!;
            Assert(editor.Text == vm.Body && editor.Document.LineCount > 30, "Bootstrap release notes must update after metadata resolves.");
            CaptureBootstrap(window, output, $"bootstrap-downloading-{theme.ToLowerInvariant()}-{scale * 100:0}.png", theme, scale, "downloading", results, hashes);
            var progress = window.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert(progress.Value == 52.5 && !progress.IsIndeterminate, "Bootstrap must display received download progress.");
            vm.CanCancel = false;
            vm.Status = "Встановлення SourceGit…";
            Dispatcher.UIThread.RunJobs();
            ValidateBootstrap(window, hasRelease: true, canCancel: false);
            window.Close();
            Assert(window.IsVisible && cancellationCount == 0, "Commit must reject close without requesting cancellation.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert(window.IsVisible && cancellationCount == 0, "Commit must ignore Escape.");
            window.Finish();
            Assert(!window.IsVisible, "Finish must close a busy bootstrap only after the worker has finished cleanup.");
            Checks.Add($"Bootstrap {theme} {scale * 100:0}%: pending/resolved metadata, progress, cancellation gate, credit footer and Skia PNGs");
            Console.WriteLine($"PASS bootstrap {theme} {scale * 100:0}% checking/download/commit states");
        }
        Apply(app, "Dark", "en_US");
        foreach (var action in new[] { "button", "close", "escape" })
        {
            var vm = new UpdateViewModel();
            var window = new UpdateWindow(vm);
            var cancellationCount = 0;
            window.CancelRequested += () => { cancellationCount++; vm.CanCancel = false; };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            if (action == "button") window.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (action == "close") window.Close();
            else window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert(cancellationCount == 1 && !vm.CanCancel && window.IsVisible,
                $"Bootstrap {action} must request cancellation once and remain visible until cleanup ends.");
            window.Close();
            Assert(cancellationCount == 1 && window.IsVisible, "A second close must not request cancellation during cleanup.");
            window.Finish();
            Assert(!window.IsVisible, "Cancelled bootstrap must close after Finish.");
            Checks.Add($"Bootstrap cancellation via {action} waits for cleanup");
            Console.WriteLine($"PASS bootstrap {action} cancellation lifecycle");
        }
    }

    private static void ValidateBootstrap(UpdateWindow window, bool hasRelease, bool canCancel)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var name in new[] { "ReleaseHeader", "ReleaseMetadata", "ReleaseNotesPanel" })
            Assert(window.FindControl<Control>(name)!.IsEffectivelyVisible == hasRelease,
                $"Bootstrap {name} visibility must follow real metadata availability.");
        Assert(window.FindControl<Control>("BootstrapHeading")!.IsEffectivelyVisible, "Bootstrap must explain its initial installation state.");
        Assert(!window.FindControl<Control>("ChoiceButtons")!.IsEffectivelyVisible, "Bootstrap must never offer Skip or Update choices.");
        var cancel = window.FindControl<Button>("CancelButton")!;
        Assert(cancel.IsEffectivelyVisible && cancel.IsEnabled == canCancel, "Bootstrap cancellation must remain available before commit only.");
        var progress = window.GetVisualDescendants().OfType<ProgressBar>().Single();
        Assert(progress.IsEffectivelyVisible, "Bootstrap progress must be visible even before metadata arrives.");
        foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && button.Classes.Contains("caption_button")))
            Assert(button.IsEnabled == canCancel, "Bootstrap caption close must follow cancellation availability.");
        ValidateAttribution(window);
        var viewport = new Rect(window.ClientSize);
        foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && (control is TextBlock || control is Button button && (button.Classes.Contains("flat") || button.Classes.Contains("caption_button") || button.Classes.Contains("attribution")))))
        {
            var transform = control.TransformToVisual(window) ?? throw new InvalidOperationException("Bootstrap control is detached.");
            var bounds = new Rect(control.Bounds.Size).TransformToAABB(transform);
            Assert(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= viewport.Right + .5 && bounds.Bottom <= viewport.Bottom + .5,
                $"Bootstrap {control.GetType().Name} is clipped: {bounds}, viewport {viewport}.");
            if (control is TextBlock text && !string.IsNullOrEmpty(text.Text))
                Assert(text.TextLayout.Width <= text.Bounds.Width + 1, $"Bootstrap text is horizontally clipped: {text.Text}");
        }
    }

    private static void ValidateAttribution(UpdateWindow window)
    {
        var link = window.FindControl<Button>("AttributionLink")!;
        Assert(link.IsEffectivelyVisible && link.IsEnabled, "The SourceGit attribution link must remain visible and usable in all states.");
        Assert(link.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("SourceGit", StringComparison.Ordinal) == true),
            "The attribution must identify the original SourceGit project.");
    }

    private static void CaptureBootstrap(UpdateWindow window, string output, string filename, string theme, double scale, string state, List<object> results, HashSet<string> hashes)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Bootstrap frame did not render.");
        var path = Path.Combine(output, filename);
        frame.Save(path);
        Assert(frame.PixelSize.Width >= 500 * scale && frame.PixelSize.Height >= 100 * scale, "Bootstrap frame is smaller than its intended content.");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Assert(hashes.Add(hash), "Bootstrap theme, scale and state must produce distinct rendered frames.");
        results.Add(new { Theme = theme, ApplicationZoom = scale, State = state, File = filename, Width = frame.PixelSize.Width, Height = frame.PixelSize.Height });
    }
    private static ReleaseInfo Release() => new("v2026.23", new Version(2026,23,0,0), new DateTimeOffset(2026,10,27,10,30,0,TimeSpan.Zero),
        "## What's Changed\n\n" + string.Join("\n", Enumerable.Range(1,30).Select(index => $"* Update {index}: preserve repository settings, improve history and safely install official release packages.")) + "\n\nhttps://github.com/sourcegit-scm/sourcegit/releases/tag/v2026.23",
        new ReleaseAsset("sourcegit_2026.23.win-x64.zip", new Uri("https://github.com/sourcegit-scm/sourcegit/releases/download/v2026.23/sourcegit_2026.23.win-x64.zip"), 1, null));

    private static void Apply(App app, string theme, string locale)
    {
        var method = typeof(App).GetMethod("ApplyPreferences", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(app, [new JsonObject { ["Theme"] = theme, ["Locale"] = locale, ["DefaultFontSize"] = 13.0 }, null]);
        Dispatcher.UIThread.RunJobs();
    }

    private static object[] ValidateLayout(UpdateWindow window, int expectedButtons = 3)
    {
        Dispatcher.UIThread.RunJobs();
        var viewport = new Rect(window.ClientSize);
        var buttons = window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && (button.Classes.Contains("flat") || button.Classes.Contains("caption_button"))).ToArray();
        Assert(buttons.Length == expectedButtons, "The dialog should expose update and skip buttons, plus a custom close button when the system frame is disabled.");
        foreach (var control in buttons.Cast<Control>().Concat(window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible)))
        {
            var matrix = control.TransformToVisual(window) ?? throw new Exception("Control is detached from its window.");
            var bounds = new Rect(control.Bounds.Size).TransformToAABB(matrix);
            Assert(bounds.Width > 0 && bounds.Height > 0, $"Visible {control.GetType().Name} has no laid out bounds.");
            Assert(bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= viewport.Right + .5 && bounds.Bottom <= viewport.Bottom + .5,
                $"{control.GetType().Name} is clipped outside window: {bounds}, viewport {viewport}.");
            if (control is TextBlock text && !string.IsNullOrEmpty(text.Text))
                Assert(text.TextLayout.Width <= text.Bounds.Width + 1, $"Text is horizontally clipped: {text.Text}");
        }
        ValidateAttribution(window);
        var tag = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "v2026.23");
        Assert(tag.Bounds.Width > 40, "New release version must be visible.");
        var notes = window.FindControl<TextEditor>("ReleaseNotes")!;
        Assert(notes.WordWrap && notes.IsReadOnly && notes.Document.LineCount > 30, "Release notes must be populated, wrapped and read-only.");
        Assert(notes.Bounds.Height > 200 && notes.Bounds.Width >= 490, "Release notes viewport must retain its intended readable dimensions.");
        return buttons.Select(button => (object)new { Classes = button.Classes.ToString(), Width = button.Bounds.Width, Height = button.Bounds.Height }).ToArray();
    }

    private static void TestChoice(UpdateChoice expected, int buttonIndex)
    {
        var window = new UpdateWindow(new UpdateViewModel(Release(), new Version(2026,22)));
        var choice = window.ChooseAsync();
        Dispatcher.UIThread.RunJobs();
        var buttons = window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && button.Classes.Contains("flat")).ToArray();
        buttons[buttonIndex].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(choice.IsCompletedSuccessfully && choice.Result == expected, $"Button should select {expected}.");
        window.Close();
        Checks.Add($"{expected} button selects correct action");
        Console.WriteLine("PASS " + expected + " button action");
    }

    private static void TestPreferences(App app)
    {
        var method = typeof(App).GetMethod("ApplyPreferences", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(app, [new JsonObject
        {
            ["Theme"] = "Light", ["Locale"] = "en_US", ["DefaultFontSize"] = 13.0,
            ["DefaultFontFamily"] = "Segoe UI", ["MonospaceFontFamily"] = "Consolas", ["UseSystemWindowFrame"] = true
        }, null]);
        var window = new UpdateWindow(new UpdateViewModel(Release(), new Version(2026,22)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert(window.SystemDecorations == SystemDecorations.Full, "SourceGit system frame preference must enable native window decorations.");
        Assert(window.FindControl<Grid>("CustomTitleBar")!.IsVisible == false, "Custom title bar must be hidden when using the system frame.");
        Assert(window.FontFamily.Name == "Segoe UI", "DefaultFontFamily preference must be applied to the window.");
        Assert(window.FindControl<TextEditor>("ReleaseNotes")!.FontFamily.Name == "Consolas", "MonospaceFontFamily preference must be applied to release notes.");
        ValidateLayout(window, expectedButtons: 2);
        window.Close();
        Checks.Add("System window frame, custom default font and monospace font preferences");
        Console.WriteLine("PASS system frame and custom font preferences");
    }

    private static void TestEscape()
    {
        var window = new UpdateWindow(new UpdateViewModel(Release(), new Version(2026,22)));
        var choice = window.ChooseAsync();
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert(choice.IsCompletedSuccessfully && choice.Result == UpdateChoice.Launch && !window.IsVisible, "Escape must launch current version without setting Skip.");
        Checks.Add("Escape selects Launch without Skip");
        Console.WriteLine("PASS Escape action");
    }
}



