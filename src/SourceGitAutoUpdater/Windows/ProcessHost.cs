using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SourceGitAutoUpdater.Windows;

public static class ProcessHost
{
    public static bool HasManagedProcess(string executable) => GetManagedProcessIds(executable).Count != 0;

    public static IReadOnlyList<int> GetManagedProcessIds(string executable)
    {
        var expected = Path.GetFullPath(executable);
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (process)
            {
                try
                {
                    if (MatchesExecutable(process.Id, expected))
                        result.Add(process.Id);
                }
                catch (InvalidOperationException) { }
            }
        }
        return result;
    }

    public static Process? Launch(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return Process.Start(info);
    }

    internal static bool MatchesExecutable(int processId, string expected)
    {
        var actual = GetExecutablePath(processId);
        return actual is not null && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    }

    internal static string? GetExecutablePath(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var capacity = 32768;
            var buffer = new StringBuilder(capacity);
            return QueryFullProcessImageName(handle, 0, buffer, ref capacity) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
