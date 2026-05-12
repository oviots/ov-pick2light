using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace P2L.CLI.Infrastructure;

[SupportedOSPlatform("windows")]
internal static class WindowsConsoleWindow
{
    private const int SwShowMinimized = 2;

    public static void MinimizeIfPresent()
    {
        var windowHandle = GetConsoleWindow();
        if (windowHandle != IntPtr.Zero)
            ShowWindow(windowHandle, SwShowMinimized);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
