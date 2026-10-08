using System.Diagnostics;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;
namespace Aviary.Tests;

// QEMU user networking maps the guest's 10.0.2.2 to the host's 127.0.0.1, so anything QEMU listens on over loopback
// TCP is reachable from every guest. Aviary keeps QMP on a named pipe and the display on an AF_UNIX socket instead.
public sealed class LoopbackExposureTests
{
    [Fact]
    public void CommasInTheSocketPathAreEscapedForQemu()
    {
        var vm = new VmConfiguration { DiskPath = @"C:\VM\system.qcow2" };
        var args = QemuCommandBuilder.Build(vm, new(@"C:\QEMU", "test", [GuestArchitecture.X86_64]), new("X64", 8, 16384, false, "Windows"), new("aviary-qmp-x", @"C:\Users\a,b\run\x.vnc")).ArgumentList;
        Assert.Equal(@"unix:C:\Users\a,,b\run\x.vnc", args[args.IndexOf("-vnc") + 1]);
        Assert.Equal("monitor-qmp,id=qmp-monitor,chardev=qmp", args[args.IndexOf("-object") + 1]);
    }

    [Fact]
    public void EndpointsAreUniqueAndFitTheUnixSocketLimit()
    {
        var a = QemuEndpoints.Create(); var b = QemuEndpoints.Create();
        Assert.NotEqual(a.QmpPipe, b.QmpPipe); Assert.NotEqual(a.VncSocket, b.VncSocket);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(a.VncSocket) < 108);
    }

    static async Task<string[]> TcpListenersOfAsync(int pid)
    {
        var result = await ProcessRunner.RunCapturedAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "netstat.exe"), ["-ano", "-p", "TCP"], TimeSpan.FromSeconds(30));
        return result.Output.Split('\n').Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length == 5 && f[3] == "LISTENING" && f[4] == pid.ToString()).Select(f => f[1]).ToArray();
    }

    [QemuFact]
    public async Task RunningQemuListensOnNoTcpPortYetDisplayAndControlWork()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(Path.GetTempPath(), "aviary-loopback-" + Guid.NewGuid());
        var installation = await QemuDiscovery.FindAsync(Environment.GetEnvironmentVariable("AVIARY_QEMU")); Assert.NotNull(installation);
        await using var backend = new QemuBackend(new VmStore(root), installation, HostProbe.Detect());
        try
        {
            var vm = await backend.CreateAsync(new() { CpuCores = 1, MemoryMB = 256, DiskGB = 1 }, timeout.Token);
            await backend.StartAsync(vm, timeout.Token);
            var pid = backend.GetStatus(vm.Id).ProcessId!.Value;
            Assert.Empty(await TcpListenersOfAsync(pid));

            // Control (QMP over the pipe) and display (RFB over the socket) still work.
            await backend.PauseAsync(vm.Id, timeout.Token); Assert.Equal(VmStatus.Paused, backend.GetStatus(vm.Id).Status);
            await backend.ResumeAsync(vm.Id, timeout.Token);
            await using var display = new VncDisplayClient();
            var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            display.FrameReceived += (_, _, _) => frame.TrySetResult();
            await display.ConnectAsync(backend.GetDisplay(vm.Id), timeout.Token);
            await frame.Task.WaitAsync(timeout.Token);
            Assert.NotEmpty(await backend.ScreenshotAsync(vm.Id, timeout.Token));
        }
        finally { await backend.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
