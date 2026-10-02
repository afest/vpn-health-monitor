using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>Что делать с результатом одной проверки.</summary>
public enum FailureFilterAction
{
    /// <summary>Это не провал связи — показываем как обычно.</summary>
    NotAFailure,

    /// <summary>Провал ещё не подтверждён: вердикт прежний, перепроверка на следующем цикле.</summary>
    Hold,

    /// <summary>Провал подтверждён или ждать нельзя — показываем сразу.</summary>
    Confirmed
}

/// <summary>Почему провал показан, а не придержан.</summary>
public enum FailureConfirmation
{
    None,

    /// <summary>Несколько неудачных проверок подряд.</summary>
    Consecutive,

    /// <summary>Неудачи идут с перерывами, но часто — серия, а не случайность.</summary>
    Repeated,

    /// <summary>Вместе со сбоем ушёл маршрут с VPN-адаптера или сменился внешний IPv4.</summary>
    RouteOrAddressChanged,

    /// <summary>Windows не видит маршрута наружу вообще — сбой физической сети, а не проверки.</summary>
    NoRoute,

    /// <summary>Ручная проверка: её жмут, когда хотят правду сейчас.</summary>
    Manual
}

public sealed record FailureFilterDecision(
    FailureFilterAction Action,
    FailureConfirmation Reason,
    int FailuresInWindow);

/// <summary>
/// Одна неудачная проверка связи — ещё не провал (T-473).
/// <para>
/// В сентябре журнал набирал до 20 ложных «интернет пропал» и «VPN отключён» в день: раз в ~301 с
/// где-то ниже VPN сеть замирает на несколько секунд, и в одном цикле разом отказывали IP API,
/// HTTP-пробы и даже ICMP. Сетка одна и та же на HideMe, Karing и голом Wi-Fi, поэтому это не
/// обрыв туннеля, а монитор кричал с первой же неудачи.
/// </para>
/// <para>
/// Провал связи засчитывается после <see cref="ConsecutiveFailuresToConfirm"/> неудачных проверок
/// подряд или <see cref="RepeatedFailuresToConfirm"/> за <see cref="RepeatWindow"/>. Второе правило
/// держит видимыми серии, когда провайдер «замораживает» соединения с VPN-сервером: каждая неудача
/// там одиночная, но они идут каждые 30–60 с. Пятиминутная сетка его не достаёт — три её сбоя
/// растянуты минимум на 10 минут.
/// </para>
/// <para>
/// Фильтр касается только провалов связи. Риск утечки сюда не попадает вовсе, а провал, который
/// пришёл вместе с уходом маршрута с VPN-адаптера, новым IPv4 или пропажей маршрута, показывается
/// сразу: это ровно те события, ради которых монитор существует. Правила фаервола от вердикта не
/// зависят и фильтром не затрагиваются.
/// </para>
/// </summary>
public sealed class ConnectivityFailureFilter
{
    public const int ConsecutiveFailuresToConfirm = 2;
    public const int RepeatedFailuresToConfirm = 3;
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

    private readonly Queue<DateTimeOffset> _recentFailures = new();
    private int _consecutiveFailures;
    private int _silentCycles;

    public static bool IsConnectivityFailure(MonitorStatus status)
        => status is MonitorStatus.NoInternet or MonitorStatus.VpnDown or MonitorStatus.CheckFailed;

    /// <summary>
    /// Засчитана ли пропажа интернета для события «интернет пропал». Оно живёт отдельно от вердикта:
    /// бывает цикл, где внешний IPv4 ответил, а HTTP-пробы и ping нет, — вердикт остаётся прежним, а
    /// событие в журнал раньше уходило. Это та же сетка сбоев, поэтому правило то же: со второго
    /// молчаливого цикла подряд, а при показанном «НЕТ ИНТЕРНЕТА» — сразу, чтобы журнал не спорил
    /// с вердиктом. Вызывать после <see cref="Observe"/> для того же замера.
    /// </summary>
    public bool IsInternetLossConfirmed(NetworkSnapshot snapshot, MonitorStatus shownStatus)
        => !Responds(snapshot)
            && (shownStatus == MonitorStatus.NoInternet || _silentCycles >= ConsecutiveFailuresToConfirm);

    /// <param name="lastKnownIPv4">Последний внешний IPv4, который монитор уже видел.</param>
    /// <param name="manual">Ручная проверка — показывается без ожидания.</param>
    public FailureFilterDecision Observe(
        HealthResult result,
        NetworkSnapshot snapshot,
        AppSettings settings,
        string? lastKnownIPv4,
        bool manual = false)
    {
        var now = snapshot.CheckedAt;
        _silentCycles = Responds(snapshot) ? 0 : _silentCycles + 1;

        if (!IsConnectivityFailure(result.Status))
        {
            _consecutiveFailures = 0;
            return new FailureFilterDecision(FailureFilterAction.NotAFailure, FailureConfirmation.None, CountRecent(now));
        }

        _consecutiveFailures++;
        _recentFailures.Enqueue(now);
        var recent = CountRecent(now);

        var reason = manual ? FailureConfirmation.Manual
            : HasNoRoute(snapshot) ? FailureConfirmation.NoRoute
            : HealthEvaluator.DefaultRouteLeftExpectedVpn(snapshot, settings, result.RouteCheck)
              || ShowsNewAddress(snapshot, lastKnownIPv4) ? FailureConfirmation.RouteOrAddressChanged
            : _consecutiveFailures >= ConsecutiveFailuresToConfirm ? FailureConfirmation.Consecutive
            : recent >= RepeatedFailuresToConfirm ? FailureConfirmation.Repeated
            : FailureConfirmation.None;

        var action = reason == FailureConfirmation.None ? FailureFilterAction.Hold : FailureFilterAction.Confirmed;
        return new FailureFilterDecision(action, reason, recent);
    }

    public void Reset()
    {
        _consecutiveFailures = 0;
        _silentCycles = 0;
        _recentFailures.Clear();
    }

    private int CountRecent(DateTimeOffset now)
    {
        while (_recentFailures.Count > 0 && now - _recentFailures.Peek() > RepeatWindow)
        {
            _recentFailures.Dequeue();
        }

        return _recentFailures.Count;
    }

    private static bool Responds(NetworkSnapshot snapshot)
        => snapshot.HttpAvailable || snapshot.PingSuccesses > 0;

    private static bool HasNoRoute(NetworkSnapshot snapshot)
        => string.IsNullOrWhiteSpace(snapshot.InterfaceName)
            || string.Equals(snapshot.InterfaceName, "Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool ShowsNewAddress(NetworkSnapshot snapshot, string? lastKnownIPv4)
        => snapshot.IpLookupSucceeded
            && !string.Equals(snapshot.ExternalIPv4, lastKnownIPv4, StringComparison.OrdinalIgnoreCase);
}
