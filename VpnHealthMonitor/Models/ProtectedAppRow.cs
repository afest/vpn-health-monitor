namespace VpnHealthMonitor.Models;

/// <summary>Display row for the "Защищённые приложения" list (rebuilt on each status refresh).</summary>
public sealed class ProtectedAppRow
{
    public ProtectedApp App { get; init; } = new();

    public string Name { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string StatusText { get; init; } = string.Empty;

    public string AppliedText { get; init; } = string.Empty;

    /// <summary>True when this is a moved Store/MSIX package and a new path was resolved — the "Обновить путь" action applies.</summary>
    public bool CanUpdatePath { get; init; }

    /// <summary>Resolved current exe path for a moved Store/MSIX package (null when not applicable).</summary>
    public string? ResolvedNewPath { get; init; }
}
