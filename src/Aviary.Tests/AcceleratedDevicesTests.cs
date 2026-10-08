using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;
namespace Aviary.Tests;

public sealed class AcceleratedDevicesTests
{
    [Fact]
    public void OldMachinesKeepCompatibleHardware()
    {
        var vm = VmStore.Deserialize("{\"schemaVersion\":1,\"name\":\"Existing\"}");
        Assert.False(vm.AcceleratedNetwork); Assert.False(vm.AcceleratedGraphics);
    }
    [Theory]
    [InlineData(true, true, "user,model=virtio-net-pci")]
    [InlineData(true, false, "user,model=e1000")]
    [InlineData(false, true, "none")]
    public void NetworkChoiceRespectsDisconnectedMode(bool enabled, bool fast, string expected)
    {
        var vm = new VmConfiguration { DiskPath = @"C:\VM\system.qcow2", NetworkEnabled = enabled, AcceleratedNetwork = fast };
        var args = QemuCommandBuilder.Build(vm, new(@"C:\QEMU", "test", [GuestArchitecture.X86_64]), new("X64", 8, 16384, false, "Windows"), TestEndpoints.Fake).ArgumentList;
        Assert.Equal(expected, args[args.IndexOf("-nic") + 1]);
    }
    [Fact]
    public void ThreeDRequiresSupportedGuestAndDisplay()
    {
        var vm = new VmConfiguration { AcceleratedGraphics = true, DynamicDisplay = true };
        vm.Validate();
        Assert.Throws<InvalidDataException>(() => (vm with { OperatingSystem = "Windows" }).Validate());
        Assert.Throws<InvalidDataException>(() => (vm with { DynamicDisplay = false }).Validate());
        Assert.Throws<InvalidDataException>(() => (vm with { Engine = VmEngine.HyperV }).Validate());
    }
    [Theory]
    [InlineData(false, "virtio-vga", "none")]
    [InlineData(true, "virtio-vga-gl", "egl-headless")]
    public void ThreeDUsesVirglOnlyWhereItWorks(bool virgl, string device, string display)
    {
        var vm = new VmConfiguration { DiskPath = @"C:\VM\system.qcow2", DynamicDisplay = true, AcceleratedGraphics = true };
        var args = QemuCommandBuilder.Build(vm, new(@"C:\QEMU", "test", [GuestArchitecture.X86_64], VirglAvailable: virgl), new("X64", 8, 16384, false, "Windows"), TestEndpoints.Fake).ArgumentList;
        Assert.Equal(display, args[args.IndexOf("-display") + 1]);
        Assert.Equal(device, args[args.IndexOf("-vga") + 3]);
    }
    [Fact]
    public async Task WindowsQemuNeverAdvertisesVirgl()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("AVIARY_QEMU") is not { Length: > 0 } path) return;
        Assert.False((await QemuDiscovery.FindAsync(path))!.VirglAvailable);
    }
    [QemuFact]
    public async Task SavedThreeDMachineStartsWithEmbeddedDisplay()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var directory = Path.Combine(Path.GetTempPath(), "aviary-gpu-" + Guid.NewGuid());
        var installation = await QemuDiscovery.FindAsync(Environment.GetEnvironmentVariable("AVIARY_QEMU")); Assert.NotNull(installation);
        await using var backend = new QemuBackend(new VmStore(directory), installation, HostProbe.Detect());
        try
        {
            var vm = await backend.CreateAsync(new() { CpuCores = 1, MemoryMB = 512, DiskGB = 1, DynamicDisplay = true, AcceleratedGraphics = true, AcceleratedNetwork = true });
            await backend.StartAsync(vm, timeout.Token);
            await using var display = new VncDisplayClient();
            var frame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            display.FrameReceived += (w, h, pixels) => { if (w > 0 && h > 0 && pixels.Length == w * h * 4) frame.TrySetResult(true); };
            await display.ConnectAsync(backend.GetDisplay(vm.Id), timeout.Token);
            Assert.True(await frame.Task.WaitAsync(timeout.Token));
            Assert.Equal(VmStatus.Running, backend.GetStatus(vm.Id).Status);
        }
        finally { await backend.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
