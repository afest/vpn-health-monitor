using System;
using System.Globalization;
using System.Windows.Data;

namespace VpnHealthMonitor.Converters;

/// <summary>
/// Доля от высоты другого элемента. Нужен для потолка развёрнутого блока Сводки: считать его в
/// code-behind по ActualHeight нельзя — получается цикл разметки (высота блока меняет высоту грида,
/// та пересчитывает потолок), WPF такой цикл обрывает и оставляет неверные размеры.
/// Привязка идёт к высоте TabControl, которая от содержимого блока не зависит.
/// </summary>
public sealed class RatioConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double source || double.IsNaN(source) || double.IsInfinity(source))
        {
            return double.PositiveInfinity;
        }

        var ratio = 0.45;
        if (parameter is string text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            ratio = parsed;
        }

        return Math.Max(120, source * ratio);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
