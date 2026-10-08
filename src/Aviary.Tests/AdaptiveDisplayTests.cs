using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;

namespace Aviary.Tests;

public sealed class AdaptiveDisplayTests
{
    [Fact]
    public void OldConfigurationsKeepTheirGraphicsAdapter()
    {
        var vm = VmStore.Deserialize("{\"schemaVersion\":1,\"name\":\"Existing Linux\"}");
        Assert.False(vm.DynamicDisplay);
    }
    [Fact]
    public void AdaptiveProfileSelectsVirtioWithoutChangingDiskOrAcceleration()
    {
        var vm = new VmConfiguration { DiskPath = @"C:\VM\system.qcow2", DynamicDisplay = true };
        var qemu = new QemuInstallation(@"C:\QEMU", "test", [GuestArchitecture.X86_64]);
        var host = new HostCapabilities("X64", 8, 16384, false, "Windows");
        var args = QemuCommandBuilder.Build(vm, qemu, host, 4444, 5900).ArgumentList;
        Assert.Contains("virtio-vga", args); Assert.Contains("tcg", args); Assert.Contains("ide-hd,drive=system,bus=ide.0,unit=0", args);
        Assert.DoesNotContain("virtio-vga", QemuCommandBuilder.Build(vm with { DynamicDisplay = false }, qemu, host, 4444, 5900).ArgumentList);
    }
    [QemuFact]
    public async Task QemuAcceptsAdaptiveDisplayResizeRequest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(Path.GetTempPath(), "aviary-adaptive-" + Guid.NewGuid());
        var store = new VmStore(root); var installation = await QemuDiscovery.FindAsync(Environment.GetEnvironmentVariable("AVIARY_QEMU")); Assert.NotNull(installation);
        await using var backend = new QemuBackend(store, installation, HostProbe.Detect());
        var vm = await backend.CreateAsync(new() { Name = "Adaptive test", CpuCores = 1, MemoryMB = 512, DiskGB = 1, DynamicDisplay = true });
        try
        {
            await backend.StartAsync(vm, timeout.Token);
            await using var client = new VncDisplayClient();
            var reply = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ResizeReply += code => { if (code != 0) reply.TrySetResult(code); };
            await client.ConnectAsync(backend.GetDisplay(vm.Id), timeout.Token);
            await client.ResizeDesktopAsync(1280, 800);
            Assert.Equal(4, await reply.Task.WaitAsync(timeout.Token)); // QEMU forwarded the request to virtio-gpu.
            await backend.ForceStopAsync(vm.Id, timeout.Token);
        }
        finally { await backend.DisposeAsync(); Directory.Delete(root, true); }
    }
}
