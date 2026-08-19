using System.IO;
using System.Threading;
using System.Windows;
using VpnHealthMonitor.Services;

namespace VpnHealthMonitor;

public partial class App : System.Windows.Application
{
    // Local\ — сеанс пользователя, а не вся машина: под другой учётной записью приложение имеет
    // право работать своим экземпляром, у него свой трей и свои настройки.
    private const string InstanceMutexName = @"Local\VpnHealthMonitor.SingleInstance";
    private const string ShowWindowEventName = @"Local\VpnHealthMonitor.ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowSignal;
    private RegisteredWaitHandle? _showWindowRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                // Второй экземпляр — это два трея, два цикла мониторинга и два писателя в один лог.
                // С автозапуском такое случается обычным образом: приложение подняла система,
                // человек кликнул по ярлыку. Поэтому не запускаемся, а поднимаем окно уже живого.
                SignalRunningInstance();
                Shutdown();
                return;
            }

            ShowFirstRunScreenIfNeeded();

            // Через Task.Run, а не напрямую: продолжение LoadAsync возвращается в диспетчер WPF, а тот
            // здесь заблокирован ожиданием — приложение зависало бы ещё до появления окна.
            var settings = Task.Run(() => new SettingsService().LoadAsync()).GetAwaiter().GetResult();

            var window = new MainWindow();
            MainWindow = window;

            if (settings.StartMinimizedToTray)
            {
                // Show() всё равно нужен: без него не отработает Window_Loaded, где приложение читает
                // настройки, поднимает трей и запускает мониторинг. Скрытая видимость плюс
                // ShowInTaskbar=false дают старт без окна и без кнопки в панели задач, без мигания.
                window.ShowInTaskbar = false;
                window.Visibility = Visibility.Hidden;
                window.Show();
            }
            else
            {
                window.Show();
            }

            RegisterShowWindowListener(window);
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

    protected override void OnExit(ExitEventArgs e)
    {
        _showWindowRegistration?.Unregister(null);
        _showWindowSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Разбудить уже работающий экземпляр. Сигнала нет — тот в процессе выхода, молча уходим.</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch
        {
            // Не поднять чужое окно — не повод показывать ошибку: повторный запуск и так ничего не делает.
        }
    }

    private void RegisterShowWindowListener(MainWindow window)
    {
        _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _showWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showWindowSignal,
            (_, _) => window.Dispatcher.BeginInvoke(new Action(window.RestoreFromTray)),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
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
