using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>Чем закончился тихий старт на этом цикле.</summary>
public enum QuietStartOutcome
{
    /// <summary>Тихого старта нет — обычная работа, балуны доставляются как всегда.</summary>
    Inactive,

    /// <summary>Тихий старт идёт: балун не доставляем.</summary>
    Quiet,

    /// <summary>Сеть поднялась, первый здоровый цикл прошёл. Шума не было — балун не нужен.</summary>
    ReleasedByHealthyCycle,

    /// <summary>Время вышло, а проблема осталась. Правду нужно доставить обычным балуном.</summary>
    ReleasedByTimeout
}

/// <summary>
/// Тихий старт: при автозапуске мониторинг стартует раньше, чем VPN-клиент успевает поднять туннель,
/// и каждый вход в Windows начинался бы с красного балуна. Гейт глушит <b>доставку уведомления</b>
/// на срок до первого здорового цикла или до таймаута — и только её.
/// <para>
/// Статус в окне, цвет иконки в трее и запись в лог остаются настоящими с первого же цикла: это
/// приложение безопасности, подавлять сам вердикт нельзя (правило единого вердикта, T-391).
/// </para>
/// <para>
/// «Здоровый цикл» — это <see cref="MonitorStatus.Ok"/>, а не «удался запрос внешнего IP». На старте
/// до туннеля запрос IP как раз удаётся — и даёт прямой выход, то есть ровно тот стартовый шум,
/// ради которого гейт и заводился. Если проблема настоящая и не проходит, её вернёт таймаут.
/// </para>
/// </summary>
public sealed class QuietStartGate
{
    public const int DefaultTimeoutSeconds = 90;

    private readonly TimeSpan _timeout;
    private DateTimeOffset? _startedAt;

    public QuietStartGate(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    public bool IsActive => _startedAt.HasValue;

    /// <summary>Начать тихий старт. Вызывается только при автозапуске мониторинга, не при ручном «Старт».</summary>
    public void Begin(DateTimeOffset now) => _startedAt = now;

    /// <summary>Прекратить досрочно: ручная остановка мониторинга, выход из приложения.</summary>
    public void Cancel() => _startedAt = null;

    /// <summary>
    /// Отчёт о завершённом цикле проверки. Возвращает, что делать с балуном, и сам закрывает гейт,
    /// когда тот отработал.
    /// </summary>
    public QuietStartOutcome ReportCycle(MonitorStatus status, DateTimeOffset now)
    {
        if (_startedAt is not { } startedAt)
        {
            return QuietStartOutcome.Inactive;
        }

        if (status == MonitorStatus.Ok)
        {
            _startedAt = null;
            return QuietStartOutcome.ReleasedByHealthyCycle;
        }

        if (now - startedAt >= _timeout)
        {
            _startedAt = null;
            return QuietStartOutcome.ReleasedByTimeout;
        }

        return QuietStartOutcome.Quiet;
    }

    /// <summary>
    /// Глушить ли доставку балуна прямо сейчас. Отдельно от <see cref="ReportCycle"/>: балуны приходят
    /// не только из цикла проверки (смена IP, статус защищённых программ), а глушить надо все.
    /// </summary>
    public bool ShouldSuppress(DateTimeOffset now)
    {
        if (_startedAt is not { } startedAt)
        {
            return false;
        }

        if (now - startedAt >= _timeout)
        {
            _startedAt = null;
            return false;
        }

        return true;
    }
}
