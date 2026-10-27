using System.Runtime.InteropServices;

namespace SourceGitAutoUpdater.Windows;

/// <summary>
/// Applies the launcher's taskbar identity to unmodified SourceGit windows.
/// Start, Refresh and Dispose must run on the same thread with a Windows message loop.
/// Out-of-process notifications can briefly lag window creation; no code is injected.
/// </summary>
public sealed class TaskbarIntegration : IDisposable
{
    public const string AppId = "SourceGitAutoUpdater.SourceGit";
    private static readonly ShellInterop.PropertyKey[] Keys =
        [ShellInterop.RelaunchCommand, ShellInterop.RelaunchDisplayName, ShellInterop.RelaunchIcon, ShellInterop.AppId];
    private readonly string _executable;
    private readonly string _launcher;
    private readonly Dictionary<IntPtr, SavedWindow> _windows = new();
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private int _ownerThread;
    private bool _disposed;

    public Action<string>? Diagnostic { get; set; }

    public TaskbarIntegration(string sourceGitExecutable, string launcherExecutable)
    {
        _executable = Path.GetFullPath(sourceGitExecutable);
        _launcher = Path.GetFullPath(launcherExecutable);
        _callback = OnWindowEvent;
    }

    public static void SetProcessIdentity()
    {
        if (OperatingSystem.IsWindows())
            Marshal.ThrowExceptionForHR(ShellInterop.SetCurrentProcessExplicitAppUserModelID(AppId));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows() || _hook != IntPtr.Zero)
            return;
        _ownerThread = Environment.CurrentManagedThreadId;
        _hook = SetWinEventHook(0x8000, 0x8003, IntPtr.Zero, _callback, 0, 0, 0x0002);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not observe SourceGit windows.");
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed || !OperatingSystem.IsWindows())
            return;
        CheckThread();
        // Reassert properties too: Explorer can restart while SourceGit remains open.
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
                TryAttach(hwnd);
            return true;
        }, IntPtr.Zero);
        foreach (var hwnd in _windows.Keys.ToArray())
        {
            if (!IsWindow(hwnd) || !HasExpectedOwner(hwnd, _windows[hwnd].ProcessId))
                Forget(hwnd);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        CheckThread();
        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        foreach (var hwnd in _windows.Keys.ToArray())
            Restore(hwnd);
        GC.KeepAlive(_callback);
    }

    private void CheckThread()
    {
        if (_ownerThread != 0 && _ownerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Taskbar integration must be used on its owning message-loop thread.");
    }

    private void OnWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || hwnd == IntPtr.Zero || objectId != 0 || childId != 0)
            return;
        try
        {
            if (eventType == 0x8001) // DESTROY is delivered after destruction; release our saved values.
                Forget(hwnd);
            else if (eventType == 0x8003) // Restore on HIDE where Windows delivers it before destruction.
                Restore(hwnd);
            else if (IsWindowVisible(hwnd))
                TryAttach(hwnd);
        }
        catch (Exception error) { Report(error); }
    }

    private void TryAttach(IntPtr hwnd)
    {
        ShellInterop.IPropertyStore? store = null;
        SavedWindow? created = null;
        try
        {
            if (GetAncestor(hwnd, 2) != hwnd)
                return;
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0 || !ProcessHost.MatchesExecutable((int)processId, _executable))
                return;
            if (_windows.TryGetValue(hwnd, out var previous) && previous.ProcessId != processId)
                Forget(hwnd);
            var interfaceId = typeof(ShellInterop.IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(ShellInterop.SHGetPropertyStoreForWindow(hwnd, in interfaceId, out store));
            if (!_windows.ContainsKey(hwnd))
            {
                created = new SavedWindow(processId);
                foreach (var key in Keys)
                {
                    Marshal.ThrowExceptionForHR(store.GetValue(in key, out var original));
                    created.Values.Add(original);
                }
                _windows.Add(hwnd, created);
            }
            // Relaunch metadata precedes ID so the first association already has a launcher target.
            ShellInterop.SetString(store, Keys[0], $"\"{_launcher}\"");
            ShellInterop.SetString(store, Keys[1], "SourceGit AutoUpdater");
            ShellInterop.SetString(store, Keys[2], $"\"{_launcher}\",0");
            ShellInterop.SetString(store, Keys[3], AppId);
            Marshal.ThrowExceptionForHR(store.GetValue(in ShellInterop.AppId, out var actual));
            using (actual)
            {
                if (actual.AsString() != AppId)
                    throw new InvalidOperationException("Windows did not retain SourceGit's taskbar identity.");
            }
        }
        catch (Exception error)
        {
            if (created is not null && !_windows.ContainsKey(hwnd))
                created.Dispose();
            if (_windows.ContainsKey(hwnd))
                Restore(hwnd);
            Report(error);
        }
        finally
        {
            if (store is not null && OperatingSystem.IsWindows())
                Marshal.FinalReleaseComObject(store);
        }
    }

    private bool HasExpectedOwner(IntPtr hwnd, uint expected)
    {
        GetWindowThreadProcessId(hwnd, out var actual);
        return actual == expected && ProcessHost.MatchesExecutable((int)actual, _executable);
    }

    private void Restore(IntPtr hwnd)
    {
        if (!_windows.Remove(hwnd, out var saved))
            return;
        ShellInterop.IPropertyStore? store = null;
        try
        {
            if (IsWindow(hwnd) && HasExpectedOwner(hwnd, saved.ProcessId))
            {
                var interfaceId = typeof(ShellInterop.IPropertyStore).GUID;
                Marshal.ThrowExceptionForHR(ShellInterop.SHGetPropertyStoreForWindow(hwnd, in interfaceId, out store));
                for (var i = 0; i < saved.Values.Count; ++i)
                {
                    var value = saved.Values[i];
                    Marshal.ThrowExceptionForHR(store.SetValue(in Keys[i], in value));
                }
            }
        }
        catch (Exception error) { Report(error); }
        finally
        {
            if (store is not null && OperatingSystem.IsWindows())
                Marshal.FinalReleaseComObject(store);
            saved.Dispose();
        }
    }

    private void Forget(IntPtr hwnd)
    {
        if (_windows.Remove(hwnd, out var saved))
            saved.Dispose();
    }

    private void Report(Exception error)
    {
        try { Diagnostic?.Invoke($"Taskbar integration: {error.Message}"); }
        catch { /* A logging callback must not escape a native window event. */ }
    }

    private sealed class SavedWindow(uint processId) : IDisposable
    {
        internal uint ProcessId { get; } = processId;
        internal List<ShellInterop.PropVariant> Values { get; } = [];
        public void Dispose()
        {
            foreach (var value in Values)
            {
                var owned = value;
                owned.Dispose();
            }
            Values.Clear();
        }
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumWindowsDelegate(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
