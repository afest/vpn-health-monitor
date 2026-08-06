using System.IO;
using System.Windows;
using VpnHealthMonitor.Services;

namespace VpnHealthMonitor;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            ShowFirstRunScreenIfNeeded();

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.ToString(),
                "VPN Health Monitor startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Показывает <see cref="FirstRunWindow"/> один раз, до первого запроса UAC, и пишет маркер вне
    /// зависимости от того, как окно закрыто (кнопка или Esc) — это информационный экран, не gate.
    /// </summary>
    private static void ShowFirstRunScreenIfNeeded()
    {
        if (File.Exists(AppPaths.FirstRunMarkerPath))
        {
            return;
        }

        new FirstRunWindow().ShowDialog();

        AppPaths.EnsureBaseDirectories();
        File.WriteAllText(AppPaths.FirstRunMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
    }
}
