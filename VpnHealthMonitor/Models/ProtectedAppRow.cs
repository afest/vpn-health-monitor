using System.ComponentModel;

namespace VpnHealthMonitor.Models;

/// <summary>Действие над строкой, которое сейчас выполняется: на его кнопке крутится индикатор.</summary>
public static class RowAction
{
    public const string Reinstall = "reinstall";
    public const string UpdatePath = "update_path";
    public const string Remove = "remove";
}

/// <summary>Display row for the "Защищённые приложения" list (rebuilt on each status refresh).</summary>
public sealed class ProtectedAppRow : INotifyPropertyChanged
{
    private string? _workingAction;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Что сейчас делается с этой строкой (<see cref="RowAction"/>), null — ничего. Меняется на лету,
    /// поэтому с уведомлением: индикатор на кнопке включается в момент нажатия, без перерисовки списка.
    /// </summary>
    public string? WorkingAction
    {
        get => _workingAction;
        set
        {
            if (_workingAction == value)
            {
                return;
            }

            _workingAction = value;
            foreach (var name in new[] { nameof(WorkingAction), nameof(IsReinstalling), nameof(IsUpdatingPath), nameof(IsRemoving) })
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    public bool IsReinstalling => _workingAction == RowAction.Reinstall;

    public bool IsUpdatingPath => _workingAction == RowAction.UpdatePath;

    public bool IsRemoving => _workingAction == RowAction.Remove;

    public ProtectedApp App { get; init; } = new();

    public string Name { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    /// <summary>Состояние защиты. Разметка раскрашивает строку по нему, а не по локализованному тексту.</summary>
    public ProtectionStatus Status { get; init; }

    public string StatusText { get; init; } = string.Empty;

    /// <summary>Знак состояния — второе отличие помимо цвета.</summary>
    public string StatusGlyph => Status.ToGlyph();

    /// <summary>Расшифровка состояния в tooltip'е ячейки.</summary>
    public string StatusHint => Status.ToHint();

    public string AppliedText { get; init; } = string.Empty;

    /// <summary>True when this is a moved Store/MSIX package and a new path was resolved — the "Обновить путь" action applies.</summary>
    public bool CanUpdatePath { get; init; }

    /// <summary>Resolved current exe path for a moved Store/MSIX package (null when not applicable).</summary>
    public string? ResolvedNewPath { get; init; }

    /// <summary>True for a rule found in Windows that no entry in the settings list claims.</summary>
    public bool CanAdopt { get; init; }

    /// <summary>False for untracked rows: there is no stored entry to re-apply rules for.</summary>
    public bool CanReinstall { get; init; } = true;

    /// <summary>
    /// False для правила старой версии, из которой ещё запущены процессы: снять его — выпустить
    /// их мимо VPN. Кнопка появится сама, когда старые процессы закроются.
    /// </summary>
    public bool CanRemove { get; init; } = true;

    /// <summary>Для строки старой версии — путь версии из списка, которая её сменила.</summary>
    public string? NewerVersionPath { get; init; }

    public bool IsPreviousVersion => Status is ProtectionStatus.PreviousVersionInUse or ProtectionStatus.PreviousVersionIdle;

    public bool IsPreviousVersionInUse => Status == ProtectionStatus.PreviousVersionInUse;
}
