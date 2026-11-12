using System.Runtime.InteropServices;

namespace LiteRec.Core;

/// <summary>Llamadas nativas mínimas por sistema operativo.</summary>
internal static partial class NativeMethods
{
    // ---------- Windows ----------
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_F9 = 0x78;
    public const uint VK_F10 = 0x79;
    private const uint WDA_NONE = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    /// <summary>Hace que la ventana no aparezca en capturas (Windows 10 2004+).</summary>
    public static void ExcludeFromCapture(IntPtr hwnd, bool exclude)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        try { SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE); } catch { }
    }

    /// <summary>Libera páginas no usadas del working set.</summary>
    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    public static void TrimMemory(IntPtr hProcess)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { EmptyWorkingSet(hProcess); } catch { }
    }

    // ---------- Linux ----------
    public const int SIGINT = 2;
    public const int SIGUSR2 = 12;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int sys_kill(int pid, int sig);

    public static bool SendSignal(int pid, int sig)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return sys_kill(pid, sig) == 0; } catch { return false; }
    }
}
