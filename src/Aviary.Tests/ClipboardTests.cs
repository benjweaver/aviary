using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;
namespace Aviary.Tests;

public sealed class ClipboardTests
{
    [Fact]
    public void SharedClipboardAddsQemusVdagentChannel()
    {
        var vm = new VmConfiguration { DiskPath = @"C:\VM\system.qcow2" };
        Assert.True(vm.SharedClipboard); // on unless turned off, including for machines saved before the setting existed
        Assert.True(VmStore.Deserialize("{\"schemaVersion\":1,\"name\":\"Old\"}").SharedClipboard);
        var args = QemuCommandBuilder.Build(vm, new(@"C:\QEMU", "test", [GuestArchitecture.X86_64]), new("X64", 8, 16384, false, "Windows"), TestEndpoints.Fake).ArgumentList;
        Assert.Contains("qemu-vdagent,id=vdagent,clipboard=on,mouse=off", args);
        Assert.Contains("virtserialport,chardev=vdagent,name=com.redhat.spice.0", args);
        Assert.DoesNotContain(QemuCommandBuilder.Build(vm with { SharedClipboard = false }, new(@"C:\QEMU", "test", [GuestArchitecture.X86_64]), new("X64", 8, 16384, false, "Windows"), TestEndpoints.Fake).ArgumentList, a => a.Contains("vdagent"));
    }

    // Two of Aviary's display clients on one real QEMU: what one copies, QEMU's clipboard hands to the other.
    // This drives the extended-clipboard notify/request/provide exchange end to end without a guest agent.
    [QemuFact]
    public async Task TextCopiedInOneDisplayArrivesInAnotherThroughQemu()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(Path.GetTempPath(), "aviary-clipboard-" + Guid.NewGuid());
        var installation = await QemuDiscovery.FindAsync(Environment.GetEnvironmentVariable("AVIARY_QEMU")); Assert.NotNull(installation);
        await using var backend = new QemuBackend(new VmStore(root), installation, HostProbe.Detect());
        try
        {
            var vm = await backend.CreateAsync(new() { CpuCores = 1, MemoryMB = 256, DiskGB = 1 }, timeout.Token);
            await backend.StartAsync(vm, timeout.Token);
            await using var copier = new VncDisplayClient();
            await using var paster = new VncDisplayClient();
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            paster.ClipboardReceived += text => received.TrySetResult(text);
            await copier.ConnectAsync(backend.GetDisplay(vm.Id), timeout.Token);
            await paster.ConnectAsync(backend.GetDisplay(vm.Id), timeout.Token);
            await Task.Delay(500, timeout.Token); // let both finish the capability exchange

            const string text = "Hello from Aviary ✈ ümlauts\r\nsecond line";
            await copier.SetClipboardTextAsync(text);
            Assert.Equal(text, await received.Task.WaitAsync(timeout.Token));
        }
        finally { await backend.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
