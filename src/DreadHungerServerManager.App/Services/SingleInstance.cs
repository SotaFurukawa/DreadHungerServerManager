using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DreadHungerServerManager.App.Services;

/// <summary>
/// 同じデータフォルダーを使うアプリを1つに限る（2つ開くと、両方からサーバーを起動・停止できてしまうため）。
/// </summary>
internal static partial class SingleInstance
{
    private const int SW_RESTORE = 9;

    public static bool TryAcquire(string dataDirectory, out Mutex? mutex)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..16];
        mutex = new Mutex(initiallyOwned: true, $@"Local\DreadHungerServerManager-{hash}", out var createdNew);
        if (createdNew)
        {
            return true;
        }

        mutex.Dispose();
        mutex = null;
        return false;
    }

    public static void ActivateExistingWindow(string title)
    {
        var window = FindWindowW(null, title);
        if (window != IntPtr.Zero)
        {
            ShowWindow(window, SW_RESTORE);
            SetForegroundWindow(window);
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string? className, string windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);
}
