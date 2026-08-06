using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
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
    private readonly NetworkCheckService _networkCheckService = new();
    private readonly LogService _logService = new();
    private readonly FirewallService _firewallService = new();
    private readonly AppxPathResolver _appxResolver = new();
    private readonly AdapterInventory _adapterInventory = new();
    private readonly RollingHealthWindow _rollingWindow = new(20);
    private readonly ObservableCollection<MonitorEvent> _events = new();
    private readonly ObservableCollection<ProtectedAppRow> _protectedAppRows = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly Dictionary<TrayIconKind, Drawing.Icon> _trayIcons = new();
    private readonly Dictionary<string, bool> _lastKnownExists = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _notifiedPathChange = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings = new();
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private Forms.NotifyIcon? _trayIcon;
    private NetworkSnapshot? _lastSnapshot;
    private MonitorStatus _currentStatus = MonitorStatus.Unknown;
    private string? _lastIp;
    private DateTimeOffset? _lastSuccessfulCheckAt;
    private DateTimeOffset? _lastIpChangeAt;
    private DateTimeOffset? _lastNotificationAt;
    private DateTimeOffset? _healthySince;
    private DateTimeOffset? _problemStartedAt;
    private TimeSpan _totalProblemTime = TimeSpan.Zero;
    private int _incidentCount;
    private bool? _internetWasAvailable;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        EventsList.ItemsSource = _events;
        ProtectedAppsList.ItemsSource = _protectedAppRows;
        InitializeTrayIcon();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        UpdateStatusBanner(MonitorStatus.Unknown, "Готово. Запусти проверку или включи мониторинг.");
        UpdateDashboard(null, null);
        UpdateAdminStatus();
        await RefreshProtectedAppsAsync(logIssues: false);
        await RefreshAdapterChoicesAsync();
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
                bypassCooldown: true);
            return;
        }

        _monitoringCts?.Cancel();

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
        ResetSessionStats();

        _monitoringCts = new CancellationTokenSource();
        SetMonitoringState(true);

        await AddEventAsync("мониторинг запущен", _currentStatus, _lastSnapshot, CancellationToken.None);
        _monitoringTask = MonitorLoopAsync(_monitoringCts.Token);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopMonitoringAsync("мониторинг остановлен");
    }

    private async void RunCheckButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsFromUiAsync();
        await RunSingleCheckAsync("manual-check", CancellationToken.None);
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

        _settings.Baseline = new BaselineInfo
        {
            IPv4 = _lastSnapshot.ExternalIPv4,
            Country = _lastSnapshot.Country,
            Provider = _lastSnapshot.Provider,
            Asn = _lastSnapshot.Asn,
            InterfaceName = _lastSnapshot.InterfaceName,
            Timestamp = DateTimeOffset.Now
        };

        // Baseline = "вот так выглядит норма", поэтому текущий провайдер сразу становится разрешённым:
        // иначе включённый риск по провайдеру сработал бы на собственном же VPN сразу после настройки.
        RememberProvider(_lastSnapshot.Asn, _lastSnapshot.Provider);
        _settings.ExpectedInterfaceName = _lastSnapshot.InterfaceName ?? _settings.ExpectedInterfaceName;

        if (_settings.ExpectedPublicIPv4.Count > 0
            && !_settings.ExpectedPublicIPv4.Contains(_lastSnapshot.ExternalIPv4, StringComparer.OrdinalIgnoreCase))
        {
            _settings.ExpectedPublicIPv4.Add(_lastSnapshot.ExternalIPv4);
        }

        await _settingsService.SaveAsync(_settings);
        UpdateSettingsControls();
        UpdateDashboard(_lastSnapshot, null);
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
        UpdateDashboard(_lastSnapshot, null);
    }

    private async void ReloadSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadSettingsAsync();
        UpdateDashboard(_lastSnapshot, null);
        await RefreshProtectedAppsAsync(logIssues: false);
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

    private async Task LoadSettingsAsync()
    {
        _settings = await _settingsService.LoadAsync();
        UpdateSettingsControls();
        FooterText.Text = $"Настройки: {AppPaths.SettingsPath} | Логи: {_settings.LogsFolderPath}";
    }

    private async Task SaveSettingsFromUiAsync()
    {
        SaveSettingsFromUi();
        await _settingsService.SaveAsync(_settings);
        FooterText.Text = $"Настройки: {AppPaths.SettingsPath} | Логи: {_settings.LogsFolderPath}";
    }

    private void SaveSettingsFromUi()
    {
        _settings.IntervalSeconds = ParseInt(IntervalTextBox.Text, 5, 1, 3600);
        _settings.ExpectedCountry = ExpectedCountryTextBox.Text.Trim();
        _settings.TreatCountryMismatchAsLeakRisk = CountryRiskCheckBox.IsChecked == true;
        _settings.ExpectedPublicIPv4 = SplitValues(ExpectedIpsTextBox.Text);
        _settings.TreatUnexpectedIPv4AsLeakRisk = IpRiskCheckBox.IsChecked == true;
        _settings.AllowIpChangesWithinExpectedCountry = AllowIpChangesCheckBox.IsChecked == true;
        _settings.ExpectedInterfaceName = (ExpectedInterfaceBox.Text ?? string.Empty).Trim();
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
        ExpectedInterfaceBox.Text = GetExpectedInterfaceName(_settings);
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
        IpApiEndpointsTextBox.Text = string.Join(Environment.NewLine, _settings.IpApiEndpoints);
        PingThresholdTextBox.Text = _settings.DegradedPingThresholdMs.ToString();
        LossThresholdTextBox.Text = _settings.DegradedPacketLossThresholdPercent.ToString("0.#");
        LogsFolderTextBox.Text = _settings.LogsFolderPath;
        AutosaveLogsCheckBox.IsChecked = _settings.AutosaveLogs;
        BaselineText.Text = FormatBaseline(_settings.Baseline);
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RunSingleCheckAsync("scheduled check", cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(_settings.IntervalSeconds), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task StopMonitoringAsync(string description)
    {
        if (_monitoringCts is null)
        {
            return;
        }

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
            var snapshot = await _networkCheckService.RunAsync(_settings, cancellationToken);
            _lastSnapshot = snapshot;
            _rollingWindow.Add(snapshot);

            await BackfillExpectedInterfaceAsync(snapshot, cancellationToken);
            var result = HealthEvaluator.Evaluate(snapshot, _rollingWindow, _settings);
            await HandleResultAsync(trigger, snapshot, result, cancellationToken);
            UpdateDashboard(snapshot, result);
            await MaybeRefreshProtectedOnChangeAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var fallbackSnapshot = new NetworkSnapshot
            {
                CheckedAt = DateTimeOffset.Now,
                Errors = new List<string> { ex.Message }
            };
            var fallbackResult = new HealthResult
            {
                Status = MonitorStatus.CheckFailed,
                Description = ex.Message
            };

            await HandleResultAsync(trigger, fallbackSnapshot, fallbackResult, CancellationToken.None);
            UpdateDashboard(fallbackSnapshot, fallbackResult);
        }
        finally
        {
            SetBusyState(false);
            _checkLock.Release();
        }
    }

    private async Task HandleResultAsync(
        string trigger,
        NetworkSnapshot snapshot,
        HealthResult result,
        CancellationToken cancellationToken)
    {
        var previousStatus = _currentStatus;
        var previousIp = _lastIp;
        var internetAvailable = snapshot.HttpAvailable || snapshot.PingSuccesses > 0;

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

        if (!string.IsNullOrWhiteSpace(previousIp)
            && !string.IsNullOrWhiteSpace(snapshot.ExternalIPv4)
            && !string.Equals(previousIp, snapshot.ExternalIPv4, StringComparison.OrdinalIgnoreCase))
        {
            _lastIpChangeAt = snapshot.CheckedAt;
            await AddEventAsync($"IP изменился: {previousIp} -> {snapshot.ExternalIPv4}", result.Status, snapshot, cancellationToken);
            // Балун только по факту смены IP — шум; гейтится отдельным toggle (детект/лог выше не трогаются).
            // Смена страны идёт своим балуном через MaybeShowStatusNotification (CountryChanged), не этим.
            if (_settings.NotifyIpChanged)
            {
                ShowNotification(
                    "VPN Health Monitor: IP изменился",
                    $"{previousIp} -> {snapshot.ExternalIPv4}",
                    Forms.ToolTipIcon.Warning);
            }
        }

        _lastIp = snapshot.ExternalIPv4 ?? _lastIp;

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
        _lastSuccessfulCheckAt = null;
        _lastIpChangeAt = null;
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
            : CountryNames.ToDisplayName(_settings.ExpectedCountry);
        ExpectedIpText.Text = _settings.ExpectedPublicIPv4.Count == 0
            ? "Не задано"
            : string.Join(", ", _settings.ExpectedPublicIPv4);
        ExpectedInterfaceText.Text = string.IsNullOrWhiteSpace(GetExpectedInterfaceName(_settings))
            ? "Не задано"
            : GetExpectedInterfaceName(_settings);
        UpdateRouteCheckText(result?.RouteCheck ?? HealthEvaluator.GetRouteCheckState(_settings));
        LastSuccessText.Text = _lastSuccessfulCheckAt.HasValue ? FormatTime(_lastSuccessfulCheckAt.Value) : "Никогда";
        LastIpChangeText.Text = _lastIpChangeAt.HasValue ? FormatTime(_lastIpChangeAt.Value) : "Никогда";
        BaselineText.Text = FormatBaseline(_settings.Baseline);

        PingText.Text = _rollingWindow.AveragePingMs.HasValue
            ? $"{_rollingWindow.AveragePingMs.Value:0} ms ({_rollingWindow.Count}/20)"
            : $"н/д ({_rollingWindow.Count}/20)";
        PacketLossText.Text = _rollingWindow.Count == 0
            ? "н/д"
            : $"{_rollingWindow.PacketLossPercent:0.#}% ({_rollingWindow.Count}/20)";

        if (result is not null)
        {
            UpdateStatusBanner(result.Status, result.Description);
        }

        UpdateSessionStats();
    }

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
    }

    private void UpdateStatusBanner(MonitorStatus status, string description)
    {
        StatusText.Text = status.ToDisplayText();
        StatusDescriptionText.Text = description;
        StatusBanner.Background = status switch
        {
            MonitorStatus.Ok => new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 132, 75)),
            MonitorStatus.NoInternet => new SolidColorBrush(System.Windows.Media.Color.FromRgb(172, 52, 52)),
            MonitorStatus.LeakRisk => new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 67, 45)),
            MonitorStatus.VpnDown => new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 67, 45)),
            MonitorStatus.IpChanged => new SolidColorBrush(System.Windows.Media.Color.FromRgb(199, 128, 27)),
            MonitorStatus.CountryChanged => new SolidColorBrush(System.Windows.Media.Color.FromRgb(199, 128, 27)),
            MonitorStatus.CountryUnknown => new SolidColorBrush(System.Windows.Media.Color.FromRgb(96, 106, 118)),
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

        var openItem = new Forms.ToolStripMenuItem("Открыть", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
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
                Dispatcher.Invoke(ShowMainWindow);
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

    private void ShowMainWindow()
    {
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

    private void ShowNotification(
        string title,
        string message,
        Forms.ToolTipIcon icon,
        bool bypassCooldown = false)
    {
        if (!_settings.EnableWindowsNotifications || _trayIcon is null)
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

    private async Task BackfillExpectedInterfaceAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_settings.TreatDefaultRouteChangeAsLeakRisk
            || !string.IsNullOrWhiteSpace(GetExpectedInterfaceName(_settings))
            || !SnapshotMatchesExpectedCountry(snapshot)
            || !VpnInterfaceHeuristics.LooksLikeVpn(snapshot.InterfaceName))
        {
            return;
        }

        _settings.ExpectedInterfaceName = snapshot.InterfaceName ?? string.Empty;
        if (_settings.Baseline is not null && string.IsNullOrWhiteSpace(_settings.Baseline.InterfaceName))
        {
            _settings.Baseline.InterfaceName = snapshot.InterfaceName;
        }

        await _settingsService.SaveAsync(_settings, cancellationToken);
        UpdateSettingsControls();
    }

    private bool SnapshotMatchesExpectedCountry(NetworkSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(_settings.ExpectedCountry)
            || string.IsNullOrWhiteSpace(snapshot.Country))
        {
            return false;
        }

        return string.Equals(
            CountryNames.NormalizeCountryCode(_settings.ExpectedCountry),
            CountryNames.NormalizeCountryCode(snapshot.Country),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExpectedInterfaceName(AppSettings settings)
    {
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
        if (FirewallService.IsProcessElevated())
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
    }

    private void UpdateRouteCheckText(RouteCheckState state)
    {
        RouteCheckText.Text = state switch
        {
            RouteCheckState.Active => "Активна — маршрут сравнивается с VPN-адаптером.",
            RouteCheckState.NotApplicable =>
                "НЕ применима: ожидаемый интерфейс не похож на VPN-адаптер. В режиме системного прокси default route не меняется, "
                + "и эта проверка не сработает ни при каком состоянии VPN — полагайся на страну и внешний IP.",
            _ => "Выключена в настройках."
        };

        RouteCheckText.Foreground = state == RouteCheckState.NotApplicable
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB3, 0x47, 0x00))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x26, 0x2C));
    }

    /// <summary>Fills the expected-interface dropdown with the machine's real adapters, keeping any manual text.</summary>
    private async Task RefreshAdapterChoicesAsync()
    {
        try
        {
            var inventory = await _adapterInventory.ReadAsync(CancellationToken.None);
            var current = ExpectedInterfaceBox.Text;
            ExpectedInterfaceBox.ItemsSource = inventory.Adapters
                .Select(adapter => adapter.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ExpectedInterfaceBox.Text = current;
        }
        catch (Exception)
        {
            // Manual entry stays available — the dropdown is a convenience, not a requirement.
        }

        UpdateBlockedAdaptersText();
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
        if (_settings.ProtectedApps.Any(a => PathEquals(a.Path, path)))
        {
            System.Windows.MessageBox.Show(this, "Эта программа уже в списке защищённых.",
                "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var app = new ProtectedApp
        {
            Path = path,
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

        // Carry the new path/rule into the elevated call, but only commit to settings on success —
        // if the user cancels UAC the firewall is untouched, so the stored path must stay as-is.
        var pending = new ProtectedApp
        {
            Name = app.Name,
            Path = newPath,
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

            var result = await _firewallService.UpdatePathAsync(pending, oldRuleName, adapters, CancellationToken.None);

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
            if (result.Items.Any(i => i.State == "applied"))
            {
                app.RulesAppliedAt = DateTimeOffset.Now;
            }
            await _settingsService.SaveAsync(_settings);
            await LogKillSwitchEventAsync("path_updated",
                $"путь обновлён, правило переустановлено, старое снято: {app.Name} ({oldPath} → {newPath})", app);
            FooterText.Text = $"Путь «{app.Name}» обновлён: правило переустановлено, старое снято.";
            await RefreshProtectedAppsAsync(logIssues: false);
        }
        catch (Exception ex)
        {
            await LogKillSwitchEventAsync("admin_error", $"исключение при обновлении пути: {ex.Message}", app);
            System.Windows.MessageBox.Show(this, ex.Message, "VPN Health Monitor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.DataContext is not ProtectedAppRow row)
        {
            return;
        }

        var app = row.App;
        var confirm = System.Windows.MessageBox.Show(this,
            $"Удалить «{app.Name}» из защиты и снять её firewall-правило?",
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
        var movedQueryPaths = new List<string>();
        var cliQueryPaths = new List<string>();
        var vscodeExtensionQueryPaths = new List<string>();
        foreach (var app in _settings.ProtectedApps)
        {
            var status = canVerifyLive
                ? FirewallService.ComputeStatus(app, rules!)
                : FirewallService.ComputeStatusFromRecord(app);
            baseStatus[app] = status;
            if (status == ProtectionStatus.FileNotFound)
            {
                if (AppxPathResolver.IsPackagedPath(app.Path))
                {
                    movedQueryPaths.Add(app.Path);
                }
                else if (CliPathResolver.IsCliVersionedPath(app.Path))
                {
                    cliQueryPaths.Add(app.Path);
                }
                else if (VsCodeExtensionPathResolver.IsVersionedExtensionPath(app.Path))
                {
                    vscodeExtensionQueryPaths.Add(app.Path);
                }
            }
        }

        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (movedQueryPaths.Count > 0)
        {
            foreach (var kvp in await _appxResolver.ResolveMovedPathsAsync(movedQueryPaths, CancellationToken.None))
            {
                moved[kvp.Key] = kvp.Value;
            }
        }
        if (cliQueryPaths.Count > 0)
        {
            foreach (var kvp in CliPathResolver.ResolveMovedPaths(cliQueryPaths))
            {
                moved[kvp.Key] = kvp.Value;
            }
        }
        if (vscodeExtensionQueryPaths.Count > 0)
        {
            foreach (var kvp in VsCodeExtensionPathResolver.ResolveMovedPaths(vscodeExtensionQueryPaths))
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
            if (status == ProtectionStatus.FileNotFound && moved.TryGetValue(app.Path, out var resolved))
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
                        "⚠️ Защита приложения не действует",
                        $"у «{app.Name}» сменился путь после обновления — kill-switch больше НЕ закрывает прямой выход. Открой вкладку «Защищённые приложения» и нажми «Обновить путь».",
                        Forms.ToolTipIcon.Error,
                        bypassCooldown: true);
                    await LogKillSwitchEventAsync("path_changed",
                        $"путь изменился после обновления, защита не действует: {app.Name} ({app.Path} → {newPath})", app);
                }
            }

            _protectedAppRows.Add(new ProtectedAppRow
            {
                App = app,
                Name = app.Name,
                Path = app.Path,
                StatusText = status.ToDisplayText(),
                AppliedText = app.RulesAppliedAt.HasValue ? FormatTime(app.RulesAppliedAt.Value) : "—",
                CanUpdatePath = status == ProtectionStatus.PathChanged,
                ResolvedNewPath = newPath
            });

            _lastKnownExists[AppIdentityKey(app)] = SafeExists(app.Path);
        }

        _notifiedPathChange = stale;
        UpdateAdminStatus(canVerifyLive);
        UpdateBlockedAdaptersText();
    }

    /// <summary>
    /// Cheap per-tick guard: only escalate to a full refresh (which spawns PowerShell) when a
    /// protected exe appears/disappears — e.g. a Store/MSIX app updated to a new versioned path.
    /// </summary>
    private async Task MaybeRefreshProtectedOnChangeAsync()
    {
        if (_settings.ProtectedApps.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var app in _settings.ProtectedApps)
        {
            var key = AppIdentityKey(app);
            var exists = SafeExists(app.Path);
            if (_lastKnownExists.TryGetValue(key, out var previous))
            {
                if (previous != exists)
                {
                    changed = true;
                }
            }
            else
            {
                _lastKnownExists[key] = exists;
            }
        }

        if (changed)
        {
            await RefreshProtectedAppsAsync(logIssues: true);
        }
    }

    /// <summary>Stable identity across version updates: MSIX family name, CLI folder, VS Code extension id, else path.</summary>
    private static string AppIdentityKey(ProtectedApp app)
    {
        if (AppxPathResolver.IsPackagedPath(app.Path)
            && AppxPathResolver.TryParse(app.Path, out var folder, out _)
            && AppxPathResolver.GetFamilyName(folder) is { } family)
        {
            return "appx:" + family;
        }

        if (CliPathResolver.GetStableKey(app.Path) is { } cliKey)
        {
            return "cli:" + cliKey;
        }

        if (VsCodeExtensionPathResolver.GetStableKey(app.Path) is { } vscodeExtensionKey)
        {
            return "vscode-ext:" + vscodeExtensionKey;
        }

        return "path:" + (app.Path ?? string.Empty).ToLowerInvariant();
    }

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

    private static string ResolveAppName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription.Trim();
            }
        }
        catch
        {
            // fall through to file name
        }

        return Path.GetFileName(path);
    }

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
