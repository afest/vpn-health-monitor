using System;
using System.Text.Json;
using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Тихий старт (T-411). Гейт глушит доставку балуна на старте, пока VPN-клиент поднимает туннель,
/// и обязан закончиться в обе стороны: сеть поднялась — шума не было; проблема осталась — после
/// таймаута приходит обычный сигнал. Тест держит именно этот контракт: молчаливо «съеденный»
/// красный статус в приложении безопасности хуже, чем лишний балун.
/// </summary>
public class QuietStartGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(5));

    [Fact]
    public void Inactive_UntilBegin()
    {
        var gate = new QuietStartGate();

        Assert.False(gate.IsActive);
        Assert.False(gate.ShouldSuppress(T0));
        Assert.Equal(QuietStartOutcome.Inactive, gate.ReportCycle(MonitorStatus.VpnDown, T0));
    }

    [Fact]
    public void FirstHealthyCycle_EndsQuietStart_WithoutBalloon()
    {
        var gate = new QuietStartGate(TimeSpan.FromSeconds(90));
        gate.Begin(T0);

        Assert.Equal(QuietStartOutcome.Quiet, gate.ReportCycle(MonitorStatus.NoInternet, T0.AddSeconds(5)));
        Assert.Equal(QuietStartOutcome.ReleasedByHealthyCycle, gate.ReportCycle(MonitorStatus.Ok, T0.AddSeconds(12)));

        Assert.False(gate.IsActive);
        Assert.False(gate.ShouldSuppress(T0.AddSeconds(13)));
    }

    [Fact]
    public void Timeout_ReleasesWithProblemStillOn()
    {
        var gate = new QuietStartGate(TimeSpan.FromSeconds(90));
        gate.Begin(T0);

        Assert.Equal(QuietStartOutcome.Quiet, gate.ReportCycle(MonitorStatus.VpnDown, T0.AddSeconds(89)));
        Assert.Equal(QuietStartOutcome.ReleasedByTimeout, gate.ReportCycle(MonitorStatus.VpnDown, T0.AddSeconds(90)));

        Assert.False(gate.IsActive);
    }

    [Fact]
    public void Timeout_AlsoReleasesSuppressionWithoutAnyCycle()
    {
        // Циклы могут не доходить до отчёта вовсе (проверка падает с исключением), но глушилка
        // обязана отпустить сама — иначе балуны выключены до перезапуска приложения.
        var gate = new QuietStartGate(TimeSpan.FromSeconds(90));
        gate.Begin(T0);

        Assert.True(gate.ShouldSuppress(T0.AddSeconds(30)));
        Assert.False(gate.ShouldSuppress(T0.AddSeconds(91)));
        Assert.False(gate.IsActive);
    }

    [Fact]
    public void Cancel_StopsSuppressionImmediately()
    {
        var gate = new QuietStartGate();
        gate.Begin(T0);
        gate.Cancel();

        Assert.False(gate.IsActive);
        Assert.False(gate.ShouldSuppress(T0.AddSeconds(1)));
    }

    [Fact]
    public void Autostart_Command_QuotesPathWithSpaces()
    {
        // Путь установленной копии содержит пробелы; без кавычек Windows запускает «D:\Programs\VPN».
        Assert.Equal(
            "\"D:\\Programs\\VPN Health Monitor\\VpnHealthMonitor.exe\"",
            AutostartService.BuildCommand(@"D:\Programs\VPN Health Monitor\VpnHealthMonitor.exe"));
    }

    [Fact]
    public void Autostart_Command_DoesNotDoubleQuote()
    {
        Assert.Equal(
            "\"C:\\App\\VpnHealthMonitor.exe\"",
            AutostartService.BuildCommand("\"C:\\App\\VpnHealthMonitor.exe\""));
    }

    [Fact]
    public void StartupSettings_DefaultToOff()
    {
        var settings = new AppSettings();

        Assert.False(settings.LaunchWithWindows);
        Assert.False(settings.StartMonitoringOnLaunch);
        Assert.False(settings.StartMinimizedToTray);
    }

    [Fact]
    public void OldSettingsFile_WithoutStartupFields_KeepsPreviousBehaviour()
    {
        // settings.json, записанный до T-411: полей нет вообще. Читается без ошибки и не включает
        // ни автозапуск, ни автостарт мониторинга — приложение ведёт себя как раньше.
        const string json = """
        {
          "IntervalSeconds": 5,
          "MinimizeToTrayOnClose": true
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(settings);
        Assert.False(settings!.LaunchWithWindows);
        Assert.False(settings.StartMonitoringOnLaunch);
        Assert.False(settings.StartMinimizedToTray);
        Assert.True(settings.MinimizeToTrayOnClose);
    }
}
