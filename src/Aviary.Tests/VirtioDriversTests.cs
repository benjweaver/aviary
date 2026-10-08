using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;
namespace Aviary.Tests;

public sealed class VirtioDriversTests
{
    sealed class Payload(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    [Fact]
    public void DriverCdIsAttachedReadOnlyBesideInstallerAndSetup()
    {
        var vm = new VmConfiguration { OperatingSystem = "Windows", DiskPath = @"C:\VM\system.qcow2", IsoPath = @"C:\ISO\win.iso", SetupIsoPath = @"C:\VM\setup.iso", DriverIsoPath = @"C:\Drivers\virtio-win.iso" };
        var args = QemuCommandBuilder.Build(vm, new(@"C:\QEMU", "test", [GuestArchitecture.X86_64]), new("X64", 8, 16384, false, "Windows"), 4444, 5900).ArgumentList.ToList();
        Assert.Contains("ide-cd,drive=drivers,bus=ide.1,unit=1", args);
        var drivers = args.Single(a => a.Contains("\"node-name\":\"drivers\""));
        Assert.Contains("\"read-only\":true", drivers);
        Assert.Equal(4, args.Count(a => a.StartsWith("ide-", StringComparison.Ordinal)));
        Assert.DoesNotContain(QemuCommandBuilder.Build(vm with { DriverIsoPath = "" }, new(@"C:\QEMU", "test", [GuestArchitecture.X86_64]), new("X64", 8, 16384, false, "Windows"), 4444, 5900).ArgumentList, a => a.Contains("drive=drivers"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnswerFileInstallsGuestToolsOnlyWithDriverCd(bool drivers)
    {
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        var doc = XDocument.Parse(GuestProvisioning.WindowsAnswerFile("aviary", "long-enough-password", drivers));
        var command = doc.Descendants(ns + "CommandLine").SingleOrDefault()?.Value;
        if (!drivers) { Assert.Null(command); return; }
        Assert.Contains("msiexec /i %d:\\virtio-win-gt-x64.msi /qn /norestart", command);
        Assert.True(command!.Length < 1024);
    }

    [Fact]
    public async Task VerifiedDownloadSavesOnlyMatchingContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "aviary-virtio-" + Guid.NewGuid());
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(3 << 20);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var destination = Path.Combine(root, "Drivers", "virtio-win.iso");
            double last = 0;
            using (var http = new HttpClient(new Payload(bytes)))
                await VirtioDrivers.DownloadVerifiedAsync(http, new Uri("https://example.invalid/virtio.iso"), hash, destination, new SyncProgress(v => last = v), default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            Assert.Equal(1, last);

            File.Delete(destination);
            using (var http = new HttpClient(new Payload(bytes)))
                await Assert.ThrowsAsync<IOException>(() => VirtioDrivers.DownloadVerifiedAsync(http, new Uri("https://example.invalid/virtio.iso"), new string('0', 64), destination, null, default));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    sealed class SyncProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }

    [QemuFact]
    public async Task QemuStartsWithAllFourIdeSlotsInUse()
    {
        var iso = Environment.GetEnvironmentVariable("AVIARY_TEST_ISO");
        if (string.IsNullOrEmpty(iso)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var directory = Path.Combine(Path.GetTempPath(), "aviary-drivers-" + Guid.NewGuid());
        var installation = await QemuDiscovery.FindAsync(Environment.GetEnvironmentVariable("AVIARY_QEMU")); Assert.NotNull(installation);
        await using var backend = new QemuBackend(new VmStore(directory), installation, HostProbe.Detect());
        try
        {
            // Any ISO stands in for the setup and driver CDs; this checks the slot layout QEMU accepts.
            var vm = await backend.CreateAsync(new() { OperatingSystem = "Windows", CpuCores = 1, MemoryMB = 512, DiskGB = 1, IsoPath = iso }, timeout.Token);
            await backend.StartAsync(vm with { SetupIsoPath = iso, DriverIsoPath = iso }, timeout.Token);
            Assert.Equal(VmStatus.Running, backend.GetStatus(vm.Id).Status);
        }
        finally { await backend.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
