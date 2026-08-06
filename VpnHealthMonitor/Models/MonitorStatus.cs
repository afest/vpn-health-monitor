namespace VpnHealthMonitor.Models;

public enum MonitorStatus
{
    Unknown,
    Ok,
    NoInternet,
    LeakRisk,
    IpChanged,
    CountryChanged,
    CountryUnknown,
    Degraded,
    CheckFailed,
    VpnDown
}

public static class MonitorStatusExtensions
{
    public static string ToDisplayText(this MonitorStatus status)
    {
        return status switch
        {
            MonitorStatus.Ok => "OK",
            MonitorStatus.NoInternet => "НЕТ ИНТЕРНЕТА",
            MonitorStatus.LeakRisk => "РИСК УТЕЧКИ",
            MonitorStatus.IpChanged => "IP ИЗМЕНИЛСЯ",
            MonitorStatus.CountryChanged => "СТРАНА ИЗМЕНИЛАСЬ",
            MonitorStatus.CountryUnknown => "СТРАНА НЕ ОПРЕДЕЛЕНА",
            MonitorStatus.Degraded => "СЕТЬ ПРОСЕЛА",
            MonitorStatus.CheckFailed => "ПРОВЕРКА НЕ УДАЛАСЬ",
            MonitorStatus.VpnDown => "VPN ОТКЛЮЧЁН",
            _ => "НЕИЗВЕСТНО"
        };
    }

    public static bool IsProblem(this MonitorStatus status)
    {
        return status is MonitorStatus.NoInternet
            or MonitorStatus.LeakRisk
            or MonitorStatus.Degraded
            or MonitorStatus.CheckFailed
            or MonitorStatus.VpnDown;
    }
}
