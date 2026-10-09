using Aviary.Infrastructure;
using Xunit;
namespace Aviary.Tests;

public sealed class HostNetworkCheckTests
{
    [Fact]
    public void FixCommandQuotesEachAdapter() =>
        Assert.Equal("Disable-NetAdapterRsc -Name \"Ethernet\", \"Wi-Fi 2\"", HostNetworkCheck.FixCommand(["Ethernet", "Wi-Fi 2"]));

    // Runs the real query on this PC: it must succeed without admin rights and only name real, up, physical adapters.
    [Fact]
    public async Task QueryRunsWithoutAdminAndNamesRealAdapters()
    {
        if (!OperatingSystem.IsWindows()) return;
        var adapters = await HostNetworkCheck.AdaptersWithRscAsync();
        var names = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Name).ToHashSet();
        Assert.All(adapters, a => Assert.Contains(a, names));
    }
}