using System.Threading;
using VpnHealthMonitor;
using VpnHealthMonitor.Models;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// The confirmation window is the last thing standing between the user and rules that can cut their
/// internet off — a XAML error there would surface only at that moment. These tests construct it on an
/// STA thread (without showing it), so a broken template or binding fails the build instead of the user.
/// </summary>
public class ConfirmAdaptersWindowTests
{
    private static IReadOnlyList<NetworkAdapterInfo> Adapters() => new[]
    {
        new NetworkAdapterInfo
        {
            Name = "Беспроводная сеть",
            Description = "TP-Link Wireless USB Adapter",
            PhysicalMediaType = "Native 802.11",
            IsVirtual = false,
            Status = "Up"
        },
        new NetworkAdapterInfo
        {
            Name = "Karing TUN Network Adapter",
            Description = "Karing TUN Network Adapter Tunnel",
            PhysicalMediaType = "Unspecified",
            IsVirtual = true,
            Status = "Up"
        }
    };

    private static T RunOnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"Окно не построилось: {failure}");
        }

        return result;
    }

    [Fact]
    public void WindowBuilds_AndPreselectsPhysicalAdapters()
    {
        var selected = RunOnSta(() =>
        {
            var window = new ConfirmAdaptersWindow(Adapters(), null);
            return window.SelectedAdapters;
        });

        Assert.Equal(new[] { "Беспроводная сеть" }, selected);
    }

    [Fact]
    public void PreselectionOverridesTheClassifier()
    {
        // The user previously unchecked the Wi-Fi and checked the tunnel — their choice must survive.
        var selected = RunOnSta(() =>
        {
            var window = new ConfirmAdaptersWindow(Adapters(), null, new[] { "Karing TUN Network Adapter" });
            return window.SelectedAdapters;
        });

        Assert.Equal(new[] { "Karing TUN Network Adapter" }, selected);
    }

    [Fact]
    public void WindowBuilds_WithWarningAndEmptyInventory()
    {
        var selected = RunOnSta(() =>
        {
            var window = new ConfirmAdaptersWindow(Array.Empty<NetworkAdapterInfo>(), "Get-NetAdapter недоступен");
            return window.SelectedAdapters;
        });

        Assert.Empty(selected);
    }
}
