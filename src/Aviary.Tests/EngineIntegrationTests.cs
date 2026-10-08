using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;
using Xunit.Abstractions;
namespace Aviary.Tests;

public sealed class QemuFactAttribute : FactAttribute
{
    public QemuFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AVIARY_QEMU"))) Skip = "Set AVIARY_QEMU to run real-engine integration tests."; }
}
public sealed class EngineIntegrationTests(ITestOutputHelper output)
{
    [QemuFact]
    public async Task CreateStartDisplayPauseResumeStopAndReload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var root = Path.Combine(Path.GetTempPath(), "aviary-engine-" + Guid.NewGuid()); var store = new VmStore(root); var host = HostProbe.Detect();
        var installation = await QemuDiscovery.FindAsync(Environment.GetEnvironmentVariable("AVIARY_QEMU")); Assert.NotNull(installation); output.WriteLine(installation.Version); output.WriteLine($"Host WHPX: {host.WhpxAvailable}");
        await using var backend = new QemuBackend(store, installation, host); var transitions = new List<VmStatus>(); backend.StateChanged += s => { lock (transitions) transitions.Add(s.Status); };
        var vm = await backend.CreateAsync(new() { Name = "Alpine integration", CpuCores = 1, MemoryMB = 512, DiskGB = 1, Acceleration = Acceleration.Tcg, IsoPath = Environment.GetEnvironmentVariable("AVIARY_TEST_ISO") ?? "" }, timeout.Token);
        try
        {
            Assert.True(File.Exists(vm.DiskPath)); await backend.StartAsync(vm, timeout.Token); Assert.Equal(VmStatus.Running, backend.GetStatus(vm.Id).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartAsync(vm, timeout.Token)); Assert.Equal(VmStatus.Running, backend.GetStatus(vm.Id).Status);
            await using (var display = new VncDisplayClient()) { var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int frames = 0; (int Width, int Height, byte[] Pixels)? last = null; display.FrameReceived += (w, h, data) => { Assert.Equal(w * h * 4, data.Length); last = (w, h, data); Interlocked.Increment(ref frames); firstFrame.TrySetResult(); }; await display.ConnectAsync(backend.GetDisplay(vm.Id), timeout.Token); await firstFrame.Task.WaitAsync(timeout.Token); await display.KeyAsync(0xff0d, true); await display.KeyAsync(0xff0d, false); await display.PointerAsync(100, 100, 0); await Task.Delay(15000, timeout.Token); if (Environment.GetEnvironmentVariable("AVIARY_FRAME_PATH") is { } framePath && last is { } frame) { using var writer = new BinaryWriter(File.Create(framePath)); writer.Write((ushort)0x4d42); writer.Write(54 + frame.Pixels.Length); writer.Write(0); writer.Write(54); writer.Write(40); writer.Write(frame.Width); writer.Write(-frame.Height); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0); writer.Write(frame.Pixels.Length); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(frame.Pixels); } output.WriteLine($"Received {frames} frames"); }
            await backend.PauseAsync(vm.Id, timeout.Token); Assert.Equal(VmStatus.Paused, backend.GetStatus(vm.Id).Status); await backend.ResumeAsync(vm.Id, timeout.Token); Assert.Equal(VmStatus.Running, backend.GetStatus(vm.Id).Status);
            await backend.ResetAsync(vm.Id, timeout.Token); await backend.StopAsync(vm.Id, timeout.Token); Assert.Contains(backend.GetStatus(vm.Id).Status, new[] { VmStatus.Stopping, VmStatus.Stopped }); if (backend.GetStatus(vm.Id).Status != VmStatus.Stopped) await backend.ForceStopAsync(vm.Id, timeout.Token);
            Assert.Equal(VmStatus.Stopped, backend.GetStatus(vm.Id).Status); Assert.Equal(vm, Assert.Single((await new VmStore(root).LoadAsync()).Vms));
            await backend.StartAsync(vm, timeout.Token); await backend.ForceStopAsync(vm.Id, timeout.Token);
            lock (transitions) { Assert.Contains(VmStatus.Starting, transitions); Assert.Contains(VmStatus.Running, transitions); Assert.Contains(VmStatus.Paused, transitions); Assert.Contains(VmStatus.Stopped, transitions); output.WriteLine(string.Join(" -> ", transitions)); }
        }
        finally { await backend.DisposeAsync(); Directory.Delete(root, true); }
    }
}

