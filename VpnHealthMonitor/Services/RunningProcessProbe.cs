using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Сколько процессов запущено из конкретного .exe. Нужен, чтобы не снять правило старой версии,
/// пока из неё ещё что-то работает: такой процесс сразу пошёл бы мимо VPN.
/// </summary>
public static class RunningProcessProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// Число процессов, запущенных из <paramref name="exePath"/>. Процесс с тем же именем, путь
    /// которого прочитать не удалось, тоже засчитывается: при сомнении правило лучше оставить.
    /// </summary>
    public static int CountPossiblyRunningFrom(string? exePath)
    {
        string target;
        try
        {
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return 0;
            }

            target = Path.GetFullPath(exePath);
        }
        catch
        {
            return 0;
        }

        var count = 0;
        foreach (var process in SafeGetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            using (process)
            {
                var image = TryGetImagePath(process.Id);
                if (image is null || string.Equals(image, target, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static Process[] SafeGetProcessesByName(string name)
    {
        try
        {
            return Process.GetProcessesByName(name);
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    private static string? TryGetImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(32768);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
