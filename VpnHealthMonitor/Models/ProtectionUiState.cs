using System.ComponentModel;

namespace VpnHealthMonitor.Models;

/// <summary>
/// Общее состояние вкладки «Приложения» для кнопок в строках списка: пока идёт операция с правилами,
/// кнопки строк заблокированы (сам список остаётся читаемым и прокручивается).
/// </summary>
public sealed class ProtectionUiState : INotifyPropertyChanged
{
    private bool _isIdle = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsIdle
    {
        get => _isIdle;
        set
        {
            if (_isIdle == value)
            {
                return;
            }

            _isIdle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIdle)));
        }
    }
}
