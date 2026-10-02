using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Velopack;
using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VpnHealthMonitor;

public partial class MainWindow : Window
{
    private const int NotificationCooldownSeconds = 60;

    private enum TrayIconKind
    {
        Gray,
        Green,
        Yellow,
        Red
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly SettingsService _settingsService = new();
    private readonly UpdateService _updateService = new();
    private readonly NetworkCheckService _networkCheckService = new();
    private readonly LogService _logService = new();
    private readonly FirewallService _firewallService = new();
    private readonly AppxPathResolver _appxResolver = new();
    private readonly AdapterInventory _adapterInventory = new();
    private readonly AutostartService _autostartService = new();
    private readonly QuietStartGate _quietStart = new();
    private readonly RollingHealthWindow _rollingWindow = new(20);
    private readonly ConnectivityFailureFilter _failureFilter = new();
    private readonly ObservableCollection<MonitorEvent> _events = new();
    private readonly ObservableCollection<ProtectedAppRow> _protectedAppRows = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly Dictionary<TrayIconKind, Drawing.Icon> _trayIcons = new();
    private readonly Dictionary<string, bool> _lastKnownExists = new(StringComparer.OrdinalIgnoreCase);
    // Правила старых версий на экране: старый путь → (путь новой версии, занята ли старая). Таймер
    // сверяет, чтобы строка сменилась на «не нужна» сама, когда VS Code перезапустят.
    private readonly Dictionary<string, (string NewerPath, bool InUse)> _previousVersionInUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _protectedAppsTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _protectedAppsRefreshInFlight;
    private bool _protectedAppsWatchErrorShown;
    private HashSet<string> _notifiedPathChange = new(StringComparer.OrdinalIgnoreCase);
    private AdapterInventoryResult? _adapterInventoryResult;
    private RouteCheckContext _routeCheckContext = RouteCheckContext.Unknown;
    private readonly AdapterPresenceTracker _adapterPresence = new();
    private DateTimeOffset _lastAdapterRefreshAt = DateTimeOffset.MinValue;
    private int _adapterRefreshInFlight;

    // Потолок на один цикл проверки. Внутри ~4 с на пробу с запасом на веер и geo-lookup; всё, что
    // дольше, — это зависание, а не медленная сеть.
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _lastCompletedCheckAt;
    private DateTimeOffset? _monitoringStartedAt;
    private bool _staleChecksReported;

    // Провайдеры, по которым в этой сессии уже спросили и получили отказ: цикл идёт каждые несколько
    // секунд, и повторять вопрос по тому же выходу нельзя. Сбрасывается вместе с перезапуском.
    private readonly HashSet<string> _declinedProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _declinedCountries = new(StringComparer.OrdinalIgnoreCase);
    private bool _providerPromptOpen;
    private bool _countryPromptOpen;
    private NetworkSnapshot? _pendingProviderPrompt;

    private AppSettings _settings = new();
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private Forms.NotifyIcon? _trayIcon;
    private NetworkSnapshot? _lastSnapshot;
    private MonitorStatus _currentStatus = MonitorStatus.Unknown;
    private string _currentStatusDescription = string.Empty;
    private string? _lastIp;
    private string? _lastConfirmedVpnInterface;
    private DateTimeOffset? _lastSuccessfulCheckAt;
    private DateTimeOffset? _lastIpChangeAt;
    private DateTimeOffset? _lastNotificationAt;
    private DateTimeOffset? _healthySince;
    private DateTimeOffset? _problemStartedAt;
    private TimeSpan _totalProblemTime = TimeSpan.Zero;
    private int _incidentCount;
    private bool? _internetWasAvailable;
    private bool _exitRequested;
    private bool _uiReady;
    private bool _suppressStartupOptionEvents;
    private bool _networkWarningShown;
    private bool _eventsDetailed;

    public MainWindow()
    {
        InitializeComponent();
        EventsList.ItemsSource = _events;
        ProtectedAppsList.ItemsSource = _protectedAppRows;
        ApplyEventsViewMode(detailed: false);
        InitializeTrayIcon();
        _protectedAppsTimer.Tick += ProtectedAppsTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        UpdateStatusBanner(MonitorStatus.Unknown, "Готово. Запусти проверку или включи мониторинг.");
        UpdateDashboard(null, null);
        UpdateAdminStatus();
        await RefreshProtectedAppsAsync(logIssues: false);
        await RefreshAdapterChoicesAsync();
        UpdateDashboard(_lastSnapshot, null);
        _uiReady = true;
        _protectedAppsTimer.Start();

        // Адаптеры появляются и исчезают уже после старта: VPN-служба поднимается позже логона, Wi-Fi
        // моргает, TUN пересоздаётся на реконнекте. Снимок, снятый один раз в Loaded, этого не видит.
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        if (_settings.StartMonitoringOnLaunch)
        {
            // Тихий старт заводится ТОЛЬКО здесь: ручной «Старт мониторинга» его не включает,
            // там человек смотрит на экран и ждёт ответа сразу.
            _quietStart.Begin(DateTimeOffset.Now);
            await StartMonitoringAsync("мониторинг запущен автоматически при старте приложения");
        }

        // Обновления ищем в фоне и не ждём: проверка VPN важнее и не должна упираться в сеть GitHub.
        _ = CheckUpdatesQuietlyAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested && _settings.MinimizeToTrayOnClose && _monitoringCts is not null)
        {
            e.Cancel = true;
            Hide();
            ShowNotification(
                "VPN Health Monitor",
                "Окно скрыто в tray, мониторинг продолжает работу.",
                Forms.ToolTipIcon.Info,
                bypassCooldown: true,
                respectQuietStart: false);
            return;
        }

        _monitoringCts?.Cancel();
        _protectedAppsTimer.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        if (_lastSnapshot is not null)
        {
            var closeEvent = CreateEvent("приложение закрыто", _currentStatus, _lastSnapshot);
            try
            {
                _logService.WriteEvent(closeEvent, _settings);
            }
            catch
            {
                // Closing must never keep the WPF UI alive.
            }
        }

        DisposeTrayIcon();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitoringCts is not null)
        {
            return;
        }

        await SaveSettingsFromUiAsync();

