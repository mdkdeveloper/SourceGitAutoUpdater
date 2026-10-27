using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceGitAutoUpdater.Windows;

/// <summary>One helper per managed installation; other invocations forward their original arguments and directory.</summary>
public sealed class LauncherInstanceBroker : IDisposable
{
    private const int MaximumMessageLength = 1024 * 1024;
    private readonly string _pipeName;
    private readonly FileStream? _ownership;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private bool _disposed;

    public bool IsPrimary => _ownership is not null;
    public event Action<string[], string>? LaunchRequested;
    public Action<string>? Diagnostic { get; set; }

    public LauncherInstanceBroker(string root)
    {
        var canonical = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var identity = Environment.UserName + "|" + canonical;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        _pipeName = "SourceGitAutoUpdater_" + hash;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceGitAutoUpdater", "instances");
        Directory.CreateDirectory(directory);
        try
        {
            _ownership = new FileStream(Path.Combine(directory, hash + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            // Windows sharing/lock violations mean another helper owns this installation.
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary && _listener is null)
            _listener = Task.Run(ListenAsync);
    }

    public async Task ForwardAsync(string[] args, string cwd, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        var message = JsonSerializer.Serialize(new LaunchMessage(args, cwd));
        if (message.Length > MaximumMessageLength)
            throw new ArgumentException("The forwarded launch request is too large.", nameof(args));
        await writer.WriteLineAsync(message.AsMemory(), timeout.Token).ConfigureAwait(false);
        await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
                // Read incrementally, with a hard bound, instead of allowing an arbitrary peer allocation.
                var line = new StringBuilder();
                var buffer = new char[1024];
                var finished = false;
                while (!finished)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), connectionTimeout.Token).ConfigureAwait(false);
                    if (count == 0)
                        break;
                    for (var index = 0; index < count; ++index)
                    {
                        if (buffer[index] == '\n') { finished = true; break; }
                        if (buffer[index] != '\r') line.Append(buffer[index]);
                        if (line.Length > MaximumMessageLength)
                            throw new InvalidDataException("The forwarded launch request is too large.");
                    }
                }
                if (line.Length == 0)
                    continue;
                var request = JsonSerializer.Deserialize<LaunchMessage>(line.ToString());
                if (request?.Arguments is null || request.Arguments.Any(argument => argument is null) ||
                    string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Path.IsPathFullyQualified(request.WorkingDirectory))
                    throw new InvalidDataException("The forwarded launch request is invalid.");
                LaunchRequested?.Invoke(request.Arguments, request.WorkingDirectory);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                try { Diagnostic?.Invoke($"Launch broker: {error.Message}"); }
                catch { }
                // Back off after errors so failed pipe creation cannot spin indefinitely.
                try { await Task.Delay(100, _shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        // Keep the cancellation source alive while its listener unwinds; no UI-thread blocking wait.
        if (_listener is { } listener)
            _ = listener.ContinueWith(_ => _shutdown.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        else
            _shutdown.Dispose();
        _ownership?.Dispose();
        // Leave the empty lock file in place: deleting it introduces an ownership race.
    }

    private sealed record LaunchMessage(string[] Arguments, string WorkingDirectory);
}
