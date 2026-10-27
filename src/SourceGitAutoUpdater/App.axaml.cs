using Avalonia.Controls;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using System.Text.Json.Nodes;

namespace SourceGitAutoUpdater;

public partial class App : Application
{
    private LauncherController? _controller;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _controller = new LauncherController(desktop);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = _controller.StartAsync());
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal void ApplyPreferences(JsonObject preferences, string[]? previewArguments = null)
    {
        string Get(string key, string fallback) => preferences[key]?.GetValue<string>() ?? fallback;
        string? Preview(string key) => previewArguments?.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        var theme = Preview("--theme") ?? Get("Theme", "Default");
        RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : theme == "Light" ? ThemeVariant.Light : ThemeVariant.Default;
        string locale = Preview("--locale") ?? Get("Locale", "en_US");
        Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(new Uri("avares://SourceGitAutoUpdater/Resources/Locales/en_US.axaml")));
        if (locale != "en_US")
        {
            try { Resources.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://SourceGitAutoUpdater/Resources/Locales/{locale}.axaml"))); }
            catch { locale = "en_US"; }
        }
        Texts.Locale = locale;
        if (preferences["DefaultFontFamily"]?.GetValue<string>() is { Length: > 0 } font)
            Resources["Fonts.Default"] = new Avalonia.Media.FontFamily(font);
        if (preferences["MonospaceFontFamily"]?.GetValue<string>() is { Length: > 0 } mono)
            Resources["Fonts.Monospace"] = new Avalonia.Media.FontFamily(mono);
        Resources["UseSystemWindowFrame"] = preferences["UseSystemWindowFrame"]?.GetValue<bool>() ?? false;
        Resources["Font.Size"] = Math.Clamp(preferences["DefaultFontSize"]?.GetValue<double>() ?? 13, 8, 32);
        if (Get("ThemeOverrides", "") is { Length: > 0 } overrides && File.Exists(overrides))
        {
            // SourceGit stores user-defined theme colors as JSON, never executable AXAML.
            try
            {
                var colors = JsonNode.Parse(File.ReadAllText(overrides))?["BasicColors"]?.AsObject();
                if (colors is not null)
                    foreach (var pair in colors)
                        if (Avalonia.Media.Color.TryParse(pair.Value?.ToString(), out var color))
                            Resources[pair.Key == "SystemAccentColor" ? pair.Key : $"Color.{pair.Key}"] = color;
            }
            catch { /* An invalid custom theme does not prevent launching SourceGit. */ }
        }
    }
}



