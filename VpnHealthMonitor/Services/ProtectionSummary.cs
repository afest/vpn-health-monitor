using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Счёт защищённых программ для блока в верхней плашке: сколько под защитой, сколько без неё.
///
/// «Без защиты» — всё, что не закрывает прямой выход программы, которая есть на диске: правило
/// пропало или не применено, указывает на старую версию, не совпадает с ожидаемым. Программа,
/// файла которой нет, считается отдельно: она не утечёт, но строка требует внимания.
/// Строки «правило вне списка» и правила старых версий в счёт не входят — это не программы
/// из списка, а следы в фаерволе.
/// </summary>
public sealed record ProtectionSummary(int Total, int Protected, int Unprotected, int Missing)
{
    public static ProtectionSummary Empty { get; } = new(0, 0, 0, 0);

    public bool HasUnprotected => Unprotected > 0;

    public static ProtectionSummary From(IEnumerable<ProtectionStatus> trackedStatuses)
    {
        int total = 0, ok = 0, unprotected = 0, missing = 0;
        foreach (var status in trackedStatuses)
        {
            total++;
            switch (status)
            {
                case ProtectionStatus.Protected:
                    ok++;
                    break;
                case ProtectionStatus.FileNotFound:
                    missing++;
                    break;
                default:
                    unprotected++;
                    break;
            }
        }

        return new ProtectionSummary(total, ok, unprotected, missing);
    }
}

public enum AutoRepairKind
{
    None,

    /// <summary>Найдена новая версия программы — перепривязать правило к новому пути.</summary>
    UpdatePath,

    /// <summary>Правило было применено, а теперь его нет или оно не совпадает — поставить заново.</summary>
    Reapply
}

/// <summary>
/// Решает, можно ли восстановить защиту без участия человека, кроме подтверждения UAC.
///
/// Восстанавливается только то, что СЛЕТЕЛО: правило, которое раньше стояло. Программа, для которой
/// правила ни разу не применяли или которые сняли кнопкой «Отключить защиту» (у неё стирается дата
/// применения), автоматически не трогается — иначе приложение спорило бы с решением человека.
/// Пропажу правила видно только при живом чтении фаервола: статус «по записи приложения» её не
/// показывает, и решать по нему нельзя.
/// </summary>
public static class ProtectionAutoRepair
{
    public static AutoRepairKind Decide(ProtectionStatus status, bool canVerifyLive, bool wasApplied, bool hasNewPath)
    {
        if (status == ProtectionStatus.PathChanged)
        {
            return hasNewPath ? AutoRepairKind.UpdatePath : AutoRepairKind.None;
        }

        if ((status is ProtectionStatus.RulesNotApplied or ProtectionStatus.Error) && canVerifyLive && wasApplied)
        {
            return AutoRepairKind.Reapply;
        }

        return AutoRepairKind.None;
    }
}
