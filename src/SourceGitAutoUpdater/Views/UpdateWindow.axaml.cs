using System.Diagnostics;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.TextMate;
using SourceGitAutoUpdater.ViewModels;
using TextMateSharp.Grammars;

namespace SourceGitAutoUpdater.Views;

public enum UpdateChoice { Launch, Update, Skip }

public partial class UpdateWindow : Window
{
    private readonly TaskCompletionSource<UpdateChoice> _choice = new();
    private TextMate.Installation? _textMate;
    private bool _finished;
    public event Action? CancelRequested;
    public UpdateViewModel ViewModel => (UpdateViewModel)DataContext!;
    public UpdateWindow() { InitializeComponent(); }
    public UpdateWindow(UpdateViewModel vm, double zoom = 1) : this()
    {
        DataContext = vm;
        if (Application.Current!.Resources.TryGetResource("UseSystemWindowFrame", null, out var systemFrame) && systemFrame is true)
        {
            SystemDecorations = SystemDecorations.Full;
            BorderThickness = new Thickness(0);
            CustomTitleBar.IsVisible = false;
            if (Content is Grid grid) grid.RowDefinitions[0].Height = new GridLength(0);
        }
        ReleaseNotes.Text = vm.Body;
        vm.PropertyChanged += ViewModelChanged;
        if (Content is Control content && zoom != 1)
        {
            Content = null;
            Content = new LayoutTransformControl { LayoutTransform = new Avalonia.Media.ScaleTransform(zoom, zoom), Child = content };
        }
        ReleaseNotes.TextArea.TextView.Options.EnableHyperlinks = false;
        ReleaseNotes.TextArea.TextView.Options.EnableEmailHyperlinks = false;
        ReleaseNotes.TextArea.TextView.Margin = new Thickness(4, 0);
        Opened += (_, _) =>
        {
            var theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;
            var options = new RegistryOptions(theme);
            _textMate = ReleaseNotes.InstallTextMate(options);
            _textMate.SetGrammar(options.GetScopeByLanguageId(options.GetLanguageByExtension(".md").Id));
        };
        Closing += (_, e) =>
        {
            if (_finished) return;
            if (ViewModel.IsFirstInstall)
            {
                e.Cancel = true;
                RequestCancellation();
            }
            else if (ViewModel.IsBusy) e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= ViewModelChanged;
            _textMate?.Dispose();
            _choice.TrySetResult(UpdateChoice.Launch);
        };
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateViewModel.Body)) ReleaseNotes.Text = ViewModel.Body;
    }
    public void Finish() { _finished = true; Close(); }
    public Task<UpdateChoice> ChooseAsync() { Show(); return _choice.Task; }
    private void BeginMoveWindow(object? sender, PointerPressedEventArgs e) { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); }
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
    private void Update(object? sender, RoutedEventArgs e) { if (ViewModel.CanChoose) _choice.TrySetResult(UpdateChoice.Update); }
    private void Skip(object? sender, RoutedEventArgs e) { if (ViewModel.CanChoose) _choice.TrySetResult(UpdateChoice.Skip); }
    private void Cancel(object? sender, RoutedEventArgs e) => RequestCancellation();
    private void RequestCancellation()
    {
        if (!ViewModel.IsFirstInstall || !ViewModel.CanCancel) return;
        ViewModel.CanCancel = false;
        ViewModel.Status = Texts.T("Cancelling…", "Скасування…");
        CancelRequested?.Invoke();
    }
    private void OpenSourceGit(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/sourcegit-scm/sourcegit") { UseShellExecute = true }); }
        catch { /* A browser error must not interrupt installation. */ }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (ViewModel.IsFirstInstall) RequestCancellation();
            else if (!ViewModel.IsBusy) Close();
        }
    }
}
