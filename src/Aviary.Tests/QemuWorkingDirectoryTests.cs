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
            Directory.CreateDirectory(QemuEndpoints.RunDirectory);
            var brokenEndpoints = QemuEndpoints.Create();
            var broken = QemuCommandBuilder.Build(vm, installation, HostProbe.Detect(), brokenEndpoints);
            broken.WorkingDirectory = root;
            using (var process = Process.Start(broken)!)
            {
                var stderr = process.StandardError.ReadToEndAsync();
                // QEMU waits for a client on its QMP pipe before it sets up the display, where the keymap is loaded.
                using var qmp = new System.IO.Pipes.NamedPipeClientStream(".", brokenEndpoints.QmpPipe, System.IO.Pipes.PipeDirection.InOut);
                await qmp.ConnectAsync(10000);
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.NotEqual(0, process.ExitCode); Assert.Contains("en-us", await stderr);
                }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            }
            var endpoints = QemuEndpoints.Create();
            var fixedStart = QemuCommandBuilder.Build(vm, installation, HostProbe.Detect(), endpoints);
            Assert.Equal(Path.GetFullPath(installation.Directory), fixedStart.WorkingDirectory);
            using var fixedProcess = Process.Start(fixedStart)!;
            var fixedError = fixedProcess.StandardError.ReadToEndAsync();
            using var fixedQmp = new System.IO.Pipes.NamedPipeClientStream(".", endpoints.QmpPipe, System.IO.Pipes.PipeDirection.InOut);
            await fixedQmp.ConnectAsync(10000);
            try
            {
                await using var client = new VncDisplayClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    Assert.False(fixedProcess.HasExited, fixedProcess.HasExited ? await fixedError : "");
                    if (File.Exists(endpoints.VncSocket)) break;
                    await Task.Delay(100, timeout.Token);
                }
                await client.ConnectAsync(new Connection(endpoints.VncSocket), timeout.Token);
            }
            finally { if (!fixedProcess.HasExited) { fixedProcess.Kill(true); await fixedProcess.WaitForExitAsync(); } }
        }
        finally { await backend.DisposeAsync(); Directory.Delete(root, true); }
    }
    sealed record Connection(string Socket) : IDisplayConnection
    {
        public async Task<Stream> OpenAsync(CancellationToken token)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await Task.Run(() => socket.Connect(new UnixDomainSocketEndPoint(Socket)), token);
            return new NetworkStream(socket, ownsSocket: true);
        }
    }
}
