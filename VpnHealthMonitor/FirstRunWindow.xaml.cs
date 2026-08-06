using System.Windows;

namespace VpnHealthMonitor;

/// <summary>
/// Экран первого запуска (T-334): объясняет kill switch, права администратора и путь возврата
/// интернета ДО того, как человек увидит первый запрос UAC. Показывается один раз — маркер пишет
/// вызывающий код (App.xaml.cs) после закрытия, вне зависимости от способа закрытия.
/// </summary>
public partial class FirstRunWindow : Window
{
    public FirstRunWindow()
    {
        InitializeComponent();
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
