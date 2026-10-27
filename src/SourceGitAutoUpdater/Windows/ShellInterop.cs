using System.Runtime.InteropServices;
using System.Text;

namespace SourceGitAutoUpdater.Windows;

internal static class ShellInterop
{
    internal static readonly PropertyKey AppId = new(5);
    internal static readonly PropertyKey RelaunchCommand = new(2);
    internal static readonly PropertyKey RelaunchIcon = new(3);
    internal static readonly PropertyKey RelaunchDisplayName = new(4);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct PropertyKey
    {
        public readonly Guid FormatId;
        public readonly uint PropertyId;
        internal PropertyKey(uint id)
        {
            FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
            PropertyId = id;
        }
    }

    // Both supported targets are 64-bit. PROPVARIANT has an eight-byte header and 16-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant : IDisposable
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal IntPtr Pointer;
        internal static PropVariant FromString(string value) => new() { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
        internal string? AsString() => Type == 31 && Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(Pointer) : null;
        public void Dispose() => PropVariantClear(ref this);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(in PropertyKey key, in PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    internal static void SetString(IPropertyStore store, PropertyKey key, string text)
    {
        using var value = PropVariant.FromString(text);
        Marshal.ThrowExceptionForHR(store.SetValue(in key, in value));
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    internal static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, in Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
