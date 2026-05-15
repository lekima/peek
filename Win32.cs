using System.Runtime.InteropServices;

namespace Peek;

internal static class Win32
{
    public const uint WdaExcludeFromCapture = 0x00000011;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    public static int GetLastError() => Marshal.GetLastWin32Error();
}
