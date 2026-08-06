using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;

namespace VpnHealthMonitor;

/// <summary>
/// Shows which adapters the kill-switch rules will bind to, before any rule is created (T-323).
/// The classifier can miss in both directions — a real NIC reported as virtual stays open, a VPN adapter
/// that looks like plain Ethernet would cut the app off entirely — and only the user recognises their own
/// hardware by name, so the proposed selection stays editable. Cancel changes nothing.
/// </summary>
public partial class ConfirmAdaptersWindow : Window
{
    private readonly ObservableCollection<AdapterChoice> _choices = new();

    /// <param name="preselected">
    /// Adapter names to tick instead of the classifier's proposal — the user's previous manual choice,
    /// when the machine's adapters have not changed since. Null or empty falls back to the proposal.
    /// </param>
    public ConfirmAdaptersWindow(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        string? warning,
        IReadOnlyCollection<string>? preselected = null)
    {
        InitializeComponent();

        var pinned = preselected is { Count: > 0 }
            ? new HashSet<string>(preselected, StringComparer.OrdinalIgnoreCase)
            : null;

        bool ShouldTick(NetworkAdapterInfo adapter) =>
            pinned is null ? AdapterClassifier.IsBlockable(adapter) : pinned.Contains(adapter.Name);

        foreach (var adapter in adapters
            .OrderByDescending(ShouldTick)
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _choices.Add(new AdapterChoice(adapter, ShouldTick(adapter), AdapterClassifier.IsBlockable(adapter)));
        }

        AdapterList.ItemsSource = _choices;

        if (!string.IsNullOrWhiteSpace(warning))
        {
            WarningText.Text = warning;
            WarningText.Visibility = Visibility.Visible;
        }
        else if (_choices.All(choice => !choice.IsSelected))
        {
            WarningText.Text = "Ни один адаптер не распознан как путь прямого выхода. Отметь свой сетевой адаптер вручную — "
                + "без отметок правила не создаются и защиты не будет.";
            WarningText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Adapter names the user left checked. Empty means "do nothing".</summary>
    public IReadOnlyList<string> SelectedAdapters =>
        _choices.Where(choice => choice.IsSelected).Select(choice => choice.Name).ToList();

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAdapters.Count == 0)
        {
            System.Windows.MessageBox.Show(this,
                "Не отмечен ни один адаптер — блокировать нечего, защита не появится. Отметь адаптеры или нажми «Отмена».",
                "VPN Health Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public sealed class AdapterChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public AdapterChoice(NetworkAdapterInfo adapter, bool isSelected, bool classifierProposal)
    {
        Name = adapter.Name;
        Description = string.IsNullOrWhiteSpace(adapter.Description) ? "—" : adapter.Description;
        Details = BuildDetails(adapter, classifierProposal);
        _isSelected = isSelected;
    }

    public string Name { get; }

    public string Description { get; }

    /// <summary>Media type, link status and why the app proposed this checkbox state.</summary>
    public string Details { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string BuildDetails(NetworkAdapterInfo adapter, bool classifierProposal)
    {
        var media = string.IsNullOrWhiteSpace(adapter.PhysicalMediaType) ? "тип неизвестен" : adapter.PhysicalMediaType;
        var status = string.IsNullOrWhiteSpace(adapter.Status) ? "статус неизвестен" : adapter.Status;
        var reason = classifierProposal
            ? "распознан как реальный сетевой путь"
            : "распознан как VPN или виртуальный адаптер";

        return $"{media} · {status} · {reason}";
    }
}
