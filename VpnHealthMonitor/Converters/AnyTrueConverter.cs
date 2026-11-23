using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace VpnHealthMonitor.Converters;

/// <summary>
/// true, если хоть одно значение true. Кнопка строки доступна, когда операций нет, — или когда
/// операция идёт именно по ней: тогда она не серая, а с крутящимся индикатором.
/// </summary>
public sealed class AnyTrueConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Any(value => value is true);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
