using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// T-323: parsing Get-NetAdapter output. A silently empty list would mean "no adapters to block", i.e.
/// no protection at all, so unreadable output must come back as null (caller falls back), not as empty.
/// </summary>
public class AdapterInventoryTests
{
    [Fact]
    public void ParsesArrayOfAdapters()
    {
        const string json = """
        [{"name":"Ethernet","description":"Intel(R) I211","media":"802.3","virtual":false,"status":"Up"},
         {"name":"Karing TUN Network Adapter","description":"Karing TUN Network Adapter Tunnel","media":"Unspecified","virtual":true,"status":"Up"}]
        """;

        var adapters = AdapterInventory.ParseInventory(json);

        Assert.NotNull(adapters);
        Assert.Equal(2, adapters!.Count);
        Assert.Equal("Ethernet", adapters[0].Name);
        Assert.False(adapters[0].IsVirtual);
        Assert.True(adapters[1].IsVirtual);
        Assert.Equal("Unspecified", adapters[1].PhysicalMediaType);
    }

    [Fact]
    public void ParsesSingleAdapterSerialisedAsObject()
    {
        const string json = """{"name":"Wi-Fi","description":"AX201","media":"Native 802.11","virtual":false,"status":"Up"}""";

        var adapters = AdapterInventory.ParseInventory(json);

        Assert.NotNull(adapters);
        var only = Assert.Single(adapters!);
        Assert.Equal("Wi-Fi", only.Name);
    }

    [Fact]
    public void ReturnsNullOnInventoryError()
    {
        Assert.Null(AdapterInventory.ParseInventory("""{"inventoryError":"Access is denied."}"""));
    }

    [Fact]
    public void ReturnsNullOnGarbageOrEmptyOutput()
    {
        Assert.Null(AdapterInventory.ParseInventory(""));
        Assert.Null(AdapterInventory.ParseInventory("   "));
        Assert.Null(AdapterInventory.ParseInventory("not json at all"));
    }

    [Fact]
    public void EmptyArrayParsesToEmptyList()
    {
        var adapters = AdapterInventory.ParseInventory("[]");

        Assert.NotNull(adapters);
        Assert.Empty(adapters!);
    }

    [Fact]
    public void SkipsEntriesWithoutName()
    {
        const string json = """[{"name":"","description":"ghost","media":"802.3","virtual":false,"status":"Up"}]""";

        var adapters = AdapterInventory.ParseInventory(json);

        Assert.NotNull(adapters);
        Assert.Empty(adapters!);
    }

    [Fact]
    public void HandlesBomAndStringBooleans()
    {
        var json = "﻿" + """[{"name":"Ethernet","description":"x","media":"802.3","virtual":"True","status":"Up"}]""";

        var adapters = AdapterInventory.ParseInventory(json);

        Assert.NotNull(adapters);
        Assert.True(Assert.Single(adapters!).IsVirtual);
    }
}
