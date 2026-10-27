using System.Runtime.InteropServices;

namespace SourceGitAutoUpdater.Windows;

/// <summary>Runs an editor/viewer child with taskbar integration and synchronous exit-code propagation.</summary>
public static class SpecialModeHost
{
    public static int Run(string sourceGitExecutable, string launcherExecutable, IEnumerable<string> args, string cwd)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SourceGit's Windows taskbar host requires Windows.");
        // Ensure this thread has a message queue before a very short-lived child can post WM_QUIT.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        var threadId = GetCurrentThreadId();
        using var integration = new TaskbarIntegration(sourceGitExecutable, launcherExecutable);
        var hooked = false;
        try { integration.Start(); hooked = true; }
        catch { /* Failure to group an editor window must never prevent Git's editor from running. */ }
        using var process = ProcessHost.Launch(sourceGitExecutable, args, cwd);
        if (process is null)
            return 1;
        if (!hooked)
        {
            process.WaitForExit();
            return process.ExitCode;
        }
        var timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 1000, IntPtr.Zero);
        var wait = Task.Run(() =>
        {
            try
            {
                process.WaitForExit();
                return process.ExitCode;
            }
            finally { PostThreadMessage(threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero); }
        });
        try
        {
            // Polling the completion task also permits recovery if Windows rejects the quit post.
            while (!wait.IsCompleted)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0)
                    break;
                if (message.Message == 0x0113 && (timer == UIntPtr.Zero || message.WParam == timer))
                {
                    try { integration.Refresh(); }
                    catch { /* Taskbar refresh must not terminate a running Git editor. */ }
                }
                else
                {
                    TranslateMessage(in message);
                    DispatchMessage(in message);
                }
            }
            return wait.GetAwaiter().GetResult();
        }
        finally
        {
            if (timer != UIntPtr.Zero)
                KillTimer(IntPtr.Zero, timer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MessageData
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out MessageData message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out MessageData message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(in MessageData message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(in MessageData message);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint interval, IntPtr callback);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr window, UIntPtr id);
}
