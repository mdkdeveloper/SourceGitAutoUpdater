using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGitAutoUpdater.Views;

public partial class MessageWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new();
    public MessageWindow() => InitializeComponent();
    public static Task<bool> AskAsync(string title, string message, string accept, string? decline = null)
    {
        var window = new MessageWindow { Title = title };
        window.Message.Text = message;
        window.Accept.Content = accept;
        window.Decline.Content = decline;
        window.Decline.IsVisible = decline is not null;
        window.Closed += (_, _) => window._result.TrySetResult(false);
        window.Show();
        return window._result.Task;
    }
    private void Yes(object? sender, RoutedEventArgs args) { _result.TrySetResult(true); Close(); }
    private void No(object? sender, RoutedEventArgs args) => Close();
    private void Move(object? sender, PointerPressedEventArgs args) { if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(args); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Escape) Close(); }
}