        // Ручной запуск тихим стартом не глушится, даже если тот идёт с автозапуска: человек нажал
        // кнопку и ждёт ответа сейчас.
        _quietStart.Cancel();
        await StartMonitoringAsync("мониторинг запущен");
    }

    /// <summary>
    /// Галка «запускать вместе с Windows» правит реестр сразу: она описывает состояние системы, а не
    /// значение в файле, и расхождение между галкой и HKCU было бы враньём в интерфейсе.
    /// </summary>
    private async void LaunchWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _suppressStartupOptionEvents)
        {
            return;
        }

        var wanted = LaunchWithWindowsCheckBox.IsChecked == true;

        try
        {
            _autostartService.Apply(wanted);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Не удалось изменить автозапуск: {ex.Message}",
                "VPN Health Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            // Галку возвращаем к тому, что реально в реестре, а не к тому, что нажали.
            _suppressStartupOptionEvents = true;
            try
            {
                LaunchWithWindowsCheckBox.IsChecked = _autostartService.IsEnabled();
            }
            finally
            {
                _suppressStartupOptionEvents = false;
            }

            UpdateAutostartHint();
            return;
        }

        UpdateAutostartHint();
        await SaveSettingsFromUiAsync();
    }

    /// <summary>
    /// «Сразу включать мониторинг» и «стартовать свёрнутым» сохраняются по клику, без кнопки
    /// «Сохранить настройки»: проверяются они перезагрузкой, и молча не сохранённая галка выглядит
    /// как сломанная функция.
    /// </summary>
    private async void StartupOptionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _suppressStartupOptionEvents)
        {
            return;
        }

        await SaveSettingsFromUiAsync();
    }

    /// <summary>Что сейчас в HKCU: подпись под галкой отвечает на «а оно точно прописалось?».</summary>
    private void UpdateAutostartHint()
    {
        if (_autostartService.IsEnabled())
        {
            AutostartHintText.Text = $"В реестре: HKCU\\{AutostartService.RunKeyPath} → «{AutostartService.ValueName}» = {AutostartService.BuildCommand(AutostartService.CurrentExecutablePath)}";
            return;
        }

        AutostartHintText.Text = _autostartService.IsRegisteredForOtherPath()
            ? $"В реестре есть запись «{AutostartService.ValueName}», но она указывает на другой файл — вероятно, приложение переставили. Включи галку заново, чтобы перезаписать её на текущий .exe."
            : $"Записи в HKCU\\{AutostartService.RunKeyPath} нет — Windows приложение сама не запускает.";
    }

    /// <summary>Общий запуск цикла: кнопкой и автоматически при старте приложения.</summary>
    private async Task StartMonitoringAsync(string description)
    {
        if (_monitoringCts is not null)
        {
            return;
        }

        ResetSessionStats();

        _monitoringCts = new CancellationTokenSource();
        SetMonitoringState(true);

        _monitoringStartedAt = DateTimeOffset.Now;
        _lastCompletedCheckAt = null;
        _staleChecksReported = false;

        await AddEventAsync(description, _currentStatus, _lastSnapshot, CancellationToken.None);
        _monitoringTask = MonitorLoopAsync(_monitoringCts.Token);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopMonitoringAsync("мониторинг остановлен");
    }

    private async void RunCheckButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsFromUiAsync();

        // Ручная проверка обязана сходить в сеть заново: её жмут, когда картинке уже не доверяют.
        // Без сброса пул соединений и кэш geo подтверждают прежний вердикт по мёртвому маршруту.
        _networkCheckService.ResetNetworkState();
        await RunSingleCheckAsync("manual-check", CancellationToken.None);
    }

    /// <summary>
    /// Три действия, которыми пользуются редко (baseline, папка логов, экспорт), живут в меню за «⋯»:
    /// в ряду остаются только старт/стоп/проверить. Сами обработчики не менялись.
    /// </summary>
    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private async void BaselineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSnapshot?.ExternalIPv4 is null)
        {
            System.Windows.MessageBox.Show(
                this,
                "Сначала запусти успешную проверку, потом сохрани текущий IP как baseline.",
                "VPN Health Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var suggestedAdapter = FindSuggestedVpnAdapter();
        VpnRouteMode selectedMode;

        if (suggestedAdapter is not null)
        {
            var choice = System.Windows.MessageBox.Show(
                this,
                $"Windows видит VPN-подобный адаптер:\n\n{suggestedAdapter}\n\n"
                + "Да — контролировать default route через этот адаптер.\n"
                + "Нет — настроить VPN без отдельного адаптера и контролировать выход по стране/ASN.\n"
                + "Отмена — ничего не менять.",
                "Как работает этот VPN?",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel)
            {
                return;
            }

            selectedMode = choice == MessageBoxResult.Yes
                ? VpnRouteMode.SeparateAdapter
                : VpnRouteMode.NoSeparateAdapter;
        }
        else
        {
            var choice = System.Windows.MessageBox.Show(
                this,
                "Отдельный VPN-адаптер в Windows не найден. Это нормально для Karing и других клиентов в proxy/WFP/redirect-режиме.\n\n"
                + "Настроить режим без отдельного адаптера? Монитор будет проверять реальный выход по стране и ASN/провайдеру.\n\n"
                + "Настройки изменятся только после нажатия «Да».",
                "VPN без отдельного адаптера",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (choice != MessageBoxResult.Yes)
            {
                return;
            }

            selectedMode = VpnRouteMode.NoSeparateAdapter;
        }

        _settings.RouteMode = selectedMode;
        _settings.TreatDefaultRouteChangeAsLeakRisk = true;

        if (selectedMode == VpnRouteMode.SeparateAdapter)
        {
            SetExpectedInterface(suggestedAdapter!);
        }
        else
        {
            SetExpectedInterface(string.Empty);

            if (!string.IsNullOrWhiteSpace(_lastSnapshot.Country))
            {
                _settings.ExpectedCountry = CountryNames.ToDisplayName(_lastSnapshot.Country);
                _settings.TreatCountryMismatchAsLeakRisk = true;
            }

            if (!ProviderMatcher.IsUnknown(_lastSnapshot.Asn, _lastSnapshot.Provider))
            {
                RememberProvider(_lastSnapshot.Asn, _lastSnapshot.Provider);
                _settings.TreatProviderChangeAsLeakRisk = true;
            }
        }

        _settings.Baseline = new BaselineInfo
        {
            IPv4 = _lastSnapshot.ExternalIPv4,
            Country = _lastSnapshot.Country,
            Provider = _lastSnapshot.Provider,
            Asn = _lastSnapshot.Asn,
            InterfaceName = selectedMode == VpnRouteMode.SeparateAdapter
                ? _settings.ExpectedInterfaceName
                : _lastSnapshot.InterfaceName,
            Timestamp = DateTimeOffset.Now
        };

        // Baseline = "вот так выглядит норма", поэтому текущий провайдер сразу становится разрешённым:
        // иначе включённый риск по провайдеру сработал бы на собственном же VPN сразу после настройки.
        RememberProvider(_lastSnapshot.Asn, _lastSnapshot.Provider);

        if (_settings.ExpectedPublicIPv4.Count > 0
            && !_settings.ExpectedPublicIPv4.Contains(_lastSnapshot.ExternalIPv4, StringComparer.OrdinalIgnoreCase))
        {
            _settings.ExpectedPublicIPv4.Add(_lastSnapshot.ExternalIPv4);
        }

        await _settingsService.SaveAsync(_settings);
        UpdateSettingsControls();
        await ReevaluateDashboardAsync("baseline-change");
        await AddEventAsync("baseline сохранен по текущему внешнему IPv4", _currentStatus, _lastSnapshot, CancellationToken.None);
    }

    private async void AddCurrentProviderButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();

        if (_lastSnapshot is null || ProviderMatcher.IsUnknown(_lastSnapshot.Asn, _lastSnapshot.Provider))
        {
            System.Windows.MessageBox.Show(this,
                "Провайдер текущего выхода неизвестен. Запусти проверку — и когда в блоке «Текущая сеть» появится провайдер, добавь его.",
                "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var label = ProviderMatcher.Describe(_lastSnapshot.Asn, _lastSnapshot.Provider);
        if (!RememberProvider(_lastSnapshot.Asn, _lastSnapshot.Provider))
        {
            FooterText.Text = $"Провайдер «{label}» уже в списке разрешённых.";
            return;
        }

        await _settingsService.SaveAsync(_settings);
        UpdateSettingsControls();
        await ReevaluateDashboardAsync("provider-settings-change");
        FooterText.Text = $"Провайдер «{label}» добавлен в разрешённые.";
    }

    /// <summary>Adds the provider to the allow-list unless an equivalent entry is already there. True if added.</summary>
    private bool RememberProvider(string? asn, string? provider)
    {
        if (ProviderMatcher.IsUnknown(asn, provider))
        {
            return false;
        }

        if (_settings.AllowedProviders.Any(allowed => ProviderMatcher.IsSameProvider(allowed.Asn, allowed.Name, asn, provider)))
        {
            return false;
        }

        _settings.AllowedProviders.Add(new ProviderIdentity { Asn = asn, Name = provider });
        return true;
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsFromUiAsync();
        FooterText.Text = $"Настройки сохранены: {AppPaths.SettingsPath}";
        await ReevaluateDashboardAsync("settings-change");
    }

    private async void ReloadSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        await ReevaluateDashboardAsync("settings-reload");
        await RefreshProtectedAppsAsync(logIssues: false);
    }

    private async void CheckUpdatesMenu_Click(object sender, RoutedEventArgs e)
    {
        // Ручная проверка всегда доводится до ответа: человек нажал кнопку и ждёт результата,
        // молчание он прочитает как поломку.
        CheckUpdatesButton.IsEnabled = false;
        FooterText.Text = "Проверяю обновления...";
        try
        {
            var info = await _updateService.CheckAsync(quiet: false);
            if (info is null)
            {
                FooterText.Text = "Обновлений нет — установлена последняя версия.";
                System.Windows.MessageBox.Show(this,
                    $"У тебя последняя версия ({UpdateService.CurrentVersion}).",
                    "Обновления", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ShowUpdateBanner(info);
            FooterText.Text = $"Доступно обновление {UpdateService.VersionOf(info)}.";
        }
        catch (Exception ex)
        {
            FooterText.Text = "Проверить обновления не удалось.";
            System.Windows.MessageBox.Show(this, ex.Message, "Обновления",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>Фоновая проверка при старте: молчит при любой ошибке, показывает плашку при находке.</summary>
    private async Task CheckUpdatesQuietlyAsync()
    {
        try
        {
            var info = await _updateService.CheckAsync(quiet: true, log => Debug.WriteLine(log));
            if (info is not null)
            {
                ShowUpdateBanner(info);
            }
        }
        catch
        {
            // Фоновая проверка не должна мешать работе монитора ничем.
        }
    }

    private void ShowUpdateBanner(UpdateInfo info)
    {
        UpdateBannerText.Text =
            $"Доступно обновление {UpdateService.VersionOf(info)} — сейчас {UpdateService.CurrentVersion}";

        var notes = UpdateService.NotesOf(info);
        UpdateBannerNotes.Text = notes;
        UpdateBannerNotes.Visibility = string.IsNullOrWhiteSpace(notes)
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void UpdateDismissButton_Click(object sender, RoutedEventArgs e)
        => UpdateBanner.Visibility = Visibility.Collapsed;

    private async void UpdateInstallButton_Click(object sender, RoutedEventArgs e)
    {
        var info = _updateService.Pending;
        if (info is null)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateInstallButton.IsEnabled = false;
        try
        {
            UpdateBannerText.Text = $"Скачиваю обновление {UpdateService.VersionOf(info)}...";
            await _updateService.DownloadAndApplyAsync(info, percent =>
                Dispatcher.Invoke(() =>
                    UpdateBannerText.Text = $"Скачиваю обновление {UpdateService.VersionOf(info)}... {percent}%"));

            // Досюда доходим только если перезапуск не случился.
            UpdateBannerText.Text = "Обновление загружено, перезапусти программу.";
        }
        catch (Exception ex)
        {
            UpdateBannerText.Text = "Обновиться не удалось.";
            System.Windows.MessageBox.Show(this,
                $"{ex.Message}\n\nМожно скачать вручную:\n{UpdateService.ReleasePageUrl(info)}",
                "Обновления", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateInstallButton.IsEnabled = true;
        }
    }

    // Перевод по номеру телефона (СБП, без комиссии) — тот же способ, что в SayType. Формулировка
    // везде «поддержать разработку», а не «купить»: программа бесплатна и такой остаётся, а обещание
    // чего-то взамен превратило бы дар в выручку.
    private const string DonatePhone = "+7 950 541 92 21";
    private const string DonatePhoneRaw = "+79505419221";

    private void DonateMenu_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            this,
            "VPN Health Monitor бесплатен и таким останется. Донат ничего не открывает "
            + "и ни к чему не обязывает — просто способ сказать спасибо.\n\n"
            + $"Перевод по номеру телефона (СБП, без комиссии):\n{DonatePhone}\n\n"
            + "Скопировать номер?",
            "Поддержать разработку",
            MessageBoxButton.YesNo,
            MessageBoxImage.None,
            MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Yes)
        {
            TrySetClipboard(DonatePhoneRaw, "Номер скопирован.");
        }
    }

    private void AboutMenu_Click(object sender, RoutedEventArgs e)
    {
        var installed = UpdateService.IsAvailable
            ? "Установленная версия — обновления работают."
            : "Запущено не через установщик: автообновление недоступно.";

        System.Windows.MessageBox.Show(
            this,
            $"VPN Health Monitor {UpdateService.CurrentVersion}\n\n"
            + "Проверяет, что VPN действительно работает: внешний IP, страна, провайдер, маршрут "
            + "по умолчанию. Умеет запретить выбранным программам выходить в интернет мимо VPN — "
            + "правилами брандмауэра Windows.\n\n"
            + installed + "\n\n"
            + UpdateService.RepositoryUrl,
            "О программе",
            MessageBoxButton.OK,
            MessageBoxImage.None);
    }

    private void TrySetClipboard(string text, string okMessage)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            FooterText.Text = okMessage;
        }
        catch (Exception ex)
        {
            // Буфер может быть занят другим процессом — это не повод падать.
            FooterText.Text = $"Не удалось скопировать: {ex.Message}";
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        Directory.CreateDirectory(_settings.LogsFolderPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = _settings.LogsFolderPath,
            UseShellExecute = true
        });
    }

    private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettingsFromUi();
            var exportPath = _logService.ExportCsv(_settings);
            FooterText.Text = $"CSV export: {exportPath}";
            System.Windows.MessageBox.Show(
                this,
                $"CSV сохранен:\n{exportPath}",
                "VPN Health Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "CSV export failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Компактный вид (Время · Статус · Описание · Страна · Ping) — повседневный; подробный добавляет
    /// IPv4, IPv6 и потери. Выбор пишется в настройки, поэтому переживает перезапуск.
    /// </summary>
    private async void EventsViewRadio_Checked(object sender, RoutedEventArgs e)
    {
        var detailed = DetailedViewRadio?.IsChecked == true;
        ApplyEventsViewMode(detailed);

        if (!_uiReady || _settings.EventsDetailedView == detailed)
        {
            return;
        }

        _settings.EventsDetailedView = detailed;
        await _settingsService.SaveAsync(_settings);
    }

    private void ApplyEventsViewMode(bool detailed)
    {
        if (EventsList?.View is not System.Windows.Controls.GridView grid)
        {
            return;
        }

        SetColumnVisible(grid, EventIpv4Column, detailed, 3);
        SetColumnVisible(grid, EventIpv6Column, detailed, 4);
        SetColumnVisible(grid, EventLossColumn, detailed, 7);
        _eventsDetailed = detailed;
        ResizeEventsDescriptionColumn();
    }

    /// <summary>
    /// «Описание» занимает всю ширину, не занятую остальными колонками: на минимальном размере окна
    /// фиксированная ширина уводила таблицу в горизонтальную прокрутку и прятала Ping.
    /// Ширина списка задаётся гридом сверху вниз, поэтому обратной связи в разметке не возникает.
    /// </summary>
    private void ResizeEventsDescriptionColumn()
    {
        // Время 78 + Статус 140 + Страна 110 + Ping 72; подробный добавляет IPv4 120, IPv6 140, Потери 72.
        var fixedWidth = _eventsDetailed ? 732d : 400d;
        var available = EventsList.ActualWidth - fixedWidth - 24;
        EventDescriptionColumn.Width = Math.Max(200, available);
    }

    private void EventsList_SizeChanged(object sender, SizeChangedEventArgs e)
        => ResizeEventsDescriptionColumn();

    /// <summary>
    /// «Путь» добирает остаток ширины: иначе таблица защищённых программ уезжает в горизонтальную
    /// прокрутку и колонка «Действия» оказывается за краем окна.
    /// </summary>
    private void ProtectedAppsList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Программа 170 + Статус 175 + Правила применены 140 + Действия 300.
        var available = ProtectedAppsList.ActualWidth - 785 - 24;
        ProtectedPathColumn.Width = Math.Max(160, available);
    }

    private static void SetColumnVisible(
        System.Windows.Controls.GridView grid,
        System.Windows.Controls.GridViewColumn column,
        bool visible,
        int index)
    {
        if (!visible)
        {
            grid.Columns.Remove(column);
            return;
        }

        if (!grid.Columns.Contains(column))
        {
            grid.Columns.Insert(Math.Min(index, grid.Columns.Count), column);
        }
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await _settingsService.LoadAsync();
        UpdateSettingsControls();
        FooterText.Text = $"Настройки: {AppPaths.SettingsPath} | Логи: {_settings.LogsFolderPath}";

        VersionText.Text = UpdateService.IsAvailable
            ? $"Версия {UpdateService.CurrentVersion}"
            : $"Версия {UpdateService.CurrentVersion} — запущено не через установщик, автообновление недоступно";
    }

    private async Task SaveSettingsFromUiAsync()
    {
        SaveSettingsFromUi();
        await _settingsService.SaveAsync(_settings);
        FooterText.Text = $"Настройки: {AppPaths.SettingsPath} | Логи: {_settings.LogsFolderPath}";
    }

    /// <summary>
    /// A saved setting can change the verdict even while monitoring is stopped. Re-evaluate the last
    /// measured snapshot immediately so the route details and the top banner can never contradict each other.
    /// </summary>
    private async Task ReevaluateDashboardAsync(string trigger)
    {
        if (_lastSnapshot is null)
        {
            UpdateDashboard(null, null);
            return;
        }

        var result = HealthEvaluator.Evaluate(_lastSnapshot, _rollingWindow, _settings, _routeCheckContext);
        var previousStatus = _currentStatus;
        _currentStatus = result.Status;

        if (previousStatus != result.Status)
        {
            UpdateSessionAccounting(previousStatus, result.Status, DateTimeOffset.Now);
            await AddStatusTransitionEventsAsync(previousStatus, result.Status, _lastSnapshot, CancellationToken.None);
            await AddEventAsync(
                $"статус пересчитан после изменения настроек ({trigger}): "
                + $"{previousStatus.ToDisplayText()} -> {result.Status.ToDisplayText()}. {result.Description}",
                result.Status,
                _lastSnapshot,
                CancellationToken.None);
            MaybeShowStatusNotification(previousStatus, result.Status, result.Description);
        }

        UpdateDashboard(_lastSnapshot, result);
    }

    private void SaveSettingsFromUi()
    {
        _settings.IntervalSeconds = ParseInt(IntervalTextBox.Text, 5, 1, 3600);
        _settings.ExpectedCountry = ExpectedCountryTextBox.Text.Trim();
        _settings.TreatCountryMismatchAsLeakRisk = CountryRiskCheckBox.IsChecked == true;
        _settings.ExpectedPublicIPv4 = SplitValues(ExpectedIpsTextBox.Text);
        _settings.TreatUnexpectedIPv4AsLeakRisk = IpRiskCheckBox.IsChecked == true;
        _settings.AllowIpChangesWithinExpectedCountry = AllowIpChangesCheckBox.IsChecked == true;
        _settings.RouteMode = GetSelectedRouteMode();
        SetExpectedInterface((ExpectedInterfaceBox.Text ?? string.Empty).Trim());
        _settings.AllowedVpnInterfaces = SplitLines(AllowedVpnInterfacesTextBox.Text);
        _settings.TreatDefaultRouteChangeAsLeakRisk = RouteRiskCheckBox.IsChecked == true;
        _settings.EnableIPv6LeakCheck = Ipv6CheckBox.IsChecked == true;
        _settings.AllowExternalIPv6 = AllowExternalIpv6CheckBox.IsChecked == true;
        _settings.IPv6ApiEndpoints = SplitValues(Ipv6ApiEndpointsTextBox.Text);
        _settings.TreatProviderChangeAsLeakRisk = ProviderRiskCheckBox.IsChecked == true;
        _settings.AllowedProviders = SplitLines(AllowedProvidersTextBox.Text)
            .Select(ProviderMatcher.ParseIdentity)
            .Where(identity => identity is not null)
            .Select(identity => identity!)
            .ToList();
        _settings.HttpProbeUrls = SplitValues(HttpProbesTextBox.Text);
        _settings.PingHosts = SplitValues(PingHostsTextBox.Text);
        _settings.EnableDnsCheck = DnsCheckBox.IsChecked == true;
        _settings.EnableWindowsNotifications = WindowsNotificationsCheckBox.IsChecked == true;
        _settings.NotifyCountryChanged = NotifyCountryChangedCheckBox.IsChecked == true;
        _settings.NotifyIpChanged = NotifyIpChangedCheckBox.IsChecked == true;
        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckBox.IsChecked == true;
        _settings.LaunchWithWindows = LaunchWithWindowsCheckBox.IsChecked == true;
        _settings.StartMonitoringOnLaunch = StartMonitoringOnLaunchCheckBox.IsChecked == true;
        _settings.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
        _settings.IpApiEndpoints = SplitValues(IpApiEndpointsTextBox.Text);
        _settings.DegradedPingThresholdMs = ParseInt(PingThresholdTextBox.Text, 250, 1, 10000);
        _settings.DegradedPacketLossThresholdPercent = ParseDouble(LossThresholdTextBox.Text, 5, 0, 100);
        _settings.LogsFolderPath = string.IsNullOrWhiteSpace(LogsFolderTextBox.Text)
            ? AppPaths.DefaultLogsFolder
            : LogsFolderTextBox.Text.Trim();
        _settings.AutosaveLogs = AutosaveLogsCheckBox.IsChecked == true;

        _settings = SettingsService.Normalize(_settings);
    }

    private void UpdateSettingsControls()
    {
        IntervalTextBox.Text = _settings.IntervalSeconds.ToString();
        ExpectedCountryTextBox.Text = _settings.ExpectedCountry;
        CountryRiskCheckBox.IsChecked = _settings.TreatCountryMismatchAsLeakRisk;
        ExpectedIpsTextBox.Text = string.Join(Environment.NewLine, _settings.ExpectedPublicIPv4);
        IpRiskCheckBox.IsChecked = _settings.TreatUnexpectedIPv4AsLeakRisk;
        AllowIpChangesCheckBox.IsChecked = _settings.AllowIpChangesWithinExpectedCountry;
        SetSelectedRouteMode(_settings.RouteMode ?? VpnRouteMode.SeparateAdapter);
        ExpectedInterfaceBox.Text = GetExpectedInterfaceName(_settings);
        AllowedVpnInterfacesTextBox.Text = string.Join(Environment.NewLine, _settings.AllowedVpnInterfaces);
        RouteRiskCheckBox.IsChecked = _settings.TreatDefaultRouteChangeAsLeakRisk;
        Ipv6CheckBox.IsChecked = _settings.EnableIPv6LeakCheck;
        AllowExternalIpv6CheckBox.IsChecked = _settings.AllowExternalIPv6;
        Ipv6ApiEndpointsTextBox.Text = string.Join(Environment.NewLine, _settings.IPv6ApiEndpoints);
        ProviderRiskCheckBox.IsChecked = _settings.TreatProviderChangeAsLeakRisk;
        AllowedProvidersTextBox.Text = string.Join(
            Environment.NewLine,
            _settings.AllowedProviders.Select(identity => ProviderMatcher.Describe(identity.Asn, identity.Name)));
        HttpProbesTextBox.Text = string.Join(Environment.NewLine, _settings.HttpProbeUrls);
        PingHostsTextBox.Text = string.Join(Environment.NewLine, _settings.PingHosts);
        DnsCheckBox.IsChecked = _settings.EnableDnsCheck;
        WindowsNotificationsCheckBox.IsChecked = _settings.EnableWindowsNotifications;
        NotifyCountryChangedCheckBox.IsChecked = _settings.NotifyCountryChanged;
        NotifyIpChangedCheckBox.IsChecked = _settings.NotifyIpChanged;
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTrayOnClose;
        // Источник истины для автозапуска — реестр, а не settings.json: приложение могли переставить
        // в другую папку, и запись указывала бы на старый путь. Галка показывает факт системы.
        _suppressStartupOptionEvents = true;
        try
        {
            _settings.LaunchWithWindows = _autostartService.IsEnabled();
            LaunchWithWindowsCheckBox.IsChecked = _settings.LaunchWithWindows;
            StartMonitoringOnLaunchCheckBox.IsChecked = _settings.StartMonitoringOnLaunch;
            StartMinimizedCheckBox.IsChecked = _settings.StartMinimizedToTray;
        }
        finally
        {
            _suppressStartupOptionEvents = false;
        }

        UpdateAutostartHint();
        IpApiEndpointsTextBox.Text = string.Join(Environment.NewLine, _settings.IpApiEndpoints);
        PingThresholdTextBox.Text = _settings.DegradedPingThresholdMs.ToString();
        LossThresholdTextBox.Text = _settings.DegradedPacketLossThresholdPercent.ToString("0.#");
        LogsFolderTextBox.Text = _settings.LogsFolderPath;
        AutosaveLogsCheckBox.IsChecked = _settings.AutosaveLogs;
        if (_settings.EventsDetailedView)
        {
            DetailedViewRadio.IsChecked = true;
        }
        else
        {
            CompactViewRadio.IsChecked = true;
        }

        // Явно: если нужный переключатель уже отмечен, событие Checked не придёт.
        ApplyEventsViewMode(_settings.EventsDetailedView);
        BaselineText.Text = FormatBaseline(_settings.Baseline);
        UpdateRouteModeControls();
    }

    private void RouteModeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateRouteModeControls();
    }

    private void ReconfigureRouteButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = SettingsTab;
        RouteModeBox.Focus();
    }

    private VpnRouteMode GetSelectedRouteMode()
    {
        if (RouteModeBox.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && Enum.TryParse<VpnRouteMode>(item.Tag?.ToString(), out var mode))
        {
            return mode;
        }

        return VpnRouteMode.SeparateAdapter;
    }

    private void SetSelectedRouteMode(VpnRouteMode mode)
    {
        foreach (var item in RouteModeBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.Ordinal))
            {
                RouteModeBox.SelectedItem = item;
                return;
            }
        }

        RouteModeBox.SelectedIndex = 0;
    }

    private void UpdateRouteModeControls()
    {
        if (ExpectedInterfaceBox is null || AllowedVpnInterfacesTextBox is null
            || RouteRiskCheckBox is null || RouteModeHintText is null)
        {
            return;
        }

        var usesAdapter = GetSelectedRouteMode() == VpnRouteMode.SeparateAdapter;
        ExpectedInterfaceBox.IsEnabled = usesAdapter;
        AllowedVpnInterfacesTextBox.IsEnabled = usesAdapter;
        RouteRiskCheckBox.IsEnabled = usesAdapter;
        RouteModeHintText.Text = usesAdapter
            ? "Если используешь несколько VPN, перечисли все их адаптеры ниже. Поле «Активный интерфейс» на Сводке остаётся фактом Windows."
            : "Default route останется на Wi-Fi/Ethernet. Маршрут не проверяется; включи риск по стране и/или ASN/провайдеру.";
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RunSingleCheckAsync("scheduled check", cancellationToken);
                await WarnIfChecksStalledAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(_settings.IntervalSeconds), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Сторож живости: если ни одна проверка не завершилась дольше допустимого, экран показывает
    /// прошлое, а не настоящее. Молча оставлять прежний вердикт нельзя — он читается как актуальный.
    /// </summary>
    private async Task WarnIfChecksStalledAsync(CancellationToken cancellationToken)
    {
        var limit = TimeSpan.FromSeconds(Math.Max(60, _settings.IntervalSeconds * 6));

        // Отсчёт от старта мониторинга, а не только от последней удачи: если зависла первая же
        // проверка, успешного замера не будет вообще, и от null сторож не должен молчать.
        var since = _lastCompletedCheckAt ?? _monitoringStartedAt;
        if (since is null || DateTimeOffset.Now - since.Value < limit)
        {
            _staleChecksReported = false;
            return;
        }

        if (_staleChecksReported)
        {
            return;
        }

        _staleChecksReported = true;
        var minutes = (DateTimeOffset.Now - since.Value).TotalMinutes;
        var description = $"Проверки не проходят {minutes:0} мин — данные на экране устарели, состояние VPN неизвестно.";

        var snapshot = new NetworkSnapshot
        {
            CheckedAt = DateTimeOffset.Now,
            Errors = new List<string> { description }
        };
        var result = new HealthResult
        {
            Status = MonitorStatus.CheckFailed,
            Description = description
        };

        await HandleResultAsync("watchdog", snapshot, result, cancellationToken);
        UpdateDashboard(snapshot, result);
    }

    private async Task StopMonitoringAsync(string description)
    {
        if (_monitoringCts is null)
        {
            return;
        }

        _quietStart.Cancel();
        _monitoringCts.Cancel();

        if (_monitoringTask is not null)
        {
            try
            {
                await _monitoringTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _monitoringCts.Dispose();
        _monitoringCts = null;
        _monitoringTask = null;
        SetMonitoringState(false);
        CloseActiveProblemWindow();
        UpdateSessionStats();

        await AddEventAsync(description, _currentStatus, _lastSnapshot, CancellationToken.None);
    }

    private async Task RunSingleCheckAsync(string trigger, CancellationToken cancellationToken)
    {
        if (!await _checkLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            SetBusyState(true);

            // Жёсткий потолок на цикл. Без него зависший сетевой вызов останавливает MonitorLoopAsync
            // навсегда: проверок нет, в лог пишется только смена статуса, и мёртвый монитор выглядит
            // ровно как монитор со стабильно плохим вердиктом (T-403: простой 4 часа на старой плашке).
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            checkCts.CancelAfter(CheckTimeout);
            var checkToken = checkCts.Token;

            var snapshot = await _networkCheckService.RunAsync(_settings, checkToken);
            _lastCompletedCheckAt = DateTimeOffset.Now;

            await MaybeRefreshAdapterInventoryAsync();

            // Провал связи определяется по самому замеру и настройкам, скользящее окно в нём не
            // участвует, поэтому классифицировать можно до того, как замер попал в окно (T-473).
            var result = HealthEvaluator.Evaluate(snapshot, _rollingWindow, _settings, _routeCheckContext);
            var failure = _failureFilter.Observe(
                result,
                snapshot,
                _settings,
                _lastIp,
                manual: string.Equals(trigger, "manual-check", StringComparison.OrdinalIgnoreCase));

            if (failure.Action == FailureFilterAction.Hold)
            {
                await NoteHeldFailureAsync(snapshot, result, cancellationToken);
            }
            else
            {
                // Придержанный замер в окно не попадает: иначе одиночный сбой со 100% потерь ещё
                // две минуты держит «СЕТЬ ПРОСЕЛА» — вторая ложная тревога от той же секунды.
                _lastSnapshot = snapshot;
                _rollingWindow.Add(snapshot);
                result = HealthEvaluator.Evaluate(snapshot, _rollingWindow, _settings, _routeCheckContext);

                if (failure.Reason == FailureConfirmation.Repeated)
                {
                    result = WithRepeatNote(result, failure.FailuresInWindow);
                }

                await HandleResultAsync(trigger, snapshot, result, cancellationToken);
                UpdateDashboard(snapshot, result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Отмена по таймауту цикла приходит сюда: внешний токен не отменён, значит проверка
            // не уложилась в CheckTimeout. Говорим об этом прямо — «проверка зависла» и «сеть
            // недоступна» требуют разных действий, и молчать про первое нельзя.
            var timedOut = ex is OperationCanceledException && !cancellationToken.IsCancellationRequested;
            var message = timedOut
                ? $"Проверка не уложилась в {CheckTimeout.TotalSeconds:0} с и была прервана. Состояние сети неизвестно."
                : ex.Message;

            if (timedOut)
            {
                _networkCheckService.ResetNetworkState();
            }

            var fallbackSnapshot = new NetworkSnapshot
            {
                CheckedAt = DateTimeOffset.Now,
                Errors = new List<string> { message }
            };
            var fallbackResult = new HealthResult
            {
                Status = MonitorStatus.CheckFailed,
                Description = message
            };

            await HandleResultAsync(trigger, fallbackSnapshot, fallbackResult, CancellationToken.None);
            UpdateDashboard(fallbackSnapshot, fallbackResult);
        }
        finally
        {
            SetBusyState(false);
            _checkLock.Release();
        }

        // Строго вне замка: диалог ждёт ответа человека, и под _checkLock это остановило бы проверки.
        var pending = _pendingProviderPrompt;
        _pendingProviderPrompt = null;
        if (pending is not null)
        {
            // Страна первой: она объясняет ситуацию крупнее. Согласие на смену страны часто снимает
            // и вопрос про провайдера — после пересчёта тот уже может оказаться разрешённым.
            await MaybeOfferCountryAsync(pending, cancellationToken);
            await MaybeOfferProviderAsync(pending, cancellationToken);
        }
    }

    /// <summary>
    /// Неподтверждённый сбой (T-473): вердикт, трей и детали остаются от последнего засчитанного
    /// замера, но молча сбой не проглатывается. В шапке — пометка, в файле журнала — строка для
    /// разбора сети. В список событий и в уведомления он не идёт.
    /// </summary>
    private async Task NoteHeldFailureAsync(NetworkSnapshot snapshot, HealthResult result, CancellationToken cancellationToken)
    {
        StatusDescriptionText.Text = _currentStatus == MonitorStatus.Unknown
            ? "Первая проверка не прошла — перепроверяю на следующем цикле."
            : $"{_currentStatusDescription} Последняя проверка не прошла — перепроверяю на следующем цикле.";

        var record = CreateEvent(
            $"одиночный сбой проверки, не засчитан ({result.Status.ToDisplayText()}): {result.Description}",
            _currentStatus,
            snapshot);
        await _logService.WriteEventAsync(record, _settings, cancellationToken);
    }

    private static HealthResult WithRepeatNote(HealthResult result, int failuresInWindow) => new()
    {
        Status = result.Status,
        Description = $"{result.Description} Сбой повторяется: неудачных проверок за "
            + $"{ConnectivityFailureFilter.RepeatWindow.TotalMinutes:0} мин — {failuresInWindow}.",
        RouteCheck = result.RouteCheck,
        ExitCheck = result.ExitCheck
    };

    private async Task HandleResultAsync(
        string trigger,
        NetworkSnapshot snapshot,
        HealthResult result,
        CancellationToken cancellationToken)
    {
        var previousStatus = _currentStatus;
        var previousIp = _lastIp;
        var internetResponds = snapshot.HttpAvailable || snapshot.PingSuccesses > 0;
        var internetAvailable = _internetWasAvailable == false
            ? internetResponds
            : !_failureFilter.IsInternetLossConfirmed(snapshot, result.Status);
        var currentVpnInterface = (result.Status is MonitorStatus.Ok or MonitorStatus.Degraded)
            && HealthEvaluator.IsTrustedKnownVpnExit(snapshot, _settings, result.RouteCheck)
            ? snapshot.InterfaceName
            : null;
        var vpnInterfaceChanged = currentVpnInterface is not null
            && _lastConfirmedVpnInterface is not null
            && !string.Equals(_lastConfirmedVpnInterface, currentVpnInterface, StringComparison.OrdinalIgnoreCase);

        if (snapshot.IpLookupSucceeded)
        {
            _lastSuccessfulCheckAt = snapshot.CheckedAt;
        }

        UpdateSessionAccounting(previousStatus, result.Status, snapshot.CheckedAt);
        _currentStatus = result.Status;

        if (_internetWasAvailable.HasValue && _internetWasAvailable.Value != internetAvailable)
        {
            await AddEventAsync(
                internetAvailable ? "интернет восстановился" : "интернет пропал",
                result.Status,
                snapshot,
                cancellationToken);
        }

        _internetWasAvailable = internetAvailable;

        if (vpnInterfaceChanged)
        {
            await AddEventAsync(
                $"VPN-интерфейс изменился: {_lastConfirmedVpnInterface} -> {currentVpnInterface}",
                result.Status, snapshot, cancellationToken);
            ShowNotification("VPN Health Monitor: VPN переключился",
                $"{_lastConfirmedVpnInterface} -> {currentVpnInterface}", Forms.ToolTipIcon.Info,
                bypassCooldown: true);
        }

        if (currentVpnInterface is not null)
        {
            _lastConfirmedVpnInterface = currentVpnInterface;
        }

        if (!string.IsNullOrWhiteSpace(previousIp)
            && !string.IsNullOrWhiteSpace(snapshot.ExternalIPv4)
            && !string.Equals(previousIp, snapshot.ExternalIPv4, StringComparison.OrdinalIgnoreCase))
        {
            _lastIpChangeAt = snapshot.CheckedAt;
            await AddEventAsync($"IP изменился: {previousIp} -> {snapshot.ExternalIPv4}", result.Status, snapshot, cancellationToken);
            // Балун только по факту смены IP — шум; гейтится отдельным toggle (детект/лог выше не трогаются).
            // Смена страны идёт своим балуном через MaybeShowStatusNotification (CountryChanged), не этим.
            if (_settings.NotifyIpChanged && !vpnInterfaceChanged)
            {
                ShowNotification(
                    "VPN Health Monitor: IP изменился",
                    $"{previousIp} -> {snapshot.ExternalIPv4}",
                    Forms.ToolTipIcon.Warning);
            }
        }

        _lastIp = snapshot.ExternalIPv4 ?? _lastIp;

        // Гейт отчитывается КАЖДЫЙ цикл и до показа балунов: он сам решает, кончился ли тихий старт,
        // и снимает глушилку раньше, чем ниже пойдёт обычное уведомление о смене статуса.
        var quietStartOutcome = _quietStart.ReportCycle(result.Status, DateTimeOffset.Now);
        if (quietStartOutcome == QuietStartOutcome.ReleasedByTimeout
            && result.Status.IsProblem()
            && previousStatus == result.Status)
        {
            // Проблема висит с самого старта, статус не менялся — обычный балун «по переходу» не придёт
            // уже никогда. Тихий старт откладывает сигнал, а не отменяет его.
            MaybeShowStatusNotification(MonitorStatus.Unknown, result.Status, result.Description);
        }

        if (previousStatus != result.Status)
        {
            await AddStatusTransitionEventsAsync(previousStatus, result.Status, snapshot, cancellationToken);
            await AddEventAsync(
                $"статус изменился: {previousStatus.ToDisplayText()} -> {result.Status.ToDisplayText()}. {result.Description}",
                result.Status,
                snapshot,
                cancellationToken);
            MaybeShowStatusNotification(previousStatus, result.Status, result.Description);
        }
        else if (string.Equals(trigger, "manual-check", StringComparison.OrdinalIgnoreCase))
        {
            await AddEventAsync($"ручная проверка завершена: {result.Description}", result.Status, snapshot, cancellationToken);
        }

        UpdateStatusBanner(result.Status, result.Description);

        // В тихом старте молчим: при автозапуске VPN-клиент ещё поднимает туннель, и модальный вопрос
        // про «мимо VPN» встретил бы пользователя до того, как картина вообще устоялась.
        // Сам вопрос показывается ПОСЛЕ выхода из-под _checkLock (см. RunSingleCheckAsync): модальное
        // окно ждёт человека сколько угодно, а держать на это время замок проверки нельзя.
        _pendingProviderPrompt = quietStartOutcome != QuietStartOutcome.Quiet ? snapshot : null;
    }

    /// <summary>
    /// Предлагает обновить ожидаемую страну, когда выход уехал в другую.
    ///
    /// В настройке с несколькими странами новый подтверждённый выход добавляется к списку;
    /// для старой настройки с одной страной сохраняется прежняя замена.
    /// </summary>
    private async Task MaybeOfferCountryAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_countryPromptOpen
            || !_settings.TreatCountryMismatchAsLeakRisk
            || string.IsNullOrWhiteSpace(_settings.ExpectedCountry)
            || string.IsNullOrWhiteSpace(snapshot.Country)
            || HealthEvaluator.CountryMatches(_settings.ExpectedCountry, snapshot.Country))
        {
            return;
        }

        var actual = CountryNames.ToDisplayName(snapshot.Country);
        var expected = _settings.ExpectedCountry;
        var countryList = expected.IndexOfAny(new[] { ',', ';', '\n', '\r' }) >= 0;
        if (string.IsNullOrWhiteSpace(actual) || !_declinedCountries.Add(actual))
        {
            return;
        }

        _countryPromptOpen = true;
        try
        {
            var vpnName = VpnClientNaming.FromAdapterName(snapshot.InterfaceName)
                ?? (_settings.AllowedVpnInterfaces.Count == 0
                    ? VpnClientNaming.FromAdapterName(ExpectedInterface.FromSettings(_settings).Alias)
                    : null);
            var yesLine = vpnName is null
                ? "ДА — если ты только что сам выбрал другую страну в своём VPN."
                : $"ДА — если ты только что сам выбрал другую страну в «{vpnName}».";

            var answer = System.Windows.MessageBox.Show(
                this,
                $"Разрешённые страны: {expected}. Сейчас выход определяется как {actual}.\n\n"
                + yesLine + (countryList
                    ? $" Тогда {actual} добавится к разрешённым странам.\n\n"
                    : $" Тогда программа будет ждать {actual} и перестанет ругаться.\n\n")
                + "НЕТ — если ты страну не менял. Тогда это тревожный признак: возможно, VPN отключился "
                + "и интернет пошёл напрямую. Проверь, включён ли VPN.\n\n"
                + $"Считать {actual} правильной страной?",
                "Интернет выходит в другой стране",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            _declinedCountries.Remove(actual);
            _settings.ExpectedCountry = countryList ? $"{expected}, {actual}" : actual;
            await _settingsService.SaveAsync(_settings);
            UpdateSettingsControls();
            await AddEventAsync($"ожидаемая страна изменена: {expected} -> {actual}", _currentStatus, snapshot, cancellationToken);
            await ReevaluateDashboardAsync("country-settings-change");
        }
        finally
        {
            _countryPromptOpen = false;
        }
    }

    /// <summary>
    /// Предлагает внести провайдера выхода в разрешённые. Без этого единственный путь — найти поле в
    /// самом низу настроек, о котором пользователь не знает: вердикт «трафик идёт мимо VPN» он видит,
    /// а что с ним делать — нет.
    ///
    /// Спрашиваем, а не добавляем сами: один и тот же признак означает либо «я переключил сервер
    /// своего VPN», либо «туннель отвалился, и это мой домашний провайдер». Различить их может только
    /// человек, поэтому вопрос ставится прямо, а вариант по умолчанию — ничего не добавлять.
    /// </summary>
    private async Task MaybeOfferProviderAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_providerPromptOpen
            || !_settings.TreatProviderChangeAsLeakRisk
            || !HealthEvaluator.ProviderIsUnexpected(snapshot, _settings))
        {
            return;
        }

        var key = ProviderMatcher.Describe(snapshot.Asn, snapshot.Provider);
        if (string.IsNullOrWhiteSpace(key) || !_declinedProviders.Add(key))
        {
            // Уже спрашивали про этого провайдера в этой сессии и получили «нет» — не переспрашиваем
            // каждые несколько секунд.
            return;
        }

        _providerPromptOpen = true;
        try
        {
            // Имя VPN берём из адаптера, который держит маршрут: пользователь знает, что включил
            // «hidemy.name», и не обязан знать, что его сервер стоит у хостера LeaseWeb.
            var vpnName = VpnClientNaming.FromAdapterName(snapshot.InterfaceName)
                ?? (_settings.AllowedVpnInterfaces.Count == 0
                    ? VpnClientNaming.FromAdapterName(ExpectedInterface.FromSettings(_settings).Alias)
                    : null);

            var intro = vpnName is null
                ? $"Сейчас интернет выходит через сервер компании «{key}»."
                : $"Сейчас интернет выходит через сервер компании «{key}» — это площадка, "
                  + $"на которой стоит сервер твоего VPN «{vpnName}».";

            var yesLine = vpnName is null
                ? "ДА — если ты только что сам переключил сервер или страну в своём VPN."
                : $"ДА — если ты только что сам переключил сервер или страну в «{vpnName}».";

            var answer = System.Windows.MessageBox.Show(
                this,
                intro + "\n\n"
                + "Раньше этой компании в списке доверенных не было, поэтому вопрос:\n\n"
                + yesLine + " Тогда она добавится в доверенные, и значок станет зелёным.\n\n"
                + "НЕТ — если ты ничего не переключал. Тогда это похоже на то, что VPN отключился "
                + "и интернет пошёл напрямую, без защиты. Проверь, включён ли VPN.\n\n"
                + "Добавить в доверенные?",
                "Интернет выходит через новую компанию",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            if (RememberProvider(snapshot.Asn, snapshot.Provider))
            {
                _declinedProviders.Remove(key);
                await _settingsService.SaveAsync(_settings);
                UpdateSettingsControls();
                await AddEventAsync($"провайдер «{key}» добавлен в разрешённые", _currentStatus, snapshot, cancellationToken);
                await ReevaluateDashboardAsync("provider-settings-change");
            }
        }
        finally
        {
            _providerPromptOpen = false;
        }
    }

    private async Task AddStatusTransitionEventsAsync(
        MonitorStatus previousStatus,
        MonitorStatus currentStatus,
        NetworkSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (previousStatus == MonitorStatus.LeakRisk && currentStatus != MonitorStatus.LeakRisk)
        {
            await AddEventAsync("риск утечки закончился", currentStatus, snapshot, cancellationToken);
        }

        if (previousStatus == MonitorStatus.Degraded && currentStatus != MonitorStatus.Degraded)
        {
            await AddEventAsync("просадка сети закончилась", currentStatus, snapshot, cancellationToken);
        }

        if (previousStatus == MonitorStatus.CheckFailed && currentStatus != MonitorStatus.CheckFailed)
        {
            await AddEventAsync("проверка снова проходит", currentStatus, snapshot, cancellationToken);
        }

        if (previousStatus == MonitorStatus.VpnDown && currentStatus != MonitorStatus.VpnDown)
        {
            await AddEventAsync("VPN снова на месте", currentStatus, snapshot, cancellationToken);
        }

        if (currentStatus == MonitorStatus.LeakRisk && previousStatus != MonitorStatus.LeakRisk)
        {
            await AddEventAsync("начался риск утечки", currentStatus, snapshot, cancellationToken);
        }

        if (currentStatus == MonitorStatus.Degraded && previousStatus != MonitorStatus.Degraded)
        {
            await AddEventAsync("началась просадка сети", currentStatus, snapshot, cancellationToken);
        }

        if (currentStatus == MonitorStatus.CheckFailed && previousStatus != MonitorStatus.CheckFailed)
        {
            await AddEventAsync("проверка начала падать", currentStatus, snapshot, cancellationToken);
        }

        if (currentStatus == MonitorStatus.VpnDown && previousStatus != MonitorStatus.VpnDown)
        {
            await AddEventAsync("VPN, похоже, выключился", currentStatus, snapshot, cancellationToken);
        }
    }

    private async Task AddEventAsync(
        string description,
        MonitorStatus status,
        NetworkSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        var monitorEvent = CreateEvent(description, status, snapshot);
        _events.Insert(0, monitorEvent);

        while (_events.Count > 100)
        {
            _events.RemoveAt(_events.Count - 1);
        }

        await _logService.WriteEventAsync(monitorEvent, _settings, cancellationToken);
    }

    private static MonitorEvent CreateEvent(string description, MonitorStatus status, NetworkSnapshot? snapshot)
    {
        return new MonitorEvent
        {
            Timestamp = DateTimeOffset.Now,
            Status = status,
            Description = description,
            IPv4 = snapshot?.ExternalIPv4,
            IPv6 = snapshot?.ExternalIPv6,
            Country = snapshot?.Country,
            Asn = snapshot?.Asn,
            Provider = snapshot?.Provider,
            PingAverageMs = snapshot?.PingAverageMs,
            PacketLossPercent = snapshot?.PacketLossPercent,
            InterfaceName = snapshot?.InterfaceName,
            DnsInfo = snapshot?.DnsServers.Count > 0 ? string.Join("; ", snapshot.DnsServers) : null
        };
    }

    private void UpdateSessionAccounting(MonitorStatus previousStatus, MonitorStatus currentStatus, DateTimeOffset now)
    {
        if (currentStatus == MonitorStatus.Ok && _healthySince is null)
        {
            _healthySince = now;
        }
        else if (currentStatus != MonitorStatus.Ok)
        {
            _healthySince = null;
        }

        if (!previousStatus.IsProblem() && currentStatus.IsProblem())
        {
            _incidentCount++;
            _problemStartedAt = now;
        }
        else if (previousStatus.IsProblem() && !currentStatus.IsProblem())
        {
            CloseProblemWindow(now);
        }
    }

    private void ResetSessionStats()
    {
        _rollingWindow.Clear();
        _failureFilter.Reset();
        _lastSuccessfulCheckAt = null;
        _lastIpChangeAt = null;
        _lastConfirmedVpnInterface = null;
        _healthySince = null;
        _problemStartedAt = null;
        _totalProblemTime = TimeSpan.Zero;
        _incidentCount = 0;
        _internetWasAvailable = null;
        UpdateSessionStats();
    }

    private void CloseActiveProblemWindow()
    {
        CloseProblemWindow(DateTimeOffset.Now);
    }

    private void CloseProblemWindow(DateTimeOffset now)
    {
        if (_problemStartedAt is not null)
        {
            _totalProblemTime += now - _problemStartedAt.Value;
            _problemStartedAt = null;
        }
    }

    private void UpdateDashboard(NetworkSnapshot? snapshot, HealthResult? result)
    {
        if (snapshot is not null)
        {
            ExternalIpText.Text = snapshot.ExternalIPv4 ?? "Неизвестно";
            ExternalIpv6Text.Text = FormatIPv6(snapshot);
            CountryText.Text = CountryNames.ToDisplayName(snapshot.Country);
            ProviderText.Text = FormatProvider(snapshot);
            InterfaceText.Text = snapshot.InterfaceName ?? "Неизвестно";
            IpApiResultsText.Text = snapshot.IpApiResults.Count == 0
                ? "Неизвестно"
                : string.Join(Environment.NewLine, snapshot.IpApiResults);
            DnsServersText.Text = FormatDnsServers(snapshot);
            HttpText.Text = snapshot.HttpAvailable ? "Доступен" : "Недоступен";
        }

        ExpectedCountryText.Text = string.IsNullOrWhiteSpace(_settings.ExpectedCountry)
            ? "Не задано"
            : _settings.ExpectedCountry;
        ExpectedIpText.Text = _settings.ExpectedPublicIPv4.Count == 0
            ? "Не задано"
            : string.Join(", ", _settings.ExpectedPublicIPv4);
        var routeState = result?.RouteCheck ?? HealthEvaluator.GetRouteCheckState(_settings, _routeCheckContext);
        var expectedInterface = _settings.AllowedVpnInterfaces.Count > 0
            ? string.Join("; ", _settings.AllowedVpnInterfaces)
            : GetExpectedInterfaceName(_settings);
        ExpectedInterfaceText.Text = _settings.RouteMode == VpnRouteMode.NoSeparateAdapter
            ? "Не используется — режим без отдельного адаптера"
            : string.IsNullOrWhiteSpace(expectedInterface)
                ? "Не задано"
                : routeState == RouteCheckState.NeedsConfiguration
                    ? $"{expectedInterface} — не найден в Windows"
                    : expectedInterface;
        var exitCheck = result?.ExitCheck ?? HealthEvaluator.GetExitCheckState(_settings);
        UpdateRouteCheckText(routeState, exitCheck);
        LastSuccessText.Text = _lastSuccessfulCheckAt.HasValue ? FormatTime(_lastSuccessfulCheckAt.Value) : "Никогда";
        LastIpChangeText.Text = _lastIpChangeAt.HasValue ? FormatTime(_lastIpChangeAt.Value) : "Никогда";
        BaselineText.Text = FormatBaseline(_settings.Baseline);

        PingText.Text = _rollingWindow.AveragePingMs.HasValue
            ? $"{_rollingWindow.AveragePingMs.Value:0} ms ({_rollingWindow.Count}/20)"
            : $"н/д ({_rollingWindow.Count}/20)";
        PacketLossText.Text = _rollingWindow.Count == 0
            ? "н/д"
            : $"{_rollingWindow.PacketLossPercent:0.#}% ({_rollingWindow.Count}/20)";

        UpdateNetworkCardSummary(snapshot ?? _lastSnapshot, routeState, exitCheck);
        UpdateQualityCardSummary(snapshot ?? _lastSnapshot);

        if (result is not null)
        {
            UpdateStatusBanner(result.Status, result.Description);
        }

        UpdateSessionStats();
    }

    /// <summary>
    /// Шапка свёрнутой «Текущей сети»: страна и внешний IP — то, ради чего в блок заглядывают.
    /// Свёрнутый блок не имеет права молчать о проблеме внутри: при warning'е шапка меняет текст,
    /// показывает знак и один раз разворачивает блок сама.
    /// </summary>
    private void UpdateNetworkCardSummary(NetworkSnapshot? snapshot, RouteCheckState routeState, VpnExitCheckState exitCheck)
    {
        var warning = routeState == RouteCheckState.NeedsConfiguration
            || (routeState == RouteCheckState.NotApplicable && exitCheck == VpnExitCheckState.NotConfigured);

        if (warning)
        {
            NetworkSummaryText.Text = routeState == RouteCheckState.NeedsConfiguration
                ? "Маршрут требует настройки — открой блок"
                : "VPN-выход фактически не контролируется — открой блок";
        }
        else
        {
            NetworkSummaryText.Text = snapshot is null
                ? "Проверка ещё не запускалась"
                : $"{CountryText.Text} · {ExternalIpText.Text}";
        }

        NetworkWarningBadge.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
        NetworkSummaryText.Foreground = warning ? WarnBrush : MutedBrush;

        if (!warning)
        {
            _networkWarningShown = false;
        }
        else if (!_networkWarningShown)
        {
            _networkWarningShown = true;
            NetworkCardToggle.IsChecked = true;
        }
    }

    /// <summary>Шапка свёрнутых «Проверок качества»: ping и доступность HTTP, знак — когда порог пробит.</summary>
    private void UpdateQualityCardSummary(NetworkSnapshot? snapshot)
    {
        var ping = _rollingWindow.AveragePingMs.HasValue
            ? $"ping {_rollingWindow.AveragePingMs.Value:0} ms"
            : "ping н/д";
        var http = snapshot is null
            ? "HTTP неизвестно"
            : snapshot.HttpAvailable ? "HTTP доступен" : "HTTP НЕДОСТУПЕН";

        var pingBad = _rollingWindow.AveragePingMs.HasValue
            && _rollingWindow.AveragePingMs.Value > _settings.DegradedPingThresholdMs;
        var lossBad = _rollingWindow.Count > 0
            && _rollingWindow.PacketLossPercent > _settings.DegradedPacketLossThresholdPercent;
        var warning = (snapshot is not null && !snapshot.HttpAvailable) || pingBad || lossBad;

        QualitySummaryText.Text = lossBad
            ? $"{ping} · потери {_rollingWindow.PacketLossPercent:0.#}% · {http}"
            : $"{ping} · {http}";
        QualityWarningBadge.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
        QualitySummaryText.Foreground = warning ? WarnBrush : MutedBrush;
    }

    private System.Windows.Media.Brush WarnBrush => (System.Windows.Media.Brush)FindResource("WarnBrush");

    private System.Windows.Media.Brush MutedBrush => (System.Windows.Media.Brush)FindResource("MutedBrush");

    private System.Windows.Media.Brush OkBrush => (System.Windows.Media.Brush)FindResource("OkBrush");

    private void UpdateSessionStats()
    {
        UptimeText.Text = _healthySince.HasValue
            ? FormatDuration(DateTimeOffset.Now - _healthySince.Value)
            : "00:00:00";
        OutagesText.Text = _incidentCount.ToString();

        var problemTime = _totalProblemTime;
        if (_problemStartedAt is not null)
        {
            problemTime += DateTimeOffset.Now - _problemStartedAt.Value;
        }

        ProblemTimeText.Text = FormatDuration(problemTime);

        SessionSummaryText.Text = $"{UptimeText.Text} · инцидентов {_incidentCount}";
        SessionWarningBadge.Visibility = _incidentCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionSummaryText.Foreground = _incidentCount > 0 ? WarnBrush : MutedBrush;
    }

    private void UpdateStatusBanner(MonitorStatus status, string description)
    {
        StatusText.Text = status.ToDisplayText();
        StatusDescriptionText.Text = description;
        _currentStatusDescription = description;
        StatusBanner.Background = status switch
        {
            MonitorStatus.Ok => new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 132, 75)),
            MonitorStatus.NoInternet => new SolidColorBrush(System.Windows.Media.Color.FromRgb(172, 52, 52)),
            MonitorStatus.LeakRisk => new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 67, 45)),
            MonitorStatus.VpnDown => new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 67, 45)),
            MonitorStatus.IpChanged => new SolidColorBrush(System.Windows.Media.Color.FromRgb(199, 128, 27)),
            MonitorStatus.CountryChanged => new SolidColorBrush(System.Windows.Media.Color.FromRgb(199, 128, 27)),
            MonitorStatus.CountryUnknown => new SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 106, 118)),
            MonitorStatus.ConfigurationRequired => new SolidColorBrush(System.Windows.Media.Color.FromRgb(199, 128, 27)),
            MonitorStatus.Degraded => new SolidColorBrush(System.Windows.Media.Color.FromRgb(151, 117, 22)),
            MonitorStatus.CheckFailed => new SolidColorBrush(System.Windows.Media.Color.FromRgb(104, 91, 166)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 106, 118))
        };
        UpdateTrayIcon(status);
    }

    private void InitializeTrayIcon()
    {
        _trayIcons[TrayIconKind.Gray] = CreateStatusIcon(Drawing.Color.FromArgb(96, 106, 118));
        _trayIcons[TrayIconKind.Green] = CreateStatusIcon(Drawing.Color.FromArgb(37, 132, 75));
        _trayIcons[TrayIconKind.Yellow] = CreateStatusIcon(Drawing.Color.FromArgb(199, 128, 27));
        _trayIcons[TrayIconKind.Red] = CreateStatusIcon(Drawing.Color.FromArgb(180, 67, 45));

        var openItem = new Forms.ToolStripMenuItem("Открыть", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        var exitItem = new Forms.ToolStripMenuItem("Выход", null, (_, _) => Dispatcher.Invoke(RequestExit));

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcons[TrayIconKind.Gray],
            Text = "VPN Health Monitor: НЕИЗВЕСТНО",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add(openItem);
        _trayIcon.ContextMenuStrip.Items.Add(exitItem);
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(RestoreFromTray);
            }
        };
    }

    private void UpdateTrayIcon(MonitorStatus status)
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Icon = _trayIcons[GetTrayIconKind(status)];
        var ip = _lastSnapshot?.ExternalIPv4 ?? _lastIp ?? "IPv4 н/д";
        _trayIcon.Text = TruncateTrayText($"{status.ToDisplayText()} | {ip}");
    }

    /// <summary>
    /// Поднять окно: из трея, из его меню и по сигналу второго экземпляра. Публичный — повторный
    /// запуск приложения будит уже работающее окно вместо второго трея (см. App.OnStartup).
    /// </summary>
    public void RestoreFromTray()
    {
        // ShowInTaskbar и Visibility возвращаются руками: при старте «свёрнутым в трей» окно было
        // показано скрытым, и одного Show() тут мало.
        ShowInTaskbar = true;
        Visibility = Visibility.Visible;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        foreach (var icon in _trayIcons.Values)
        {
            icon.Dispose();
        }

        _trayIcons.Clear();
    }

    private void MaybeShowStatusNotification(MonitorStatus previousStatus, MonitorStatus currentStatus, string description)
    {
        if (currentStatus is MonitorStatus.LeakRisk or MonitorStatus.NoInternet or MonitorStatus.IpChanged
            or MonitorStatus.CountryChanged or MonitorStatus.VpnDown)
        {
            // Per-type гейт: шумные статусы (IpChanged/CountryChanged) под toggle; safety-статусы всегда.
            if (!_settings.ShouldNotifyForStatus(currentStatus))
            {
                return;
            }

            ShowNotification(
                $"VPN Health Monitor: {currentStatus.ToDisplayText()}",
                description,
                currentStatus is MonitorStatus.LeakRisk or MonitorStatus.NoInternet or MonitorStatus.VpnDown
                    ? Forms.ToolTipIcon.Error
                    : Forms.ToolTipIcon.Warning);
            return;
        }

        if (currentStatus == MonitorStatus.Ok
            && previousStatus != MonitorStatus.Unknown
            && previousStatus != MonitorStatus.Ok)
        {
            ShowNotification("VPN Health Monitor: OK", "Состояние восстановилось.", Forms.ToolTipIcon.Info);
        }
    }

    /// <param name="respectQuietStart">
    /// false — балун показывается даже во время тихого старта. Так помечен ответ на действие человека
    /// (окно свернулось в трей): он нажал сам и ждёт подтверждения, шумом это не является.
    /// </param>
    private void ShowNotification(
        string title,
        string message,
        Forms.ToolTipIcon icon,
        bool bypassCooldown = false,
        bool respectQuietStart = true)
    {
        if (!_settings.EnableWindowsNotifications || _trayIcon is null)
        {
            return;
        }

        // Глушится ТОЛЬКО доставка. Статус в окне, цвет иконки в трее и запись в лог уже произошли
        // и остаются настоящими с первого цикла — правило единого вердикта (T-391).
        if (respectQuietStart && _quietStart.ShouldSuppress(DateTimeOffset.Now))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (!bypassCooldown
            && _lastNotificationAt.HasValue
            && now - _lastNotificationAt.Value < TimeSpan.FromSeconds(NotificationCooldownSeconds))
        {
            return;
        }

        try
        {
            _trayIcon.ShowBalloonTip(5000, title, message, icon);
            _lastNotificationAt = now;
        }
        catch
        {
            // Notification failures should never affect monitoring.
        }
    }

    private static Drawing.Icon CreateStatusIcon(Drawing.Color fill)
    {
        using var bitmap = new Drawing.Bitmap(16, 16);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Drawing.Color.Transparent);
        using var path = BuildHeartPath(new Drawing.RectangleF(2f, 1.5f, 12f, 12.5f));
        using var brush = new Drawing.SolidBrush(fill);
        using var pen = new Drawing.Pen(Drawing.Color.White, 1.4f)
        {
            LineJoin = Drawing.Drawing2D.LineJoin.Round
        };
        graphics.FillPath(brush, path);
        graphics.DrawPath(pen, path);

        var handle = bitmap.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Drawing.Drawing2D.GraphicsPath BuildHeartPath(Drawing.RectangleF box)
    {
        float x = box.X, y = box.Y, w = box.Width, h = box.Height;
        Drawing.PointF P(float nx, float ny) => new(x + nx * w, y + ny * h);

        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddBezier(P(0.5f, 0.25f), P(0.5f, 0.10f), P(0.20f, 0.05f), P(0.10f, 0.25f));
        path.AddBezier(P(0.10f, 0.25f), P(0.00f, 0.42f), P(0.15f, 0.60f), P(0.50f, 0.90f));
        path.AddBezier(P(0.50f, 0.90f), P(0.85f, 0.60f), P(1.00f, 0.42f), P(0.90f, 0.25f));
        path.AddBezier(P(0.90f, 0.25f), P(0.80f, 0.05f), P(0.50f, 0.10f), P(0.5f, 0.25f));
        path.CloseFigure();
        return path;
    }

    private static TrayIconKind GetTrayIconKind(MonitorStatus status)
    {
        return status switch
        {
            MonitorStatus.Ok => TrayIconKind.Green,
            MonitorStatus.NoInternet or MonitorStatus.LeakRisk or MonitorStatus.VpnDown => TrayIconKind.Red,
            MonitorStatus.Degraded
                or MonitorStatus.IpChanged
                or MonitorStatus.CountryChanged
                or MonitorStatus.ConfigurationRequired
                or MonitorStatus.CheckFailed => TrayIconKind.Yellow,
            _ => TrayIconKind.Gray
        };
    }

    private static string TruncateTrayText(string text)
    {
        const int maxLength = 63;
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    private void SetMonitoringState(bool isMonitoring)
    {
        StartButton.IsEnabled = !isMonitoring;
        StopButton.IsEnabled = isMonitoring;
    }

    private void SetBusyState(bool isBusy)
    {
        RunCheckButton.IsEnabled = !isBusy;
        StartButton.IsEnabled = !isBusy && _monitoringCts is null;
        StopButton.IsEnabled = _monitoringCts is not null;
        FooterText.Text = isBusy
            ? "Проверяю сеть..."
            : $"Настройки: {AppPaths.SettingsPath} | Логи: {_settings.LogsFolderPath}";
    }

    private static int ParseInt(string text, int fallback, int min, int max)
    {
        return int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;
    }

    private static double ParseDouble(string text, double fallback, double min, double max)
    {
        return double.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;
    }

    /// <summary>
    /// Line-per-value split. Provider names contain spaces and commas ("M247 Europe SRL (AS9009)"), so
    /// <see cref="SplitValues"/> — which also breaks on those — would shred them into separate entries.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        return text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> SplitValues(string text)
    {
        return text
            .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatProvider(NetworkSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Asn) && string.IsNullOrWhiteSpace(snapshot.Provider))
        {
            return "Неизвестно";
        }

        return string.Join(" ", new[] { snapshot.Asn, snapshot.Provider }.Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string FormatIPv6(NetworkSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ExternalIPv6))
        {
            return snapshot.ExternalIPv6;
        }

        return string.IsNullOrWhiteSpace(snapshot.IPv6CheckStatus)
            ? "Не обнаружен / проверка не прошла"
            : snapshot.IPv6CheckStatus;
    }

    private static string FormatDnsServers(NetworkSnapshot snapshot)
    {
        return snapshot.DnsServers.Count == 0
            ? "Не обнаружены или проверка выключена"
            : string.Join(Environment.NewLine, snapshot.DnsServers);
    }

    private static string FormatBaseline(BaselineInfo? baseline)
    {
        if (baseline?.IPv4 is null)
        {
            return "Не задано";
        }

        var parts = new List<string>
        {
            baseline.IPv4,
            CountryNames.ToDisplayName(baseline.Country),
            FormatTime(baseline.Timestamp)
        };

        if (!string.IsNullOrWhiteSpace(baseline.InterfaceName))
        {
            parts.Add(baseline.InterfaceName);
        }

        return string.Join(" / ", parts);
    }

    /// <summary>
    /// Writes the expected adapter as three values: the display string the user sees, plus the alias and
    /// description matching runs on. The GUID is filled from the live inventory when the adapter is there —
    /// as a hint, never as the sole key, because some tunnel drivers regenerate it.
    /// </summary>
    private void SetExpectedInterface(string display)
    {
        var (alias, description) = ExpectedInterface.SplitDisplay(display);
        _settings.ExpectedInterfaceName = display;
        _settings.ExpectedInterfaceAlias = alias;
        _settings.ExpectedInterfaceDescription = description;
        _settings.ExpectedInterfaceId = string.Empty;
        _adapterPresence.Reset();
    }

    private static string GetExpectedInterfaceName(AppSettings settings)
    {
        if (settings.RouteMode == VpnRouteMode.NoSeparateAdapter)
        {
            return string.Empty;
        }

        return !string.IsNullOrWhiteSpace(settings.ExpectedInterfaceName)
            ? settings.ExpectedInterfaceName
            : settings.Baseline?.InterfaceName ?? string.Empty;
    }

    private static string FormatTime(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string FormatDuration(TimeSpan value)
    {
        return value < TimeSpan.Zero
            ? "00:00:00"
            : $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    // ----- Per-app kill switch (Защищённые приложения) -----

    private void UpdateAdminStatus(bool? canVerifyLive = null)
    {
        var elevated = FirewallService.IsProcessElevated();

        if (elevated)
        {
            AdminStatusText.Text = "Права администратора: ДА — правила применяются без отдельного UAC.";
        }
        else if (canVerifyLive == false)
        {
            AdminStatusText.Text = "Права администратора: нет. Чтение firewall без admin недоступно — статусы показаны по записи приложения; сверка с firewall происходит при применении правил (UAC).";
        }
        else
        {
            AdminStatusText.Text = "Права администратора: нет — каждая операция с правилами запросит UAC.";
        }

        // Строка длинная и обрезается по ширине окна — полный текст остаётся в tooltip'е.
        AdminStatusText.ToolTip = AdminStatusText.Text;
        AdminStatusGlyph.Text = elevated ? "✓" : "!";
        AdminStatusGlyph.Foreground = elevated ? OkBrush : WarnBrush;
        AdminStatusBorder.Background = elevated
            ? (System.Windows.Media.Brush)FindResource("SurfaceBrush")
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFB, 0xF2));
    }

    private void UpdateRouteCheckText(RouteCheckState state, VpnExitCheckState exitCheck)
    {
        RouteCheckText.Text = state switch
        {
            RouteCheckState.Active => "Активна — маршрут сравнивается с VPN-адаптером.",
            RouteCheckState.NeedsConfiguration =>
                "ТРЕБУЕТ НАСТРОЙКИ: сохранённый VPN-адаптер отсутствует в Windows или больше не подходит. "
                + "Это не доказанная утечка — выбери текущий режим VPN и адаптер.",
            RouteCheckState.NotApplicable =>
                "НЕ применима: выбран режим без отдельного VPN-адаптера. Windows законно оставляет default route на Wi-Fi/Ethernet.",
            _ => "Выключена в настройках."
        };

        var warning = state == RouteCheckState.NeedsConfiguration
            || (state == RouteCheckState.NotApplicable && exitCheck == VpnExitCheckState.NotConfigured);
        RouteCheckText.Foreground = warning
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB3, 0x47, 0x00))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x26, 0x2C));

        ReconfigureRouteButton.Content = state == RouteCheckState.NeedsConfiguration
            ? "Перенастроить маршрут"
            : "Настроить маршрут";

        VpnExitControlText.Text = exitCheck == VpnExitCheckState.Configured
            ? "Активен — прямой выход проверяется по стране, ASN/провайдеру или строгому списку IP."
            : _settings.RouteMode == VpnRouteMode.NoSeparateAdapter
                ? "VPN ФАКТИЧЕСКИ НЕ КОНТРОЛИРУЕТСЯ: включи риск по стране и/или ASN/провайдеру."
                : "Дополнительный контроль выхода не настроен; в режиме с адаптером маршрут проверяется отдельно.";

        VpnExitControlText.Foreground = exitCheck == VpnExitCheckState.NotConfigured
            && _settings.RouteMode == VpnRouteMode.NoSeparateAdapter
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB3, 0x47, 0x00))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x26, 0x2C));
    }

    /// <summary>
    /// Re-reads the adapter list on a timer and on every network-address change, so the route check follows
    /// the machine instead of the snapshot taken at startup. Cheap guard: one PowerShell child at a time,
    /// and not more often than <see cref="AdapterRefreshInterval"/>.
    /// </summary>
    private static readonly TimeSpan AdapterRefreshInterval = TimeSpan.FromSeconds(30);

    private async Task MaybeRefreshAdapterInventoryAsync(bool force = false)
    {
        if (!force && DateTimeOffset.Now - _lastAdapterRefreshAt < AdapterRefreshInterval)
        {
            return;
        }

        if (Interlocked.Exchange(ref _adapterRefreshInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            await RefreshAdapterChoicesAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _adapterRefreshInFlight, 0);
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Событие приходит не в UI-потоке, а обновление трогает ComboBox.
        Dispatcher.InvokeAsync(async () => await MaybeRefreshAdapterInventoryAsync(force: true));
    }

    /// <summary>Fills the expected-interface dropdown with the machine's real adapters, keeping any manual text.</summary>
    private async Task RefreshAdapterChoicesAsync()
    {
        try
        {
            var inventory = await _adapterInventory.ReadAsync(CancellationToken.None);
            _lastAdapterRefreshAt = DateTimeOffset.Now;
            _adapterInventoryResult = inventory;
            UpdateRouteCheckContext(inventory);
            var current = ExpectedInterfaceBox.Text;
            ExpectedInterfaceBox.ItemsSource = inventory.Adapters
                .Select(adapter => adapter.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ExpectedInterfaceBox.Text = current;
        }
        catch (Exception)
        {
            _adapterInventoryResult = null;
            _routeCheckContext = RouteCheckContext.Unknown;
            // Manual entry stays available — the dropdown is a convenience, not a requirement.
        }

        UpdateBlockedAdaptersText();
    }

    /// <summary>
    /// Turns one adapter snapshot into the route-check context. A snapshot that is missing the expected
    /// adapter is NOT published until it has been missing several times in a row: at logon the monitor
    /// routinely wins the race against the VPN service, and one such reading used to freeze the app in
    /// "НУЖНА НАСТРОЙКА" until it was restarted. Until absence is confirmed the context stays Unknown,
    /// which by contract never declares a working route stale.
    /// </summary>
    private void UpdateRouteCheckContext(AdapterInventoryResult inventory)
    {
        var expected = ExpectedInterface.AllFromSettings(_settings);
        var live = RouteCheckContext.FromAdapters(inventory.Adapters);

        if (expected.Count == 0)
        {
            _adapterPresence.Reset();
            _routeCheckContext = live;
            return;
        }

        var present = expected.Any(live.Contains);
        _adapterPresence.Observe(present);

        _routeCheckContext = present || _adapterPresence.ConfirmedAbsent
            ? live
            : RouteCheckContext.Unknown;
    }

    private string? FindSuggestedVpnAdapter()
    {
        return _adapterInventoryResult?.Adapters
            .Where(adapter => VpnInterfaceHeuristics.LooksLikeVpn(adapter.DisplayName))
            .OrderByDescending(adapter => string.Equals(adapter.Status, "Up", StringComparison.OrdinalIgnoreCase))
            .Select(adapter => adapter.DisplayName)
            .FirstOrDefault();
    }

    private void UpdateBlockedAdaptersText()
    {
        BlockedAdaptersText.Text = _settings.ConfirmedBlockedAdapters.Count == 0
            ? "Адаптеры блокировки: пока не подтверждены — список покажется перед применением правил."
            : $"Прямой выход блокируется через: {string.Join(", ", _settings.ConfirmedBlockedAdapters)}.";
    }

    /// <summary>
    /// Works out which adapters the rules must bind to and — for an explicit apply, or whenever the set of
    /// adapters changed since last time — shows them for confirmation (T-323). Returns null if the user
    /// cancelled or nothing could be determined; the caller then leaves the firewall untouched.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ResolveBlockedAdaptersAsync(bool alwaysConfirm)
    {
        var inventory = await _adapterInventory.ReadAsync(CancellationToken.None);
        var fingerprint = BuildAdapterFingerprint(inventory.Adapters);
        var hardwareChanged = !string.Equals(fingerprint, _settings.ConfirmedAdapterFingerprint, StringComparison.OrdinalIgnoreCase);

        // Outside the confirmation screen, keep what the user actually approved last time — the classifier's
        // own proposal would silently undo a manual correction.
        var names = hardwareChanged || _settings.ConfirmedBlockedAdapters.Count == 0
            ? AdapterClassifier.SelectBlockable(inventory.Adapters).Select(adapter => adapter.Name).ToList()
            : _settings.ConfirmedBlockedAdapters.ToList();

        if (alwaysConfirm || hardwareChanged || names.Count == 0)
        {
            var preselected = hardwareChanged ? null : _settings.ConfirmedBlockedAdapters;
            var dialog = new ConfirmAdaptersWindow(inventory.Adapters, inventory.Warning, preselected) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                await LogKillSwitchEventAsync("adapters_declined",
                    "пользователь не подтвердил список адаптеров — правила не менялись.");
                FooterText.Text = "Список адаптеров не подтверждён. Правила не менялись.";
                return null;
            }

            names = dialog.SelectedAdapters.ToList();
        }

        if (names.Count == 0)
        {
            return null;
        }

        _settings.ConfirmedBlockedAdapters = names;
        _settings.ConfirmedAdapterFingerprint = fingerprint;
        await _settingsService.SaveAsync(_settings);
        UpdateBlockedAdaptersText();
        await LogKillSwitchEventAsync("adapters_confirmed", $"адаптеры блокировки подтверждены: {string.Join(", ", names)}.");
        return names;
    }

    /// <summary>Stable signature of the machine's adapter set — a new NIC (USB modem, dock) changes it.</summary>
    private static string BuildAdapterFingerprint(IEnumerable<NetworkAdapterInfo> adapters)
        => string.Join("|", adapters
            .Select(adapter => adapter.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    private async void AddAppButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери программу (.exe) для защиты",
            Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = dialog.FileName;
        var identityKey = ProtectedAppIdentity.ComputeKey(path);

        // Сверяем по устойчивому ключу, а не по пути: после обновления VS Code-расширения (или Store-пакета,
        // или CLI) путь другой, и проверка по пути заводила вторую строку на ту же самую программу.
        var duplicate = _settings.ProtectedApps.FirstOrDefault(a =>
            PathEquals(a.Path, path)
            || string.Equals(ProtectedAppIdentity.KeyOf(a), identityKey, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            if (!PathEquals(duplicate.Path, path))
            {
                System.Windows.MessageBox.Show(this,
                    $"Эта программа уже в списке как «{duplicate.Name}», но по старому пути.\n\n"
                    + "Нажми «Обновить путь» в её строке — это перепривяжет существующее правило, "
                    + "а не создаст второе.",
                    "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show(this, "Эта программа уже в списке защищённых.",
                    "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        var app = new ProtectedApp
        {
            Path = path,
            IdentityKey = identityKey,
            Name = ResolveAppName(path),
            RuleName = FirewallService.BuildRuleName(path),
            AddedAt = DateTimeOffset.Now
        };
        _settings.ProtectedApps.Add(app);
        await _settingsService.SaveAsync(_settings);
        await LogKillSwitchEventAsync("app_added", $"программа добавлена в защиту: {app.Name} ({app.Path})", app);
        await RefreshProtectedAppsAsync(logIssues: false);

        var apply = System.Windows.MessageBox.Show(this,
            $"«{app.Name}» добавлена. Применить firewall-правило сейчас? Потребуется подтверждение UAC.",
            "VPN Health Monitor", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (apply == MessageBoxResult.Yes)
        {
            await ApplyRulesForAsync(new[] { app }, "apply");
        }
    }

    private async void ApplyRulesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.ProtectedApps.Count == 0)
        {
            System.Windows.MessageBox.Show(this, "Список защищённых программ пуст. Сначала добавь программу.",
                "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ApplyRulesForAsync(_settings.ProtectedApps.ToList(), "apply");
    }

    private async void DisableProtectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.ProtectedApps.Count == 0)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(this,
            "Снять firewall-правила со всех защищённых программ? Программы из списка останутся, но защита отключится.",
            "VPN Health Monitor", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await ApplyRulesForAsync(Array.Empty<ProtectedApp>(), "remove_all");
    }

    private async void RefreshProtectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshProtectedAppsAsync(logIssues: true);
    }

    private async void ReinstallApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is ProtectedAppRow row)
        {
            await ApplyRulesForAsync(new[] { row.App }, "apply");
        }
    }

    private async void UpdatePathApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not ProtectedAppRow row)
        {
            return;
        }

        var app = row.App;
        var oldPath = app.Path;
        var oldRuleName = app.RuleName;

        // Prefer the path resolved during refresh; re-resolve if it's missing or already moved again.
        var newPath = row.ResolvedNewPath;
        if (string.IsNullOrWhiteSpace(newPath) || !SafeExists(newPath))
        {
            if (AppxPathResolver.IsPackagedPath(oldPath))
            {
                var moved = await _appxResolver.ResolveMovedPathsAsync(new[] { oldPath }, CancellationToken.None);
                moved.TryGetValue(oldPath, out newPath);
            }
            else if (CliPathResolver.IsCliVersionedPath(oldPath))
            {
                CliPathResolver.ResolveMovedPaths(new[] { oldPath }).TryGetValue(oldPath, out newPath);
            }
            else if (VsCodeExtensionPathResolver.IsVersionedExtensionPath(oldPath))
            {
                VsCodeExtensionPathResolver.ResolveMovedPaths(new[] { oldPath }).TryGetValue(oldPath, out newPath);
            }
        }

        if (string.IsNullOrWhiteSpace(newPath))
        {
            System.Windows.MessageBox.Show(this,
                $"Не удалось определить новый путь для «{app.Name}». Возможно, программа удалена или ещё не переустановлена — защита не восстановлена.",
                "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshProtectedAppsAsync(logIssues: false);
            return;
        }

        var newRuleName = FirewallService.BuildRuleName(newPath);

        // Имя берётся из нового файла: у самообновляющихся программ версия зашита в FileDescription,
        // и строка, оставленная как есть, годами показывает версию, которой на диске давно нет.
        var newName = ResolveAppName(newPath);

        // Carry the new path/rule into the elevated call, but only commit to settings on success —
        // if the user cancels UAC the firewall is untouched, so the stored path must stay as-is.
        var pending = new ProtectedApp
        {
            Name = newName,
            Path = newPath,
            IdentityKey = ProtectedAppIdentity.ComputeKey(newPath),
            RuleName = newRuleName,
            AddedAt = app.AddedAt,
            RulesAppliedAt = app.RulesAppliedAt
        };

        try
        {
            // Path updates re-create the rule, so they need the adapter list too. Ask again only when the
            // set of adapters changed since the user last confirmed it — otherwise this is a silent re-bind.
            var adapters = await ResolveBlockedAdaptersAsync(alwaysConfirm: false);
            if (adapters is null)
            {
                await RefreshProtectedAppsAsync(logIssues: false);
                return;
            }

            var idlePreviousVersions = await CollectIdlePreviousVersionRulesAsync();
            var result = await _firewallService.UpdatePathAsync(
                pending, oldRuleName, adapters, CancellationToken.None, idlePreviousVersions);

            if (result.Cancelled)
            {
                await LogKillSwitchEventAsync("uac_cancelled", $"обновление пути отменено в UAC: {app.Name}", app);
                FooterText.Text = "Обновление пути отменено в UAC. Путь не изменён.";
                return;
            }

            if (!result.Success)
            {
                await LogKillSwitchEventAsync("admin_error", $"ошибка обновления пути: {result.Error}", app);
                System.Windows.MessageBox.Show(this,
                    result.Error ?? "Не удалось обновить правило для нового пути.",
                    "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshProtectedAppsAsync(logIssues: false);
                return;
            }

            // Rule is in place — commit the new path/rule to the persisted app.
            app.Path = newPath;
            app.RuleName = newRuleName;
            app.Name = newName;
            app.IdentityKey = pending.IdentityKey;
            if (result.Items.Any(i => i.State == "applied"))
            {
                app.RulesAppliedAt = DateTimeOffset.Now;
            }
            await _settingsService.SaveAsync(_settings);
            await LogKillSwitchEventAsync("path_updated",
                $"путь обновлён, новое правило применено, старое сохранено при наличии файла: {app.Name} ({oldPath} → {newPath})", app);

            var cleaned = result.Items.Where(item => item.State == "stale_removed").ToList();
            foreach (var item in cleaned)
            {
                await LogKillSwitchEventAsync("rule_removed",
                    $"вместе с обновлением пути снято ненужное правило старой версии: {item.Path}");
            }

            FooterText.Text = $"Путь «{app.Name}» обновлён. Старое правило сохраняется, пока прежняя версия может работать."
                + (cleaned.Count > 0 ? $" Сняты ненужные правила старых версий: {cleaned.Count}." : string.Empty);
            await RefreshProtectedAppsAsync(logIssues: false);
        }
        catch (Exception ex)
        {
            await LogKillSwitchEventAsync("admin_error", $"исключение при обновлении пути: {ex.Message}", app);
            System.Windows.MessageBox.Show(this, ex.Message, "VPN Health Monitor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Правила прошлых версий, которые сейчас никому не нужны. Уходят в тот же UAC, что и обновление
    /// пути (T-482): копиться они перестают, а отдельного запроса прав нет.
    /// </summary>
    private async Task<IReadOnlyList<FirewallRuleInfo>> CollectIdlePreviousVersionRulesAsync()
    {
        var rules = await _firewallService.QueryRulesAsync(CancellationToken.None);
        if (rules is null)
        {
            return Array.Empty<FirewallRuleInfo>();
        }

        return FirewallService.FindUntrackedRules(rules, _settings.ProtectedApps)
            .Where(rule => FirewallService.FindNewerTrackedVersion(rule, _settings.ProtectedApps) is { } newer
                && !PreviousVersionUsage.Check(rule.Program!, newer.Path).InUse)
            .ToList();
    }

    private async void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not ProtectedAppRow row)
        {
            return;
        }

        var app = row.App;

        // Правило старой версии: строка могла устареть с прошлого обновления списка, поэтому
        // проверяем ещё раз в момент нажатия. Снять правило у работающего процесса — выпустить его мимо VPN.
        if (row.IsPreviousVersion)
        {
            var usage = PreviousVersionUsage.Check(app.Path, row.NewerVersionPath);
            if (usage.InUse)
            {
                var why = usage.RunningFromOldExe > 0
                    ? $"Из старой версии «{app.Name}» сейчас запущено процессов: {usage.RunningFromOldExe}. Правило не пускает их мимо VPN."
                    : "VS Code запущен раньше, чем скачалась новая версия расширения: какое-то его окно может работать на старой "
                      + "и запустить её снова уже без правила.";
                System.Windows.MessageBox.Show(this,
                    why + " Снимать рано: закрой все окна программы (для расширений — VS Code) и открой снова, тогда строка станет «не нужна».",
                    "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshProtectedAppsAsync(logIssues: false);
                return;
            }
        }

        var confirm = System.Windows.MessageBox.Show(this,
            row.IsPreviousVersion
                ? $"Снять правило старой версии «{app.Name}»? Новая версия останется защищённой своей строкой."
                : $"Удалить «{app.Name}» из защиты и снять её firewall-правило?",
            "VPN Health Monitor", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        // Remove the rule first so we never leave an orphan, then drop it from settings.
        var result = await _firewallService.RemoveAsync(new[] { app }, CancellationToken.None);
        if (result.Cancelled)
        {
            await LogKillSwitchEventAsync("uac_cancelled", $"снятие правила отменено в UAC: {app.Name}", app);
            FooterText.Text = "Операция отменена в UAC. Программа осталась в списке.";
            return;
        }

        if (!result.Success)
        {
            await LogKillSwitchEventAsync("admin_error", $"ошибка снятия правила: {result.Error}", app);
            System.Windows.MessageBox.Show(this,
                result.Error ?? "Не удалось снять правило.",
                "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshProtectedAppsAsync(logIssues: false);
            return;
        }

        if (row.IsPreviousVersion)
        {
            // В списке старой версии нет: запись новой версии трогать нельзя, хотя ключ у них общий.
            await LogKillSwitchEventAsync("rule_removed", $"снято правило старой версии: {app.Name} ({app.Path})", app);
            await RefreshProtectedAppsAsync(logIssues: false);
            return;
        }

        _settings.ProtectedApps.RemoveAll(a => PathEquals(a.Path, app.Path));
        await _settingsService.SaveAsync(_settings);
        await LogKillSwitchEventAsync("app_removed", $"программа удалена из защиты: {app.Name}", app);
        await RefreshProtectedAppsAsync(logIssues: false);
    }

    private async Task ApplyRulesForAsync(IReadOnlyList<ProtectedApp> apps, string action)
    {
        try
        {
            IReadOnlyList<string> adapters = Array.Empty<string>();
            if (action == "apply")
            {
                // Rules are only as good as the adapter list they bind to — the user confirms it first.
                var resolved = await ResolveBlockedAdaptersAsync(alwaysConfirm: true);
                if (resolved is null)
                {
                    await RefreshProtectedAppsAsync(logIssues: false);
                    return;
                }

                adapters = resolved;
            }

            var result = action switch
            {
                "remove_all" => await _firewallService.RemoveAllAsync(CancellationToken.None),
                "remove" => await _firewallService.RemoveAsync(apps, CancellationToken.None),
                _ => await _firewallService.ApplyAsync(apps, adapters, CancellationToken.None)
            };

            if (result.Cancelled)
            {
                await LogKillSwitchEventAsync("uac_cancelled", "операция с правилами отменена в UAC.");
                FooterText.Text = "Операция отменена в UAC.";
                await RefreshProtectedAppsAsync(logIssues: false);
                return;
            }

            if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
            {
                await LogKillSwitchEventAsync("admin_error", $"ошибка firewall: {result.Error}");
            }

            if (action == "apply")
            {
                var now = DateTimeOffset.Now;
                foreach (var item in result.Items.Where(i => i.State == "applied"))
                {
                    var match = _settings.ProtectedApps.FirstOrDefault(a =>
                        string.Equals(a.RuleName, item.RuleName, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        match.RulesAppliedAt = now;
                    }
                }
                await _settingsService.SaveAsync(_settings);

                foreach (var item in result.Items.Where(i => i.State == "file_not_found"))
                {
                    var miss = _settings.ProtectedApps.FirstOrDefault(a =>
                        string.Equals(a.RuleName, item.RuleName, StringComparison.OrdinalIgnoreCase));
                    await LogKillSwitchEventAsync("file_not_found", $"файл не найден при применении правил: {item.Path}", miss);
                }
            }

            if (action == "remove_all")
            {
                foreach (var protectedApp in _settings.ProtectedApps)
                {
                    protectedApp.RulesAppliedAt = null;
                }
                _settings.ConfirmedBlockedAdapters = new List<string>();
                _settings.ConfirmedAdapterFingerprint = string.Empty;
                await _settingsService.SaveAsync(_settings);
                UpdateBlockedAdaptersText();
            }

            await LogApplyResultAsync(action, result);
            await RefreshProtectedAppsAsync(logIssues: false);

            if (result.Items.Any(i => i.State == "file_not_found"))
            {
                FooterText.Text = "Часть программ не найдена по сохранённому пути — см. колонку «Статус».";
            }
        }
        catch (Exception ex)
        {
            await LogKillSwitchEventAsync("admin_error", $"исключение при операции с правилами: {ex.Message}");
            System.Windows.MessageBox.Show(this, ex.Message, "VPN Health Monitor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task LogApplyResultAsync(string action, FirewallActionResult result)
    {
        var applied = result.Items.Count(i => i.State == "applied");
        var removed = result.Items.Count(i => i.State == "removed");
        var missing = result.Items.Count(i => i.State == "file_not_found");
        var errored = result.Items.Count(i => i.State == "error");
        var phys = result.PhysicalAdapters.Count > 0 ? string.Join(", ", result.PhysicalAdapters) : "не найдены";

        if (action == "apply")
        {
            await LogKillSwitchEventAsync("rules_applied",
                $"правила применены: {applied}, файл не найден: {missing}, ошибок: {errored}. Физ. адаптеры: {phys}.");
        }
        else if (action == "remove_all")
        {
            await LogKillSwitchEventAsync("rules_removed", "защита отключена: все правила VPN Health Monitor сняты.");
        }
        else
        {
            await LogKillSwitchEventAsync("rules_removed", $"правила сняты: {removed}, ошибок: {errored}.");
        }
    }

    private async Task RefreshProtectedAppsAsync(bool logIssues)
    {
        var rules = await _firewallService.QueryRulesAsync(CancellationToken.None);
        var canVerifyLive = rules is not null;

        // Base status per app; collect apps whose pinned exe is missing → candidates for a moved path
        // (Store/MSIX packages, self-updating Claude Code CLI, and VS Code extension sidecars
        // live under different version schemes).
        var baseStatus = new Dictionary<ProtectedApp, ProtectionStatus>();
        var appxQueryPaths = new List<string>();
        foreach (var app in _settings.ProtectedApps)
        {
            var status = canVerifyLive
                ? FirewallService.ComputeStatus(app, rules!)
                : FirewallService.ComputeStatusFromRecord(app);
            baseStatus[app] = status;
            if (status == ProtectionStatus.FileNotFound && AppxPathResolver.IsPackagedPath(app.Path))
            {
                appxQueryPaths.Add(app.Path);
            }
        }

        // Versioned sidecars can be replaced while the old extension or CLI folder still exists.
        // A live firewall rule for that old file does not protect the executable now in use.
        var moved = new Dictionary<string, string>(
            VersionedProtectedAppPathResolver.Resolve(_settings.ProtectedApps),
            StringComparer.OrdinalIgnoreCase);
        if (appxQueryPaths.Count > 0)
        {
            foreach (var kvp in await _appxResolver.ResolveMovedPathsAsync(appxQueryPaths, CancellationToken.None))
            {
                moved[kvp.Key] = kvp.Value;
            }
        }

        _protectedAppRows.Clear();
        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in _settings.ProtectedApps)
        {
            var status = baseStatus[app];
            string? newPath = null;
            if (moved.TryGetValue(app.Path, out var resolved))
            {
                status = ProtectionStatus.PathChanged;
                newPath = resolved;
            }

            if (logIssues && canVerifyLive && status == ProtectionStatus.Error)
            {
                await LogKillSwitchEventAsync("rule_verification_failed",
                    $"правило не соответствует ожидаемому: {app.Name}", app);
            }
            else if (logIssues && status == ProtectionStatus.FileNotFound)
            {
                await LogKillSwitchEventAsync("file_not_found", $"файл защищённой программы не найден: {app.Path}", app);
            }

            if (status == ProtectionStatus.PathChanged)
            {
                var key = AppIdentityKey(app);
                stale.Add(key);
                if (!_notifiedPathChange.Contains(key))
                {
                    // Safety-алерт (T-196): защита приложения отвалилась. Делаем визуально отличимым от
                    // рутинных балунов (Error-иконка + ⚠️-префикс + слово «защита»), всегда показываем
                    // (bypassCooldown, без toggle) — цель «заметнее, не тише».
                    ShowNotification(
                        "⚠️ Новая версия без защиты",
                        $"у «{app.Name}» найдена новая версия — её прямой выход ещё не заблокирован. Открой вкладку «Защищённые приложения» и нажми «Обновить путь».",
                        Forms.ToolTipIcon.Error,
                        bypassCooldown: true,
                        respectQuietStart: false);
                    await LogKillSwitchEventAsync("path_changed",
                        $"новая версия не защищена правилом: {app.Name} ({app.Path} → {newPath})", app);
                }
            }

            _protectedAppRows.Add(new ProtectedAppRow
            {
                App = app,
                Name = app.Name,
                Path = app.Path,
                Status = status,
                StatusText = status.ToDisplayText(),
                AppliedText = app.RulesAppliedAt.HasValue ? FormatTime(app.RulesAppliedAt.Value) : "—",
                CanUpdatePath = status == ProtectionStatus.PathChanged,
                ResolvedNewPath = newPath
            });

            _lastKnownExists[AppIdentityKey(app)] = SafeExists(app.Path);
        }

        AddUntrackedRuleRows(rules);

        _notifiedPathChange = stale;
        UpdateAdminStatus(canVerifyLive);
        UpdateBlockedAdaptersText();
    }

    /// <summary>
    /// Показывает правила, которые есть в Windows, но которых нет в списке защищённых программ.
    ///
    /// Без этого список на экране и настоящее состояние фаервола расходятся молча: правило работает,
    /// а приложение о нём не знает и ничего про него не говорит. Именно так выглядела защита движа,
    /// поставленная в обход приложения, — и точно так же выглядели бы правила, оставшиеся от прошлой
    /// установки. Строку можно либо взять под наблюдение, либо снять.
    /// </summary>
    private void AddUntrackedRuleRows(IReadOnlyList<FirewallRuleInfo>? rules)
    {
        _previousVersionInUse.Clear();
        if (rules is null)
        {
            return;
        }

        foreach (var rule in FirewallService.FindUntrackedRules(rules, _settings.ProtectedApps))
        {
            var path = rule.Program ?? string.Empty;
            var name = string.IsNullOrWhiteSpace(path)
                ? rule.Rule
                : ProtectedAppNaming.RefreshIfPossible(path, SafeFileName(path));

            // Правило прошлой версии программы из списка (T-473): «Взять под наблюдение» к нему не
            // применимо, а снимать его можно только когда из старой версии ничего не запущено.
            if (FirewallService.FindNewerTrackedVersion(rule, _settings.ProtectedApps) is { } newer)
            {
                var inUse = PreviousVersionUsage.Check(path, newer.Path).InUse;
                _previousVersionInUse[path] = (newer.Path, inUse);
                var status = inUse ? ProtectionStatus.PreviousVersionInUse : ProtectionStatus.PreviousVersionIdle;

                _protectedAppRows.Add(new ProtectedAppRow
                {
                    App = new ProtectedApp
                    {
                        Name = name,
                        Path = path,
                        RuleName = rule.Rule,
                        IdentityKey = ProtectedAppIdentity.ComputeKey(path)
                    },
                    Name = name,
                    Path = path,
                    Status = status,
                    StatusText = status.ToDisplayText(),
                    AppliedText = "—",
                    CanUpdatePath = false,
                    CanAdopt = false,
                    CanReinstall = false,
                    CanRemove = !inUse,
                    NewerVersionPath = newer.Path
                });
                continue;
            }

            _protectedAppRows.Add(new ProtectedAppRow
            {
                // Не сохраняется в настройки: строка живёт до следующего обновления списка.
                App = new ProtectedApp
                {
                    Name = name,
                    Path = path,
                    RuleName = rule.Rule,
                    IdentityKey = ProtectedAppIdentity.ComputeKey(path)
                },
                Name = name,
                Path = path,
                Status = ProtectionStatus.Untracked,
                StatusText = ProtectionStatus.Untracked.ToDisplayText(),
                AppliedText = "—",
                CanUpdatePath = false,
                CanAdopt = true,
                CanReinstall = false
            });
        }
    }

    private static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path); } catch { return path; }
    }

    /// <summary>
    /// Берёт найденное правило под наблюдение: заводит запись в списке защищённых программ.
    /// UAC не нужен — правило в Windows уже есть, меняется только список приложения.
    /// </summary>
    private async void AdoptRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not ProtectedAppRow row)
        {
            return;
        }

        var app = new ProtectedApp
        {
            Name = row.App.Name,
            Path = row.App.Path,
            RuleName = row.App.RuleName,
            IdentityKey = row.App.IdentityKey,
            AddedAt = DateTimeOffset.Now,
            // Правило существует и проверено живым запросом — значит оно применено, дата известна лишь приблизительно.
            RulesAppliedAt = DateTimeOffset.Now
        };

        // В списке одна запись на программу: вторая с тем же ключом склеится с первой при сохранении,
        // и кнопка молча ничего не сделает (T-473). Говорим об этом, а не пишем «добавлена».
        var key = ProtectedAppIdentity.KeyOf(app);
        var existing = _settings.ProtectedApps.FirstOrDefault(a =>
            string.Equals(ProtectedAppIdentity.KeyOf(a), key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            System.Windows.MessageBox.Show(this,
                $"«{existing.Name}» уже в списке защищённых — с путём {existing.Path}. Это правило другой версии той же "
                + "программы, отдельной строкой его не взять. Если оно больше не нужно, сними его кнопкой «Удалить».",
                "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshProtectedAppsAsync(logIssues: false);
            return;
        }

        _settings.ProtectedApps.Add(app);
        await _settingsService.SaveAsync(_settings);

        if (!_settings.ProtectedApps.Any(a => string.Equals(a.RuleName, app.RuleName, StringComparison.OrdinalIgnoreCase)))
        {
            FooterText.Text = $"«{app.Name}» не удалось добавить в список: запись слилась с уже существующей.";
            await RefreshProtectedAppsAsync(logIssues: false);
            return;
        }

        await LogKillSwitchEventAsync("rule_adopted",
            $"правило взято под наблюдение: {app.Name} ({app.RuleName})", app);
        FooterText.Text = $"«{app.Name}» добавлена в список — правило уже действовало, UAC не потребовался.";
        await RefreshProtectedAppsAsync(logIssues: false);
    }

    /// <summary>
    /// Protection path checks run independently of VPN health checks: a stale firewall path must
    /// still be reported when monitoring is stopped or a network probe hangs.
    /// </summary>
    private async void ProtectedAppsTimer_Tick(object? sender, EventArgs e)
    {
        if (_protectedAppsRefreshInFlight)
        {
            return;
        }

        _protectedAppsRefreshInFlight = true;
        try
        {
            await MaybeRefreshProtectedOnChangeAsync();
            _protectedAppsWatchErrorShown = false;
        }
        catch (Exception)
        {
            if (!_protectedAppsWatchErrorShown)
            {
                _protectedAppsWatchErrorShown = true;
                ShowNotification("⚠️ Проверка защиты не удалась",
                    "Не удалось проверить пути защищённых программ. Открой вкладку «Защищённые приложения».",
                    Forms.ToolTipIcon.Error, bypassCooldown: true, respectQuietStart: false);
            }
        }
        finally
        {
            _protectedAppsRefreshInFlight = false;
        }
    }

    private async Task MaybeRefreshProtectedOnChangeAsync()
    {
        if (_settings.ProtectedApps.Count == 0)
        {
            return;
        }

        var changed = VersionedProtectedAppPathResolver.Resolve(_settings.ProtectedApps).Count > 0;
        foreach (var app in _settings.ProtectedApps)
        {
            var key = AppIdentityKey(app);
            var exists = SafeExists(app.Path);
            if (_lastKnownExists.TryGetValue(key, out var previous) && previous != exists)
            {
                changed = true;
            }
            else if (!_lastKnownExists.ContainsKey(key))
            {
                _lastKnownExists[key] = exists;
            }
        }

        foreach (var (path, state) in _previousVersionInUse)
        {
            if (PreviousVersionUsage.Check(path, state.NewerPath).InUse != state.InUse)
            {
                changed = true;
                break;
            }
        }

        if (changed)
        {
            await RefreshProtectedAppsAsync(logIssues: true);
        }
    }

    /// <summary>Stable identity across version updates: MSIX family name, CLI folder, VS Code extension id, else path.</summary>
    private static string AppIdentityKey(ProtectedApp app) => ProtectedAppIdentity.KeyOf(app);

    private static bool SafeExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }

    private async Task LogKillSwitchEventAsync(string action, string description, ProtectedApp? app = null)
    {
        var monitorEvent = new MonitorEvent
        {
            Timestamp = DateTimeOffset.Now,
            Status = _currentStatus,
            Description = description,
            Category = "killswitch",
            Action = action,
            AppName = app?.Name,
            AppPath = app?.Path
        };

        _events.Insert(0, monitorEvent);
        while (_events.Count > 100)
        {
            _events.RemoveAt(_events.Count - 1);
        }

        try
        {
            await _logService.WriteEventAsync(monitorEvent, _settings, CancellationToken.None);
        }
        catch
        {
            // Logging must never break the kill-switch UI flow.
        }
    }

    private static string ResolveAppName(string path) => ProtectedAppNaming.Resolve(path);

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
