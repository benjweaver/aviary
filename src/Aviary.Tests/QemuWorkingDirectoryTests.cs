using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;

namespace Aviary.Tests;

public sealed class QemuWorkingDirectoryTests
{
    [QemuFact]
    public async Task LocalizationFolderCannotShadowQemuKeyboardMap()
    {
        var root = Path.Combine(Path.GetTempPath(), "aviary-keymap-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "en-us"));
        var installation = await QemuDiscovery.FindAsync(Environment.GetEnvironmentVariable("AVIARY_QEMU")); Assert.NotNull(installation);
        await using var backend = new QemuBackend(new VmStore(Path.Combine(root, "Machines")), installation, HostProbe.Detect());
        try
        {
            var vm = await backend.CreateAsync(new() { CpuCores = 1, MemoryMB = 512, DiskGB = 1 });
            int qmp = FreePort(), vnc = FreePort(); while (qmp == vnc) vnc = FreePort();
            var broken = QemuCommandBuilder.Build(vm, installation, HostProbe.Detect(), qmp, vnc);
            broken.WorkingDirectory = root;
            using (var process = Process.Start(broken)!)
            {
                var stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.NotEqual(0, process.ExitCode); Assert.Contains("en-us", await stderr);
                }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            }
            var fixedStart = QemuCommandBuilder.Build(vm, installation, HostProbe.Detect(), qmp, vnc);
            Assert.Equal(Path.GetFullPath(installation.Directory), fixedStart.WorkingDirectory);
            using var fixedProcess = Process.Start(fixedStart)!;
            var fixedError = fixedProcess.StandardError.ReadToEndAsync();
            try
            {
                await using var client = new VncDisplayClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    Assert.False(fixedProcess.HasExited, fixedProcess.HasExited ? await fixedError : "");
                    using var probe = new TcpClient();
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); attempt.CancelAfter(500);
                    try { await probe.ConnectAsync(IPAddress.Loopback, vnc, attempt.Token); break; }
                    catch (Exception ex) when ((ex is SocketException or OperationCanceledException) && !timeout.IsCancellationRequested) { await Task.Delay(100, timeout.Token); }
                }
                await client.ConnectAsync(new Connection(vnc), timeout.Token);
            }
            finally { if (!fixedProcess.HasExited) { fixedProcess.Kill(true); await fixedProcess.WaitForExitAsync(); } }
        }
        finally { await backend.DisposeAsync(); Directory.Delete(root, true); }
    }
    sealed record Connection(int Port) : IDisplayConnection;
    static int FreePort()
    {
        while (true) { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); if (port >= 5900) return port; }
    }
}
